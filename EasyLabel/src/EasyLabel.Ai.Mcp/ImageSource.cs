using System.Globalization;
using System.Runtime.InteropServices;
using EasyLabel.Ai.Onnx;
using OpenCvSharp;

namespace EasyLabel.Ai.Mcp;

/// <summary>The run image: decoded lazily once, owned and disposed by this object.</summary>
internal sealed class ImageSource : IDisposable
{
    public const int DefaultMaxSide = 768;
    public const int MinMaxSide = 64;
    public const int MaxMaxSide = 1536;
    public const int JpegQuality = 85;

    /// <summary>
    /// Most pixels any picture a tool returns may have. The model API downsizes a larger picture itself (to about 1.15 megapixels), which
    /// makes every scale a tool reports wrong; so the tools never return more (a 2448x2048 image comes back about 1170 px on the long side).
    /// </summary>
    public const double MaxOutputPixels = 1150000.0;

    /// <summary>The largest output-per-source scale (at most 1 for an image, any value for a zoom) that keeps width x height x scale^2 within <see cref="MaxOutputPixels"/>.</summary>
    public static double PixelCapScale(double width, double height)
    {
        double area = Math.Max(1.0, width * height);
        // 0.999: rounding the output size to whole pixels must not push it over the cap.
        return Math.Sqrt(MaxOutputPixels / area) * 0.999;
    }

    private readonly RunRequest _request;
    private Mat? _mat;

    public ImageSource(RunRequest request)
    {
        _request = request;
    }

    /// <summary>Image size in pixels: from request.json when given, otherwise from the decoded image.</summary>
    public (int Width, int Height) ImageSize
    {
        get
        {
            if (_request.ImageWidth > 0 && _request.ImageHeight > 0) return (_request.ImageWidth, _request.ImageHeight);
            Mat mat = GetMat();
            return (mat.Width, mat.Height);
        }
    }

    private readonly object _decodeGate = new object();

    public Mat GetMat()
    {
        lock (_decodeGate) return GetMatLocked();
    }

    private Mat GetMatLocked()
    {
        if (_mat != null) return _mat;
        string path = _request.ImagePath;
        if (!File.Exists(path)) throw new FileNotFoundException("Image file not found: " + path);
        // IgnoreOrientation: the app (WPF preview, saved shapes, request.json size) works in the stored pixel grid and does not apply the
        // EXIF rotation; OpenCV would rotate a JPEG by default, so every crop and coordinate would be in a different frame than the shapes.
        Mat decoded = Cv2.ImDecode(File.ReadAllBytes(path), ImreadModes.Color | ImreadModes.IgnoreOrientation);
        if (decoded.Empty())
        {
            decoded.Dispose();
            throw new IOException("Could not decode image: " + path);
        }

        _mat = decoded;
        return _mat;
    }

    /// <summary>The image as BGR packed bytes for a backend.</summary>
    public AiImage ToAiImage()
    {
        Mat mat = GetMat();
        using Mat continuous = mat.Clone();
        byte[] pixels = new byte[(long)mat.Width * mat.Height * 3];
        Marshal.Copy(continuous.Data, pixels, 0, pixels.Length);
        var info = new FileInfo(_request.ImagePath);
        string key = info.FullName + "|" + info.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture) + "|" + info.Length.ToString(CultureInfo.InvariantCulture);
        return new AiImage(pixels, mat.Width, mat.Height, key);
    }

    /// <summary>
    /// Crops (clamped to the image) and downscales so the longer side is at most maxSide. Returns the JPEG and the geometry:
    /// image_x = x + crop_x / scale, image_y = y + crop_y / scale.
    /// </summary>
    public CropResult Crop(double? x, double? y, double? width, double? height, int maxSide)
    {
        Mat image = GetMat();
        int w = image.Width;
        int h = image.Height;
        double rx = x ?? 0;
        double ry = y ?? 0;
        double rw = width ?? (w - rx);
        double rh = height ?? (h - ry);

        int x1 = (int)Math.Floor(Math.Min(w, Math.Max(0, rx)));
        int y1 = (int)Math.Floor(Math.Min(h, Math.Max(0, ry)));
        int x2 = (int)Math.Ceiling(Math.Max(0, Math.Min(w, rx + rw)));
        int y2 = (int)Math.Ceiling(Math.Max(0, Math.Min(h, ry + rh)));
        int cw = x2 - x1;
        int ch = y2 - y1;
        if (cw < 1 || ch < 1) throw new ArgumentException("Crop rectangle is outside the image (" + w + "x" + h + ").");

        int side = Math.Min(MaxMaxSide, Math.Max(MinMaxSide, maxSide));
        double scale = Math.Min(Math.Min(1.0, side / (double)Math.Max(cw, ch)), PixelCapScale(cw, ch));
        int ow = Math.Max(1, (int)Math.Round(cw * scale));
        int oh = Math.Max(1, (int)Math.Round(ch * scale));
        scale = ow / (double)cw;

        using var roi = new Mat(image, new Rect(x1, y1, cw, ch));
        using var resized = new Mat();
        Mat source = roi;
        if (ow != cw || oh != ch)
        {
            Cv2.Resize(roi, resized, new Size(ow, oh), 0, 0, InterpolationFlags.Area);
            source = resized;
        }

        if (!Cv2.ImEncode(".jpg", source, out byte[] jpeg, new[] { (int)ImwriteFlags.JpegQuality, JpegQuality }))
        {
            throw new IOException("Could not encode the crop as JPEG.");
        }

        return new CropResult(jpeg, x1, y1, cw, ch, scale, ow, oh);
    }

    /// <summary>The same clamping as <see cref="Crop"/> (image pixels; throws <see cref="ArgumentException"/> when the rectangle is outside the image).</summary>
    public Rect ClampRegion(double? x, double? y, double? width, double? height)
    {
        Mat image = GetMat();
        int w = image.Width;
        int h = image.Height;
        double rx = x ?? 0;
        double ry = y ?? 0;
        double rw = width ?? (w - rx);
        double rh = height ?? (h - ry);

        int x1 = (int)Math.Floor(Math.Min(w, Math.Max(0, rx)));
        int y1 = (int)Math.Floor(Math.Min(h, Math.Max(0, ry)));
        int x2 = (int)Math.Ceiling(Math.Max(0, Math.Min(w, rx + rw)));
        int y2 = (int)Math.Ceiling(Math.Max(0, Math.Min(h, ry + rh)));
        if (x2 - x1 < 1 || y2 - y1 < 1) throw new ArgumentException("Rectangle is outside the image (" + w + "x" + h + ").");
        return new Rect(x1, y1, x2 - x1, y2 - y1);
    }

    /// <summary>A new BGR picture of <paramref name="region"/> resized to outWidth x outHeight (owned and disposed by the caller; safe to draw on).</summary>
    public Mat ExtractRegion(Rect region, int outWidth, int outHeight)
    {
        Mat image = GetMat();
        using var roi = new Mat(image, region);
        if (outWidth == region.Width && outHeight == region.Height) return roi.Clone();
        var resized = new Mat();
        bool enlarge = outWidth > region.Width || outHeight > region.Height;
        Cv2.Resize(roi, resized, new Size(outWidth, outHeight), 0, 0, enlarge ? InterpolationFlags.Linear : InterpolationFlags.Area);
        return resized;
    }

    public void Dispose()
    {
        _mat?.Dispose();
        _mat = null;
    }
}

internal sealed record CropResult(byte[] Jpeg, int X, int Y, int Width, int Height, double Scale, int OutWidth, int OutHeight);
