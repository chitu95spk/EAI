using EasyLabel.Ai.Onnx;

namespace EasyLabel.Ai.Onnx.Tests;

public class ModelFilesTests : IDisposable
{
    private readonly string _root = Path.Combine(AppContext.BaseDirectory, "onnx-files-" + Guid.NewGuid().ToString("N"));

    public ModelFilesTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private string Touch(params string[] names)
    {
        foreach (string n in names) File.WriteAllText(Path.Combine(_root, n), "x");
        return _root;
    }

    [Fact]
    public void Sam2NeedsOneEncoderAndOneDecoder()
    {
        Touch("sam2.1_tiny.encoder.onnx");
        Assert.False(AiModelFiles.Resolve(AiModelFiles.Sam2Id, _root).Found);
        Assert.Contains("decoder", AiModelFiles.Resolve(AiModelFiles.Sam2Id, _root).Reason);

        Touch("sam2.1_tiny.decoder.onnx");
        AiModelResolution ok = AiModelFiles.Resolve(AiModelFiles.Sam2Id, _root);
        Assert.True(ok.Found);
        Assert.EndsWith("sam2.1_tiny.encoder.onnx", ok.EncoderPath);
        Assert.EndsWith("sam2.1_tiny.decoder.onnx", ok.DecoderPath);

        Touch("other.decoder.onnx");
        Assert.False(AiModelFiles.Resolve(AiModelFiles.Sam2Id, _root).Found);
    }

    [Fact]
    public void YoloWorldNeedsExactlyOneOnnx()
    {
        Assert.False(AiModelFiles.Resolve(AiModelFiles.YoloWorldId, _root).Found);
        Touch("yolov8s-worldv2.onnx", "yolov8s-worldv2.classes.txt");
        AiModelResolution ok = AiModelFiles.Resolve(AiModelFiles.YoloWorldId, _root);
        Assert.True(ok.Found);
        Assert.EndsWith("yolov8s-worldv2.onnx", ok.ModelPath);

        Touch("second.onnx");
        Assert.False(AiModelFiles.Resolve(AiModelFiles.YoloWorldId, _root).Found);
    }

    [Fact]
    public void MissingFolderOrUnknownIdIsAMissNotAnException()
    {
        Assert.False(AiModelFiles.Resolve(AiModelFiles.Sam2Id, Path.Combine(_root, "nope")).Found);
        Assert.False(AiModelFiles.Resolve(AiModelFiles.Sam2Id, "").Found);
        Assert.False(AiModelFiles.Resolve("other", _root).Found);
    }

    [Fact]
    public void ImageValidatesItsBuffer()
    {
        Assert.Throws<ArgumentException>(() => new AiImage(new byte[5], 2, 1, "k"));
        var image = new AiImage(new byte[6], 2, 1, "k");
        Assert.Equal("k", image.CacheKey);
    }
}
