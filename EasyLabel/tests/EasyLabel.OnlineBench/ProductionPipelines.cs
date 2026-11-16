using System.Reflection;
using System.Runtime.InteropServices;
using EasyLabel.SegAI;
using EasyLabel.SegAI.Engine;
using EasyLabel.SegAI.Inspection;
using EasyLabel.SegAI.Model;
using EasyLabel.SegAI.Native;
using EasyLabel.SegAI.Options;
using EasyLabel.Ai.Onnx;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;

namespace EasyLabel.OnlineBench;

internal interface IPipeline : IDisposable
{
    string Name { get; }
    string Model { get; }
    string InputDescription { get; }
    string ThreadDescription { get; }
    string Gpu { get; }
    string Roi { get; }
    string Device { get; }
    string RoiRect { get; }
    object Prepare(Mat input);
    object Infer(object prepared);
    object Postprocess(object output);
    SplitTiming? CaptureSplitTimings();
    IReadOnlyDictionary<string, double> Diagnostics();
}

internal sealed record SplitTiming(bool Available, int ApiVersion, double BackboneMs, double HeadMs, double InferMs, string Reason);

internal sealed class SegPipeline : IPipeline
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private delegate DefectRegionAnalysis Analyze(SegAiPredictResult result, Mat? valid, float threshold, int minimum, int effective, out float[]? scores);
    private delegate DefectRegionAnalysis? AnalyzeNormal(SegAiPredictResult result, Mat? valid, float threshold, int minimum, out byte[]? mask, out float[]? scores);
    private readonly SegAiSession _session;
    private readonly SegAiInferer _inferer;
    private readonly Func<Mat, Mat> _prepare;
    private readonly Action<SegTrainingOptions, SegInferenceSettings> _configure;
    private readonly Analyze _analyze;
    private readonly AnalyzeNormal _normal;
    private readonly bool _instances;
    private readonly SegAiModelInfo _info;
    private readonly bool _gpu;
    private readonly Func<IntPtr, int>? _backend;
    private readonly IntPtr _nativeHandle;
    private Rect _lastRoi;
    private int _lastWidth, _lastHeight;
    public string Name => _instances ? "segai-instances" : "segai-mask";
    public string Model { get; }
    public string InputDescription { get; }
    public string Gpu => _gpu ? "on" : "off";
    public string Roi { get; }
    public string Device { get; private set; } = "unverified (no prediction yet)";
    public string RoiRect => RoiSelection.Describe(_lastRoi);
    public string ThreadDescription => $"SegAI has no supported inference-thread setter; affinity cap only. OpenCV threads set separately. EnableGpu={_gpu}; mask backend checked after every call.";

    public SegPipeline(string root, string model, string worker, bool gpu, string roi, bool maskExperiment)
    {
        Model = model;
        _gpu = gpu;
        Roi = roi;
        // Same NativeSegAiOptions/default resolver as NativeSegAiFact and InfererSmokeTests.
        NativeSegAiLoadResult load = NativeSegAiLibrary.TryLoad(new NativeSegAiOptions(null, Array.Empty<string>()));
        if (!NativeSegAiLibrary.IsLoaded) throw new InvalidOperationException("Native SegAI load failed: " + load);
        _session = SegAiSession.Create(Paths.Scratch(root, "sessions", worker + "-" + Guid.NewGuid().ToString("N")));
        try
        {
            _session.Inference.EnableGpu = gpu;
            _info = _session.LoadModel(model);
            if (_info.Problem != null) throw new InvalidOperationException(_info.Problem.En);
            object predictor = typeof(SegAiSession).GetField("_predictor", Hidden)!.GetValue(_session)
                ?? throw new InvalidOperationException("Production session did not load a predictor.");
            Type type = predictor.GetType();
            _inferer = (SegAiInferer)type.GetField("_inferer", Hidden)!.GetValue(predictor)!;
            // Preserve the original default path. Explicit P3a experiments compare the same mask Predict API on both devices.
            _instances = _inferer.HasInstanceHead && !maskExperiment;
            if (!_instances)
            {
                Type native = typeof(SegAiInferer).Assembly.GetType("EasyLabel.SegAI.Interop.SegAiInfererNative", true)!;
                _backend = native.GetMethod("SEGAI_InferGetLastBackend", BindingFlags.Static | BindingFlags.NonPublic)!
                    .CreateDelegate<Func<IntPtr, int>>();
                _nativeHandle = (IntPtr)typeof(SegAiInferer).GetMethod("Handle", Hidden)!.Invoke(_inferer, null)!;
                // Unlike UsedGpu's legacy false fallback, this fails if device reporting is unavailable.
                _ = _backend(_nativeHandle);
            }
            // Bind existing production implementations once. No copied preprocessing/postprocessing algorithms.
            _prepare = type.GetMethod("AcquireInferenceInput", Hidden)!.CreateDelegate<Func<Mat, Mat>>(predictor);
            _configure = type.GetMethod("ConfigureInferer", Hidden)!.CreateDelegate<Action<SegTrainingOptions, SegInferenceSettings>>(predictor);
            _analyze = type.GetMethod("AnalyzePrediction", BindingFlags.Static | BindingFlags.NonPublic)!.CreateDelegate<Analyze>();
            _normal = type.GetMethod("AnalyzeNormalPrediction", BindingFlags.Static | BindingFlags.NonPublic)!.CreateDelegate<AnalyzeNormal>();
            string head = SegAiModelFile.ReadPayloadHead(model) ?? "";
            int? side = SegAiModelFile.ReadYamlInt(head, "side");
            InputDescription = $"format={_info.FormatVersion}; network side={side?.ToString() ?? "not exposed (pixel/structural model)"}; "
                + $"ROI mode={roi}; EnableGpu={gpu}; native resize/normalise stays inside infer; host prepare is production BGR conversion/view/clone; "
                + $"instanceHead={_instances}; threshold={_session.Inference.DefectThreshold}; minArea={_session.Inference.MinDefectArea}; "
                + $"native={NativeSegAiFolderResolver.Resolve(new NativeSegAiOptions(null, Array.Empty<string>())).Folder}";
        }
        catch { _session.Dispose(); throw; }
    }

    public object Prepare(Mat input) => _prepare(input);

    public object Infer(object prepared) => InferAt(prepared, null);

    internal object InferAt(object prepared, Rect? roiOverride)
    {
        var bgr = (Mat)prepared;
        _configure(_session.Options, _session.Inference);
        Rect roi = roiOverride ?? RoiSelection.Select(Roi, bgr.Width, bgr.Height);
        _lastRoi = roi; _lastWidth = bgr.Width; _lastHeight = bgr.Height;
        if (_instances)
        {
            Device = "CPU requested; legacy instance API does not report actual device";
            return _inferer.PredictInstances(bgr, roi, _session.Inference.DefectThreshold, EasyLabel.SegAI.Data.SegInstanceQuality.Performance);
        }
        SegAiPredictResult output = _inferer.Predict(bgr, roi, _session.Inference.DefectThreshold);
        int backend = _backend!(_nativeHandle);
        if (backend is not (0 or 1) || output.UsedGpu != (backend == 1))
            throw new InvalidOperationException("Native actual-device reporting is invalid or inconsistent.");
        Device = output.UsedGpu
            ? (_info.IsV2 ? (_session.Inference.GpuBackend == SegGpuBackend.NvidiaCuda ? "NVIDIA GPU / CUDA (ONNX Runtime)" : "Intel GPU / OpenVINO") : "GPU / OpenCL matching") + " (UsedGpu=true; physical adapter ID not exported)"
            : "CPU (UsedGpu=false)";
        if (output.UsedGpu != _gpu)
            throw new InvalidOperationException($"Requested gpu={Gpu}, actual={Device}. Requested device unavailable; refusing fallback samples.");
        return output;
    }

    public object Postprocess(object output)
    {
        if (_instances) return output; // Production PredictInstances already returns original-image positions (native postprocess bundled).
        var result = (SegAiPredictResult)output;
        var settings = _session.Inference;
        using Mat? valid = ValidMask(result);
        DefectRegionAnalysis ng = _analyze(result, valid, settings.DefectThreshold, settings.MinDefectArea,
            Math.Max(settings.MinDefectArea, Math.Max(1, _info.TrainedMinimumArea ?? 1)), out float[]? scores);
        DefectRegionAnalysis? ok = _normal(result, valid, settings.DefectThreshold, settings.MinDefectArea, out _, out float[]? normalScores);
        return new { result.Width, result.Height, result.Score, Regions = ng.Regions, Scores = scores, NormalRegions = ok?.Regions, NormalScores = normalScores };
    }

    public SplitTiming? CaptureSplitTimings()
    {
        if (_instances)
            return new SplitTiming(false, 0, double.NaN, double.NaN, double.NaN, "the legacy instance Predict API has no native mask split timing");
        int apiVersion = SegAiInferer.SplitTimingApiVersion;
        if (apiVersion <= 0)
            return new SplitTiming(false, apiVersion, double.NaN, double.NaN, double.NaN, "the loaded native DLL does not expose the split timing API");
        double[]? values = _inferer.GetNativeSplitTimings();
        if (values == null)
            return new SplitTiming(false, apiVersion, double.NaN, double.NaN, double.NaN, "the native diagnostic returned no last-call split timings; EASYAI_SEGAI_SPLIT_TIMING must be set before worker startup and model compilation");
        if (values.Length != SegAiInferer.SplitTimingCount || values.Any(value => !double.IsFinite(value) || value < 0))
            return new SplitTiming(false, apiVersion, double.NaN, double.NaN, double.NaN, "the native diagnostic returned invalid split timing values");
        return new SplitTiming(true, apiVersion, values[0], values[1], values[2], string.Empty);
    }

    private Mat? ValidMask(SegAiPredictResult result)
    {
        if (_lastRoi == new Rect(0, 0, _lastWidth, _lastHeight) || result.Width != _lastWidth || result.Height != _lastHeight) return null;
        var valid = new Mat(_lastHeight, _lastWidth, MatType.CV_8UC1, Scalar.All(0));
        try { using var inside = new Mat(valid, _lastRoi); inside.SetTo(Scalar.All(255)); return valid; }
        catch { valid.Dispose(); throw; }
    }

    public IReadOnlyDictionary<string, double> Diagnostics()
    {
        var values = new Dictionary<string, double>();
        if (_instances) return values;
        double[]? native = _inferer.GetNativePhaseTimings();
        if (native != null)
            for (int i = 0; i < native.Length; i++)
                if (double.IsFinite(native[i]) && native[i] >= 0) values["native." + SegAiInferer.PhaseTimingNames[i]] = native[i];
        values["managed.native_invoke"] = _inferer.LastPredictInvokeMs;
        values["managed.mask_copy"] = _inferer.LastMaskCopyMs;
        values["managed.confidence_read"] = _inferer.LastConfidenceReadMs;
        values["managed.normal_read"] = _inferer.LastNormalReadMs;
        values["managed.free"] = _inferer.LastFreeMs;
        return values;
    }

    public void Dispose() => _session.Dispose();
}

internal sealed class YoloPipeline : IPipeline
{
    private sealed record Prepared(float[] Pixels, object Box, int Width, int Height);
    private sealed record Output(float[] Pixels, int[] Dimensions, Prepared Input);
    private readonly OnnxSession _session;
    private readonly string _input, _output;
    private readonly int _classes;
    private readonly Type _letterbox;
    private readonly MethodInfo _preprocess, _decode;
    public string Name => "onnx-yolo";
    public string Model { get; }
    public string InputDescription => "1x3x640x640 float RGB; production Letterbox/YoloDecoder/Nms; CPU EP; confidence=.25, NMS=.5";
    public string ThreadDescription { get; }
    public string Gpu => "off";
    public string Roi => "whole";
    public string Device => "CPU EP";
    public string RoiRect => "whole frame";

    public YoloPipeline(string model, int threads)
    {
        Model = model;
        _session = OnnxSessionFactory.Create(model, preferGpu: false, intraOpThreads: threads);
        try
        {
            if (_session.InputMetadata.Count != 1 || _session.OutputMetadata.Count != 1) throw new NotSupportedException("Only the production single-input/output YOLO detect contract is supported.");
            _input = _session.InputMetadata.Keys.Single();
            _output = _session.OutputMetadata.Keys.Single();
            int[] shape = _session.InputMetadata[_input].Dimensions;
            if (_session.InputMetadata[_input].ElementType != typeof(float) || shape.Length != 4 ||
                (shape[0] > 0 && shape[0] != 1) || (shape[1] > 0 && shape[1] != 3) ||
                (shape[2] > 0 && shape[2] != 640) || (shape[3] > 0 && shape[3] != 640))
                throw new NotSupportedException("Model does not accept the production YOLO 1x3x640x640 float input.");
            Assembly assembly = typeof(YoloWorldBackend).Assembly;
            Type namesType = assembly.GetType("EasyLabel.Ai.Onnx.YoloNames", true)!;
            string[]? names = null;
            if (_session.CustomMetadata.TryGetValue("names", out string? text))
                names = (string[]?)namesType.GetMethod("ParseUltralytics")!.Invoke(null, [text]);
            string classesFile = Path.ChangeExtension(model, null) + ".classes.txt";
            if (names == null && File.Exists(classesFile))
                names = (string[]?)namesType.GetMethod("ParseLines")!.Invoke(null, [File.ReadAllLines(classesFile)]);
            _classes = names?.Length ?? throw new NotSupportedException("Production YOLO class metadata/sidecar is missing.");
            int[] dims = _session.OutputMetadata[_output].Dimensions;
            if (dims.Length != 3 || (dims[1] > 0 && dims[1] != _classes + 4)) throw new NotSupportedException("Output is not production YOLO detect [1,4+classes,N].");
            _letterbox = assembly.GetType("EasyLabel.Ai.Onnx.Letterbox", true)!;
            Type decoder = assembly.GetType("EasyLabel.Ai.Onnx.YoloDecoder", true)!;
            _preprocess = decoder.GetMethod("Preprocess")!;
            _decode = decoder.GetMethod("Decode")!;
            ThreadDescription = $"OnnxSessionFactory CPU EP intraOpThreads={threads}; sequential execution; affinity cap={threads}";
        }
        catch { _session.Dispose(); throw; }
    }

    public object Prepare(Mat input)
    {
        using var bgr = new Mat();
        if (input.Channels() == 1) Cv2.CvtColor(input, bgr, ColorConversionCodes.GRAY2BGR);
        else input.CopyTo(bgr);
        byte[] bytes = new byte[checked(bgr.Width * bgr.Height * 3)];
        Marshal.Copy(bgr.Data, bytes, 0, bytes.Length);
        object box = Activator.CreateInstance(_letterbox, bgr.Width, bgr.Height, 640)!;
        float[] pixels = (float[])_preprocess.Invoke(null, [bytes, bgr.Width, bgr.Height, box])!;
        return new Prepared(pixels, box, bgr.Width, bgr.Height);
    }

    public object Infer(object prepared)
    {
        var input = (Prepared)prepared;
        var tensor = new DenseTensor<float>(input.Pixels, new[] { 1, 3, 640, 640 });
        using var result = _session.Run(new[] { NamedOnnxValue.CreateFromTensor(_input, tensor) }, new[] { _output }, CancellationToken.None);
        Tensor<float> output = result.Single(r => r.Name == _output).AsTensor<float>();
        int[] dims = output.Dimensions.ToArray();
        if (dims.Length != 3 || dims[0] != 1 || dims[1] != _classes + 4) throw new InvalidDataException("Unexpected production YOLO output shape.");
        return new Output(output.ToArray(), dims, input); // Output copy/disposal is included in infer, as in production.
    }

    public object Postprocess(object output)
    {
        var value = (Output)output;
        var raw = (List<BoxDetection>)_decode.Invoke(null, [value.Pixels, value.Dimensions[1], value.Dimensions[2], _classes,
            value.Input.Box, value.Input.Width, value.Input.Height, .25f, null])!;
        return Nms.Apply(raw, .5f);
    }

    public SplitTiming? CaptureSplitTimings() => null;
    public IReadOnlyDictionary<string, double> Diagnostics() => new Dictionary<string, double>();
    public void Dispose() => _session.Dispose();
}
