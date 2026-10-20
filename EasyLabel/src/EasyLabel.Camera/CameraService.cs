using EasyLabel.Camera.Mvs;

namespace EasyLabel.Camera;

/// <summary>Connection state of <see cref="CameraService"/>.</summary>
public enum CameraServiceState
{
    Disconnected,
    Connecting,
    Connected,
    Faulted,
}

/// <summary>A grabbed frame converted to tightly packed BGR 24-bit (stride = Width * 3). The array belongs to the caller.</summary>
public sealed class CameraBgrImage
{
    public CameraBgrImage(int width, int height, byte[] bgr, long frameNumber, DateTime timestampUtc)
    {
        Width = width;
        Height = height;
        Bgr = bgr ?? throw new ArgumentNullException(nameof(bgr));
        FrameNumber = frameNumber;
        TimestampUtc = timestampUtc;
    }

    public int Width { get; }

    public int Height { get; }

    public byte[] Bgr { get; }

    public long FrameNumber { get; }

    public DateTime TimestampUtc { get; }
}

/// <summary>Arguments of <see cref="CameraService.FrameReceived"/>. The frame is valid only while the handler runs: copy it to keep it.</summary>
public sealed class CameraFrameEventArgs : EventArgs
{
    public CameraFrameEventArgs(CameraFrame frame)
    {
        Frame = frame ?? throw new ArgumentNullException(nameof(frame));
    }

    public CameraFrame Frame { get; }
}

/// <summary>
/// The one shared camera connection of the app: device list, a single open source, reconnect after a fault, one-shot grab and a
/// live frame stream. No UI types; every await here continues on the thread pool, so a caller on the UI thread never deadlocks.
/// <see cref="StateChanged"/> and <see cref="FrameReceived"/> are raised on worker threads: subscribers marshal themselves.
/// </summary>
public sealed class CameraService : IAsyncDisposable, IDisposable
{
    /// <summary>Reconnect tries after a fault before the service stays Faulted.</summary>
    public const int MaxReconnectAttempts = 3;

    public static readonly TimeSpan DefaultReconnectDelay = TimeSpan.FromSeconds(2);

    private readonly ICameraProvider _provider;
    private readonly TimeSpan _reconnectDelay;
    private readonly object _sync = new();
    private readonly SemaphoreSlim _opLock = new(1, 1);
    private readonly SemaphoreSlim _enumLock = new(1, 1);
    private readonly List<TaskCompletionSource<CameraBgrImage>> _grabWaiters = new();

    private IReadOnlyList<CameraInfo> _devices = Array.Empty<CameraInfo>();
    private string _enumerationMessage = string.Empty;
    private bool _enumerated;
    private ICameraSource? _source;
    private CameraServiceState _state = CameraServiceState.Disconnected;
    private string? _deviceId;
    private string? _deviceName;
    private string _errorText = string.Empty;
    private int _reconnectAttempt;
    private int _generation;
    private bool _disposed;
    private CancellationTokenSource? _opCts;
    private CancellationTokenSource? _pumpCts;
    private Task? _pumpTask;
    private EventHandler<CameraFrameEventArgs>? _frameReceived;

    public CameraService(ICameraProvider provider)
        : this(provider, DefaultReconnectDelay)
    {
    }

    public CameraService(ICameraProvider provider, TimeSpan reconnectDelay)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _reconnectDelay = reconnectDelay < TimeSpan.Zero ? TimeSpan.Zero : reconnectDelay;
    }

    /// <summary>Raised (on any thread) when the state, the device list or the error text changed.</summary>
    public event EventHandler? StateChanged;

    /// <summary>
    /// Live frames, latest-frame-wins, raised on a worker thread. Acquisition runs only while this event has a subscriber and the
    /// camera is connected. The frame is valid only during the handler.
    /// </summary>
    public event EventHandler<CameraFrameEventArgs> FrameReceived
    {
        add
        {
            lock (_sync)
            {
                _frameReceived += value;
                UpdatePumpLocked();
            }
        }

        remove
        {
            lock (_sync)
            {
                _frameReceived -= value;
                UpdatePumpLocked();
            }
        }
    }

    public CameraServiceState State
    {
        get { lock (_sync) { return _state; } }
    }

    /// <summary>Localization key of the status line for the current state (the text may use {0} = device name, {1} = reconnect try, {2} = tries).</summary>
    public string StatusKey
    {
        get
        {
            lock (_sync)
            {
                switch (_state)
                {
                    case CameraServiceState.Connecting: return "CameraStatusConnecting";
                    case CameraServiceState.Connected: return "CameraStatusConnected";
                    case CameraServiceState.Faulted: return _reconnectAttempt > 0 ? "CameraStatusReconnecting" : "CameraStatusFaulted";
                    default: return "CameraStatusDisconnected";
                }
            }
        }
    }

    /// <summary>Last error (connect failure, fault); empty when there is none.</summary>
    public string ErrorText
    {
        get { lock (_sync) { return _errorText; } }
    }

    /// <summary>Why the latest enumeration found nothing (for example the MVS runtime is missing); empty otherwise.</summary>
    public string EnumerationMessage
    {
        get { lock (_sync) { return _enumerationMessage; } }
    }

    /// <summary>True once an enumeration finished (successfully or not).</summary>
    public bool HasEnumerated
    {
        get { lock (_sync) { return _enumerated; } }
    }

    public IReadOnlyList<CameraInfo> Devices
    {
        get { lock (_sync) { return _devices; } }
    }

    public string? DeviceId
    {
        get { lock (_sync) { return _deviceId; } }
    }

    public string? DeviceName
    {
        get { lock (_sync) { return _deviceName; } }
    }

    /// <summary>Current reconnect try (1..<see cref="MaxReconnectAttempts"/>) while reconnecting, otherwise 0.</summary>
    public int ReconnectAttempt
    {
        get { lock (_sync) { return _reconnectAttempt; } }
    }

    public bool IsConnected => State == CameraServiceState.Connected;

    /// <summary>Parameter access of the open camera; null when not connected.</summary>
    public ICameraParameters? Parameters
    {
        get
        {
            lock (_sync)
            {
                return _state == CameraServiceState.Connected ? _source as ICameraParameters : null;
            }
        }
    }

    /// <summary>Refreshes the device list off the calling thread. Never throws: a failure gives an empty list and <see cref="EnumerationMessage"/>.</summary>
    public async Task<IReadOnlyList<CameraInfo>> EnumerateAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await _enumLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Devices;
        }

        try
        {
            return await EnumerateCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _enumLock.Release();
        }
    }

    /// <summary>
    /// Opens the device (closing the current one first when connected). Returns true when connected; false on a failure (state
    /// Faulted, <see cref="ErrorText"/> set), when another connect / disconnect superseded this one, or when cancelled. Never throws.
    /// </summary>
    public async Task<bool> ConnectAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            throw new ArgumentException("A device id is required.", nameof(deviceId));
        }

        CancellationTokenSource cts;
        int gen;
        ICameraSource? retired;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            CancelOperationLocked();
            retired = DetachSourceLocked();
            FailWaitersLocked("The camera connection changed.");
            cts = _opCts = new CancellationTokenSource();
            gen = ++_generation;
            _deviceId = deviceId;
            _deviceName = _devices.FirstOrDefault(d => string.Equals(d.Id, deviceId, StringComparison.Ordinal))?.DisplayName ?? deviceId;
            _errorText = string.Empty;
            _reconnectAttempt = 0;
            _state = CameraServiceState.Connecting;
        }

        RaiseStateChanged();
        using CancellationTokenRegistration registration = cancellationToken.Register(() => TryCancel(cts));
        await _opLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await CloseSourceAsync(retired).ConfigureAwait(false);
            cts.Token.ThrowIfCancellationRequested();
            if (!IsCurrent(gen))
            {
                return false;
            }

            string? error = await OpenCoreAsync(deviceId, gen, cts.Token).ConfigureAwait(false);
            if (error == null)
            {
                return true;
            }

            lock (_sync)
            {
                if (_generation != gen || _disposed)
                {
                    return false;
                }

                _state = CameraServiceState.Faulted;
                _errorText = error;
            }

            RaiseStateChanged();
            return false;
        }
        catch (OperationCanceledException)
        {
            bool mine;
            lock (_sync)
            {
                mine = _generation == gen && !_disposed;
                if (mine)
                {
                    _state = CameraServiceState.Disconnected;
                    _errorText = string.Empty;
                }
            }

            if (mine)
            {
                RaiseStateChanged();
            }

            return false;
        }
        finally
        {
            _opLock.Release();
        }
    }

    /// <summary>Closes the camera without waiting: the state is Disconnected at once, the device is released in the background.</summary>
    public void Disconnect()
    {
        _ = DisconnectAsync();
    }

    /// <summary>Cancels any connect or reconnect, closes the camera and returns to Disconnected. Never throws.</summary>
    public async Task DisconnectAsync()
    {
        ICameraSource? retired;
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            CancelOperationLocked();
            retired = DetachSourceLocked();
            FailWaitersLocked("The camera was disconnected.");
            _generation++;
            _state = CameraServiceState.Disconnected;
            _errorText = string.Empty;
            _reconnectAttempt = 0;
        }

        RaiseStateChanged();
        await _opLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await CloseSourceAsync(retired).ConfigureAwait(false);
        }
        finally
        {
            _opLock.Release();
        }
    }

    /// <summary>
    /// Grabs one frame and returns a BGR copy the caller owns. While live frames are running the next live frame is used.
    /// Throws InvalidOperationException when not connected and TimeoutException when no frame arrives in time.
    /// </summary>
    public async Task<CameraBgrImage> GrabOneAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (timeout != Timeout.InfiniteTimeSpan && timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), "Timeout must be positive or infinite.");
        }

        ICameraSource source;
        TaskCompletionSource<CameraBgrImage>? waiter = null;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_state != CameraServiceState.Connected || _source == null)
            {
                throw new InvalidOperationException("The camera is not connected.");
            }

            source = _source;
            if (_pumpTask != null)
            {
                waiter = new TaskCompletionSource<CameraBgrImage>(TaskCreationOptions.RunContinuationsAsynchronously);
                _grabWaiters.Add(waiter);
            }
        }

        if (waiter == null)
        {
            using CameraFrame frame = await source.GrabOneAsync(timeout, cancellationToken).ConfigureAwait(false);
            return ToImage(frame);
        }

        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (timeout != Timeout.InfiniteTimeSpan)
        {
            linked.CancelAfter(timeout);
        }

        using CancellationTokenRegistration registration = linked.Token.Register(() => waiter.TrySetCanceled());
        try
        {
            return await waiter.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("No frame arrived within " + timeout.TotalMilliseconds.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + " ms.");
        }
        finally
        {
            lock (_sync)
            {
                _grabWaiters.Remove(waiter);
            }
        }
    }

    public void Dispose()
    {
        try
        {
            Task.Run(() => DisposeAsync().AsTask()).Wait(TimeSpan.FromSeconds(10));
        }
        catch (Exception)
        {
            // Shutdown must finish.
        }
    }

    public async ValueTask DisposeAsync()
    {
        ICameraSource? retired;
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            CancelOperationLocked();
            retired = DetachSourceLocked();
            FailWaitersLocked("The camera service was disposed.");
            _generation++;
            _state = CameraServiceState.Disconnected;
            _reconnectAttempt = 0;
        }

        RaiseStateChanged();
        await _opLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await CloseSourceAsync(retired).ConfigureAwait(false);
        }
        finally
        {
            _opLock.Release();
        }
    }

    // ---- internals ----

    private bool IsCurrent(int gen)
    {
        lock (_sync)
        {
            return _generation == gen && !_disposed;
        }
    }

    private static void TryCancel(CancellationTokenSource cts)
    {
        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    // Caller holds _sync.
    private void CancelOperationLocked()
    {
        CancellationTokenSource? cts = _opCts;
        _opCts = null;
        if (cts != null)
        {
            TryCancel(cts);
        }
    }

    // Caller holds _sync. Returns the source to close (outside the lock, under _opLock).
    private ICameraSource? DetachSourceLocked()
    {
        StopPumpLocked();
        ICameraSource? source = _source;
        _source = null;
        if (source != null)
        {
            source.Faulted -= OnSourceFaulted;
        }

        return source;
    }

    // Caller holds _sync.
    private void FailWaitersLocked(string reason)
    {
        if (_grabWaiters.Count == 0)
        {
            return;
        }

        foreach (TaskCompletionSource<CameraBgrImage> waiter in _grabWaiters.ToArray())
        {
            waiter.TrySetException(new InvalidOperationException(reason));
        }

        _grabWaiters.Clear();
    }

    private static async Task CloseSourceAsync(ICameraSource? source)
    {
        if (source == null)
        {
            return;
        }

        try
        {
            await source.CloseAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Close must always finish.
        }

        try
        {
            await source.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
        }
    }

    private void RaiseStateChanged()
    {
        EventHandler? handlers = StateChanged;
        if (handlers == null)
        {
            return;
        }

        foreach (Delegate handler in handlers.GetInvocationList())
        {
            try
            {
                ((EventHandler)handler)(this, EventArgs.Empty);
            }
            catch (Exception)
            {
                // A throwing subscriber must not break the service.
            }
        }
    }

    private async Task<IReadOnlyList<CameraInfo>> EnumerateCoreAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<CameraInfo> list;
        string message = string.Empty;
        try
        {
            list = await Task.Run(() => _provider.EnumerateAsync(cancellationToken), cancellationToken).ConfigureAwait(false)
                ?? Array.Empty<CameraInfo>();
            if (list.Count == 0 && _provider is MvsCameraProvider mvs)
            {
                message = mvs.LastMessage;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Devices;
        }
        catch (Exception ex)
        {
            list = Array.Empty<CameraInfo>();
            message = ex.Message;
        }

        lock (_sync)
        {
            _devices = list.ToArray();
            _enumerationMessage = message;
            _enumerated = true;
        }

        RaiseStateChanged();
        return list;
    }

    /// <summary>
    /// Creates and opens the source of the device. Caller holds _opLock. Returns null on success, the error text on a failure;
    /// throws OperationCanceledException when cancelled or superseded (the new source is released first).
    /// </summary>
    private async Task<string?> OpenCoreAsync(string deviceId, int gen, CancellationToken cancellationToken)
    {
        CameraInfo? info = FindDevice(deviceId);
        if (info == null)
        {
            await EnumerateCoreAsync(cancellationToken).ConfigureAwait(false);
            info = FindDevice(deviceId);
        }

        if (info == null)
        {
            return "Camera '" + deviceId + "' was not found.";
        }

        ICameraSource source;
        try
        {
            source = _provider.Create(info);
        }
        catch (Exception ex)
        {
            return ex.Message;
        }

        bool committed = false;
        try
        {
            await source.OpenAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            lock (_sync)
            {
                if (_generation != gen || _disposed)
                {
                    throw new OperationCanceledException();
                }

                source.Faulted -= OnSourceFaulted;
                source.Faulted += OnSourceFaulted;
                _source = source;
                _deviceId = info.Id;
                _deviceName = info.DisplayName;
                _state = CameraServiceState.Connected;
                _errorText = string.Empty;
                _reconnectAttempt = 0;
                committed = true;
                UpdatePumpLocked();
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
        finally
        {
            if (!committed)
            {
                await CloseSourceAsync(source).ConfigureAwait(false);
            }
        }

        RaiseStateChanged();
        if (source.State == CameraState.Faulted)
        {
            OnSourceFaulted(source, new CameraFaultEventArgs("The camera faulted right after opening."));
        }

        return null;
    }

    private CameraInfo? FindDevice(string deviceId)
    {
        lock (_sync)
        {
            return _devices.FirstOrDefault(d => string.Equals(d.Id, deviceId, StringComparison.Ordinal));
        }
    }

    private void OnSourceFaulted(object? sender, CameraFaultEventArgs e)
    {
        CancellationTokenSource cts;
        int gen;
        string? deviceId;
        ICameraSource? retired;
        lock (_sync)
        {
            if (_disposed || sender == null || !ReferenceEquals(sender, _source))
            {
                return;
            }

            CancelOperationLocked();
            retired = DetachSourceLocked();
            FailWaitersLocked("The camera faulted.");
            cts = _opCts = new CancellationTokenSource();
            gen = ++_generation;
            deviceId = _deviceId;
            _state = CameraServiceState.Faulted;
            _errorText = e.Message;
            _reconnectAttempt = 0;
        }

        RaiseStateChanged();
        if (deviceId == null)
        {
            _ = Task.Run(() => CloseWithLockAsync(retired));
            return;
        }

        _ = Task.Run(() => ReconnectAsync(deviceId, gen, cts.Token, retired));
    }

    private async Task CloseWithLockAsync(ICameraSource? retired)
    {
        await _opLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await CloseSourceAsync(retired).ConfigureAwait(false);
        }
        finally
        {
            _opLock.Release();
        }
    }

    private async Task ReconnectAsync(string deviceId, int gen, CancellationToken cancellationToken, ICameraSource? retired)
    {
        try
        {
            await CloseWithLockAsync(retired).ConfigureAwait(false);
            for (int attempt = 1; attempt <= MaxReconnectAttempts; attempt++)
            {
                lock (_sync)
                {
                    if (_generation != gen || _disposed)
                    {
                        return;
                    }

                    _reconnectAttempt = attempt;
                }

                RaiseStateChanged();
                await Task.Delay(_reconnectDelay, cancellationToken).ConfigureAwait(false);
                await _opLock.WaitAsync().ConfigureAwait(false);
                try
                {
                    if (!IsCurrent(gen))
                    {
                        return;
                    }

                    string? error = await OpenCoreAsync(deviceId, gen, cancellationToken).ConfigureAwait(false);
                    if (error == null)
                    {
                        return;
                    }

                    lock (_sync)
                    {
                        if (_generation == gen)
                        {
                            _errorText = error;
                        }
                    }
                }
                finally
                {
                    _opLock.Release();
                }
            }

            bool exhausted;
            lock (_sync)
            {
                exhausted = _generation == gen && !_disposed;
                if (exhausted)
                {
                    _reconnectAttempt = 0;
                }
            }

            if (exhausted)
            {
                RaiseStateChanged();
            }
        }
        catch (OperationCanceledException)
        {
            // Superseded by a connect / disconnect / dispose, which sets the state.
        }
    }

    // ---- live frames ----

    // Caller holds _sync.
    private void UpdatePumpLocked()
    {
        bool wanted = !_disposed && _state == CameraServiceState.Connected && _source != null && _frameReceived != null;
        if (!wanted)
        {
            if (_pumpCts != null)
            {
                StopPumpLocked();
            }

            return;
        }

        if (_pumpTask != null)
        {
            return;
        }

        ICameraSource source = _source!;
        var cts = new CancellationTokenSource();
        _pumpCts = cts;
        _pumpTask = Task.Run(() => PumpAsync(source, cts.Token));
    }

    // Caller holds _sync.
    private void StopPumpLocked()
    {
        CancellationTokenSource? cts = _pumpCts;
        _pumpCts = null;
        _pumpTask = null;
        if (cts != null)
        {
            TryCancel(cts);
        }
    }

    private async Task PumpAsync(ICameraSource source, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (CameraFrame frame in source.StreamAsync(cancellationToken).WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                using (frame)
                {
                    Deliver(frame);
                }
            }
        }
        catch (Exception)
        {
            // Cancelled, closed or faulted: the source's Faulted event drives the reconnect.
        }
    }

    private void Deliver(CameraFrame frame)
    {
        TaskCompletionSource<CameraBgrImage>[] waiters;
        lock (_sync)
        {
            waiters = _grabWaiters.ToArray();
            _grabWaiters.Clear();
        }

        foreach (TaskCompletionSource<CameraBgrImage> waiter in waiters)
        {
            try
            {
                waiter.TrySetResult(ToImage(frame));
            }
            catch (Exception ex)
            {
                waiter.TrySetException(ex);
            }
        }

        EventHandler<CameraFrameEventArgs>? handlers = Volatile.Read(ref _frameReceived);
        if (handlers == null)
        {
            return;
        }

        var args = new CameraFrameEventArgs(frame);
        foreach (Delegate handler in handlers.GetInvocationList())
        {
            try
            {
                ((EventHandler<CameraFrameEventArgs>)handler)(this, args);
            }
            catch (Exception)
            {
                // A throwing subscriber must not stop the stream.
            }
        }
    }

    private static CameraBgrImage ToImage(CameraFrame frame)
    {
        return new CameraBgrImage(frame.Width, frame.Height, CameraFrameConverter.ToBgr24(frame), frame.FrameNumber, frame.TimestampUtc);
    }
}
