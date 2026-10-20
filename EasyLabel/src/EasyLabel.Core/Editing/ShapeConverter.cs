using EasyLabel.Core.Model;
using Newtonsoft.Json.Linq;

namespace EasyLabel.Core.Editing;

/// <summary>Outcome of <see cref="ShapeConverter.Convert"/>. <see cref="Shape"/> is null when the conversion is impossible.</summary>
public sealed class ShapeConversionResult
{
    public ShapeConversionResult(LabelShape? shape, bool lossy, string? warningKey)
    {
        Shape = shape;
        Lossy = lossy;
        WarningKey = warningKey;
    }

    /// <summary>A new shape (the source is never touched), or null when not convertible.</summary>
    public LabelShape? Shape { get; }

    public bool Lossy { get; }

    /// <summary>Resource key describing the loss; null when lossless (or when <see cref="Shape"/> is null and the key is <see cref="ShapeConverter.NotConvertibleKey"/>).</summary>
    public string? WarningKey { get; }
}

/// <summary>Result of planning a conversion of the whole selection (see CanvasEditSession.PlanConversion).</summary>
public sealed class ShapeConversionPlan
{
    public ShapeConversionPlan(string targetType) { TargetType = targetType; }

    public string TargetType { get; }

    /// <summary>Shape index to converted shape.</summary>
    public Dictionary<int, LabelShape> Replacements { get; } = new Dictionary<int, LabelShape>();

    /// <summary>Distinct warning keys of the lossy conversions; non-empty means the user must confirm.</summary>
    public List<string> WarningKeys { get; } = new List<string>();

    /// <summary>Selected shapes that could not be converted.</summary>
    public int Skipped { get; set; }
}

/// <summary>
/// Pure shape-type converter. Point layouts (image space, x right, y down):
/// rectangle = [p0, p1] (any two opposite corners); rotation = [TL, TR, BR, BL] with Direction = atan2 of TL to TR (radians);
/// circle = [centre, a point on the circle]; polygon/linestrip = vertices; line = 2 points; point = 1 point.
/// </summary>
public static class ShapeConverter
{
    public const string NotConvertibleKey = "shape.convert.unsupported";
    public const string LossyHolesKey = "shape.convert.lossy.holes";
    public const string LossyLineKey = "shape.convert.lossy.line";
    public const string LossyPointKey = "shape.convert.lossy.point";
    public const string LossyCircleKey = "shape.convert.lossy.circle";
    public const string LossyVerticesKey = "shape.convert.lossy.vertices";

    private const int CircleSegments = 32;
    private const double Eps = 1e-9;

    private static readonly string[] Targets = { "polygon", "rectangle", "oriented_rectangle", "circle", "point" };

    private static string? Normalize(string? type)
    {
        if (type == null) return null;
        return string.Equals(type, "rotation", StringComparison.Ordinal) ? "oriented_rectangle" : type;
    }

    /// <summary>Type-level check; a concrete shape can still fail in <see cref="Convert"/> when its points are degenerate.</summary>
    public static bool CanConvert(string? from, string? to)
    {
        string? f = Normalize(from), t = Normalize(to);
        if (f == null || t == null || f == t || Array.IndexOf(Targets, t) < 0) return false;
        switch (f)
        {
            case "polygon":
            case "rectangle":
            case "oriented_rectangle":
            case "circle":
            case "linestrip":
                return true;
            case "line":
                return t == "rectangle" || t == "circle" || t == "point";
            default: // point, unknown
                return false;
        }
    }

    public static ShapeConversionResult Convert(LabelShape source, string targetType)
    {
        if (source == null) throw new ArgumentNullException(nameof(source));
        string? from = Normalize(source.ShapeType), to = Normalize(targetType);
        if (!CanConvert(from, to) || source.Points == null || source.Points.Any(p => p == null || p.Length < 2))
            return Fail();

        List<double[]>? points = ConvertPoints(source, from!, to!, out double? direction);
        if (points == null) return Fail();

        bool holes = HasHoles(source) && to != "polygon";
        string? warning = null;
        if (holes) warning = LossyHolesKey;
        else if (from == "line" || from == "linestrip") warning = LossyLineKey;
        else if (to == "point") warning = LossyPointKey;
        else if (from == "circle" && to != "circle") warning = LossyCircleKey;
        else if (to == "circle" && (from == "rectangle" || from == "oriented_rectangle")) warning = LossyCircleKey;
        else if (from == "polygon" && source.Points.Count > 4 && to != "polygon") warning = LossyVerticesKey;

        var shape = new LabelShape
        {
            Label = source.Label,
            GroupId = source.GroupId,
            ShapeType = to,
            Points = points,
            Flags = new Dictionary<string, bool>(source.Flags ?? new Dictionary<string, bool>()),
            Description = source.Description,
            Direction = direction,
            EasyAi = CloneExtension(source.EasyAi, keepHoles: to == "polygon"),
            ExtensionData = source.ExtensionData == null
                ? null
                : source.ExtensionData.ToDictionary(kv => kv.Key, kv => kv.Value.DeepClone())
        };
        return new ShapeConversionResult(shape, warning != null, warning);
    }

    /// <summary>Minimum-area rectangle around the points as [TL, TR, BR, BL] (image pixels, same layout as an oriented_rectangle), or null when they have no area.</summary>
    public static List<double[]>? FitOrientedRectangle(IEnumerable<double[]> points)
    {
        return MinAreaRectangle(points.Select(p => new[] { p[0], p[1] }).ToList(), out _);
    }

    private static ShapeConversionResult Fail() => new ShapeConversionResult(null, false, NotConvertibleKey);

    private static bool HasHoles(LabelShape s) => s.EasyAi?["holes"] is JArray a && a.Count > 0;

    // Holes stay valid only on a polygon outline; the geometry hash always goes stale.
    private static JObject? CloneExtension(JObject? ext, bool keepHoles)
    {
        if (ext == null) return null;
        var copy = (JObject)ext.DeepClone();
        copy.Remove("geometryHash");
        if (!keepHoles) copy.Remove("holes");
        return copy.HasValues ? copy : null;
    }

    private static List<double[]>? ConvertPoints(LabelShape source, string from, string to, out double? direction)
    {
        direction = null;
        List<double[]> pts = source.Points;
        int need = from == "polygon" ? 3 : from == "oriented_rectangle" ? 4 : from == "rectangle" || from == "circle" || from == "line" || from == "linestrip" ? 2 : 1;
        if (pts.Count < need) return null;

        // Outline used by the geometric fits: a circle is its exact bounding square for box/rotation targets.
        List<double[]> ring;
        double radius = 0;
        (double cx, double cy) centre = (0, 0);
        if (from == "circle")
        {
            centre = (pts[0][0], pts[0][1]);
            radius = Math.Sqrt((pts[1][0] - pts[0][0]) * (pts[1][0] - pts[0][0]) + (pts[1][1] - pts[0][1]) * (pts[1][1] - pts[0][1]));
            if (radius < Eps) return null;
            if (to == "polygon")
            {
                var poly = new List<double[]>();
                for (int i = 0; i < CircleSegments; i++)
                {
                    double a = 2 * Math.PI * i / CircleSegments;
                    poly.Add(new[] { centre.cx + radius * Math.Cos(a), centre.cy + radius * Math.Sin(a) });
                }
                return poly;
            }
            ring = new List<double[]>
            {
                new[] { centre.cx - radius, centre.cy - radius }, new[] { centre.cx + radius, centre.cy - radius },
                new[] { centre.cx + radius, centre.cy + radius }, new[] { centre.cx - radius, centre.cy + radius }
            };
        }
        else if (from == "oriented_rectangle")
        {
            ring = pts.Take(4).Select(p => new[] { p[0], p[1] }).ToList();
        }
        else
        {
            ring = ShapeGeometryOps.RingOf(source);
        }

        switch (to)
        {
            case "polygon":
                if (ring.Count < 3) return null;
                return ring.Select(p => new[] { p[0], p[1] }).ToList();

            case "rectangle":
            {
                Bounds(ring, out double l, out double t, out double r, out double b);
                if (r - l < Eps || b - t < Eps) return null;
                return new List<double[]> { new[] { l, t }, new[] { r, b } };
            }

            case "oriented_rectangle":
                return MinAreaRectangle(ring, out direction);

            case "circle":
            {
                (double x, double y) c = from == "circle" ? centre : Centroid(source, ring);
                double rad = 0;
                foreach (double[] p in ring) rad = Math.Max(rad, Math.Sqrt((p[0] - c.x) * (p[0] - c.x) + (p[1] - c.y) * (p[1] - c.y)));
                if (rad < Eps) return null;
                return new List<double[]> { new[] { c.x, c.y }, new[] { c.x + rad, c.y } };
            }

            case "point":
            {
                (double x, double y) c = from == "circle" ? centre : Centroid(source, ring);
                return new List<double[]> { new[] { c.x, c.y } };
            }

            default:
                return null;
        }
    }

    private static void Bounds(List<double[]> ring, out double l, out double t, out double r, out double b)
    {
        l = t = double.MaxValue; r = b = double.MinValue;
        foreach (double[] p in ring)
        {
            l = Math.Min(l, p[0]); r = Math.Max(r, p[0]);
            t = Math.Min(t, p[1]); b = Math.Max(b, p[1]);
        }
    }

    private static (double X, double Y) Centroid(LabelShape source, List<double[]> ring)
    {
        string? type = source.ShapeType;
        bool closed = type == "polygon" || type == "rectangle" || type == "oriented_rectangle" || type == "rotation";
        if (closed && ring.Count >= 3)
        {
            double a = 0, sx = 0, sy = 0;
            for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
            {
                double cross = ring[j][0] * ring[i][1] - ring[i][0] * ring[j][1];
                a += cross;
                sx += (ring[j][0] + ring[i][0]) * cross;
                sy += (ring[j][1] + ring[i][1]) * cross;
            }
            if (Math.Abs(a) > Eps) return (sx / (3 * a), sy / (3 * a));
        }
        return (ring.Average(p => p[0]), ring.Average(p => p[1]));
    }

    /// <summary>Minimum-area enclosing rectangle (rotating calipers over the convex hull) as [TL, TR, BR, BL]; angle reduced to [-45, 45) degrees.</summary>
    private static List<double[]>? MinAreaRectangle(List<double[]> ring, out double? direction)
    {
        direction = null;
        List<double[]> hull = ConvexHull(ring);
        if (hull.Count < 3) return null;

        double bestArea = double.MaxValue, bestAngle = 0, bu0 = 0, bu1 = 0, bv0 = 0, bv1 = 0;
        for (int i = 0; i < hull.Count; i++)
        {
            double[] p = hull[i], q = hull[(i + 1) % hull.Count];
            double theta = Math.Atan2(q[1] - p[1], q[0] - p[0]);
            if (Math.Abs(q[0] - p[0]) < Eps && Math.Abs(q[1] - p[1]) < Eps) continue;
            // Reduce by quarter turns: the rectangle is identical, only the labelling of its sides changes.
            double quarter = Math.PI / 2;
            theta -= Math.Floor((theta + Math.PI / 4) / quarter) * quarter;
            double ux = Math.Cos(theta), uy = Math.Sin(theta), nx = -uy, ny = ux;
            double u0 = double.MaxValue, u1 = double.MinValue, v0 = double.MaxValue, v1 = double.MinValue;
            foreach (double[] h in hull)
            {
                double u = h[0] * ux + h[1] * uy, v = h[0] * nx + h[1] * ny;
                u0 = Math.Min(u0, u); u1 = Math.Max(u1, u);
                v0 = Math.Min(v0, v); v1 = Math.Max(v1, v);
            }
            double area = (u1 - u0) * (v1 - v0);
            bool better = area < bestArea * (1 - 1e-9)
                || (area <= bestArea * (1 + 1e-9) && Math.Abs(theta) < Math.Abs(bestAngle) - 1e-12);
            if (better) { bestArea = area; bestAngle = theta; bu0 = u0; bu1 = u1; bv0 = v0; bv1 = v1; }
        }
        if (bestArea == double.MaxValue || bu1 - bu0 < Eps || bv1 - bv0 < Eps) return null;

        double cux = Math.Cos(bestAngle), cuy = Math.Sin(bestAngle), cnx = -cuy, cny = cux;
        double[] Corner(double u, double v) => new[] { u * cux + v * cnx, u * cuy + v * cny };
        direction = Math.Atan2(cuy, cux);
        return new List<double[]> { Corner(bu0, bv0), Corner(bu1, bv0), Corner(bu1, bv1), Corner(bu0, bv1) };
    }

    // Andrew's monotone chain; collinear points are dropped.
    private static List<double[]> ConvexHull(List<double[]> input)
    {
        var pts = input.Select(p => new[] { p[0], p[1] }).OrderBy(p => p[0]).ThenBy(p => p[1]).ToList();
        if (pts.Count < 3) return pts;
        double Cross(double[] o, double[] a, double[] b) => (a[0] - o[0]) * (b[1] - o[1]) - (a[1] - o[1]) * (b[0] - o[0]);
        var hull = new List<double[]>();
        foreach (double[] p in pts)
        {
            while (hull.Count >= 2 && Cross(hull[hull.Count - 2], hull[hull.Count - 1], p) <= Eps) hull.RemoveAt(hull.Count - 1);
            hull.Add(p);
        }
        int lower = hull.Count + 1;
        for (int i = pts.Count - 2; i >= 0; i--)
        {
            double[] p = pts[i];
            while (hull.Count >= lower && Cross(hull[hull.Count - 2], hull[hull.Count - 1], p) <= Eps) hull.RemoveAt(hull.Count - 1);
            hull.Add(p);
        }
        hull.RemoveAt(hull.Count - 1);
        return hull;
    }
}
