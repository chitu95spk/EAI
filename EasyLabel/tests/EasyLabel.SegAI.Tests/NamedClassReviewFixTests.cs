using System.Runtime.InteropServices;
using EasyLabel.SegAI.Convert;
using EasyLabel.SegAI.Data;
using EasyLabel.SegAI.Engine;
using EasyLabel.SegAI.Model;
using EasyLabel.SegAI.Options;
using EasyLabel.SegAI.Project;
using EasyLabel.SegAI.Training;
using OpenCvSharp;
using PointF = System.Drawing.PointF;

namespace EasyLabel.SegAI.Tests;

/// <summary>
/// EAI-CLS review fixes A3-A5: disabled classes are left out of validation metrics, a legacy EasyVision dataset is matched against a
/// named table by names and kinds only, and an invalid segClasses in advanced.json is an error instead of a silent NG/OK fallback.
/// </summary>
public sealed class NamedClassReviewFixTests
{
    private static string NewFolder(string name)
    {
        string dir = Path.Combine(SegAiFixtures.ScratchFolder("reviewfix"), name + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static PointF[] Box(float x0, float y0, float x1, float y1)
    {
        return new[] { new PointF(x0, y0), new PointF(x1, y0), new PointF(x1, y1), new PointF(x0, y1) };
    }

    private static byte[] TinyPng()
    {
        using var image = new Mat(64, 64, MatType.CV_8UC3, new Scalar(30, 60, 90));
        Assert.True(Cv2.ImEncode(".png", image, out byte[] png));
        return png;
    }

    private static EvDatasetEntry OneEntry() => new("img0.bmp", null, null, TinyPng(), null,
        new List<SegAnnotation> { new SegAnnotation { Label = 1, Points = Box(4, 4, 30, 30) } }, (byte)0);

    [Fact]
    public void EvaluatedLabels_SkipsDisabledClasses_AndKeepsTheNgOkDefault()
    {
        Assert.Equal(new[] { 1, 2 }, SegAiTrainingPipeline.EvaluatedLabels(null));
        Assert.Equal(new[] { 1, 2 }, SegAiTrainingPipeline.EvaluatedLabels(SegAiClassTable.Legacy()));
        var table = new SegAiClassTable(new[]
        {
            new SegAiClassEntry("scratch", SegAiClassKind.Defect, true),
            new SegAiClassEntry("dent", SegAiClassKind.Defect, false),
            new SegAiClassEntry("stain", SegAiClassKind.Good, true),
        });
        Assert.Equal(new[] { 1, 3 }, SegAiTrainingPipeline.EvaluatedLabels(table));
    }

    [Fact]
    public void EvaluateTrainingModel_DisabledClass_IsLeftOutOfClassMetricsPixelsAndImageResults()
    {
        string dir = NewFolder("disabled-eval");
        const int w = 64, h = 64;
        using (var image = new Mat(h, w, MatType.CV_8UC3, new Scalar(10, 20, 30)))
        using (var labels = new Mat(h, w, MatType.CV_8UC1, Scalar.All(0)))
        {
            Cv2.Rectangle(labels, new OpenCvSharp.Rect(4, 4, 16, 16), Scalar.All(1), -1);   // scratch: enabled DEFECT
            Cv2.Rectangle(labels, new OpenCvSharp.Rect(4, 40, 12, 12), Scalar.All(2), -1);  // dent: disabled DEFECT, truth present
            Cv2.Rectangle(labels, new OpenCvSharp.Rect(30, 30, 20, 20), Scalar.All(3), -1); // stain: enabled GOOD
            Cv2.ImWrite(Path.Combine(dir, "0.png"), image);
            Cv2.ImWrite(Path.Combine(dir, "0-labels.png"), labels);
        }

        var table = new SegAiClassTable(new[]
        {
            new SegAiClassEntry("scratch", SegAiClassKind.Defect, true),
            new SegAiClassEntry("dent", SegAiClassKind.Defect, false),
            new SegAiClassEntry("stain", SegAiClassKind.Good, true),
        });
        var backend = new FakeTrainBackend
        {
            // The fake model finds the truth of the enabled classes exactly and gives the disabled class no plane.
            MultiFactory = (bgr, count) =>
            {
                using var truthLabels = Cv2.ImRead(Path.Combine(dir, "0-labels.png"), ImreadModes.Grayscale);
                var label = new byte[w * h];
                Marshal.Copy(truthLabels.Data, label, 0, label.Length);
                var score = new byte[label.Length];
                for (int i = 0; i < label.Length; i++)
                {
                    if (label[i] == 2) label[i] = 0;
                    else if (label[i] != 0) score[i] = 255;
                }

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

        Assert.Equal(new[] { "scratch", "stain" }, report.Classes.Select(c => c.Label).ToArray());
        Assert.Equal(new[] { 1, 1 }, report.Classes.Select(c => c.Truth).ToArray());
        Assert.Equal(new[] { 1, 1 }, report.Classes.Select(c => c.TruePositive).ToArray());
        Assert.Equal(new[] { 0, 0 }, report.Classes.Select(c => c.FalsePositive).ToArray());
        // Pixel recall counts only the enabled truth (256 + 400 pixels); the disabled class 144 pixels would give 0.82.
        Assert.Equal(1.0, report.PixelRecall);
        Assert.Equal(1.0, report.PixelPrecision);
        Assert.Equal(1, report.ImageResults![0].NgTruth);
        Assert.Equal(1, report.ImageResults[0].NgTruePositive);
        Assert.Equal(1, report.ImageResults[0].OkTruth);
        Assert.Equal(1, report.ImageResults[0].OkTruePositive);
    }

    [Fact]
    public void LegacyDataset_WithEnabledOnlyChange_IsAccepted()
    {
        string root = NewFolder("legacy-enabled");
        string work = NewFolder("legacy-enabled-work");
        string zip = Path.Combine(work, "plain.zip");
        EasyVisionDataset.WriteZip(zip, new[] { OneEntry() }, null);

        using var session = SegAiSession.Create(root);
        session.SaveClassTable(SegAiClassTable.Legacy(okEnabled: false));
        Assert.Equal(1, session.ImportEasyVisionDataset(zip).Imported);
    }

    [Fact]
    public void LegacyDataset_AgainstRenamedClass_IsRejected()
    {
        string root = NewFolder("legacy-renamed");
        string work = NewFolder("legacy-renamed-work");
        string zip = Path.Combine(work, "plain.zip");
        EasyVisionDataset.WriteZip(zip, new[] { OneEntry() }, null);

        using var session = SegAiSession.Create(root);
        session.SaveClassTable(new SegAiClassTable(new[]
        {
            new SegAiClassEntry("Scratch", SegAiClassKind.Defect, true),
            new SegAiClassEntry("OK", SegAiClassKind.Good, true),
        }));
        var ex = Assert.Throws<InvalidDataException>(() => session.ImportEasyVisionDataset(zip));
        Assert.Contains("no class table", ex.Message);
    }

    [Fact]
    public void InvalidSegClasses_Throws_WithPathAndReason_InsteadOfFallingBackToNgOk()
    {
        string root = NewFolder("invalid-segclasses");
        var store = new SegProjectStore(root);
        Directory.CreateDirectory(store.EasyAiDir);
        string path = Path.Combine(store.EasyAiDir, "advanced.json");
        File.WriteAllText(path, "{\"segClasses\":[{\"name\":\"g1\",\"kind\":\"good\"},{\"name\":\"g2\",\"kind\":\"good\"}]}");

        var ex = Assert.Throws<InvalidDataException>(() => store.LoadClassMap());
        Assert.Contains(path, ex.Message);
        Assert.Contains("more than one GOOD class", ex.Message);

        File.WriteAllText(path, "{\"segClasses\":\"not-an-array\"}");
        Assert.Throws<InvalidDataException>(() => store.LoadClassMap());
    }

    [Fact]
    public void ValidSegClasses_ParsesKindCaseInsensitively_AndMissingKeyKeepsNgOk()
    {
        string root = NewFolder("valid-segclasses");
        var store = new SegProjectStore(root);
        Assert.False(store.LoadClassMap().HasNamedTable);

        Directory.CreateDirectory(store.EasyAiDir);
        File.WriteAllText(Path.Combine(store.EasyAiDir, "advanced.json"),
            "{\"segClasses\":[{\"name\":\"scratch\",\"kind\":\"DEFECT\"},{\"name\":\"dent\",\"kind\":\"Defect\",\"enabled\":false},{\"name\":\"good\",\"kind\":\"GOOD\"}]}");

        SegClassMap map = store.LoadClassMap();
        Assert.True(map.HasNamedTable);
        Assert.Equal(3, map.Count);
        Assert.Equal(SegAiClassKind.Defect, map.KindOf(1));
        Assert.Equal(SegAiClassKind.Defect, map.KindOf(2));
        Assert.Equal(SegAiClassKind.Good, map.KindOf(3));
        Assert.False(map.Table.Classes[1].Enabled);
    }
}
