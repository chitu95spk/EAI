p='Export/ExportService.cs'
s=open(p,encoding='utf-8',newline='').read()
crlf='\r\n' in s
s=s.replace('\r\n','\n')
for fmt in ['segment ? "YOLO segment" : "YOLO detect"']:
    pass
# insert clip after the size check in the three exporters
old_y='''        string stem = ctx.Alloc.Stem(visit.Item.Rel);
        string flat = stem + Path.GetExtension(visit.Item.Rel);
        string split ='''
assert old_y in s
s=s.replace(old_y,'''        kept = ClipShapesToImage(ctx, visit, kept, width, height);
        if (!HasContent(ctx, visit, kept)) return;
        string stem = ctx.Alloc.Stem(visit.Item.Rel);
        string flat = stem + Path.GetExtension(visit.Item.Rel);
        string split =''',1)
old_c='''        string flat = ctx.Alloc.Stem(visit.Item.Rel) + Path.GetExtension(visit.Item.Rel);
        var document = new LabelMeDocument { ImagePath = flat, ImageWidth = width, ImageHeight = height, Shapes = kept };
        sources.Add('''
assert old_c in s
s=s.replace(old_c,'''        kept = ClipShapesToImage(ctx, visit, kept, width, height);
        if (!HasContent(ctx, visit, kept)) return;
        string flat = ctx.Alloc.Stem(visit.Item.Rel) + Path.GetExtension(visit.Item.Rel);
        var document = new LabelMeDocument { ImagePath = flat, ImageWidth = width, ImageHeight = height, Shapes = kept };
        sources.Add(''',1)
old_v='''        string stem = ctx.Alloc.Stem(visit.Item.Rel);
        string flat = stem + Path.GetExtension(visit.Item.Rel);
        var document = new LabelMeDocument { ImagePath = flat, ImageWidth = width, ImageHeight = height, Shapes = kept };
        string[] lines = new PascalVocConverter()'''
assert old_v in s
s=s.replace(old_v,'''        kept = ClipShapesToImage(ctx, visit, kept, width, height);
        if (!HasContent(ctx, visit, kept)) return;
        string stem = ctx.Alloc.Stem(visit.Item.Rel);
        string flat = stem + Path.GetExtension(visit.Item.Rel);
        var document = new LabelMeDocument { ImagePath = flat, ImageWidth = width, ImageHeight = height, Shapes = kept };
        string[] lines = new PascalVocConverter()''',1)
helper='''    /// <summary>
    /// A shape cut by the image border (out-of-bounds labelling) is exported as its visible part: polygons and oriented boxes become the clipped
    /// polygon, a rectangle the clipped rectangle. A shape fully inside keeps its instance (output unchanged); one with no visible area is
    /// skipped and listed. Circles, points and lines are left as they are.
    /// </summary>
    internal static List<LabelShape> ClipShapesToImage(ExportContext ctx, Visit visit, List<LabelShape> shapes, int width, int height)
    {
        var result = new List<LabelShape>(shapes.Count);
        foreach (LabelShape shape in shapes)
        {
            string type = shape.ShapeType ?? string.Empty;
            List<double[]> pts = shape.Points ?? new List<double[]>();
            bool box = type == "rectangle" || type == "oriented_rectangle" || type == "polygon";
            if (!box || pts.All(p => p[0] >= 0 && p[0] <= width && p[1] >= 0 && p[1] <= height))
            {
                result.Add(shape);
                continue;
            }

            var ring = new List<System.Drawing.PointF>();
            if (type == "rectangle" && pts.Count == 2)
            {
                double x1 = Math.Min(pts[0][0], pts[1][0]), x2 = Math.Max(pts[0][0], pts[1][0]), y1 = Math.Min(pts[0][1], pts[1][1]), y2 = Math.Max(pts[0][1], pts[1][1]);
                ring.AddRange(new[] { new System.Drawing.PointF((float)x1, (float)y1), new System.Drawing.PointF((float)x2, (float)y1), new System.Drawing.PointF((float)x2, (float)y2), new System.Drawing.PointF((float)x1, (float)y2) });
            }
            else
            {
                ring.AddRange(pts.Select(p => new System.Drawing.PointF((float)p[0], (float)p[1])));
            }

            List<SegAnnotation> clipped = SegLabelRaster.ClipToImage(new[] { new SegAnnotation { Points = ring.ToArray() } }, width, height);
            if (clipped.Count == 0 || clipped[0].Points.Length < 3)
            {
                SkipShape(ctx, visit.Item.Rel, DescribeShape(visit, shape), "the shape lies outside the image (no visible area)");
                continue;
            }

            System.Drawing.PointF[] c = clipped[0].Points;
            var copy = new LabelShape { Label = shape.Label, GroupId = shape.GroupId, Flags = shape.Flags, Description = shape.Description, Direction = shape.Direction };
            if (type == "rectangle")
            {
                copy.ShapeType = "rectangle";
                copy.Points = new List<double[]> { new double[] { c.Min(p => p.X), c.Min(p => p.Y) }, new double[] { c.Max(p => p.X), c.Max(p => p.Y) } };
            }
            else
            {
                copy.ShapeType = "polygon";
                copy.Points = c.Select(p => new double[] { p.X, p.Y }).ToList();
            }

            result.Add(copy);
        }

        return result;
    }

    private static List<LabelShape> FilterShapes('''
s=s.replace('    private static List<LabelShape> FilterShapes(',helper,1)
if crlf: s=s.replace('\n','\r\n')
open(p,'w',encoding='utf-8',newline='').write(s)

p='Data/SegLabelRaster.cs'
s=open(p,encoding='utf-8',newline='').read()
s=s.replace("if (outer.Length < 3) continue;","if (outer.Length < 3 || Math.Abs(SignedArea(outer)) < MinVisibleArea) continue; // a zero-area sliver on the border paints nothing",1)
s=s.replace("    private static bool IsInside(","""    /// <summary>A clipped region with less area than this (square pixels) is a sliver along the border and is dropped.</summary>
    public const double MinVisibleArea = 0.01;

    private static double SignedArea(PointF[] ring)
    {
        double sum = 0;
        for (int i = 0; i < ring.Length; i++)
        {
            PointF a = ring[i], b = ring[(i + 1) % ring.Length];
            sum += (double)a.X * b.Y - (double)b.X * a.Y;
        }

        return sum / 2.0;
    }

    private static bool IsInside(""",1)
open(p,'w',encoding='utf-8',newline='').write(s)

p='../EasyAI.Wpf/Infrastructure/AppSettings.cs'
s=open(p,encoding='utf-8',newline='').read()
import re
s=re.sub(r'/// <summary>Vertices \(and the agent.s boxes\).*?</summary>','/// <summary>Vertices and the agent\'s boxes may leave the image, so an object cut by the border keeps its true box in the LabelMe JSON. Training masks cover only the visible part, and YOLO/VOC/COCO/EasyVision exports write the visible (clipped) shape; instances whose centre is outside the image are not trained.</summary>',s,count=1)
open(p,'w',encoding='utf-8',newline='').write(s)
