using System.Drawing;
using System.Runtime.InteropServices;
using OpenCvSharp;
using CvPoint = OpenCvSharp.Point;

namespace EasyLabel.Ai.Onnx;

/// <summary>The largest connected region of a mask as an outer polygon plus hole polygons, in mask pixels.</summary>
public sealed class MaskPolygon
{
    public MaskPolygon(PointF[] outer, PointF[][] holes, int pixelCount, RectangleF bounds)
    {
        Outer = outer;
        Holes = holes;
        PixelCount = pixelCount;
        Bounds = bounds;
    }

    public PointF[] Outer { get; }

    public PointF[][] Holes { get; }

    /// <summary>Number of mask pixels of the chosen component.</summary>
    public int PixelCount { get; }

    public RectangleF Bounds { get; }
}

/// <summary>
/// Mask to polygon with the same policy as the Segment AI suggestions: 8-connected components, RETR_CCOMP contours,
/// ApproxPolyDP epsilon 0.5 (loosened until at most <c>maxPoints</c> remain), hole polygons that follow the background pixels
/// of the hole so cutting them out of the outer polygon reproduces the mask. Only the largest component is returned.
/// </summary>
public static class MaskPolygonizer
{
    public const double SimplifyEpsilon = 0.5;
    public const int DefaultMaxPoints = 2000;

    /// <summary>Converts logits to a 0/255 mask: 255 where value is greater than <paramref name="threshold"/>.</summary>
    public static byte[] Threshold(float[] logits, float threshold = 0f)
    {
        ArgumentNullException.ThrowIfNull(logits);
        var mask = new byte[logits.Length];
        for (int i = 0; i < logits.Length; i++) mask[i] = logits[i] > threshold ? (byte)255 : (byte)0;
        return mask;
    }

    /// <summary>Polygon of the largest component of <paramref name="mask"/> (non-zero = foreground); null when it is empty or too thin for a polygon.</summary>
    public static MaskPolygon? Polygonize(byte[] mask, int width, int height, int maxPoints = DefaultMaxPoints)
    {
        ArgumentNullException.ThrowIfNull(mask);
        if (width <= 0 || height <= 0 || mask.Length != width * height) throw new ArgumentException("Mask length must equal width*height.", nameof(mask));
        if (maxPoints < 3) throw new ArgumentOutOfRangeException(nameof(maxPoints));

        using var source = new Mat(height, width, MatType.CV_8UC1);
        Marshal.Copy(mask, 0, source.Data, mask.Length);
        using var binary = new Mat();
        Cv2.Threshold(source, binary, 0, 255, ThresholdTypes.Binary);
        using var labels = new Mat();
        using var stats = new Mat();
        using var centroids = new Mat();
        int labelCount = Cv2.ConnectedComponentsWithStats(binary, labels, stats, centroids, PixelConnectivity.Connectivity8, MatType.CV_32S);
        if (labelCount <= 1) return null;

        int best = 1;
        int bestArea = -1;
        for (int label = 1; label < labelCount; label++)
        {
            int area = stats.At<int>(label, (int)ConnectedComponentsTypes.Area);
            if (area > bestArea) { bestArea = area; best = label; }
        }

        int left = stats.At<int>(best, (int)ConnectedComponentsTypes.Left);
        int top = stats.At<int>(best, (int)ConnectedComponentsTypes.Top);
        int cw = stats.At<int>(best, (int)ConnectedComponentsTypes.Width);
        int ch = stats.At<int>(best, (int)ConnectedComponentsTypes.Height);
        using var local = new Mat(labels, new OpenCvSharp.Rect(left, top, cw, ch));
        using var component = new Mat();
        Cv2.Compare(local, best, component, CmpTypes.EQ);
        Cv2.FindContours(component, out CvPoint[][] contours, out HierarchyIndex[] hierarchy, RetrievalModes.CComp, ContourApproximationModes.ApproxSimple);

        int outerIndex = -1;
        double outerArea = -1;
        for (int i = 0; i < contours.Length; i++)
        {
            if (hierarchy[i].Parent >= 0) continue;
            double area = Math.Abs(Cv2.ContourArea(contours[i]));
            if (area > outerArea) { outerArea = area; outerIndex = i; }
        }

        if (outerIndex < 0) return null;
        PointF[]? outer = Simplify(contours[outerIndex], left, top, width, height, maxPoints);
        if (outer == null) return null;

        var holes = new List<PointF[]>();
        for (int i = 0; i < contours.Length; i++)
        {
            if (hierarchy[i].Parent != outerIndex) continue;
            // Same trick as SegAiSuggestionBuilder: trace the background pixels inside the hole border so the cut-out is exact.
            using var filled = new Mat(component.Size(), MatType.CV_8UC1, Scalar.All(0));
            Cv2.DrawContours(filled, contours, i, Scalar.All(255), -1);
            using var notComponent = new Mat();
            Cv2.BitwiseNot(component, notComponent);
            Cv2.BitwiseAnd(filled, notComponent, filled);
            Cv2.FindContours(filled, out CvPoint[][] holeContours, out HierarchyIndex[] _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);
            if (holeContours.Length == 0) continue;
            CvPoint[] biggest = holeContours.OrderByDescending(c => c.Length).First();
            PointF[]? hole = Simplify(biggest, left, top, width, height, maxPoints);
            if (hole != null) holes.Add(hole);
        }

        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        foreach (PointF p in outer)
        {
            minX = Math.Min(minX, p.X);
            minY = Math.Min(minY, p.Y);
            maxX = Math.Max(maxX, p.X);
            maxY = Math.Max(maxY, p.Y);
        }

        return new MaskPolygon(outer, holes.ToArray(), bestArea, new RectangleF(minX, minY, maxX - minX, maxY - minY));
    }

    private static PointF[]? Simplify(CvPoint[] contour, int offsetX, int offsetY, int width, int height, int maxPoints)
    {
        CvPoint[] approx = Cv2.ApproxPolyDP(contour, SimplifyEpsilon, true);
        double epsilon = SimplifyEpsilon;
        while (approx.Length > maxPoints && epsilon < 256)
        {
            epsilon *= 1.5;
            approx = Cv2.ApproxPolyDP(contour, epsilon, true);
        }

        if (approx.Length > maxPoints)
        {
            var sampled = new CvPoint[maxPoints];
            for (int i = 0; i < maxPoints; i++) sampled[i] = approx[(int)((long)i * approx.Length / maxPoints)];
            approx = sampled;
        }

        if (approx.Length < 3) return null;
        var points = new PointF[approx.Length];
        for (int i = 0; i < approx.Length; i++)
        {
            float x = Math.Min(Math.Max(approx[i].X + offsetX, 0f), width);
            float y = Math.Min(Math.Max(approx[i].Y + offsetY, 0f), height);
            points[i] = new PointF(x, y);
        }

        return points;
    }
}
