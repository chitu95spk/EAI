using EasyLabel.Ai.Onnx;
using EasyLabel.Ai.Onnx.Classify;
using EasyLabel.Core.Classify;
using OpenCvSharp;

namespace EasyLabel.Ai.Onnx.Tests;

public sealed class ClassifyServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(AppContext.BaseDirectory, "classify-service-" + Guid.NewGuid().ToString("N"));

    public ClassifyServiceTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private static string RealModelsRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EasyAI", "models");

    private List<ClassifyTrainImage> FakeImages(int ok, int ng)
    {
        var list = new List<ClassifyTrainImage>();
        for (int i = 0; i < ok; i++) list.Add(new ClassifyTrainImage(Path.Combine(_root, $"ok{i}.png"), $"ok{i}.png", false));
        for (int i = 0; i < ng; i++) list.Add(new ClassifyTrainImage(Path.Combine(_root, $"ng{i}.png"), $"ng{i}.png", true));
        return list;
    }

    private ClassifyTrainRequest Request(IReadOnlyList<ClassifyTrainImage> images, string? modelsRoot, int longSide = 224) => new ClassifyTrainRequest(
        images,
        Path.Combine(_root, "classify"),
        "okng",
        modelsRoot,
        null,
        longSide,
        2,
        false);

    [Fact]
    public async Task Missing_backbone_is_refused_before_any_image_is_read()
    {
        string empty = Path.Combine(_root, "no-models");
        await Assert.ThrowsAsync<AiModelException>(() => ClassifyService.TrainAsync(Request(FakeImages(2, 2), empty)));
    }

    [Fact]
    public async Task One_class_is_refused_by_the_split_rules()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => ClassifyService.TrainAsync(Request(FakeImages(3, 1), Path.Combine(_root, "none"))));
    }

    [Fact]
    public async Task Long_side_must_be_a_multiple_of_32()
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => ClassifyService.TrainAsync(Request(FakeImages(2, 2), Path.Combine(_root, "none"), 450)));
    }

    [Fact]
    public async Task Predict_without_a_backbone_is_refused()
    {
        var predict = new ClassifyPredictRequest(
            Path.Combine(_root, "okng_v001.eaicls"),
            new[] { new ClassifyImageRef(Path.Combine(_root, "a.png"), "a.png") },
            Path.Combine(_root, "none"),
            null,
            1,
            false,
            null);
        await Assert.ThrowsAsync<AiModelException>(() => ClassifyService.PredictAsync(predict));
    }

    [Fact]
    public async Task Train_then_predict_end_to_end_when_the_backbone_is_installed()
    {
        if (ClassifyBackbone.Locate(RealModelsRoot, null) == null) return; // Not installed here: nothing to run.

        var images = new List<ClassifyTrainImage>();
        for (int i = 0; i < 3; i++)
        {
            string ok = Path.Combine(_root, $"ok{i}.png");
            string ng = Path.Combine(_root, $"ng{i}.png");
            using (var dark = new Mat(96, 128, MatType.CV_8UC3, Scalar.All(40 + i * 5)))
                Cv2.ImWrite(ok, dark);
            using (var bright = new Mat(96, 128, MatType.CV_8UC3, Scalar.All(200 - i * 5)))
            {
                Cv2.Rectangle(bright, new OpenCvSharp.Rect(10, 10, 40, 40), Scalar.All(0), -1);
                Cv2.ImWrite(ng, bright);
            }

            images.Add(new ClassifyTrainImage(ok, $"ok{i}.png", false));
            images.Add(new ClassifyTrainImage(ng, $"ng{i}.png", true));
        }

        ClassifyTrainResult result = await ClassifyService.TrainAsync(Request(images, RealModelsRoot));
        Assert.True(result.LeaveOneOut);
        Assert.Equal(3, result.OkCount);
        Assert.Equal(3, result.NgCount);
        Assert.True(File.Exists(result.ModelPath));
        Assert.Equal(ClassifyModelFile.MetricsBasisLeaveOneOut, result.MetricsBasis);

        using (ClassifyBackbone installed = ClassifyBackbone.Open(ClassifyBackbone.Locate(RealModelsRoot, null)!, 1))
        {
            Assert.Equal(ClassifyModelStatus.Ok, ClassifyModelFile.Load(result.ModelPath, installed.Sha256).Status);
            Assert.Equal(ClassifyModelStatus.BackboneMismatch, ClassifyModelFile.Load(result.ModelPath, new string('0', 64)).Status);
        }

        var predict = new ClassifyPredictRequest(
            result.ModelPath,
            images.Select(i => new ClassifyImageRef(i.FullPath, i.RelativePath)).ToList(),
            RealModelsRoot,
            null,
            2,
            false,
            Path.Combine(_root, "classify", "cache"));
        ClassifyPredictResult prediction = await ClassifyService.PredictAsync(predict);
        Assert.Equal(ClassifyModelStatus.Ok, prediction.Status);
        Assert.Equal(images.Count, prediction.Predictions.Count);
        Assert.All(prediction.Predictions, p => Assert.InRange(p.PNg, 0.0, 1.0));
    }
}
