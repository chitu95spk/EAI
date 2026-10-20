using System.Drawing;
using EasyAI.Desktop.ViewModels;
using EasyLabel.Core.Io;
using EasyLabel.Core.Model;
using EasyLabel.Core.Projects;
using EasyLabel.SegAI.AutoLabel;
using EasyLabel.SegAI.Data;
using EasyLabel.SegAI.Training;

namespace EasyLabel.SegAI.Tests;

// EAI-POS P6: instance head results to oriented box / circle suggestions, the label points they produce, and the Position train rule.
public sealed class SegInstanceSuggestionTests
{
    private const double Tolerance = 1e-3;

    private static SegAiInstanceResult Instance(int kind, float cx, float cy, float w, float h, float theta, int classIndex = 0, float score = 0.9f)
    {
        return new SegAiInstanceResult { Kind = kind, Cx = cx, Cy = cy, W = w, H = h, Theta = theta, Score = score, ClassIndex = classIndex };
    }

    private static double Dist(PointF a, PointF b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    [Fact]
    public void BoxCorners_RoundTrip_RecoversCentreSizeAndAngle()
    {
        // The label-file decode of the Position converter (SegLabelMeConverter.ToInstances): centre = mean corner, w = TL-TR, h = TL-BL, theta = atan2(TR-TL).
        float[] thetas = { 0f, 0.3f, -0.7f, 1.2f };
        foreach (float theta in thetas)
        {
            PointF[] c = SegAiSuggestionBuilder.BoxCorners(10f, 20f, 30f, 12f, theta);
            Assert.Equal(4, c.Length);
            double cx = (c[0].X + c[1].X + c[2].X + c[3].X) / 4.0, cy = (c[0].Y + c[1].Y + c[2].Y + c[3].Y) / 4.0;
            Assert.True(Math.Abs(cx - 10) < Tolerance && Math.Abs(cy - 20) < Tolerance, "centre");
            Assert.True(Math.Abs(Dist(c[0], c[1]) - 30) < Tolerance, "w = TL to TR");
            Assert.True(Math.Abs(Dist(c[0], c[3]) - 12) < Tolerance, "h = TL to BL");
            Assert.True(Math.Abs(Math.Atan2(c[1].Y - c[0].Y, c[1].X - c[0].X) - theta) < Tolerance, "theta = atan2(TR - TL)");
        }
    }

    [Fact]
    public void BuildInstances_Box_IsOrientedRectangleWithClassAndScore()
    {
        var list = SegAiSuggestionBuilder.BuildInstances("a.png", new[] { Instance(SegInstance.KindBox, 50, 40, 20, 8, 0.5f, classIndex: 2, score: 0.75f) });

        SegAiSuggestion s = Assert.Single(list);
        Assert.Equal("oriented_rectangle", s.ShapeType);
        Assert.Equal(4, s.Outer.Length);
        Assert.Empty(s.Holes);
        Assert.Equal(2, s.ClassIndex);
        Assert.Equal(0.75f, s.Score);
        Assert.Equal("a.png", s.Rel);
    }

    [Fact]
    public void BuildInstances_Circle_IsRingOfRadiusFromSize()
    {
        var list = SegAiSuggestionBuilder.BuildInstances("a.png", new[] { Instance(SegInstance.KindCircle, 30, 40, 10, 10, 0) });

        SegAiSuggestion s = Assert.Single(list);
        Assert.Equal("circle", s.ShapeType);
        Assert.Equal(SegAiSuggestionBuilder.CircleRingSegments, s.Outer.Length);
        foreach (PointF p in s.Outer) Assert.True(Math.Abs(Dist(new PointF(30, 40), p) - 5) < Tolerance, "ring radius = W/2");
    }

    [Fact]
    public void BuildInstances_DropsNonFiniteAndEmptyInstances()
    {
        var list = SegAiSuggestionBuilder.BuildInstances("a.png", new[]
        {
            Instance(SegInstance.KindBox, float.NaN, 40, 20, 8, 0),
            Instance(SegInstance.KindBox, 50, 40, 0, 8, 0),
            Instance(SegInstance.KindCircle, 50, 40, 10, float.PositiveInfinity, 0),
        });

        Assert.Empty(list);
    }

    [Fact]
    public void InstancePoints_Box_RoundTripsToDirection()
    {
        PointF[] corners = SegAiSuggestionBuilder.BoxCorners(50f, 40f, 20f, 8f, 0.4f);
        List<double[]> points = SegAiSuggestionBuilder.InstancePoints("oriented_rectangle", corners);

        Assert.Equal(4, points.Count);
        Assert.True(Math.Abs(SegAiSuggestionBuilder.DirectionOf(corners) - 0.4) < Tolerance, "Direction is the TL to TR angle");
    }

    [Fact]
    public void InstancePoints_Circle_IsCentreAndEdgePoint()
    {
        PointF[] ring = SegAiSuggestionBuilder.CircleRing(30f, 40f, 5f);
        List<double[]> points = SegAiSuggestionBuilder.InstancePoints("circle", ring);

        Assert.Equal(2, points.Count);
        Assert.True(Math.Abs(points[0][0] - 30) < Tolerance && Math.Abs(points[0][1] - 40) < Tolerance, "centre");
        Assert.True(Math.Abs(Math.Sqrt(Math.Pow(points[1][0] - points[0][0], 2) + Math.Pow(points[1][1] - points[0][1], 2)) - 5) < Tolerance, "radius");
    }

    [Fact]
    public void LabelStepRules_ToShape_OrientedAndCircle_KeepInstanceGeometry()
    {
        SegAiSuggestion box = SegAiSuggestionBuilder.BuildInstances("a.png", new[] { Instance(SegInstance.KindBox, 50, 40, 20, 8, 0.4f) })[0];
        LabelShape oriented = LabelStepRules.ToShape(box, "NG");
        Assert.Equal("oriented_rectangle", oriented.ShapeType);
        Assert.Equal(4, oriented.Points.Count);
        Assert.NotNull(oriented.Direction);
        Assert.True(Math.Abs(oriented.Direction!.Value - 0.4) < Tolerance, "Direction survives the accept");

        SegAiSuggestion circle = SegAiSuggestionBuilder.BuildInstances("a.png", new[] { Instance(SegInstance.KindCircle, 30, 40, 10, 10, 0) })[0];
        LabelShape disc = LabelStepRules.ToShape(circle, "OK");
        Assert.Equal("circle", disc.ShapeType);
        Assert.Equal(2, disc.Points.Count);
        Assert.True(Math.Abs(disc.Points[0][0] - 30) < Tolerance && Math.Abs(disc.Points[0][1] - 40) < Tolerance, "centre");
        Assert.True(Math.Abs(disc.Points[1][0] - 35) < Tolerance && Math.Abs(disc.Points[1][1] - 40) < Tolerance, "edge point at r = 5");
    }

    [Fact]
    public void InstanceBlockOf_PositionNeedsBoxOrCircleLabel_DetectDoesNot()
    {
        Assert.Equal(SegAiTrainBlock.NoInstanceLabels, SegAiTrainingService.InstanceBlockOf(true, 0));
        Assert.Equal(SegAiTrainBlock.None, SegAiTrainingService.InstanceBlockOf(true, 1));
        Assert.Equal(SegAiTrainBlock.None, SegAiTrainingService.InstanceBlockOf(false, 0));
    }

    [Fact]
    public void TrainStepRules_NoInstanceLabels_HasTextAndLabelFix()
    {
        Assert.Equal("TrainBlockNoInstanceLabels", TrainStepRules.BlockKey(SegAiTrainBlock.NoInstanceLabels));
        Assert.Equal(TrainStepRules.TrainFixKind.GoToLabel, TrainStepRules.FixKindOf(SegAiTrainBlock.NoInstanceLabels, 5));
    }

    [Fact]
    public void AutoLabelSuggestions_IsInstanceShape_OnlyBoxAndCircle()
    {
        Assert.True(SegAiSuggestionBuilder.IsInstanceShape("oriented_rectangle"));
        Assert.True(SegAiSuggestionBuilder.IsInstanceShape("circle"));
        Assert.False(SegAiSuggestionBuilder.IsInstanceShape("polygon"));
        Assert.False(SegAiSuggestionBuilder.IsInstanceShape("rectangle"));
    }

    [Fact]
    public void BuildInstances_Circle_UsesWidthAndHeightMeanForRadius()
    {
        // W = 10, H = 14: radius (W + H) / 4 = 6.
        var list = SegAiSuggestionBuilder.BuildInstances("a.png", new[] { Instance(SegInstance.KindCircle, 30, 40, 10, 14, 0) });

        SegAiSuggestion s = Assert.Single(list);
        foreach (PointF p in s.Outer) Assert.True(Math.Abs(Dist(new PointF(30, 40), p) - 6) < Tolerance, "ring radius = (W + H) / 4");
    }

    [Fact]
    public void MissingInstanceHead_OnlyForPositionWithLoadedModelWithoutHead()
    {
        Assert.True(AutoLabelService.MissingInstanceHead(isPosition: true, modelLoaded: true, instanceHead: false));
        Assert.False(AutoLabelService.MissingInstanceHead(isPosition: true, modelLoaded: true, instanceHead: true));
        Assert.False(AutoLabelService.MissingInstanceHead(isPosition: true, modelLoaded: false, instanceHead: false));
        Assert.False(AutoLabelService.MissingInstanceHead(isPosition: false, modelLoaded: true, instanceHead: false));
    }

    [Fact]
    public void Accept_PositionSkipsPolygon_WritesInstanceShapes()
    {
        string root = TrainFixtures.Folder("eai-pos-accept");
        TrainFixtures.WriteImage(root, "a.png", 1, 160, 120);
        using (SegAiSession created = SegAiSession.Create(root)) ProjectKindStore.Write(created.Paths, ProjectKind.Position);
        using SegAiSession session = SegAiSession.Open(root);
        var service = new AutoLabelService(session, rel => throw new NotSupportedException(), (rel, floor) => throw new NotSupportedException());

        var polygon = new SegAiSuggestion("a.png", 0, new[] { new PointF(10, 10), new PointF(40, 10), new PointF(40, 40) }, Array.Empty<PointF[]>(), 0.5f);
        List<SegAiSuggestion> suggestions = new List<SegAiSuggestion> { polygon };
        suggestions.AddRange(SegAiSuggestionBuilder.BuildInstances("a.png", new[] { Instance(SegInstance.KindBox, 50, 40, 20, 8, 0.4f) }));

        Assert.Equal(1, service.Accept("a.png", suggestions));
        LabelMeDocument doc = new LabelMeIO().Read(Path.Combine(root, "a.json"));
        LabelShape shape = Assert.Single(doc.Shapes);
        Assert.Equal("oriented_rectangle", shape.ShapeType);
        Assert.Equal(4, shape.Points.Count);
    }

    [Fact]
    public void Accept_DetectKeepsPolygon()
    {
        string root = TrainFixtures.Folder("eai-detect-accept");
        TrainFixtures.WriteImage(root, "a.png", 1, 160, 120);
        using SegAiSession session = SegAiSession.Create(root);
        var service = new AutoLabelService(session, rel => throw new NotSupportedException(), (rel, floor) => throw new NotSupportedException());

        var polygon = new SegAiSuggestion("a.png", 0, new[] { new PointF(10, 10), new PointF(40, 10), new PointF(40, 40) }, Array.Empty<PointF[]>(), 0.5f);

        Assert.Equal(1, service.Accept("a.png", new List<SegAiSuggestion> { polygon }));
        LabelShape shape = Assert.Single(new LabelMeIO().Read(Path.Combine(root, "a.json")).Shapes);
        Assert.Equal("polygon", shape.ShapeType);
    }

    [Fact]
    public void InstanceStamp_DetectHasNoSuffix_PositionCarriesQuality()
    {
        string detectRoot = TrainFixtures.Folder("eai-stamp-detect");
        TrainFixtures.WriteImage(detectRoot, "a.png", 1, 64, 64);
        using (SegAiSession detect = SegAiSession.Create(detectRoot))
        {
            Assert.Equal(string.Empty, detect.InstanceStampSuffix());
        }

        string root = TrainFixtures.Folder("eai-stamp-position");
        TrainFixtures.WriteImage(root, "a.png", 1, 64, 64);
        using (SegAiSession created = SegAiSession.Create(root)) ProjectKindStore.Write(created.Paths, ProjectKind.Position);
        using SegAiSession position = SegAiSession.Open(root);
        Assert.Equal("|quality:Quality", position.InstanceStampSuffix());
        position.SetInstanceQuality(SegInstanceQuality.Performance);
        Assert.Equal(SegInstanceQuality.Performance, position.InstanceQuality);
        Assert.Equal("|quality:Performance", position.InstanceStampSuffix());
    }
}
