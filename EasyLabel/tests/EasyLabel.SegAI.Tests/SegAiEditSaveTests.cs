using EasyLabel.Core.Model;
using EasyLabel.SegAI.Data;
using OpenCvSharp;
using PointF = System.Drawing.PointF;

namespace EasyLabel.SegAI.Tests;

public sealed class SegAiEditSaveTests
{
    private static string NewFolder(string name)
    {
        string dir = Path.Combine(SegAiFixtures.ScratchFolder("editsave"), name + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void WriteImage(string folder, string fileName)
    {
        using var image = new Mat(120, 160, MatType.CV_8UC3, new Scalar(30, 60, 90));
        Assert.True(Cv2.ImWrite(Path.Combine(folder, fileName), image));
    }

    private static PointF[] Box(float x0, float y0, float x1, float y1)
    {
        return new[] { new PointF(x0, y0), new PointF(x1, y0), new PointF(x1, y1), new PointF(x0, y1) };
    }

    private static SegAiSession Prepare(string name, out string rel)
    {
        string root = NewFolder(name);
        WriteImage(root, "a.png");
        rel = "a.png";
        SegAiSession session = SegAiSession.Create(root);
        session.SetAnnotations(rel, new[]
        {
            new SegAnnotation { Label = 1, Points = Box(20, 20, 100, 80), Holes = new[] { Box(40, 40, 60, 60) } },
        });
        return session;
    }

    private static Func<LabelShape, PointF[][]?> Lookup(SegAiEditDocument edit)
    {
        return shape => edit.Holes.TryGet(shape, out PointF[][] holes) ? holes : null;
    }

    private static void Shift(LabelShape shape, double dx)
    {
        foreach (double[] p in shape.Points) p[0] += dx;
    }

    [Fact]
    public void Save_UntouchedShape_KeepsHolesAndIsNotStale()
    {
        using SegAiSession session = Prepare("untouched", out string rel);
        SegAiEditDocument edit = session.LoadForEdit(rel);
        Assert.False(edit.Stale);
        Assert.NotNull(Lookup(edit)(edit.Document.Shapes[0]));

        session.SaveEditedShapes(rel, edit.Document, edit.Sidecar, Lookup(edit));

        IReadOnlyList<SegAnnotation> regions = session.GetAnnotations(rel);
        Assert.Single(regions);
        Assert.Single(regions[0].Holes);
        Assert.Equal(Box(40, 40, 60, 60), regions[0].Holes[0]);
        Assert.False(session.Samples.Single().Stale);
    }

    [Fact]
    public void Save_MovedShape_UsesCallerHolesAndIsNotStale()
    {
        using SegAiSession session = Prepare("moved", out string rel);
        SegAiEditDocument edit = session.LoadForEdit(rel);
        PointF[][] oldHoles = Lookup(edit)(edit.Document.Shapes[0])!;
        Shift(edit.Document.Shapes[0], 10);
        PointF[][] moved = oldHoles.Select(h => h.Select(p => new PointF(p.X + 10, p.Y)).ToArray()).ToArray();

        session.SaveEditedShapes(rel, edit.Document, edit.Sidecar, _ => moved);

        Assert.False(session.Samples.Single().Stale);
        IReadOnlyList<SegAnnotation> regions = session.GetAnnotations(rel);
        Assert.Equal(Box(30, 20, 110, 80)[0], regions[0].Points[0]);
        Assert.Equal(Box(50, 40, 70, 60), regions[0].Holes[0]);
    }

    [Fact]
    public void Save_KeepsStoredMode()
    {
        using SegAiSession session = Prepare("mode", out string rel);
        session.SetMode(rel, SegTrainingMode.Val);
        SegAiEditDocument edit = session.LoadForEdit(rel);

        session.SaveEditedShapes(rel, edit.Document, edit.Sidecar, Lookup(edit));

        Assert.Equal(SegTrainingMode.Val, session.Samples.Single().Mode);
    }

    [Fact]
    public void Save_KeepsNonSegAiShape()
    {
        using SegAiSession session = Prepare("other", out string rel);
        SegAiEditDocument edit = session.LoadForEdit(rel);
        var other = new LabelShape { Label = "scratch", ShapeType = "rectangle" };
        other.Points.Add(new double[] { 5, 5 });
        other.Points.Add(new double[] { 15, 15 });
        edit.Document.Shapes.Add(other);

        session.SaveEditedShapes(rel, edit.Document, edit.Sidecar, Lookup(edit));

        SegAiEditDocument again = session.LoadForEdit(rel);
        Assert.Equal(2, again.Document.Shapes.Count);
        Assert.Contains(again.Document.Shapes, s => s.Label == "scratch");
        Assert.False(again.Stale);
        Assert.Single(session.GetAnnotations(rel));
    }

    [Fact]
    public void Save_RegionOutsideImage_ThrowsAndWritesNothing()
    {
        using SegAiSession session = Prepare("outside", out string rel);
        string root = session.ProjectRoot;
        string json = Path.Combine(root, "a.json");
        string seg = Path.Combine(root, ".easyai", "cache", "a.seg.json");
        string jsonBefore = File.ReadAllText(json);
        string segBefore = File.ReadAllText(seg);
        SegAiEditDocument edit = session.LoadForEdit(rel);
        Shift(edit.Document.Shapes[0], 100000); // far beyond the 4-image-size limit of a part cut by the border

        Assert.Throws<InvalidDataException>(() => session.SaveEditedShapes(rel, edit.Document, edit.Sidecar, Lookup(edit)));

        Assert.Equal(jsonBefore, File.ReadAllText(json));
        Assert.Equal(segBefore, File.ReadAllText(seg));
        Assert.False(File.Exists(Path.Combine(root, ".easyai", "meta", "a.json")));
    }

    [Fact]
    public void Save_RegionCutByTheBorder_IsKeptAndTheRasterPaintsOnlyTheVisiblePart()
    {
        using SegAiSession session = Prepare("cut", out string rel);
        SegAiEditDocument edit = session.LoadForEdit(rel);
        Shift(edit.Document.Shapes[0], 100); // image is 160x120: the box spans x 120..200

        session.SaveEditedShapes(rel, edit.Document, edit.Sidecar, Lookup(edit));

        IReadOnlyList<SegAnnotation> regions = session.GetAnnotations(rel);
        Assert.Single(regions);
        Assert.True(regions[0].Points.Max(p => p.X) > 160f, "the stored region keeps its part outside the image");
        string rasterPath = session.AnnotationFiles(rel).Single(f => f.EndsWith(".png", StringComparison.OrdinalIgnoreCase));
        using Mat raster = Cv2.ImRead(rasterPath, ImreadModes.Unchanged);
        if (!raster.Empty())
        {
            Assert.Equal(1, raster.At<byte>(50, 130));
            Assert.Equal(0, raster.At<byte>(50, 100));
        }
    }

    [Fact]
    public void LoadForEdit_AfterSave_RoundTripsShapesAndHoles()
    {
        using SegAiSession session = Prepare("roundtrip", out string rel);
        SegAiEditDocument first = session.LoadForEdit(rel);
        Shift(first.Document.Shapes[0], 7.3);
        PointF[][] holes = new[] { Box(47.3f, 40, 67.3f, 60) };
        session.SaveEditedShapes(rel, first.Document, first.Sidecar, _ => holes);

        SegAiEditDocument second = session.LoadForEdit(rel);

        Assert.False(second.Stale);
        Assert.Equal(first.Document.Shapes.Count, second.Document.Shapes.Count);
        for (int i = 0; i < first.Document.Shapes.Count; i++)
        {
            Assert.Equal(first.Document.Shapes[i].Label, second.Document.Shapes[i].Label);
            Assert.Equal(first.Document.Shapes[i].Points, second.Document.Shapes[i].Points);
        }

        Assert.Equal(holes, Lookup(second)(second.Document.Shapes[0]));
        Assert.Equal(new[] { "NG", "OK" }, session.ClassNames);
    }
}
