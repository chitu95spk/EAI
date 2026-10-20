using System.Globalization;
using System.Text;

namespace EasyLabel.Core.Io;

/// <summary>
/// A short plain-text "Learned from review" summary of <see cref="AgentOutcomeLog"/> records, given to the labelling agent so it repeats what the user
/// accepted and avoids what the user rejected. The text is deterministic (no timestamps, invariant culture, ordinal sorting) and coarse on purpose:
/// rates are rounded to 10 %, sizes to 5 % / 10 % buckets and review counts to a band (few / some / many), so one more record in the same buckets
/// leaves the text, and therefore the agent's cache key, unchanged.
/// </summary>
public static class AgentOutcomeDigest
{
    /// <summary>Fewest records for which a digest is built.</summary>
    public const int MinRecords = 5;

    /// <summary>Fewest records of one label that are worth a line.</summary>
    public const int MinLabelRecords = 3;

    /// <summary>Fewest equal rejected patterns that are worth a line.</summary>
    public const int MinPatternRecords = 2;

    /// <summary>Most rejected patterns listed.</summary>
    public const int MaxPatterns = 3;

    private const string Header = "Learned from review (the user's earlier accept/reject decisions on proposed shapes):";

    private const string RejectedHeader = "Most often rejected (label, shape, size):";

    private const string CrossHeader = "Other suggestion sources and edits after accepting:";

    /// <summary>Most over-proposal lines about the other sources.</summary>
    public const int MaxCrossLines = 3;

    /// <summary>The digest, or an empty string when there are fewer than <see cref="MinRecords"/> records or nothing useful in them. Never longer than <paramref name="maxChars"/>.</summary>
    public static string Build(IReadOnlyList<AgentOutcome> records, int maxChars = 1200)
    {
        if (records == null || records.Count < MinRecords || maxChars <= 0)
        {
            return string.Empty;
        }

        List<AgentOutcome> all = records.Where(r => r != null).ToList();
        if (all.Count < MinRecords)
        {
            return string.Empty;
        }

        // The agent's own accept/reject history drives the label and rejected-pattern lines; the other sources and the edits after acceptance
        // only add the short cross-source lines at the end.
        List<AgentOutcome> reviews = all.Where(r => !IsEdit(r)).ToList();
        List<AgentOutcome> agentReviews = reviews.Where(r => OutcomeSources.Normalize(r.Source) == OutcomeSources.Agent).ToList();
        List<string> crossLines = CrossSourceLines(reviews, all);

        List<string> labelLines = new List<string>();
        foreach (IGrouping<string, AgentOutcome> group in agentReviews.GroupBy(r => Name(r.Label)).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            List<AgentOutcome> items = group.ToList();
            if (items.Count < MinLabelRecords)
            {
                continue;
            }

            int accepted = items.Count(r => r.Accepted);
            StringBuilder line = new StringBuilder();
            line.Append("- ").Append(group.Key).Append(": accept rate ~")
                .Append(Round(100.0 * accepted / items.Count, 10).ToString(CultureInfo.InvariantCulture)).Append("% (")
                .Append(Band(items.Count)).Append(" reviews)");

            List<AgentOutcome> good = items.Where(r => r.Accepted).ToList();
            if (good.Count > 0)
            {
                line.Append("; accepted size width ").Append(Range(good.Select(r => r.WidthFraction)))
                    .Append(", height ").Append(Range(good.Select(r => r.HeightFraction)));
            }

            labelLines.Add(line.ToString());
        }

        if (labelLines.Count == 0 && crossLines.Count == 0)
        {
            return string.Empty;
        }

        List<string> lines = new List<string> { Header };
        lines.AddRange(labelLines);

        List<string> patterns = agentReviews
            .Where(r => !r.Accepted)
            .GroupBy(r => Name(r.Label) + ", " + Name(r.ShapeType) + ", width ~" + Percent(r.WidthFraction, 10) + "% height ~" + Percent(r.HeightFraction, 10) + "%")
            .Where(g => g.Count() >= MinPatternRecords)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .Take(MaxPatterns)
            .Select(g => "- " + g.Key)
            .ToList();
        if (patterns.Count > 0)
        {
            lines.Add(RejectedHeader);
            lines.AddRange(patterns);
        }

        if (crossLines.Count > 0)
        {
            lines.Add(CrossHeader);
            lines.AddRange(crossLines);
        }

        // Keep whole lines only, and never end on the heading of a list.
        int total = 0;
        int kept = 0;
        foreach (string line in lines)
        {
            int next = total + line.Length + (kept > 0 ? 1 : 0);
            if (next > maxChars)
            {
                break;
            }

            total = next;
            kept++;
        }

        if (kept > 0 && (lines[kept - 1] == RejectedHeader || lines[kept - 1] == CrossHeader))
        {
            kept--;
        }

        // A heading alone says nothing.
        return kept < 2 ? string.Empty : string.Join("\n", lines.Take(kept));
    }

    private static bool IsEdit(AgentOutcome record) => string.Equals(record.Action, OutcomeSources.EditAction, StringComparison.Ordinal);

    // Short lines about the other sources (what they over-propose) and about the edits the user made after accepting. Deterministic and coarse like
    // the rest of the digest: rates to 10 %, size changes to 5 %.
    private static List<string> CrossSourceLines(List<AgentOutcome> reviews, List<AgentOutcome> all)
    {
        List<string> lines = new List<string>();

        lines.AddRange(reviews
            .Where(r => OutcomeSources.Normalize(r.Source) != OutcomeSources.Agent)
            .GroupBy(r => OutcomeSources.Normalize(r.Source) + "\u001f" + Name(r.Label))
            .Where(g => g.Count() >= MinLabelRecords)
            .Select(g => new { g.Key, Source = OutcomeSources.Normalize(g.First().Source), Label = Name(g.First().Label), Rejected = Round(100.0 * g.Count(r => !r.Accepted) / g.Count(), 10) })
            .Where(g => g.Rejected >= 50)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Take(MaxCrossLines)
            .Select(g => "- " + g.Source + " over-proposes label " + g.Label + " (rejected " + g.Rejected.ToString(CultureInfo.InvariantCulture) + "%)"));

        List<double> ratios = all.Where(r => IsEdit(r) && r.AreaRatio.HasValue).Select(r => (double)r.AreaRatio!.Value).OrderBy(v => v).ToList();
        if (ratios.Count >= MinLabelRecords)
        {
            double change = 100.0 * (1.0 - Rank(ratios, 0.5));
            int step = Round(Math.Abs(change), 5);
            if (step >= 5)
            {
                lines.Add("- Accepted shapes usually " + (change > 0 ? "shrink" : "grow") + " by ~" + step.ToString(CultureInfo.InvariantCulture) + "% in area when the user edits them");
            }
        }

        lines.AddRange(all
            .Where(r => IsEdit(r) && !string.IsNullOrWhiteSpace(r.RelabelTo))
            .GroupBy(r => Name(r.Label) + " -> " + Name(r.RelabelTo))
            .Where(g => g.Count() >= MinPatternRecords)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .Take(2)
            .Select(g => "- Often relabelled after accepting: " + g.Key));

        return lines;
    }

    private static string Name(string? value)
    {
        string clean = string.Join(" ", (value ?? string.Empty).Split(new[] { '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries)).Trim();
        return clean.Length == 0 ? "(none)" : clean;
    }

    private static string Band(int count) => count < 10 ? "few" : count < 30 ? "some" : "many";

    private static int Round(double value, int step) => (int)Math.Floor(value / step + 0.5) * step;

    private static string Percent(double fraction, int step)
    {
        double value = double.IsNaN(fraction) ? 0 : Math.Min(1, Math.Max(0, fraction));
        return Round(100.0 * value, step).ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>"p10-p90%" of the fractions (nearest rank), each end rounded to a 5 % bucket.</summary>
    private static string Range(IEnumerable<double> fractions)
    {
        List<double> sorted = fractions.OrderBy(v => v).ToList();
        string low = Percent(Rank(sorted, 0.10), 5);
        string high = Percent(Rank(sorted, 0.90), 5);
        return low == high ? "~" + low + "%" : low + "-" + high + "%";
    }

    private static double Rank(List<double> sorted, double p)
    {
        int index = (int)Math.Ceiling(p * sorted.Count) - 1;
        return sorted[Math.Min(sorted.Count - 1, Math.Max(0, index))];
    }
}
