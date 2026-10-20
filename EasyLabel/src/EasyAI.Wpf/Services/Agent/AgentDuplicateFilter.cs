using EasyLabel.Core.Editing;
using EasyLabel.SegAI.AutoLabel;

namespace EasyAI.Desktop.Services.Agent;

/// <summary>
/// Drops the suggestions of an agent run that only repeat a part which is already labelled (a shape of the document) or already suggested
/// and not reviewed yet (a pending suggestion). Pure: no UI, no file. The rule is <see cref="ShapeOverlap.IsDuplicate"/>; the MCP server
/// applies the same rule while the agent works, this is the safety net for a result that was cached or that came from an older run.
/// A review suggestion (replace or remove) is never dropped.
/// </summary>
public static class AgentDuplicateFilter
{
    /// <summary>
    /// <paramref name="made"/> without the plain new shapes that duplicate a shape of <paramref name="existing"/> or a suggestion of
    /// <paramref name="pending"/>; <paramref name="dropped"/> is how many were removed. An existing shape that a review suggestion in
    /// <paramref name="made"/> replaces or removes is not compared. The order of the kept suggestions is unchanged.
    /// </summary>
    public static IReadOnlyList<SegAiSuggestion> Filter(
        IReadOnlyList<SegAiSuggestion> made,
        IReadOnlyList<AgentShapeInfo>? existing,
        IReadOnlyList<SegAiSuggestion>? pending,
        IReadOnlyList<string>? classNames,
        out int dropped)
    {
        dropped = 0;
        if (made.Count == 0)
        {
            return made;
        }

        var reviewed = new HashSet<int>();
        foreach (SegAiSuggestion suggestion in made)
        {
            if (suggestion.Action != null && suggestion.TargetIndex.HasValue)
            {
                reviewed.Add(suggestion.TargetIndex.Value);
            }
        }

        var others = new List<(string Label, List<double[]> Ring)>();
        if (existing != null)
        {
            for (int i = 0; i < existing.Count; i++)
            {
                if (reviewed.Contains(i))
                {
                    continue;
                }

                List<double[]> ring = ShapeOverlap.ToRing(existing[i].ShapeType, existing[i].Points);
                if (ShapeOverlap.Area(ring) > 0)
                {
                    others.Add((existing[i].Label ?? string.Empty, ring));
                }
            }
        }

        if (pending != null)
        {
            foreach (SegAiSuggestion other in pending)
            {
                List<double[]>? ring = RingOf(other);
                if (ring != null)
                {
                    others.Add((LabelOf(other, classNames), ring));
                }
            }
        }

        if (others.Count == 0)
        {
            return made;
        }

        var kept = new List<SegAiSuggestion>(made.Count);
        foreach (SegAiSuggestion suggestion in made)
        {
            List<double[]>? ring = suggestion.Action == null ? RingOf(suggestion) : null;
            if (ring != null && IsDuplicateOfAny(LabelOf(suggestion, classNames), ring, others))
            {
                dropped++;
                continue;
            }

            kept.Add(suggestion);
        }

        return dropped == 0 ? made : kept;
    }

    /// <summary>
    /// The suggestions that carry an outline as shape infos (label, shape type, points), the form request.json uses for pending shapes.
    /// A removal (no outline) is left out.
    /// </summary>
    public static List<AgentShapeInfo> ToShapeInfos(IEnumerable<SegAiSuggestion> suggestions, IReadOnlyList<string>? classNames)
    {
        var infos = new List<AgentShapeInfo>();
        foreach (SegAiSuggestion suggestion in suggestions)
        {
            if (RingOf(suggestion) == null)
            {
                continue;
            }

            List<double[]> points = suggestion.Outer.Select(p => new[] { (double)p.X, (double)p.Y }).ToList();
            string shapeType = string.IsNullOrWhiteSpace(suggestion.ShapeType) ? "polygon" : suggestion.ShapeType;
            infos.Add(new AgentShapeInfo(LabelOf(suggestion, classNames), shapeType, points));
        }

        return infos;
    }

    private static bool IsDuplicateOfAny(string label, List<double[]> ring, List<(string Label, List<double[]> Ring)> others)
    {
        foreach ((string otherLabel, List<double[]> otherRing) in others)
        {
            if (ShapeOverlap.IsDuplicate(label, ring, otherLabel, otherRing, out _))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The outline of a suggestion (holes ignored), or null when it has none (a removal) or no area.</summary>
    private static List<double[]>? RingOf(SegAiSuggestion suggestion)
    {
        if (suggestion.Outer == null || suggestion.Outer.Length < 3 || suggestion.Action == SegAiSuggestion.ActionRemove)
        {
            return null;
        }

        List<double[]> ring = ShapeOverlap.ToRing(suggestion.ShapeType, suggestion.Outer.Select(p => new[] { (double)p.X, (double)p.Y }).ToList());
        return ShapeOverlap.Area(ring) > 0 ? ring : null;
    }

    private static string LabelOf(SegAiSuggestion suggestion, IReadOnlyList<string>? classNames)
    {
        if (!string.IsNullOrWhiteSpace(suggestion.Label))
        {
            return suggestion.Label!.Trim();
        }

        return classNames != null && suggestion.ClassIndex >= 0 && suggestion.ClassIndex < classNames.Count
            ? (classNames[suggestion.ClassIndex] ?? string.Empty).Trim()
            : string.Empty;
    }
}
