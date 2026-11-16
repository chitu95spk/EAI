using System.Globalization;
using EasyLabel.SegAI;
using EasyLabel.SegAI.Options;
using EasyLabel.SegAI.Training;
using EasyLabel.SegAI.Training.Ultralytics;

namespace EasyAI.Desktop.ViewModels;

/// <summary>Points of the live training chart (epoch on X). Validation series skip epochs that did not report a value.</summary>
public sealed record TrainChartData(
    double[] TrainLossX, double[] TrainLossY,
    double[] ValidationLossX, double[] ValidationLossY,
    double[] ValidationIouX, double[] ValidationIouY)
{
    public bool IsEmpty => TrainLossX.Length == 0 && ValidationLossX.Length == 0 && ValidationIouX.Length == 0;
}

/// <summary>
/// UI-free rules of Step 3 (Train): option limits, block reason to resource key, progress/ETA text, chart series, candidate list
/// display and the comparison row formatting. No WPF types, so it is unit tested from EasyLabel.SegAI.Tests (linked file).
/// </summary>
public static class TrainStepRules
{
    /// <summary>Progress percent at which the SegAI epochs are over and only the instance head / fine-tune / save remain.</summary>
    public const double FinishingPercent = 95;

    public const int MinEpochs = 1;
    public const int MaxEpochs = 2000;
    public const int DefaultEpochs = 200;
    public const int MinInputSize = 160;
    public const int MaxInputSize = 2560;
    public const int InputSizeStep = 32;
    public const int DefaultInputSize = 800;

    /// <summary>Longest wait for a stopped run to unwind before the UI reports an error.</summary>
    public static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(30);

    public static IReadOnlyList<int> InputSizeChoices()
    {
        List<int> sizes = new List<int>();
        for (int size = MinInputSize; size <= MaxInputSize; size += InputSizeStep)
        {
            sizes.Add(size);
        }

        return sizes;
    }

    /// <summary>Epoch count from the number box: empty/NaN gives the default, anything else is rounded and clamped to 1..2000.</summary>
    public static int NormalizeEpochs(double? value)
    {
        if (value == null || double.IsNaN(value.Value) || double.IsInfinity(value.Value))
        {
            return DefaultEpochs;
        }

        return (int)Math.Min(MaxEpochs, Math.Max(MinEpochs, Math.Round(value.Value)));
    }

    /// <summary>Nearest multiple of 32 inside 160..2560.</summary>
    public static int NormalizeInputSize(int value)
    {
        int snapped = (int)Math.Round(value / (double)InputSizeStep) * InputSizeStep;
        return Math.Min(MaxInputSize, Math.Max(MinInputSize, snapped));
    }

    /// <summary>Options of a run: V2 head training with only Epochs and InputSize chosen; every other field keeps the pipeline default.</summary>
    public static SegTrainingOptions BuildOptions(int epochs, int inputSize, int earlyStopPatience = 0)
    {
        return new SegTrainingOptions
        {
            Version = 2,
            Epochs = NormalizeEpochs(epochs),
            InputSize = NormalizeInputSize(inputSize),
            EarlyStopPatience = NormalizeEarlyStop(earlyStopPatience),
        };
    }

    public const int MinEarlyStop = 5;
    public const int MaxEarlyStop = 200;

    /// <summary>Early-stop window in epochs: 0 (automatic) or 5..200; a value between 1 and 4 is raised to 5.</summary>
    public static int NormalizeEarlyStop(double? value)
    {
        if (value == null || double.IsNaN(value.Value) || double.IsInfinity(value.Value) || value.Value < 1)
        {
            return 0;
        }

        return (int)Math.Min(MaxEarlyStop, Math.Max(MinEarlyStop, Math.Round(value.Value)));
    }

    /// <summary>Resource key of the text explaining why training cannot start; null for <see cref="SegAiTrainBlock.None"/>.</summary>
    public static string? BlockKey(SegAiTrainBlock block)
    {
        switch (block)
        {
            case SegAiTrainBlock.NoTrainImages: return "TrainBlockNoTrainImages";
            case SegAiTrainBlock.NoValImages: return "TrainBlockNoValImages";
            case SegAiTrainBlock.NativeMissing: return "TrainBlockNativeMissing";
            case SegAiTrainBlock.BackboneMissing: return "TrainBlockBackboneMissing";
            case SegAiTrainBlock.Busy: return "TrainBlockBusy";
            case SegAiTrainBlock.NoInstanceLabels: return "TrainBlockNoInstanceLabels";
            default: return null;
        }
    }

    /// <summary>Fewest labelled images (Train + Val) Segment AI training needs: one for Train, one for Val.</summary>
    public const int MinSegmentImages = 2;

    /// <summary>What a block reason offers as a one-click fix; <see cref="None"/> = only the text.</summary>
    public enum TrainFixKind
    {
        None,
        AutoAssignVal,

        /// <summary>Open the folder that should hold the missing backbone model file.</summary>
        OpenModelFolder,

        /// <summary>Switch the trainer choice to Segment AI (works on every PC).</summary>
        UseSegmentAi,

        /// <summary>Switch to the Label tab to label more images.</summary>
        GoToLabel,
    }

    /// <summary>
    /// The fix of a Segment AI block: no Val image while at least <see cref="MinSegmentImages"/> labelled images exist is fixed by
    /// auto-assigning Val; too few labelled images by going to Label; a missing backbone by opening the model folder.
    /// </summary>
    public static TrainFixKind FixKindOf(SegAiTrainBlock block, int labelledImages)
    {
        switch (block)
        {
            case SegAiTrainBlock.NoValImages:
                return labelledImages >= MinSegmentImages ? TrainFixKind.AutoAssignVal : TrainFixKind.GoToLabel;
            case SegAiTrainBlock.NoTrainImages:
                return TrainFixKind.GoToLabel;
            case SegAiTrainBlock.BackboneMissing:
                return TrainFixKind.OpenModelFolder;
            case SegAiTrainBlock.NoInstanceLabels:
                return TrainFixKind.GoToLabel; // Position: draw oriented boxes or circles in Label
            default:
                return TrainFixKind.None;
        }
    }

    /// <summary>The fix of a YOLO / RT-DETR block: an unusable trainer is replaced by Segment AI, too few labelled images are fixed in Label.</summary>
    public static TrainFixKind UltraFixKindOf(UltraBlock block)
    {
        switch (block)
        {
            case UltraBlock.NoTrainerFolder:
            case UltraBlock.NoVenv:
            case UltraBlock.NoCuda:
            case UltraBlock.Failed:
            case UltraBlock.NoFamily:
                return TrainFixKind.UseSegmentAi;
            case UltraBlock.NoSamples:
            case UltraBlock.TooFewImages:
                return TrainFixKind.GoToLabel;
            default:
                return TrainFixKind.None;
        }
    }

    /// <summary>Resource key of the fix button caption; null for <see cref="TrainFixKind.None"/>.</summary>
    public static string? FixButtonKey(TrainFixKind kind)
    {
        switch (kind)
        {
            case TrainFixKind.AutoAssignVal: return "TrainAutoAssignVal";
            case TrainFixKind.OpenModelFolder: return "TrainFixOpenModelFolder";
            case TrainFixKind.UseSegmentAi: return "TrainFixUseSegmentAi";
            case TrainFixKind.GoToLabel: return "TrainFixGoToLabel";
            default: return null;
        }
    }

    /// <summary>
    /// The folder to show for a missing file: the nearest existing parent of <paramref name="path"/> (the folder itself when it exists).
    /// Null for an empty or invalid path or when no parent exists. Never throws.
    /// </summary>
    public static string? NearestExistingFolder(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            string? current = System.IO.Path.GetFullPath(path);
            while (!string.IsNullOrEmpty(current))
            {
                if (System.IO.Directory.Exists(current))
                {
                    return current;
                }

                current = System.IO.Path.GetDirectoryName(current);
            }
        }
        catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is System.IO.PathTooLongException || ex is System.Security.SecurityException)
        {
            return null;
        }

        return null;
    }

    /// <summary>
    /// Resource key of the Segment AI block text. Like <see cref="BlockKey"/>, but a missing Val image with fewer than
    /// <see cref="MinSegmentImages"/> labelled images asks for more labelled images (auto-assigning Val cannot help).
    /// </summary>
    public static string? SegmentBlockKey(SegAiTrainBlock block, int labelledImages)
    {
        if (block == SegAiTrainBlock.NoValImages && labelledImages < MinSegmentImages)
        {
            return "TrainBlockNeedTwoImages";
        }

        return BlockKey(block);
    }

    /// <summary>Resource key of the text for a dataset problem found while building the YOLO dataset.</summary>
    public static string UltraDatasetProblemKey(UltraDatasetProblem problem)
    {
        return problem == UltraDatasetProblem.ClassNotInTrain ? "UltraErrorClassNotInTrain" : "UltraErrorTooFewImages";
    }

    /// <summary>The Start button: a session, nothing else running, no run of this panel and no missing prerequisite.</summary>
    public static bool CanStart(bool hasSession, bool hostBusy, bool running, SegAiTrainBlock block)
    {
        return hasSession && !hostBusy && !running && block == SegAiTrainBlock.None;
    }

    /// <summary>"Use this model": a selected candidate that is not active while nothing else runs.</summary>
    public static bool CanUseModel(bool hasSession, bool hostBusy, bool running, SegAiCandidateInfo? selected)
    {
        return hasSession && !hostBusy && !running && selected != null && !selected.IsActive;
    }

    /// <summary>Number of epochs finished so far (the highest reported epoch number).</summary>
    public static int EpochsDone(IReadOnlyList<SegEpochMetric> epochs)
    {
        int done = 0;
        foreach (SegEpochMetric metric in epochs)
        {
            done = Math.Max(done, metric.Epoch);
        }

        return done;
    }

    /// <summary>Remaining time from the average epoch time so far; null before the first epoch finished.</summary>
    public static TimeSpan? Eta(TimeSpan elapsed, int epochsDone, int totalEpochs)
    {
        if (epochsDone <= 0 || totalEpochs <= 0)
        {
            return null;
        }

        int left = Math.Max(0, totalEpochs - epochsDone);
        return TimeSpan.FromTicks((long)(elapsed.Ticks / (double)epochsDone * left));
    }

    /// <summary>h:mm:ss, or --:-- for an unknown duration.</summary>
    public static string FormatDuration(TimeSpan? value)
    {
        if (value == null)
        {
            return "--:--";
        }

        return value.Value.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture);
    }

    /// <summary>X/Y series for the loss and IoU plots.</summary>
    public static TrainChartData BuildChart(IReadOnlyList<SegEpochMetric> epochs)
    {
        List<double> tx = new List<double>(), ty = new List<double>();
        List<double> vx = new List<double>(), vy = new List<double>();
        List<double> ix = new List<double>(), iy = new List<double>();
        foreach (SegEpochMetric metric in epochs)
        {
            if (IsFinite(metric.TrainLoss))
            {
                tx.Add(metric.Epoch);
                ty.Add(metric.TrainLoss);
            }

            if (metric.ValidationLoss is double validationLoss && IsFinite(validationLoss))
            {
                vx.Add(metric.Epoch);
                vy.Add(validationLoss);
            }

            if (metric.ValidationIou is double iou && IsFinite(iou))
            {
                ix.Add(metric.Epoch);
                iy.Add(iou);
            }
        }

        return new TrainChartData(tx.ToArray(), ty.ToArray(), vx.ToArray(), vy.ToArray(), ix.ToArray(), iy.ToArray());
    }

    /// <summary>Y range of a series set: from 0 to a little above the maximum (1 when there is no positive value).</summary>
    public static double YMax(params double[][] series)
    {
        double max = 0;
        foreach (double[] values in series)
        {
            foreach (double value in values)
            {
                max = Math.Max(max, value);
            }
        }

        return max > 0 ? max * 1.1 : 1.0;
    }

    // ---------- candidate list ----------

    /// <summary>ACTIVE for the active model, otherwise the registry state (TRAINED, IMPORTED).</summary>
    public static string StateText(SegAiCandidateInfo candidate)
    {
        return candidate.IsActive ? "ACTIVE" : candidate.Entry.State;
    }

    public static string Map50Text(SegAiCandidateInfo candidate, CultureInfo culture)
    {
        return candidate.Map50 is double value && IsFinite(value) ? value.ToString("P1", culture) : "-";
    }

    public static string ElapsedText(SegAiCandidateInfo candidate)
    {
        return string.IsNullOrEmpty(candidate.ElapsedText) ? "-" : candidate.ElapsedText!;
    }

    public static string CreatedText(SegAiCandidateInfo candidate, CultureInfo culture)
    {
        return candidate.Entry.CreatedUtc.ToLocalTime().ToString("g", culture);
    }

    /// <summary>Registry order is oldest first; the list shows the newest first.</summary>
    public static IReadOnlyList<SegAiCandidateInfo> NewestFirst(IReadOnlyList<SegAiCandidateInfo> candidates)
    {
        List<SegAiCandidateInfo> list = new List<SegAiCandidateInfo>(candidates);
        list.Reverse();
        return list;
    }

    // ---------- comparison ----------

    /// <summary>Resource key of the metric name.</summary>
    public static string MetricKey(SegAiComparisonMetric metric)
    {
        switch (metric)
        {
            case SegAiComparisonMetric.Map50: return "MetricMap50";
            case SegAiComparisonMetric.Map5095: return "MetricMap5095";
            case SegAiComparisonMetric.PixelIou: return "MetricPixelIou";
            case SegAiComparisonMetric.PixelPrecision: return "MetricPixelPrecision";
            case SegAiComparisonMetric.PixelRecall: return "MetricPixelRecall";
            case SegAiComparisonMetric.ClassAp50: return "MetricClassAp50Format";
            case SegAiComparisonMetric.MeanBoxAreaRatio: return "MetricBoxRatio";
            case SegAiComparisonMetric.ImagesOk: return "MetricImagesOk";
            case SegAiComparisonMetric.ImagesFalseAlarm: return "MetricImagesFalseAlarm";
            case SegAiComparisonMetric.ImagesPartial: return "MetricImagesPartial";
            case SegAiComparisonMetric.ImagesMissed: return "MetricImagesMissed";
            default: return "MetricValidationImages";
        }
    }

    /// <summary>Resource key of the verdict text (used as the non-colour cue next to the icon).</summary>
    public static string VerdictKey(SegAiVerdict verdict)
    {
        switch (verdict)
        {
            case SegAiVerdict.Better: return "VerdictBetter";
            case SegAiVerdict.Worse: return "VerdictWorse";
            case SegAiVerdict.Equal: return "VerdictEqual";
            default: return "VerdictNotComparable";
        }
    }

    private static bool IsFraction(SegAiComparisonMetric metric)
    {
        return metric == SegAiComparisonMetric.Map50
            || metric == SegAiComparisonMetric.Map5095
            || metric == SegAiComparisonMetric.PixelIou
            || metric == SegAiComparisonMetric.PixelPrecision
            || metric == SegAiComparisonMetric.PixelRecall
            || metric == SegAiComparisonMetric.ClassAp50;
    }

    /// <summary>Value of one side: percent for fractions, two decimals for the box ratio, an integer for counts, "-" when missing.</summary>
    public static string FormatValue(SegAiComparisonMetric metric, double? value, CultureInfo culture)
    {
        if (value == null || !IsFinite(value.Value))
        {
            return "-";
        }

        if (IsFraction(metric))
        {
            return value.Value.ToString("P1", culture);
        }

        return metric == SegAiComparisonMetric.MeanBoxAreaRatio
            ? value.Value.ToString("0.00", culture)
            : Math.Round(value.Value).ToString("0", culture);
    }

    /// <summary>Signed change (candidate minus active): percentage points for fractions, plain difference otherwise; "-" when missing.</summary>
    public static string FormatDelta(SegAiComparisonMetric metric, double? delta, CultureInfo culture)
    {
        if (delta == null || !IsFinite(delta.Value))
        {
            return "-";
        }

        if (IsFraction(metric))
        {
            return (delta.Value * 100.0).ToString("+0.0;-0.0;0.0", culture) + " pt";
        }

        return metric == SegAiComparisonMetric.MeanBoxAreaRatio
            ? delta.Value.ToString("+0.00;-0.00;0.00", culture)
            : Math.Round(delta.Value).ToString("+0;-0;0", culture);
    }

    // ---------- YOLO / RT-DETR (Ultralytics trainer) ----------

    /// <summary>What the one-shot trainer queries (locate, preflight, list families) found.</summary>
    public enum UltraEnvironment
    {
        Unknown,
        Checking,
        Ready,
        NoTrainerFolder,
        NoVenv,
        NoCuda,
        Failed,

        /// <summary>No NVIDIA GPU, but the CPU run is allowed (Advanced mode and the Settings opt-in); see <see cref="EffectiveUltraEnvironment"/>.</summary>
        CpuOnly,
    }

    /// <summary>Why a YOLO / RT-DETR run cannot start; <see cref="None"/> = it can.</summary>
    public enum UltraBlock
    {
        None,
        NoSession,
        Busy,
        Checking,
        NoTrainerFolder,
        NoVenv,
        NoCuda,
        Failed,
        NoFamily,
        NoSamples,

        /// <summary>Fewer than <see cref="UltraDatasetBuilder.MinYoloImages"/> labelled images.</summary>
        TooFewImages,
    }

    public const int MaxBatch = 512;

    public static UltraBlock UltraBlockOf(bool hasSession, bool hostBusy, bool running, UltraEnvironment environment, bool hasFamily, bool hasTrainSamples = true, int labelledImages = int.MaxValue)
    {
        if (!hasSession)
        {
            return UltraBlock.NoSession;
        }

        if (hostBusy || running)
        {
            return UltraBlock.Busy;
        }

        switch (environment)
        {
            case UltraEnvironment.Unknown:
            case UltraEnvironment.Checking:
                return UltraBlock.Checking;
            case UltraEnvironment.NoTrainerFolder:
                return UltraBlock.NoTrainerFolder;
            case UltraEnvironment.NoVenv:
                return UltraBlock.NoVenv;
            case UltraEnvironment.NoCuda:
                return UltraBlock.NoCuda;
            case UltraEnvironment.Failed:
                return UltraBlock.Failed;
            case UltraEnvironment.CpuOnly:
                break;
        }

        if (!hasFamily)
        {
            return UltraBlock.NoFamily;
        }

        // A Val count of 0 is allowed: the dataset builder assigns Val itself when no image is set to Val.
        if (!hasTrainSamples)
        {
            return UltraBlock.NoSamples;
        }

        return labelledImages < UltraDatasetBuilder.MinYoloImages ? UltraBlock.TooFewImages : UltraBlock.None;
    }

    public static bool CanStartUltra(UltraBlock block) => block == UltraBlock.None;

    /// <summary>Smallest input size of a CPU run (the preselected range starts here).</summary>
    public const int CpuInputSizeMin = 320;

    /// <summary>Largest input size of a CPU run.</summary>
    public const int CpuInputSizeMax = 416;

    /// <summary>
    /// The environment a run uses. Without CUDA it becomes <see cref="UltraEnvironment.CpuOnly"/> only in Advanced mode with the Settings
    /// opt-in; every other probe answer (Ready included) is kept as it is.
    /// </summary>
    public static UltraEnvironment EffectiveUltraEnvironment(UltraEnvironment probed, bool advanced, bool cpuOptIn)
    {
        return probed == UltraEnvironment.NoCuda && advanced && cpuOptIn ? UltraEnvironment.CpuOnly : probed;
    }

    /// <summary>True when the run trains on the CPU (<see cref="UltraTrainRequest.AllowCpu"/>).</summary>
    public static bool IsCpuRun(UltraEnvironment effective) => effective == UltraEnvironment.CpuOnly;

    /// <summary>The CPU family: the n-size detect model (yolov8n, yolo11n). The trainer accepts no other family on the CPU.</summary>
    public static bool IsCpuFamily(UltraFamily family)
    {
        return family.Task == "detect"
            && !family.Experimental
            && family.Id.StartsWith("yolo", StringComparison.Ordinal)
            && family.Id.EndsWith("n", StringComparison.Ordinal);
    }

    /// <summary>Input size of a CPU run: the value moved into 320..416 (snapped to the 32 step).</summary>
    public static int CpuInputSize(int value) => NormalizeInputSize(Math.Min(CpuInputSizeMax, Math.Max(CpuInputSizeMin, value)));

    /// <summary>Resource key of the reason text; null for <see cref="UltraBlock.None"/>.</summary>
    public static string? UltraBlockKey(UltraBlock block)
    {
        switch (block)
        {
            case UltraBlock.NoSession: return "TrainBlockNoSession";
            case UltraBlock.Busy: return "TrainBlockBusy";
            case UltraBlock.Checking: return "UltraBlockChecking";
            case UltraBlock.NoTrainerFolder: return "UltraBlockNoTrainerFolder";
            case UltraBlock.NoVenv: return "UltraBlockNoVenv";
            case UltraBlock.NoCuda: return "UltraBlockNoCuda";
            case UltraBlock.Failed: return "UltraBlockFailed";
            case UltraBlock.NoFamily: return "UltraBlockNoFamily";
            case UltraBlock.NoSamples: return "UltraBlockNoSamples";
            case UltraBlock.TooFewImages: return "UltraBlockTooFewImages";
            default: return null;
        }
    }

    /// <summary>Batch size from the number box: empty, NaN or below 1 = null (the family default); otherwise rounded and clamped to 1..512.</summary>
    public static int? NormalizeBatch(double? value)
    {
        if (value == null || double.IsNaN(value.Value) || double.IsInfinity(value.Value) || value.Value < 0.5)
        {
            return null;
        }

        return (int)Math.Min(MaxBatch, Math.Max(1, Math.Round(value.Value)));
    }

    /// <summary>Epoch progress 0..100 from the trainer's epoch counters.</summary>
    public static int UltraPercent(int epoch, int epochs)
    {
        if (epochs <= 0 || epoch <= 0)
        {
            return 0;
        }

        return (int)Math.Min(100, Math.Round(100.0 * epoch / epochs));
    }

    /// <summary>
    /// Chart point of an epoch event: TrainLoss = box + cls + dfl over the finite parts; ValidationLoss and the IoU stay empty
    /// (mAP is not an IoU). Null for a non-epoch event or one without any loss.
    /// </summary>
    public static SegEpochMetric? UltraEpochMetric(UltraEvent ev)
    {
        if (ev.Kind != UltraEventKind.Epoch || ev.Epoch <= 0)
        {
            return null;
        }

        double sum = 0;
        bool any = false;
        foreach (double? part in new[] { ev.BoxLoss, ev.ClsLoss, ev.DflLoss })
        {
            if (part is double value && IsFinite(value))
            {
                sum += value;
                any = true;
            }
        }

        if (!any)
        {
            return null;
        }

        return new SegEpochMetric { Epoch = ev.Epoch, Seconds = ev.ElapsedSeconds, TrainLoss = sum, ValidationLoss = null };
    }

    /// <summary>Family id plus the task, with a suffix for experimental or not-4-GB items (kept selectable): "yolov8n-seg (segment) (experimental)".</summary>
    public static string FamilyLabel(UltraFamily family, string experimentalSuffix, string needsVramSuffix)
    {
        string text = family.Id + " (" + family.Task + ")";
        if (family.Experimental)
        {
            text += " " + experimentalSuffix;
        }

        if (!family.Fits4Gb)
        {
            text += " " + needsVramSuffix;
        }

        return text;
    }

    /// <summary>"GPU name · 4096 MB" (or just the name when the VRAM is unknown).</summary>
    public static string UltraGpuText(string? gpu, int? vramMb)
    {
        string name = string.IsNullOrWhiteSpace(gpu) ? "GPU" : gpu.Trim();
        return vramMb is int mb && mb > 0 ? name + " · " + mb.ToString(CultureInfo.InvariantCulture) + " MB" : name;
    }

    /// <summary>The first non-empty line of a trainer message, cut to <paramref name="maxChars"/> with an ellipsis.</summary>
    public static string FirstLine(string? message, int maxChars)
    {
        foreach (string line in (message ?? string.Empty).Split('\n'))
        {
            string trimmed = line.Trim();
            if (trimmed.Length > 0)
            {
                return trimmed.Length <= maxChars ? trimmed : trimmed.Substring(0, Math.Max(0, maxChars - 1)) + "…";
            }
        }

        return string.Empty;
    }

    /// <summary>A loss or mAP to 3 decimals, "-" when missing.</summary>
    public static string UltraNumber(double? value)
    {
        return value is double v && IsFinite(v) ? v.ToString("0.000", CultureInfo.InvariantCulture) : "-";
    }

    /// <summary>Run folder name: UTC time as yyyyMMddTHHmmssfff.</summary>
    public static string UltraRunName(DateTime utc)
    {
        return utc.ToString("yyyyMMdd'T'HHmmssfff", CultureInfo.InvariantCulture);
    }

    /// <summary>Resource key of the status caption of a run row.</summary>
    public static string UltraStatusKey(string status)
    {
        switch ((status ?? string.Empty).ToLowerInvariant())
        {
            case UltraRunRecord.StatusSuccess: return "UltraRunSuccess";
            case UltraRunRecord.StatusCancelled: return "UltraRunCancelled";
            default: return "UltraRunFailed";
        }
    }

    /// <summary>The record that <c>ultra-run.json</c> gets for a finished request (success, cancelled or failed).</summary>
    public static UltraRunRecord BuildRunRecord(string runName, DateTime createdUtc, UltraFamily family, int epochs, int imgSize, int? batch, UltraTrainResult result)
    {
        return new UltraRunRecord
        {
            RunName = runName,
            CreatedUtc = createdUtc,
            Family = family.Id,
            Task = family.Task,
            Epochs = epochs,
            ImgSize = imgSize,
            Batch = batch,
            Classes = new List<string>(result.Classes),
            Status = result.Success ? UltraRunRecord.StatusSuccess : result.Cancelled ? UltraRunRecord.StatusCancelled : UltraRunRecord.StatusFailed,
            ErrorCode = result.ErrorCode,
            Message = result.ErrorMessage,
            BestPt = result.BestPt,
            Onnx = result.Onnx,
            Map50 = result.Map50,
            Map5095 = result.Map5095,
            ElapsedSeconds = result.ElapsedSeconds,
        };
    }

    private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
}
