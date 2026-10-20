using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace EasyLabel.Camera;

/// <summary>
/// Shared source machinery: lifecycle, one grab worker thread, latest-frame-wins hand-off and fault reporting.
/// Derived classes supply the device specific steps. Acquisition runs only while a consumer (StreamAsync or
/// GrabOneAsync) is active; with no consumer the worker stops grabbing. All derived hooks run on thread-pool or
/// worker threads, never the caller's UI thread. One consumer at a time is supported; a second concurrent consumer
/// competes for the same frames.
/// </summary>
public abstract class CameraSourceBase : ICameraSource
{
    /// <summary>Worker wait when idle; also the longest delay before an idle camera stops grabbing.</summary>
    private const int IdleWaitMilliseconds = 250;

    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly FrameMailbox _mailbox = new();
    private readonly ManualResetEventSlim _wake = new(false);
    private readonly object _faultLock = new();
    private Thread? _worker;
    private volatile bool _stopRequested;
    private bool _grabbing;
    private int _demand;
    private int _pendingGateOps;
    private int _state = (int)CameraState.Closed;
    private int _disposed;

    protected CameraSourceBase(CameraInfo info)
    {
        Info = info ?? throw new ArgumentNullException(nameof(info));
    }

    public CameraInfo Info { get; }

    public CameraState State => (CameraState)Volatile.Read(ref _state);

    public event EventHandler<CameraFaultEventArgs> Faulted = delegate { };

    /// <summary>Serialises every SDK call of this device (grab polling, parameter access, open and close).</summary>
    protected object SdkGate { get; } = new();

    /// <summary>True while acquisition is running. Read it with SdkGate held.</summary>
    protected bool IsGrabbing => _grabbing;

    /// <summary>Consecutive poll errors that turn into a fault.</summary>
    protected virtual int MaxConsecutivePollErrors => 3;

    /// <summary>How long Close waits for the grab thread before it gives up and keeps the thread registered.</summary>
    protected virtual int WorkerJoinTimeoutMilliseconds => 5000;

    /// <summary>A continuous no-data streak this long makes the worker ask <see cref="IsDeviceConnected"/>.</summary>
    protected virtual TimeSpan NoDataLivenessWindow => TimeSpan.FromSeconds(2);

    /// <summary>
    /// Liveness probe used after a long no-data streak (an unplugged camera only ever reports "no data").
    /// Called with SdkGate held. Return false to fault the source with "device disconnected".
    /// </summary>
    protected virtual bool IsDeviceConnected() => true;

    // ---- device hooks ----

    /// <summary>Opens the device. Runs on a pool thread; throw on failure (cleanup is done through OnClose).</summary>
    protected abstract void OnOpen(CancellationToken cancellationToken);

    /// <summary>Releases the device. Must not throw and must tolerate a partly opened device.</summary>
    protected abstract void OnClose();

    /// <summary>Starts image acquisition. Called with SdkGate held. Throw on failure.</summary>
    protected abstract void OnStartGrabbing();

    /// <summary>Stops image acquisition. Called with SdkGate held. Must not throw.</summary>
    protected abstract void OnStopGrabbing();

    /// <summary>
    /// Waits briefly (about 100 ms at most) for one frame. Called with SdkGate held while grabbing.
    /// On <see cref="PollStatus.Frame"/> the frame is returned and owned by the base class.
    /// </summary>
    protected abstract PollStatus Poll(out CameraFrame? frame, out string? error);

    /// <summary>Hook before GrabOneAsync waits (for example a software trigger). Runs on a pool thread.</summary>
    protected virtual void PrepareGrabOne()
    {
    }

    protected enum PollStatus
    {
        /// <summary>A frame was produced.</summary>
        Frame,

        /// <summary>Nothing arrived within the wait; not an error.</summary>
        NoData,

        /// <summary>The SDK reported an error; see the error text.</summary>
        Error,
    }

    // ---- helpers for derived classes ----

    /// <summary>Runs an action holding SdkGate. Poll yields to waiters so parameter calls are not starved.</summary>
    protected T WithGate<T>(Func<T> action)
    {
        Interlocked.Increment(ref _pendingGateOps);
        try
        {
            lock (SdkGate)
            {
                return action();
            }
        }
        finally
        {
            Interlocked.Decrement(ref _pendingGateOps);
        }
    }

    /// <summary>Starts acquisition if it is not running. Safe to call from any thread.</summary>
    protected void EnsureGrabbing()
    {
        lock (SdkGate)
        {
            if (!_grabbing)
            {
                StartGrabbingLocked();
            }
        }
    }

    /// <summary>Runs an action with acquisition stopped and restarts it afterwards (Width, PixelFormat and similar).</summary>
    protected void RunStructural(Action action)
    {
        Exception? restartFailure = null;
        lock (SdkGate)
        {
            bool wasGrabbing = _grabbing;
            if (wasGrabbing)
            {
                StopGrabbingLocked();
            }

            try
            {
                action();
            }
            finally
            {
                if (wasGrabbing && State != CameraState.Closed)
                {
                    try
                    {
                        StartGrabbingLocked();
                    }
                    catch (Exception ex)
                    {
                        restartFailure = ex;
                    }
                }
            }
        }

        if (restartFailure != null)
        {
            Fault("Restarting acquisition failed: " + restartFailure.Message, restartFailure);
        }
    }

    /// <summary>Marks the source faulted, fails the frame hand-off and raises <see cref="Faulted"/>.</summary>
    protected void Fault(string message, Exception? exception)
    {
        lock (_faultLock)
        {
            CameraState current = State;
            if (current == CameraState.Faulted || current == CameraState.Closed || Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            Volatile.Write(ref _state, (int)CameraState.Faulted);
        }

        _mailbox.Fail(message, exception);
        _wake.Set();
        try
        {
            Faulted(this, new CameraFaultEventArgs(message, exception));
        }
        catch
        {
            // A throwing subscriber must not kill the worker or the caller.
        }
    }

    // ---- ICameraSource ----

    public async Task OpenAsync(CancellationToken cancellationToken)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            CameraState current = State;
            if (current == CameraState.Open || current == CameraState.Streaming)
            {
                return;
            }

            if (_worker != null)
            {
                // Faulted source being reopened: release the old session first.
                await Task.Run(CloseCore).ConfigureAwait(false);
                if (_worker != null)
                {
                    throw new InvalidOperationException(
                        "The previous grab thread has not ended yet; close the camera and try again later.");
                }
            }

            Volatile.Write(ref _state, (int)CameraState.Opening);
            try
            {
                await Task.Run(() =>
                {
                    lock (SdkGate)
                    {
                        OnOpen(cancellationToken);
                    }
                }).ConfigureAwait(false);
            }
            catch
            {
                await Task.Run(() =>
                {
                    lock (SdkGate)
                    {
                        SafeOnClose();
                    }
                }).ConfigureAwait(false);
                Volatile.Write(ref _state, (int)CameraState.Closed);
                throw;
            }

            cancellationToken.ThrowIfCancellationRequested();
            _mailbox.Reset();
            _stopRequested = false;
            _grabbing = false;
            Volatile.Write(ref _state, (int)CameraState.Open);
            var thread = new Thread(WorkerLoop)
            {
                IsBackground = true,
                Name = "Camera-" + Info.Id,
            };
            _worker = thread;
            thread.Start();
        }
        catch (OperationCanceledException)
        {
            if (_worker == null)
            {
                await Task.Run(() =>
                {
                    lock (SdkGate)
                    {
                        SafeOnClose();
                    }
                }).ConfigureAwait(false);
                Volatile.Write(ref _state, (int)CameraState.Closed);
            }

            throw;
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async Task<CameraFrame> GrabOneAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (timeout != Timeout.InfiniteTimeSpan && timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), "Timeout must be positive or infinite.");
        }

        ThrowIfNotReady();
        Interlocked.Increment(ref _demand);
        _wake.Set();
        try
        {
            _mailbox.Clear();
            await Task.Run(() =>
            {
                EnsureGrabbing();
                PrepareGrabOne();
            }, cancellationToken).ConfigureAwait(false);

            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if (timeout != Timeout.InfiniteTimeSpan)
            {
                linked.CancelAfter(timeout);
            }

            CameraFrame? frame;
            try
            {
                frame = await _mailbox.TakeAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException("No frame arrived within " + timeout.TotalMilliseconds.ToString("0") + " ms.");
            }

            if (frame == null)
            {
                throw new InvalidOperationException("The camera was closed while waiting for a frame.");
            }

            return frame;
        }
        finally
        {
            Interlocked.Decrement(ref _demand);
        }
    }

    public async IAsyncEnumerable<CameraFrame> StreamAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ThrowIfNotReady();
        Interlocked.Increment(ref _demand);
        _wake.Set();
        try
        {
            while (true)
            {
                CameraFrame? frame = await _mailbox.TakeAsync(cancellationToken).ConfigureAwait(false);
                if (frame == null)
                {
                    yield break;
                }

                yield return frame;
            }
        }
        finally
        {
            Interlocked.Decrement(ref _demand);
        }
    }

    public async Task CloseAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            await Task.Run(CloseCore).ConfigureAwait(false);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            await Task.Run(CloseCore).ConfigureAwait(false);
        }
        finally
        {
            _lifecycle.Release();
        }

        _mailbox.Complete();
        GC.SuppressFinalize(this);
    }

    // ---- internals ----

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    }

    private void ThrowIfNotReady()
    {
        ThrowIfDisposed();
        CameraState current = State;
        if (current == CameraState.Faulted)
        {
            throw new InvalidOperationException("The camera is faulted; close and open it again.");
        }

        if (current != CameraState.Open && current != CameraState.Streaming)
        {
            throw new InvalidOperationException("The camera is not open (state " + current + ").");
        }
    }

    // Caller holds SdkGate.
    private void StartGrabbingLocked()
    {
        OnStartGrabbing();
        _grabbing = true;
        if (State == CameraState.Open)
        {
            Volatile.Write(ref _state, (int)CameraState.Streaming);
        }
    }

    // Caller holds SdkGate.
    private void StopGrabbingLocked()
    {
        if (!_grabbing)
        {
            return;
        }

        _grabbing = false;
        try
        {
            OnStopGrabbing();
        }
        catch
        {
            // Stopping is best effort.
        }

        if (State == CameraState.Streaming)
        {
            Volatile.Write(ref _state, (int)CameraState.Open);
        }
    }

    // Caller holds SdkGate.
    private void SafeOnClose()
    {
        try
        {
            OnClose();
        }
        catch
        {
            // Close must always finish.
        }
    }

    private void CloseCore()
    {
        Thread? worker = _worker;
        if (worker == null && State == CameraState.Closed)
        {
            return;
        }

        _stopRequested = true;
        _wake.Set();
        if (worker != null && worker != Thread.CurrentThread && !worker.Join(WorkerJoinTimeoutMilliseconds))
        {
            // The grab thread is stuck (for example inside an SDK call). Keep _worker so Open refuses to start a
            // second worker; the device is released by a later Close once the thread has ended.
            _mailbox.Complete();
            Volatile.Write(ref _state, (int)CameraState.Closed);
            return;
        }

        lock (SdkGate)
        {
            StopGrabbingLocked();
            SafeOnClose();
        }

        _worker = null;
        _mailbox.Complete();
        Volatile.Write(ref _state, (int)CameraState.Closed);
    }

    private void StopGrabbingIfNeeded()
    {
        lock (SdkGate)
        {
            // A consumer registers its demand before it takes the gate, so this re-check cannot cancel a fresh start.
            if (Volatile.Read(ref _demand) > 0)
            {
                return;
            }

            StopGrabbingLocked();
        }
    }

    private void WorkerLoop()
    {
        int errors = 0;
        long noDataSince = 0;
        while (!_stopRequested && State != CameraState.Faulted)
        {
            _wake.Reset();
            if (Volatile.Read(ref _demand) <= 0)
            {
                noDataSince = 0;
                StopGrabbingIfNeeded();
                _wake.Wait(IdleWaitMilliseconds);
                continue;
            }

            PollStatus status = PollStatus.NoData;
            CameraFrame? frame = null;
            string? error = null;
            Exception? exception = null;
            try
            {
                lock (SdkGate)
                {
                    if (_stopRequested)
                    {
                        break;
                    }

                    if (!_grabbing)
                    {
                        StartGrabbingLocked();
                    }

                    status = Poll(out frame, out error);
                }
            }
            catch (Exception ex)
            {
                status = PollStatus.Error;
                error = ex.Message;
                exception = ex;
            }

            switch (status)
            {
                case PollStatus.Frame:
                    errors = 0;
                    noDataSince = 0;
                    if (frame != null)
                    {
                        _mailbox.Post(frame);
                    }

                    break;
                case PollStatus.NoData:
                    errors = 0;
                    if (noDataSince == 0)
                    {
                        noDataSince = Stopwatch.GetTimestamp();
                    }
                    else if (Stopwatch.GetElapsedTime(noDataSince) >= NoDataLivenessWindow)
                    {
                        noDataSince = Stopwatch.GetTimestamp(); // probe again after another window
                        bool connected = true;
                        try
                        {
                            lock (SdkGate)
                            {
                                connected = IsDeviceConnected();
                            }
                        }
                        catch (Exception)
                        {
                            // A failing probe is not proof of a disconnect.
                        }

                        if (!connected)
                        {
                            Fault("Device disconnected: no frames arrived and the camera no longer responds.", null);
                        }
                    }

                    break;
                default:
                    errors++;
                    if (errors >= MaxConsecutivePollErrors || exception != null)
                    {
                        Fault(error ?? "Image acquisition failed.", exception);
                    }

                    break;
            }

            if (Volatile.Read(ref _pendingGateOps) > 0)
            {
                Thread.Sleep(1); // let a waiting parameter call take the gate
            }
        }
    }
}
