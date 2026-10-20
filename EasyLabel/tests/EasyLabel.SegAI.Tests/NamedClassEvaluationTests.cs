using EasyLabel.SegAI.Engine;
using EasyLabel.SegAI.Model;
using EasyLabel.SegAI.Options;
using EasyLabel.SegAI.Training;
using OpenCvSharp;

namespace EasyLabel.SegAI.Tests;

public sealed class NamedClassEvaluationTests
{
    [Fact]
    public void EvaluateTrainingModel_reports_one_metric_per_named_class()
    {
        string dir = Path.Combine(SegAiFixtures.ScratchFolder("namedeval"), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        const int w = 64, h = 64;
        using (var image = new Mat(h, w, MatType.CV_8UC3, new Scalar(10, 20, 30)))
        using (var labels = new Mat(h, w, MatType.CV_8UC1, Scalar.All(0)))
        {
            Cv2.Rectangle(labels, new OpenCvSharp.Rect(4, 4, 16, 16), Scalar.All(1), -1);
            Cv2.Rectangle(labels, new OpenCvSharp.Rect(30, 30, 20, 20), Scalar.All(3), -1);
            Cv2.ImWrite(Path.Combine(dir, "0.png"), image);
            Cv2.ImWrite(Path.Combine(dir, "0-labels.png"), labels);
        }

        var table = new SegAiClassTable(new[]
        {
            new SegAiClassEntry("scratch", SegAiClassKind.Defect, true),
            new SegAiClassEntry("dent", SegAiClassKind.Defect, true),
            new SegAiClassEntry("stain", SegAiClassKind.Defect, true),
        });
        var backend = new FakeTrainBackend
        {
            // The fake model reproduces the ground truth exactly: class 1 and class 3 found with full score.
            MultiFactory = (bgr, count) =>
            {
                using var truthLabels = Cv2.ImRead(Path.Combine(dir, "0-labels.png"), ImreadModes.Grayscale);
                var label = new byte[w * h];
                System.Runtime.InteropServices.Marshal.Copy(truthLabels.Data, label, 0, label.Length);
                var score = new byte[label.Length];
                for (int i = 0; i < score.Length; i++) if (label[i] != 0) score[i] = 255;
                return new SegAiMultiPredictResult { Width = w, Height = h, Label = label, Score = score, ClassFractions = new float[count] };
            },
        };
        var frames = new List<SegAiTrainingFrame>
        {
            new() { Index = 0, Name = "a", Image = Path.Combine(dir, "0.png"), Mask = Path.Combine(dir, "0-labels.png"), Width = w, Height = h },
        };
        var report = new SegTrainingReport { Version = 2, ScorePercent = 50 };

        SegAiTrainingPipeline.EvaluateTrainingModel(backend, new SegInferenceSettings { EnableGpu = false }, "ignored.segai", frames, report,
            CancellationToken.None, null, table);

        Assert.Equal(new[] { "scratch", "dent", "stain" }, report.Classes.Select(c => c.Label).ToArray());
        Assert.Equal(new[] { 1, 0, 1 }, report.Classes.Select(c => c.Truth).ToArray());
        Assert.Equal(new[] { 1, 0, 1 }, report.Classes.Select(c => c.TruePositive).ToArray());
        Assert.Equal(new[] { 0, 0, 0 }, report.Classes.Select(c => c.FalsePositive).ToArray());
        Assert.Equal(1.0, report.PixelRecall);
        Assert.Equal(1.0, report.PixelPrecision);
        Assert.Equal(2, report.ImageResults![0].NgTruth);
        Assert.Equal(2, report.ImageResults[0].NgTruePositive);
    }
}
