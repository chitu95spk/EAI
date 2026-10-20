namespace EasyLabel.Core.Editing;

/// <summary>
/// Pure overlap geometry for the duplicate guard of the agent (no image, no UI). Shapes are simple polygons given as rings of [x, y] in
/// image pixels (holes are ignored). Intersection: exact Sutherland-Hodgman clipping when both outlines are convex (rectangles, rotated
/// boxes, most polygons), otherwise a point-in-polygon raster of the intersection of the two bounding boxes on a
/// <see cref="RasterSide"/> grid. Ported from the measure tool of EasyLabel.Ai.Mcp (ShapeMeasure), which stays as it is.
/// </summary>
public static class ShapeOverlap
{
    /// <summary>A new shape whose IoU with another shape (any label) reaches this value is a duplicate of it.</summary>
    public const double DuplicateIou = 0.5;

    /// <summary>A new shape of which at least this share lies inside one shape of the SAME label is a duplicate of it.</summary>
    public const double SameLabelCover = 0.8;

    /// <summary>Cells along the longer side of the bounding-box intersection in the raster fallback.</summary>
    public const int RasterSide = 256;

    /// <summary>
    /// The outline of a shape: a rectangle (2 or 4 points) becomes its 4 axis-aligned corners; every other kind keeps its points.
    /// Missing or short points give a ring without area.
    /// </summary>
    public static List<double[]> ToRing(string? shapeType, IReadOnlyList<double[]>? points)
    {
        var ring = new List<double[]>();
        if (points == null) return ring;
        if (string.Equals(shapeType, "rectangle", StringComparison.OrdinalIgnoreCase) && points.Count >= 2)
        {
            double x1 = double.MaxValue, x2 = double.MinValue, y1 = double.MaxValue, y2 = double.MinValue;
            foreach (double[] p in points)
            {
                if (p == null || p.Length < 2) continue;
                x1 = Math.Min(x1, p[0]);
                x2 = Math.Max(x2, p[0]);
                y1 = Math.Min(y1, p[1]);
                y2 = Math.Max(y2, p[1]);
            }

            if (x1 > x2 || y1 > y2) return ring;
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

    /// <summary>Absolute polygon area; 0 for fewer than 3 points.</summary>
    public static double Area(IReadOnlyList<double[]> ring)
    {
        return Math.Abs(SignedArea(ring));
    }

    /// <summary>Area of the intersection of two rings; 0 when either has no area or the bounding boxes do not meet.</summary>
    public static double IntersectionArea(IReadOnlyList<double[]> a, IReadOnlyList<double[]> b)
    {
        double areaA = Area(a);
        double areaB = Area(b);
        if (areaA <= 0 || areaB <= 0) return 0;
        double left = Math.Max(a.Min(p => p[0]), b.Min(p => p[0]));
        double right = Math.Min(a.Max(p => p[0]), b.Max(p => p[0]));
        double top = Math.Max(a.Min(p => p[1]), b.Min(p => p[1]));
        double bottom = Math.Min(a.Max(p => p[1]), b.Max(p => p[1]));
        if (right <= left || bottom <= top) return 0;

        double inter;
        if (IsConvex(a) && IsConvex(b))
        {
            List<double[]> subject = SignedArea(a) > 0 ? a.ToList() : a.Reverse().ToList();
            List<double[]> clip = SignedArea(b) > 0 ? b.ToList() : b.Reverse().ToList();
            inter = Area(Clip(subject, clip));
        }
        else
        {
            inter = RasterIntersection(a, b, left, top, right, bottom);
        }

        return Math.Min(inter, Math.Min(areaA, areaB));
    }

    /// <summary>Intersection over union of two rings (0 when either has no area).</summary>
    public static double Iou(IReadOnlyList<double[]> a, IReadOnlyList<double[]> b)
    {
        double inter = IntersectionArea(a, b);
        double union = Area(a) + Area(b) - inter;
        return union > 0 ? inter / union : 0;
    }

    /// <summary>Share (0..1) of <paramref name="inner"/> that lies inside <paramref name="outer"/>.</summary>
    public static double Coverage(IReadOnlyList<double[]> inner, IReadOnlyList<double[]> outer)
    {
        double area = Area(inner);
        return area > 0 ? IntersectionArea(inner, outer) / area : 0;
    }

    /// <summary>
    /// The duplicate rule for a NEW shape against one other shape: IoU of at least <see cref="DuplicateIou"/> (any label), or at least
    /// <see cref="SameLabelCover"/> of the new shape inside the other one when both carry the same label (case-insensitive). A small part
    /// inside a larger part with a different label is not a duplicate. <paramref name="iou"/> is the IoU found (0 when nothing overlaps).
    /// </summary>
    public static bool IsDuplicate(string? newLabel, IReadOnlyList<double[]> newRing, string? otherLabel, IReadOnlyList<double[]> otherRing, out double iou)
    {
        iou = 0;
        double areaNew = Area(newRing);
        double areaOther = Area(otherRing);
        if (areaNew <= 0 || areaOther <= 0) return false;
        double inter = IntersectionArea(newRing, otherRing);
        if (inter <= 0) return false;
        double union = areaNew + areaOther - inter;
        iou = union > 0 ? inter / union : 0;
        if (iou >= DuplicateIou) return true;
        bool sameLabel = string.Equals((newLabel ?? string.Empty).Trim(), (otherLabel ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase);
        return sameLabel && inter / areaNew >= SameLabelCover;
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

    // Sutherland-Hodgman; both polygons wound the same way (the y-down sign convention does not matter as long as both agree).
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
