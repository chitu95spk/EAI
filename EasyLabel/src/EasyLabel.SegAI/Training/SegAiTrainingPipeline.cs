// Cloned from BeeCore/Unit/SegmentAI.TrainingEvaluation.cs (:20-136 frame preparation, :146-495 TrainConfiguredCore, :554-726 split/delta/
// manifest helpers), BeeCore/Unit/SegmentAI.cs (:541-563 TrainGated, :789-811 LoadSampleImage/LoadOrBuildMask, :891-904 EnsureBgr),
// BeeCore/Unit/SegmentAI.Inspection.cs (:259-264 WithAlignedTrainingSample, ownerless branch only) and
// BeeCore/Unit/SegmentAI.ModelWorkspace.cs (:44-50 Outline)@d1201fa3d
// Replaced: pathModel/ActivateTrainedCandidate -> SegAiRegistry.StageCandidate; rotArea outline -> full-image rectangle;
// V2BackbonePath -> native folder or request override; PreserveTrainingFailure -> .easyai/runs/<utc> status files.
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using EasyLabel.SegAI.Convert;
using EasyLabel.SegAI.Data;
using EasyLabel.SegAI.Engine;
using EasyLabel.SegAI.Model;
using EasyLabel.SegAI.Options;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using OpenCvSharp;

namespace EasyLabel.SegAI.Training;

internal sealed class SegAiTrainingFrame
{
    public int Index, Width, Height;
    // Bit c-1 is set when class id c has pixels (up to 32 classes).
    public uint ClassBits;
    public string? Image, Mask, Name, Hash;
    // Crops of one source image share GroupKey (validation keeps them together); for ordinary frames it equals Hash.
    public string? GroupKey;
    // Content hashes of the training input/label Mats, used to detect "unchanged since last train".
    public string? InputHash, LabelHash;
    // The source sample resolved to Val.
    public bool IsVal;
    // Position projects: the per-instance labels of the source sample (null or empty = background only).
    public IReadOnlyList<SegInstance>? Instances;
}

internal sealed class SegAiRetrainManifest
{
    public int Schema, Generation;
    public string? Context;
    public List<string>? Frames;
}

internal sealed class SegAiRetrainDeltaRejectedException : Exception
{
    public SegAiRetrainDeltaRejectedException(string message) : base(message) { }
}

internal sealed class SegAiRetrainNothingChangedException : InvalidOperationException
{
    public SegAiRetrainNothingChangedException(string message) : base(message) { }
}

/// <summary>A failure inside a named stage of the training run; the stage and input size go into the run folder.</summary>
internal sealed class SegAiTrainStageException : Exception
{
    public SegAiTrainStageException(string stage, string inputDimensions, Exception inner)
        : base("Training failed while trying to " + stage + ": " + inner.Message, inner)
    {
        Stage = stage;
        InputDimensions = inputDimensions;
    }

    public string Stage { get; }

    public string InputDimensions { get; }
}

/// <summary>All inputs of one training run. The session builds it from its snapshots; tests build it directly with a fake backend.</summary>
internal sealed class SegAiTrainJob
{
    public string RunsDir { get; init; } = string.Empty;

    public SegAiRegistry Registry { get; init; } = null!;

    public ISegAiTrainBackend Backend { get; init; } = null!;

    public IReadOnlyList<SegSample> Samples { get; init; } = Array.Empty<SegSample>();

    public SegTrainingOptions Options { get; init; } = new();

    public SegInferenceSettings Inference { get; init; } = new();

    public bool Retrain { get; init; }

    /// <summary>Full path of the model to resume from; null with Retrain = a confirmed fresh run.</summary>
    public string? ResumeSource { get; init; }

    public string? BackbonePath { get; init; }

    public Action<int>? Progress { get; init; }

    /// <summary>Named classes of a V2 run (ids 1..N); null keeps the two-class NG/OK behaviour and the version 3 model format.</summary>
    public SegAiClassTable? ClassTable { get; init; }

    public Action<SegEpochMetric>? EpochSink { get; init; }

    /// <summary>Position projects: the V2 model gets the per-instance head and every sample's instances are sent to the native trainer.</summary>
    public bool InstanceHead { get; init; }

    /// <summary>EAI-POS P9: output kind of a Detect box-mode run (<see cref="EasyLabel.SegAI.Convert.SegBoxModeRule"/>); null for Position and polygon runs.</summary>
    public string? BoxOutputShape { get; init; }

    /// <summary>EAI-POS P9: report note of the box-mode decision ("Box mode on ..." or the reason it is off); null for Position and polygon-only sets.</summary>
    public string? BoxModeNote { get; init; }

    /// <summary>EAI-POS follow-up: angle symmetry of the instance head (rule or project override); written only when the run has an instance head.</summary>
    public int AngleSymmetry { get; init; } = SegBoxModeRule.FullTurnSymmetry;

    /// <summary>EAI-POS follow-up: report note of the angle symmetry; null when the run has no instance head.</summary>
    public string? AngleSymmetryNote { get; init; }

    public CancellationToken Token { get; init; }
}

internal static partial class SegAiTrainingPipeline
{
    internal const string ReportKey = SegAiModelFile.ReportKey;
    // Retrain: the resumed head continues training with a smaller learning rate than a fresh run.
    internal const float RetrainLearningRateScale = 0.3f;
    internal const string RetrainManifestKey = "bee_retrain_manifest: ";
    internal const double RetrainRehearsalFraction = 0.20;
    internal const float RetrainValidationTolerance = 0.05f;
    // A warm-started head converges in far fewer epochs than a random init: a resumed run defaults to 20.
    internal const int RetrainEarlyStopPatience = 20;
    internal const int RetrainManifestSchema = 1;
    internal const int RetrainManifestMaxBytes = 8 * 1024 * 1024;
    internal const string RetrainRejectedMarker = "[retrain-delta-rejected]";
    private const int ValidationNamesMax = 5000;

    // 0 = leave the native legacy formula in place (key omitted); explicit option wins; resumed head defaults to 20.
    internal static int EffectiveEarlyStopPatience(int optionPatience, bool resumed)
    {
        return optionPatience > 0 ? optionPatience : (resumed ? RetrainEarlyStopPatience : 0);
    }

    // ---------------------------------------------------------------- run

    /// <summary>
    /// Runs one training. Never throws for an expected failure or a cancel. Nothing is registered unless the whole run succeeded:
    /// the candidate goes through <see cref="SegAiRegistry.StageCandidate(string, string, string, Func{string, string?})"/>.
    /// </summary>
    internal static SegAiTrainResult Run(SegAiTrainJob job)
    {
        ArgumentNullException.ThrowIfNull(job);
        var total = Stopwatch.StartNew();
        SegTrainingOptions options = job.Options.Clone();
        try { options.Validate(); }
        catch (ArgumentException ex) { return SegAiTrainResult.Failed(ex.Message, total.Elapsed); }
        if (job.Retrain && options.Version != 2) return SegAiTrainResult.Failed("Retrain is available for V2 only.", total.Elapsed);
        if (job.Token.IsCancellationRequested) return SegAiTrainResult.CancelledResult(total.Elapsed);

        // The backbone is checked before any run folder, trainer or native call exists.
        string? backbone = null;
        if (options.Version == 2)
        {
            backbone = !string.IsNullOrWhiteSpace(job.BackbonePath) ? job.BackbonePath : SafeDefaultBackbone(job.Backend);
            if (string.IsNullOrWhiteSpace(backbone))
                return SegAiTrainResult.Failed("The V2 backbone cannot be located because BeeNativeSegAI.dll is not loaded. Set BackbonePathOverride or install the native library.", total.Elapsed);
            if (!File.Exists(backbone))
                return SegAiTrainResult.Failed("V2 backbone is missing: " + backbone + " (expected Models\\DefectV2\\mobilenetv2-12.onnx next to BeeNativeSegAI.dll).", total.Elapsed);
            backbone = Path.GetFullPath(backbone);
        }

        string? resume = job.Retrain && !string.IsNullOrWhiteSpace(job.ResumeSource) ? Path.GetFullPath(job.ResumeSource) : null;
        if (resume != null && !File.Exists(resume))
            return SegAiTrainResult.Failed("The model to resume from was not found: " + resume, total.Elapsed);

        List<SegSample> labelled = SegTrainingSplit.GetTrainingAndValidationSamples(job.Samples);
        if (!labelled.Any(SegSample.IsTrainSample))
        {
            // Nothing labelled at all keeps the plain "nothing to train" message; labelled images that are all pinned Val cannot train.
            return SegAiTrainResult.Failed(labelled.Count == 0
                ? "There are no labelled images to train."
                : "At least one labelled Train image is required; every labelled image is set to Val.", total.Elapsed);
        }

        // A corrupt registry must fail now, not after a finished training cannot be registered.
        try { job.Registry.Load(); }
        catch (InvalidDataException ex)
        {
            return SegAiTrainResult.Failed("Training failed while trying to load registry: " + ex.Message, total.Elapsed);
        }

        string runDir;
        try { runDir = SegAiRunFolders.Create(job.RunsDir); }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            return SegAiTrainResult.Failed("Cannot create the training run folder: " + ex.Message, total.Elapsed);
        }

        string work = Path.Combine(runDir, "work");
        string fingerprint = ComputeFingerprint(labelled);
        SegAiRunFolders.WriteStatus(runDir, "running", null, null);
        SegAiTrainResult result;
        try
        {
            var context = new RunContext(job, options, backbone, resume, runDir, work, fingerprint, labelled);
            try
            {
                result = RunOnce(context, true, null);
            }
            catch (SegAiRetrainDeltaRejectedException rejected)
            {
                // A rejected changed-only run reruns once on the full dataset instead of silently producing a bad model.
                result = RunOnce(context, false, rejected.Message);
            }

            SegAiRunFolders.WriteStatus(runDir, "success", result.Entry?.File, result.Message);
        }
        catch (OperationCanceledException)
        {
            result = SegAiTrainResult.CancelledResult(total.Elapsed, runDir);
            SegAiRunFolders.WriteStatus(runDir, "cancelled", null, result.Message);
        }
        catch (SegAiRetrainNothingChangedException nothing)
        {
            result = SegAiTrainResult.Failed(nothing.Message, total.Elapsed, runDir);
            SegAiRunFolders.WriteStatus(runDir, "failed", null, nothing.Message);
        }
        catch (SegAiTrainStageException stage)
        {
            result = SegAiTrainResult.Failed(stage.Message, total.Elapsed, runDir);
            SegAiRunFolders.WriteFailure(runDir, stage.Stage, stage.InputDimensions, job.Inference.EnableGpu, stage.InnerException ?? stage);
            SegAiRunFolders.WriteStatus(runDir, "failed", null, stage.Message);
        }
        catch (Exception error)
        {
            result = SegAiTrainResult.Failed("Training failed: " + error.Message, total.Elapsed, runDir);
            SegAiRunFolders.WriteFailure(runDir, "unknown", "unknown", job.Inference.EnableGpu, error);
            SegAiRunFolders.WriteStatus(runDir, "failed", null, result.Message);
        }
        finally
        {
            SegAiRunFolders.TryDeleteInside(runDir, work);
            SegAiRunFolders.PruneFailed(job.RunsDir, SegAiRunFolders.KeepFailed);
        }

        return result;
    }

    private static string? SafeDefaultBackbone(ISegAiTrainBackend backend)
    {
        try { return backend.DefaultBackbonePath(); }
        catch (Exception) { return null; }
    }

    private sealed class RunContext
    {
        public RunContext(SegAiTrainJob job, SegTrainingOptions options, string? backbone, string? resume, string runDir, string work,
            string fingerprint, List<SegSample> labelled)
        {
            Job = job;
            Options = options;
            Backbone = backbone;
            Resume = resume;
            RunDir = runDir;
            Work = work;
            Fingerprint = fingerprint;
            Labelled = labelled;
        }

        public SegAiTrainJob Job { get; }
        public SegTrainingOptions Options { get; }
        public string? Backbone { get; }
        public string? Resume { get; }
        public string RunDir { get; }
        public string Work { get; }
        public string Fingerprint { get; }
        public List<SegSample> Labelled { get; }
    }

    private static SegAiTrainResult RunOnce(RunContext ctx, bool allowDelta, string? fallbackReason)
    {
        SegAiTrainJob job = ctx.Job;
        SegTrainingOptions options = ctx.Options;
        SegInferenceSettings inference = job.Inference;
        CancellationToken token = job.Token;
        Action<int>? progress = job.Progress;
        bool retrain = job.Retrain;
        string? resumeSource = ctx.Resume;
        string work = ctx.Work;
        // NVIDIA CUDA covers inference/evaluation only; the trainer, retrain context and settings keep Intel/CPU (CUDA selected = train on CPU).
        bool enableGpu = inference.EnableGpu && inference.GpuBackend == SegGpuBackend.IntelOpenVino;
        float defectThreshold = inference.DefectThreshold;
        int minDefectArea = inference.MinDefectArea;

        // A rerun after a rejected changed-only run starts from an empty work folder.
        SegAiRunFolders.TryDeleteInside(ctx.RunDir, work);
        Directory.CreateDirectory(work);
        string staged = Path.Combine(work, "candidate.segai");
        var elapsed = Stopwatch.StartNew();
        string stage = "prepare dataset", inputDimensions = "unknown";
        try
        {
            int classCount = job.ClassTable?.Classes.Count ?? 2;
            if (options.Version == 1 && job.ClassTable != null) throw new InvalidOperationException("V1 training supports only the NG/OK classes.");
            if (job.InstanceHead && options.Version != 2) throw new InvalidOperationException("Position projects train the V2 model only.");
            string[] classNames = job.ClassTable?.Classes.Select(c => c.Name).ToArray() ?? new[] { "NG", "OK" };
            List<SegAiTrainingFrame> frames = PrepareTrainingFrames(ctx.Labelled, token, progress, value => inputDimensions = value, work, true, out string preparedDimensions, classCount);
            inputDimensions = preparedDimensions;
            // Validation = exactly the Val-tagged images (expanded to whole identical-pixel groups). Class aggregates count Train frames only.
            HashSet<int> validation = ResolveValidationFrames(frames, out string? validationWarning);
            ValidateValidationSplit(frames, validation, classNames);
            List<SegAiTrainingFrame> trainFrames = frames.Where(f => !validation.Contains(f.Index)).ToList();
            uint presentClasses = trainFrames.Aggregate(0u, (bits, frame) => bits | frame.ClassBits);
            if ((options.Version == 1 && presentClasses != 3) || (options.Version == 2 && presentClasses == 0))
                throw new InvalidOperationException(options.Version == 2 ? "V2 training requires " + string.Join(" and/or ", classNames) + " labels." : "V1 training requires NG and OK labels.");

            // Plan which frames are new/changed, unchanged-but-rehearsed, or unchanged-and-skipped from the resumed model's manifest.
            // AddSample below stays unconditional for every frame, so class enablement and the small-defect threshold cover the whole dataset.
            string? backbone = ctx.Backbone;
            string? context = options.Version == 2 && backbone != null && File.Exists(backbone) ? RetrainContext(options, backbone, enableGpu) : null;
            SegAiRetrainManifest? previous = null;
            string? manifestReason = null;
            if (retrain && resumeSource != null && options.Version == 2)
            {
                previous = ReadRetrainManifest(resumeSource, out manifestReason);
                if (previous != null && previous.Context != context) { previous = null; manifestReason = "training settings changed"; }
            }

            bool delta = false;
            string? deltaReason = fallbackReason ?? manifestReason;
            var changed = new List<SegAiTrainingFrame>();
            var rehearsal = new HashSet<int>();
            var skip = new HashSet<int>();
            if (allowDelta && retrain && resumeSource != null && options.Version == 2 && previous != null)
            {
                var learned = new HashSet<string>(previous.Frames ?? new List<string>(), StringComparer.Ordinal);
                SplitChangedFrames(frames, validation, learned, out changed, out List<SegAiTrainingFrame> unchanged);
                if (changed.Count == 0)
                {
                    throw new SegAiRetrainNothingChangedException(
                        "There are no new or relabelled images since the last training; the current model is kept. Use Train to retrain everything.");
                }
                else if (validation.Count == 0) deltaReason = "validation disabled (no Val images)";
                else
                {
                    HashSet<int> candidateRehearsal = SelectRehearsal(unchanged, presentClasses, previous.Generation);
                    var candidateSkip = new HashSet<int>(unchanged.Where(f => !candidateRehearsal.Contains(f.Index)).Select(f => f.Index));
                    if (candidateSkip.Count == 0) deltaReason = "no images to skip";
                    else { rehearsal = candidateRehearsal; skip = candidateSkip; delta = true; }
                }
            }

            // A previously trained image that is now Val makes validation optimistic: the resumed head has already seen it.
            int seenValidation = 0;
            if (previous != null && previous.Frames != null)
            {
                var seen = new HashSet<string>(previous.Frames, StringComparer.Ordinal);
                seenValidation = frames.Count(f => validation.Contains(f.Index) && seen.Contains(FrameKey(f)));
            }

            int unlabelledValidation = 0; // kept for saved-report compatibility: Val images always carry labels
            int earlyStopPatience = options.Version == 2 ? EffectiveEarlyStopPatience(options.EarlyStopPatience, retrain && resumeSource != null) : 0;

            stage = "configure trainer";
            var report = new SegTrainingReport
            {
                Version = options.Version,
                Options = options,
                CreatedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                TrainingImages = frames.Count - validation.Count - skip.Count,
                ValidationImages = validation.Count,
                TrainingRecheck = validation.Count == 0,
                ScorePercent = defectThreshold * 100,
                Note = validation.Count > 0
                    ? "Val images are chosen by the user and excluded from training; this split does not establish independence between physical parts."
                    : "Training-image recheck only. No image is tagged Val; not a generalization estimate.",
                UnlabelledValidationImages = unlabelledValidation,
                ValidationWarning = validationWarning,
                OutputShape = job.BoxOutputShape,
            };
            if (!string.IsNullOrEmpty(validationWarning)) report.Note += " " + validationWarning;
            if (!string.IsNullOrEmpty(job.BoxModeNote)) report.Note += " " + job.BoxModeNote;
            if (!string.IsNullOrEmpty(job.AngleSymmetryNote)) report.Note += " " + job.AngleSymmetryNote;
            if (InstanceEpochsNote(options, job.InstanceHead) is string epochsNote) report.Note += " " + epochsNote;
            if (options.Version == 2) report.Note += " Unlabelled pixels inside Check are background; annotate every NG target. OK-object labels are optional. Frozen backbone; only the head is trained. V2 remains experimental.";
            if (earlyStopPatience > 0) report.Note += " Early stop patience: " + earlyStopPatience + " epochs.";
            // Only ever written into the report; capped so a huge Val set cannot push the report past the 2 MB read window.
            report.ValidationNames.AddRange(frames.Where(f => validation.Contains(f.Index)).Select(f => f.Name ?? string.Empty).Take(ValidationNamesMax));

            using (ISegAiTrainerSession trainer = job.Backend.CreateTrainer())
            {
                if (options.Version == 1) trainer.UseStructuralModel(inference.MaxDepth);
                else
                {
                    if (backbone == null || !File.Exists(backbone)) throw new FileNotFoundException("V2 backbone is missing: Models/DefectV2/mobilenetv2-12.onnx", backbone);
                    string config = Path.Combine(work, "settings.json");
                    string settingsJson = BuildV2SettingsJson(options, backbone, work, enableGpu, validation, retrain, resumeSource, delta, skip, earlyStopPatience, job.InstanceHead, job.AngleSymmetry);
                    File.WriteAllText(config, settingsJson, new UTF8Encoding(false));
                    trainer.ConfigureV2(config);
                    if (job.ClassTable != null && trainer.SetClassTable(job.ClassTable) != 0)
                        throw new InvalidOperationException("Cannot set the class table. " + trainer.LastError);
                    // The old model must stay live and no training work should run against an old DLL that silently ignored "resumeFrom".
                    if (retrain && resumeSource != null && !File.Exists(Path.Combine(work, "resume.txt")))
                        throw new InvalidOperationException("Update BeeNativeSegAI.dll to use Retrain.");
                    // An old DLL ignores "priorBalance" and never writes prior.txt; keep training (the model is a legacy one) but say so.
                    if (!options.V2LegacyPrior && !File.Exists(Path.Combine(work, "prior.txt")))
                        report.Note += " BeeNativeSegAI.dll ignored prior balancing; model is legacy.";
                    // Native may itself downgrade a changed-only run to a full run at Configure time: mirror that in the report.
                    if (delta)
                    {
                        string deltaStatusPath = Path.Combine(work, "delta.txt");
                        if (!File.Exists(deltaStatusPath))
                            throw new InvalidOperationException("Update BeeNativeSegAI.dll to use Retrain (changed images only).");
                        string? deltaStatusLine = File.ReadLines(deltaStatusPath).FirstOrDefault();
                        if (deltaStatusLine != null && deltaStatusLine.StartsWith("full", StringComparison.Ordinal))
                        {
                            delta = false;
                            int semicolonIndex = deltaStatusLine.IndexOf(';');
                            deltaReason = semicolonIndex >= 0 ? deltaStatusLine.Substring(semicolonIndex + 1).Trim() : deltaStatusLine;
                            skip = new HashSet<int>();
                        }
                    }

                    report.TrainingImages = frames.Count - validation.Count - skip.Count;
                    report.ChangedImages = delta ? changed.Count : 0;
                    report.SkippedImages = skip.Count;
                    report.RehearsalImages = delta ? rehearsal.Count : 0;
                }

                stage = "add samples";
                foreach (SegAiTrainingFrame frame in frames)
                {
                    token.ThrowIfCancellationRequested();
                    if (options.Version == 1 && validation.Contains(frame.Index)) continue;
                    // Every V2 frame (validation, rehearsal, skipped, changed) still reaches AddSample unconditionally.
                    using Mat image = ReadMat(frame.Image!, ImreadModes.Color);
                    using Mat labels = ReadMat(frame.Mask!, ImreadModes.Grayscale);
                    // Structural training uses zero for excluded pixels; evaluation keeps 255 as an ignore marker.
                    if (options.Version == 1)
                    {
                        using var excluded = new Mat();
                        Cv2.Compare(labels, 255, excluded, CmpTypes.EQ);
                        labels.SetTo(Scalar.All(0), excluded);
                    }

                    trainer.SetRoi(0, 0, image.Width, image.Height);
                    if (trainer.AddSample(image, labels) != 0) throw new InvalidOperationException("Cannot learn sample: " + frame.Name + ". " + trainer.LastError);
                    // Instances attach to the sample just added (native contract); a background-only sample sends none.
                    if (job.InstanceHead && frame.Instances != null && frame.Instances.Count > 0)
                    {
                        var instanceTrainer = trainer as ISegAiInstanceTrainerSession
                            ?? throw new InvalidOperationException("This trainer cannot learn instances. Update BeeNativeSegAI.dll.");
                        if (instanceTrainer.AddInstances(frame.Instances) != 0) throw new InvalidOperationException("Cannot learn instances: " + frame.Name + ". " + trainer.LastError);
                    }
                }

                stage = "native training";
                int rc;
                using (var cancelFlag = new SegAiCancelFlag())
                {
                    // Tails the already-flushed epochs.csv while native training runs; Dispose does one last read (also on cancel/exception).
                    EpochLogTail? epochTail = null;
                    try
                    {
                        if (token.IsCancellationRequested) cancelFlag.Cancel();
                        if (options.Version == 2 && job.EpochSink != null)
                        {
                            epochTail = new EpochLogTail(Path.Combine(work, "epochs.csv"), validation.Count > 0, job.EpochSink);
                            epochTail.Start();
                        }

                        using (token.Register(() => { try { cancelFlag.Cancel(); } catch (ObjectDisposedException) { } }))
                        {
                            rc = trainer.Train(inference.NumTrees, inference.MaxDepth, inference.MinSample,
                                p => { int value = 15 + p * 65 / 100; progress?.Invoke(value); }, cancelFlag.Pointer);
                        }
                    }
                    finally
                    {
                        epochTail?.Dispose();
                    }
                }

                token.ThrowIfCancellationRequested();
                if (rc != 0)
                {
                    string nativeError = !string.IsNullOrEmpty(trainer.LastError) ? trainer.LastError
                        : File.Exists(Path.Combine(work, "error.txt")) ? File.ReadAllText(Path.Combine(work, "error.txt"))
                        : "Training failed (" + rc + ").";
                    // Native rejects a changed-only run with a marked message; the wrapper reruns on the full dataset.
                    int markerIndex = delta ? nativeError.IndexOf(RetrainRejectedMarker, StringComparison.Ordinal) : -1;
                    if (markerIndex >= 0) throw new SegAiRetrainDeltaRejectedException(nativeError.Substring(markerIndex + RetrainRejectedMarker.Length).Trim());
                    throw new InvalidOperationException(nativeError);
                }

                if (options.Version == 2)
                {
                    // prior.txt line 1 = effective mode ("balanced"/"legacy"[; reason]); absent for legacy runs without the key.
                    string priorStatusPath = Path.Combine(work, "prior.txt");
                    string? priorLine = File.Exists(priorStatusPath) ? File.ReadLines(priorStatusPath).FirstOrDefault() : null;
                    if (!string.IsNullOrWhiteSpace(priorLine))
                    {
                        priorLine = priorLine.Trim();
                        if (priorLine.Length > 512) priorLine = priorLine.Substring(0, 512);
                        report.Note += " Class prior: " + priorLine + ".";
                    }
                }

                if (retrain)
                {
                    if (resumeSource == null)
                        report.Resume = "fresh; previous model has no Retrain data (user confirmed)";
                    else
                    {
                        string resumeStatusPath = Path.Combine(work, "resume.txt");
                        string? resumeLine = File.Exists(resumeStatusPath) ? File.ReadLines(resumeStatusPath).FirstOrDefault() : null;
                        report.Resume = resumeLine?.Trim();
                        if (report.Resume != null && report.Resume.Length > 512) report.Resume = report.Resume.Substring(0, 512);
                    }

                    if (report.Resume != null && report.Resume.StartsWith("resumed", StringComparison.Ordinal))
                    {
                        report.Note += " Retrain: head continued from the previous model; images seen by that model may be in this validation split, so validation metrics can be optimistic." +
                            (seenValidation > 0 ? " " + seenValidation + " Val image(s) were already learned by the previous model." : "");
                    }
                    else if (resumeSource != null && report.Resume != null && report.Resume.StartsWith("fresh", StringComparison.Ordinal))
                    {
                        int semicolon = report.Resume.IndexOf(';');
                        report.Note += " Retrain fell back to fresh training: " + (semicolon >= 0 ? report.Resume.Substring(semicolon + 1).Trim() : report.Resume) + ".";
                    }
                }

                stage = "save candidate";
                if (trainer.Save(staged, defectThreshold, (uint)Math.Max(1, minDefectArea)) != 0) throw new IOException("Cannot save trained model. " + trainer.LastError);
            }

            if (options.Version == 2 && options.InputSizeOff)
            {
                using var file = File.OpenRead(staged);
                using var reader = new BinaryReader(file);
                file.Position = SegAiModelFile.HeaderSize;
                string prefix = Encoding.UTF8.GetString(reader.ReadBytes(1024));
                if (!prefix.Split('\n').Any(line => line.Trim() == "native_v2: 2"))
                    throw new InvalidOperationException("Update BeeNativeSegAI.dll to train with Input size OFF.");
            }

            if (options.Version == 2)
            {
                stage = "read epoch metrics";
                ReadEpochMetrics(Path.Combine(work, "epochs.csv"), report);
            }

            List<SegAiTrainingFrame> evaluation = frames.Where(f => validation.Count == 0 || validation.Contains(f.Index)).ToList();
            stage = "evaluate candidate";
            EvaluateTrainingModel(job.Backend, inference, staged, evaluation, report, token, progress, job.ClassTable);
            report.TotalTrainingSeconds = elapsed.Elapsed.TotalSeconds;
            stage = "save metadata";
            // Snapshot the "learned set" manifest between the report and outline metadata writes (V2 only; V1 never writes one).
            if (options.Version == 2)
            {
                bool resumed = report.Resume != null && report.Resume.StartsWith("resumed", StringComparison.Ordinal);
                var trained = new HashSet<string>(StringComparer.Ordinal);
                foreach (SegAiTrainingFrame frame in frames)
                    if (!validation.Contains(frame.Index) && !skip.Contains(frame.Index))
                        trained.Add(FrameKey(frame));
                if (resumed && previous != null && previous.Frames != null)
                {
                    // Carry forward still-known-unchanged entries so skipped/unchanged images do not fall out of the manifest.
                    var current = new HashSet<string>(frames.Select(FrameKey), StringComparer.Ordinal);
                    foreach (string key in previous.Frames) if (current.Contains(key)) trained.Add(key);
                }

                var manifest = new SegAiRetrainManifest
                {
                    Schema = RetrainManifestSchema,
                    Generation = (resumed && previous != null ? previous.Generation : 0) + 1,
                    Context = context,
                    Frames = trained.OrderBy(k => k, StringComparer.Ordinal).ToList(),
                };
                string manifestJson = JsonConvert.SerializeObject(manifest);
                if (Encoding.UTF8.GetByteCount(manifestJson) > RetrainManifestMaxBytes)
                    report.Note += " Retrain image list too large; next Retrain uses all images.";
                else
                    SegAiModelFile.AppendModelMetadata(staged, RetrainManifestKey, manifestJson);
                if (delta)
                {
                    string deltaFinalPath = Path.Combine(work, "delta.txt");
                    string? deltaFinalLine = File.Exists(deltaFinalPath) ? File.ReadLines(deltaFinalPath).FirstOrDefault() : null;
                    report.Note += " Retrain (changed images only): " + changed.Count + " new/changed images trained with " + rehearsal.Count +
                        " unchanged rehearsal images; " + skip.Count + " unchanged images skipped. Validation uses the Val-tagged images; candidates raising " +
                        "validation loss by more than 5% were rejected. Changed images are not held out, so their fit is not independently measured. " +
                        "Deleting an image does not remove what the previous model learned from it.";
                    report.RetrainScope = "changed-only; changed=" + changed.Count + "; rehearsal=" + rehearsal.Count + "; skipped=" + skip.Count +
                        (deltaFinalLine != null ? "; " + deltaFinalLine : "");
                }
                else if (retrain)
                {
                    report.RetrainScope = fallbackReason != null
                        ? "full-after-reject; " + fallbackReason
                        : "full; " + (deltaReason ?? "all images");
                }
            }

            SegAiModelFile.AppendModelMetadata(staged, ReportKey, JsonConvert.SerializeObject(report));
            SegAiModelFile.EmbedTrainingOutline(staged, FullImageOutline(frames[0].Width, frames[0].Height));
            token.ThrowIfCancellationRequested();

            // The candidate never replaces a model: it is validated in .pending and moved to a new vNNN.segai.
            stage = "register candidate";
            SegAiRegistryEntry entry = job.Registry.StageCandidate(staged, BuildReportSummary(report), ctx.Fingerprint, job.Backend.CheckLoad);
            // A progress observer cannot turn a committed model into a failed training run.
            try { progress?.Invoke(100); } catch (Exception) { }
            return new SegAiTrainResult
            {
                Success = true,
                CandidatePath = job.Registry.PathOf(entry.File),
                Entry = entry,
                Report = report,
                Message = "Trained candidate " + entry.File + " (not active). " + report.TrainingImages + " training and " + report.ValidationImages + " validation image(s).",
                Elapsed = elapsed.Elapsed,
                ValidationSummary = BuildValidationSummary(trainFrames.Count, validation.Count, report.ValidationNames, validationWarning),
                RunFolder = ctx.RunDir,
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (SegAiRetrainDeltaRejectedException) { throw; }
        catch (SegAiRetrainNothingChangedException) { throw; }
        catch (Exception error)
        {
            throw new SegAiTrainStageException(stage, inputDimensions, error);
        }
        finally
        {
            SegAiRunFolders.TryDeleteInside(ctx.RunDir, work);
        }
    }

    // ---------------------------------------------------------------- small helpers

    /// <summary>
    /// Outline(RectRotate) of an unrotated rectangle (0,0,w,h) in EasyVision: ConvertToPolygonAutoBounds writes the local corners
    /// (-hx,-hy), (+hx,-hy), (+hx,+hy), (-hx,+hy) and GetPolygonVerticesWorld adds the centre (w/2,h/2): top-left, top-right, bottom-right, bottom-left.
    /// </summary>
    internal static PointF[] FullImageOutline(int width, int height)
    {
        if (width <= 0 || height <= 0) return new PointF[0];
        return new[] { new PointF(0, 0), new PointF(width, 0), new PointF(width, height), new PointF(0, height) };
    }

    internal static string BuildReportSummary(SegTrainingReport report)
    {
        var text = new StringBuilder("V").Append(report.Version == 2 ? "2" : "1");
        if (report.Epochs.Count > 0) text.Append(", ").Append(report.Epochs.Count.ToString(CultureInfo.InvariantCulture)).Append(" epochs");
        if (report.Map50.HasValue) text.Append(", mAP50 ").Append(report.Map50.Value.ToString("0.###", CultureInfo.InvariantCulture));
        else
        {
            double? bestLoss = report.Epochs.Select(e => e.ValidationLoss ?? e.TrainLoss).Cast<double?>().Min();
            if (bestLoss.HasValue) text.Append(", best loss ").Append(bestLoss.Value.ToString("0.####", CultureInfo.InvariantCulture));
        }

        return text.ToString();
    }

    private static string BuildValidationSummary(int trainCount, int validationCount, List<string> validationNames, string? warning)
    {
        var text = new StringBuilder("Train ").Append(trainCount).Append(", Val ").Append(validationCount);
        if (validationNames.Count > 0)
            text.Append(" (").Append(string.Join(", ", validationNames.Take(5))).Append(validationNames.Count > 5 ? ", ..." : string.Empty).Append(')');
        if (!string.IsNullOrEmpty(warning)) text.Append(". ").Append(warning);
        return text.ToString();
    }

    /// <summary>SHA256 (lower hex) of the sorted "key|mode" lines of the samples that train or validate.</summary>
    internal static string ComputeFingerprint(IEnumerable<SegSample> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        List<string> lines = samples
            .Select(s => (s.OriginalName ?? Path.GetFileName(s.ImagePath)) + "|" + SegTrainingSplit.ResolveTrainingMode(s))
            .OrderBy(l => l, StringComparer.Ordinal).ToList();
        return System.Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", lines)))).ToLowerInvariant();
    }

    internal static string FrameKey(SegAiTrainingFrame frame)
    {
        return frame.InputHash + "|" + frame.LabelHash;
    }

    // ---------------------------------------------------------------- settings JSON

    /// <summary>
    /// The V2 settings file handed to ConfigureV2. The fresh-path bytes are the ones EasyVision produced before Retrain existed;
    /// resume, delta, early-stop and prior-balance keys are additive and added after serialization, in the same order.
    /// </summary>
    /// <summary>EAI-POS follow-up: report note of the instance head epochs ("Instance epochs N."); null when the run has no instance head.</summary>
    internal static string? InstanceEpochsNote(SegTrainingOptions options, bool instanceHead)
    {
        if (!instanceHead) return null;
        int epochs = options.InstanceEpochs > 0 ? options.InstanceEpochs : SegTrainingOptions.NativeInstanceEpochs;
        return "Instance epochs " + epochs.ToString(CultureInfo.InvariantCulture) + (options.InstanceEpochs > 0 ? "." : " (native default).");
    }

    internal static string BuildV2SettingsJson(SegTrainingOptions options, string backbone, string work, bool gpu, IEnumerable<int> validation,
        bool retrain, string? resumeSource, bool delta, IEnumerable<int> skip, int earlyStopPatience, bool instanceHead = false, int angleSymmetry = SegBoxModeRule.FullTurnSymmetry)
    {
        string settingsJson = JsonConvert.SerializeObject(new
        {
            backbone = backbone.Replace('\\', '/'),
            logs = work.Replace('\\', '/'),
            epochs = options.Epochs,
            side = options.InputSize,
            originalInput = options.InputSizeOff ? 1 : 0,
            learningRate = options.LearningRate,
            regularization = options.Regularization,
            rotation = options.Rotation,
            scaleVariation = options.ScaleVariation,
            brightness = options.Brightness,
            objectScaleMin = options.V2Scale.OffScale ? 1f : options.V2Scale.Minimum,
            objectScaleMax = options.V2Scale.OffScale ? 1f : options.V2Scale.Maximum,
            augment = options.Augment ? 1 : 0,
            gpu = gpu ? 1 : 0,
            validation = validation.OrderBy(i => i).ToArray(),
        });
        if ((retrain && resumeSource != null) || earlyStopPatience > 0 || !options.V2LegacyPrior || instanceHead || options.BackboneEpochs > 0)
        {
            JObject settingsObject = JObject.Parse(settingsJson);
            // Backbone fine-tune (native, libtorch on the CPU): written only when asked. With the instance head the native trainer gathers
            // the instance rows again from the fine-tuned features.
            if (options.BackboneEpochs > 0 && !string.IsNullOrWhiteSpace(options.BackboneTorchPath))
            {
                settingsObject["backboneEpochs"] = options.BackboneEpochs;
                settingsObject["backboneTorch"] = Path.GetFullPath(options.BackboneTorchPath).Replace('\\', '/');
                settingsObject["backboneThreads"] = Math.Max(2, Math.Min(8, Environment.ProcessorCount / 2));
            }

            // Instance head: one angle symmetry for every class (1 = full 360 degrees, the direction of the label; 4 = near-square boxes,
            // chosen by SegBoxModeRule.AngleSymmetry or the project override). Native rejects it with
            // originalInput, Retrain or backbone epochs (reason in the trainer's last error).
            if (instanceHead)
            {
                settingsObject["instanceHead"] = 1;
                settingsObject["angleSymmetry"] = angleSymmetry;
                // EAI-POS follow-up: 0 leaves the key out, so the native default (60) applies.
                if (options.InstanceEpochs > 0) settingsObject["instanceEpochs"] = options.InstanceEpochs;
            }

            if (retrain && resumeSource != null)
            {
                settingsObject["resumeFrom"] = Path.GetFullPath(resumeSource).Replace('\\', '/');
                settingsObject["resumeLearningRateScale"] = RetrainLearningRateScale;
                if (delta)
                {
                    settingsObject["trainSkip"] = new JArray(skip.OrderBy(i => i));
                    settingsObject["deltaValidationTolerance"] = RetrainValidationTolerance;
                }
            }

            if (earlyStopPatience > 0) settingsObject["earlyStopWindow"] = earlyStopPatience;
            if (!options.V2LegacyPrior) settingsObject["priorBalance"] = 1;
            settingsJson = settingsObject.ToString(Formatting.None);
        }

        return settingsJson;
    }

    // ---------------------------------------------------------------- frame preparation

    /// <summary>
    /// The sole preparation path for training. Ownerless branch of EasyVision's WithAlignedTrainingSample: the frame is the raw full
    /// image and its normalized label raster (no Check, no Cropper, no preprocess pipeline).
    /// </summary>
    internal static List<SegAiTrainingFrame> PrepareTrainingFrames(IReadOnlyList<SegSample> labelled, CancellationToken token,
        Action<int>? progress, Action<string>? dimensionsChanged, string? work, bool writeSnapshots, out string inputDimensions, int classCount = 2)
    {
        ArgumentNullException.ThrowIfNull(labelled);
        if (writeSnapshots && string.IsNullOrEmpty(work)) throw new ArgumentNullException(nameof(work));
        string preparedDimensions = "unknown";
        inputDimensions = preparedDimensions;
        var frames = new List<SegAiTrainingFrame>();
        int preparedSamples = 0;
        foreach (SegSample sample in labelled)
        {
            token.ThrowIfCancellationRequested();
            bool isVal = SegSample.IsValidationSample(sample);
            using Mat source = LoadSampleImage(sample);
            using Mat input = EnsureBgr(source);
            using Mat aligned = LoadOrBuildMask(sample, input.Size(), classCount);
            preparedDimensions = input.Width + "x" + input.Height;
            dimensionsChanged?.Invoke(preparedDimensions);
            int index = frames.Count;
            var frame = new SegAiTrainingFrame
            {
                Index = index,
                Width = input.Width,
                Height = input.Height,
                Image = writeSnapshots ? Path.Combine(work!, index + ".png") : null,
                Mask = writeSnapshots ? Path.Combine(work!, index + "-labels.png") : null,
                Name = sample.OriginalName ?? Path.GetFileName(sample.ImagePath),
                IsVal = isVal,
                Instances = sample.Instances,
            };
            frame.ClassBits = ClassBitsOf(aligned, classCount);

            // Val images are labelled too, so no label-less frame (Train or Val) may reach the trainer or validation.
            if (frame.ClassBits == 0) throw new InvalidDataException("No class labels in image: " + frame.Name);
            if (writeSnapshots)
            {
                byte[] imagePng = EncodePng(input);
                File.WriteAllBytes(frame.Image!, imagePng);
                File.WriteAllBytes(frame.Mask!, EncodePng(aligned));
                frame.Hash = System.Convert.ToBase64String(SHA256.HashData(imagePng));
            }
            else frame.Hash = PngHash(input);
            frame.GroupKey = frame.Hash;
            frame.InputHash = RawMatHash(input);
            frame.LabelHash = RawMatHash(aligned);
            frames.Add(frame);
            progress?.Invoke(++preparedSamples * 15 / labelled.Count);
        }

        inputDimensions = preparedDimensions;
        return frames;
    }

    private static byte[] EncodePng(Mat image)
    {
        if (!Cv2.ImEncode(".png", image, out byte[] png) || png == null || png.Length == 0) throw new IOException("Cannot encode training image.");
        return png;
    }

    private static string PngHash(Mat image)
    {
        return System.Convert.ToBase64String(SHA256.HashData(EncodePng(image)));
    }

    internal static Mat ReadMat(string path, ImreadModes mode)
    {
        // Decoding from bytes keeps non-ASCII project paths working (OpenCV's own file API is not Unicode-safe on Windows).
        Mat mat = Cv2.ImDecode(File.ReadAllBytes(path), mode);
        if (mat.Empty())
        {
            mat.Dispose();
            throw new InvalidDataException("Image cannot be decoded: " + path);
        }

        return mat;
    }

    private static Mat LoadSampleImage(SegSample sample)
    {
        if (sample == null || string.IsNullOrEmpty(sample.ImagePath) || !File.Exists(sample.ImagePath))
            throw new FileNotFoundException("Training sample image not found.", sample?.ImagePath ?? string.Empty);
        return ReadMat(sample.ImagePath, ImreadModes.Color);
    }

    /// <summary>Bit c-1 set when class id c (1..classCount) has at least one pixel in the label raster.</summary>
    internal static uint ClassBitsOf(Mat labels, int classCount)
    {
        uint bits = 0;
        for (int c = 1; c <= classCount && c <= 32; c++)
        {
            using var hit = new Mat();
            Cv2.Compare(labels, c, hit, CmpTypes.EQ);
            if (Cv2.CountNonZero(hit) > 0) bits |= 1u << (c - 1);
        }

        return bits;
    }

    private static Mat LoadOrBuildMask(SegSample sample, OpenCvSharp.Size size, int classCount = 2)
    {
        if (!string.IsNullOrEmpty(sample.MaskPath) && File.Exists(sample.MaskPath))
        {
            Mat mask = Cv2.ImDecode(File.ReadAllBytes(sample.MaskPath), ImreadModes.Grayscale);
            if (!mask.Empty() && mask.Size() == size)
            {
                try { return SegLabelRaster.Normalize(mask, classCount); }
                finally { mask.Dispose(); }
            }

            mask.Dispose();
        }

        // SegLabelRaster.Rasterize is the cloned DrawPolygons path: same FillPoly/Round/overwrite order. The stored raster of the
        // EasyLabel project is not read here because the annotation list is the current truth.
        return SegLabelRaster.Rasterize(sample.Annotations ?? new List<SegAnnotation>(), size.Width, size.Height);
    }

    private static Mat EnsureBgr(Mat input)
    {
        if (input.Type() == MatType.CV_8UC3) return input.Clone();
        var bgr = new Mat();
        if (input.Channels() == 1) Cv2.CvtColor(input, bgr, ColorConversionCodes.GRAY2BGR);
        else if (input.Channels() == 4) Cv2.CvtColor(input, bgr, ColorConversionCodes.BGRA2BGR);
        else input.ConvertTo(bgr, MatType.CV_8UC3);
        return bgr;
    }

    // ---------------------------------------------------------------- split / delta / manifest

    // Validation = frames whose sample resolved to Val, expanded to whole GroupKey (identical-pixel) groups: a group with any Val frame
    // goes entirely to Val. When such a group also held a Train frame the split is recorded as a warning (never thrown).
    internal static HashSet<int> ResolveValidationFrames(List<SegAiTrainingFrame> frames, out string? warning)
    {
        warning = null;
        var selected = new HashSet<int>();
        if (frames == null || frames.Count == 0) return selected;
        var valGroups = new HashSet<string?>(frames.Where(f => f.IsVal).Select(f => f.GroupKey ?? f.Hash), StringComparer.Ordinal);
        var conflicts = new List<string>();
        foreach (var group in frames.GroupBy(f => f.GroupKey ?? f.Hash ?? string.Empty).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            if (!valGroups.Contains(group.Key)) continue;
            foreach (SegAiTrainingFrame frame in group) selected.Add(frame.Index);
            if (group.Any(f => !f.IsVal))
                conflicts.Add(string.Join(" = ", group.Select(f => f.Name ?? string.Empty).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()));
        }

        if (conflicts.Count > 0)
        {
            warning = "Identical images were split between Train and Val, so the whole group was moved to Val (" + conflicts.Count + " group(s)): " +
                string.Join("; ", conflicts.Take(5).ToArray()) + (conflicts.Count > 5 ? "; ..." : "") + ".";
        }

        return selected;
    }

    // Changed/unchanged versus the learned-set manifest, over Train frames only (Val frames are neither).
    private static void SplitChangedFrames(List<SegAiTrainingFrame> frames, HashSet<int> validation, HashSet<string> learned,
        out List<SegAiTrainingFrame> changed, out List<SegAiTrainingFrame> unchanged)
    {
        changed = new List<SegAiTrainingFrame>();
        unchanged = new List<SegAiTrainingFrame>();
        foreach (SegAiTrainingFrame frame in frames)
        {
            if (validation.Contains(frame.Index)) continue;
            if (learned.Contains(FrameKey(frame))) unchanged.Add(frame); else changed.Add(frame);
        }
    }

    // Managed guard that replaces the native "No usable NG/OK pixels in the training split" failure. Train frames must exist, and every
    // class present in the Val frames must also be present in the Train frames.
    internal static void ValidateValidationSplit(List<SegAiTrainingFrame> frames, HashSet<int> validation, IReadOnlyList<string>? classNames = null)
    {
        uint trainBits = 0, valBits = 0;
        int trainCount = 0;
        foreach (SegAiTrainingFrame frame in frames)
        {
            if (validation.Contains(frame.Index)) valBits |= frame.ClassBits;
            else { trainBits |= frame.ClassBits; trainCount++; }
        }

        IReadOnlyList<string> names = classNames ?? new[] { "NG", "OK" };
        if (trainCount == 0 || trainBits == 0)
            throw new InvalidOperationException("At least one labelled Train image with " + string.Join("/", names) + " labels is required (no Train image is left after the Val split).");
        uint missing = valBits & ~trainBits;
        if (missing != 0)
        {
            var missingNames = new List<string>();
            for (int i = 0; i < names.Count && i < 32; i++)
                if ((missing & (1u << i)) != 0) missingNames.Add(names[i]);
            throw new InvalidOperationException("Val images contain " + string.Join(" and ", missingNames) +
                " that no Train image has; move an image with that class to Train or remove it from Val.");
        }
    }

    // Deterministically rotates which unchanged images are trained each generation (mandatory rehearsal), so repeated Retrain cycles do
    // not permanently starve any group of unchanged images. Guarantees at least one group per present class label.
    internal static HashSet<int> SelectRehearsal(List<SegAiTrainingFrame> pool, uint classBits, int generation)
    {
        var selected = new HashSet<int>();
        List<IGrouping<string, SegAiTrainingFrame>> groups = pool.GroupBy(f => f.GroupKey ?? f.Hash ?? string.Empty)
            .OrderBy(g => System.Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(generation + ":" + g.Key))), StringComparer.Ordinal)
            .ToList();
        bool Take(IGrouping<string, SegAiTrainingFrame> group)
        {
            if (group.Any(f => selected.Contains(f.Index))) return false;
            foreach (SegAiTrainingFrame f in group) selected.Add(f.Index);
            return true;
        }

        for (int index = 0; index < 32; index++)
        {
            uint bit = 1u << index;
            if ((classBits & bit) == 0) continue;
            foreach (var group in groups)
            {
                if (group.Any(f => (f.ClassBits & bit) != 0)) { Take(group); break; }
            }
        }

        int target = Math.Max(1, (int)Math.Ceiling(pool.Count * RetrainRehearsalFraction));
        foreach (var group in groups) { if (selected.Count >= target) break; Take(group); }
        return selected;
    }

    // Content hash of the actual pixels/labels fed to training: a raw header (rows/cols/type) plus the buffer, independent of file encoding.
    internal static string RawMatHash(Mat mat)
    {
        using Mat? cloned = mat.IsContinuous() ? null : mat.Clone();
        Mat src = cloned ?? mat;
        int rows = src.Rows, cols = src.Cols;
        long length = (long)rows * cols * src.ElemSize();
        if (length > int.MaxValue) throw new InvalidDataException("Training image too large");
        byte[] header = new byte[12];
        Buffer.BlockCopy(BitConverter.GetBytes(rows), 0, header, 0, 4);
        Buffer.BlockCopy(BitConverter.GetBytes(cols), 0, header, 4, 4);
        Buffer.BlockCopy(BitConverter.GetBytes((int)src.Type()), 0, header, 8, 4);
        byte[] body = new byte[length];
        Marshal.Copy(src.Data, body, 0, body.Length);
        using var sha = SHA256.Create();
        sha.TransformBlock(header, 0, header.Length, null, 0);
        sha.TransformBlock(body, 0, body.Length, null, 0);
        sha.TransformFinalBlock(new byte[0], 0, 0);
        return System.Convert.ToBase64String(sha.Hash!);
    }

    // Fingerprint of the training settings that change which feature rows get extracted from an image (deliberately excludes
    // epochs/learning rate/regularization and inference-only options). A model resumed under a different context discards its manifest.
    internal static string RetrainContext(SegTrainingOptions o, string backbone, bool gpu)
    {
        return "schema=1;version=2;side=" + o.InputSize + ";originalInput=" + (o.InputSizeOff ? 1 : 0) +
            ";augment=" + (o.Augment ? 1 : 0) + ";rotation=" + o.Rotation.ToString("R", CultureInfo.InvariantCulture) +
            ";scaleVariation=" + o.ScaleVariation.ToString("R", CultureInfo.InvariantCulture) +
            ";brightness=" + o.Brightness.ToString("R", CultureInfo.InvariantCulture) +
            ";objectScaleMin=" + (o.V2Scale.OffScale ? 1f : o.V2Scale.Minimum).ToString("R", CultureInfo.InvariantCulture) +
            ";objectScaleMax=" + (o.V2Scale.OffScale ? 1f : o.V2Scale.Maximum).ToString("R", CultureInfo.InvariantCulture) +
            ";gpu=" + (gpu ? 1 : 0) + ";backbone=" + Path.GetFileName(backbone) + ":" + new FileInfo(backbone).Length;
    }

    // Reads the "learned set" manifest from the tail of a previously trained V2 model. Never throws: any failure just means
    // "no manifest", so Retrain falls back to training everything.
    internal static SegAiRetrainManifest? ReadRetrainManifest(string path, out string? reason)
    {
        reason = null;
        try
        {
            using var stream = File.OpenRead(path);
            stream.Position = Math.Max(SegAiModelFile.HeaderSize, stream.Length - 32 * 1024 * 1024);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            byte[]? encoded = SegAiModelFile.ReadModelMetadata(reader.ReadToEnd(), RetrainManifestKey);
            if (encoded == null) { reason = "model has no Retrain image list (trained before V146)"; return null; }
            var manifest = JsonConvert.DeserializeObject<SegAiRetrainManifest>(Encoding.UTF8.GetString(encoded));
            if (manifest == null || manifest.Schema != RetrainManifestSchema || manifest.Frames == null || manifest.Context == null ||
                manifest.Generation < 1 || manifest.Frames.Any(f => f == null || f.Count(c => c == '|') != 1))
            {
                reason = "Retrain image list unreadable";
                return null;
            }

            return manifest;
        }
        catch (Exception)
        {
            reason = "Retrain image list unreadable";
            return null;
        }
    }
}
