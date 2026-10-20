namespace EasyLabel.Camera;

public enum CameraPixelFormat
{
    Unknown,
    Mono8,
    Bgr8,
    Rgb8,
    BayerRG8,
    BayerGB8,
    BayerGR8,
    BayerBG8,

    /// <summary>16-bit little-endian container, value in the low 10 bits.</summary>
    Mono10,

    /// <summary>16-bit little-endian container, value in the low 12 bits.</summary>
    Mono12,

    /// <summary>16-bit little-endian container, full range.</summary>
    Mono16,
}
