using System.Drawing;
using System.Globalization;
using EasyLabel.Ai.Onnx;
using EasyLabel.SegAI.AutoLabel;

namespace EasyAI.Desktop.ViewModels;

/// <summary>A cropped image and the position of its top-left corner in the original image.</summary>
public sealed class AiCrop
{
    public AiCrop(AiImage image, int offsetX, int offsetY)
    {
        Image = image;
        OffsetX = offsetX;
        OffsetY = offsetY;
    }

    public AiImage Image { get; }

    public int OffsetX { get; }

    public int OffsetY { get; }
}

/// <summary>
/// UI-free mapping of an AI backend result to the suggestion layer and the texts around it. No WPF types, so it is unit tested from
/// EasyLabel.SegAI.Tests (linked file).
/// </summary>
public static class AiSuggestionMapper
{
    public const string PolygonShape = "polygon";
    public const string RectangleShape = "rectangle";

    /// <summary>Rotated box: four corners in order (TL, TR, BR, BL), the shape type of the project's oriented_rectangle.</summary>
    public const string OrientedRectangleShape = "oriented_rectangle";

    /// <summary>
    /// One proposal as a suggestion of <paramref name="rel"/>. A mask result keeps its outer contour and holes (polygon); a box result
    /// becomes a "rectangle" suggestion. The class is the one chosen in the Labels panel (<paramref name="classIndex"/>); the model's
    /// own class name (YOLO-World) goes to the provenance text, not to the label, because the project only knows its OK / NG classes.
    /// Returns null for a degenerate result (polygon with fewer than 3 points, empty box).
    /// </summary>
    public static SegAiSuggestion? ToSuggestion(AiProposal proposal, string rel, int classIndex, string backendName, string modelFile)
    {
        string provenance = Provenance(backendName, modelFile, proposal.Label);
        if (proposal.IsBox)
        {
            RectangleF box = proposal.Box;
            if (box.Width <= 0f || box.Height <= 0f)
            {
                return null;
            }

            PointF[] corners =
            {
                new PointF(box.Left, box.Top),
                new PointF(box.Right, box.Top),
                new PointF(box.Right, box.Bottom),
                new PointF(box.Left, box.Bottom),
            };
            return new SegAiSuggestion(rel, classIndex, corners, Array.Empty<PointF[]>(), proposal.Score, null, RectangleShape, provenance);
        }

        if (proposal.Outer.Count < 3)
        {
            return null;
        }

        List<PointF[]> holes = new List<PointF[]>();
        foreach (IReadOnlyList<PointF> hole in proposal.Holes)
        {
            if (hole.Count >= 3)
            {
                holes.Add(hole.ToArray());
            }
        }

        return new SegAiSuggestion(rel, classIndex, proposal.Outer.ToArray(), holes.ToArray(), proposal.Score, null, PolygonShape, provenance);
    }

    /// <summary>
    /// The part of <paramref name="image"/> inside <paramref name="box"/> (outward rounded to whole pixels, kept inside the image, at least
    /// 1x1) as its own image, and where its top-left corner sits in the original. Used for YOLO-World, which detects inside a dragged box.
    /// </summary>
    public static AiCrop Crop(AiImage image, AiBox box)
    {
        int left = Math.Min(Math.Max((int)Math.Floor(Math.Min(box.X1, box.X2)), 0), image.Width - 1);
        int top = Math.Min(Math.Max((int)Math.Floor(Math.Min(box.Y1, box.Y2)), 0), image.Height - 1);
        int right = Math.Min(Math.Max((int)Math.Ceiling(Math.Max(box.X1, box.X2)), left + 1), image.Width);
        int bottom = Math.Min(Math.Max((int)Math.Ceiling(Math.Max(box.Y1, box.Y2)), top + 1), image.Height);
        int width = right - left;
        int height = bottom - top;
        byte[] pixels = new byte[width * height * 3];
        for (int row = 0; row < height; row++)
        {
            Buffer.BlockCopy(image.Pixels, ((top + row) * image.Width + left) * 3, pixels, row * width * 3, width * 3);
        }

        string key = image.CacheKey + "|crop:" + left.ToString(CultureInfo.InvariantCulture) + "," + top.ToString(CultureInfo.InvariantCulture)
            + "," + width.ToString(CultureInfo.InvariantCulture) + "," + height.ToString(CultureInfo.InvariantCulture);
        return new AiCrop(new AiImage(pixels, width, height, key), left, top);
    }

    /// <summary>The proposal moved by (<paramref name="dx"/>, <paramref name="dy"/>) image pixels (results of a crop back into the full image).</summary>
    public static AiProposal Offset(AiProposal proposal, int dx, int dy)
    {
        if (dx == 0 && dy == 0)
        {
            return proposal;
        }

        PointF[] outer = proposal.Outer.Select(p => new PointF(p.X + dx, p.Y + dy)).ToArray();
        List<IReadOnlyList<PointF>> holes = new List<IReadOnlyList<PointF>>();
        foreach (IReadOnlyList<PointF> hole in proposal.Holes)
        {
            holes.Add(hole.Select(p => new PointF(p.X + dx, p.Y + dy)).ToArray());
        }

        RectangleF box = proposal.Box;
        return new AiProposal(outer, holes, proposal.Score, proposal.Label, proposal.IsBox, new RectangleF(box.X + dx, box.Y + dy, box.Width, box.Height));
    }

    /// <summary>Provenance text of an accepted shape: backend, model file and, for a detector, the class the model named.</summary>
    public static string Provenance(string backendName, string modelFile, string? modelClass)
    {
        string text = backendName + " | " + modelFile;
        return string.IsNullOrWhiteSpace(modelClass) ? text : text + " | " + modelClass;
    }

    /// <summary>Embedding-cache key of an image file: path, last-write ticks and length (a changed file is a different image).</summary>
    public static string ImageKey(string path, long lastWriteTicks, long length)
    {
        return path + "|" + lastWriteTicks.ToString(CultureInfo.InvariantCulture) + "|" + length.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Where the model ran: the provider name ("DirectML", "CPU"), or the localized fallback text when the GPU could not be used.
    /// <paramref name="fallbackFormat"/> takes the raw reason as {0}.
    /// </summary>
    public static string ProviderText(string provider, string? fallbackReason, string fallbackFormat)
    {
        return string.IsNullOrWhiteSpace(fallbackReason) ? provider : string.Format(CultureInfo.CurrentCulture, fallbackFormat, fallbackReason);
    }
}
