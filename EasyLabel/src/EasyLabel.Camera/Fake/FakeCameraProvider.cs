namespace EasyLabel.Camera.Fake;

/// <summary>
/// Configurable list of simulated cameras. An empty device list simulates a PC with no camera attached.
/// Enumeration reads the current list, so tests can add or remove devices between calls.
/// </summary>
public sealed class FakeCameraProvider : ICameraProvider
{
    private readonly object _lock = new();
    private readonly List<FakeCameraSpec> _devices;

    public FakeCameraProvider()
        : this(new[] { new FakeCameraSpec() })
    {
    }

    public FakeCameraProvider(IEnumerable<FakeCameraSpec> devices)
    {
        if (devices == null)
        {
            throw new ArgumentNullException(nameof(devices));
        }

        _devices = new List<FakeCameraSpec>(devices);
    }

    public void AddDevice(FakeCameraSpec spec)
    {
        if (spec == null)
        {
            throw new ArgumentNullException(nameof(spec));
        }

        lock (_lock)
        {
            _devices.Add(spec);
        }
    }

    public void ClearDevices()
    {
        lock (_lock)
        {
            _devices.Clear();
        }
    }

    public Task<IReadOnlyList<CameraInfo>> EnumerateAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<CameraInfo> list;
        lock (_lock)
        {
            list = _devices.Select(spec => spec.ToInfo()).ToList();
        }

        return Task.FromResult(list);
    }

    public ICameraSource Create(CameraInfo info)
    {
        if (info == null)
        {
            throw new ArgumentNullException(nameof(info));
        }

        FakeCameraSpec? spec;
        lock (_lock)
        {
            spec = _devices.FirstOrDefault(s => string.Equals(s.Id, info.Id, StringComparison.Ordinal));
        }

        if (spec == null)
        {
            throw new ArgumentException("No simulated camera with id '" + info.Id + "'.", nameof(info));
        }

        return new FakeCameraSource(spec);
    }
}
