using EasyLabel.SegAI.Engine;
using EasyLabel.SegAI.Model;
using OpenCvSharp;

namespace EasyLabel.SegAI.Tests;

/// <summary>
/// P2c interop against a native build that has the class API. Opt-in: set EASYAI_SEGAI_DIR to the native folder and
/// EASYAI_CLASS_MODELS to a folder holding n3.segai and v3-source.segai (artifacts/EAI/EAI-CLS/p1/s3/t7out); skipped otherwise.
/// </summary>
public sealed class ClassApiInteropTests
{
    private static string? ModelDir => Environment.GetEnvironmentVariable("EASYAI_CLASS_MODELS");

    private static bool Available()
    {
        string? dir = ModelDir;
        if (string.IsNullOrEmpty(dir) || !File.Exists(Path.Combine(dir, "n3.segai"))) return false;
        try { return SegAiInferer.ClassApiVersion >= 1; }
        catch (Exception) { return false; }
    }

    [Fact]
    public void NamedClassModel_TableAndPredictMulti_AreConsistent()
    {
        if (!Available()) return;
        using var inferer = new SegAiInferer();
        Assert.Equal(0, inferer.SetGpu(false));
        Assert.Equal(0, inferer.Load(Path.Combine(ModelDir!, "n3.segai")));

        SegAiClassTable? table = inferer.GetClassTable();
        Assert.NotNull(table);
        Assert.True(table!.Classes.Count >= 3);

        using var image = new Mat(240, 320, MatType.CV_8UC3, new Scalar(90, 90, 90));
        SegAiMultiPredictResult result = inferer.PredictMulti(image, new OpenCvSharp.Rect(0, 0, 320, 240), 0.5f, table.Classes.Count);
        Assert.Equal(320 * 240, result.Label.Length);
        Assert.Equal(result.Label.Length, result.Score.Length);
        Assert.Equal(table.Classes.Count, result.ClassFractions.Length);
        Assert.All(result.Label, l => Assert.True(l <= table.Classes.Count));
    }

    [Fact]
    public void LegacyModel_ReportsSynthesizedNgOkTable()
    {
        if (!Available() || !File.Exists(Path.Combine(ModelDir!, "v3-source.segai"))) return;
        using var inferer = new SegAiInferer();
        Assert.Equal(0, inferer.SetGpu(false));
        Assert.Equal(0, inferer.Load(Path.Combine(ModelDir!, "v3-source.segai")));
        SegAiClassTable? table = inferer.GetClassTable();
        Assert.NotNull(table);
        Assert.Equal(new[] { "NG", "OK" }, table!.Classes.Select(c => c.Name).ToArray());
    }

    [Fact]
    public void GetClassTable_WithoutModel_ReturnsNull()
    {
        if (!Available()) return;
        using var inferer = new SegAiInferer();
        Assert.Null(inferer.GetClassTable());
    }
}
