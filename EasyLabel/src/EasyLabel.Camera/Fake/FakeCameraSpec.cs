namespace EasyLabel.Camera.Fake;

/// <summary>Describes one simulated camera for <see cref="FakeCameraProvider"/>. All values are read when the source is created or opened.</summary>
public sealed class FakeCameraSpec
{
    public string Id { get; set; } = "FAKE:0001";

    public string DisplayName { get; set; } = "Fake camera 1";

    public string Model { get; set; } = "FAKE-CAM";

    public string SerialNumber { get; set; } = "0001";

    public string Vendor { get; set; } = "EasyLabel";

    /// <summary>Full sensor size; the ROI (Width/Height/Offset) lives inside it. Use multiples of 8.</summary>
    public int SensorWidth { get; set; } = 1280;

    public int SensorHeight { get; set; } = 960;

    /// <summary>Initial ROI size at open (multiples of 8, within the sensor).</summary>
    public int Width { get; set; } = 640;

    public int Height { get; set; } = 480;

    /// <summary>Initial frame rate in frames per second (the camera node accepts 1..1000).</summary>
    public double FrameRate { get; set; } = 30;

    /// <summary>Initial pixel format; must be one of Mono8, Mono10, Mono12, BayerRG8, Rgb8, Bgr8.</summary>
    public CameraPixelFormat PixelFormat { get; set; } = CameraPixelFormat.Bgr8;

    /// <summary>When true, OpenAsync throws <see cref="OpenFailureMessage"/> wrapped in an InvalidOperationException.</summary>
    public bool FailOnOpen { get; set; }

    public string OpenFailureMessage { get; set; } = "Simulated open failure.";

    /// <summary>Parameters the simulated camera does not have; they report Supported=false.</summary>
    public ISet<CameraParameterId> UnsupportedParameters { get; } = new HashSet<CameraParameterId>();

    /// <summary>Raw GenICam node names to leave out (for example "ExposureAuto" to remove only the auto mode).</summary>
    public ISet<string> MissingNodes { get; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>No-data streak after which the source probes the device (shortened in tests).</summary>
    public TimeSpan LivenessWindow { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>How long Close waits for the grab thread (shortened in tests).</summary>
    public int WorkerJoinTimeoutMilliseconds { get; set; } = 5000;

    public CameraInfo ToInfo() => new CameraInfo(Id, DisplayName, Model, SerialNumber, Vendor);
}
