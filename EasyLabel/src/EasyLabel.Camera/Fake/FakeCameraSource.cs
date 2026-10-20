using System.Diagnostics;

namespace EasyLabel.Camera.Fake;

/// <summary>
/// Simulated camera: moving test pattern at the configured frame rate, a strict parameter store (ranges, steps,
/// auto modes, ROI dependencies, "locked while grabbing" nodes), software trigger, and failure simulation.
/// It uses the same base class and parameter engine as <c>MvsCameraSource</c>, so lifecycle and parameter logic are
/// exercised without hardware.
/// </summary>
public sealed class FakeCameraSource : CameraSourceBase, ICameraParameters
{
    // Poll waits at most this long so parameter calls on the gate are never blocked for a full frame period.
    private const int MaxPollSleepMilliseconds = 10;

    private readonly FakeCameraSpec _spec;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly byte[] _noScratch = Array.Empty<byte>();
    private FakeNodeMap? _nodes;
    private CameraParameterEngine? _engine;
    private byte[] _scratch;
    private long _nextDueTicks;
    private long _frameCounter;
    private long _framesProduced;
    private volatile string? _pollFailure;
    private volatile bool _disconnected;
    private volatile bool _stuckRequested;
    private readonly ManualResetEventSlim _stuck = new(true);

    public FakeCameraSource(FakeCameraSpec spec)
        : base((spec ?? throw new ArgumentNullException(nameof(spec))).ToInfo())
    {
        _spec = spec;
        _scratch = _noScratch;
    }

    /// <summary>Number of frames the simulated sensor has produced since creation.</summary>
    public long FramesProduced => Interlocked.Read(ref _framesProduced);

    /// <summary>Raises a camera fault as if the SDK had lost the device.</summary>
    public void SimulateFault(string message)
    {
        Fault(string.IsNullOrEmpty(message) ? "Simulated camera fault." : message, null);
    }

    /// <summary>The next poll reports an SDK error with this text; after the error threshold the camera faults.</summary>
    public void SimulatePollError(string message)
    {
        _pollFailure = string.IsNullOrEmpty(message) ? "Simulated grab error." : message;
    }

    /// <summary>Simulates an unplugged camera: no more frames, the liveness probe reports "not connected".</summary>
    public void SimulateDisconnect()
    {
        _disconnected = true;
    }

    /// <summary>Makes the next Poll block (like an SDK call that never returns) until <see cref="ReleaseStuckWorker"/>.</summary>
    public void SimulateStuckWorker()
    {
        _stuck.Reset();
        _stuckRequested = true;
    }

    public void ReleaseStuckWorker()
    {
        _stuckRequested = false;
        _stuck.Set();
    }

    protected override int WorkerJoinTimeoutMilliseconds => _spec.WorkerJoinTimeoutMilliseconds;

    protected override TimeSpan NoDataLivenessWindow => _spec.LivenessWindow;

    protected override bool IsDeviceConnected() => !_disconnected;

    // ---- hooks ----

    protected override void OnOpen(CancellationToken cancellationToken)
    {
        if (_spec.FailOnOpen)
        {
            throw new InvalidOperationException(_spec.OpenFailureMessage);
        }

        var nodes = new FakeNodeMap(_spec, () => IsGrabbing);
        _nodes = nodes;
        _engine = new CameraParameterEngine(nodes, RunStructural);
        _pollFailure = null;
        _disconnected = false;
    }

    protected override void OnClose()
    {
        _nodes = null;
        _engine = null;
        _scratch = _noScratch;
    }

    protected override void OnStartGrabbing()
    {
        _nextDueTicks = _clock.ElapsedTicks;
    }

    protected override void OnStopGrabbing()
    {
    }

    protected override PollStatus Poll(out CameraFrame? frame, out string? error)
    {
        frame = null;
        error = null;
        FakeNodeMap? nodes = _nodes;
        if (nodes == null)
        {
            error = "The camera is not open.";
            return PollStatus.Error;
        }

        if (_stuckRequested)
        {
            _stuck.Wait();
            return PollStatus.NoData;
        }

        if (_disconnected)
        {
            Thread.Sleep(5);
            return PollStatus.NoData;
        }

        string? failure = _pollFailure;
        if (failure != null)
        {
            error = failure;
            return PollStatus.Error;
        }

        if (nodes.TriggerModeOn)
        {
            if (!nodes.TryConsumeSoftwareTrigger())
            {
                Thread.Sleep(2);
                return PollStatus.NoData;
            }
        }
        else
        {
            long now = _clock.ElapsedTicks;
            if (now < _nextDueTicks)
            {
                double remainingMs = (_nextDueTicks - now) * 1000.0 / Stopwatch.Frequency;
                if (remainingMs < 2)
                {
                    Thread.Yield(); // Sleep(1) can take a whole timer tick, which would cap high frame rates
                }
                else
                {
                    Thread.Sleep((int)Math.Min(MaxPollSleepMilliseconds, remainingMs - 1));
                }

                return PollStatus.NoData;
            }

            double rate = Math.Max(1, nodes.FrameRate);
            _nextDueTicks = now + (long)(Stopwatch.Frequency / rate);
        }

        int width = nodes.Width;
        int height = nodes.Height;
        CameraPixelFormat format = nodes.PixelFormat;
        int size = FakeTestPattern.FrameSize(width, height, format);
        if (_scratch.Length < size)
        {
            _scratch = new byte[size];
        }

        long number = ++_frameCounter;
        FakeTestPattern.Render(
            number, nodes.SensorWidth, nodes.SensorHeight, nodes.OffsetX, nodes.OffsetY, width, height, format, _scratch);
        frame = CameraFrame.CopyFrom(
            new ReadOnlySpan<byte>(_scratch, 0, size),
            width, height, width * CameraFrameConverter.BytesPerPixel(format), format, number, DateTime.UtcNow);
        Interlocked.Increment(ref _framesProduced);
        return PollStatus.Frame;
    }

    protected override void PrepareGrabOne()
    {
        lock (SdkGate)
        {
            FakeNodeMap? nodes = _nodes;
            if (nodes != null)
            {
                SoftwareTrigger.FireIfEnabled(nodes);
            }
        }
    }

    // ---- ICameraParameters ----

    public Task<IReadOnlyList<CameraParameter>> RefreshAsync(CancellationToken cancellationToken)
    {
        return Task.Run(() => WithGate(() => _engine?.GetAll() ?? CameraParameterEngine.AllUnavailable()), cancellationToken);
    }

    public Task<CameraParameter> GetAsync(CameraParameterId id, CancellationToken cancellationToken)
    {
        return Task.Run(() => WithGate(() => _engine?.Get(id) ?? CameraParameterEngine.Unavailable(id)), cancellationToken);
    }

    public Task<CameraParameter> SetAsync(CameraParameterId id, double value, CancellationToken cancellationToken)
    {
        return Task.Run(() => WithGate(() => _engine?.Set(id, value) ?? CameraParameterEngine.Unavailable(id)), cancellationToken);
    }

    public Task<CameraParameter> SetAutoAsync(CameraParameterId id, bool auto, CancellationToken cancellationToken)
    {
        return Task.Run(() => WithGate(() => _engine?.SetAuto(id, auto) ?? CameraParameterEngine.Unavailable(id)), cancellationToken);
    }
}
