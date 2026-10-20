using System.Globalization;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace EasyLabel.Core.Io;

/// <summary>
/// One decision of the user about a shape the labelling agent proposed. <see cref="Action"/> is <c>add</c> today (later <c>replace</c> / <c>remove</c>);
/// <see cref="WidthFraction"/> and <see cref="HeightFraction"/> are the size of the shape's bounding box as a fraction (0..1) of the image.
/// The optional fields are absent in old files: <see cref="Source"/> is then <c>Agent</c>. <see cref="Action"/> <c>edit</c> is an update record written
/// after an accepted shape was changed by the user: <see cref="EditIou"/> is the box overlap of the final shape with the accepted one,
/// <see cref="RelabelTo"/> the new label when it changed, <see cref="AreaRatio"/> the final box area over the accepted box area.
/// </summary>
public sealed record AgentOutcome(
    DateTime Time,
    string ImageRel,
    string RunId,
    string Label,
    string ShapeType,
    string Action,
    bool Accepted,
    double WidthFraction,
    double HeightFraction,
    string Source = OutcomeSources.Agent,
    float? EditIou = null,
    string? RelabelTo = null,
    float? AreaRatio = null);

/// <summary>Where a reviewed suggestion came from (the <see cref="AgentOutcome.Source"/> text).</summary>
public static class OutcomeSources
{
    public const string Agent = "Agent";
    public const string SegAi = "SegAI";
    public const string Sam2 = "SAM2";
    public const string Yolo = "YOLO";
    public const string CustomYolo = "CustomYolo";
    public const string Other = "Other";

    /// <summary>The action text of an update record written after the user edited an accepted shape.</summary>
    public const string EditAction = "edit";

    /// <summary>The known source text for <paramref name="value"/> (case-insensitive); empty = <c>Agent</c> (old files), anything unknown = <c>Other</c>.</summary>
    public static string Normalize(string? value)
    {
        string text = (value ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            return Agent;
        }

        foreach (string known in new[] { Agent, SegAi, Sam2, Yolo, CustomYolo })
        {
            if (string.Equals(text, known, StringComparison.OrdinalIgnoreCase))
            {
                return known;
            }
        }

        return Other;
    }
}

/// <summary>
/// The review history of the labelling agent: <c>.easyai/agent-outcomes.jsonl</c>, one JSON object per line. The file stays small: when it grows past
/// <see cref="MaxRecords"/> records or <see cref="MaxBytes"/> bytes the oldest records are dropped on write. Writes go through a temporary file and are
/// serialised by a lock, because a review can be accepted on the UI thread while an agent batch is still running. Reading skips lines that are
/// corrupt or partial and never throws for bad content.
/// </summary>
public static class AgentOutcomeLog
{
    /// <summary>Most records kept in the file.</summary>
    public const int MaxRecords = 2000;

    /// <summary>Largest size of the file in bytes (UTF-8, one '\n' per record).</summary>
    public const int MaxBytes = 512 * 1024;

    private const string TimeFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";

    private static readonly object Gate = new object();

    /// <summary>Appends one record (see <see cref="AppendMany"/>).</summary>
    public static void Append(string path, AgentOutcome outcome)
    {
        if (outcome == null) throw new ArgumentNullException(nameof(outcome));
        AppendMany(path, new[] { outcome });
    }

    /// <summary>
    /// Appends the records in order, drops the oldest records past the caps, and rewrites the file atomically. Throws <see cref="IOException"/> /
    /// <see cref="UnauthorizedAccessException"/> when the file cannot be written; an empty list does nothing.
    /// </summary>
    public static void AppendMany(string path, IEnumerable<AgentOutcome> outcomes)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("The path is required.", nameof(path));
        if (outcomes == null) throw new ArgumentNullException(nameof(outcomes));

        List<string> added = outcomes.Where(o => o != null).Select(Serialize).ToList();
        if (added.Count == 0)
        {
            return;
        }

        lock (Gate)
        {
            List<string> lines = ReadValidLines(path);
            lines.AddRange(added);

            long bytes = 0;
            foreach (string line in lines)
            {
                bytes += Encoding.UTF8.GetByteCount(line) + 1;
            }

            int skip = 0;
            while (lines.Count - skip > 1 && (lines.Count - skip > MaxRecords || bytes > MaxBytes))
            {
                bytes -= Encoding.UTF8.GetByteCount(lines[skip]) + 1;
                skip++;
            }

            string? folder = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(folder))
            {
                Directory.CreateDirectory(folder);
            }

            StringBuilder text = new StringBuilder();
            for (int i = skip; i < lines.Count; i++)
            {
                text.Append(lines[i]).Append('\n');
            }

            string temp = path + ".tmp";
            File.WriteAllText(temp, text.ToString(), new UTF8Encoding(false));
            MoveWithRetry(temp, path);
        }
    }

    // A virus scanner or the search indexer can hold the freshly written file for a few milliseconds on Windows; the replace then fails with
    // "access denied" although nothing in this process holds it. Retry briefly before giving up.
    private static void MoveWithRetry(string temp, string path)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                File.Move(temp, path, true);
                return;
            }
            catch (Exception ex) when (attempt < 6 && (ex is IOException || ex is UnauthorizedAccessException))
            {
                Thread.Sleep(15 * (attempt + 1));
            }
        }
    }

    /// <summary>All valid records, oldest first. A missing or unreadable file gives an empty list; corrupt lines are skipped.</summary>
    public static IReadOnlyList<AgentOutcome> ReadAll(string? path)
    {
        List<AgentOutcome> result = new List<AgentOutcome>();
        if (string.IsNullOrWhiteSpace(path))
        {
            return result;
        }

        lock (Gate)
        {
            foreach (string line in ReadRawLines(path))
            {
                AgentOutcome? outcome = TryParse(line);
                if (outcome != null)
                {
                    result.Add(outcome);
                }
            }
        }

        return result;
    }

    public static void Clear(string path)
    {
        lock (Gate)
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private static List<string> ReadValidLines(string path)
    {
        List<string> lines = new List<string>();
        foreach (string line in ReadRawLines(path))
        {
            if (TryParse(line) != null)
            {
                lines.Add(line.Trim());
            }
        }

        return lines;
    }

    private static IEnumerable<string> ReadRawLines(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return Array.Empty<string>();
            }

            return File.ReadAllText(path).Replace("\r\n", "\n").Split('\n').Where(l => l.Trim().Length > 0).ToList();
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }

    private static string Serialize(AgentOutcome o)
    {
        DateTime utc = o.Time.Kind == DateTimeKind.Local ? o.Time.ToUniversalTime() : DateTime.SpecifyKind(o.Time, DateTimeKind.Utc);
        JObject json = new JObject
        {
            ["time"] = utc.ToString(TimeFormat, CultureInfo.InvariantCulture),
            ["imageRel"] = Clean(o.ImageRel),
            ["runId"] = Clean(o.RunId),
            ["label"] = Clean(o.Label),
            ["shapeType"] = Clean(o.ShapeType),
            ["action"] = Clean(o.Action),
            ["accepted"] = o.Accepted,
            ["widthFraction"] = Fraction(o.WidthFraction),
            ["heightFraction"] = Fraction(o.HeightFraction),
        };

        // The optional fields are written only when they carry something, so an agent line is byte-identical to the format before they existed.
        string source = OutcomeSources.Normalize(o.Source);
        if (source != OutcomeSources.Agent)
        {
            json["source"] = source;
        }

        if (o.EditIou.HasValue && !float.IsNaN(o.EditIou.Value) && !float.IsInfinity(o.EditIou.Value))
        {
            json["editIou"] = Math.Round(Math.Min(1.0, Math.Max(0.0, o.EditIou.Value)), 4);
        }

        if (!string.IsNullOrWhiteSpace(o.RelabelTo))
        {
            json["relabelTo"] = Clean(o.RelabelTo);
        }

        if (o.AreaRatio.HasValue && !float.IsNaN(o.AreaRatio.Value) && !float.IsInfinity(o.AreaRatio.Value))
        {
            json["areaRatio"] = Math.Round(Math.Min(10.0, Math.Max(0.0, o.AreaRatio.Value)), 4);
        }

        return json.ToString(Formatting.None);
    }

    private static string Clean(string? value) => value ?? string.Empty;

    private static double Fraction(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return 0;
        }

        return Math.Round(Math.Min(1, Math.Max(0, value)), 6);
    }

    private static AgentOutcome? TryParse(string line)
    {
        try
        {
            using JsonTextReader reader = new JsonTextReader(new StringReader(line)) { DateParseHandling = DateParseHandling.None };
            JObject json = JObject.Load(reader);

            string? time = Str(json, "time");
            string? label = Str(json, "label");
            if (time == null || label == null
                || !DateTime.TryParseExact(time, TimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime parsed)
                || json["accepted"]?.Type != JTokenType.Boolean)
            {
                return null;
            }

            double width = Num(json, "widthFraction");
            double height = Num(json, "heightFraction");
            if (double.IsNaN(width) || double.IsNaN(height))
            {
                return null;
            }

            return new AgentOutcome(
                DateTime.SpecifyKind(parsed, DateTimeKind.Utc),
                Str(json, "imageRel") ?? string.Empty,
                Str(json, "runId") ?? string.Empty,
                label,
                Str(json, "shapeType") ?? string.Empty,
                Str(json, "action") ?? string.Empty,
                json["accepted"]!.Value<bool>(),
                Fraction(width),
                Fraction(height),
                OutcomeSources.Normalize(Str(json, "source")),
                OptionalFloat(json, "editIou"),
                Str(json, "relabelTo"),
                OptionalFloat(json, "areaRatio"));
        }
        catch (Exception ex) when (ex is JsonException || ex is InvalidCastException || ex is FormatException || ex is ArgumentException)
        {
            return null;
        }
    }

    private static string? Str(JObject json, string name)
    {
        JToken? token = json[name];
        return token != null && token.Type == JTokenType.String ? token.Value<string>() : null;
    }

    private static float? OptionalFloat(JObject json, string name)
    {
        double value = Num(json, name);
        return double.IsNaN(value) || double.IsInfinity(value) ? (float?)null : (float)value;
    }

    private static double Num(JObject json, string name)
    {
        JToken? token = json[name];
        return token != null && (token.Type == JTokenType.Float || token.Type == JTokenType.Integer) ? token.Value<double>() : double.NaN;
    }
}
