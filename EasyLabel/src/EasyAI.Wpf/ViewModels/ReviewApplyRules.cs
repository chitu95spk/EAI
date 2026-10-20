using EasyLabel.Core.Io;
using EasyLabel.Core.Model;
using EasyLabel.SegAI.AutoLabel;

namespace EasyAI.Desktop.ViewModels;

/// <summary>A replace / remove suggestion that passed the target check: the suggestion and the document shape index it applies to.</summary>
public sealed record ReviewTarget(SegAiSuggestion Suggestion, int Index);

/// <summary>
/// The accept split of a batch of suggestions: plain adds, replacements and removals whose target is still the shape the agent looked at,
/// and the suggestions that were skipped (stale target, missing hash, no geometry, or a second action on one target).
/// </summary>
public sealed class ReviewPlan
{
    public List<SegAiSuggestion> Adds { get; } = new List<SegAiSuggestion>();

    public List<ReviewTarget> Replacements { get; } = new List<ReviewTarget>();

    public List<ReviewTarget> Removals { get; } = new List<ReviewTarget>();

    public List<SegAiSuggestion> Skipped { get; } = new List<SegAiSuggestion>();

    /// <summary>Suggestions the plan will apply.</summary>
    public int AppliedCount => Adds.Count + Replacements.Count + Removals.Count;
}

/// <summary>
/// UI-free rules for accepting an agent review: splits suggestions by action and re-checks every replace / remove target against the
/// current document (index in range and <see cref="ShapeFingerprint"/> equal to <see cref="SegAiSuggestion.TargetHash"/>) before anything is
/// applied. No WPF types, so it is unit tested from EasyLabel.SegAI.Tests (linked file).
/// </summary>
public static class ReviewApplyRules
{
    /// <summary>True for a replace or remove review suggestion.</summary>
    public static bool IsReview(SegAiSuggestion? suggestion)
    {
        return suggestion != null && (IsRemove(suggestion) || IsReplace(suggestion));
    }

    public static bool IsReplace(SegAiSuggestion suggestion)
    {
        return string.Equals(suggestion.Action, SegAiSuggestion.ActionReplace, StringComparison.Ordinal);
    }

    public static bool IsRemove(SegAiSuggestion suggestion)
    {
        return string.Equals(suggestion.Action, SegAiSuggestion.ActionRemove, StringComparison.Ordinal);
    }

    /// <summary>The fingerprint of a document shape in the form list_shapes reports it.</summary>
    public static string FingerprintOf(LabelShape shape)
    {
        return ShapeFingerprint.Compute(shape.Label, shape.ShapeType, shape.Points);
    }

    /// <summary>True when the suggestion's target is in range and still has the fingerprint the agent saw.</summary>
    public static bool TargetIsCurrent(SegAiSuggestion suggestion, IReadOnlyList<LabelShape> shapes)
    {
        int? index = suggestion.TargetIndex;
        if (!index.HasValue || index.Value < 0 || index.Value >= shapes.Count || string.IsNullOrEmpty(suggestion.TargetHash))
        {
            return false;
        }

        return string.Equals(FingerprintOf(shapes[index.Value]), suggestion.TargetHash, StringComparison.Ordinal);
    }

    /// <summary>
    /// Splits <paramref name="chosen"/> by action against <paramref name="shapes"/> (the document as it is now). A replace / remove with a
    /// stale target is skipped; so is a replace without an outline, an add without an outline, and any further action on a target that
    /// an earlier suggestion of the batch already claimed (one action per target). The rest is returned in input order.
    /// </summary>
    public static ReviewPlan Plan(IReadOnlyList<SegAiSuggestion> chosen, IReadOnlyList<LabelShape> shapes)
    {
        ReviewPlan plan = new ReviewPlan();
        HashSet<int> claimed = new HashSet<int>();
        foreach (SegAiSuggestion suggestion in chosen)
        {
            bool remove = IsRemove(suggestion);
            bool replace = IsReplace(suggestion);
            if (!remove && !replace)
            {
                if (suggestion.Outer.Length == 0)
                {
                    plan.Skipped.Add(suggestion);
                }
                else
                {
                    plan.Adds.Add(suggestion);
                }

                continue;
            }

            if ((replace && suggestion.Outer.Length == 0) || !TargetIsCurrent(suggestion, shapes) || !claimed.Add(suggestion.TargetIndex!.Value))
            {
                plan.Skipped.Add(suggestion);
                continue;
            }

            ReviewTarget target = new ReviewTarget(suggestion, suggestion.TargetIndex!.Value);
            if (remove)
            {
                plan.Removals.Add(target);
            }
            else
            {
                plan.Replacements.Add(target);
            }
        }

        return plan;
    }
}
