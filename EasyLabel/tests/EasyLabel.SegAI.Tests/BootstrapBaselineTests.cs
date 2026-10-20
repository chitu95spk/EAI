using System.Diagnostics;
using System.Globalization;
using System.Text;
using EasyAI.Desktop.Services;
using EasyAI.Desktop.Services.Agent;
using EasyLabel.Core;
using EasyLabel.Core.Io;
using EasyLabel.Core.Model;
using Newtonsoft.Json.Linq;
using Xunit.Abstractions;

namespace EasyLabel.SegAI.Tests;

/// <summary>Environment of the opt-in EAI-LABEL tests (EAI_LABEL_*). Nothing here touches %LOCALAPPDATA% or the E:\EAI_TEST data.</summary>
internal static class LabelRunEnv
{
    public const string ProtectedFolder = @"E:\EAI_TEST";

    public static string? Get(string name)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    public static bool Enabled => Get("EAI_LABEL_RUN") == "1";

    /// <summary>The repository root (the folder that holds both EasyLabel and artifacts), or null.</summary>
    public static string? RepoRoot()
    {
        DirectoryInfo? dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "EasyLabel")) && Directory.Exists(Path.Combine(dir.FullName, "artifacts")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        return null;
    }

    public static string? DefaultAppDir()
    {
        string? root = RepoRoot();
        string? candidate = root == null ? null : Path.Combine(root, "artifacts", "EAI-OKNG", "b5", "out");
        return candidate != null && Directory.Exists(candidate) ? candidate : null;
    }

    public static bool IsEqualOrUnder(string path, string folder)
    {
        string a = Path.GetFullPath(path).TrimEnd('\\', '/');
        string b = Path.GetFullPath(folder).TrimEnd('\\', '/');
        return string.Equals(a, b, StringComparison.OrdinalIgnoreCase) || a.StartsWith(b + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>Skips unless EAI_LABEL_RUN=1 and the folders named by <paramref name="required"/> env vars are set.</summary>
internal class LabelRunFactAttribute : FactAttribute
{
    public LabelRunFactAttribute(params string[] required)
    {
        if (!LabelRunEnv.Enabled)
        {
            Skip = "SKIPPED (nothing proven): opt-in, set EAI_LABEL_RUN=1 (the baseline spends the signed-in Claude account).";
            return;
        }

        foreach (string name in required)
        {
            if (LabelRunEnv.Get(name) == null)
            {
                Skip = "SKIPPED (nothing proven): set " + name + ".";
                return;
            }
        }
    }
}

/// <summary>
/// EAI-LABEL S0: headless measurement of TODAY'S Agent Prompt batch labelling (Remaining mode, local-first off, default model/effort,
/// default prompt suffix) over a COPY of the images. Opt-in (spends money): EAI_LABEL_RUN=1.
/// Env: EAI_LABEL_APPDIR (built EasyAI app with agent\; default artifacts\EAI-OKNG\b5\out), EAI_LABEL_PROJECT (a copy of the images; never E:\EAI_TEST),
/// EAI_LABEL_OUT (artifacts\EAI-LABEL\run), EAI_LABEL_MAX_USD (default 10), EAI_LABEL_PROMPT, EAI_LABEL_CLASSES (default OK),
/// EAI_LABEL_IMAGES (count, comma list of names, 'sample:&lt;n&gt;' or 'gt+ok:&lt;a&gt;,&lt;b&gt;'; see SelectImages), EAI_LABEL_MODELS (default %LOCALAPPDATA%\EasyAI\models, read only).
/// Lever env (all optional, defaults reproduce S0): EAI_LABEL_PARALLEL (default 1; N workers, each with its own AgentRunner and runs\w&lt;i&gt; folder),
/// EAI_LABEL_EXPERIENCE (1 = experience block of the first image, sequential only; default 1 when PARALLEL=1, else 0; PARALLEL&gt;1 with EXPERIENCE=1 is refused),
/// EAI_LABEL_ACCURACY (Easy|Normal|Hard, aliases Economy=Easy and Best=Hard; default Normal), EAI_LABEL_EFFORT (Economy|Low|Normal|Medium|Best|High|XHigh|Max; default Normal),
/// EAI_LABEL_TOOL (claude|codex, default claude), EAI_LABEL_REFERENCES (semicolon list of hint files attached to every image), EAI_LABEL_MODEL (default empty = the product default 'sonnet'; checked against AgentRunOptions), EAI_LABEL_DISABLED_SKILLS (comma list of built-in label skill names),
/// EAI_LABEL_GT (default E:\EAI_TEST, read only: only the *.json names are listed for the image selection forms).
/// Writes per-image.csv, summary.json, proposals\, and the LabelMe JSON of each image next to its copy under OUT\project.
/// </summary>
[Trait("Category", "Agent")]
public sealed class BootstrapBaselineTests
{
    private const string DefaultPrompt = "Label every fan impeller part on the tray with an oriented_rectangle, class OK.";
    private static readonly string[] ImageExtensions = { ".bmp", ".png", ".jpg", ".jpeg", ".tif", ".tiff" };

    private readonly ITestOutputHelper _out;

    public BootstrapBaselineTests(ITestOutputHelper output) => _out = output;

    private sealed class BaselineFactAttribute : LabelRunFactAttribute
    {
        public BaselineFactAttribute() : base("EAI_LABEL_PROJECT", "EAI_LABEL_OUT")
        {
            if (Skip == null && LabelRunEnv.Get("EAI_LABEL_APPDIR") == null && LabelRunEnv.DefaultAppDir() == null)
            {
                Skip = "SKIPPED (nothing proven): set EAI_LABEL_APPDIR (or build artifacts\\EAI-OKNG\\b5\\out).";
            }
        }
    }

    private sealed class Row
    {
        public string Rel = string.Empty;
        public double WallSeconds;
        public double CostUsd;
        public int Turns;
        public string Model = string.Empty;
        public long InputTokens;
        public long OutputTokens;
        public long CacheCreationTokens;
        public long CacheReadTokens;
        public int Proposals;
        public string Outcome = string.Empty;
        public string Error = string.Empty;
        public bool FromCache;
        public int Worker;
        public double StartOffsetSeconds;
        public double EndOffsetSeconds;
        public int SystemChars = -1;
        public bool Retried;
        public double Sam2Seconds;
        public int Sam2Shapes;
        public int Sam2Changed;
    }

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Conservative cost reserved per run in flight when a worker takes the next image (USD).</summary>
    private const double ReserveUsdPerInFlight = 0.35;

    private sealed class LeverConfig
    {
        public int Parallel = 1;
        public bool Experience = true;
        public AgentPrecision Precision = AgentPrecision.Normal;
        public AgentEffort Effort = AgentEffort.Normal;
        public string AccuracyRequested = "Normal";
        public string EffortRequested = "Normal";
        public string ModelRequested = string.Empty;
        public string[] DisabledSkills = Array.Empty<string>();
        public AgentRunOptions Options = AgentRunOptions.Default;
        public AgentTool Tool = AgentTool.Claude;
    }

    /// <summary>Reads and validates the lever env vars before anything is copied or spent. Throws InvalidOperationException on a bad value.</summary>
    private static LeverConfig ReadLevers()
    {
        LeverConfig config = new LeverConfig();
        string? parallelText = LabelRunEnv.Get("EAI_LABEL_PARALLEL");
        if (parallelText != null)
        {
            if (!int.TryParse(parallelText, NumberStyles.None, Inv, out int parallel) || parallel < 1 || parallel > 16)
            {
                throw new InvalidOperationException("EAI_LABEL_PARALLEL must be an integer from 1 to 16: " + parallelText);
            }

            config.Parallel = parallel;
        }

        string? experienceText = LabelRunEnv.Get("EAI_LABEL_EXPERIENCE");
        if (experienceText != null && experienceText != "0" && experienceText != "1")
        {
            throw new InvalidOperationException("EAI_LABEL_EXPERIENCE must be 0 or 1: " + experienceText);
        }

        if (config.Parallel > 1 && experienceText == "1")
        {
            throw new InvalidOperationException("EAI_LABEL_PARALLEL>1 cannot be combined with EAI_LABEL_EXPERIENCE=1 (the experience block comes from the previous image; it is sequential only).");
        }

        config.Experience = experienceText == null ? config.Parallel == 1 : experienceText == "1";

        config.AccuracyRequested = LabelRunEnv.Get("EAI_LABEL_ACCURACY") ?? "Normal";
        switch (config.AccuracyRequested.ToLowerInvariant())
        {
            case "easy":
            case "economy":
                config.Precision = AgentPrecision.Easy;
                break;
            case "normal":
                config.Precision = AgentPrecision.Normal;
                break;
            case "hard":
            case "best":
                config.Precision = AgentPrecision.Hard;
                break;
            default:
                throw new InvalidOperationException("EAI_LABEL_ACCURACY must be Easy|Normal|Hard (Economy=Easy, Best=Hard): " + config.AccuracyRequested);
        }

        config.EffortRequested = LabelRunEnv.Get("EAI_LABEL_EFFORT") ?? "Normal";
        string[] knownEfforts = { "economy", "low", "normal", "medium", "best", "high", "xhigh", "max" };
        if (!knownEfforts.Contains(config.EffortRequested.ToLowerInvariant()))
        {
            throw new InvalidOperationException("EAI_LABEL_EFFORT must be one of " + string.Join("|", knownEfforts) + ": " + config.EffortRequested);
        }

        config.Effort = AgentRunOptions.ParseEffort(config.EffortRequested);

        config.ModelRequested = LabelRunEnv.Get("EAI_LABEL_MODEL") ?? string.Empty;
        config.Options = new AgentRunOptions(config.Precision, config.Effort, config.ModelRequested.Length == 0 ? null : config.ModelRequested);
        if (config.ModelRequested.Length > 0 && !string.Equals(config.Options.ClaudeModel, config.ModelRequested, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("EAI_LABEL_MODEL was rejected by AgentRunOptions (not a plain model id): " + config.ModelRequested);
        }

        string toolText = (LabelRunEnv.Get("EAI_LABEL_TOOL") ?? "claude").ToLowerInvariant();
        if (toolText != "claude" && toolText != "codex")
        {
            throw new InvalidOperationException("EAI_LABEL_TOOL must be claude or codex: " + toolText);
        }

        config.Tool = toolText == "codex" ? AgentTool.Codex : AgentTool.Claude;

        string? skillsText = LabelRunEnv.Get("EAI_LABEL_DISABLED_SKILLS");
        if (skillsText != null)
        {
            config.DisabledSkills = skillsText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            IReadOnlyList<string> known = RunSkillCatalog.BuiltInNamesFor(AgentRunKind.Label);
            foreach (string name in config.DisabledSkills)
            {
                if (!known.Contains(name, StringComparer.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("EAI_LABEL_DISABLED_SKILLS: '" + name + "' is not a built-in label skill (" + string.Join(",", known) + ").");
                }
            }
        }

        return config;
    }

    [BaselineFact]
    public async Task MeasureAgentPromptBatchLabelling()
    {
        LeverConfig levers = ReadLevers();
        string appDir = Path.GetFullPath(LabelRunEnv.Get("EAI_LABEL_APPDIR") ?? LabelRunEnv.DefaultAppDir()!);
        string project = Path.GetFullPath(LabelRunEnv.Get("EAI_LABEL_PROJECT")!);
        string outDir = Path.GetFullPath(LabelRunEnv.Get("EAI_LABEL_OUT")!);
        if (LabelRunEnv.IsEqualOrUnder(project, LabelRunEnv.ProtectedFolder) || LabelRunEnv.IsEqualOrUnder(outDir, LabelRunEnv.ProtectedFolder))
        {
            throw new InvalidOperationException("REFUSED: EAI_LABEL_PROJECT / EAI_LABEL_OUT must be a copy outside " + LabelRunEnv.ProtectedFolder + ".");
        }

        string? repoRoot = LabelRunEnv.RepoRoot();
        if (repoRoot != null)
        {
            foreach (string finished in new[] { "s0-baseline", "s1-replay" })
            {
                if (LabelRunEnv.IsEqualOrUnder(outDir, Path.Combine(repoRoot, "artifacts", "EAI-LABEL", finished)))
                {
                    throw new InvalidOperationException("REFUSED: EAI_LABEL_OUT must not be inside the finished result folder " + finished + ".");
                }
            }
        }

        Assert.True(Directory.Exists(project), "EAI_LABEL_PROJECT does not exist: " + project);
        double maxUsd = double.Parse(LabelRunEnv.Get("EAI_LABEL_MAX_USD") ?? "10", CultureInfo.InvariantCulture);
        string prompt = LabelRunEnv.Get("EAI_LABEL_PROMPT") ?? DefaultPrompt;
        string[] classNames = (LabelRunEnv.Get("EAI_LABEL_CLASSES") ?? "OK").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string[] referencePaths = (LabelRunEnv.Get("EAI_LABEL_REFERENCES") ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(Path.GetFullPath).ToArray();
        string modelsFolder = LabelRunEnv.Get("EAI_LABEL_MODELS") ?? Environment.ExpandEnvironmentVariables(@"%LOCALAPPDATA%\EasyAI\models");

        Directory.CreateDirectory(outDir);
        string csvPath = Path.Combine(outDir, "per-image.csv");
        string summaryPath = Path.Combine(outDir, "summary.json");
        Assert.False(File.Exists(csvPath) || File.Exists(summaryPath), "EAI_LABEL_OUT already holds a run; use a new folder: " + outDir);

        // The images only (no LabelMe JSON, so no ground truth leaks into the run), ordered by file name, copied under OUT\project.
        string projectCopy = Path.Combine(outDir, "project");
        string gtFolder = Path.GetFullPath(LabelRunEnv.Get("EAI_LABEL_GT") ?? LabelRunEnv.ProtectedFolder);
        List<string> rels = SelectImages(project, LabelRunEnv.Get("EAI_LABEL_IMAGES"), gtFolder, out string selectionRule);
        Assert.NotEmpty(rels);
        foreach (string rel in rels)
        {
            string target = Path.Combine(projectCopy, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(Path.Combine(project, rel.Replace('/', Path.DirectorySeparatorChar)), target, true);
        }

        ProjectPaths paths = ProjectPaths.Legacy(projectCopy);
        // One AgentRunner per worker: a runner holds a single live process slot. PARALLEL=1 keeps the S0 folder OUT\runs; otherwise OUT\runs\w<i>.
        string cacheRoot = Path.Combine(outDir, "cache");
        string[] runsRoots = new string[levers.Parallel];
        AgentRunner[] runners = new AgentRunner[levers.Parallel];
        for (int w = 0; w < levers.Parallel; w++)
        {
            runsRoots[w] = levers.Parallel == 1 ? Path.Combine(outDir, "runs") : Path.Combine(outDir, "runs", "w" + w.ToString(Inv));
            runners[w] = new AgentRunner(new SystemAgentEnvironment(), appDir, runsRoots[w], cacheRoot)
            {
                Options = levers.Options,
                Tool = levers.Tool,
                DisabledSkills = levers.DisabledSkills.Length == 0 ? null : levers.DisabledSkills,
            };
        }

        // Opt-in levers that mirror the product batch (default off): EAI_LABEL_RETRY_NORMAL=1 (Economy only) and EAI_LABEL_SAM2_REFINE=1.
        bool retryNormal = LabelRunEnv.Get("EAI_LABEL_RETRY_NORMAL") == "1" && levers.Effort == AgentEffort.Economy;
        bool sam2Refine = LabelRunEnv.Get("EAI_LABEL_SAM2_REFINE") == "1";
        AgentRunner[] retryRunners = new AgentRunner[levers.Parallel];
        if (retryNormal)
        {
            for (int w = 0; w < levers.Parallel; w++)
            {
                retryRunners[w] = new AgentRunner(new SystemAgentEnvironment(), appDir, Path.Combine(outDir, "runs-retry", "w" + w.ToString(Inv)), cacheRoot)
                {
                    Options = new AgentRunOptions(levers.Precision, AgentEffort.Normal, levers.ModelRequested.Length == 0 ? null : levers.ModelRequested),
                    Tool = levers.Tool,
                    DisabledSkills = levers.DisabledSkills.Length == 0 ? null : levers.DisabledSkills,
                };
            }
        }

        AgentSam2Refiner? refiner = null;
        if (sam2Refine)
        {
            string sam2Folder = Environment.ExpandEnvironmentVariables(@"%LOCALAPPDATA%\EasyAI\models\sam2");
            refiner = AgentSam2Refiner.CreateLocal(sam2Folder, (imagePath, loadToken) =>
            {
                using OpenCvSharp.Mat mat = OpenCvSharp.Cv2.ImRead(imagePath, OpenCvSharp.ImreadModes.Color);
                if (mat.Empty() || !mat.IsContinuous())
                {
                    throw new InvalidOperationException("Cannot decode " + imagePath);
                }

                byte[] pixels = new byte[mat.Width * mat.Height * 3];
                System.Runtime.InteropServices.Marshal.Copy(mat.Data, pixels, 0, pixels.Length);
                return new EasyLabel.Ai.Onnx.AiImage(pixels, mat.Width, mat.Height, imagePath);
            });
        }

        AgentRunner agent = runners[0];
        string? claudePath = levers.Tool == AgentTool.Codex ? agent.FindAgent() : agent.FindClaude();
        string claudeVersion = ClaudeVersion(claudePath);
        _out.WriteLine("claude=" + claudePath + " (" + claudeVersion + ") mcp=" + agent.FindMcp() + " images=" + rels.Count + " maxUsd=" + maxUsd.ToString(Inv)
            + " parallel=" + levers.Parallel + " experience=" + levers.Experience + " model=" + levers.Options.ClaudeModel + " effort=" + levers.Options.EffortLevel + " accuracy=" + levers.Precision);
        Assert.NotNull(claudePath);
        Assert.NotNull(agent.FindMcp());
        string effectivePrompt = levers.Options.PromptSuffix.Length > 0 ? prompt + " " + levers.Options.PromptSuffix : prompt;
        AgentRunOptions labelOptions = levers.Options.ForKind(AgentRunKind.Label);
        JObject config = new JObject
        {
            ["parallel"] = levers.Parallel,
            ["retry_normal"] = retryNormal,
            ["sam2_refine"] = sam2Refine,
            ["experience"] = levers.Experience,
            ["accuracy"] = levers.Precision.ToString(),
            ["accuracy_requested"] = levers.AccuracyRequested,
            ["effort"] = levers.Effort.ToString(),
            ["effort_requested"] = levers.EffortRequested,
            ["effort_flag"] = levers.Options.ClaudeEffort,
            ["model"] = levers.Options.ClaudeModel,
            ["model_requested"] = levers.ModelRequested,
            ["max_turns"] = labelOptions.MaxTurns,
            ["prompt_suffix"] = levers.Options.PromptSuffix,
            ["prompt_effective"] = effectivePrompt,
            ["disabled_skills"] = new JArray(levers.DisabledSkills),
            ["claude_path"] = claudePath,
            ["claude_cli_version"] = claudeVersion,
            ["image_selection"] = LabelRunEnv.Get("EAI_LABEL_IMAGES") ?? "all",
            ["image_selection_rule"] = selectionRule,
            ["images"] = new JArray(rels),
            ["max_usd"] = maxUsd,
            ["cost_reservation_rule"] = "a worker takes the next image only while spent + (runs in flight) * " + ReserveUsdPerInFlight.ToString("F2", Inv) + " < max_usd; with nothing in flight it stops once spent >= max_usd (a started run may overshoot by its own cost)",
        };

        // Mirrors LabelStepViewModel.RunBatchAsync: Remaining plan, digest and vocabulary frozen once, caps from the test.
        IReadOnlyList<string> plan = AgentBatchPlanner.Plan(rels.Select(r => new AgentBatchImage(r, false, false, false)).ToList(), AgentBatchMode.Remaining, false);
        AgentBatchCaps caps = new AgentBatchCaps(Math.Min(AgentBatchCaps.MaxMaxImages, plan.Count), maxUsd);
        string digest = AgentRunFiles.OutcomeDigest(paths.AgentOutcomesFile);
        string vocabulary = AgentRunFiles.VocabularyBlockOf(paths.VocabularyFile);

        List<Row> rows = new List<Row>();
        object gate = new object();
        Stopwatch total = Stopwatch.StartNew();
        Stopwatch batch = new Stopwatch();
        string stoppedBy = "in_progress";
        string runError = string.Empty;
        int notProcessed = 0;

        void Flush()
        {
            lock (gate)
            {
                File.WriteAllText(csvPath, BuildCsv(rows), new UTF8Encoding(false));
                File.WriteAllText(summaryPath, BuildSummary(rows, total.Elapsed.TotalSeconds, batch.Elapsed.TotalSeconds, plan.Count, caps, stoppedBy, runError, notProcessed, effectivePrompt, classNames, appDir, config).ToString(Newtonsoft.Json.Formatting.Indented), new UTF8Encoding(false));
            }
        }

        Task<AgentRunInput?> LoadInput(string rel)
        {
            string path = Path.Combine(projectCopy, rel.Replace('/', Path.DirectorySeparatorChar));
            using OpenCvSharp.Mat image = OpenCvSharp.Cv2.ImRead(path, OpenCvSharp.ImreadModes.Color);
            if (image.Empty())
            {
                return Task.FromResult<AgentRunInput?>(null);
            }

            return Task.FromResult<AgentRunInput?>(new AgentRunInput(
                path, rel, image.Width, image.Height, classNames, Array.Empty<AgentShapeInfo>(), effectivePrompt, modelsFolder, 0, true,
                paths.AgentMemoryFile, digest, null, null, AgentRunKind.Label, vocabulary, referencePaths));
        }

        async Task<AgentRunResult> MeasuredRun(int worker, AgentRunInput input, CancellationToken ct, IProgress<AgentStreamEvent>? live)
        {
            HashSet<string> foldersBefore = RunFolders(runsRoots[worker]);
            double startOffset = batch.Elapsed.TotalSeconds;
            Stopwatch sw = Stopwatch.StartNew();
            AgentRunResult result;
            bool retried = false;
            double sam2Seconds = 0;
            int sam2Shapes = 0;
            int sam2Changed = 0;
            try
            {
                result = await runners[worker].RunAsync(input, ct);
                if (retryNormal && !ct.IsCancellationRequested && AgentBatchRunner.ShouldRetry(result))
                {
                    retried = true;
                    AgentRunResult second = await retryRunners[worker].RunAsync(input, ct);
                    result = second with { CostUsd = result.CostUsd + second.CostUsd };
                }

                if (refiner != null && result.Kind == AgentOutcomeKind.Success && !ct.IsCancellationRequested)
                {
                    Stopwatch refineWatch = Stopwatch.StartNew();
                    AgentRunResult refined = await refiner.RefineAsync(input, result, ct);
                    refineWatch.Stop();
                    sam2Seconds = refineWatch.Elapsed.TotalSeconds;
                    sam2Shapes = refined.Proposals.Count;
                    for (int i = 0; i < refined.Proposals.Count && i < result.Proposals.Count; i++)
                    {
                        if (!ReferenceEquals(refined.Proposals[i], result.Proposals[i]))
                        {
                            sam2Changed++;
                        }
                    }

                    result = refined;
                }
            }
            catch (Exception ex)
            {
                sw.Stop();
                lock (gate)
                {
                    rows.Add(new Row { Rel = input.Rel, WallSeconds = sw.Elapsed.TotalSeconds, Outcome = "exception", Error = ex.Message, Worker = worker, StartOffsetSeconds = startOffset, EndOffsetSeconds = batch.Elapsed.TotalSeconds });
                }

                Flush();
                throw;
            }

            sw.Stop();
            double endOffset = batch.Elapsed.TotalSeconds;
            int systemChars = SystemChars(runsRoots[worker], foldersBefore);
            lock (gate)
            {
                rows.Add(new Row
                {
                    Rel = input.Rel,
                    WallSeconds = sw.Elapsed.TotalSeconds,
                    CostUsd = result.CostUsd,
                    Turns = result.Turns,
                    Model = result.Model,
                    InputTokens = result.Usage.InputTokens,
                    OutputTokens = result.Usage.OutputTokens,
                    CacheCreationTokens = result.Usage.CacheCreationTokens,
                    CacheReadTokens = result.Usage.CacheReadTokens,
                    Proposals = result.Proposals.Count,
                    Outcome = result.Kind.ToString(),
                    Error = result.Kind == AgentOutcomeKind.Success ? string.Empty : (result.Message.Length > 0 ? result.Message : result.ResultText),
                    FromCache = result.FromCache,
                    Worker = worker,
                    StartOffsetSeconds = startOffset,
                    EndOffsetSeconds = endOffset,
                    SystemChars = systemChars,
                    Retried = retried,
                    Sam2Seconds = sam2Seconds,
                    Sam2Shapes = sam2Shapes,
                    Sam2Changed = sam2Changed,
                });
            }

            WriteProposals(outDir, input.Rel, result);
            Flush();
            return result;
        }

        int Apply(string rel, AgentRunInput input, AgentRunResult result)
        {
            // Not AutoLabelService.Accept (it forces polygon): keep shape_type and the 4 corners of each proposal.
            LabelMeDocument document = new LabelMeDocument
            {
                ImagePath = Path.GetFileName(rel),
                ImageWidth = input.ImageWidth,
                ImageHeight = input.ImageHeight,
            };
            foreach (AgentProposal proposal in result.Proposals)
            {
                if (proposal.Action != null)
                {
                    continue;
                }

                document.Shapes.Add(new LabelShape
                {
                    Label = proposal.Label,
                    ShapeType = proposal.ShapeType,
                    Points = proposal.Points.Select(p => new[] { p[0], p[1] }).ToList(),
                });
            }

            string jsonPath = Path.ChangeExtension(Path.Combine(projectCopy, rel.Replace('/', Path.DirectorySeparatorChar)), ".json");
            new LabelMeIO().Write(document, jsonPath);
            return document.Shapes.Count;
        }

        int succeeded;
        bool errorStop;
        try
        {
            batch.Start();
            if (levers.Parallel == 1 && levers.Experience)
            {
                // Today's behaviour: the real sequential batch runner with the experience block of the first labelled image.
                AgentBatchRunner runner = new AgentBatchRunner((input, ct, live) => MeasuredRun(0, input, ct, live));
                AgentBatchReport report = await runner.RunAsync(plan, caps, LoadInput, Apply, null, CancellationToken.None);
                stoppedBy = report.Stop.ToString();
                notProcessed = report.NotProcessed.Count;
                succeeded = report.Progress.Succeeded;
                errorStop = report.Stop == AgentBatchStop.Error;
                if (report.Failures.Count > 0)
                {
                    runError = string.Join(" | ", report.Failures.Take(5).Select(f => f.Rel + ": " + f.Message));
                }
            }
            else
            {
                // Independent images pulled from one ordered queue by N workers, each with its own runner. No experience block.
                int nextIndex = 0;
                int inFlight = 0;
                int done = 0;
                double spentUsd = 0;
                string? stopReason = null;
                List<string> failures = new List<string>();

                async Task WorkerLoop(int worker)
                {
                    while (true)
                    {
                        string? rel = null;
                        bool wait = false;
                        lock (gate)
                        {
                            if (stopReason != null || nextIndex >= plan.Count)
                            {
                                return;
                            }

                            if (nextIndex >= caps.MaxImages)
                            {
                                stopReason = "ImageCap";
                                return;
                            }

                            if (spentUsd + inFlight * ReserveUsdPerInFlight >= maxUsd)
                            {
                                if (inFlight == 0)
                                {
                                    stopReason = "CostCap";
                                    return;
                                }

                                wait = true;
                            }
                            else
                            {
                                rel = plan[nextIndex++];
                                inFlight++;
                            }
                        }

                        if (wait || rel == null)
                        {
                            await Task.Delay(500);
                            continue;
                        }

                        AgentRunInput? input = null;
                        string loadError = "The image or its shapes could not be read.";
                        try
                        {
                            input = await LoadInput(rel);
                        }
                        catch (Exception ex)
                        {
                            loadError = ex.Message;
                        }

                        if (input == null)
                        {
                            lock (gate)
                            {
                                inFlight--;
                                failures.Add(rel + ": " + loadError);
                            }

                            continue;
                        }

                        AgentRunResult result;
                        try
                        {
                            result = await MeasuredRun(worker, input, CancellationToken.None, null);
                        }
                        catch (Exception ex)
                        {
                            lock (gate)
                            {
                                inFlight--;
                                failures.Add(rel + ": " + ex.Message);
                            }

                            continue;
                        }

                        string message = result.Message.Length > 0 ? result.Message : result.ResultText;
                        AgentBatchStep step = AgentBatchPlanner.Classify(result.Kind, message);
                        if (result.Kind == AgentOutcomeKind.Success)
                        {
                            Apply(rel, input, result);
                        }

                        lock (gate)
                        {
                            inFlight--;
                            spentUsd += result.CostUsd;
                            if (result.Kind == AgentOutcomeKind.Success)
                            {
                                done++;
                            }
                            else
                            {
                                failures.Add(rel + ": " + (message.Length > 0 ? message : result.Kind.ToString()));
                            }

                            if (step == AgentBatchStep.Stop)
                            {
                                stopReason ??= "Error";
                            }
                            else if (step == AgentBatchStep.Cancelled)
                            {
                                stopReason ??= "Cancelled";
                            }
                        }
                    }
                }

                await Task.WhenAll(Enumerable.Range(0, levers.Parallel).Select(w => Task.Run(() => WorkerLoop(w))));
                lock (gate)
                {
                    stoppedBy = stopReason ?? "Done";
                    notProcessed = stopReason == "ImageCap" || stopReason == "CostCap" ? plan.Count - nextIndex : 0;
                    succeeded = done;
                    errorStop = stopReason == "Error";
                    if (failures.Count > 0)
                    {
                        runError = string.Join(" | ", failures.Take(5));
                    }
                }
            }
        }
        catch (Exception ex)
        {
            stoppedBy = ex is OperationCanceledException ? "cancelled" : "exception";
            runError = ex.Message;
            throw;
        }
        finally
        {
            batch.Stop();
            refiner?.Dispose();
            Flush();
        }

        _out.WriteLine("stop=" + stoppedBy + " done=" + rows.Count + " usd=" + rows.Sum(r => r.CostUsd).ToString("F4", Inv) + " batchWall=" + batch.Elapsed.TotalSeconds.ToString("F1", Inv) + "s sumAgent=" + rows.Sum(r => r.WallSeconds).ToString("F1", Inv) + "s out=" + outDir);
        Assert.False(errorStop, "The batch stopped on an error: " + runError);
        Assert.True(succeeded > 0, "No image was labelled: " + runError);
    }

    private static HashSet<string> RunFolders(string runsRoot)
    {
        HashSet<string> names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (Directory.Exists(runsRoot))
        {
            foreach (string dir in Directory.GetDirectories(runsRoot))
            {
                names.Add(Path.GetFileName(dir));
            }
        }

        return names;
    }

    /// <summary>Character count of the system.md of the run folder created since <paramref name="before"/> was taken; -1 when there is none (a cache hit) or it cannot be read.</summary>
    private static int SystemChars(string runsRoot, HashSet<string> before)
    {
        try
        {
            if (!Directory.Exists(runsRoot))
            {
                return -1;
            }

            foreach (string dir in Directory.GetDirectories(runsRoot))
            {
                string name = Path.GetFileName(dir);
                if (before.Contains(name) || !AgentRunRetention.IsRunFolderName(name))
                {
                    continue;
                }

                string file = Path.Combine(dir, AgentRunFiles.SystemPromptFileName);
                if (File.Exists(file))
                {
                    return File.ReadAllText(file).Length;
                }
            }
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            return -1;
        }

        return -1;
    }

    /// <summary>Output of <c>claude --version</c> (a .cmd shim is started through cmd.exe), or a text starting with "unknown".</summary>
    private static string ClaudeVersion(string? claudePath)
    {
        if (claudePath == null)
        {
            return "unknown (claude not found)";
        }

        try
        {
            bool shim = claudePath.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || claudePath.EndsWith(".bat", StringComparison.OrdinalIgnoreCase);
            ProcessStartInfo info = new ProcessStartInfo
            {
                FileName = shim ? "cmd.exe" : claudePath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            if (shim)
            {
                info.ArgumentList.Add("/c");
                info.ArgumentList.Add(claudePath);
            }

            info.ArgumentList.Add("--version");
            using Process? process = Process.Start(info);
            if (process == null)
            {
                return "unknown (not started)";
            }

            Task<string> output = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(20000))
            {
                try { process.Kill(true); } catch (Exception) { }
                return "unknown (timeout)";
            }

            string text = output.GetAwaiter().GetResult().Trim().Replace('\r', ' ').Replace('\n', ' ');
            return text.Length == 0 ? "unknown (empty output)" : text;
        }
        catch (Exception ex)
        {
            return "unknown (" + ex.Message + ")";
        }
    }

    private static bool HasTag(string rel, string tag) => Path.GetFileNameWithoutExtension(rel).EndsWith(tag, StringComparison.OrdinalIgnoreCase);

    private static HashSet<string> GroundTruthNames(string gtFolder)
    {
        if (!Directory.Exists(gtFolder))
        {
            throw new InvalidOperationException("The ground-truth folder (EAI_LABEL_GT) is needed to select images by ground truth and was not found: " + gtFolder);
        }

        return new HashSet<string>(Directory.GetFiles(gtFolder, "*.json", SearchOption.TopDirectoryOnly).Select(p => Path.GetFileNameWithoutExtension(p)), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Images of the project in name order. Filter forms: none (all), a count (the first n), a comma list of names, 'sample:&lt;n&gt;' (n images
    /// alternating *_NG and *_OK in name order over the images that are not ground-truth images, starting with the kind of the first such image; when one
    /// kind runs out the other fills up) or 'gt+ok:&lt;a&gt;,&lt;b&gt;' (the first a ground-truth images, then the first b *_OK images not yet chosen). The result is processed in name order.
    /// </summary>
    private static List<string> SelectImages(string project, string? filter, string gtFolder, out string rule)
    {
        List<string> all = ListImages(project);
        if (filter != null && filter.StartsWith("sample:", StringComparison.OrdinalIgnoreCase))
        {
            if (!int.TryParse(filter.Substring("sample:".Length).Trim(), NumberStyles.None, Inv, out int n) || n < 1)
            {
                throw new InvalidOperationException("EAI_LABEL_IMAGES sample:<n> needs a positive integer: " + filter);
            }

            HashSet<string> gt = GroundTruthNames(gtFolder);
            List<string> candidates = all.Where(r => !gt.Contains(Path.GetFileNameWithoutExtension(r))).ToList();
            List<string> ng = candidates.Where(r => HasTag(r, "_NG")).ToList();
            List<string> ok = candidates.Where(r => HasTag(r, "_OK")).ToList();
            bool takeNg = candidates.Count == 0 || !HasTag(candidates[0], "_OK");
            int ngIndex = 0;
            int okIndex = 0;
            List<string> picked = new List<string>();
            while (picked.Count < n && (ngIndex < ng.Count || okIndex < ok.Count))
            {
                if (takeNg && ngIndex < ng.Count)
                {
                    picked.Add(ng[ngIndex++]);
                }
                else if (!takeNg && okIndex < ok.Count)
                {
                    picked.Add(ok[okIndex++]);
                }
                else if (ngIndex < ng.Count)
                {
                    picked.Add(ng[ngIndex++]);
                }
                else
                {
                    picked.Add(ok[okIndex++]);
                }

                takeNg = !takeNg;
            }

            picked.Sort(StringComparer.OrdinalIgnoreCase);
            rule = "sample:" + n.ToString(Inv) + " = over the " + candidates.Count.ToString(Inv) + " images that are not among the " + gt.Count.ToString(Inv)
                + " ground-truth names (" + Path.GetFileName(gtFolder.TrimEnd('\\', '/')) + "\\*.json), alternate *_NG and *_OK in name order starting with the kind of the first such image ("
                + (candidates.Count > 0 && HasTag(candidates[0], "_OK") ? "OK" : "NG") + "); when a kind runs out the other fills up; processed in name order";
            return picked;
        }

        if (filter != null && filter.StartsWith("gt+ok:", StringComparison.OrdinalIgnoreCase))
        {
            string[] parts = filter.Substring("gt+ok:".Length).Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length != 2 || !int.TryParse(parts[0], NumberStyles.None, Inv, out int a) || !int.TryParse(parts[1], NumberStyles.None, Inv, out int b))
            {
                throw new InvalidOperationException("EAI_LABEL_IMAGES gt+ok:<a>,<b> needs two non-negative integers: " + filter);
            }

            HashSet<string> gt = GroundTruthNames(gtFolder);
            List<string> first = all.Where(r => gt.Contains(Path.GetFileNameWithoutExtension(r))).Take(a).ToList();
            List<string> second = all.Where(r => HasTag(r, "_OK") && !first.Contains(r)).Take(b).ToList();
            List<string> union = first.Concat(second).ToList();
            union.Sort(StringComparer.OrdinalIgnoreCase);
            rule = "gt+ok:" + a.ToString(Inv) + "," + b.ToString(Inv) + " = the first " + a.ToString(Inv) + " ground-truth images (" + first.Count.ToString(Inv)
                + " found) in name order, then the first " + b.ToString(Inv) + " *_OK images not already chosen (" + second.Count.ToString(Inv) + " found); processed in name order";
            return union;
        }

        List<string> chosen = SelectImages(all, filter);
        rule = filter == null ? "all images in name order" : (int.TryParse(filter, NumberStyles.None, Inv, out _) ? "the first n images in name order" : "images named in the list, in name order");
        return chosen;
    }

    private static List<string> ListImages(string project)
    {
        List<string> all = new List<string>();
        foreach (string file in Directory.EnumerateFiles(project, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(project, file).Replace('\\', '/');
            if (rel.Split('/').Any(part => part.StartsWith('.')) || !ImageExtensions.Contains(Path.GetExtension(file).ToLowerInvariant()))
            {
                continue;
            }

            all.Add(rel);
        }

        all.Sort(StringComparer.OrdinalIgnoreCase);
        return all;
    }

    private static List<string> SelectImages(List<string> all, string? filter)
    {
        if (filter == null)
        {
            return all;
        }

        if (int.TryParse(filter, NumberStyles.None, CultureInfo.InvariantCulture, out int count))
        {
            return all.Take(count).ToList();
        }

        HashSet<string> wanted = new HashSet<string>(filter.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), StringComparer.OrdinalIgnoreCase);
        return all.Where(r => wanted.Contains(r) || wanted.Contains(Path.GetFileName(r)) || wanted.Contains(Path.GetFileNameWithoutExtension(r))).ToList();
    }

    private static void WriteProposals(string outDir, string rel, AgentRunResult result)
    {
        JArray array = new JArray(result.Proposals.Select(p => new JObject
        {
            ["label"] = p.Label,
            ["shapeType"] = p.ShapeType,
            ["action"] = p.Action,
            ["score"] = p.Score,
            ["note"] = p.Note,
            ["points"] = new JArray(p.Points.Select(pt => new JArray(pt[0], pt[1]))),
        }));
        string path = Path.Combine(outDir, "proposals", rel.Replace('/', Path.DirectorySeparatorChar) + ".json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, array.ToString(Newtonsoft.Json.Formatting.Indented), new UTF8Encoding(false));
    }

    private static string Csv(string value)
    {
        string clean = value.Replace('\r', ' ').Replace('\n', ' ');
        return clean.IndexOfAny(new[] { ',', '"' }) >= 0 ? "\"" + clean.Replace("\"", "\"\"") + "\"" : clean;
    }

    private static string BuildCsv(IReadOnlyList<Row> rows)
    {
        CultureInfo c = CultureInfo.InvariantCulture;
        StringBuilder text = new StringBuilder("rel,wall_seconds,cost_usd,turns,model,input_tokens,output_tokens,cache_creation_tokens,cache_read_tokens,proposals,outcome,from_cache,error,worker,start_offset_seconds,end_offset_seconds,system_chars\n");
        foreach (Row r in rows)
        {
            text.Append(Csv(r.Rel)).Append(',').Append(r.WallSeconds.ToString("F2", c)).Append(',').Append(r.CostUsd.ToString("F5", c)).Append(',')
                .Append(r.Turns.ToString(c)).Append(',').Append(Csv(r.Model)).Append(',').Append(r.InputTokens.ToString(c)).Append(',')
                .Append(r.OutputTokens.ToString(c)).Append(',').Append(r.CacheCreationTokens.ToString(c)).Append(',').Append(r.CacheReadTokens.ToString(c)).Append(',')
                .Append(r.Proposals.ToString(c)).Append(',').Append(r.Outcome).Append(',').Append(r.FromCache ? "1" : "0").Append(',').Append(Csv(r.Error)).Append(',')
                .Append(r.Worker.ToString(c)).Append(',').Append(r.StartOffsetSeconds.ToString("F2", c)).Append(',').Append(r.EndOffsetSeconds.ToString("F2", c)).Append(',').Append(r.SystemChars.ToString(c)).Append('\n');
        }

        return text.ToString();
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

    private static JObject BuildSummary(IReadOnlyList<Row> rows, double elapsedSeconds, double batchSeconds, int planned, AgentBatchCaps caps, string stoppedBy, string error, int notProcessed, string prompt, IReadOnlyList<string> classNames, string appDir, JObject config)
    {
        JObject summary = new JObject
        {
            ["wall_seconds_batch"] = Math.Round(batchSeconds, 2),
            ["sum_agent_seconds"] = Math.Round(rows.Sum(r => r.WallSeconds), 2),
            ["parallel"] = config["parallel"]?.DeepClone(),
            ["config"] = config.DeepClone(),
            ["images_done"] = rows.Count,
            ["images_succeeded"] = rows.Count(r => r.Outcome == nameof(AgentOutcomeKind.Success)),
            ["images_planned"] = planned,
            ["not_processed"] = notProcessed,
            ["total_wall_seconds"] = Math.Round(rows.Sum(r => r.WallSeconds), 2),
            ["elapsed_seconds"] = Math.Round(elapsedSeconds, 2),
            ["total_usd"] = Math.Round(rows.Sum(r => r.CostUsd), 5),
            ["median_seconds_per_image"] = Math.Round(Median(rows.Select(r => r.WallSeconds)), 2),
            ["median_usd_per_image"] = Math.Round(Median(rows.Select(r => r.CostUsd)), 5),
            ["total_proposals"] = rows.Sum(r => r.Proposals),
            ["retries"] = rows.Count(r => r.Retried),
            ["sam2_seconds_total"] = Math.Round(rows.Sum(r => r.Sam2Seconds), 2),
            ["sam2_shapes"] = rows.Sum(r => r.Sam2Shapes),
            ["sam2_shapes_changed"] = rows.Sum(r => r.Sam2Changed),
            ["stopped_by"] = stoppedBy,
            ["cap_images"] = caps.MaxImages,
            ["cap_usd"] = caps.MaxCostUsd,
            ["error"] = error,
            ["prompt"] = prompt,
            ["classes"] = new JArray(classNames),
            ["app_dir"] = appDir,
        };
        return summary;
    }
}

/// <summary>EAI-LABEL S0: scores the labelling in EAI_LABEL_OUT\project against the ground-truth LabelMe JSON (read only). Opt-in: EAI_LABEL_RUN=1.</summary>
[Trait("Category", "Agent")]
public sealed class LabelMetricsOnGroundTruthTests
{
    private readonly ITestOutputHelper _out;

    public LabelMetricsOnGroundTruthTests(ITestOutputHelper output) => _out = output;

    [LabelRunFact("EAI_LABEL_OUT")]
    public void ScoreOutputAgainstGroundTruth()
    {
        string outDir = Path.GetFullPath(LabelRunEnv.Get("EAI_LABEL_OUT")!);
        string gt = Path.GetFullPath(LabelRunEnv.Get("EAI_LABEL_GT") ?? LabelRunEnv.ProtectedFolder);
        string predictions = Path.Combine(outDir, "project");
        double threshold = double.Parse(LabelRunEnv.Get("EAI_LABEL_IOU") ?? "0.5", CultureInfo.InvariantCulture);
        if (LabelRunEnv.IsEqualOrUnder(outDir, gt))
        {
            throw new InvalidOperationException("REFUSED: EAI_LABEL_OUT must not be inside the ground-truth folder.");
        }

        Assert.True(Directory.Exists(gt), "Ground-truth folder not found: " + gt);
        Assert.True(Directory.Exists(predictions), "No predictions folder (run BootstrapBaselineTests first): " + predictions);

        FolderQuality quality = LabelMetrics.EvaluateFolder(predictions, gt, threshold);
        File.WriteAllText(Path.Combine(outDir, "quality.json"), LabelMetrics.ToJson(quality), new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(outDir, "quality.csv"), LabelMetrics.ToCsv(quality), new UTF8Encoding(false));
        _out.WriteLine(string.Format(CultureInfo.InvariantCulture, "images={0} missing={1} tp={2} fp={3} fn={4} precision={5:F3} recall={6:F3} f1={7:F3} meanIoU={8:F3}",
            quality.Images.Count, quality.MissingPredictions, quality.TruePositives, quality.FalsePositives, quality.FalseNegatives, quality.Precision, quality.Recall, quality.F1, quality.MeanIou));
        Assert.NotEmpty(quality.Images);
    }
}

/// <summary>
/// EAI-LABEL S1b: scores a lever run (EAI_LABEL_OUT\project) against (1) the s0 baseline labels as pseudo ground truth and (2) the real ground
/// truth, each only over the images the run actually labelled (those that have a prediction JSON in OUT\project). Writes OUT\quality-vs-baseline.json/csv,
/// OUT\quality-vs-gt-subset.json/csv and OUT\quality-vs-baseline.meta.json. Opt-in: EAI_LABEL_RUN=1.
/// Env: EAI_LABEL_OUT, EAI_LABEL_BASELINE (default artifacts\EAI-LABEL\s0-baseline; its project\ sub-folder is used when it exists, so the 'project' path itself also works),
/// EAI_LABEL_GT (default E:\EAI_TEST, read only), EAI_LABEL_IOU (0.5).
/// </summary>
[Trait("Category", "Agent")]
public sealed class LabelMetricsVsBaselineTests
{
    private readonly ITestOutputHelper _out;

    public LabelMetricsVsBaselineTests(ITestOutputHelper output) => _out = output;

    [LabelRunFact("EAI_LABEL_OUT")]
    public void ScoreOutputAgainstBaselineAndGroundTruth()
    {
        CultureInfo inv = CultureInfo.InvariantCulture;
        string root = LabelRunEnv.RepoRoot() ?? throw new InvalidOperationException("Repository root not found; all output must stay inside the project.");
        string outDir = Path.GetFullPath(LabelRunEnv.Get("EAI_LABEL_OUT")!);
        string baseline = Path.GetFullPath(LabelRunEnv.Get("EAI_LABEL_BASELINE") ?? Path.Combine(root, "artifacts", "EAI-LABEL", "s0-baseline"));
        string baselineProject = Directory.Exists(Path.Combine(baseline, "project")) ? Path.Combine(baseline, "project") : baseline;
        string gtFolder = Path.GetFullPath(LabelRunEnv.Get("EAI_LABEL_GT") ?? LabelRunEnv.ProtectedFolder);
        double threshold = double.Parse(LabelRunEnv.Get("EAI_LABEL_IOU") ?? "0.5", inv);
        string predictions = Path.Combine(outDir, "project");
        foreach (string guarded in new[] { baseline, gtFolder, LabelRunEnv.ProtectedFolder, Path.Combine(root, "artifacts", "EAI-LABEL", "s0-baseline"), Path.Combine(root, "artifacts", "EAI-LABEL", "s1-replay") })
        {
            if (LabelRunEnv.IsEqualOrUnder(outDir, guarded))
            {
                throw new InvalidOperationException("REFUSED: EAI_LABEL_OUT must not be inside " + guarded);
            }
        }

        Assert.True(Directory.Exists(predictions), "No predictions folder (run BootstrapBaselineTests first): " + predictions);
        Assert.True(Directory.Exists(baselineProject), "Baseline folder not found: " + baselineProject);

        List<string> labelled = Directory.GetFiles(predictions, "*.json", SearchOption.TopDirectoryOnly).Select(p => Path.GetFileNameWithoutExtension(p)).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        List<string> unlabelled = Directory.GetFiles(predictions, "*", SearchOption.TopDirectoryOnly)
            .Where(p => !string.Equals(Path.GetExtension(p), ".json", StringComparison.OrdinalIgnoreCase))
            .Select(p => Path.GetFileNameWithoutExtension(p)).Where(n => !labelled.Contains(n, StringComparer.OrdinalIgnoreCase)).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();

        List<string> vsBaseline = labelled.Where(n => File.Exists(Path.Combine(baselineProject, n + ".json"))).ToList();
        List<string> vsGt = Directory.Exists(gtFolder) ? labelled.Where(n => File.Exists(Path.Combine(gtFolder, n + ".json"))).ToList() : new List<string>();
        FolderQuality qualityBaseline = Score(predictions, vsBaseline, baselineProject, threshold);
        File.WriteAllText(Path.Combine(outDir, "quality-vs-baseline.json"), LabelMetrics.ToJson(qualityBaseline), new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(outDir, "quality-vs-baseline.csv"), LabelMetrics.ToCsv(qualityBaseline), new UTF8Encoding(false));
        FolderQuality qualityGt = Score(predictions, vsGt, gtFolder, threshold);
        File.WriteAllText(Path.Combine(outDir, "quality-vs-gt-subset.json"), LabelMetrics.ToJson(qualityGt), new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(outDir, "quality-vs-gt-subset.csv"), LabelMetrics.ToCsv(qualityGt), new UTF8Encoding(false));

        JObject meta = new JObject
        {
            ["baseline_folder"] = baselineProject,
            ["ground_truth_folder"] = gtFolder,
            ["iou_threshold"] = threshold,
            ["images_labelled_by_run"] = labelled.Count,
            ["images_scored_vs_baseline"] = vsBaseline.Count,
            ["images_scored_vs_gt"] = vsGt.Count,
            ["labelled_without_baseline_json"] = new JArray(labelled.Where(n => !vsBaseline.Contains(n)).ToList()),
            ["images_without_prediction_json"] = new JArray(unlabelled),
            ["note"] = "Only images with a prediction JSON in OUT\\project are scored; an image the run did not label is listed, not counted as a miss. The baseline is the agent's own S0 output (pseudo ground truth), not the real truth.",
        };
        File.WriteAllText(Path.Combine(outDir, "quality-vs-baseline.meta.json"), meta.ToString(Newtonsoft.Json.Formatting.Indented), new UTF8Encoding(false));
        _out.WriteLine(string.Format(inv, "vsBaseline images={0} precision={1:F3} recall={2:F3} meanIoU={3:F3} | vsGt images={4} precision={5:F3} recall={6:F3} meanIoU={7:F3}",
            qualityBaseline.Images.Count, qualityBaseline.Precision, qualityBaseline.Recall, qualityBaseline.MeanIou, qualityGt.Images.Count, qualityGt.Precision, qualityGt.Recall, qualityGt.MeanIou));
        Assert.NotEmpty(labelled);
    }

    private static FolderQuality Score(string predictionFolder, IReadOnlyList<string> names, string truthFolder, double threshold)
    {
        List<ImageQuality> images = new List<ImageQuality>();
        double iouTotal = 0;
        int matchedTotal = 0;
        foreach (string name in names)
        {
            ImageQuality quality = LabelMetrics.Evaluate(
                name,
                LabelMetrics.LoadShapes(Path.Combine(predictionFolder, name + ".json")),
                LabelMetrics.LoadShapes(Path.Combine(truthFolder, name + ".json")),
                threshold);
            iouTotal += quality.MeanIou * quality.TruePositives;
            matchedTotal += quality.TruePositives;
            images.Add(quality);
        }

        return new FolderQuality(threshold, images, matchedTotal == 0 ? 0.0 : iouTotal / matchedTotal);
    }
}
