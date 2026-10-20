using System.Runtime.InteropServices;
using System.Text;
using EasyLabel.SegAI.Data;
using EasyLabel.SegAI.Engine;
using EasyLabel.SegAI.Model;
using EasyLabel.SegAI.Options;
using EasyLabel.SegAI.Training;
using Newtonsoft.Json.Linq;
using OpenCvSharp;

namespace EasyLabel.SegAI.Tests;

/// <summary>Writes a tiny but structurally valid version-3 .segai (passes SegAiModelFile.Inspect; not loadable by the native runtime).</summary>
internal static class ModelStub
{
    public static void Write(string path, string note = "stub")
    {
        byte[] payload = Encoding.UTF8.GetBytes("%YAML:1.0\nnative_v2: 1\nside: 320\nactive_classes: 7\npostprocess_version: 2\nnote: \"" + note + "\"\n");
        var header = new byte[72];
        byte[] magic = { 83, 69, 71, 65, 73, 0, 0, 0 };
        Array.Copy(magic, header, 8);
        BitConverter.GetBytes(3u).CopyTo(header, 8);
        BitConverter.GetBytes(0x02000000u).CopyTo(header, 12);
        BitConverter.GetBytes(3u).CopyTo(header, 16);
        BitConverter.GetBytes(448u).CopyTo(header, 20);
        BitConverter.GetBytes(1u).CopyTo(header, 28);
        BitConverter.GetBytes((ulong)payload.Length).CopyTo(header, 56);
        BitConverter.GetBytes(SegAiModelFile.PayloadCrc(payload)).CopyTo(header, 64);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = File.Create(path);
        stream.Write(header, 0, header.Length);
        stream.Write(payload, 0, payload.Length);
    }

    /// <summary>A file long enough for the metadata writers but with a bad signature, so Inspect rejects it.</summary>
    public static void WriteInvalid(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[200]);
    }
}

internal sealed class FakeEvaluator : ISegAiEvaluator
{
    private readonly Func<Mat, int, SegAiMultiPredictResult>? _multi;

    public FakeEvaluator(Func<Mat, int, SegAiMultiPredictResult>? multi = null)
    {
        _multi = multi;
    }

    public SegAiMultiPredictResult? PredictMulti(Mat bgr, OpenCvSharp.Rect roi, int classCount) => _multi?.Invoke(bgr, classCount);

    public SegAiPredictResult? Predict(Mat bgr, OpenCvSharp.Rect roi)
    {
        int length = bgr.Width * bgr.Height;
        return new SegAiPredictResult
        {
            Width = bgr.Width,
            Height = bgr.Height,
            Mask = new byte[length],
            Confidence = new byte[length],
            NormalConfidence = new byte[length],
            Score = 0,
        };
    }

    public void Dispose()
    {
    }
}

internal sealed class FakeTrainer : ISegAiTrainerSession
{
    private readonly FakeTrainBackend _backend;
    private string _work = string.Empty;

    public FakeTrainer(FakeTrainBackend backend)
    {
        _backend = backend;
    }

    public string LastError => _backend.TrainError;

    public SegAiClassTable? ClassTable => _backend.ClassTable;

    public int SetClassTable(SegAiClassTable table)
    {
        _backend.ClassTable = table;
        return 0;
    }

    public void ConfigureV2(string settingsPath)
    {
        _work = Path.GetDirectoryName(settingsPath)!;
        string json = File.ReadAllText(settingsPath);
        _backend.SettingsJson = json;
        JObject settings = JObject.Parse(json);
        if (settings["resumeFrom"] != null) File.WriteAllText(Path.Combine(_work, "resume.txt"), "resumed; head continued\n");
        if (settings["priorBalance"] != null) File.WriteAllText(Path.Combine(_work, "prior.txt"), "balanced\n");
    }

    public void UseStructuralModel(int detail)
    {
        _backend.StructuralCalls++;
    }

    public int SetRoi(int x, int y, int width, int height) => 0;

    public int AddSample(Mat bgr, Mat mask)
    {
        _backend.AddSampleCalls++;
        return 0;
    }

    public int Train(int numTrees, int maxDepth, int minSampleCount, Action<int>? onProgress, IntPtr cancelFlag)
    {
        _backend.TrainCalls++;
        _backend.TrainHook?.Invoke(cancelFlag);
        onProgress?.Invoke(0);
        onProgress?.Invoke(50);
        onProgress?.Invoke(100);
        if (cancelFlag != IntPtr.Zero && Marshal.ReadInt32(cancelFlag) != 0) return 1;
        if (_backend.TrainResult != 0) return _backend.TrainResult;
        if (_work.Length > 0)
        {
            File.WriteAllText(Path.Combine(_work, "epochs.csv"),
                "epoch,seconds,train_loss,val_loss,lr,train_rows,train_iou,val_iou\n" +
                "1,0.5,0.9,0.8,0.01,10,0.2,0.1\n" +
                "2,0.5,0.7,0.6,0.01,10,0.4,0.3\n");
        }

        return 0;
    }

    public int Save(string path, float defectThreshold, uint minDefectArea)
    {
        if (_backend.WriteInvalidModel) ModelStub.WriteInvalid(path);
        else ModelStub.Write(path);
        return 0;
    }

    public void Dispose()
    {
    }
}

internal sealed class FakeTrainBackend : ISegAiTrainBackend
{
    public string? BackbonePath { get; set; }

    public int CreateTrainerCalls;
    public int StructuralCalls;
    public int AddSampleCalls;
    public int TrainCalls;
    public int TrainResult;
    public string TrainError = string.Empty;
    public bool WriteInvalidModel;
    public string? LoadError;
    public string? SettingsJson;
    public EasyLabel.SegAI.Model.SegAiClassTable? ClassTable;
    public Action<IntPtr>? TrainHook;

    public string? DefaultBackbonePath() => BackbonePath;

    public ISegAiTrainerSession CreateTrainer()
    {
        Interlocked.Increment(ref CreateTrainerCalls);
        return new FakeTrainer(this);
    }

    public string? CheckLoad(string modelPath) => LoadError;

    public Func<Mat, int, SegAiMultiPredictResult>? MultiFactory;

    public ISegAiEvaluator OpenEvaluator(SegAiEvaluatorConfig config) => new FakeEvaluator(MultiFactory);
}

internal static class TrainFixtures
{
    public static string Folder(string name)
    {
        string folder = Path.Combine(SegAiFixtures.ScratchFolder("training"), name + "-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(folder);
        return folder;
    }

    /// <summary>A stand-in backbone file (the fake trainer never reads it; the pipeline only checks that it exists).</summary>
    public static string Backbone(string folder)
    {
        string path = Path.Combine(folder, "mobilenetv2-12.onnx");
        File.WriteAllBytes(path, new byte[] { 1, 2, 3, 4 });
        return path;
    }

    /// <summary>Writes a distinct solid-colour PNG (identical pixels would be grouped into one validation group).</summary>
    public static string WriteImage(string folder, string name, int seed, int width = 64, int height = 64)
    {
        string path = Path.Combine(folder, name);
        using var image = new Mat(height, width, MatType.CV_8UC3, new Scalar(20 + seed * 17 % 200, 60 + seed * 31 % 150, 90 + seed * 13 % 120));
        Cv2.ImEncode(".png", image, out byte[] png);
        File.WriteAllBytes(path, png);
        return path;
    }

    public static SegAnnotation Square(byte label, float x0 = 8, float y0 = 8, float x1 = 40, float y1 = 40)
    {
        return new SegAnnotation
        {
            Label = label,
            Points = new[] { new System.Drawing.PointF(x0, y0), new System.Drawing.PointF(x1, y0), new System.Drawing.PointF(x1, y1), new System.Drawing.PointF(x0, y1) },
        };
    }

    /// <summary>A sample over a freshly written image; labels null or empty = unlabelled.</summary>
    public static SegSample Sample(string folder, string name, int seed, byte mode, params byte[] labels)
    {
        string path = WriteImage(folder, name, seed);
        var sample = new SegSample
        {
            ImagePath = path,
            OriginalName = name,
            TrainValMode = mode,
            Annotations = new List<SegAnnotation>(),
        };
        for (int i = 0; i < labels.Length; i++) sample.Annotations.Add(Square(labels[i], 4 + i * 30, 4, 28 + i * 30, 30));
        return sample;
    }

    public static SegAiTrainJob Job(string folder, FakeTrainBackend backend, IReadOnlyList<SegSample> samples, SegTrainingOptions? options = null,
        CancellationToken token = default, Action<int>? progress = null, Action<SegEpochMetric>? epochs = null, bool retrain = false,
        string? resume = null, string? backbone = "default")
    {
        return new SegAiTrainJob
        {
            RunsDir = Path.Combine(folder, ".easyai", "runs"),
            Registry = new SegAiRegistry(Path.Combine(folder, ".easyai", "models")),
            Backend = backend,
            Samples = samples,
            Options = options ?? new SegTrainingOptions { Version = 2, Epochs = 5, InputSize = 320 },
            Inference = new SegInferenceSettings(),
            Retrain = retrain,
            ResumeSource = resume,
            BackbonePath = backbone == "default" ? Backbone(folder) : backbone,
            Progress = progress,
            EpochSink = epochs,
            Token = token,
        };
    }
}
