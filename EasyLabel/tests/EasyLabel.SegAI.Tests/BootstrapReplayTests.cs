using System.Diagnostics;
using System.Globalization;
using System.Text;
using EasyAI.Desktop.ViewModels;
using EasyLabel.Core.Model;
using EasyLabel.SegAI.AutoLabel;
using EasyLabel.SegAI.Data;
using EasyLabel.SegAI.Inspection;
using EasyLabel.SegAI.Model;
using EasyLabel.SegAI.Native;
using EasyLabel.SegAI.Options;
using EasyLabel.SegAI.Training;
using Newtonsoft.Json.Linq;
using Xunit.Abstractions;
using Cv = OpenCvSharp;

namespace EasyLabel.SegAI.Tests;

/// <summary>
/// EAI-LABEL S1: OFFLINE replay of bootstrap labelling, no agent spend. The agent's S0 baseline labels stand in for "the agent labelled the
/// k seed images"; a Segment AI V2 model (one class named OK) is trained on them with the app's default options, activated, and run over every
/// other image through AutoLabelService. Opt-in: EAI_LABEL_RUN=1 and EAI_LABEL_REPLAY=1.
/// Env: EAI_LABEL_BASELINE (default artifacts\EAI-LABEL\s0-baseline; read only: project\*.json = agent labels, project\*.png = images),
/// EAI_LABEL_GT (default E:\EAI_TEST; read only: *.json ground truth), EAI_LABEL_REPLAY_OUT (default artifacts\EAI-LABEL\s1-replay),
/// EAI_LABEL_K (default 2,3,5), EAI_LABEL_SEEDS (default stratified,first), EAI_LABEL_P (probe images, default 2),
/// EAI_LABEL_A_SECONDS (20.5), EAI_LABEL_C_USD (0.065), EAI_LABEL_W0_SECONDS (1939), EAI_LABEL_C0_USD (7.83), EAI_LABEL_TRAIN_MINUTES (15),
/// EASYAI_SEGAI_DIR (native BeeNativeSegAI.dll folder, when not found by default).
/// Seed rules: 'first' = the first k images by ordinal name that are not ground-truth images. 'stratified' = over the non-GT images ordered by
/// (baseline shape count, name) take alternately the lowest, the highest, the next lowest, the next highest ... until k are chosen.
/// Resumable: a run whose result.json exists is loaded, not repeated. Writes replay-summary.json and replay-summary.csv after every run.
/// </summary>
[Trait("Category", "Replay")]
public sealed class BootstrapReplayTests
{
    private const string ClassName = "OK";
    private readonly ITestOutputHelper _out;

    public BootstrapReplayTests(ITestOutputHelper output) => _out = output;

    private sealed class ReplayFactAttribute : LabelRunFactAttribute
    {
        public ReplayFactAttribute() : base()
        {
            if (Skip != null)
            {
                return;
            }

            if (LabelRunEnv.Get("EAI_LABEL_REPLAY") != "1")
            {
                Skip = "SKIPPED (nothing proven): opt-in replay, set EAI_LABEL_REPLAY=1 (and EAI_LABEL_RUN=1).";
                return;
            }

            NativeSegAiResolution resolution = NativeSegAiFolderResolver.Resolve(new NativeSegAiOptions(null, Array.Empty<string>()));
            if (resolution.Folder is null)
            {
                Skip = "SKIPPED (nothing proven): " + NativeSegAiFolderResolver.DllName + " not found (set EASYAI_SEGAI_DIR).";
            }
        }
    }

    private sealed record ImgInfo(string FileName, string Base, string Path, int BaselineCount, bool IsGt);

    private sealed class Row
    {
        public string Rel = string.Empty;
        public bool IsGt;
        public int BaselineCount = -1;
        public double Seconds;
        public string? Error;
        public bool FromCache;
        public string Bucket = "none";
        public float? Score;
        public bool NeedsAgent1;
        public int Polygons;
        public int Rects;
        public List<LabelShape> Shapes = new List<LabelShape>();
        public ImageQuality? VsBaseline;
        public ImageQuality? VsGt;
        public bool R1;
        public bool R2;
        public bool R3;
    }

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    [ReplayFact]
    public async Task ReplayBootstrapLabellingOffline()
    {
        string root = LabelRunEnv.RepoRoot() ?? throw new InvalidOperationException("Repository root not found; all output must stay inside the project.");
        string baseline = Path.GetFullPath(LabelRunEnv.Get("EAI_LABEL_BASELINE") ?? Path.Combine(root, "artifacts", "EAI-LABEL", "s0-baseline"));
        string gtFolder = Path.GetFullPath(LabelRunEnv.Get("EAI_LABEL_GT") ?? LabelRunEnv.ProtectedFolder);
        string outRoot = Path.GetFullPath(LabelRunEnv.Get("EAI_LABEL_REPLAY_OUT") ?? Path.Combine(root, "artifacts", "EAI-LABEL", "s1-replay"));
        if (!LabelRunEnv.IsEqualOrUnder(outRoot, root) || LabelRunEnv.IsEqualOrUnder(outRoot, LabelRunEnv.ProtectedFolder) || LabelRunEnv.IsEqualOrUnder(outRoot, baseline) || LabelRunEnv.IsEqualOrUnder(outRoot, gtFolder))
        {
            throw new InvalidOperationException("REFUSED: output must be inside the project and outside the baseline and ground-truth folders: " + outRoot);
        }

        string baselineProject = Path.Combine(baseline, "project");
        Assert.True(Directory.Exists(baselineProject), "Baseline project folder not found: " + baselineProject);
        Assert.True(Directory.Exists(gtFolder), "Ground-truth folder not found: " + gtFolder);

        double a = Dbl("EAI_LABEL_A_SECONDS", 20.5);
        double c = Dbl("EAI_LABEL_C_USD", 0.065);
        double w0 = Dbl("EAI_LABEL_W0_SECONDS", 1939);
        double c0 = Dbl("EAI_LABEL_C0_USD", 7.83);
        int probes = (int)Dbl("EAI_LABEL_P", 2);
        TimeSpan trainLimit = TimeSpan.FromMinutes(Dbl("EAI_LABEL_TRAIN_MINUTES", 15));
        int[] ks = (LabelRunEnv.Get("EAI_LABEL_K") ?? "2,3,5").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(s => int.Parse(s, Inv)).ToArray();
        string[] strategies = (LabelRunEnv.Get("EAI_LABEL_SEEDS") ?? "stratified,first").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (string strategy in strategies)
        {
            Assert.Contains(strategy, new[] { "stratified", "first" });
        }

        // Inventory: images and agent labels from the baseline, ground truth names from the GT folder (names only matched by base name).
        HashSet<string> gtBases = new HashSet<string>(Directory.GetFiles(gtFolder, "*.json", SearchOption.TopDirectoryOnly).Select(p => Path.GetFileNameWithoutExtension(p)), StringComparer.OrdinalIgnoreCase);
        List<ImgInfo> all = new List<ImgInfo>();
        foreach (string png in Directory.GetFiles(baselineProject, "*.png", SearchOption.TopDirectoryOnly).OrderBy(p => Path.GetFileName(p), StringComparer.Ordinal))
        {
            string baseName = Path.GetFileNameWithoutExtension(png);
            string json = Path.Combine(baselineProject, baseName + ".json");
            int count = File.Exists(json) ? LabelMetrics.LoadShapes(json).Count : -1;
            all.Add(new ImgInfo(Path.GetFileName(png), baseName, png, count, gtBases.Contains(baseName)));
        }

        Assert.NotEmpty(all);
        Assert.True(all.All(i => i.BaselineCount >= 0), "Every image needs a baseline JSON: " + string.Join(", ", all.Where(i => i.BaselineCount < 0).Select(i => i.FileName).Take(5)));
        NativeSegAiResolution nativeRes = NativeSegAiFolderResolver.Resolve(new NativeSegAiOptions(null, Array.Empty<string>()));
        NativeSegAiLoadResult nativeLoad = NativeSegAiLibrary.TryLoad(new NativeSegAiOptions(nativeRes.Folder, Array.Empty<string>()));
        _out.WriteLine("native folder=" + nativeRes.Folder + " status=" + nativeLoad.Status + " " + nativeLoad.Message);
        Assert.True(nativeLoad.IsLoaded, "native Segment AI library did not load: " + nativeLoad.Message);
        _out.WriteLine("images=" + all.Count + " gt=" + all.Count(i => i.IsGt) + " baselineGtJsonFound=" + gtBases.Count);

        Directory.CreateDirectory(outRoot);
        List<JObject> results = new List<JObject>();
        foreach (int k in ks)
        {
            foreach (string strategy in strategies)
            {
                string runDir = Path.Combine(outRoot, "k" + k.ToString(Inv) + "-" + strategy);
                string resultPath = Path.Combine(runDir, "result.json");
                if (File.Exists(resultPath))
                {
                    results.Add(JObject.Parse(File.ReadAllText(resultPath)));
                    _out.WriteLine("resume: skipping " + runDir);
                    continue;
                }

                JObject run = await RunOneAsync(k, strategy, all, baselineProject, gtFolder, runDir, a, c, w0, c0, probes, trainLimit);
                AtomicWrite(resultPath, run.ToString(Newtonsoft.Json.Formatting.Indented));
                results.Add(run);
                WriteSummary(outRoot, results, a, c, w0, c0, probes, trainLimit);
                _out.WriteLine("done " + runDir + " status=" + (string?)run["status"]);
            }
        }

        WriteSummary(outRoot, results, a, c, w0, c0, probes, trainLimit);
        Assert.NotEmpty(results);
        Assert.Contains(results, r => (string?)r["status"] == "ok");
    }

    private async Task<JObject> RunOneAsync(int k, string strategy, List<ImgInfo> all, string baselineProject, string gtFolder, string runDir, double a, double c, double w0, double c0, int probes, TimeSpan trainLimit)
    {
        List<ImgInfo> nonGt = all.Where(i => !i.IsGt).OrderBy(i => i.FileName, StringComparer.Ordinal).ToList();
        Assert.True(k >= 2 && k <= nonGt.Count, "k out of range: " + k);
        List<ImgInfo> seeds = SelectSeeds(nonGt, k, strategy);
        HashSet<string> seedNames = new HashSet<string>(seeds.Select(s => s.FileName), StringComparer.OrdinalIgnoreCase);
        double medianSeed = Median(seeds.Select(s => (double)s.BaselineCount));

        // Fresh copy project: all images, then (after the class table is saved, so no label is renamed) only the seed JSONs.
        string project = Path.Combine(runDir, "project");
        if (Directory.Exists(project))
        {
            Directory.Move(project, project + ".incomplete-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss", Inv));
        }

        Directory.CreateDirectory(project);
        foreach (ImgInfo image in all)
        {
            File.Copy(image.Path, Path.Combine(project, image.FileName), false);
        }

        JObject result = new JObject
        {
            ["k"] = k,
            ["strategy"] = strategy,
            ["seed_rule"] = strategy == "first"
                ? "first k images by ordinal file name that are not ground-truth images"
                : "non-GT images ordered by (baseline shape count, ordinal name); alternately lowest, highest, next lowest, next highest ... until k",
            ["seeds"] = new JArray(seeds.Select(s => new JObject { ["name"] = s.FileName, ["baseline_shapes"] = s.BaselineCount })),
            ["median_seed_shapes"] = medianSeed,
            ["images_total"] = all.Count,
            ["gt_images_total"] = all.Count(i => i.IsGt),
            ["params"] = new JObject { ["a_seconds"] = a, ["c_usd"] = c, ["w0_seconds"] = w0, ["c0_usd"] = c0, ["probe_images_p"] = probes, ["train_limit_minutes"] = trainLimit.TotalMinutes },
        };

        using SegAiSession session = SegAiSession.Create(project);
        session.SaveClassTable(new SegAiClassTable(new[] { new SegAiClassEntry(ClassName, SegAiClassKind.Defect, true) }));
        foreach (ImgInfo seed in seeds)
        {
            File.Copy(Path.Combine(baselineProject, seed.Base + ".json"), Path.Combine(project, seed.Base + ".json"), false);
        }

        List<string> val = AssignVal(session);
        result["val_images"] = new JArray(val);

        SegTrainingOptions options = TrainStepRules.BuildOptions(TrainStepRules.DefaultEpochs, TrainStepRules.DefaultInputSize);
        result["epochs_requested"] = options.Epochs;
        result["input_size"] = options.InputSize;
        SegAiTrainingService service = new SegAiTrainingService(session);
        SegAiTrainBlock block = SegAiTrainingService.Check(session, options);
        if (block != SegAiTrainBlock.None)
        {
            result["status"] = "blocked";
            result["message"] = "Training cannot start: " + block;
            return result;
        }

        using CancellationTokenSource cts = new CancellationTokenSource(trainLimit);
        Stopwatch trainClock = Stopwatch.StartNew();
        SegAiTrainResult train;
        try
        {
            train = await service.StartAsync(options, null, cts.Token).WaitAsync(trainLimit + TimeSpan.FromMinutes(2));
        }
        catch (TimeoutException)
        {
            // The native trainer did not honour the cancel token: record it, then fail the test (the process may still be training).
            result["status"] = "train_hung";
            result["message"] = "StartAsync did not return within the limit plus 2 minutes.";
            result["train_seconds"] = trainClock.Elapsed.TotalSeconds;
            AtomicWrite(Path.Combine(runDir, "result.json"), result.ToString(Newtonsoft.Json.Formatting.Indented));
            throw;
        }

        trainClock.Stop();
        int epochsRun = train.Report?.Epochs.Count ?? 0;
        result["train_seconds"] = Math.Round(trainClock.Elapsed.TotalSeconds, 2);
        result["train_result_elapsed_seconds"] = Math.Round(train.Elapsed.TotalSeconds, 2);
        result["train_message"] = train.Message;
        result["validation_summary"] = train.ValidationSummary;
        result["epochs_run"] = epochsRun;
        result["early_stopped"] = train.Success && epochsRun > 0 && epochsRun < options.Epochs;
        result["train_timeout"] = train.Cancelled && cts.IsCancellationRequested;
        result["map50"] = train.Report?.Map50;
        result["pixel_iou"] = train.Report?.PixelIou;
        if (!train.Success)
        {
            result["status"] = train.Cancelled && cts.IsCancellationRequested ? "train_timeout" : (train.Cancelled ? "train_cancelled" : "train_failed");
            result["message"] = train.Message;
            return result;
        }

        string modelFile = train.Entry?.File ?? Path.GetFileName(train.CandidatePath ?? throw new InvalidOperationException("Training succeeded without a candidate."));
        session.SetActiveModel(modelFile);
        result["model_file"] = modelFile;

        // Inference on every non-seed image (all 84 - k, ground-truth images included), sequentially, timed per image.
        AutoLabelService auto = new AutoLabelService(session);
        List<ImgInfo> targets = all.Where(i => !seedNames.Contains(i.FileName)).OrderBy(i => i.FileName, StringComparer.Ordinal).ToList();
        List<Row> rows = new List<Row>();
        foreach (ImgInfo target in targets)
        {
            Stopwatch sw = Stopwatch.StartNew();
            AutoLabelImageResult r = auto.Run(target.FileName);
            sw.Stop();
            Row row = new Row { Rel = target.FileName, IsGt = target.IsGt, BaselineCount = target.BaselineCount, Seconds = sw.Elapsed.TotalSeconds, Error = r.Error, FromCache = r.FromCache };
            foreach (SegAiSuggestion s in r.Suggestions)
            {
                row.Polygons++;
                LabelShape? shape = ToRect(s);
                if (shape != null)
                {
                    row.Shapes.Add(shape);
                }
            }

            row.Rects = row.Shapes.Count;
            if (r.Error != null)
            {
                row.Bucket = "Error";
                row.NeedsAgent1 = true;
            }
            else if (r.Uncertainty == null)
            {
                row.Bucket = "Unknown";
                row.NeedsAgent1 = true;
            }
            else
            {
                row.Bucket = r.Uncertainty.Value.Bucket.ToString();
                row.Score = r.Uncertainty.Value.Score;
                row.NeedsAgent1 = r.Uncertainty.Value.NeedsReview;
            }

            IReadOnlyList<LabelShape> agent = LabelMetrics.LoadShapes(Path.Combine(baselineProject, target.Base + ".json"));
            row.VsBaseline = LabelMetrics.Evaluate(target.Base, row.Shapes, agent);
            if (target.IsGt)
            {
                row.VsGt = LabelMetrics.Evaluate(target.Base, row.Shapes, LabelMetrics.LoadShapes(Path.Combine(gtFolder, target.Base + ".json")));
            }

            row.R1 = row.NeedsAgent1;
            row.R2 = row.R1 || row.Rects == 0;
            row.R3 = row.R2 || Math.Abs(row.Rects - medianSeed) > 0.30 * medianSeed;
            rows.Add(row);
        }

        WritePerImageCsv(Path.Combine(runDir, "per-image.csv"), rows);

        double[] seconds = rows.Select(r => r.Seconds).ToArray();
        double inferTotal = seconds.Sum();
        double inferMedian = Median(seconds);
        result["infer_images"] = rows.Count;
        result["infer_seconds_total"] = Math.Round(inferTotal, 2);
        result["infer_seconds_median"] = Math.Round(inferMedian, 3);
        result["infer_seconds_first_image"] = rows.Count > 0 ? Math.Round(rows[0].Seconds, 3) : (double?)null;
        result["infer_errors"] = rows.Count(r => r.Error != null);
        result["zero_shape_images"] = rows.Count(r => r.Rects == 0);
        result["polygons_total"] = rows.Sum(r => r.Polygons);
        result["rects_total"] = rows.Sum(r => r.Rects);
        result["buckets"] = new JObject(rows.GroupBy(r => r.Bucket).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => new JProperty(g.Key, g.Count())));
        result["overall_vs_baseline"] = Aggregate(rows.Select(r => r.VsBaseline!).ToList());
        result["overall_vs_gt"] = Aggregate(rows.Where(r => r.VsGt != null).Select(r => r.VsGt!).ToList());

        int n = all.Count;
        int others = n - k;
        JObject rules = new JObject();
        (string Name, Func<Row, bool> Routed, string Text)[] defs =
        {
            ("R1", r => r.R1, "Doubtful + Unsure (and error / unknown uncertainty) go to the agent"),
            ("R2", r => r.R2, "R1 plus any image whose model returned zero shapes"),
            ("R3", r => r.R3, "R2 plus model shape count differing from the median seed shape count by more than 30% (a PROBE only, not a product rule)"),
        };
        foreach ((string name, Func<Row, bool> routed, string text) in defs)
        {
            List<Row> kept = rows.Where(r => !routed(r)).ToList();
            int sent = rows.Count - kept.Count;
            double f = rows.Count == 0 ? 0 : (double)sent / rows.Count;
            double w1 = (k + probes) * a + (double)result["train_seconds"]! + others * inferMedian + f * others * a;
            double w1Total = (k + probes) * a + (double)result["train_seconds"]! + inferTotal + f * others * a;
            double c1 = (k + probes + f * others) * c;
            rules[name] = new JObject
            {
                ["description"] = text,
                ["routed_to_agent"] = sent,
                ["kept_local"] = kept.Count,
                ["fallback_fraction_f"] = Math.Round(f, 4),
                ["kept_vs_baseline"] = Aggregate(kept.Select(r => r.VsBaseline!).ToList()),
                ["kept_vs_gt"] = Aggregate(kept.Where(r => r.VsGt != null).Select(r => r.VsGt!).ToList()),
                ["est_w1_seconds"] = Math.Round(w1, 1),
                ["est_w1_seconds_using_infer_total"] = Math.Round(w1Total, 1),
                ["est_c1_usd"] = Math.Round(c1, 4),
                ["speedup_vs_baseline"] = Math.Round(w0 / w1, 3),
                ["cost_ratio_vs_baseline"] = Math.Round(c1 / c0, 3),
                ["meets_4x"] = w1 <= w0 / 4.0,
                ["meets_minus_50_percent_cost"] = c1 <= c0 / 2.0,
            };
        }

        result["rules"] = rules;
        result["status"] = "ok";
        return result;
    }

    private sealed class SweepFactAttribute : LabelRunFactAttribute
    {
        public SweepFactAttribute() : base()
        {
            if (Skip != null)
            {
                return;
            }

            if (LabelRunEnv.Get("EAI_LABEL_SWEEP") != "1")
            {
                Skip = "SKIPPED (nothing proven): opt-in sweep, set EAI_LABEL_SWEEP=1 (and EAI_LABEL_RUN=1).";
                return;
            }

            NativeSegAiResolution resolution = NativeSegAiFolderResolver.Resolve(new NativeSegAiOptions(null, Array.Empty<string>()));
            if (resolution.Folder is null)
            {
                Skip = "SKIPPED (nothing proven): " + NativeSegAiFolderResolver.DllName + " not found (set EASYAI_SEGAI_DIR).";
            }
        }
    }

    /// <summary>
    /// EAI-LABEL S1b: NO-SPEND sweep of the inference gate of a replay model. For each recipe (default k20-first and k5-stratified) a model is made once
    /// (the saved model of the s1-replay run folder is imported into a fresh sweep project when it exists, else it is trained here from the same seeds), then for
    /// every Foreground x Morphology config the model is reloaded with those SegAiSession.Options and auto-label is re-run on all non-seed images.
    /// Opt-in: EAI_LABEL_RUN=1 and EAI_LABEL_SWEEP=1. Env: EAI_LABEL_BASELINE, EAI_LABEL_GT, EAI_LABEL_REPLAY_OUT (read only; where the saved models and seeds are taken from),
    /// EAI_LABEL_SWEEP_OUT (default artifacts\EAI-LABEL\s1b-sweep), EAI_LABEL_SWEEP_RECIPES (default 20:first,5:stratified), EAI_LABEL_SWEEP_FOREGROUND (0.5,0.75,0.9,0.95),
    /// EAI_LABEL_SWEEP_MORPH (0,3), EAI_LABEL_TRAIN_MINUTES (15, used only when a model has to be trained).
    /// Writes sweep.csv (one line per recipe and config), sweep-per-image.csv and sweep-meta.json. The Foreground is the stored option; a legacy (not prior-balanced)
    /// model gets it odds-corrected by SegTrainingOptions.EffectiveV2Foreground (0.5 becomes 0.9375), which is recorded in foreground_effective.
    /// </summary>
    [SweepFact]
    public async Task ReplayThresholdSweep()
    {
        string root = LabelRunEnv.RepoRoot() ?? throw new InvalidOperationException("Repository root not found; all output must stay inside the project.");
        string baseline = Path.GetFullPath(LabelRunEnv.Get("EAI_LABEL_BASELINE") ?? Path.Combine(root, "artifacts", "EAI-LABEL", "s0-baseline"));
        string baselineProject = Directory.Exists(Path.Combine(baseline, "project")) ? Path.Combine(baseline, "project") : baseline;
        string gtFolder = Path.GetFullPath(LabelRunEnv.Get("EAI_LABEL_GT") ?? LabelRunEnv.ProtectedFolder);
        string replayRoot = Path.GetFullPath(LabelRunEnv.Get("EAI_LABEL_REPLAY_OUT") ?? Path.Combine(root, "artifacts", "EAI-LABEL", "s1-replay"));
        string sweepOut = Path.GetFullPath(LabelRunEnv.Get("EAI_LABEL_SWEEP_OUT") ?? Path.Combine(root, "artifacts", "EAI-LABEL", "s1b-sweep"));
        if (!LabelRunEnv.IsEqualOrUnder(sweepOut, root) || LabelRunEnv.IsEqualOrUnder(sweepOut, LabelRunEnv.ProtectedFolder) || LabelRunEnv.IsEqualOrUnder(sweepOut, baseline)
            || LabelRunEnv.IsEqualOrUnder(sweepOut, gtFolder) || LabelRunEnv.IsEqualOrUnder(sweepOut, replayRoot) || LabelRunEnv.IsEqualOrUnder(sweepOut, Path.Combine(root, "artifacts", "EAI-LABEL", "s0-baseline")))
        {
            throw new InvalidOperationException("REFUSED: sweep output must be inside the project and outside the baseline, replay and ground-truth folders: " + sweepOut);
        }

        Assert.True(Directory.Exists(baselineProject), "Baseline project folder not found: " + baselineProject);
        Assert.True(Directory.Exists(gtFolder), "Ground-truth folder not found: " + gtFolder);
        TimeSpan trainLimit = TimeSpan.FromMinutes(Dbl("EAI_LABEL_TRAIN_MINUTES", 15));
        double[] foregrounds = (LabelRunEnv.Get("EAI_LABEL_SWEEP_FOREGROUND") ?? "0.5,0.75,0.9,0.95").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(s => double.Parse(s, Inv)).ToArray();
        int[] morphologies = (LabelRunEnv.Get("EAI_LABEL_SWEEP_MORPH") ?? "0,3").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(s => int.Parse(s, Inv)).ToArray();
        List<(int K, string Strategy)> recipes = new List<(int, string)>();
        foreach (string text in (LabelRunEnv.Get("EAI_LABEL_SWEEP_RECIPES") ?? "20:first,5:stratified").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string[] parts = text.Split(':');
            Assert.True(parts.Length == 2 && (parts[1] == "first" || parts[1] == "stratified"), "Bad recipe '" + text + "', expected <k>:first or <k>:stratified.");
            recipes.Add((int.Parse(parts[0], Inv), parts[1]));
        }

        Assert.NotEmpty(recipes);
        HashSet<string> gtBases = new HashSet<string>(Directory.GetFiles(gtFolder, "*.json", SearchOption.TopDirectoryOnly).Select(p => Path.GetFileNameWithoutExtension(p)), StringComparer.OrdinalIgnoreCase);
        List<ImgInfo> all = LoadInventory(baselineProject, gtBases);
        Assert.NotEmpty(all);
        Assert.True(all.All(i => i.BaselineCount >= 0), "Every image needs a baseline JSON: " + string.Join(", ", all.Where(i => i.BaselineCount < 0).Select(i => i.FileName).Take(5)));
        NativeSegAiResolution nativeRes = NativeSegAiFolderResolver.Resolve(new NativeSegAiOptions(null, Array.Empty<string>()));
        NativeSegAiLoadResult nativeLoad = NativeSegAiLibrary.TryLoad(new NativeSegAiOptions(nativeRes.Folder, Array.Empty<string>()));
        _out.WriteLine("native folder=" + nativeRes.Folder + " status=" + nativeLoad.Status + " " + nativeLoad.Message);
        Assert.True(nativeLoad.IsLoaded, "native Segment AI library did not load: " + nativeLoad.Message);

        Directory.CreateDirectory(sweepOut);
        List<string> header = new List<string>
        {
            "recipe", "k", "strategy", "seed_median_shapes", "model_source", "foreground", "morphology", "foreground_effective", "prior_balanced",
            "images", "errors", "zero_shape_images", "needs_agent_r1", "median_regions_ng", "median_regions_ok", "n_ng", "n_ok", "images_eq_seed_median",
        };
        foreach (string prefix in new[] { "gt", "ng_vs_base", "ok_vs_base" })
        {
            foreach (string field in new[] { "images", "tp", "fp", "fn", "precision", "recall", "mean_iou" })
            {
                header.Add(prefix + "_" + field);
            }
        }

        header.Add("seconds_infer_total");
        header.Add("seconds_config_total");
        StringBuilder csv = new StringBuilder(string.Join(",", header)).Append('\n');
        StringBuilder perImage = new StringBuilder("recipe,foreground,morphology,rel,is_gt,baseline_shapes,model_rects,model_polygons,bucket,error\n");
        JArray meta = new JArray();
        foreach ((int k, string strategy) in recipes)
        {
            await SweepRecipeAsync(k, strategy, all, baselineProject, gtFolder, replayRoot, sweepOut, foregrounds, morphologies, trainLimit, header.Count, csv, perImage, meta);
        }

        JObject metaRoot = new JObject
        {
            ["generated_utc"] = DateTime.UtcNow.ToString("o", Inv),
            ["note"] = "No agent spend. Seed labels and the NG/OK comparison truth are the s0 baseline labels (pseudo ground truth); gt_* columns use the real ground truth on non-seed GT images. Auto-label is re-run for every config with a fresh AutoLabelService without disk cache (its cache key ignores Foreground and Morphology).",
            ["foregrounds"] = new JArray(foregrounds),
            ["morphologies"] = new JArray(morphologies),
            ["session_option_defaults"] = new JObject { ["foreground"] = new SegTrainingOptions().Foreground, ["morphology"] = new SegTrainingOptions().Morphology },
            ["recipes"] = meta,
        };
        AtomicWrite(Path.Combine(sweepOut, "sweep-meta.json"), metaRoot.ToString(Newtonsoft.Json.Formatting.Indented));
    }

    private async Task SweepRecipeAsync(int k, string strategy, List<ImgInfo> all, string baselineProject, string gtFolder, string replayRoot, string sweepOut,
        double[] foregrounds, int[] morphologies, TimeSpan trainLimit, int columns, StringBuilder csv, StringBuilder perImage, JArray meta)
    {
        string recipe = "k" + k.ToString(Inv) + "-" + strategy;
        string projectDir = Path.Combine(sweepOut, recipe, "project");
        if (Directory.Exists(projectDir))
        {
            Directory.Move(projectDir, projectDir + ".incomplete-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss", Inv));
        }

        Directory.CreateDirectory(projectDir);
        foreach (ImgInfo image in all)
        {
            File.Copy(image.Path, Path.Combine(projectDir, image.FileName), false);
        }

        // Seeds: exactly the replay's when its result.json exists, else the same selection rule.
        string replayRun = Path.Combine(replayRoot, recipe);
        string replayResultPath = Path.Combine(replayRun, "result.json");
        JObject? replayResult = File.Exists(replayResultPath) ? JObject.Parse(File.ReadAllText(replayResultPath)) : null;
        List<ImgInfo> nonGt = all.Where(i => !i.IsGt).OrderBy(i => i.FileName, StringComparer.Ordinal).ToList();
        List<ImgInfo> seeds;
        string seedSource;
        if (replayResult?["seeds"] is JArray seedArray && seedArray.Count == k)
        {
            HashSet<string> names = new HashSet<string>(seedArray.Select(s => (string?)s["name"] ?? string.Empty), StringComparer.OrdinalIgnoreCase);
            seeds = all.Where(i => names.Contains(i.FileName)).OrderBy(i => i.FileName, StringComparer.Ordinal).ToList();
            seedSource = "s1-replay result.json";
        }
        else
        {
            seeds = SelectSeeds(nonGt, k, strategy);
            seedSource = "SelectSeeds rule";
        }

        Assert.Equal(k, seeds.Count);
        HashSet<string> seedNames = new HashSet<string>(seeds.Select(s => s.FileName), StringComparer.OrdinalIgnoreCase);
        double medianSeed = Median(seeds.Select(s => (double)s.BaselineCount));

        using SegAiSession session = SegAiSession.Create(projectDir);
        session.SaveClassTable(new SegAiClassTable(new[] { new SegAiClassEntry(ClassName, SegAiClassKind.Defect, true) }));

        string modelFile;
        string modelSource;
        string? reuseName = (string?)replayResult?["model_file"];
        string? reusePath = reuseName == null ? null : Path.Combine(replayRun, "project", ".easyai", "models", reuseName);
        if ((string?)replayResult?["status"] == "ok" && reusePath != null && File.Exists(reusePath))
        {
            SegAiModelImportResult imported = session.ImportModel(reusePath);
            if (imported.Entry == null)
            {
                throw new InvalidOperationException("The saved replay model could not be imported: " + reusePath + " " + imported.Problem?.En);
            }

            modelFile = imported.Entry.File;
            modelSource = "imported (read only) from " + reusePath;
        }
        else
        {
            foreach (ImgInfo seed in seeds)
            {
                File.Copy(Path.Combine(baselineProject, seed.Base + ".json"), Path.Combine(projectDir, seed.Base + ".json"), false);
            }

            AssignVal(session);
            SegTrainingOptions options = TrainStepRules.BuildOptions(TrainStepRules.DefaultEpochs, TrainStepRules.DefaultInputSize);
            SegAiTrainBlock block = SegAiTrainingService.Check(session, options);
            Assert.True(block == SegAiTrainBlock.None, "Training cannot start: " + block);
            SegAiTrainingService service = new SegAiTrainingService(session);
            using CancellationTokenSource cts = new CancellationTokenSource(trainLimit);
            SegAiTrainResult train = await service.StartAsync(options, null, cts.Token).WaitAsync(trainLimit + TimeSpan.FromMinutes(2));
            Assert.True(train.Success, "Training failed: " + train.Message);
            modelFile = train.Entry?.File ?? Path.GetFileName(train.CandidatePath ?? throw new InvalidOperationException("Training succeeded without a candidate."));
            modelSource = "trained here from the seeds (" + Math.Round(train.Elapsed.TotalSeconds, 1).ToString(Inv) + " s)";
        }

        session.SetActiveModel(modelFile);
        List<ImgInfo> targets = all.Where(i => !seedNames.Contains(i.FileName)).OrderBy(i => i.FileName, StringComparer.Ordinal).ToList();
        _out.WriteLine(recipe + ": model " + modelFile + " (" + modelSource + "), seeds=" + k + ", targets=" + targets.Count + ", seed median shapes=" + medianSeed.ToString(Inv));

        JArray configMeta = new JArray();
        foreach (double foreground in foregrounds)
        {
            foreach (int morphology in morphologies)
            {
                Stopwatch configClock = Stopwatch.StartNew();
                session.Options.Foreground = (float)foreground;
                session.Options.Morphology = morphology;
                SegAiModelInfo info = session.LoadCandidate(modelFile);
                AutoLabelService auto = new AutoLabelService(session);
                auto.ConfigureDiskCache(null, null);
                List<Row> rows = new List<Row>();
                foreach (ImgInfo target in targets)
                {
                    rows.Add(InferRow(auto, target, baselineProject, gtFolder));
                }

                configClock.Stop();
                List<Row> ng = rows.Where(r => Path.GetFileNameWithoutExtension(r.Rel).EndsWith("_NG", StringComparison.OrdinalIgnoreCase)).ToList();
                List<Row> ok = rows.Where(r => Path.GetFileNameWithoutExtension(r.Rel).EndsWith("_OK", StringComparison.OrdinalIgnoreCase)).ToList();
                float effective = SegTrainingOptions.EffectiveV2Foreground((float)foreground, info.PriorBalanced == true, false);
                List<string> cells = new List<string>
                {
                    recipe, k.ToString(Inv), strategy, medianSeed.ToString("0.###", Inv), Csv(modelSource), foreground.ToString("0.###", Inv), morphology.ToString(Inv),
                    effective.ToString("0.####", Inv), info.IsV2 ? (info.PriorBalanced == true ? "1" : "0") : "n/a",
                    rows.Count.ToString(Inv), rows.Count(r => r.Error != null).ToString(Inv), rows.Count(r => r.Rects == 0).ToString(Inv), rows.Count(r => r.NeedsAgent1).ToString(Inv),
                    Median(ng.Select(r => (double)r.Rects)).ToString("0.###", Inv), Median(ok.Select(r => (double)r.Rects)).ToString("0.###", Inv),
                    ng.Count.ToString(Inv), ok.Count.ToString(Inv), rows.Count(r => Math.Abs(r.Rects - medianSeed) < 1e-9).ToString(Inv),
                };
                AddStat(cells, Aggregate(rows.Where(r => r.VsGt != null).Select(r => r.VsGt!).ToList()));
                AddStat(cells, Aggregate(ng.Select(r => r.VsBaseline!).ToList()));
                AddStat(cells, Aggregate(ok.Select(r => r.VsBaseline!).ToList()));
                cells.Add(rows.Sum(r => r.Seconds).ToString("F2", Inv));
                cells.Add(configClock.Elapsed.TotalSeconds.ToString("F2", Inv));
                Assert.Equal(columns, cells.Count);
                csv.Append(string.Join(",", cells)).Append('\n');
                foreach (Row r in rows)
                {
                    perImage.Append(recipe).Append(',').Append(foreground.ToString("0.###", Inv)).Append(',').Append(morphology.ToString(Inv)).Append(',').Append(r.Rel).Append(',')
                        .Append(r.IsGt ? 1 : 0).Append(',').Append(r.BaselineCount).Append(',').Append(r.Rects).Append(',').Append(r.Polygons).Append(',').Append(r.Bucket).Append(',').Append(Csv(r.Error ?? string.Empty)).Append('\n');
                }

                AtomicWrite(Path.Combine(sweepOut, "sweep.csv"), csv.ToString());
                AtomicWrite(Path.Combine(sweepOut, "sweep-per-image.csv"), perImage.ToString());
                _out.WriteLine(recipe + " fg=" + foreground.ToString(Inv) + " morph=" + morphology.ToString(Inv) + " effective=" + effective.ToString("0.####", Inv) + " medianNg=" + Median(ng.Select(r => (double)r.Rects)).ToString(Inv) + " medianOk=" + Median(ok.Select(r => (double)r.Rects)).ToString(Inv) + " s=" + configClock.Elapsed.TotalSeconds.ToString("F1", Inv));
            }
        }

        meta.Add(new JObject
        {
            ["recipe"] = recipe,
            ["seed_source"] = seedSource,
            ["seeds"] = new JArray(seeds.Select(s => new JObject { ["name"] = s.FileName, ["baseline_shapes"] = s.BaselineCount })),
            ["seed_median_shapes"] = medianSeed,
            ["model_file"] = modelFile,
            ["model_source"] = modelSource,
            ["non_seed_images"] = targets.Count,
            ["non_seed_gt_images"] = targets.Count(t => t.IsGt),
        });
    }

    private static List<ImgInfo> LoadInventory(string baselineProject, HashSet<string> gtBases)
    {
        List<ImgInfo> all = new List<ImgInfo>();
        foreach (string png in Directory.GetFiles(baselineProject, "*.png", SearchOption.TopDirectoryOnly).OrderBy(p => Path.GetFileName(p), StringComparer.Ordinal))
        {
            string baseName = Path.GetFileNameWithoutExtension(png);
            string json = Path.Combine(baselineProject, baseName + ".json");
            int count = File.Exists(json) ? LabelMetrics.LoadShapes(json).Count : -1;
            all.Add(new ImgInfo(Path.GetFileName(png), baseName, png, count, gtBases.Contains(baseName)));
        }

        return all;
    }

    /// <summary>Same per-image work as the replay: auto-label, polygons to minimum-area rotated rectangles, quality against the baseline and (GT images) the real truth.</summary>
    private static Row InferRow(AutoLabelService auto, ImgInfo target, string baselineProject, string gtFolder)
    {
        Stopwatch sw = Stopwatch.StartNew();
        AutoLabelImageResult r = auto.Run(target.FileName);
        sw.Stop();
        Row row = new Row { Rel = target.FileName, IsGt = target.IsGt, BaselineCount = target.BaselineCount, Seconds = sw.Elapsed.TotalSeconds, Error = r.Error, FromCache = r.FromCache };
        foreach (SegAiSuggestion s in r.Suggestions)
        {
            row.Polygons++;
            LabelShape? shape = ToRect(s);
            if (shape != null)
            {
                row.Shapes.Add(shape);
            }
        }

        row.Rects = row.Shapes.Count;
        if (r.Error != null)
        {
            row.Bucket = "Error";
            row.NeedsAgent1 = true;
        }
        else if (r.Uncertainty == null)
        {
            row.Bucket = "Unknown";
            row.NeedsAgent1 = true;
        }
        else
        {
            row.Bucket = r.Uncertainty.Value.Bucket.ToString();
            row.Score = r.Uncertainty.Value.Score;
            row.NeedsAgent1 = r.Uncertainty.Value.NeedsReview;
        }

        row.VsBaseline = LabelMetrics.Evaluate(target.Base, row.Shapes, LabelMetrics.LoadShapes(Path.Combine(baselineProject, target.Base + ".json")));
        if (target.IsGt)
        {
            row.VsGt = LabelMetrics.Evaluate(target.Base, row.Shapes, LabelMetrics.LoadShapes(Path.Combine(gtFolder, target.Base + ".json")));
        }

        return row;
    }

    private static void AddStat(List<string> cells, JObject aggregate)
    {
        foreach (string field in new[] { "images", "tp", "fp", "fn", "precision", "recall", "mean_iou" })
        {
            cells.Add(Cell(aggregate[field]));
        }
    }

    private static List<ImgInfo> SelectSeeds(List<ImgInfo> nonGtByName, int k, string strategy)
    {
        if (strategy == "first")
        {
            return nonGtByName.Take(k).ToList();
        }

        List<ImgInfo> ordered = nonGtByName.OrderBy(i => i.BaselineCount).ThenBy(i => i.FileName, StringComparer.Ordinal).ToList();
        List<ImgInfo> chosen = new List<ImgInfo>();
        int low = 0;
        int high = ordered.Count - 1;
        bool takeLow = true;
        while (chosen.Count < k && low <= high)
        {
            chosen.Add(takeLow ? ordered[low++] : ordered[high--]);
            takeLow = !takeLow;
        }

        return chosen.OrderBy(i => i.FileName, StringComparer.Ordinal).ToList();
    }

    /// <summary>Same as TrainStepViewModel.AssignValCore: AutoAssign (seed 1337) over the labelled images, the Val side pinned through SetMode.</summary>
    private static List<string> AssignVal(SegAiSession session)
    {
        List<SegAiSampleInfo> labelled = session.Samples.Where(s => s.AnnotationCount > 0).ToList();
        List<SegSplitItem> items = new List<SegSplitItem>();
        foreach (SegAiSampleInfo sample in labelled)
        {
            HashSet<int> classes = new HashSet<int>(session.GetAnnotations(sample.Rel).Select(x => (int)x.Label));
            items.Add(new SegSplitItem(sample.Rel, classes, sample.Mode));
        }

        SegSplitSide[] sides = SegTrainingSplit.AutoAssign(items, 1337);
        List<string> val = new List<string>();
        for (int i = 0; i < labelled.Count; i++)
        {
            if (sides[i] == SegSplitSide.Val)
            {
                session.SetMode(labelled[i].Rel, SegTrainingMode.Val);
                val.Add(labelled[i].Rel);
            }
        }

        return val;
    }

    /// <summary>The model yields polygons: each becomes its minimum-area rotated rectangle (4 corners, shape type oriented_rectangle). A suggestion that already is a rectangle keeps its corners.</summary>
    private static LabelShape? ToRect(SegAiSuggestion s)
    {
        if (s.Outer.Length < 3)
        {
            return null;
        }

        LabelShape shape = new LabelShape { Label = ClassName, ShapeType = "oriented_rectangle" };
        if ((s.ShapeType == "rectangle" || s.ShapeType == "oriented_rectangle") && s.Outer.Length == 4)
        {
            foreach (System.Drawing.PointF p in s.Outer)
            {
                shape.Points.Add(new[] { (double)p.X, (double)p.Y });
            }

            return shape;
        }

        Cv.RotatedRect box = Cv.Cv2.MinAreaRect(s.Outer.Select(p => new Cv.Point2f(p.X, p.Y)));
        foreach (Cv.Point2f corner in box.Points())
        {
            shape.Points.Add(new[] { (double)corner.X, (double)corner.Y });
        }

        return shape;
    }

    private static JObject Aggregate(IReadOnlyList<ImageQuality> q)
    {
        int tp = q.Sum(i => i.TruePositives);
        int fp = q.Sum(i => i.FalsePositives);
        int fn = q.Sum(i => i.FalseNegatives);
        double iouSum = q.Sum(i => i.MeanIou * i.TruePositives);
        return new JObject
        {
            ["images"] = q.Count,
            ["tp"] = tp,
            ["fp"] = fp,
            ["fn"] = fn,
            ["precision"] = tp + fp == 0 ? (double?)null : Math.Round((double)tp / (tp + fp), 4),
            ["recall"] = tp + fn == 0 ? (double?)null : Math.Round((double)tp / (tp + fn), 4),
            ["mean_iou"] = tp == 0 ? (double?)null : Math.Round(iouSum / tp, 4),
            ["wrong_shape_count_images"] = q.Count(i => i.PredictedCount != i.GroundTruthCount),
            ["any_error_images"] = q.Count(i => i.FalsePositives > 0 || i.FalseNegatives > 0),
        };
    }

    private static void WritePerImageCsv(string path, IReadOnlyList<Row> rows)
    {
        StringBuilder text = new StringBuilder("rel,is_gt,baseline_shapes,model_polygons,model_rects,seconds,from_cache,bucket,score,error,r1,r2,r3,tp_b,fp_b,fn_b,iou_b,tp_g,fp_g,fn_g,iou_g\n");
        foreach (Row r in rows)
        {
            text.Append(r.Rel).Append(',').Append(r.IsGt ? 1 : 0).Append(',').Append(r.BaselineCount).Append(',').Append(r.Polygons).Append(',').Append(r.Rects).Append(',')
                .Append(r.Seconds.ToString("F3", Inv)).Append(',').Append(r.FromCache ? 1 : 0).Append(',').Append(r.Bucket).Append(',')
                .Append(r.Score?.ToString("F4", Inv) ?? string.Empty).Append(',').Append(Csv(r.Error ?? string.Empty)).Append(',')
                .Append(r.R1 ? 1 : 0).Append(',').Append(r.R2 ? 1 : 0).Append(',').Append(r.R3 ? 1 : 0).Append(',')
                .Append(r.VsBaseline?.TruePositives).Append(',').Append(r.VsBaseline?.FalsePositives).Append(',').Append(r.VsBaseline?.FalseNegatives).Append(',').Append(r.VsBaseline?.MeanIou.ToString("F4", Inv)).Append(',')
                .Append(r.VsGt?.TruePositives).Append(',').Append(r.VsGt?.FalsePositives).Append(',').Append(r.VsGt?.FalseNegatives).Append(',').Append(r.VsGt?.MeanIou.ToString("F4", Inv)).Append('\n');
        }

        AtomicWrite(path, text.ToString());
    }

    private static void WriteSummary(string outRoot, IReadOnlyList<JObject> runs, double a, double c, double w0, double c0, int probes, TimeSpan trainLimit)
    {
        JObject summary = new JObject
        {
            ["generated_utc"] = DateTime.UtcNow.ToString("o", Inv),
            ["note"] = "Offline replay, no agent spend. Agent baseline labels stand in for the seed labels and for the comparison truth; routing rules R1-R3 are computed offline from the model output. R3 is a probe, not a product rule.",
            ["params"] = new JObject { ["a_seconds"] = a, ["c_usd"] = c, ["w0_seconds"] = w0, ["c0_usd"] = c0, ["probe_images_p"] = probes, ["train_limit_minutes"] = trainLimit.TotalMinutes },
            ["formula"] = "W1 = (k+p)*a + T + (N-k)*m + f*(N-k)*a ; C1 = (k+p+f*(N-k))*c ; meets 4x: W1 <= W0/4 ; meets -50%: C1 <= C0/2",
            ["runs"] = new JArray(runs),
        };
        AtomicWrite(Path.Combine(outRoot, "replay-summary.json"), summary.ToString(Newtonsoft.Json.Formatting.Indented));

        StringBuilder csv = new StringBuilder("k,strategy,status,train_seconds,epochs_run,early_stopped,train_timeout,infer_images,infer_seconds_total,infer_seconds_median,sure,doubtful,unsure,unknown,error,zero_shape_images,overall_precision_vs_baseline,overall_recall_vs_baseline,overall_precision_vs_gt,overall_recall_vs_gt");
        foreach (string rule in new[] { "R1", "R2", "R3" })
        {
            foreach (string field in new[] { "f", "kept", "precision", "recall", "mean_iou", "wrong_count_imgs", "any_error_imgs", "w1_s", "c1_usd", "meets_4x", "meets_minus50" })
            {
                csv.Append(',').Append(rule).Append('_').Append(field);
            }
        }

        csv.Append('\n');
        foreach (JObject run in runs)
        {
            List<string> cells = new List<string>
            {
                Cell(run["k"]), Cell(run["strategy"]), Cell(run["status"]), Cell(run["train_seconds"]), Cell(run["epochs_run"]), Cell(run["early_stopped"]), Cell(run["train_timeout"]),
                Cell(run["infer_images"]), Cell(run["infer_seconds_total"]), Cell(run["infer_seconds_median"]),
                Cell(run.SelectToken("buckets.Sure")), Cell(run.SelectToken("buckets.Doubtful")), Cell(run.SelectToken("buckets.Unsure")), Cell(run.SelectToken("buckets.Unknown")), Cell(run.SelectToken("buckets.Error")),
                Cell(run["zero_shape_images"]),
                Cell(run.SelectToken("overall_vs_baseline.precision")), Cell(run.SelectToken("overall_vs_baseline.recall")), Cell(run.SelectToken("overall_vs_gt.precision")), Cell(run.SelectToken("overall_vs_gt.recall")),
            };
            foreach (string rule in new[] { "R1", "R2", "R3" })
            {
                string p = "rules." + rule + ".";
                cells.Add(Cell(run.SelectToken(p + "fallback_fraction_f")));
                cells.Add(Cell(run.SelectToken(p + "kept_local")));
                cells.Add(Cell(run.SelectToken(p + "kept_vs_baseline.precision")));
                cells.Add(Cell(run.SelectToken(p + "kept_vs_baseline.recall")));
                cells.Add(Cell(run.SelectToken(p + "kept_vs_baseline.mean_iou")));
                cells.Add(Cell(run.SelectToken(p + "kept_vs_baseline.wrong_shape_count_images")));
                cells.Add(Cell(run.SelectToken(p + "kept_vs_baseline.any_error_images")));
                cells.Add(Cell(run.SelectToken(p + "est_w1_seconds")));
                cells.Add(Cell(run.SelectToken(p + "est_c1_usd")));
                cells.Add(Cell(run.SelectToken(p + "meets_4x")));
                cells.Add(Cell(run.SelectToken(p + "meets_minus_50_percent_cost")));
            }

            csv.Append(string.Join(",", cells)).Append('\n');
        }

        AtomicWrite(Path.Combine(outRoot, "replay-summary.csv"), csv.ToString());
    }

    private static string Cell(JToken? token)
    {
        if (token == null || token.Type == JTokenType.Null)
        {
            return string.Empty;
        }

        return Csv(System.Convert.ToString(token, Inv) ?? string.Empty);
    }

    private static string Csv(string value)
    {
        string clean = value.Replace('\r', ' ').Replace('\n', ' ');
        return clean.IndexOfAny(new[] { ',', '"' }) >= 0 ? "\"" + clean.Replace("\"", "\"\"") + "\"" : clean;
    }

    private static void AtomicWrite(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + ".tmp";
        File.WriteAllText(temp, text, new UTF8Encoding(false));
        File.Move(temp, path, true);
    }

    private static double Dbl(string name, double fallback)
    {
        string? value = LabelRunEnv.Get(name);
        return value == null ? fallback : double.Parse(value, Inv);
    }

    private static double Median(IEnumerable<double> values)
    {
        double[] sorted = values.OrderBy(v => v).ToArray();
        if (sorted.Length == 0)
        {
            return 0;
        }

        return sorted.Length % 2 == 1 ? sorted[sorted.Length / 2] : (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2.0;
    }
}
