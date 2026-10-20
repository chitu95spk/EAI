using System.Diagnostics;
using System.Globalization;
using System.Text;
using EasyLabel.SegAI.Data;
using EasyLabel.SegAI.Model;
using EasyLabel.SegAI.Native;
using EasyLabel.SegAI.Options;
using EasyLabel.SegAI.Training;
using Newtonsoft.Json.Linq;
using OpenCvSharp;

namespace EasyLabel.SegAI.Tests;

/// <summary>
/// EAI-PLUS S5 step 0: opt-in wall-clock baseline of one real Segment AI V2 training on an external LabelMe dataset.
/// Skips unless EASYAI_RUN_TRAIN=1 and EASYAI_TRAIN_DATASET (folder with images\*.png and json\*.json) are set.
/// Measurement only: it never changes training defaults.
/// </summary>
public sealed class SegAiRealDatasetTimingTests
{
    private sealed class DatasetFactAttribute : FactAttribute
    {
        public DatasetFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("EASYAI_RUN_TRAIN") != "1")
            {
                Skip = "Real native training is opt-in: set EASYAI_RUN_TRAIN=1.";
                return;
            }

            string? dataset = Environment.GetEnvironmentVariable("EASYAI_TRAIN_DATASET");
            if (string.IsNullOrWhiteSpace(dataset) || !Directory.Exists(Path.Combine(dataset, "images")))
            {
                Skip = "Set EASYAI_TRAIN_DATASET to a folder containing images\\ and json\\.";
                return;
            }

            NativeSegAiResolution resolution = NativeSegAiFolderResolver.Resolve(new NativeSegAiOptions(null, Array.Empty<string>()));
            if (resolution.Folder is null) Skip = NativeSegAiFolderResolver.DllName + " not found (set EASYAI_SEGAI_DIR).";
        }
    }

    /// <summary>Forwards to the real native backend; after Save it copies stage_times.csv and epochs.csv out of the work folder the pipeline deletes.</summary>
    private sealed class CapturingBackend : ISegAiTrainBackend
    {
        private readonly string _capture;

        public CapturingBackend(string capture) { _capture = capture; }

        public string? DefaultBackbonePath() => SegAiNativeTrainBackend.Instance.DefaultBackbonePath();

        public ISegAiTrainerSession CreateTrainer() => new CapturingTrainer(SegAiNativeTrainBackend.Instance.CreateTrainer(), _capture);

        public string? CheckLoad(string modelPath) => SegAiNativeTrainBackend.Instance.CheckLoad(modelPath);

        public ISegAiEvaluator OpenEvaluator(SegAiEvaluatorConfig config) => SegAiNativeTrainBackend.Instance.OpenEvaluator(config);
    }

    private sealed class CapturingTrainer : ISegAiTrainerSession
    {
        private readonly ISegAiTrainerSession _inner;
        private readonly string _capture;

        public CapturingTrainer(ISegAiTrainerSession inner, string capture) { _inner = inner; _capture = capture; }

        public string LastError => _inner.LastError;

        public void ConfigureV2(string settingsPath) => _inner.ConfigureV2(settingsPath);

        public int SetClassTable(EasyLabel.SegAI.Model.SegAiClassTable table) => _inner.SetClassTable(table);

        public void UseStructuralModel(int detail) => _inner.UseStructuralModel(detail);

        public int SetRoi(int x, int y, int width, int height) => _inner.SetRoi(x, y, width, height);

        public int AddSample(Mat bgr, Mat mask) => _inner.AddSample(bgr, mask);

        public int Train(int numTrees, int maxDepth, int minSampleCount, Action<int>? onProgress, IntPtr cancelFlag)
            => _inner.Train(numTrees, maxDepth, minSampleCount, onProgress, cancelFlag);

        public int Save(string path, float defectThreshold, uint minDefectArea)
        {
            int rc = _inner.Save(path, defectThreshold, minDefectArea);
            string? work = Path.GetDirectoryName(path);
            if (work != null)
            {
                Directory.CreateDirectory(_capture);
                foreach (string name in new[] { "stage_times.csv", "epochs.csv" })
                {
                    string source = Path.Combine(work, name);
                    if (File.Exists(source)) File.Copy(source, Path.Combine(_capture, name), true);
                }
            }

            return rc;
        }

        public void Dispose() => _inner.Dispose();
    }

    private static string ReadOrNote(string path) => File.Exists(path) ? File.ReadAllText(path) : "(missing: " + path + ")";

    [DatasetFact]
    public async Task RealDataset_V2Training_WritesStageTimingReport()
    {
        string dataset = Environment.GetEnvironmentVariable("EASYAI_TRAIN_DATASET")!;
        string runName = Environment.GetEnvironmentVariable("EASYAI_RUN_NAME") is { Length: > 0 } envRun ? envRun : "baseline";
        int epochs = int.TryParse(Environment.GetEnvironmentVariable("EASYAI_TRAIN_EPOCHS"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int e) && e > 0 ? e : 20;
        int classes = int.TryParse(Environment.GetEnvironmentVariable("EASYAI_TRAIN_CLASSES"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int c) && c > 0 ? c : 1;
        // The managed pipeline only rasterizes and trains labels 1 (NG) and 2 (OK): SegLabelRaster/LoadOrBuildMask drop other ids and the
        // native head is K = 3. Fail loudly rather than silently training fewer classes than requested.
        if (classes > 2) throw new InvalidOperationException("EASYAI_TRAIN_CLASSES=" + classes + " is not supported: SegAiTrainingPipeline trains labels 1..2 only (K=3).");
        string root = SegAiFixtures.RepoRoot() ?? throw new InvalidOperationException("Repository root not found; the report must stay inside the project.");

        // Fixed order (ordinal file name), every 5th image (index 4, 9, ...) is Val, the rest Train. The native trainer seeds its RNG internally.
        string[] images = Directory.GetFiles(Path.Combine(dataset, "images"), "*.png").OrderBy(p => p, StringComparer.Ordinal).ToArray();
        var samples = new List<SegSample>();
        int skipped = 0;
        for (int i = 0; i < images.Length; i++)
        {
            string jsonPath = Path.Combine(dataset, "json", Path.GetFileNameWithoutExtension(images[i]) + ".json");
            if (!File.Exists(jsonPath)) { skipped++; continue; }
            var annotations = new List<SegAnnotation>();
            int shapeIndex = -1;
            foreach (JToken shape in (JArray?)JObject.Parse(File.ReadAllText(jsonPath))["shapes"] ?? new JArray())
            {
                shapeIndex++;
                // 'oriented_rectangle' is not a region kind the existing LabelMe reader is assumed to know: its 4 corner points are used
                // as a plain polygon here (test-only conversion). Class FAN3 is the only class, mapped to label 1.
                var points = shape["points"]!.Select(p => new System.Drawing.PointF((float)p[0]!, (float)p[1]!)).ToArray();
                if (points.Length >= 3) annotations.Add(new SegAnnotation { Kind = "Polygon", Label = (byte)((shapeIndex % classes) + 1), Points = points });
            }

            samples.Add(new SegSample
            {
                ImagePath = images[i],
                OriginalName = Path.GetFileName(images[i]),
                TrainValMode = (i % 5 == 4) ? SegSample.ModeVal : SegSample.ModeTrain,
                Annotations = annotations,
            });
        }

        string work = Path.Combine(root, "tmp", "EAI-PLUS", "s5-work", runName + "-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        string capture = Path.Combine(work, "capture");
        Directory.CreateDirectory(work);
        var options = new SegTrainingOptions { Version = 2, Epochs = epochs };
        var job = new SegAiTrainJob
        {
            RunsDir = Path.Combine(work, "runs"),
            Registry = new SegAiRegistry(Path.Combine(work, "models")),
            Backend = new CapturingBackend(capture),
            Samples = samples,
            Options = options,
            Inference = new SegInferenceSettings(),
        };

        var wall = Stopwatch.StartNew();
        SegAiTrainResult result = await Task.Run(() => SegAiTrainingPipeline.Run(job)).WaitAsync(TimeSpan.FromHours(4));
        wall.Stop();

        int width = 0, height = 0;
        if (images.Length > 0) using (Mat first = Cv2.ImRead(images[0])) { width = first.Width; height = first.Height; }
        string[] epochLines = File.Exists(Path.Combine(capture, "epochs.csv")) ? File.ReadAllLines(Path.Combine(capture, "epochs.csv")) : Array.Empty<string>();
        var text = new StringBuilder();
        text.AppendLine("run=" + runName + " classes=" + classes + " utc=" + DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
        text.AppendLine("dataset=" + dataset);
        text.AppendLine("images=" + images.Length + " usedSamples=" + samples.Count + " skippedNoJson=" + skipped + " size=" + width + "x" + height
            + " val=" + samples.Count(s => s.TrainValMode == SegSample.ModeVal) + " train=" + samples.Count(s => s.TrainValMode == SegSample.ModeTrain));
        text.AppendLine("options=Version 2, Epochs " + options.Epochs + ", InputSize " + options.InputSize + ", defaults otherwise; oriented_rectangle read as 4-point polygon, label 1");
        text.AppendLine("logicalCpus=" + Environment.ProcessorCount);
        text.AppendLine("success=" + result.Success + " message=" + result.Message);
        text.AppendLine("managedWallSeconds=" + wall.Elapsed.TotalSeconds.ToString("0.00", CultureInfo.InvariantCulture)
            + " resultElapsedSeconds=" + result.Elapsed.TotalSeconds.ToString("0.00", CultureInfo.InvariantCulture)
            + " reportTrainingSeconds=" + (result.Report?.TotalTrainingSeconds ?? 0).ToString("0.00", CultureInfo.InvariantCulture));
        text.AppendLine("finalVal map50=" + (result.Report?.Map50?.ToString("0.####", CultureInfo.InvariantCulture) ?? "n/a")
            + " map5095=" + (result.Report?.Map5095?.ToString("0.####", CultureInfo.InvariantCulture) ?? "n/a")
            + " pixelIou=" + (result.Report?.PixelIou?.ToString("0.####", CultureInfo.InvariantCulture) ?? "n/a")
            + " epochsInReport=" + (result.Report?.Epochs.Count ?? 0));
        text.AppendLine("--- stage_times.csv ---");
        text.AppendLine(ReadOrNote(Path.Combine(capture, "stage_times.csv")).TrimEnd());
        text.AppendLine("--- epochs.csv header + first 3 / last 3 ---");
        if (epochLines.Length > 0) text.AppendLine(epochLines[0]);
        foreach (string line in epochLines.Skip(1).Take(3)) text.AppendLine(line);
        text.AppendLine("...");
        foreach (string line in epochLines.Skip(1).Reverse().Take(3).Reverse()) text.AppendLine(line);

        string outDir = Path.Combine(root, "artifacts", "EAI-PLUS", "s5", runName);
        Directory.CreateDirectory(outDir);
        File.WriteAllText(Path.Combine(outDir, "timing.txt"), text.ToString());
        foreach (string name in new[] { "stage_times.csv", "epochs.csv" })
            if (File.Exists(Path.Combine(capture, name))) File.Copy(Path.Combine(capture, name), Path.Combine(outDir, name), true);

        Assert.True(result.Success, result.Message);
    }
}
