namespace EasyLabel.Core.Convert;

/// <summary>
/// Minimum-area enclosing rectangle of a point set in pure C# (convex hull, then every hull edge as a rectangle side, which is the
/// rotating-calipers result). It replaces <c>cv2.minAreaRect</c> + <c>cv2.boxPoints</c> of the OBB script. The corner order is
/// stable (clockwise on screen, y down, starting at the lowest-y then leftmost corner); <c>cv2.boxPoints</c> may list the same four
/// corners in another order, which does not matter for YOLO OBB.
/// </summary>
public static class MinAreaRectangle
{
    /// <summary>
    /// Computes the rectangle of <paramref name="points"/> (x, y pairs; at least one point). Returns the 8 corner coordinates
    /// x1 y1 x2 y2 x3 y3 x4 y4, or null when there are no finite points. A single point or collinear points give a zero-area rectangle.
    /// </summary>
    public static double[]? Compute(IReadOnlyList<(double X, double Y)> points, out double area)
    {
        area = 0d;
        List<(double X, double Y)> finite = new List<(double X, double Y)>(points.Count);
        foreach ((double X, double Y) p in points)
        {
            if (double.IsFinite(p.X) && double.IsFinite(p.Y))
            {
                finite.Add(p);
            }
        }

        if (finite.Count == 0)
        {
            return null;
        }

        List<(double X, double Y)> hull = ConvexHull(finite);
        if (hull.Count == 1)
        {
            (double X, double Y) only = hull[0];
            return new[] { only.X, only.Y, only.X, only.Y, only.X, only.Y, only.X, only.Y };
        }

        double bestArea = double.PositiveInfinity;
        double[]? best = null;
        for (int i = 0; i < hull.Count; i++)
        {
            (double X, double Y) a = hull[i];
            (double X, double Y) b = hull[(i + 1) % hull.Count];
            double ex = b.X - a.X;
            double ey = b.Y - a.Y;
            double length = Math.Sqrt((ex * ex) + (ey * ey));
            if (length <= 0d)
            {
                continue;
            }

            double ux = ex / length;
            double uy = ey / length;
            double nx = -uy;
            double ny = ux;
            double minU = double.PositiveInfinity;
            double maxU = double.NegativeInfinity;
            double minN = double.PositiveInfinity;
            double maxN = double.NegativeInfinity;
            foreach ((double X, double Y) p in hull)
            {
                double pu = (p.X * ux) + (p.Y * uy);
                double pn = (p.X * nx) + (p.Y * ny);
                minU = Math.Min(minU, pu);
                maxU = Math.Max(maxU, pu);
                minN = Math.Min(minN, pn);
                maxN = Math.Max(maxN, pn);
            }

            double candidate = (maxU - minU) * (maxN - minN);
            if (candidate < bestArea)
            {
                bestArea = candidate;
                best = new[]
                {
                    (minU * ux) + (minN * nx), (minU * uy) + (minN * ny),
                    (maxU * ux) + (minN * nx), (maxU * uy) + (minN * ny),
                    (maxU * ux) + (maxN * nx), (maxU * uy) + (maxN * ny),
                    (minU * ux) + (maxN * nx), (minU * uy) + (maxN * ny),
                };
            }
        }

        if (best == null)
        {
            return null;
        }

        area = bestArea;
        return Order(best);
    }

    /// <summary>Clockwise on screen (y down), starting at the lowest-y corner (leftmost among near-equal y).</summary>
    private static double[] Order(double[] corners)
    {
        double signed = 0d;
        for (int i = 0; i < 4; i++)
        {
            int j = (i + 1) % 4;
            signed += (corners[i * 2] * corners[(j * 2) + 1]) - (corners[j * 2] * corners[(i * 2) + 1]);
        }

        // Positive shoelace sum = clockwise when y points down.
        int[] sequence = signed >= 0d ? new[] { 0, 1, 2, 3 } : new[] { 0, 3, 2, 1 };

        double minY = double.PositiveInfinity;
        double maxAbs = 1d;
        for (int i = 0; i < 4; i++)
        {
            minY = Math.Min(minY, corners[(i * 2) + 1]);
            maxAbs = Math.Max(maxAbs, Math.Max(Math.Abs(corners[i * 2]), Math.Abs(corners[(i * 2) + 1])));
        }

        double tolerance = 1e-9d * maxAbs;
        int start = -1;
        for (int k = 0; k < 4; k++)
        {
            int index = sequence[k];
            if (corners[(index * 2) + 1] - minY > tolerance)
            {
                continue;
            }

            if (start < 0 || corners[index * 2] < corners[sequence[start] * 2])
            {
                start = k;
            }
        }

        double[] result = new double[8];
        for (int k = 0; k < 4; k++)
        {
            int index = sequence[(start + k) % 4];
            result[k * 2] = corners[index * 2];
            result[(k * 2) + 1] = corners[(index * 2) + 1];
        }

        return result;
    }

    /// <summary>Andrew monotone chain; duplicates removed; collinear points dropped. One point for identical input, two for collinear input.</summary>
    private static List<(double X, double Y)> ConvexHull(List<(double X, double Y)> input)
    {
        List<(double X, double Y)> sorted = new List<(double X, double Y)>(input);
        sorted.Sort((p, q) => p.X != q.X ? p.X.CompareTo(q.X) : p.Y.CompareTo(q.Y));
        List<(double X, double Y)> unique = new List<(double X, double Y)>(sorted.Count);
        foreach ((double X, double Y) p in sorted)
        {
            if (unique.Count == 0 || unique[unique.Count - 1].X != p.X || unique[unique.Count - 1].Y != p.Y)
            {
                unique.Add(p);
            }
        }

        if (unique.Count <= 2)
        {
            return unique;
        }

        List<(double X, double Y)> hull = new List<(double X, double Y)>(unique.Count * 2);
        for (int i = 0; i < unique.Count; i++)
        {
            while (hull.Count >= 2 && Cross(hull[hull.Count - 2], hull[hull.Count - 1], unique[i]) <= 0d)
            {
                hull.RemoveAt(hull.Count - 1);
            }

            hull.Add(unique[i]);
        }

        int lower = hull.Count + 1;
        for (int i = unique.Count - 2; i >= 0; i--)
        {
            while (hull.Count >= lower && Cross(hull[hull.Count - 2], hull[hull.Count - 1], unique[i]) <= 0d)
            {
                hull.RemoveAt(hull.Count - 1);
            }

            hull.Add(unique[i]);
        }

        hull.RemoveAt(hull.Count - 1);
        return hull;
    }

    private static double Cross((double X, double Y) o, (double X, double Y) a, (double X, double Y) b)
    {
        return ((a.X - o.X) * (b.Y - o.Y)) - ((a.Y - o.Y) * (b.X - o.X));
    }
}
