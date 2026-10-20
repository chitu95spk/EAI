using EasyLabel.Ai.Onnx;
using EasyLabel.Ai.Onnx.Classify;

namespace EasyLabel.Ai.Onnx.Tests;

public sealed class ClassifyBackboneTests : IDisposable
{
    private readonly string _root = Path.Combine(AppContext.BaseDirectory, "classify-backbone-" + Guid.NewGuid().ToString("N"));

    public ClassifyBackboneTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private static string RealModelsRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EasyAI", "models");

    [Fact]
    public void Locate_prefers_the_models_root_then_the_native_folder()
    {
        string models = Path.Combine(_root, "models");
        string native = Path.Combine(_root, "native");
        Assert.Null(ClassifyBackbone.Locate(models, native));

        string nativeFile = Path.Combine(native, "Models", "DefectV2", ClassifyBackbone.FileName);
        Directory.CreateDirectory(Path.GetDirectoryName(nativeFile)!);
        File.WriteAllText(nativeFile, "x");
        Assert.Equal(nativeFile, ClassifyBackbone.Locate(models, native));

        string modelsFile = Path.Combine(models, ClassifyBackbone.FolderName, ClassifyBackbone.FileName);
        Directory.CreateDirectory(Path.GetDirectoryName(modelsFile)!);
        File.WriteAllText(modelsFile, "x");
        Assert.Equal(modelsFile, ClassifyBackbone.Locate(models, native));
    }

    [Fact]
    public void Missing_file_is_an_ai_model_exception()
    {
        Assert.Throws<AiModelException>(() => ClassifyBackbone.Open(Path.Combine(_root, "missing.onnx"), 1));
    }

    [Fact]
    public void Sidecar_naming_another_output_hash_refuses_the_backbone_before_loading()
    {
        string file = Path.Combine(_root, ClassifyBackbone.FileName);
        File.WriteAllText(file, "not an onnx model");
        File.WriteAllText(Path.Combine(_root, ClassifyBackbone.SidecarName), "{\"outputSha256\":\"" + new string('0', 64) + "\"}");
        Assert.Throws<AiModelException>(() => ClassifyBackbone.Open(file, 1));
    }

    [Fact]
    public void Damaged_sidecar_is_an_ai_model_exception()
    {
        string file = Path.Combine(_root, ClassifyBackbone.FileName);
        File.WriteAllText(file, "not an onnx model");
        File.WriteAllText(Path.Combine(_root, ClassifyBackbone.SidecarName), "{not json");
        Assert.Throws<AiModelException>(() => ClassifyBackbone.Open(file, 1));
    }

    [Fact]
    public void Input_size_keeps_the_aspect_ratio_on_multiples_of_32()
    {
        Assert.Equal((448, 352), ClassifyBackbone.InputSize(640, 480, 448));
        Assert.Equal((32, 320), ClassifyBackbone.InputSize(100, 1000, 320));
        Assert.Equal((32, 32), ClassifyBackbone.InputSize(10, 10, 32));
    }

    [Fact]
    public void Real_backbone_gives_a_finite_deterministic_feature_vector()
    {
        string? path = ClassifyBackbone.Locate(RealModelsRoot, null);
        if (path == null) return; // The backbone is not installed on this machine: nothing to check.

        using ClassifyBackbone backbone = ClassifyBackbone.Open(path, 2);
        Assert.Equal(64, backbone.Sha256.Length);

        byte[] pixels = new byte[120 * 80 * 3];
        new Random(7).NextBytes(pixels);
        var image = new AiImage(pixels, 120, 80, "synthetic");

        float[] first = backbone.Extract(image, 224);
        float[] second = backbone.Extract(image, 224);
        Assert.Equal(ClassifyBackbone.FeatureCount, first.Length);
        Assert.All(first, v => Assert.True(float.IsFinite(v)));
        Assert.Equal(first, second);
    }

    [Fact]
    public void Extract_refuses_a_long_side_that_is_not_a_multiple_of_32()
    {
        string? path = ClassifyBackbone.Locate(RealModelsRoot, null);
        if (path == null) return;

        using ClassifyBackbone backbone = ClassifyBackbone.Open(path, 1);
        var image = new AiImage(new byte[64 * 64 * 3], 64, 64, "tiny");
        Assert.Throws<ArgumentOutOfRangeException>(() => backbone.Extract(image, 450));
    }
}
