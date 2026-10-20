using EasyLabel.Core.Commands;
using EasyLabel.Core.Model;
using EasyLabel.SegAI.Data;
using OpenCvSharp;
using PointF = System.Drawing.PointF;

namespace EasyLabel.SegAI.Tests;

/// <summary>EAI-UI commit C: circle / point / line / linestrip shapes survive the Segment AI save path untouched.</summary>
public sealed class SegAiShapeKindsTests
{
    private static string NewFolder(string name)
    {
        string dir = Path.Combine(SegAiFixtures.ScratchFolder("shapekinds"), name + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static LabelShape Shape(string type, string label, params double[] xy)
    {
        var s = new LabelShape { Label = label, ShapeType = type };
        for (int i = 0; i < xy.Length; i += 2) s.Points.Add(new[] { xy[i], xy[i + 1] });
        return s;
    }

    [Fact]
    public void Save_KeepsCircleLineAndPointKindsInDocumentButNotInRegions()
    {
        string root = NewFolder("roundtrip");
        using (var image = new Mat(120, 160, MatType.CV_8UC3, new Scalar(30, 60, 90)))
        {
            Assert.True(Cv2.ImWrite(Path.Combine(root, "a.png"), image));
        }

        using SegAiSession session = SegAiSession.Create(root);
        const string rel = "a.png";
        session.SetAnnotations(rel, new[]
        {
            new SegAnnotation
            {
                Label = 1,
                Kind = "Rect",
                Points = new[] { new PointF(20, 20), new PointF(100, 20), new PointF(100, 80), new PointF(20, 80) },
            },
        });

        SegAiEditDocument edit = session.LoadForEdit(rel);
        string segLabel = edit.Document.Shapes[0].Label!;
        var editor = new DocumentEditor(edit.Document, edit.Sidecar);
        var added = new List<LabelShape>
        {
            Shape("circle", segLabel, 50, 50, 60, 50),
            Shape("point", segLabel, 70, 70),
            Shape("line", "other", 10, 100, 80, 110),
            Shape("linestrip", segLabel, 5, 5, 30, 10, 40, 40),
        };
        editor.Add(added, added.Select(_ => new ShapeProvenance()).ToList(), "kinds");

        session.SaveEditedShapes(rel, edit.Document, edit.Sidecar, shape => edit.Holes.TryGet(shape, out PointF[][] holes) ? holes : null);

        // Only the rectangle is a Segment AI region; the other kinds never enter the regions.
        Assert.Single(session.GetAnnotations(rel));
        Assert.False(session.Samples.Single().Stale);

        SegAiEditDocument again = session.LoadForEdit(rel);
        Assert.False(again.Stale);
        Assert.Equal(new[] { "rectangle", "circle", "point", "line", "linestrip" }, again.Document.Shapes.Select(s => s.ShapeType).ToArray());
        Assert.Equal(new[] { 2, 2, 1, 2, 3 }, again.Document.Shapes.Select(s => s.Points.Count).ToArray());
        Assert.Equal(new[] { 50.0, 50.0 }, again.Document.Shapes[1].Points[0]);
        Assert.Equal(new[] { 60.0, 50.0 }, again.Document.Shapes[1].Points[1]);
        Assert.Equal("other", again.Document.Shapes[3].Label);
        Assert.Equal(new[] { 40.0, 40.0 }, again.Document.Shapes[4].Points[2]);

        // A second save of the reloaded document keeps everything and stays consistent.
        session.SaveEditedShapes(rel, again.Document, again.Sidecar, shape => again.Holes.TryGet(shape, out PointF[][] holes) ? holes : null);
        Assert.Single(session.GetAnnotations(rel));
        Assert.Equal(5, session.LoadForEdit(rel).Document.Shapes.Count);
    }
}
