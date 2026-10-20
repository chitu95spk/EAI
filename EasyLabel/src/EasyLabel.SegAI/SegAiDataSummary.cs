using EasyLabel.SegAI.Data;

namespace EasyLabel.SegAI;

/// <summary>Row of the Step 1 data summary; also the filter used to find the matching images.</summary>
public enum SegAiDataFilter
{
    Train,
    Val,
    /// <summary>No regions: the image does not take part in training.</summary>
    Ignored,
    Stale,
    MissingJson,
}

/// <summary>Counts of a project's images by Segment AI state (UI-free; built from <see cref="SegAiSession.Samples"/>).</summary>
/// <param name="Total">Every counted image; Train + Val + Ignored equals Total.</param>
/// <param name="Stale">Images whose regions no longer match the stored sidecar (independent of the mode).</param>
/// <param name="MissingJson">Images without a LabelMe JSON next to them (independent of the mode).</param>
public sealed record SegAiDataSummary(int Total, int Train, int Val, int Ignored, int Stale, int MissingJson)
{
    public static SegAiDataSummary Empty { get; } = new SegAiDataSummary(0, 0, 0, 0, 0, 0);

    /// <summary>Counts the samples; null entries are skipped.</summary>
    public static SegAiDataSummary From(IEnumerable<SegAiSampleInfo?> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        int total = 0, train = 0, val = 0, ignored = 0, stale = 0, missing = 0;
        foreach (SegAiSampleInfo? sample in samples)
        {
            if (sample == null) continue;
            total++;
            switch (DisplayMode(sample.Mode))
            {
                case SegTrainingMode.Val: val++; break;
                case SegTrainingMode.Ignored: ignored++; break;
                default: train++; break;
            }

            if (sample.Stale) stale++;
            if (!sample.HasLabelMeJson) missing++;
        }

        return new SegAiDataSummary(total, train, val, ignored, stale, missing);
    }

    /// <summary>Mode shown to the user: Auto (resolved at training time) is displayed as Train.</summary>
    public static SegTrainingMode DisplayMode(SegTrainingMode mode)
    {
        return mode == SegTrainingMode.Auto ? SegTrainingMode.Train : mode;
    }

    /// <summary>True when the sample belongs to the summary row <paramref name="filter"/>.</summary>
    public static bool Matches(SegAiSampleInfo sample, SegAiDataFilter filter)
    {
        ArgumentNullException.ThrowIfNull(sample);
        switch (filter)
        {
            case SegAiDataFilter.Train: return DisplayMode(sample.Mode) == SegTrainingMode.Train;
            case SegAiDataFilter.Val: return DisplayMode(sample.Mode) == SegTrainingMode.Val;
            case SegAiDataFilter.Ignored: return DisplayMode(sample.Mode) == SegTrainingMode.Ignored;
            case SegAiDataFilter.Stale: return sample.Stale;
            case SegAiDataFilter.MissingJson: return !sample.HasLabelMeJson;
            default: return false;
        }
    }

    /// <summary>The mode an image switches to when the user toggles Train/Val; null for an image without regions (nothing to toggle).</summary>
    public static SegTrainingMode? ToggledMode(SegTrainingMode current)
    {
        switch (DisplayMode(current))
        {
            case SegTrainingMode.Train: return SegTrainingMode.Val;
            case SegTrainingMode.Val: return SegTrainingMode.Train;
            default: return null;
        }
    }
}
