// Cloned from BeeCore/Unit/SegmentAI.TrainingEvaluation.cs@d1201fa3d (EvaluateTrainingModel :862-892 inferer setup) and
// BeeCore/Unit/SegmentAI.Inspection.cs ApplyObjectScale. The interfaces are the seam that lets the training logic run in
// unit tests without the native trainer.
using EasyLabel.SegAI.Data;
using EasyLabel.SegAI.Engine;
using EasyLabel.SegAI.Native;
using EasyLabel.SegAI.Options;
using EasyLabel.SegAI.Model;
using OpenCvSharp;

namespace EasyLabel.SegAI.Training;

/// <summary>One native trainer instance. Members map 1:1 to <see cref="SegAiTrainer"/>.</summary>
internal interface ISegAiTrainerSession : IDisposable
{
    string LastError { get; }

    void ConfigureV2(string settingsPath);

    void UseStructuralModel(int detail);

    int SetRoi(int x, int y, int width, int height);

    int AddSample(Mat bgr, Mat mask);

    /// <summary>Names the classes of the model to save (V2, after ConfigureV2 and before the first AddSample); 0 = ok.</summary>
    int SetClassTable(SegAiClassTable table);

    /// <param name="cancelFlag">Native int* owned by the caller; a non-zero value asks the trainer to stop.</param>
    int Train(int numTrees, int maxDepth, int minSampleCount, Action<int>? onProgress, IntPtr cancelFlag);

    int Save(string path, float defectThreshold, uint minDefectArea);
}

/// <summary>Per-instance labels of the instance head (Position projects). Separate from the session so test fakes need no change; the native trainer implements it.</summary>
internal interface ISegAiInstanceTrainerSession
{
    /// <summary>Attaches the instances to the sample added last; 0 = ok.</summary>
    int AddInstances(IReadOnlyList<SegInstance> instances);
}

/// <summary>Everything EvaluateTrainingModel configures on its inferer, in the EasyVision order.</summary>
internal sealed class SegAiEvaluatorConfig
{
    public string ModelPath { get; init; } = string.Empty;

    public int Version { get; init; }

    public bool Gpu { get; init; }

    /// <summary>V1 only.</summary>
    public bool HighSpeed { get; init; }

    /// <summary>V1 only.</summary>
    public float AngleStep { get; init; }

    /// <summary>V1 only.</summary>
    public float AngleRange { get; init; } = 180;

    /// <summary>V2 only: the foreground gate already corrected for a legacy prior.</summary>
    public float EffectiveForeground { get; init; }

    public SegTrainingOptions Options { get; init; } = new();

    /// <summary>Named classes of the model under evaluation; null for the two-class NG/OK path.</summary>
    public SegAiClassTable? ClassTable { get; init; }
}

internal interface ISegAiEvaluator : IDisposable
{
    /// <summary>Predicts with threshold 0 over <paramref name="roi"/>. May throw InvalidOperationException ("No learned..." / "NG and OK examples...").</summary>
    SegAiPredictResult? Predict(Mat bgr, OpenCvSharp.Rect roi);

    /// <summary>Named-class prediction with threshold 0 (class API); label plane = class id per pixel, score plane 0..255.</summary>
    SegAiMultiPredictResult? PredictMulti(Mat bgr, OpenCvSharp.Rect roi, int classCount);
}

internal interface ISegAiTrainBackend
{
    /// <summary>Folder-derived default of the V2 backbone, or null when it cannot be derived (native library not loaded).</summary>
    string? DefaultBackbonePath();

    ISegAiTrainerSession CreateTrainer();

    /// <summary>Loads the file into a fresh CPU inferer; null when it loads, otherwise an error text. Never throws for a bad model.</summary>
    string? CheckLoad(string modelPath);

    /// <summary>Loads the model into a fresh inferer and configures it. Throws InvalidDataException when the model does not load.</summary>
    ISegAiEvaluator OpenEvaluator(SegAiEvaluatorConfig config);
}

/// <summary>The real backend: <see cref="SegAiTrainer"/> and <see cref="SegAiInferer"/>.</summary>
internal sealed class SegAiNativeTrainBackend : ISegAiTrainBackend
{
    public static readonly SegAiNativeTrainBackend Instance = new();

    private SegAiNativeTrainBackend()
    {
    }

    public string? DefaultBackbonePath()
    {
        string? folder = NativeSegAiLibrary.LoadedFolder;
        if (folder == null)
        {
            NativeSegAiLoadResult result = NativeSegAiLibrary.TryLoad(new NativeSegAiOptions(null, Array.Empty<string>()));
            folder = result.IsLoaded ? NativeSegAiLibrary.LoadedFolder : null;
        }

        return folder == null ? null : Path.Combine(folder, "Models", "DefectV2", "mobilenetv2-12.onnx");
    }

    public ISegAiTrainerSession CreateTrainer() => new NativeTrainer(new SegAiTrainer());

    public string? CheckLoad(string modelPath) => Model.SegAiRegistry.NativeLoadCheck(modelPath);

    public ISegAiEvaluator OpenEvaluator(SegAiEvaluatorConfig config)
    {
        var engine = new SegAiInferer();
        try
        {
            if (engine.Load(config.ModelPath) != 0) throw new InvalidDataException("Cannot open model for validation. " + engine.LastError);
            engine.SetGpu(config.Gpu);
            engine.SetHighSpeed(config.Version == 1 && config.HighSpeed);
            if (config.Version == 1) engine.SetAngles(config.AngleStep, config.AngleRange);
            else engine.SetV2Options(config.EffectiveForeground, config.Options.MinimumRegionRatio, config.Options.Morphology);
            bool v2 = config.Version == 2;
            SegObjectScaleRange scale = config.Options.ScaleFor(v2 ? 2 : 1);
            scale.Validate();
            engine.SetObjectScale(scale.OffScale ? 1f : scale.Minimum, scale.OffScale ? 1f : scale.Maximum,
                v2 && !scale.OffScale && config.Options.V2MultiScaleDetection, v2);
            if (v2) engine.SetV2BoundaryMode(config.Options.V2WinnerForeground);
            return new NativeEvaluator(engine);
        }
        catch
        {
            engine.Dispose();
            throw;
        }
    }

    private sealed class NativeTrainer : ISegAiTrainerSession, ISegAiInstanceTrainerSession
    {
        private readonly SegAiTrainer _trainer;

        public NativeTrainer(SegAiTrainer trainer)
        {
            _trainer = trainer;
        }

        public string LastError => _trainer.LastError;

        public void ConfigureV2(string settingsPath) => _trainer.ConfigureV2(settingsPath);

        public void UseStructuralModel(int detail) => _trainer.UseStructuralModel(detail);

        public int SetRoi(int x, int y, int width, int height) => _trainer.SetRoi(x, y, width, height);

        public int AddSample(Mat bgr, Mat mask) => _trainer.AddSample(bgr, mask);

        public int AddInstances(IReadOnlyList<SegInstance> instances) => _trainer.AddInstances(instances);

        public int SetClassTable(SegAiClassTable table) => _trainer.SetClassTable(table);

        public int Train(int numTrees, int maxDepth, int minSampleCount, Action<int>? onProgress, IntPtr cancelFlag)
            => _trainer.Train(numTrees, maxDepth, minSampleCount, onProgress, cancelFlag);

        public int Save(string path, float defectThreshold, uint minDefectArea) => _trainer.Save(path, defectThreshold, minDefectArea);

        public void Dispose() => _trainer.Dispose();
    }

    private sealed class NativeEvaluator : ISegAiEvaluator
    {
        private readonly SegAiInferer _engine;

        public NativeEvaluator(SegAiInferer engine)
        {
            _engine = engine;
        }

        public SegAiPredictResult? Predict(Mat bgr, OpenCvSharp.Rect roi) => _engine.Predict(bgr, roi, 0f);

        public SegAiMultiPredictResult? PredictMulti(Mat bgr, OpenCvSharp.Rect roi, int classCount) => _engine.PredictMulti(bgr, roi, 0f, classCount);

        public void Dispose() => _engine.Dispose();
    }
}
