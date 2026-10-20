using System.Globalization;
using System.Text;
using OpenCvSharp;

namespace EasyLabel.Ai.Onnx;

/// <summary>
/// The image as text (put into the agent prompt by the app): a table of the dark-on-light blobs with centre, size and angle in original pixels, plus a coarse
/// occupancy map. The model reads numbers faster than it localises objects in a picture; blobs flagged "merged" hold touching objects.
/// </summary>
public static class ImageMarkdown
{
    public const int WorkSide = 640;
    public const int MapColumns = 64;
    public const int MaxBlobs = 120;
    private const double MinAreaFraction = 0.0004;

    internal sealed record Blob(int Id, double X, double Y, double Long, double Short, double Angle, double AreaShare, bool Merged);

    public static string Build(Mat source)
    {
        double scale = Math.Min(1.0, (double)WorkSide / Math.Max(source.Width, source.Height));
        using Mat work = new Mat();
        if (scale < 1.0) Cv2.Resize(source, work, new Size(Math.Max(1, (int)Math.Round(source.Width * scale)), Math.Max(1, (int)Math.Round(source.Height * scale))), 0, 0, InterpolationFlags.Area);
        else source.CopyTo(work);
        using Mat gray = new Mat();
        if (work.Channels() == 1) work.CopyTo(gray);
        else Cv2.CvtColor(work, gray, ColorConversionCodes.BGR2GRAY);

        using Mat fg = Foreground(gray);
        List<Blob> blobs = Blobs(fg, scale, work.Width, work.Height);
        return Render(blobs, fg, scale, source.Width, source.Height);
    }

    // Objects differ from the (smooth) background by darkness or by texture: a blurred copy is the background estimate.
    internal static Mat Foreground(Mat gray)
    {
        using Mat blur = new Mat();
        Cv2.GaussianBlur(gray, blur, new Size(5, 5), 0);
        using Mat bg = new Mat();
        int k = (Math.Max(gray.Width, gray.Height) / 8) | 1;
        Cv2.MedianBlur(blur, bg, Math.Min(k, 255) | 1);
        using Mat diff = new Mat();
        Cv2.Subtract(bg, blur, diff);
        Mat mask = new Mat();
        Cv2.Threshold(diff, mask, 0, 255, ThresholdTypes.Binary | ThresholdTypes.Otsu);
        using Mat kernel = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(5, 5));
        Cv2.MorphologyEx(mask, mask, MorphTypes.Close, kernel);
        Cv2.MorphologyEx(mask, mask, MorphTypes.Open, kernel);
        return mask;
    }

    private static List<Blob> Blobs(Mat fg, double scale, int w, int h)
    {
        Cv2.FindContours(fg, out Point[][] contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);
        double imageArea = (double)w * h;
        var found = new List<(RotatedRect Rect, double Area, double Solidity)>();
        foreach (Point[] c in contours)
        {
            double area = Cv2.ContourArea(c);
            if (area < MinAreaFraction * imageArea) continue;
            RotatedRect r = Cv2.MinAreaRect(c);
            double hull = Cv2.ContourArea(Cv2.ConvexHull(c));
            found.Add((r, area, hull > 0 ? area / hull : 1));
        }

        found = found.OrderByDescending(f => f.Area).Take(MaxBlobs).OrderBy(f => f.Rect.Center.Y).ThenBy(f => f.Rect.Center.X).ToList();
        double median = found.Count >= 3 ? found.Select(f => f.Area).OrderBy(a => a).ElementAt(found.Count / 2) : 0;
        var blobs = new List<Blob>();
        for (int i = 0; i < found.Count; i++)
        {
            RotatedRect r = found[i].Rect;
            double lng = Math.Max(r.Size.Width, r.Size.Height) / scale, sht = Math.Min(r.Size.Width, r.Size.Height) / scale;
            double angle = r.Size.Width >= r.Size.Height ? r.Angle : r.Angle + 90;
            angle = ((angle % 180) + 180) % 180;
            bool merged = found[i].Solidity < 0.8 || (median > 0 && found[i].Area > 1.8 * median);
            blobs.Add(new Blob(i + 1, r.Center.X / scale, r.Center.Y / scale, lng, sht, angle, found[i].Area / imageArea, merged));
        }

        return blobs;
    }

    private static string Render(List<Blob> blobs, Mat fg, double scale, int width, int height)
    {
        var sb = new StringBuilder();
        CultureInfo ci = CultureInfo.InvariantCulture;
        sb.Append("# Image ").Append(width.ToString(ci)).Append('x').Append(height.ToString(ci)).Append(" px\n\n");
        sb.Append("Blobs that differ from the background (x, y, long and short side in ORIGINAL pixels; angle of the long axis 0-180 deg, clockwise from +x). ");
        sb.Append(blobs.Count.ToString(ci)).Append(" found, ordered top to bottom.\n\n");
        sb.Append("| # | x | y | long | short | angle | area% | note |\n|---|---|---|---|---|---|---|---|\n");
        foreach (Blob b in blobs)
        {
            sb.Append("| ").Append(b.Id.ToString(ci)).Append(" | ").Append(b.X.ToString("0", ci)).Append(" | ").Append(b.Y.ToString("0", ci))
              .Append(" | ").Append(b.Long.ToString("0", ci)).Append(" | ").Append(b.Short.ToString("0", ci)).Append(" | ").Append(b.Angle.ToString("0", ci))
              .Append(" | ").Append((b.AreaShare * 100).ToString("0.0", ci)).Append(" | ").Append(b.Merged ? "merged? (several touching objects)" : string.Empty).Append(" |\n");
        }

        // Occupancy map: one char per cell, '#' mostly foreground, '+' some, '.' none.
        int cols = MapColumns;
        double cell = (double)fg.Width / cols;
        int rows = Math.Max(1, (int)Math.Round(fg.Height / (cell * 2)));
        double cellH = (double)fg.Height / rows;
        sb.Append("\nOccupancy map (").Append(cols.ToString(ci)).Append(" columns = ").Append((width / (double)cols).ToString("0", ci)).Append(" px each, ")
          .Append(rows.ToString(ci)).Append(" rows = ").Append((height / (double)rows).ToString("0", ci)).Append(" px each):\n```\n");
        for (int r = 0; r < rows; r++)
        {
            for (int c = 0; c < cols; c++)
            {
                var rect = new Rect((int)(c * cell), (int)(r * cellH), Math.Max(1, (int)cell), Math.Max(1, (int)cellH));
                rect = rect.Intersect(new Rect(0, 0, fg.Width, fg.Height));
                double share = rect.Width * rect.Height == 0 ? 0 : Cv2.CountNonZero(new Mat(fg, rect)) / (double)(rect.Width * rect.Height);
                sb.Append(share > 0.5 ? '#' : share > 0.1 ? '+' : '.');
            }

            sb.Append('\n');
        }

        sb.Append("```\n\nThe blob centres can be used as the clicks for detect_points. A row marked merged? holds several touching objects: read those from the picture and give one click per object.\n");
        return sb.ToString();
    }
}
