using EasyLabel.Core.Hardware;

namespace EasyAI.Desktop.ViewModels;

/// <summary>"About N min left" during a run: measured time per finished epoch times the epochs still to do. Pure, no WPF types.</summary>
public static class TrainEtaCalculator
{
    /// <summary>Time left, or null while unknown (no finished epoch, no total, or no measured time). Zero once every epoch is done.</summary>
    public static TimeSpan? Remaining(TimeSpan elapsed, int epochsDone, int totalEpochs)
    {
        if (epochsDone <= 0 || totalEpochs <= 0 || elapsed <= TimeSpan.Zero)
        {
            return null;
        }

        if (epochsDone >= totalEpochs)
        {
            return TimeSpan.Zero;
        }

        int left = totalEpochs - epochsDone;
        return TimeSpan.FromTicks((long)(elapsed.Ticks / (double)epochsDone * left));
    }

    /// <summary>Whole minutes for the label: null when unknown, 0 when finished, otherwise at least 1.</summary>
    public static int? RemainingMinutes(TimeSpan elapsed, int epochsDone, int totalEpochs)
    {
        TimeSpan? left = Remaining(elapsed, epochsDone, totalEpochs);
        if (left == null)
        {
            return null;
        }

        if (left.Value <= TimeSpan.Zero)
        {
            return 0;
        }

        return Math.Max(1, (int)Math.Round(left.Value.TotalMinutes, MidpointRounding.AwayFromZero));
    }

    /// <summary>The estimate is recomputed once per finished epoch only (a later, higher epoch count than the one last shown).</summary>
    public static bool ShouldRefresh(int shownAtEpoch, int epochsDone)
    {
        return epochsDone >= 1 && epochsDone > shownAtEpoch;
    }
}

/// <summary>The Train options the recommendation can change (the values of the Epochs box, Input size combo, trainer kind and YOLO batch box).</summary>
public sealed class TrainOptionValues
{
    public TrainOptionValues(double? epochs, int inputSize, bool isUltra, double? batch)
    {
        Epochs = epochs;
        InputSize = inputSize;
        IsUltra = isUltra;
        Batch = batch;
    }

    public double? Epochs { get; }

    public int InputSize { get; }

    public bool IsUltra { get; }

    public double? Batch { get; }
}

/// <summary>Maps <see cref="RecommendedSettings"/> onto the Train options and builds the one-line recommendation text. Pure, no WPF types.</summary>
public static class TrainRecommendationRules
{
    public const string ModeKeyPrefix = "Hardware.Mode.";
    public const string FormatKey = "Hardware.RecommendedFormat";
    public const string SafeSlowerKey = "Hardware.SafeSlower";
    public const string EtaFormatKey = "TrainEtaFormat";
    public const string EtaSoonKey = "TrainEtaSoon";

    /// <summary>True while the user has not touched the options: default epochs (or empty), default input size, no YOLO batch.</summary>
    public static bool AreDefaults(TrainOptionValues values)
    {
        bool epochsDefault = values.Epochs == null || TrainStepRules.NormalizeEpochs(values.Epochs) == TrainStepRules.DefaultEpochs;
        bool batchDefault = TrainStepRules.NormalizeBatch(values.Batch) == null;
        return epochsDefault && values.InputSize == TrainStepRules.DefaultInputSize && batchDefault && !values.IsUltra;
    }

    /// <summary>
    /// The options after applying the recommendation. Without <paramref name="force"/> options the user already changed are returned
    /// untouched. Only options the trainer has are set: epochs, input size (capped, never raised), the YOLO batch, and Segment AI for
    /// a Safe or Segment-AI-only recommendation.
    /// </summary>
    public static TrainOptionValues Apply(RecommendedSettings recommended, TrainOptionValues current, bool force)
    {
        if (!force && !AreDefaults(current))
        {
            return current;
        }

        bool isUltra = current.IsUltra && !(recommended.Mode == PerformanceMode.Safe || recommended.UseSegmentAiOnly);
        double? epochs = recommended.EpochsHint > 0 ? recommended.EpochsHint : current.Epochs;
        int size = current.InputSize;
        if (recommended.MaxInputPixels > 0)
        {
            size = Math.Min(size, TrainStepRules.NormalizeInputSize(recommended.MaxInputPixels));
        }

        double? batch = current.Batch;
        if (isUltra && recommended.BatchSize > 0)
        {
            batch = recommended.BatchSize;
        }

        return new TrainOptionValues(epochs, size, isUltra, batch);
    }

    /// <summary>"Recommended for this PC: &lt;mode&gt; - &lt;reason&gt;"; Safe mode adds that training is slower and uses fewer resources.</summary>
    public static string Text(RecommendedSettings recommended, Func<string, string?> lookup)
    {
        string mode = Resolve(lookup, ModeKeyPrefix + recommended.Mode, ModeFallback(recommended.Mode));
        string reason = recommended.Notes.Count > 0
            ? Resolve(lookup, recommended.Notes[0], NoteFallback(recommended.Notes[0]))
            : string.Empty;
        if (recommended.Mode == PerformanceMode.Safe)
        {
            string slower = Resolve(lookup, SafeSlowerKey, "training is slower and uses fewer resources");
            reason = reason.Length == 0 ? slower : reason + "; " + slower;
        }

        string format = Resolve(lookup, FormatKey, "Recommended for this PC: {0} - {1}");
        return string.Format(System.Globalization.CultureInfo.CurrentCulture, format, mode, reason);
    }

    /// <summary>"About N min left"; an unknown or finished estimate gives an empty text.</summary>
    public static string EtaText(int? minutes, Func<string, string?> lookup)
    {
        if (minutes == null || minutes.Value <= 0)
        {
            return string.Empty;
        }

        string format = Resolve(lookup, EtaFormatKey, "About {0} min left");
        return string.Format(System.Globalization.CultureInfo.CurrentCulture, format, minutes.Value);
    }

    /// <summary>The resource text, or <paramref name="fallback"/> (English) when the resource is missing, blank or throws.</summary>
    public static string Resolve(Func<string, string?> lookup, string key, string fallback)
    {
        string? text = null;
        try
        {
            text = lookup(key);
        }
        catch (Exception)
        {
            text = null;
        }

        // Strings.ByName returns the key itself for a missing resource.
        return string.IsNullOrWhiteSpace(text) || string.Equals(text, key, StringComparison.Ordinal) ? fallback : text!;
    }

    private static string ModeFallback(PerformanceMode mode)
    {
        switch (mode)
        {
            case PerformanceMode.Safe: return "Safe mode";
            case PerformanceMode.Fast: return "Fast";
            default: return "Standard";
        }
    }

    private static string NoteFallback(string key)
    {
        switch (key)
        {
            case RecommendedSettings.NoteHardwareUnknown: return "this PC could not be read";
            case RecommendedSettings.NoteSafeFewCores: return "few CPU cores";
            case RecommendedSettings.NoteSafeLowRam: return "limited memory";
            case RecommendedSettings.NoteSafeSegmentAiOnly: return "Segment AI only";
            case RecommendedSettings.NoteSafeBelowNormal: return "background priority";
            case RecommendedSettings.NoteFastCpuRam: return "many CPU cores and plenty of memory";
            case RecommendedSettings.NoteFastCudaGpu: return "a CUDA graphics card";
            case RecommendedSettings.NoteGpuAvailable: return "a graphics card is available";
            case RecommendedSettings.NoteStandard: return "balanced settings";
            default: return string.Empty;
        }
    }
}
