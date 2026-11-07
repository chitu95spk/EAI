using OpenCvSharp;

namespace EasyLabel.LabelHarness;

/// <summary>A labelled object in image pixels: the class and the outline the user (SAM) confirmed, or one the harness produced.</summary>
public sealed record SeedObject(string Label, IReadOnlyList<double[]> Polygon);

public static class LabelGeometry
{
    /// <summary>Boundary check shared by every entry point: at least 3 finite points.</summary>
    public static void Validate(IReadOnlyList<double[]> polygon)
    {
        if (polygon == null || polygon.Count < 3) throw new ArgumentException("A polygon needs at least 3 points.");
        foreach (double[] p in polygon)
        {
            if (p == null || p.Length < 2 || !double.IsFinite(p[0]) || !double.IsFinite(p[1])) throw new ArgumentException("A polygon point must be a finite (x, y).");
        }
    }

    /// <summary>x1, y1, x2, y2 of the polygon.</summary>
    public static double[] Bounds(IReadOnlyList<double[]> polygon)
    {
        return new[] { polygon.Min(p => p[0]), polygon.Min(p => p[1]), polygon.Max(p => p[0]), polygon.Max(p => p[1]) };
    }

    /// <summary>Intersection over union of two outlines, rasterised into a width x height image.</summary>
    public static double PolygonIou(IReadOnlyList<double[]> a, IReadOnlyList<double[]> b, int width, int height)
    {
        using Mat ma = Raster(a, width, height);
        using Mat mb = Raster(b, width, height);
        using Mat inter = new Mat();
        using Mat union = new Mat();
        Cv2.BitwiseAnd(ma, mb, inter);
        Cv2.BitwiseOr(ma, mb, union);
        int u = Cv2.CountNonZero(union);
        return u == 0 ? 0.0 : (double)Cv2.CountNonZero(inter) / u;
    }

    public static Mat Raster(IReadOnlyList<double[]> polygon, int width, int height)
    {
        Validate(polygon);
        var mat = new Mat(height, width, MatType.CV_8UC1, Scalar.All(0));
        Point[] pts = polygon.Select(p => new Point((int)Math.Round(p[0]), (int)Math.Round(p[1]))).ToArray();
        Cv2.FillPoly(mat, new[] { pts }, Scalar.All(255));
        return mat;
    }
}
