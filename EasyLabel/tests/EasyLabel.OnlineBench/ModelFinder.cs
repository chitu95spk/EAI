using System.Drawing;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EasyLabel.SegAI;
using EasyLabel.SegAI.Data;
using EasyLabel.SegAI.Engine;
using EasyLabel.SegAI.Native;
using EasyLabel.SegAI.Options;
using EasyLabel.SegAI.Training;
using OpenCvSharp;

namespace EasyLabel.OnlineBench;

internal static class ModelFinder
{
    public static string Resolve(string root, Options options, List<string> notes)
    {
        NativeSegAiLibrary.EnsureLoaded();
        if (options.Model != null)
        {
            using var explicitModel = new SegAiInferer(options.Model);
            return options.Model;
        }
        if (options.SelfTest) return TrainMinimal(root, options, notes);
        foreach (string model in Candidates(root, options.Dataset).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                using var inferer = new SegAiInferer(model);
                notes.Add("Selected first loadable .segai; load checked, prediction compatibility checked by each measured configuration: " + model);
                return model;
            }
            catch (Exception error) { notes.Add("Rejected model " + model + ": " + error.GetBaseException().Message); }
        }
        return TrainMinimal(root, options, notes);
    }

    private static IEnumerable<string> Candidates(string root, string dataset)
    {
        string models = Path.Combine(dataset, ".easyai", "models");
        string registry = Path.Combine(models, "registry.json");
        string? active = null;
        if (File.Exists(registry))
        {
            using JsonDocument json = JsonDocument.Parse(File.ReadAllText(registry));
            if (json.RootElement.TryGetProperty("active", out JsonElement value)) active = value.GetString();
        }
        if (active != null && Path.GetFileName(active) == active && File.Exists(Path.Combine(models, active))) yield return Path.Combine(models, active);
        foreach (string file in Paths.Files(dataset, "*.segai")) yield return file;
        // Exact candidates used by SegAiFixtures.ResolveAny in InfererSmokeTests.
        foreach (string relative in new[] { "tmp/ToolUpgrade/SegAI-fixtures/synthetic.segai", "artifacts/V133/v2-both/model.segai", "artifacts/V135/baseline/scan/scan-1.segai" })
        {
            string file = Path.Combine(root, relative);
            if (File.Exists(file)) yield return file;
        }
        foreach (string folder in Directory.EnumerateDirectories(Path.Combine(root, "tmp"), "EAI*"))
            foreach (string file in Paths.Files(folder, "*.segai")) yield return file;
        foreach (string folder in new[] { "EasyLabel", "Models", "artifacts" })
            foreach (string file in Paths.Files(Path.Combine(root, folder), "*.segai")) yield return file;
    }

    public static IEnumerable<string> OnnxCandidates(string root, Options options)
    {
        if (options.Onnx != null) return [options.Onnx];
        string? configured = Environment.GetEnvironmentVariable("EASYAI_CUSTOM_ONNX");
        if (!string.IsNullOrWhiteSpace(configured)) return [Path.GetFullPath(configured)];
        IEnumerable<string> roots = new[] { options.Dataset, Path.Combine(root, "EasyLabel", "tests"), Path.Combine(root, "Models") }
            .Concat(Directory.EnumerateDirectories(Path.Combine(root, "tmp"), "EAI*"));
        // A generic classifier/backbone/OCR graph is not the production YOLO detect path.
        return roots.SelectMany(r => Paths.Files(r, "*.onnx"))
            .Where(f => File.Exists(Path.ChangeExtension(f, null) + ".classes.txt") || Path.GetFileName(f).Contains("yolo", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static string TrainMinimal(string root, Options options, List<string> notes)
    {
        string cache = options.DatasetSpecified
            ? Paths.Scratch(root, "model", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(options.Dataset))).Substring(0, 16))
            : Paths.Scratch(root, "model");
        string cached = Path.Combine(cache, "minimal-v1.segai");
        string description = "BENCHMARK-ONLY minimal V1 structural model: SegAiSession.SetAnnotations/SetMode/Train (same real native backend as SegAiTrainingServiceTests), "
            + "3 real PNGs resized to 64x64, first 2 Train/third Val, 2 synthetic NG/OK square labels per image, 8 trees/depth 4/min sample 2. "
            + "If fewer than 3 source images exist they are reused; validation quality is not evidence. No neural network input, full 64x64 training ROI. "
            + "Source dataset: " + options.Dataset + "; cache: " + cache + "; unsuitable for a production model sizing decision.";
        if (File.Exists(cached))
        {
            try { using var loaded = new SegAiInferer(cached); notes.Add("Reused " + description); return cached; }
            catch (Exception error) { notes.Add("Cached minimal model rejected: " + error.GetBaseException().Message); }
        }
        string[] images = Paths.Files(options.Dataset, "*.png").Take(3).ToArray();
        if (images.Length == 0) throw new FileNotFoundException("Fallback training needs a real PNG in " + options.Dataset + ".");
        string project = Paths.Scratch(root, "model", "training-" + Guid.NewGuid().ToString("N"));
        for (int i = 0; i < 3; i++)
        {
            using Mat source = Cv2.ImRead(images[i % images.Length]);
            using var small = new Mat();
            Cv2.Resize(source, small, new OpenCvSharp.Size(64, 64));
            if (!Cv2.ImWrite(Path.Combine(project, $"sample-{i}.png"), small)) throw new IOException("Cannot write minimal training image.");
        }
        using SegAiSession session = SegAiSession.Create(project);
        for (int i = 0; i < 3; i++)
        {
            session.SetAnnotations($"sample-{i}.png", new[]
            {
                new SegAnnotation { Label = 1, Points = new[] { new PointF(4,4), new PointF(28,4), new PointF(28,28), new PointF(4,28) } },
                new SegAnnotation { Label = 2, Points = new[] { new PointF(36,36), new PointF(60,36), new PointF(60,60), new PointF(36,60) } }
            });
            session.SetMode($"sample-{i}.png", i == 2 ? SegTrainingMode.Val : SegTrainingMode.Train);
        }
        var inference = new SegInferenceSettings { NumTrees = 8, MaxDepth = 4, MinSample = 2, MinDefectArea = 1, EnableGpu = false };
        SegAiTrainResult trained = session.Train(new SegAiTrainRequest { Options = new SegTrainingOptions { Version = 1 }, Inference = inference }, null, null, CancellationToken.None);
        if (!trained.Success || trained.CandidatePath == null) throw new InvalidOperationException("Minimal production-API training failed: " + trained.Message);
        // Preserve a failed older cache rather than overwriting it.
        if (File.Exists(cached)) cached = Path.Combine(cache, "minimal-v1-" + Guid.NewGuid().ToString("N") + ".segai");
        File.Copy(trained.CandidatePath, cached, false);
        notes.Add(description);
        return cached;
    }
}
