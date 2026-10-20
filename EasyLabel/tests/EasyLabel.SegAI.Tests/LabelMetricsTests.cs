using EasyLabel.Core.Io;
using EasyLabel.Core.Model;

namespace EasyLabel.SegAI.Tests;

public sealed class LabelMetricsTests
{
    private static double[][] Square(double cx, double cy, double half)
        => new[] { new[] { cx - half, cy - half }, new[] { cx + half, cy - half }, new[] { cx + half, cy + half }, new[] { cx - half, cy + half } };

    private static double[][] Diamond(double cx, double cy, double radius)
        => new[] { new[] { cx + radius, cy }, new[] { cx, cy + radius }, new[] { cx - radius, cy }, new[] { cx, cy - radius } };

    private static LabelShape Box(string label, double[][] points)
        => new LabelShape { Label = label, ShapeType = "oriented_rectangle", Points = points.ToList() };

    [Fact]
    public void IdenticalShapesHaveIouOne()
    {
        Assert.Equal(1.0, LabelMetrics.Iou(Square(10, 10, 5), Square(10, 10, 5)), 9);
    }

    [Fact]
    public void CornerOrderAndWindingDoNotMatter()
    {
        double[][] a = Square(10, 10, 5);
        double[][] reversed = a.Reverse().ToArray();
        Assert.Equal(1.0, LabelMetrics.Iou(a, reversed), 9);
    }

    [Fact]
    public void DisjointShapesHaveIouZero()
    {
        Assert.Equal(0.0, LabelMetrics.Iou(Square(0, 0, 1), Square(10, 10, 1)), 9);
    }

    [Fact]
    public void HalfOverlapHasIouOneThird()
    {
        // 2x2 squares shifted by 1 in x: intersection 2, union 6.
        Assert.Equal(1.0 / 3.0, LabelMetrics.Iou(Square(0, 0, 1), Square(1, 0, 1)), 9);
    }

    [Fact]
    public void SquareAgainstItself45DegreesRotatedIsOneOverRootTwo()
    {
        // Side-2 square vs the diamond of the same area (4): intersection 8*sqrt(2)-8, union 16-8*sqrt(2), IoU = 1/sqrt(2).
        Assert.Equal(1.0 / Math.Sqrt(2.0), LabelMetrics.Iou(Square(0, 0, 1), Diamond(0, 0, Math.Sqrt(2.0))), 9);
    }

    [Fact]
    public void DegenerateShapeHasIouZero()
    {
        double[][] line = { new[] { 0.0, 0.0 }, new[] { 1.0, 1.0 }, new[] { 2.0, 2.0 }, new[] { 3.0, 3.0 } };
        Assert.Equal(0.0, LabelMetrics.Iou(line, Square(1, 1, 1)), 9);
    }

    [Fact]
    public void PerfectPredictionsHaveNoErrors()
    {
        var gt = new List<LabelShape> { Box("OK", Square(10, 10, 5)), Box("OK", Square(40, 10, 5)) };
        var pred = new List<LabelShape> { Box("OK", Square(40, 10, 5)), Box("OK", Square(10, 10, 5)) };
        ImageQuality q = LabelMetrics.Evaluate("a", pred, gt);
        Assert.Equal(2, q.TruePositives);
        Assert.Equal(0, q.FalsePositives);
        Assert.Equal(0, q.FalseNegatives);
        Assert.Equal(1.0, q.Precision, 9);
        Assert.Equal(1.0, q.Recall, 9);
        Assert.Equal(1.0, q.MeanIou, 9);
        Assert.Equal(0, q.ShapeCountError);
    }

    [Fact]
    public void MatchingIsOneToOneAndGreedyByIou()
    {
        // One ground-truth box; two predictions overlap it. The better one wins, the other is a false positive.
        var gt = new List<LabelShape> { Box("OK", Square(0, 0, 1)) };
        var pred = new List<LabelShape> { Box("OK", Square(0.5, 0, 1)), Box("OK", Square(0.1, 0, 1)) };
        ImageQuality q = LabelMetrics.Evaluate("a", pred, gt);
        Assert.Equal(1, q.TruePositives);
        Assert.Equal(1, q.FalsePositives);
        Assert.Equal(0, q.FalseNegatives);
        Assert.Equal(1, q.ShapeCountError);
        Assert.Equal(1.9 / 2.1, q.MeanIou, 9);
    }

    [Fact]
    public void ThresholdAndClassDecideTheMatch()
    {
        var gt = new List<LabelShape> { Box("OK", Square(0, 0, 1)) };
        ImageQuality below = LabelMetrics.Evaluate("a", new List<LabelShape> { Box("OK", Square(1, 0, 1)) }, gt);
        Assert.Equal(0, below.TruePositives);
        Assert.Equal(1, below.FalsePositives);
        Assert.Equal(1, below.FalseNegatives);

        ImageQuality loose = LabelMetrics.Evaluate("a", new List<LabelShape> { Box("OK", Square(1, 0, 1)) }, gt, 0.3);
        Assert.Equal(1, loose.TruePositives);

        ImageQuality otherClass = LabelMetrics.Evaluate("a", new List<LabelShape> { Box("NG", Square(0, 0, 1)) }, gt);
        Assert.Equal(0, otherClass.TruePositives);
        Assert.Equal(1, otherClass.FalsePositives);
        Assert.Equal(1, otherClass.FalseNegatives);
    }

    [Fact]
    public void EmptyPredictionsMissEverything()
    {
        var gt = new List<LabelShape> { Box("OK", Square(0, 0, 1)), Box("OK", Square(9, 9, 1)) };
        ImageQuality q = LabelMetrics.Evaluate("a", new List<LabelShape>(), gt);
        Assert.Equal(0, q.TruePositives);
        Assert.Equal(0, q.FalsePositives);
        Assert.Equal(2, q.FalseNegatives);
        Assert.Equal(0.0, q.Precision, 9);
        Assert.Equal(0.0, q.Recall, 9);
        Assert.Equal(0.0, q.MeanIou, 9);
        Assert.Equal(-2, q.ShapeCountError);
    }

    [Fact]
    public void EmptyAgainstEmptyIsPerfect()
    {
        ImageQuality q = LabelMetrics.Evaluate("a", new List<LabelShape>(), new List<LabelShape>());
        Assert.Equal(1.0, q.Precision, 9);
        Assert.Equal(1.0, q.Recall, 9);
    }

    [Fact]
    public void FolderEvaluationCountsMissingPredictionFiles()
    {
        string root = Path.Combine(AppContext.BaseDirectory, "label-metrics-" + Guid.NewGuid().ToString("N"));
        string gtDir = Path.Combine(root, "gt");
        string predDir = Path.Combine(root, "pred");
        Directory.CreateDirectory(gtDir);
        Directory.CreateDirectory(predDir);
        try
        {
            var io = new LabelMeIO();
            io.Write(new LabelMeDocument { Shapes = { Box("OK", Square(10, 10, 5)), Box("OK", Square(40, 10, 5)) } }, Path.Combine(gtDir, "a.json"));
            io.Write(new LabelMeDocument { Shapes = { Box("OK", Square(10, 10, 5)) } }, Path.Combine(gtDir, "b.json"));
            io.Write(new LabelMeDocument { Shapes = { Box("OK", Square(10, 10, 5)), Box("OK", Square(80, 80, 5)) } }, Path.Combine(predDir, "a.json"));

            FolderQuality q = LabelMetrics.EvaluateFolder(predDir, gtDir);
            Assert.Equal(2, q.Images.Count);
            Assert.Equal(1, q.MissingPredictions);
            Assert.Equal(1, q.TruePositives);
            Assert.Equal(1, q.FalsePositives);
            Assert.Equal(2, q.FalseNegatives);
            Assert.Equal(1.0, q.MeanIou, 9);
            Assert.Contains("\"tp\": 1", LabelMetrics.ToJson(q));
            Assert.StartsWith("name,", LabelMetrics.ToCsv(q));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
