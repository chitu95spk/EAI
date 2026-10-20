using EasyLabel.SegAI.AutoLabel;
using EasyLabel.SegAI.Inspection;
using EasyLabel.SegAI.Model;
using OpenCvSharp;

namespace EasyLabel.SegAI.Tests;

/// <summary>
/// P2e named-class prediction against a native build with the class API. Opt-in like <see cref="ClassApiInteropTests"/>
/// (EASYAI_SEGAI_DIR + EASYAI_CLASS_MODELS); returns early when not configured.
/// </summary>
public sealed class PredictClassesTests
{
    private static string? Model(string file)
    {
        string? dir = Environment.GetEnvironmentVariable("EASYAI_CLASS_MODELS");
        if (string.IsNullOrEmpty(dir) || Environment.GetEnvironmentVariable("EASYAI_SEGAI_DIR") == null) return null;
        string path = Path.Combine(dir, file);
        return File.Exists(path) ? path : null;
    }

    private static SegAiSession Open(string name, string model)
    {
        string folder = Path.Combine(SegAiFixtures.ScratchFolder("predictclasses"), name + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var session = SegAiSession.Create(folder);
        session.Inference.EnableGpu = false;
        Assert.Null(session.LoadModel(model).Problem);
        return session;
    }

    [Fact]
    public void NamedModel_ReturnsOnePlanePerEnabledClass()
    {
        string? model = Model("n3.segai");
        if (model == null) return;
        using SegAiSession session = Open("n3", model);
        using var image = new Mat(240, 320, MatType.CV_8UC3, new Scalar(90, 90, 90));
        SegAiResult result = session.PredictClasses(image);
        Assert.Null(result.Error);
        Assert.NotNull(result.PerClass);
        Assert.True(result.PerClass!.Count >= 3);
        Assert.All(result.PerClass, p => Assert.Equal(320 * 240, p.Mask.Length));
        Assert.Equal(result.PerClass.Select(p => p.ClassId).OrderBy(i => i), result.PerClass.Select(p => p.ClassId));
        Assert.Equal(result.IsOK, result.PerClass.All(p => p.Kind != SegAiClassKind.Defect || p.Regions.Count == 0));
        IReadOnlyList<SegAiSuggestion> suggestions = SegAiSuggestionBuilder.BuildClasses("a.png", result, 320, 240, 1);
        Assert.All(suggestions, s => Assert.InRange(s.ClassIndex, 0, result.PerClass.Count));
    }

    [Fact]
    public void LegacyModel_ReturnsNgAndOkPlanes()
    {
        string? model = Model("v3-source.segai");
        if (model == null) return;
        using SegAiSession session = Open("legacy", model);
        using var image = new Mat(240, 320, MatType.CV_8UC3, new Scalar(90, 90, 90));
        SegAiResult result = session.PredictClasses(image);
        Assert.Null(result.Error);
        Assert.Equal(new[] { "NG", "OK" }, result.PerClass!.Select(p => p.Name).ToArray());
    }
}
