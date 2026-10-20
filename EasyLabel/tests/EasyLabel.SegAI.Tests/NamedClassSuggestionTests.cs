using EasyLabel.SegAI.AutoLabel;
using EasyLabel.SegAI.Inspection;
using EasyLabel.SegAI.Model;
using OpenCvSharp;

namespace EasyLabel.SegAI.Tests;

/// <summary>
/// P2h: auto-label and inspect on the named-class (N-class) prediction path. The fake tests need no native library; the
/// native tests are opt-in like <see cref="PredictClassesTests"/> (EASYAI_SEGAI_DIR + EASYAI_CLASS_MODELS) and return early otherwise.
/// </summary>
public sealed class NamedClassSuggestionTests
{
    private static readonly SegAiClassTable ThreeClasses = Table(
        ("Scratch", SegAiClassKind.Defect), ("Dent", SegAiClassKind.Defect), ("Good", SegAiClassKind.Good));

    private static SegAiClassTable Table(params (string Name, SegAiClassKind Kind)[] classes) =>
        new(classes.Select(c => new SegAiClassEntry(c.Name, c.Kind, true)).ToList());

    private static SegAiClassPlane Plane(int id, string name, SegAiClassKind kind, int x, int y)
    {
        var mask = new byte[64 * 64];
        for (int py = y; py < y + 20; py++)
            for (int px = x; px < x + 20; px++) mask[py * 64 + px] = 255;
        return new SegAiClassPlane { ClassId = id, Name = name, Kind = kind, Mask = mask };
    }

    /// <summary>Three planes in class id order (1 Scratch, 2 Dent, 3 Good), one 20x20 square each, no region scores.</summary>
    private static SegAiResult ThreeClassResult() => new()
    {
        Width = 64,
        Height = 64,
        IsOK = false,
        PerClass = new List<SegAiClassPlane>
        {
            Plane(1, "Scratch", SegAiClassKind.Defect, 4, 4),
            Plane(2, "Dent", SegAiClassKind.Defect, 30, 4),
            Plane(3, "Good", SegAiClassKind.Good, 4, 36),
        },
    };

    private static string? ClassModel(string file)
    {
        string? dir = Environment.GetEnvironmentVariable("EASYAI_CLASS_MODELS");
        if (string.IsNullOrEmpty(dir) || Environment.GetEnvironmentVariable("EASYAI_SEGAI_DIR") == null) return null;
        string path = Path.Combine(dir, file);
        return File.Exists(path) ? path : null;
    }

    private static SegAiSession OpenNative(string name, string model)
    {
        var session = SegAiSession.Create(TrainFixtures.Folder(name + "-" + Guid.NewGuid().ToString("N")));
        session.Inference.EnableGpu = false;
        Assert.Null(session.LoadModel(model).Problem);
        return session;
    }

    [Fact]
    public void SameClasses_ComparesCountNamesAndKindsByPosition_NotEnabledFlags()
    {
        Assert.True(SegAiSession.SameClasses(ThreeClasses, Table(("scratch", SegAiClassKind.Defect), ("DENT", SegAiClassKind.Defect), ("good", SegAiClassKind.Good))));
        Assert.False(SegAiSession.SameClasses(ThreeClasses, Table(("Dent", SegAiClassKind.Defect), ("Scratch", SegAiClassKind.Defect), ("Good", SegAiClassKind.Good))));
        Assert.False(SegAiSession.SameClasses(ThreeClasses, Table(("Scratch", SegAiClassKind.Defect), ("Dent", SegAiClassKind.Defect), ("Good", SegAiClassKind.Defect))));
        Assert.False(SegAiSession.SameClasses(ThreeClasses, Table(("Scratch", SegAiClassKind.Defect), ("Dent", SegAiClassKind.Defect))));
        var disabled = new SegAiClassTable(new[]
        {
            new SegAiClassEntry("Scratch", SegAiClassKind.Defect, false),
            new SegAiClassEntry("Dent", SegAiClassKind.Defect, true),
            new SegAiClassEntry("Good", SegAiClassKind.Good, true),
        });
        Assert.True(SegAiSession.SameClasses(ThreeClasses, disabled));
    }

    [Fact]
    public void ThreeClassAutoLabel_SuggestionsCarryTablePosition_AndAcceptWritesClassNames()
    {
        using SegAiSession session = SegAiSession.Create(TrainFixtures.Folder("p2h-accept"));
        session.SaveClassTable(ThreeClasses);
        TrainFixtures.WriteImage(session.ProjectRoot, "a.png", 1, 64, 64);
        var service = new AutoLabelService(session, rel => ("m1", "s1"), (rel, threshold) => (ThreeClassResult(), 64, 64));

        AutoLabelImageResult run = service.Run("a.png");

        Assert.Null(run.Error);
        Assert.True(run.HasScores);
        Assert.Equal(new[] { 0, 1, 2 }, run.Suggestions.Select(s => s.ClassIndex).ToArray());
        Assert.Equal(3, service.Accept("a.png", run.Suggestions));
        string[] written = session.LoadForEdit("a.png").Document.Shapes.Select(s => s.Label ?? string.Empty).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        Assert.Equal(new[] { "Dent", "Good", "Scratch" }, written);
    }

    [Fact]
    public void ThreeClassInspect_ExposesClassNamesAndIndexes()
    {
        var service = new InspectService(
            () => false,
            path => ("m1", "s1"),
            path => (ThreeClassResult(), 64, 64),
            () => (0.5f, 1),
            () => new DateTime(2026, 10, 10, 0, 0, 0, DateTimeKind.Utc));

        InspectResult result = service.Run("a.png");

        Assert.Null(result.Error);
        Assert.Equal(InspectVerdict.NG, result.Verdict);
        Assert.NotNull(result.Classes);
        Assert.Equal(new[] { (0, "Scratch", SegAiClassKind.Defect), (1, "Dent", SegAiClassKind.Defect), (2, "Good", SegAiClassKind.Good) },
            result.Classes!.Select(c => (c.ClassIndex, c.Name, c.Kind)).ToArray());
        Assert.Equal(new[] { 0, 1, 2 }, result.Suggestions.Select(s => s.ClassIndex).ToArray());
    }

    [Fact]
    public void TwoClassInspect_KeepsNgOkResultWithoutClassList()
    {
        using var mask = new Mat(64, 64, MatType.CV_8UC1, Scalar.All(0));
        Cv2.Rectangle(mask, new OpenCvSharp.Rect(10, 10, 20, 20), Scalar.All(255), -1);
        SegAiResult native = SegAiSuggestionBuilderTests.ResultOf(mask, new[] { 0.8f });
        var service = new InspectService(
            () => false,
            path => ("m1", "s1"),
            path => (native, 64, 64),
            () => (0.5f, 1),
            () => new DateTime(2026, 10, 10, 0, 0, 0, DateTimeKind.Utc));

        InspectResult result = service.Run("a.png");

        Assert.Equal(InspectVerdict.NG, result.Verdict);
        Assert.Null(result.Classes);
        Assert.Equal(1, result.Regions);
        Assert.Equal(new[] { 0 }, result.Suggestions.Select(s => s.ClassIndex).ToArray());
    }

    [Fact]
    public void NamedModel_AutoLabel_UsesPerClassPredictionAndTableNames_NativeGated()
    {
        string? model = ClassModel("n3.segai");
        if (model == null) return;
        SegAiClassTable table = SegAiModelFile.ReadClassTable(model, out _)!;
        using SegAiSession session = OpenNative("p2h-native", model);
        session.SaveClassTable(table);
        TrainFixtures.WriteImage(session.ProjectRoot, "a.png", 1, 320, 240);

        (SegAiResult result, _, _) = session.PredictForAutoLabel("a.png", AutoLabelService.FloorThreshold);
        Assert.Null(result.Error);
        Assert.NotNull(result.PerClass);

        var service = new AutoLabelService(session);
        AutoLabelImageResult run = service.Run("a.png");
        Assert.Null(run.Error);
        string[] names = table.Classes.Select(c => c.Name).ToArray();
        Assert.All(run.Suggestions, s => Assert.InRange(s.ClassIndex, 0, names.Length - 1));
        Assert.Equal(run.Suggestions.Count, service.Accept("a.png", run.Suggestions));
        string[] expected = run.Suggestions.Select(s => names[s.ClassIndex]).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        string[] written = session.LoadForEdit("a.png").Document.Shapes.Select(s => s.Label ?? string.Empty).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        Assert.Equal(expected, written);
    }

    [Fact]
    public void NamedModel_TableMismatch_IsRejectedWithClearMessage_NativeGated()
    {
        string? model = ClassModel("n3.segai");
        if (model == null) return;
        SegAiClassTable table = SegAiModelFile.ReadClassTable(model, out _)!;
        var renamed = new SegAiClassTable(table.Classes
            .Select((c, i) => i == 0 ? new SegAiClassEntry("Renamed", c.Kind, c.Enabled) : c).ToList());
        using SegAiSession session = OpenNative("p2h-mismatch", model);
        session.SaveClassTable(renamed);
        TrainFixtures.WriteImage(session.ProjectRoot, "a.png", 1, 320, 240);

        AutoLabelImageResult run = new AutoLabelService(session).Run("a.png");
        Assert.NotNull(run.Error);
        Assert.Contains("do not match", run.Error);
        Assert.Empty(run.Suggestions);

        InspectResult inspected = new InspectService(session).Run(Path.Combine(session.ProjectRoot, "a.png"));
        Assert.Equal(InspectVerdict.Error, inspected.Verdict);
        Assert.Contains("do not match", inspected.Error);
    }

    [Fact]
    public void LegacyModel_NgOkProject_KeepsTwoClassPredictForAutoLabel_NativeGated()
    {
        string? model = ClassModel("v3-source.segai");
        if (model == null) return;
        using SegAiSession session = OpenNative("p2h-legacy", model);
        TrainFixtures.WriteImage(session.ProjectRoot, "a.png", 1, 320, 240);

        (SegAiResult result, _, _) = session.PredictForAutoLabel("a.png", AutoLabelService.FloorThreshold);
        Assert.Null(result.Error);
        Assert.Null(result.PerClass);

        AutoLabelImageResult run = new AutoLabelService(session).Run("a.png");
        Assert.Null(run.Error);
        Assert.All(run.Suggestions, s => Assert.InRange(s.ClassIndex, 0, 1));
    }
}
