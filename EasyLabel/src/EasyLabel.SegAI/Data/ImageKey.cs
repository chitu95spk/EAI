using System.Security.Cryptography;
using OpenCvSharp;

namespace EasyLabel.SegAI.Data;

public static class ImageKey
{
    /// <summary>Lowercase hex SHA256 of the OpenCV-encoded PNG of a BGR image. NOT comparable with EasyVision's GDI+ keys.</summary>
    public static string Compute(Mat bgr)
    {
        ArgumentNullException.ThrowIfNull(bgr);
        if (bgr.Empty()) throw new ArgumentException("Image is empty.", nameof(bgr));
        if (!Cv2.ImEncode(".png", bgr, out byte[] png)) throw new InvalidOperationException("PNG encoding failed.");
        return System.Convert.ToHexString(SHA256.HashData(png)).ToLowerInvariant();
    }
}
