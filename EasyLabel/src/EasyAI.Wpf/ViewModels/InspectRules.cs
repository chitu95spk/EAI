using System.Globalization;
using EasyLabel.SegAI;
using EasyLabel.SegAI.Inspection;
using EasyLabel.SegAI.Model;

namespace EasyAI.Desktop.ViewModels;

/// <summary>One entry of the Inspect model selector.</summary>
/// <param name="File">Registry file name (vNNN.segai).</param>
/// <param name="State">Registry state (TRAINED, IMPORTED).</param>
/// <param name="Map50">mAP50 of the embedded report, or null.</param>
/// <param name="IsActive">The model the user activated; Auto-label always uses it.</param>
public sealed record InspectModelChoice(string File, string State, double? Map50, bool IsActive);

/// <summary>Why Run is unavailable; <see cref="None"/> when it is available.</summary>
public enum InspectBlock
{
    None,
    NoSession,
    Busy,
    Training,
    NoModel,
    NoImage,
}

/// <summary>Row filter of the Inspect list.</summary>
public enum InspectFilter
{
    All,
    Ok,
    Ng,
    NotRun,
    /// <summary>Images whose result needs a review (uncertainty bucket Doubtful or Unsure); see <see cref="InspectRules.MatchesReview"/>.</summary>
    Doubtful,
}

/// <summary>Sort column of the Inspect list.</summary>
public enum InspectSortKey
{
    File,
    Verdict,
    Regions,
    Score,
    Ms,
}

/// <summary>
/// UI-free rules of the Inspect tab: the model selector list and its preselection, the run guard, and the list filter, search and
/// sort. No WPF types, so it is unit tested from EasyLabel.SegAI.Tests (linked file).
/// </summary>
public static class InspectRules
{
    /// <summary>
    /// Models offered for inspection: the active model first (whatever its state), then every TRAINED or IMPORTED model, newest first.
    /// The registry lists oldest first, so the order is the reverse of the input. A model appears once.
    /// </summary>
    public static IReadOnlyList<InspectModelChoice> BuildChoices(IReadOnlyList<SegAiCandidateInfo> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        List<InspectModelChoice> list = new List<InspectModelChoice>();
        SegAiCandidateInfo? active = candidates.LastOrDefault(c => c.IsActive);
        if (active != null)
        {
            list.Add(ToChoice(active));
        }

        for (int i = candidates.Count - 1; i >= 0; i--)
        {
            SegAiCandidateInfo candidate = candidates[i];
            bool offered = string.Equals(candidate.Entry.State, SegAiRegistryEntry.StateTrained, StringComparison.OrdinalIgnoreCase)
                || string.Equals(candidate.Entry.State, SegAiRegistryEntry.StateImported, StringComparison.OrdinalIgnoreCase);
            if (candidate.IsActive || !offered || list.Any(c => string.Equals(c.File, candidate.Entry.File, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            list.Add(ToChoice(candidate));
        }

        return list;
    }

    private static InspectModelChoice ToChoice(SegAiCandidateInfo candidate)
    {
        return new InspectModelChoice(candidate.Entry.File, candidate.Entry.State, candidate.Map50, candidate.IsActive);
    }

    /// <summary>Selector text: "v003.segai · mAP50 84.2 % · (active)"; the suffix only for the active model.</summary>
    public static string ChoiceText(InspectModelChoice choice, string activeSuffix, CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(choice);
        string map = choice.Map50 is double value && !double.IsNaN(value) && !double.IsInfinity(value) ? value.ToString("P1", culture) : "-";
        string text = choice.File + " · mAP50 " + map;
        return choice.IsActive ? text + " · " + activeSuffix : text;
    }

    /// <summary>
    /// The model to select after the list was rebuilt: the current selection while it is still offered, otherwise the active model,
    /// otherwise nothing. A new candidate never changes the selection by itself.
    /// </summary>
    public static string? ResolveSelection(IReadOnlyList<InspectModelChoice> choices, string? current)
    {
        ArgumentNullException.ThrowIfNull(choices);
        if (current != null)
        {
            InspectModelChoice? kept = choices.FirstOrDefault(c => string.Equals(c.File, current, StringComparison.OrdinalIgnoreCase));
            if (kept != null)
            {
                return kept.File;
            }
        }

        return choices.FirstOrDefault(c => c.IsActive)?.File;
    }

    /// <summary>
    /// The newly trained model to offer as "Inspect with the new model": <paramref name="newFile"/> when it is in the list and is not
    /// already the selection; otherwise null. Offering never selects, loads or activates anything.
    /// </summary>
    public static string? OfferNewModel(IReadOnlyList<InspectModelChoice> choices, string? newFile, string? selected)
    {
        ArgumentNullException.ThrowIfNull(choices);
        if (string.IsNullOrEmpty(newFile))
        {
            return null;
        }

        InspectModelChoice? found = choices.FirstOrDefault(c => string.Equals(c.File, newFile, StringComparison.OrdinalIgnoreCase));
        if (found == null || string.Equals(found.File, selected, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return found.File;
    }

    /// <summary>First reason that blocks Run, in the order the user can fix them.</summary>
    public static InspectBlock BlockOf(bool hasSession, bool hostBusy, bool trainingActive, bool hasModelSelected, int imageCount)
    {
        if (!hasSession) return InspectBlock.NoSession;
        if (trainingActive) return InspectBlock.Training;
        if (hostBusy) return InspectBlock.Busy;
        if (!hasModelSelected) return InspectBlock.NoModel;
        if (imageCount <= 0) return InspectBlock.NoImage;
        return InspectBlock.None;
    }

    /// <summary>Resource key of the text explaining a block; null for <see cref="InspectBlock.None"/>.</summary>
    public static string? BlockKey(InspectBlock block)
    {
        switch (block)
        {
            case InspectBlock.NoSession: return "TrainBlockNoSession";
            case InspectBlock.Busy: return "TrainBlockBusy";
            case InspectBlock.Training: return "InspectBlockTraining";
            case InspectBlock.NoModel: return "InspectBlockNoModel";
            case InspectBlock.NoImage: return "InspectBlockNoImage";
            default: return null;
        }
    }

    /// <summary>
    /// True when a row passes the filter. <see cref="InspectFilter.Doubtful"/> looks at the uncertainty instead of the verdict: a row
    /// without an uncertainty (an older result, an error) is not shown there.
    /// </summary>
    public static bool MatchesReview(InspectFilter filter, InspectVerdict verdict, ImageUncertainty? uncertainty)
    {
        return filter == InspectFilter.Doubtful ? UncertaintyOrdering.IsDoubtful(uncertainty) : Matches(filter, verdict);
    }

    /// <summary>True when a row with <paramref name="verdict"/> passes the filter.</summary>
    public static bool Matches(InspectFilter filter, InspectVerdict verdict)
    {
        switch (filter)
        {
            case InspectFilter.Ok: return verdict == InspectVerdict.OK;
            case InspectFilter.Ng: return verdict == InspectVerdict.NG;
            case InspectFilter.NotRun: return verdict == InspectVerdict.NotRun;
            default: return true;
        }
    }

    /// <summary>Case-insensitive search in the full path; empty text matches everything.</summary>
    public static bool MatchesSearch(string fullPath, string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        return fullPath.IndexOf(text.Trim(), StringComparison.OrdinalIgnoreCase) >= 0;
    }

    /// <summary>
    /// Orders two rows for <paramref name="key"/>. Rows without a result sort after rows with one for every key but File, in both
    /// directions, so unfinished rows do not jump to the top when the direction flips.
    /// </summary>
    public static int Compare(InspectSortKey key, bool descending, string pathA, InspectResult? a, string pathB, InspectResult? b)
    {
        int byName = string.Compare(System.IO.Path.GetFileName(pathA), System.IO.Path.GetFileName(pathB), StringComparison.OrdinalIgnoreCase);
        if (byName == 0)
        {
            byName = string.Compare(pathA, pathB, StringComparison.OrdinalIgnoreCase);
        }

        if (key == InspectSortKey.File)
        {
            return descending ? -byName : byName;
        }

        bool hasA = a != null && a.Verdict != InspectVerdict.NotRun;
        bool hasB = b != null && b.Verdict != InspectVerdict.NotRun;
        if (!hasA || !hasB)
        {
            return hasA == hasB ? byName : hasA ? -1 : 1;
        }

        int value;
        switch (key)
        {
            case InspectSortKey.Verdict: value = ((int)a!.Verdict).CompareTo((int)b!.Verdict); break;
            case InspectSortKey.Regions: value = a!.Regions.CompareTo(b!.Regions); break;
            case InspectSortKey.Score: value = a!.Score.CompareTo(b!.Score); break;
            default: value = a!.ElapsedMs.CompareTo(b!.ElapsedMs); break;
        }

        if (value == 0)
        {
            return byName;
        }

        return descending ? -value : value;
    }

    /// <summary>Summary strip text parts: "run / OK / NG / mean ms" are formatted by the caller from this record.</summary>
    public static string MeanMsText(InspectSummary summary, CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(summary);
        return summary.MeanMs.ToString("0.0", culture);
    }

    /// <summary>Score cell: 3 decimals, or a dash for rows that have no result or failed.</summary>
    public static string ScoreText(InspectResult? result, CultureInfo culture)
    {
        return result == null || result.Verdict == InspectVerdict.NotRun || result.Verdict == InspectVerdict.Error
            ? "-"
            : result.Score.ToString("0.000", culture);
    }

    /// <summary>Milliseconds cell: one decimal, or a dash for rows without a successful result.</summary>
    public static string MsText(InspectResult? result, CultureInfo culture)
    {
        return result == null || result.Verdict == InspectVerdict.NotRun || result.Verdict == InspectVerdict.Error
            ? "-"
            : result.ElapsedMs.ToString("0.0", culture);
    }

    /// <summary>
    /// Confidence cell of an OK/NG model row: the confidence of the verdict as a percentage. Only rows whose uncertainty came from the
    /// classifier have one; segmentation rows and errors show a dash.
    /// </summary>
    public static string ConfidenceText(InspectResult? result, CultureInfo culture)
    {
        if (result == null
            || result.Uncertainty?.Signal != UncertaintySignal.ClassProbability
            || (result.Verdict != InspectVerdict.OK && result.Verdict != InspectVerdict.NG))
        {
            return "-";
        }

        return OkNgModelRules.ConfidenceOf(result.Score).ToString("P1", culture);
    }
}
