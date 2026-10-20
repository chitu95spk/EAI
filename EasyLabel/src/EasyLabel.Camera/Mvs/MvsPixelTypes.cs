namespace EasyLabel.Camera.Mvs;

/// <summary>
/// GVSP pixel type values from C:\Program Files (x86)\MVS\Development\Includes\PixelType.h.
/// Value = color class | (bits per pixel &lt;&lt; 16) | id; MONO = 0x01000000, COLOR = 0x02000000 (PixelType.h:10-18).
/// </summary>
internal static class MvsPixelTypes
{
    internal const uint Mono8 = 0x01080001;      // PixelType.h:41  MONO | 8 bit  | 0x0001
    internal const uint Mono10 = 0x01100003;     // PixelType.h:43  MONO | 16 bit | 0x0003
    internal const uint Mono12 = 0x01100005;     // PixelType.h:45  MONO | 16 bit | 0x0005
    internal const uint Mono16 = 0x01100007;     // PixelType.h:48  MONO | 16 bit | 0x0007
    internal const uint BayerGR8 = 0x01080008;   // PixelType.h:51  MONO | 8 bit  | 0x0008
    internal const uint BayerRG8 = 0x01080009;   // PixelType.h:52  MONO | 8 bit  | 0x0009
    internal const uint BayerGB8 = 0x0108000A;   // PixelType.h:53  MONO | 8 bit  | 0x000A
    internal const uint BayerBG8 = 0x0108000B;   // PixelType.h:54  MONO | 8 bit  | 0x000B
    internal const uint Rgb8Packed = 0x02180014; // PixelType.h:78  COLOR | 24 bit | 0x0014
    internal const uint Bgr8Packed = 0x02180015; // PixelType.h:79  COLOR | 24 bit | 0x0015

    internal static CameraPixelFormat ToCameraFormat(uint gvsp)
    {
        switch (gvsp)
        {
            case Mono8: return CameraPixelFormat.Mono8;
            case Mono10: return CameraPixelFormat.Mono10;
            case Mono12: return CameraPixelFormat.Mono12;
            case Mono16: return CameraPixelFormat.Mono16;
            case BayerGR8: return CameraPixelFormat.BayerGR8;
            case BayerRG8: return CameraPixelFormat.BayerRG8;
            case BayerGB8: return CameraPixelFormat.BayerGB8;
            case BayerBG8: return CameraPixelFormat.BayerBG8;
            case Rgb8Packed: return CameraPixelFormat.Rgb8;
            case Bgr8Packed: return CameraPixelFormat.Bgr8;
            default: return CameraPixelFormat.Unknown;
        }
    }

    internal static uint ToGvsp(CameraPixelFormat format)
    {
        switch (format)
        {
            case CameraPixelFormat.Mono8: return Mono8;
            case CameraPixelFormat.Mono10: return Mono10;
            case CameraPixelFormat.Mono12: return Mono12;
            case CameraPixelFormat.Mono16: return Mono16;
            case CameraPixelFormat.BayerGR8: return BayerGR8;
            case CameraPixelFormat.BayerRG8: return BayerRG8;
            case CameraPixelFormat.BayerGB8: return BayerGB8;
            case CameraPixelFormat.BayerBG8: return BayerBG8;
            case CameraPixelFormat.Rgb8: return Rgb8Packed;
            case CameraPixelFormat.Bgr8: return Bgr8Packed;
            default: return 0xFFFFFFFF; // PixelType_Gvsp_Undefined (PixelType.h:31)
        }
    }
}
