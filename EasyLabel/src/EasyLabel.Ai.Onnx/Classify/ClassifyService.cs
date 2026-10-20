using System.Diagnostics;
using System.Runtime.InteropServices;
using EasyLabel.Core.Classify;
using OpenCvSharp;

namespace EasyLabel.Ai.Onnx.Classify;

/// <summary>Phase of a classifier run: reading and extracting images, fitting heads, saving, or predicting.</summary>
public enum ClassifyStage
{
    Reading = 0,
    Fitting = 1,
    Saving = 2,
    Predicting = 3,
}

/// <summary>Progress report. <see cref="Done"/> of <see cref="Total"/> steps of <see cref="Stage"/> are finished.</summary>
public sealed record ClassifyProgress(ClassifyStage Stage, int Done, int Total);

/// <summary>One marked image of a training run. <see cref="FullPath"/> is read; <see cref="RelativePath"/> identifies it in the split.</summary>
public sealed record ClassifyTrainImage(string FullPath, string RelativePath, bool IsNg);

/// <summary>
/// Training request. <see cref="ClassifyFolder"/> is <c>ProjectPaths.ClassifyDir</c> (the model and its <c>cache</c> folder).
/// <see cref="LongSide"/>, <see cref="Threads"/> and <see cref="AllowGpu"/> come from <c>RecommendedSettings</c> (for
/// <c>HardwareTask.ClassifyTrain</c>). <see cref="ModelsRoot"/> and <see cref="NativeFolder"/> locate the backbone.
/// </summary>
public sealed record ClassifyTrainRequest(
    IReadOnlyList<ClassifyTrainImage> Images,
    string ClassifyFolder,
    string ModelName,
    string? ModelsRoot,
    string? NativeFolder,
    int LongSide,
    int Threads,
    bool AllowGpu);

/// <summary>
/// Outcome of a training run. <see cref="Metrics"/> come from held-out predictions (<see cref="MetricsBasis"/> names the scheme);
/// the saved model is fitted on every marked image.
/// </summary>
public sealed record ClassifyTrainResult(
    string ModelPath,
    ClassifyMetrics Metrics,
    string MetricsBasis,
    bool LeaveOneOut,
    int OkCount,
    int NgCount,
    int FeaturesFromCache,
    int FeaturesExtracted,
    TimeSpan FeatureTime,
    TimeSpan HeadTime,
    string Provider);

/// <summary>One image to classify.</summary>
public sealed record ClassifyImageRef(string FullPath, string RelativePath);

/// <summary>P(NG) of one image. Map it with <c>ImageUncertaintyCalculator.FromClassProbability</c> and <see cref="ClassifyMetrics.IsNg"/>.</summary>
public sealed record ClassifyPrediction(string RelativePath, double PNg);

/// <summary>Prediction request. The model's own long side and backbone hash are used. <see cref="CacheFolder"/> may be null.</summary>
public sealed record ClassifyPredictRequest(
    string ModelPath,
    IReadOnlyList<ClassifyImageRef> Images,
    string? ModelsRoot,
    string? NativeFolder,
    int Threads,
    bool AllowGpu,
    string? CacheFolder);

/// <summary>
/// Prediction outcome. When <see cref="Status"/> is not Ok (for example <see cref="ClassifyModelStatus.BackboneMismatch"/>),
/// <see cref="Predictions"/> is empty and <see cref="Message"/> says why.
/// </summary>
public sealed record ClassifyPredictResult(ClassifyModelStatus Status, string Message, IReadOnlyList<ClassifyPrediction> Predictions);

/// <summary>
/// OK/NG classifier runs: feature extraction (backbone + disk cache), held-out evaluation, the final head and the <c>.eaicls</c> save;
/// and prediction with a saved model. Work runs on a thread-pool thread; cancellation stops between images and inside an inference.
/// Throws <see cref="AiModelException"/> when the backbone is missing or unusable, and <see cref="ArgumentException"/> when the
/// marks do not allow a split (see <see cref="ClassifyDataset.MinPerClass"/>).
/// </summary>
public static class ClassifyService
{
    /// <summary>Trains and saves an OK/NG model. Progress is reported per image, per fitted head and once for the save.</summary>
    public static Task<ClassifyTrainResult> TrainAsync(ClassifyTrainRequest request, IProgress<ClassifyProgress>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Task.Run(() => Train(request, progress, ct), ct);
    }

    /// <summary>Predicts P(NG) for each image with a saved model, after checking the model against the installed backbone.</summary>
    public static Task<ClassifyPredictResult> PredictAsync(ClassifyPredictRequest request, IProgress<ClassifyProgress>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Task.Run(() => Predict(request, progress, ct), ct);
    }

    private static ClassifyTrainResult Train(ClassifyTrainRequest request, IProgress<ClassifyProgress>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request.Images);
        ValidateRuntime(request.LongSide, request.Threads);

        IReadOnlyList<ClassifyTrainImage> images = request.Images;
        ClassifyPlan plan = ClassifyDataset.Plan(images.Select(i => new ClassifySample(i.RelativePath, i.IsNg)));
        string backbonePath = RequireBackbone(request.ModelsRoot, request.NativeFolder);

        using ClassifyBackbone backbone = ClassifyBackbone.Open(backbonePath, request.Threads, request.AllowGpu);
        var cache = new FeatureCache(Path.Combine(request.ClassifyFolder, "cache"));

        var features = new float[images.Count][];
        int fromCache = 0;
        var featureWatch = Stopwatch.StartNew();
        for (int i = 0; i < images.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            features[i] = FeaturesOf(backbone, cache, images[i].FullPath, request.LongSide, ct, out bool hit);
            if (hit) fromCache++;
            progress?.Report(new ClassifyProgress(ClassifyStage.Reading, i + 1, images.Count));
        }

        featureWatch.Stop();
        var indexOf = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < images.Count; i++) indexOf[images[i].RelativePath] = i;

        var headWatch = Stopwatch.StartNew();
        var heldTruth = new List<bool>();
        var heldPNg = new List<double>();
        int steps = plan.Splits.Count + 1;
        int done = 0;
        foreach (ClassifySplit split in plan.Splits)
        {
            ct.ThrowIfCancellationRequested();
            var trainX = new List<float[]>(split.Train.Count);
            var trainY = new List<bool>(split.Train.Count);
            foreach (ClassifySample sample in split.Train)
            {
                trainX.Add(features[indexOf[sample.RelativePath]]);
                trainY.Add(sample.IsNg);
            }

            ClassifyHead fold = ClassifyHead.Train(trainX, trainY, ClassifyHead.DefaultLambda, ct);
            foreach (ClassifySample sample in split.Validation)
            {
                heldTruth.Add(sample.IsNg);
                heldPNg.Add(fold.PredictNgProbability(features[indexOf[sample.RelativePath]]));
            }

            progress?.Report(new ClassifyProgress(ClassifyStage.Fitting, ++done, steps));
        }

        var allX = new List<float[]>(features);
        var allY = images.Select(i => i.IsNg).ToList();
        ClassifyHead head = ClassifyHead.Train(allX, allY, ClassifyHead.DefaultLambda, ct);
        progress?.Report(new ClassifyProgress(ClassifyStage.Fitting, steps, steps));
        headWatch.Stop();

        ClassifyMetrics metrics = ClassifyMetrics.Compute(heldTruth, heldPNg);
        string basis = plan.LeaveOneOut ? ClassifyModelFile.MetricsBasisLeaveOneOut : ClassifyModelFile.MetricsBasisHeldOut;
        int ngCount = allY.Count(v => v);
        int okCount = allY.Count - ngCount;

        progress?.Report(new ClassifyProgress(ClassifyStage.Saving, 0, 1));
        var document = new ClassifyModelDocument(
            request.ModelName,
            DateTime.UtcNow,
            Path.GetFileName(backbone.FilePath),
            backbone.Sha256,
            ClassifyModelFile.FeatureSpecV1,
            ClassifyBackbone.FeatureCount,
            request.LongSide,
            head,
            metrics,
            basis,
            okCount,
            ngCount);
        string modelPath = ClassifyModelFile.Write(request.ClassifyFolder, document);
        progress?.Report(new ClassifyProgress(ClassifyStage.Saving, 1, 1));

        return new ClassifyTrainResult(
            modelPath,
            metrics,
            basis,
            plan.LeaveOneOut,
            okCount,
            ngCount,
            fromCache,
            images.Count - fromCache,
            featureWatch.Elapsed,
            headWatch.Elapsed,
            backbone.Provider);
    }

    private static ClassifyPredictResult Predict(ClassifyPredictRequest request, IProgress<ClassifyProgress>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request.Images);
        if (request.Threads < 1) throw new ArgumentOutOfRangeException(nameof(request), "At least one thread is needed.");

        string backbonePath = RequireBackbone(request.ModelsRoot, request.NativeFolder);
        using ClassifyBackbone backbone = ClassifyBackbone.Open(backbonePath, request.Threads, request.AllowGpu);
        ClassifyModelLoad loaded = ClassifyModelFile.Load(request.ModelPath, backbone.Sha256);
        if (loaded.Status != ClassifyModelStatus.Ok || loaded.Document == null)
            return new ClassifyPredictResult(loaded.Status, loaded.Message, Array.Empty<ClassifyPrediction>());

        ClassifyModelDocument document = loaded.Document;
        var cache = new FeatureCache(request.CacheFolder);
        var predictions = new List<ClassifyPrediction>(request.Images.Count);
        for (int i = 0; i < request.Images.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            ClassifyImageRef image = request.Images[i];
            float[] feature = FeaturesOf(backbone, cache, image.FullPath, document.InputLongSide, ct, out _);
            predictions.Add(new ClassifyPrediction(image.RelativePath, document.Head.PredictNgProbability(feature)));
            progress?.Report(new ClassifyProgress(ClassifyStage.Predicting, i + 1, request.Images.Count));
        }

        return new ClassifyPredictResult(ClassifyModelStatus.Ok, string.Empty, predictions);
    }

    private static float[] FeaturesOf(ClassifyBackbone backbone, FeatureCache cache, string path, int longSide, CancellationToken ct, out bool fromCache)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Image file not found: " + path, path);
        var info = new FileInfo(path);
        string key = FeatureCache.MakeKey(path, info.Length, info.LastWriteTimeUtc, backbone.Sha256, longSide);
        if (cache.TryGet(key, out float[]? cached) && cached != null)
        {
            fromCache = true;
            return cached;
        }

        fromCache = false;
        float[] feature = backbone.Extract(ReadImage(path), longSide, ct);
        cache.Put(key, feature);
        return feature;
    }

    private static AiImage ReadImage(string path)
    {
        using Mat decoded = Cv2.ImDecode(File.ReadAllBytes(path), ImreadModes.Color | ImreadModes.IgnoreOrientation);
        if (decoded.Empty()) throw new IOException("Could not decode image: " + path);
        using Mat continuous = decoded.Clone();
        int length = checked(continuous.Width * continuous.Height * 3);
        var pixels = new byte[length];
        Marshal.Copy(continuous.Data, pixels, 0, length);
        return new AiImage(pixels, continuous.Width, continuous.Height, path);
    }

    private static string RequireBackbone(string? modelsRoot, string? nativeFolder)
    {
        return ClassifyBackbone.Locate(modelsRoot, nativeFolder)
            ?? throw new AiModelException("The OK/NG base model (" + ClassifyBackbone.FileName + ") is not installed.");
    }

    private static void ValidateRuntime(int longSide, int threads)
    {
        if (longSide < ClassifyBackbone.MinLongSide || longSide > ClassifyBackbone.MaxLongSide || longSide % ClassifyBackbone.Multiple != 0)
            throw new ArgumentOutOfRangeException(nameof(longSide), "The long side must be a multiple of 32 from 32 to 1536.");
        if (threads < 1) throw new ArgumentOutOfRangeException(nameof(threads), "At least one thread is needed.");
    }
}
