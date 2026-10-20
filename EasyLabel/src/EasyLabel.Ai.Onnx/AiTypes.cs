using System.Drawing;

namespace EasyLabel.Ai.Onnx;

/// <summary>Raised when a model file or its ONNX interface is not what a backend supports.</summary>
public sealed class AiModelException : Exception
{
    public AiModelException(string message) : base(message) { }

    public AiModelException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// One image handed to a backend. <see cref="Pixels"/> is BGR, 3 bytes per pixel, rows tightly packed (no stride padding),
/// i.e. the layout of an OpenCV CV_8UC3 Mat. <see cref="CacheKey"/> identifies the image content (for example a file path plus
/// last-write time or a content hash); equal keys must mean equal pixels because embeddings are cached by it.
/// </summary>
public sealed class AiImage
{
    public AiImage(byte[] pixels, int width, int height, string cacheKey)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        ArgumentNullException.ThrowIfNull(cacheKey);
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width), "Image size must be positive.");
        if (pixels.LongLength != (long)width * height * 3) throw new ArgumentException("Pixels must hold width*height*3 BGR bytes.", nameof(pixels));
        Pixels = pixels;
        Width = width;
        Height = height;
        CacheKey = cacheKey;
    }

    public byte[] Pixels { get; }

    public int Width { get; }

    public int Height { get; }

    public string CacheKey { get; }
}

/// <summary>A click in image pixels; <paramref name="Positive"/> false marks background.</summary>
public readonly record struct AiPoint(float X, float Y, bool Positive);

/// <summary>A prompt box in image pixels (X1,Y1 top-left; X2,Y2 bottom-right).</summary>
public readonly record struct AiBox(float X1, float Y1, float X2, float Y2);

/// <summary>Prompt for a backend: SAM2 uses points and the box, YOLO-World uses the text classes (empty = all model classes).</summary>
public sealed class AiPrompt
{
    public AiPrompt(IReadOnlyList<AiPoint>? points = null, AiBox? box = null, IReadOnlyList<string>? textClasses = null)
    {
        Points = points ?? Array.Empty<AiPoint>();
        Box = box;
        TextClasses = textClasses ?? Array.Empty<string>();
    }

    public IReadOnlyList<AiPoint> Points { get; }

    public AiBox? Box { get; }

    public IReadOnlyList<string> TextClasses { get; }
}

/// <summary>
/// One suggestion in original image pixels. A mask result has an <see cref="Outer"/> polygon and <see cref="Holes"/>;
/// a box result (<see cref="IsBox"/>) has its four corners as <see cref="Outer"/> and no holes. <see cref="Box"/> is the bounds.
/// </summary>
public sealed class AiProposal
{
    public AiProposal(IReadOnlyList<PointF> outer, IReadOnlyList<IReadOnlyList<PointF>> holes, float score, string? label, bool isBox, RectangleF box)
    {
        Outer = outer;
        Holes = holes;
        Score = score;
        Label = label;
        IsBox = isBox;
        Box = box;
    }

    public IReadOnlyList<PointF> Outer { get; }

    public IReadOnlyList<IReadOnlyList<PointF>> Holes { get; }

    public float Score { get; }

    public string? Label { get; }

    public bool IsBox { get; }

    public RectangleF Box { get; }
}

/// <summary>An inference backend. Implementations are thread-safe; calls on one backend are serialised.</summary>
public interface IAiBackend
{
    string Name { get; }

    Task<IReadOnlyList<AiProposal>> RunAsync(AiImage image, AiPrompt prompt, CancellationToken ct);
}
