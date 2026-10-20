using System.Globalization;
using System.Text;

namespace EasyAI.Desktop.Services.Agent;

/// <summary>One usable line of <c>log.csv</c>.</summary>
public sealed class AgentSpendRow
{
    public AgentSpendRow(DateTime utc, string model, double costUsd, string outcome)
    {
        Utc = utc;
        Model = model;
        CostUsd = costUsd;
        Outcome = outcome;
    }

    /// <summary>When the run ended (UTC).</summary>
    public DateTime Utc { get; }

    public string Model { get; }

    public double CostUsd { get; }

    /// <summary>The outcome column: <c>cached</c>, <c>Success</c>, <c>Failed</c> and so on.</summary>
    public string Outcome { get; }

    /// <summary>A cached answer cost nothing.</summary>
    public bool IsCached => string.Equals(Outcome, "cached", StringComparison.OrdinalIgnoreCase);
}

/// <summary>What a batch of N images is expected to cost.</summary>
public readonly struct AgentSpendEstimate
{
    public AgentSpendEstimate(bool isKnown, double usd, int images)
    {
        IsKnown = isKnown;
        Usd = usd;
        Images = images;
    }

    /// <summary>False = there is no history for the model (Unknown).</summary>
    public bool IsKnown { get; }

    /// <summary>Always true when known: the number is median x images, not a promise.</summary>
    public bool IsEstimate => IsKnown;

    public double Usd { get; }

    public int Images { get; }

    public static AgentSpendEstimate Unknown => new AgentSpendEstimate(false, 0, 0);
}

/// <summary>
/// Reads the agent usage log and answers how much was spent today and what a batch is expected to cost. Pure: the clock and the
/// UTC-to-local conversion are injected. "Today" is the local calendar day of the PC, computed from the UTC column.
/// </summary>
public sealed class AgentSpendTracker
{
    private readonly IReadOnlyList<AgentSpendRow> _rows;
    private readonly Func<DateTime> _utcNow;
    private readonly Func<DateTime, DateTime> _toLocal;

    /// <param name="rows">The usable log lines.</param>
    /// <param name="utcNow">The clock (UTC); null = the system clock.</param>
    /// <param name="toLocal">UTC to local time; null = the time zone of the PC.</param>
    public AgentSpendTracker(IReadOnlyList<AgentSpendRow>? rows, Func<DateTime>? utcNow = null, Func<DateTime, DateTime>? toLocal = null)
    {
        _rows = rows ?? Array.Empty<AgentSpendRow>();
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _toLocal = toLocal ?? (utc => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), TimeZoneInfo.Local));
    }

    /// <summary>A tracker over the log file (missing or unreadable = no history).</summary>
    public static AgentSpendTracker FromFile(string logPath, Func<DateTime>? utcNow = null, Func<DateTime, DateTime>? toLocal = null)
    {
        return new AgentSpendTracker(ReadFile(logPath), utcNow, toLocal);
    }

    /// <summary>The usable lines of the log file; a missing or locked file gives an empty list.</summary>
    public static IReadOnlyList<AgentSpendRow> ReadFile(string logPath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(logPath) || !File.Exists(logPath))
            {
                return Array.Empty<AgentSpendRow>();
            }

            using FileStream stream = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using StreamReader reader = new StreamReader(stream, new UTF8Encoding(false));
            return Parse(reader.ReadToEnd());
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            return Array.Empty<AgentSpendRow>();
        }
    }

    /// <summary>Parses the log text. The header, blank lines and damaged lines are skipped; a damaged line never throws.</summary>
    public static IReadOnlyList<AgentSpendRow> Parse(string? text)
    {
        List<AgentSpendRow> rows = new List<AgentSpendRow>();
        if (string.IsNullOrEmpty(text))
        {
            return rows;
        }

        foreach (string line in text.Split('\n'))
        {
            string trimmed = line.TrimEnd('\r');
            if (trimmed.Length == 0)
            {
                continue;
            }

            List<string> fields = SplitCsv(trimmed);
            if (fields.Count < 12)
            {
                continue;
            }

            if (!DateTime.TryParseExact(
                    fields[0].Trim(),
                    "yyyy-MM-dd'T'HH:mm:ss'Z'",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out DateTime utc))
            {
                continue;
            }

            if (!double.TryParse(fields[4].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double cost)
                || double.IsNaN(cost) || double.IsInfinity(cost) || cost < 0)
            {
                continue;
            }

            rows.Add(new AgentSpendRow(utc, fields[2].Trim(), cost, fields[11].Trim()));
        }

        return rows;
    }

    /// <summary>Splits one CSV line; a quoted field may hold commas and doubled quotes.</summary>
    public static List<string> SplitCsv(string line)
    {
        List<string> fields = new List<string>();
        StringBuilder current = new StringBuilder();
        bool quoted = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (quoted)
            {
                if (c == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"')
                    {
                        current.Append('"');
                        i++;
                    }
                    else
                    {
                        quoted = false;
                    }
                }
                else
                {
                    current.Append(c);
                }
            }
            else if (c == '"')
            {
                quoted = true;
            }
            else if (c == ',')
            {
                fields.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }

        fields.Add(current.ToString());
        return fields;
    }

    /// <summary>The local calendar day of <paramref name="utc"/>.</summary>
    public DateTime LocalDay(DateTime utc)
    {
        return _toLocal(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).Date;
    }

    private bool IsToday(AgentSpendRow row)
    {
        return LocalDay(row.Utc) == LocalDay(_utcNow());
    }

    /// <summary>What the agent cost today (local day); cached answers count 0.</summary>
    public double TodayTotalUsd()
    {
        double total = 0;
        foreach (AgentSpendRow row in _rows)
        {
            if (!row.IsCached && IsToday(row))
            {
                total += row.CostUsd;
            }
        }

        return total;
    }

    /// <summary>How many log lines belong to today (local day), cached ones included.</summary>
    public int TodayRunCount()
    {
        int count = 0;
        foreach (AgentSpendRow row in _rows)
        {
            if (IsToday(row))
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>
    /// The log names the model but not the effort, so history is matched by model only. A logged name matches when it equals the wanted one
    /// or contains it ("claude-sonnet-4" matches "sonnet"); the comparison ignores case. An empty wanted model matches nothing.
    /// </summary>
    public static bool ModelMatches(string? logged, string? wanted)
    {
        string a = (logged ?? string.Empty).Trim();
        string b = (wanted ?? string.Empty).Trim();
        if (a.Length == 0 || b.Length == 0)
        {
            return false;
        }

        return a.IndexOf(b, StringComparison.OrdinalIgnoreCase) >= 0 || b.IndexOf(a, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    /// <summary>The paid runs of the model: not cached, not free, finished with a result.</summary>
    private IEnumerable<AgentSpendRow> PaidRuns(string model)
    {
        foreach (AgentSpendRow row in _rows)
        {
            if (!row.IsCached && row.CostUsd > 0 && ModelMatches(row.Model, model))
            {
                yield return row;
            }
        }
    }

    /// <summary>How many paid runs of the model are in the log.</summary>
    public int RunCount(string model)
    {
        return PaidRuns(model).Count();
    }

    /// <summary>The median cost of one paid run of the model, or null when the log has none.</summary>
    public double? MedianCostUsd(string model)
    {
        List<double> costs = PaidRuns(model).Select(r => r.CostUsd).ToList();
        if (costs.Count == 0)
        {
            return null;
        }

        costs.Sort();
        int mid = costs.Count / 2;
        return costs.Count % 2 == 1 ? costs[mid] : (costs[mid - 1] + costs[mid]) / 2.0;
    }

    /// <summary>
    /// images x median cost of the model. Without a logged run of the model (Codex runs are never logged with a cost) the price table gives
    /// images x the nominal cost of one run; Unknown when neither exists or there is no image.
    /// </summary>
    public AgentSpendEstimate Estimate(string model, int images, AgentPriceTable? prices = null)
    {
        if (images <= 0)
        {
            return AgentSpendEstimate.Unknown;
        }

        double? median = MedianCostUsd(model) ?? prices?.NominalCostPerImage(model);
        if (!median.HasValue)
        {
            return AgentSpendEstimate.Unknown;
        }

        return new AgentSpendEstimate(true, median.Value * images, images);
    }

    /// <summary>True when a daily cap is on (above zero) and <paramref name="spentUsd"/> reached it.</summary>
    public static bool DayCapReached(double spentUsd, double dayCapUsd)
    {
        return !double.IsNaN(dayCapUsd) && dayCapUsd > 0 && spentUsd >= dayCapUsd;
    }
}
