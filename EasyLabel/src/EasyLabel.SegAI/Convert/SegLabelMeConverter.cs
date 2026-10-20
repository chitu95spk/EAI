using System.Drawing;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using EasyLabel.Core.Model;
using EasyLabel.SegAI.Data;
using EasyLabel.SegAI.Project;
using Newtonsoft.Json.Linq;

namespace EasyLabel.SegAI.Convert;

/// <summary>
/// Converts Segment AI regions to LabelMe shapes and back. Shape types: "polygon" (Kind Polygon), "rectangle" (Kind Rect, two
/// corner points), "oriented_rectangle" (Kind Rot, four corner points plus Direction = atan2 of the first edge, as in the Core YOLO OBB import).
/// Holes are not representable in LabelMe; they live in the sidecar and survive only while the shapes hash still matches.
/// </summary>
public static class SegLabelMeConverter
{
    public const string PolygonType = "polygon";
    public const string RectangleType = "rectangle";
    public const string RotationType = "oriented_rectangle";

    public static List<LabelShape> ToLabelMe(IReadOnlyList<SegAnnotation> regions, SegClassMap map)
    {
        ArgumentNullException.ThrowIfNull(regions);
        ArgumentNullException.ThrowIfNull(map);
        var shapes = new List<LabelShape>();
        for (int i = 0; i < regions.Count; i++)
        {
            SegAnnotation region = regions[i];
            if (region == null || region.Points == null || region.Points.Length < 2) continue;
            var shape = new LabelShape { Label = map.NameOf(region.Label), GroupId = i };
            if (string.Equals(region.Kind, "Rect", StringComparison.OrdinalIgnoreCase))
            {
                float minX = region.Points.Min(p => p.X), maxX = region.Points.Max(p => p.X);
                float minY = region.Points.Min(p => p.Y), maxY = region.Points.Max(p => p.Y);
                shape.ShapeType = RectangleType;
                shape.Points.Add(new double[] { minX, minY });
                shape.Points.Add(new double[] { maxX, maxY });
            }
            else if (string.Equals(region.Kind, "Rot", StringComparison.OrdinalIgnoreCase) && region.Points.Length >= 4)
            {
                shape.ShapeType = RotationType;
                foreach (PointF p in region.Points.Take(4)) shape.Points.Add(new double[] { p.X, p.Y });
                shape.Direction = Math.Atan2(region.Points[1].Y - region.Points[0].Y, region.Points[1].X - region.Points[0].X);
            }
            else
            {
                if (region.Points.Length < 3) continue;
                shape.ShapeType = PolygonType;
                foreach (PointF p in region.Points) shape.Points.Add(new double[] { p.X, p.Y });
            }
            WriteHoles(shape, region.Holes);
            shapes.Add(shape);
        }
        return shapes;
    }

    /// <summary>
    /// Position projects: oriented boxes ("oriented_rectangle", corners TL, TR, BR, BL) and circles ("circle", centre then edge point) as
    /// instances of the per-instance head. Other shapes, unknown classes and degenerate sizes are skipped. Detect projects call this only in
    /// box mode (<see cref="SegBoxModeRule"/>), with <paramref name="includeRectangles"/> = true: an axis-aligned "rectangle" (two corners)
    /// becomes a box with Theta 0 and its bounds as centre and size. Position never passes true, so its instances do not change.
    /// </summary>
    public static List<SegInstance> ToInstances(IReadOnlyList<LabelShape> shapes, SegClassMap map, bool includeRectangles = false, int imageWidth = 0, int imageHeight = 0)
    {
        ArgumentNullException.ThrowIfNull(shapes);
        ArgumentNullException.ThrowIfNull(map);
        var instances = new List<SegInstance>();
        foreach (LabelShape shape in shapes)
        {
            byte? label = map.LabelOf(shape?.Label);
            if (shape == null || label == null) continue;
            List<double[]> pts = shape.Points ?? new List<double[]>();
            if (pts.Any(p => p == null || p.Length < 2 || !double.IsFinite(p[0]) || !double.IsFinite(p[1]))) continue;
            int classIndex = label.Value - 1;
            string type = shape.ShapeType ?? "";
            if (type == "circle" && pts.Count >= 2)
            {
                double radius = Distance(pts[0], pts[1]);
                if (radius <= 0) continue;
                AddIfFinite(instances, new SegInstance
                {
                    Kind = SegInstance.KindCircle, Cx = (float)pts[0][0], Cy = (float)pts[0][1],
                    W = (float)(2 * radius), H = (float)(2 * radius), Theta = 0, ClassIndex = classIndex,
                });
            }
            else if ((type == RotationType || type == "rotation") && pts.Count >= 4)
            {
                double w = Distance(pts[0], pts[1]), h = Distance(pts[0], pts[3]);
                if (w <= 0 || h <= 0) continue;
                double cx = (pts[0][0] + pts[1][0] + pts[2][0] + pts[3][0]) / 4;
                double cy = (pts[0][1] + pts[1][1] + pts[2][1] + pts[3][1]) / 4;
                double theta = Math.Atan2(pts[1][1] - pts[0][1], pts[1][0] - pts[0][0]);
                AddIfFinite(instances, new SegInstance
                {
                    Kind = SegInstance.KindBox, Cx = (float)cx, Cy = (float)cy, W = (float)w, H = (float)h,
                    Theta = (float)theta, ClassIndex = classIndex,
                });
            }
            else if (includeRectangles && type == RectangleType && pts.Count == 2)
            {
                double x0 = Math.Min(pts[0][0], pts[1][0]), x1 = Math.Max(pts[0][0], pts[1][0]);
                double y0 = Math.Min(pts[0][1], pts[1][1]), y1 = Math.Max(pts[0][1], pts[1][1]);
                if (x1 - x0 <= 0 || y1 - y0 <= 0) continue;
                AddIfFinite(instances, new SegInstance
                {
                    Kind = SegInstance.KindBox, Cx = (float)((x0 + x1) / 2), Cy = (float)((y0 + y1) / 2),
                    W = (float)(x1 - x0), H = (float)(y1 - y0), Theta = 0, ClassIndex = classIndex,
                });
            }
        }

        // Out-of-bounds labels: the native trainer drops a centre outside its grid without a trace (SegInstanceTargets.cpp:210-212) and a centre in
        // the letterbox padding would train a peak on padding, so only instances whose centre is inside the image reach it (partly-outside ones stay).
        if (imageWidth > 0 && imageHeight > 0)
            instances.RemoveAll(i => i.Cx < 0 || i.Cy < 0 || i.Cx > imageWidth || i.Cy > imageHeight);

        return instances;
    }

    /// <summary>Position projects: circles as <see cref="SegAnnotation.CircleKind"/> regions, so the training mask paints the disc. Detect never calls this.</summary>
    public static List<SegAnnotation> ToCircleRegions(IReadOnlyList<LabelShape> shapes, SegClassMap map)
    {
        var regions = new List<SegAnnotation>();
        foreach (SegInstance instance in ToInstances(shapes, map))
        {
            if (instance.Kind != SegInstance.KindCircle) continue;
            regions.Add(new SegAnnotation
            {
                Kind = SegAnnotation.CircleKind,
                Label = (byte)(instance.ClassIndex + 1),
                Points = new[] { new PointF(instance.Cx, instance.Cy), new PointF(instance.Cx + instance.W / 2, instance.Cy) },
            });
        }

        return regions;
    }

    /// <summary>Adds the instance only when every value is finite (a huge coordinate can overflow to infinity when narrowed to float).</summary>
    private static void AddIfFinite(List<SegInstance> instances, SegInstance instance)
    {
        if (float.IsFinite(instance.Cx) && float.IsFinite(instance.Cy) && float.IsFinite(instance.W)
            && float.IsFinite(instance.H) && float.IsFinite(instance.Theta))
        {
            instances.Add(instance);
        }
    }

    private static double Distance(double[] a, double[] b)
    {
        double dx = b[0] - a[0], dy = b[1] - a[1];
        return Math.Sqrt(dx * dx + dy * dy);
    }

    public static List<SegAnnotation> FromLabelMe(IReadOnlyList<LabelShape> shapes, SegSidecar? sidecar, SegClassMap map, out bool stale)
    {
        return FromLabelMe(shapes, sidecar, map, out stale, out _);
    }

    /// <param name="skipped">Shapes that were not converted (unknown class, unsupported type, too few points); 0 when the sidecar was used.</param>
    public static List<SegAnnotation> FromLabelMe(IReadOnlyList<LabelShape> shapes, SegSidecar? sidecar, SegClassMap map, out bool stale, out int skipped)
    {
        ArgumentNullException.ThrowIfNull(shapes);
        ArgumentNullException.ThrowIfNull(map);
        skipped = 0;
        if (sidecar != null && sidecar.Regions != null && string.Equals(sidecar.ShapesHash, Hash(shapes), StringComparison.Ordinal))
        {
            stale = false;
            return sidecar.Regions.Select(r => r.Clone()).ToList();
        }

        stale = true;
        var regions = new List<SegAnnotation>();
        foreach (LabelShape shape in shapes)
        {
            byte? label = map.LabelOf(shape?.Label);
            if (shape == null || label == null) { skipped++; continue; }
            List<double[]> pts = shape.Points ?? new List<double[]>();
            if (pts.Any(p => p == null || p.Length < 2)) { skipped++; continue; }
            string type = shape.ShapeType ?? "";
            SegAnnotation? region = null;
            if (type == PolygonType && pts.Count >= 3)
            {
                region = new SegAnnotation { Kind = "Polygon", Points = pts.Select(ToPointF).ToArray() };
            }
            else if (type == RectangleType && pts.Count == 2)
            {
                float x0 = (float)Math.Min(pts[0][0], pts[1][0]), x1 = (float)Math.Max(pts[0][0], pts[1][0]);
                float y0 = (float)Math.Min(pts[0][1], pts[1][1]), y1 = (float)Math.Max(pts[0][1], pts[1][1]);
                region = new SegAnnotation
                {
                    Kind = "Rect",
                    Points = new[] { new PointF(x0, y0), new PointF(x1, y0), new PointF(x1, y1), new PointF(x0, y1) }
                };
            }
            else if ((type == RotationType || type == "rotation") && pts.Count >= 4)
            {
                region = new SegAnnotation { Kind = "Rot", Points = pts.Take(4).Select(ToPointF).ToArray() };
            }
            if (region == null) { skipped++; continue; }
            region.Label = label.Value;
            region.Holes = ReadHoles(shape);
            regions.Add(region);
        }
        return regions;
    }

    private static PointF ToPointF(double[] p) { return new PointF((float)p[0], (float)p[1]); }

    /// <summary>
    /// Stores holes in the shape's optional <c>"easyai"</c> object as <c>{"holes": [[[x,y],...]], "geometryHash": "..."}</c>; shapes without holes
    /// carry no such key (an existing one is removed). The hash ties the holes to the shape's type and points, so a polygon moved in another
    /// tool never keeps holes that no longer fit.
    /// </summary>
    public static void WriteHoles(LabelShape shape, PointF[][]? holes)
    {
        ArgumentNullException.ThrowIfNull(shape);
        if (holes == null || holes.Length == 0)
        {
            if (shape.EasyAi != null)
            {
                shape.EasyAi.Remove("holes");
                shape.EasyAi.Remove("geometryHash");
                if (!shape.EasyAi.HasValues) shape.EasyAi = null;
            }
            return;
        }

        var rings = new JArray();
        foreach (PointF[] ring in holes)
        {
            var points = new JArray();
            foreach (PointF p in ring) points.Add(new JArray(p.X, p.Y));
            rings.Add(points);
        }

        shape.EasyAi ??= new JObject();
        shape.EasyAi["holes"] = rings;
        shape.EasyAi["geometryHash"] = GeometryHash(shape);
    }

    /// <summary>Holes stored by <see cref="WriteHoles"/>; empty when the key is missing, malformed, or its geometry hash no longer matches the shape.</summary>
    public static PointF[][] ReadHoles(LabelShape shape)
    {
        ArgumentNullException.ThrowIfNull(shape);
        try
        {
            JObject? ext = shape.EasyAi;
            if (ext == null || !(ext["holes"] is JArray rings) || rings.Count == 0) return new PointF[0][];
            if (!string.Equals(ext["geometryHash"]?.Value<string>(), GeometryHash(shape), StringComparison.Ordinal)) return new PointF[0][];
            var result = new List<PointF[]>();
            foreach (JToken ringToken in rings)
            {
                if (!(ringToken is JArray ring) || ring.Count < 3) return new PointF[0][];
                var points = new PointF[ring.Count];
                for (int i = 0; i < ring.Count; i++)
                {
                    if (!(ring[i] is JArray pair) || pair.Count != 2) return new PointF[0][];
                    points[i] = new PointF(pair[0].Value<float>(), pair[1].Value<float>());
                }
                result.Add(points);
            }
            return result.ToArray();
        }
        catch (Exception ex) when (ex is FormatException || ex is InvalidCastException || ex is OverflowException)
        {
            return new PointF[0][];
        }
    }

    private static string GeometryHash(LabelShape shape)
    {
        return Hash(new[] { new LabelShape { ShapeType = shape.ShapeType, Points = shape.Points } });
    }

    /// <summary>SHA256 hex of a canonical string of label, shape_type and points (invariant culture, "R" doubles). group_id, flags and description are excluded.</summary>
    public static string Hash(IReadOnlyList<LabelShape> shapes)
    {
        ArgumentNullException.ThrowIfNull(shapes);
        var sb = new StringBuilder();
        foreach (LabelShape shape in shapes)
        {
            sb.Append("S|").Append(shape?.Label ?? "").Append('|').Append(shape?.ShapeType ?? "").Append('|');
            if (shape?.Points != null)
                foreach (double[] p in shape.Points)
                {
                    if (p == null) { sb.Append("null;"); continue; }
                    sb.Append(string.Join(",", p.Select(v => v.ToString("R", CultureInfo.InvariantCulture)))).Append(';');
                }
            sb.Append('\n');
        }
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return System.Convert.ToHexString(digest).ToLowerInvariant();
    }
}
