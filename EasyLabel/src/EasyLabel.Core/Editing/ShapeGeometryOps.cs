using EasyLabel.Core.Model;

namespace EasyLabel.Core.Editing;

public enum SelectDirection
{
    Up,
    Down,
    Left,
    Right
}

/// <summary>UI-free geometry used by the edit commands: polygon union, shape centres, directional neighbour search.</summary>
public static class ShapeGeometryOps
{
    private const double Eps = 1e-7;

    /// <summary>True for the closed kinds that can be merged: rectangle, rotation, polygon.</summary>
    public static bool IsClosedKind(LabelShape shape)
    {
        if (shape == null || shape.Points == null) return false;
        switch (shape.ShapeType)
        {
            case "rectangle": return shape.Points.Count >= 2;
            case "oriented_rectangle": return shape.Points.Count == 4;
            case "polygon": return shape.Points.Count >= 3;
            default: return false;
        }
    }

    /// <summary>The outline of a closed shape as a ring of (x, y) pairs (a rectangle becomes its four corners).</summary>
    public static List<double[]> RingOf(LabelShape shape)
    {
        var ring = new List<double[]>();
        if (shape.ShapeType == "rectangle" && shape.Points.Count >= 2)
        {
            double l = Math.Min(shape.Points[0][0], shape.Points[1][0]), r = Math.Max(shape.Points[0][0], shape.Points[1][0]);
            double t = Math.Min(shape.Points[0][1], shape.Points[1][1]), b = Math.Max(shape.Points[0][1], shape.Points[1][1]);
            ring.Add(new[] { l, t });
            ring.Add(new[] { r, t });
            ring.Add(new[] { r, b });
            ring.Add(new[] { l, b });
            return ring;
        }

        foreach (double[] p in shape.Points) ring.Add(new[] { p[0], p[1] });
        return ring;
    }

    /// <summary>Visual centre of a shape: the circle centre, otherwise the middle of the bounding box.</summary>
    public static (double X, double Y) CentreOf(LabelShape shape)
    {
        if (shape.ShapeType == "circle" && shape.Points.Count >= 1) return (shape.Points[0][0], shape.Points[0][1]);
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (double[] p in shape.Points)
        {
            minX = Math.Min(minX, p[0]); maxX = Math.Max(maxX, p[0]);
            minY = Math.Min(minY, p[1]); maxY = Math.Max(maxY, p[1]);
        }
        return minX > maxX ? (0, 0) : ((minX + maxX) / 2.0, (minY + maxY) / 2.0);
    }

    /// <summary>
    /// Nearest eligible shape (by centre) in a direction from shape <paramref name="from"/>: first inside the 90 degree cone of the
    /// direction, otherwise anywhere in that half-plane. Returns -1 when there is none.
    /// </summary>
    public static int NearestInDirection(IReadOnlyList<LabelShape> shapes, int from, SelectDirection direction, Func<int, bool> eligible)
    {
        if (from < 0 || from >= shapes.Count) return -1;
        (double cx, double cy) = CentreOf(shapes[from]);
        int bestCone = -1, bestHalf = -1;
        double coneDist = double.MaxValue, halfDist = double.MaxValue;
        for (int i = 0; i < shapes.Count; i++)
        {
            if (i == from || !eligible(i)) continue;
            (double x, double y) = CentreOf(shapes[i]);
            double dx = x - cx, dy = y - cy;
            double along, across;
            switch (direction)
            {
                case SelectDirection.Up: along = -dy; across = dx; break;
                case SelectDirection.Down: along = dy; across = dx; break;
                case SelectDirection.Left: along = -dx; across = dy; break;
                default: along = dx; across = dy; break;
            }
            if (along <= Eps) continue;
            double dist = Math.Sqrt(dx * dx + dy * dy);
            if (Math.Abs(across) <= along + Eps && dist < coneDist) { coneDist = dist; bestCone = i; }
            if (dist < halfDist) { halfDist = dist; bestHalf = i; }
        }
        return bestCone >= 0 ? bestCone : bestHalf;
    }

    public static double SignedArea(IReadOnlyList<double[]> ring)
    {
        double s = 0;
        for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
            s += ring[j][0] * ring[i][1] - ring[i][0] * ring[j][1];
        return s / 2.0;
    }

    public static bool Contains(IReadOnlyList<double[]> ring, double x, double y)
    {
        bool inside = false;
        for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
        {
            if ((ring[i][1] > y) != (ring[j][1] > y) &&
                x < (ring[j][0] - ring[i][0]) * (y - ring[i][1]) / (ring[j][1] - ring[i][1]) + ring[i][0])
                inside = !inside;
        }
        return inside;
    }

    private static double DistanceToRing(IReadOnlyList<double[]> ring, double x, double y)
    {
        double best = double.MaxValue;
        for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
        {
            double ax = ring[j][0], ay = ring[j][1], bx = ring[i][0], by = ring[i][1];
            double abx = bx - ax, aby = by - ay;
            double len2 = abx * abx + aby * aby;
            double t = len2 <= 0 ? 0 : Math.Min(Math.Max(((x - ax) * abx + (y - ay) * aby) / len2, 0), 1);
            double qx = ax + abx * t, qy = ay + aby * t;
            best = Math.Min(best, Math.Sqrt((x - qx) * (x - qx) + (y - qy) * (y - qy)));
        }
        return best;
    }

    /// <summary>
    /// Union of two simple polygons as ONE simple outline. Returns false when the union is not a single hole-free polygon
    /// (disjoint shapes, a hole, or degenerate touching/collinear edges), so the caller can leave the command disabled.
    /// </summary>
    public static bool TryUnion(IReadOnlyList<double[]> first, IReadOnlyList<double[]> second, out List<double[]> union)
    {
        union = new List<double[]>();
        if (first.Count < 3 || second.Count < 3) return false;
        List<double[]> a = Orient(first), b = Orient(second);

        // Split every edge at its crossings with the other polygon (the crossing point object is shared by both edges).
        var splitA = new List<List<(double T, double[] P)>>();
        var splitB = new List<List<(double T, double[] P)>>();
        for (int i = 0; i < a.Count; i++) splitA.Add(new List<(double, double[])>());
        for (int j = 0; j < b.Count; j++) splitB.Add(new List<(double, double[])>());
        int crossings = 0;
        for (int i = 0; i < a.Count; i++)
        {
            double[] a0 = a[i], a1 = a[(i + 1) % a.Count];
            for (int j = 0; j < b.Count; j++)
            {
                double[] b0 = b[j], b1 = b[(j + 1) % b.Count];
                double rx = a1[0] - a0[0], ry = a1[1] - a0[1], sx = b1[0] - b0[0], sy = b1[1] - b0[1];
                double den = rx * sy - ry * sx;
                if (Math.Abs(den) < Eps)
                {
                    // Parallel: overlapping collinear edges are a degenerate union.
                    double cross = (b0[0] - a0[0]) * ry - (b0[1] - a0[1]) * rx;
                    if (Math.Abs(cross) < Eps && SegmentsOverlap(a0, a1, b0, b1)) return false;
                    continue;
                }
                double t = ((b0[0] - a0[0]) * sy - (b0[1] - a0[1]) * sx) / den;
                double u = ((b0[0] - a0[0]) * ry - (b0[1] - a0[1]) * rx) / den;
                if (t < -Eps || t > 1 + Eps || u < -Eps || u > 1 + Eps) continue;
                if (t < Eps || t > 1 - Eps || u < Eps || u > 1 - Eps) return false; // a vertex touches an edge
                double[] p = { a0[0] + t * rx, a0[1] + t * ry };
                splitA[i].Add((t, p));
                splitB[j].Add((u, p));
                crossings++;
            }
        }

        if (crossings == 0)
        {
            if (Contains(a, b[0][0], b[0][1])) { union = a; return true; }
            if (Contains(b, a[0][0], a[0][1])) { union = b; return true; }
            return false; // disjoint
        }

        var edges = new List<(double[] From, double[] To)>();
        if (!CollectOutside(a, splitA, b, edges) || !CollectOutside(b, splitB, a, edges)) return false;
        if (edges.Count < 3) return false;

        var byStart = new Dictionary<(long, long), int>();
        for (int i = 0; i < edges.Count; i++)
        {
            if (byStart.ContainsKey(Key(edges[i].From))) return false; // two edges leave one point: not a simple outline
            byStart[Key(edges[i].From)] = i;
        }

        var used = new bool[edges.Count];
        var ring = new List<double[]>();
        int current = 0;
        for (int step = 0; step < edges.Count; step++)
        {
            if (used[current]) return false;
            used[current] = true;
            ring.Add(edges[current].From);
            if (!byStart.TryGetValue(Key(edges[current].To), out int next)) return false;
            current = next;
        }
        if (current != 0 || ring.Count != edges.Count) return false; // more than one ring (a hole or separate pieces)

        union = Dedupe(ring);
        return union.Count >= 3;
    }

    private static List<double[]> Orient(IReadOnlyList<double[]> ring)
    {
        var copy = ring.Select(p => new[] { p[0], p[1] }).ToList();
        if (SignedArea(copy) < 0) copy.Reverse();
        return copy;
    }

    private static (long, long) Key(double[] p) => ((long)Math.Round(p[0] * 1e6), (long)Math.Round(p[1] * 1e6));

    private static bool SegmentsOverlap(double[] a0, double[] a1, double[] b0, double[] b1)
    {
        double dx = a1[0] - a0[0], dy = a1[1] - a0[1];
        double len2 = dx * dx + dy * dy;
        if (len2 <= 0) return false;
        double t0 = ((b0[0] - a0[0]) * dx + (b0[1] - a0[1]) * dy) / len2;
        double t1 = ((b1[0] - a0[0]) * dx + (b1[1] - a0[1]) * dy) / len2;
        double lo = Math.Min(t0, t1), hi = Math.Max(t0, t1);
        return Math.Min(hi, 1) - Math.Max(lo, 0) > Eps;
    }

    /// <summary>Appends the sub-edges of <paramref name="ring"/> that lie outside <paramref name="other"/>; false on a boundary-ambiguous piece.</summary>
    private static bool CollectOutside(List<double[]> ring, List<List<(double T, double[] P)>> splits, List<double[]> other,
        List<(double[] From, double[] To)> edges)
    {
        for (int i = 0; i < ring.Count; i++)
        {
            double[] start = ring[i], end = ring[(i + 1) % ring.Count];
            var points = new List<double[]> { start };
            foreach (var split in splits[i].OrderBy(s => s.T)) points.Add(split.P);
            points.Add(end);
            for (int k = 0; k + 1 < points.Count; k++)
            {
                double mx = (points[k][0] + points[k + 1][0]) / 2.0, my = (points[k][1] + points[k + 1][1]) / 2.0;
                if (DistanceToRing(other, mx, my) < 1e-6) return false;
                if (!Contains(other, mx, my)) edges.Add((points[k], points[k + 1]));
            }
        }
        return true;
    }

    private static List<double[]> Dedupe(List<double[]> ring)
    {
        var result = new List<double[]>();
        foreach (double[] p in ring)
        {
            if (result.Count > 0 && Key(result[result.Count - 1]) == Key(p)) continue;
            result.Add(new[] { p[0], p[1] });
        }
        if (result.Count > 1 && Key(result[0]) == Key(result[result.Count - 1])) result.RemoveAt(result.Count - 1);
        return result;
    }
}
