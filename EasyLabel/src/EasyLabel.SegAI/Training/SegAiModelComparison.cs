using EasyLabel.SegAI.Options;

namespace EasyLabel.SegAI.Training;

public enum SegAiComparisonMetric
{
    Map50,
    Map5095,
    PixelIou,
    PixelPrecision,
    PixelRecall,

    /// <summary>One row per class label; <see cref="SegAiComparisonRow.Label"/> is the class.</summary>
    ClassAp50,

    /// <summary>Better when closer to 1.0 (the box is as large as the label).</summary>
    MeanBoxAreaRatio,

    /// <summary>Validation images with status OK (more is better).</summary>
    ImagesOk,

    /// <summary>Validation images with status false alarm (fewer is better).</summary>
    ImagesFalseAlarm,

    /// <summary>Validation images with status partial (fewer is better).</summary>
    ImagesPartial,

    /// <summary>Validation images with status missed (fewer is better).</summary>
    ImagesMissed,

    /// <summary>Number of validation images; informational: equal or NotComparable, never better or worse.</summary>
    ValidationImages,
}

public enum SegAiVerdict
{
    Equal,
    Better,
    Worse,
    NotComparable,
}

/// <param name="Label">Class label for <see cref="SegAiComparisonMetric.ClassAp50"/>, else null.</param>
/// <param name="Delta">Candidate minus Active (raw, also for the box-area ratio); null when either side is missing.</param>
public sealed record SegAiComparisonRow(SegAiComparisonMetric Metric, string? Label, double? Candidate, double? Active, double? Delta, SegAiVerdict Verdict);

/// <param name="DifferentValidationSet">Both reports exist and the validation image count differs or the dataset fingerprints are not the same known value (an unknown fingerprint counts as different).</param>
/// <param name="BoxOverextension">The candidate shows the prior-shift symptom: mAP50 high with mAP50-95 low, or a mean box-area ratio above the limit.</param>
/// <param name="NoActiveModel">There is no active report to compare against; every row is NotComparable.</param>
public sealed record SegAiModelComparisonResult(IReadOnlyList<SegAiComparisonRow> Rows, bool DifferentValidationSet, bool BoxOverextension, bool NoActiveModel);

/// <summary>Pure candidate-versus-active comparison of two embedded training reports (no native code, no I/O). A worse result is never hidden.</summary>
public static class SegAiModelComparison
{
    /// <summary>Differences up to this size (fractions and ratios, 0.5 percentage points) are Equal; counts compare exactly.</summary>
    public const double Epsilon = 0.005;

    // Doubles such as 0.805 - 0.80 land a hair above 0.005; without a tolerance a difference of exactly Epsilon would read as a change.
    private const double FloatTolerance = 1e-9;

    public const double OverextensionMap50Min = 0.8;

    public const double OverextensionMap5095Max = 0.4;

    public const double OverextensionBoxRatio = 1.3;

    public static SegAiModelComparisonResult Compare(SegTrainingReport? candidate, SegTrainingReport? active, string? candidateFingerprint, string? activeFingerprint)
    {
        var rows = new List<SegAiComparisonRow>
        {
            Row(SegAiComparisonMetric.Map50, null, candidate?.Map50, active?.Map50, true, Epsilon),
            Row(SegAiComparisonMetric.Map5095, null, candidate?.Map5095, active?.Map5095, true, Epsilon),
            Row(SegAiComparisonMetric.PixelIou, null, candidate?.PixelIou, active?.PixelIou, true, Epsilon),
            Row(SegAiComparisonMetric.PixelPrecision, null, candidate?.PixelPrecision, active?.PixelPrecision, true, Epsilon),
            Row(SegAiComparisonMetric.PixelRecall, null, candidate?.PixelRecall, active?.PixelRecall, true, Epsilon),
        };

        var labels = new List<string>();
        foreach (SegClassMetric metric in (candidate?.Classes ?? new List<SegClassMetric>()).Concat(active?.Classes ?? new List<SegClassMetric>()))
        {
            string label = metric?.Label ?? string.Empty;
            if (!labels.Contains(label, StringComparer.OrdinalIgnoreCase)) labels.Add(label);
        }

        foreach (string label in labels)
            rows.Add(Row(SegAiComparisonMetric.ClassAp50, label, ApOf(candidate, label), ApOf(active, label), true, Epsilon));

        rows.Add(RatioRow(candidate?.MeanBoxAreaRatio, active?.MeanBoxAreaRatio));
        rows.Add(Row(SegAiComparisonMetric.ImagesOk, null, CountOf(candidate, SegImageResult.StatusOk), CountOf(active, SegImageResult.StatusOk), true, 0));
        rows.Add(Row(SegAiComparisonMetric.ImagesFalseAlarm, null, CountOf(candidate, SegImageResult.StatusFalseAlarm), CountOf(active, SegImageResult.StatusFalseAlarm), false, 0));
        rows.Add(Row(SegAiComparisonMetric.ImagesPartial, null, CountOf(candidate, SegImageResult.StatusPartial), CountOf(active, SegImageResult.StatusPartial), false, 0));
        rows.Add(Row(SegAiComparisonMetric.ImagesMissed, null, CountOf(candidate, SegImageResult.StatusMissed), CountOf(active, SegImageResult.StatusMissed), false, 0));

        double? candidateVal = candidate?.ValidationImages, activeVal = active?.ValidationImages;
        SegAiComparisonRow valRow = Row(SegAiComparisonMetric.ValidationImages, null, candidateVal, activeVal, true, 0);
        // The validation count is context, not quality: a different count is NotComparable, never Better or Worse.
        if (valRow.Verdict == SegAiVerdict.Better || valRow.Verdict == SegAiVerdict.Worse) valRow = valRow with { Verdict = SegAiVerdict.NotComparable };
        rows.Add(valRow);

        bool noActive = active == null;
        bool differentValidation = false;
        if (candidate != null && active != null)
        {
            bool knownSame = !string.IsNullOrEmpty(candidateFingerprint) && string.Equals(candidateFingerprint, activeFingerprint, StringComparison.Ordinal);
            differentValidation = candidate.ValidationImages != active.ValidationImages || !knownSame;
        }

        bool overextension = candidate != null &&
            ((candidate.Map50.HasValue && candidate.Map50.Value >= OverextensionMap50Min && candidate.Map5095.HasValue && candidate.Map5095.Value <= OverextensionMap5095Max)
             || (candidate.MeanBoxAreaRatio.HasValue && candidate.MeanBoxAreaRatio.Value > OverextensionBoxRatio));
        return new SegAiModelComparisonResult(rows, differentValidation, overextension, noActive);
    }

    private static double? ApOf(SegTrainingReport? report, string label)
    {
        SegClassMetric? metric = report?.Classes?.FirstOrDefault(c => c != null && string.Equals(c.Label ?? string.Empty, label, StringComparison.OrdinalIgnoreCase));
        return metric?.AveragePrecision50;
    }

    /// <summary>Count of validation images with this status; null when the report has no per-image results (older reports).</summary>
    private static double? CountOf(SegTrainingReport? report, int status)
    {
        if (report?.ImageResults == null) return null;
        return report.ImageResults.Count(r => r != null && r.Status == status);
    }

    private static SegAiComparisonRow Row(SegAiComparisonMetric metric, string? label, double? candidate, double? active, bool higherIsBetter, double epsilon)
    {
        if (candidate == null || active == null || double.IsNaN(candidate.Value) || double.IsNaN(active.Value))
            return new SegAiComparisonRow(metric, label, candidate, active, null, SegAiVerdict.NotComparable);
        double delta = candidate.Value - active.Value;
        SegAiVerdict verdict = Math.Abs(delta) <= epsilon + FloatTolerance ? SegAiVerdict.Equal
            : (delta > 0) == higherIsBetter ? SegAiVerdict.Better : SegAiVerdict.Worse;
        return new SegAiComparisonRow(metric, label, candidate, active, delta, verdict);
    }

    private static SegAiComparisonRow RatioRow(double? candidate, double? active)
    {
        if (candidate == null || active == null || double.IsNaN(candidate.Value) || double.IsNaN(active.Value))
            return new SegAiComparisonRow(SegAiComparisonMetric.MeanBoxAreaRatio, null, candidate, active, null, SegAiVerdict.NotComparable);
        double candidateGap = Math.Abs(candidate.Value - 1.0), activeGap = Math.Abs(active.Value - 1.0);
        double change = candidateGap - activeGap; // negative = the candidate is closer to 1.0
        SegAiVerdict verdict = Math.Abs(change) <= Epsilon + FloatTolerance ? SegAiVerdict.Equal : change < 0 ? SegAiVerdict.Better : SegAiVerdict.Worse;
        return new SegAiComparisonRow(SegAiComparisonMetric.MeanBoxAreaRatio, null, candidate, active, candidate.Value - active.Value, verdict);
    }
}
