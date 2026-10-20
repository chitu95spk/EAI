namespace EasyLabel.Ai.Mcp;

/// <summary>One shape for measuring: a reference name the agent can quote, its label and its outline ring in image pixels.</summary>
internal sealed record MeasuredShape(string Ref, string Label, string ShapeType, List<double[]> Ring);

internal sealed record ShapeStats(string Ref, string Label, string ShapeType, double X, double Y, double Width, double Height, double Area);

internal sealed record PairStats(string A, string B, double Intersection, double Iou, double CoverA, double CoverB, string Relation, bool Duplicate);

/// <summary>
/// Pure geometry for the measure tool (no image needed). Shapes are simple polygons (holes are ignored).
/// Intersection area: exact Sutherland-Hodgman clipping when both outlines are convex (rectangles, rotated boxes, most polygons);
/// otherwise a point-in-polygon raster of the intersection of the two bounding boxes on a <see cref="RasterSide"/> grid
/// (about 0.5 percent of the box area per cell edge). IoU = I / (A + B - I); cover = I / own area.
/// </summary>
internal static class ShapeMeasure
{
    /// <summary>IoU above this value flags two shapes as near-duplicates.</summary>
    public const double DuplicateIou = 0.85;

    /// <summary>A shape counts as inside another when at least this share of its area lies in the other.</summary>
    public const double ContainShare = 0.98;

    /// <summary>Cells along the longer side of the bounding-box intersection in the raster fallback.</summary>
    public const int RasterSide = 256;

    /// <summary>The outline of a shape: a rectangle given as 2 corners becomes 4 corners; other shapes keep their points.</summary>
    public static List<double[]> ToRing(string shapeType, IReadOnlyList<double[]> points)
    {
        var ring = new List<double[]>();
        if (points == null) return ring;
        if (string.Equals(shapeType, "rectangle", StringComparison.OrdinalIgnoreCase) && points.Count == 2)
        {
            double x1 = Math.Min(points[0][0], points[1][0]), x2 = Math.Max(points[0][0], points[1][0]);
            double y1 = Math.Min(points[0][1], points[1][1]), y2 = Math.Max(points[0][1], points[1][1]);
            ring.Add(new[] { x1, y1 });
            ring.Add(new[] { x2, y1 });
            ring.Add(new[] { x2, y2 });
            ring.Add(new[] { x1, y2 });
            return ring;
        }

        foreach (double[] p in points)
        {
            if (p != null && p.Length >= 2) ring.Add(new[] { p[0], p[1] });
        }

        return ring;
    }

    public static double Area(IReadOnlyList<double[]> ring)
    {
        return Math.Abs(SignedArea(ring));
    }

    private static double SignedArea(IReadOnlyList<double[]> ring)
    {
        if (ring.Count < 3) return 0;
        double sum = 0;
        for (int i = 0; i < ring.Count; i++)
        {
            double[] a = ring[i];
            double[] b = ring[(i + 1) % ring.Count];
            sum += a[0] * b[1] - b[0] * a[1];
        }

        return sum / 2.0;
    }

    public static ShapeStats Stats(MeasuredShape shape)
    {
        if (shape.Ring.Count == 0) return new ShapeStats(shape.Ref, shape.Label, shape.ShapeType, 0, 0, 0, 0, 0);
        double minX = shape.Ring.Min(p => p[0]), maxX = shape.Ring.Max(p => p[0]);
        double minY = shape.Ring.Min(p => p[1]), maxY = shape.Ring.Max(p => p[1]);
        return new ShapeStats(shape.Ref, shape.Label, shape.ShapeType, minX, minY, maxX - minX, maxY - minY, Area(shape.Ring));
    }

    public static PairStats Compare(MeasuredShape a, MeasuredShape b)
    {
        double areaA = Area(a.Ring);
        double areaB = Area(b.Ring);
        double inter = IntersectionArea(a.Ring, b.Ring);
        inter = Math.Min(inter, Math.Min(areaA, areaB));
        double union = areaA + areaB - inter;
        double iou = union > 0 ? inter / union : 0;
        double coverA = areaA > 0 ? inter / areaA : 0;
        double coverB = areaB > 0 ? inter / areaB : 0;

        string relation;
        if (inter <= 0) relation = "disjoint";
        else if (coverA >= ContainShare && coverB >= ContainShare) relation = "same";
        else if (coverA >= ContainShare) relation = "a_inside_b";
        else if (coverB >= ContainShare) relation = "b_inside_a";
        else relation = "overlap";

        return new PairStats(a.Ref, b.Ref, inter, iou, coverA, coverB, relation, iou > DuplicateIou);
    }

    public static double IntersectionArea(IReadOnlyList<double[]> a, IReadOnlyList<double[]> b)
    {
        if (Area(a) <= 0 || Area(b) <= 0) return 0;
        double left = Math.Max(a.Min(p => p[0]), b.Min(p => p[0]));
        double right = Math.Min(a.Max(p => p[0]), b.Max(p => p[0]));
        double top = Math.Max(a.Min(p => p[1]), b.Min(p => p[1]));
        double bottom = Math.Min(a.Max(p => p[1]), b.Max(p => p[1]));
        if (right <= left || bottom <= top) return 0;

        if (IsConvex(a) && IsConvex(b))
        {
            List<double[]> subject = SignedArea(a) > 0 ? a.ToList() : a.Reverse().ToList();
            List<double[]> clip = SignedArea(b) > 0 ? b.ToList() : b.Reverse().ToList();
            return Area(Clip(subject, clip));
        }

        return RasterIntersection(a, b, left, top, right, bottom);
    }

    private static bool IsConvex(IReadOnlyList<double[]> ring)
    {
        if (ring.Count < 3) return false;
        int sign = 0;
        for (int i = 0; i < ring.Count; i++)
        {
            double[] p = ring[i];
            double[] q = ring[(i + 1) % ring.Count];
            double[] r = ring[(i + 2) % ring.Count];
            double cross = (q[0] - p[0]) * (r[1] - q[1]) - (q[1] - p[1]) * (r[0] - q[0]);
            if (Math.Abs(cross) < 1e-9) continue;
            int s = cross > 0 ? 1 : -1;
            if (sign == 0) sign = s;
            else if (sign != s) return false;
        }

        return true;
    }

    // Sutherland-Hodgman; both polygons counter-clockwise (in the y-down image frame the sign convention is irrelevant as long as both agree).
    private static List<double[]> Clip(List<double[]> subject, List<double[]> clip)
    {
        List<double[]> output = subject;
        for (int i = 0; i < clip.Count && output.Count > 0; i++)
        {
            double[] c1 = clip[i];
            double[] c2 = clip[(i + 1) % clip.Count];
            List<double[]> input = output;
            output = new List<double[]>();
            for (int j = 0; j < input.Count; j++)
            {
                double[] cur = input[j];
                double[] prev = input[(j + input.Count - 1) % input.Count];
                double dc = Side(c1, c2, cur);
                double dp = Side(c1, c2, prev);
                bool curIn = dc >= 0;
                bool prevIn = dp >= 0;
                if (curIn)
                {
                    if (!prevIn) output.Add(Cut(prev, cur, dp, dc));
                    output.Add(cur);
                }
                else if (prevIn)
                {
                    output.Add(Cut(prev, cur, dp, dc));
                }
            }
        }

        return output;
    }

    private static double Side(double[] a, double[] b, double[] p)
    {
        return (b[0] - a[0]) * (p[1] - a[1]) - (b[1] - a[1]) * (p[0] - a[0]);
    }

    private static double[] Cut(double[] prev, double[] cur, double dp, double dc)
    {
        double t = dp / (dp - dc);
        return new[] { prev[0] + t * (cur[0] - prev[0]), prev[1] + t * (cur[1] - prev[1]) };
    }

    private static double RasterIntersection(IReadOnlyList<double[]> a, IReadOnlyList<double[]> b, double left, double top, double right, double bottom)
    {
        double cell = Math.Max(right - left, bottom - top) / RasterSide;
        int nx = Math.Max(1, (int)Math.Ceiling((right - left) / cell));
        int ny = Math.Max(1, (int)Math.Ceiling((bottom - top) / cell));
        double dx = (right - left) / nx;
        double dy = (bottom - top) / ny;
        long hits = 0;
        for (int iy = 0; iy < ny; iy++)
        {
            double y = top + (iy + 0.5) * dy;
            for (int ix = 0; ix < nx; ix++)
            {
                double x = left + (ix + 0.5) * dx;
                if (Contains(a, x, y) && Contains(b, x, y)) hits++;
            }
        }

        return hits * dx * dy;
    }

    /// <summary>Even-odd point-in-polygon test.</summary>
    public static bool Contains(IReadOnlyList<double[]> ring, double x, double y)
    {
        bool inside = false;
        for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
        {
            double xi = ring[i][0], yi = ring[i][1], xj = ring[j][0], yj = ring[j][1];
            if ((yi > y) != (yj > y) && x < (xj - xi) * (y - yi) / (yj - yi) + xi) inside = !inside;
        }

        return inside;
    }
}
