using System.Drawing;
using EasyLabel.Core.Model;

namespace EasyLabel.Core.Geometry;

public enum ShapeHitKind
{
    None,
    /// <summary>Box corner; index 0..3 = TL, TR, BR, BL.</summary>
    Corner,
    /// <summary>Rotation handle of a "oriented_rectangle" shape.</summary>
    Rotate,
    /// <summary>Polygon vertex; index into Points.</summary>
    Vertex,
    /// <summary>Inside the shape (even-odd).</summary>
    Move
}

public readonly struct ShapeHit
{
    public ShapeHit(ShapeHitKind kind, int index)
    {
        Kind = kind;
        Index = index;
    }

    public ShapeHitKind Kind { get; }

    public int Index { get; }

    public static ShapeHit None => new ShapeHit(ShapeHitKind.None, -1);
}

/// <summary>
/// Hit order: corner, rotation handle, polygon vertex, even-odd containment (Move).
/// Shapes are in image coordinates; handle sizes and tolerance are in screen units and
/// converted with <c>scale</c> (screen px per image px).
/// </summary>
public static class ShapeHitTester
{
    public static ShapeHit HitTest(LabelShape shape, PointF imagePoint, float scale, float handleScreenSize = EditHandleMetrics.DefaultHandleScreen)
    {
        if (shape == null || scale <= 0f || float.IsNaN(scale) || float.IsInfinity(scale))
            return ShapeHit.None;

        PointF[] pts = ToPoints(shape);
        string? type = shape.ShapeType;
        if (string.Equals(type, "rectangle", StringComparison.Ordinal) && pts.Length >= 2)
        {
            float l = Math.Min(pts[0].X, pts[1].X), r = Math.Max(pts[0].X, pts[1].X);
            float t = Math.Min(pts[0].Y, pts[1].Y), b = Math.Max(pts[0].Y, pts[1].Y);
            var corners = new[] { new PointF(l, t), new PointF(r, t), new PointF(r, b), new PointF(l, b) };
            return HitBox(corners, new PointF(1f, 0f), new PointF(0f, 1f), r - l, b - t, false, imagePoint, scale, handleScreenSize);
        }

        if (string.Equals(type, "oriented_rectangle", StringComparison.Ordinal) && RotatedBoxGeometry.TryCreate(pts.Length == 4 ? pts : null, out RotatedBoxGeometry box))
            return HitBox(box.ToPoints(), box.XAxis, box.YAxis, box.Width, box.Height, true, imagePoint, scale, handleScreenSize);

        if (string.Equals(type, "polygon", StringComparison.Ordinal) && pts.Length >= 3)
            return HitPolygon(pts, imagePoint, scale, handleScreenSize);

        if (string.Equals(type, "circle", StringComparison.Ordinal) && pts.Length >= 2)
            return HitCircle(pts, imagePoint, scale, handleScreenSize);

        if (string.Equals(type, "point", StringComparison.Ordinal) && pts.Length >= 1)
        {
            double r = Math.Max(PointHitScreen, EditHandleMetrics.HitScreenSize(handleScreenSize) * .5f) / scale;
            return Distance(pts[0], imagePoint) <= r ? new ShapeHit(ShapeHitKind.Move, 0) : ShapeHit.None;
        }

        if (string.Equals(type, "line", StringComparison.Ordinal) && pts.Length >= 2)
            return HitPolyline(new[] { pts[0], pts[1] }, imagePoint, scale, handleScreenSize);

        if (string.Equals(type, "linestrip", StringComparison.Ordinal) && pts.Length >= 2)
            return HitPolyline(pts, imagePoint, scale, handleScreenSize);

        return ShapeHit.None;
    }

    /// <summary>Screen-unit tolerance around a line / ring / point.</summary>
    public const float LineHitScreen = 6f;

    /// <summary>Screen-unit radius of the hit area of a "point" shape.</summary>
    public const float PointHitScreen = 8f;

    /// <summary>Distance in image units from <paramref name="p"/> to the segment a-b.</summary>
    public static double DistanceToSegment(PointF a, PointF b, PointF p)
    {
        double abx = b.X - a.X, aby = b.Y - a.Y;
        double len2 = abx * abx + aby * aby;
        double t = len2 <= 0 ? 0 : ((p.X - a.X) * abx + (p.Y - a.Y) * aby) / len2;
        t = Math.Min(Math.Max(t, 0), 1);
        double qx = a.X + abx * t, qy = a.Y + aby * t;
        return Math.Sqrt((p.X - qx) * (p.X - qx) + (p.Y - qy) * (p.Y - qy));
    }

    private static double Distance(PointF a, PointF b)
    {
        double dx = a.X - b.X, dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static int NearestHandle(PointF[] pts, PointF p, float scale, float handleScreenSize)
    {
        float half = EditHandleMetrics.HitScreenSize(handleScreenSize) * .5f;
        int best = -1;
        float bestDist = float.MaxValue;
        for (int i = 0; i < pts.Length; i++)
        {
            float ax = Math.Abs(p.X - pts[i].X) * scale;
            float ay = Math.Abs(p.Y - pts[i].Y) * scale;
            if (ax <= half && ay <= half)
            {
                float d = ax * ax + ay * ay;
                if (d < bestDist) { bestDist = d; best = i; }
            }
        }
        return best;
    }

    /// <summary>Circle: handles on the centre (0) and the edge point (1); the disc itself moves the shape.</summary>
    private static ShapeHit HitCircle(PointF[] pts, PointF p, float scale, float handleScreenSize)
    {
        int handle = NearestHandle(new[] { pts[0], pts[1] }, p, scale, handleScreenSize);
        if (handle >= 0) return new ShapeHit(ShapeHitKind.Vertex, handle);
        double radius = Distance(pts[0], pts[1]);
        double tol = LineHitScreen / scale;
        return Distance(pts[0], p) <= radius + tol ? new ShapeHit(ShapeHitKind.Move, 0) : ShapeHit.None;
    }

    /// <summary>Line / linestrip: handles on the vertices; the segments (within a few screen pixels) move the shape.</summary>
    private static ShapeHit HitPolyline(PointF[] pts, PointF p, float scale, float handleScreenSize)
    {
        int handle = NearestHandle(pts, p, scale, handleScreenSize);
        if (handle >= 0) return new ShapeHit(ShapeHitKind.Vertex, handle);
        double tol = LineHitScreen / scale;
        for (int i = 0; i + 1 < pts.Length; i++)
        {
            if (DistanceToSegment(pts[i], pts[i + 1], p) <= tol) return new ShapeHit(ShapeHitKind.Move, 0);
        }
        return ShapeHit.None;
    }

    /// <summary>Topmost shape wins: scans from the end of the list. Returns shape index or -1.</summary>
    public static int HitTestTopmost(IReadOnlyList<LabelShape> shapes, PointF imagePoint, float scale, float handleScreenSize, out ShapeHit hit)
    {
        for (int i = shapes.Count - 1; i >= 0; i--)
        {
            ShapeHit h = HitTest(shapes[i], imagePoint, scale, handleScreenSize);
            if (h.Kind != ShapeHitKind.None)
            {
                hit = h;
                return i;
            }
        }
        hit = ShapeHit.None;
        return -1;
    }

    /// <summary>Image-space centre of the rotation handle for a box (stem from the middle of the top edge, away from the box).</summary>
    public static PointF RotationHandlePosition(RotatedBoxGeometry box, float scale, float handleScreenSize)
        => RotationHandlePosition(box.ToPoints(), box.YAxis, scale, handleScreenSize);

    private static PointF RotationHandlePosition(PointF[] corners, PointF yAxis, float scale, float handleScreenSize)
    {
        float gap = EditHandleMetrics.RotationGapScreen(handleScreenSize) / scale;
        PointF mid = new PointF((corners[0].X + corners[1].X) * .5f, (corners[0].Y + corners[1].Y) * .5f);
        return new PointF(mid.X - yAxis.X * gap, mid.Y - yAxis.Y * gap);
    }

    private static ShapeHit HitBox(PointF[] corners, PointF xAxis, PointF yAxis, float width, float height, bool rotatable,
        PointF p, float scale, float handleScreenSize)
    {
        float hitSize = EditHandleMetrics.HitScreenSize(handleScreenSize);
        float cornerSize = EditHandleMetrics.ClampToShapeScreen(hitSize, Math.Min(Math.Abs(width), Math.Abs(height)), scale);
        float cornerHalf = cornerSize * .5f;

        int bestCorner = -1;
        float bestDist = float.MaxValue;
        for (int i = 0; i < 4; i++)
        {
            float dx = p.X - corners[i].X, dy = p.Y - corners[i].Y;
            float ax = Math.Abs(dx * xAxis.X + dy * xAxis.Y) * scale;
            float ay = Math.Abs(dx * yAxis.X + dy * yAxis.Y) * scale;
            if (ax <= cornerHalf && ay <= cornerHalf)
            {
                float d = ax * ax + ay * ay;
                if (d < bestDist) { bestDist = d; bestCorner = i; }
            }
        }
        if (bestCorner >= 0)
            return new ShapeHit(ShapeHitKind.Corner, bestCorner);

        if (rotatable)
        {
            PointF h = RotationHandlePosition(corners, yAxis, scale, handleScreenSize);
            float dx = p.X - h.X, dy = p.Y - h.Y;
            float half = hitSize * .5f;
            if (Math.Abs(dx * xAxis.X + dy * xAxis.Y) * scale <= half && Math.Abs(dx * yAxis.X + dy * yAxis.Y) * scale <= half)
                return new ShapeHit(ShapeHitKind.Rotate, 0);
        }

        return ContainsEvenOdd(corners, p) ? new ShapeHit(ShapeHitKind.Move, 0) : ShapeHit.None;
    }

    private static ShapeHit HitPolygon(PointF[] pts, PointF p, float scale, float handleScreenSize)
    {
        float minX = pts.Min(q => q.X), maxX = pts.Max(q => q.X);
        float minY = pts.Min(q => q.Y), maxY = pts.Max(q => q.Y);
        float size = EditHandleMetrics.ClampToShapeScreen(EditHandleMetrics.HitScreenSize(handleScreenSize), Math.Min(maxX - minX, maxY - minY), scale);
        float half = size * .5f;

        int best = -1;
        float bestDist = float.MaxValue;
        for (int i = 0; i < pts.Length; i++)
        {
            float ax = Math.Abs(p.X - pts[i].X) * scale;
            float ay = Math.Abs(p.Y - pts[i].Y) * scale;
            if (ax <= half && ay <= half)
            {
                float d = ax * ax + ay * ay;
                if (d < bestDist) { bestDist = d; best = i; }
            }
        }
        if (best >= 0)
            return new ShapeHit(ShapeHitKind.Vertex, best);

        return ContainsEvenOdd(pts, p) ? new ShapeHit(ShapeHitKind.Move, 0) : ShapeHit.None;
    }

    public static bool ContainsEvenOdd(PointF[] poly, PointF p)
    {
        bool inside = false;
        for (int i = 0, j = poly.Length - 1; i < poly.Length; j = i++)
        {
            if ((poly[i].Y > p.Y) != (poly[j].Y > p.Y) &&
                p.X < (poly[j].X - poly[i].X) * (p.Y - poly[i].Y) / (poly[j].Y - poly[i].Y) + poly[i].X)
                inside = !inside;
        }
        return inside;
    }

    private static PointF[] ToPoints(LabelShape shape)
    {
        var list = new List<PointF>(shape.Points.Count);
        foreach (double[] pt in shape.Points)
        {
            if (pt != null && pt.Length >= 2)
                list.Add(new PointF((float)pt[0], (float)pt[1]));
        }
        return list.ToArray();
    }
}
