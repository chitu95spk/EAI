using System.Globalization;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace EasyLabel.Core.Io;

/// <summary>
/// One line of the agent chat history (what the user asked and what a run did). The history is for the user only: it is never read into an agent
/// prompt, so every run stays stateless. <see cref="Role"/> is <c>user</c>, <c>agent</c> or <c>system</c>; <see cref="Mode"/> is <c>all</c>,
/// <c>current</c> or <c>remaining</c>; <see cref="Effort"/> is the value of the settings (<c>economy</c>, <c>normal</c>, <c>best</c>);
/// <see cref="Outcome"/> is <c>ok</c>, <c>error</c>, <c>cancelled</c> or <c>cached</c> (empty on a user entry); <see cref="Error"/> is a short error text.
/// <see cref="Cost"/> is in USD and null when unknown, <see cref="Turns"/> null when unknown.
/// </summary>
public sealed record AgentChatEntry(
    DateTime Time,
    string Role,
    string Mode,
    string Effort,
    string Prompt,
    IReadOnlyList<string> Images,
    int Proposed,
    int Accepted,
    int Rejected,
    decimal? Cost,
    int? Turns,
    string Outcome,
    string Error);

/// <summary>
/// The chat history of the labelling agent: <c>.easyai/agent-chat.jsonl</c>, one JSON object per line. Only the newest <see cref="MaxEntries"/>
/// entries are kept (older ones are dropped on write, the file is replaced atomically through a temporary file). Writing never throws: a failing
/// history must not disturb a run. Reading skips corrupt or partial lines and never throws for bad content.
/// </summary>
public static class AgentChatLog
{
    /// <summary>Most entries kept in the file.</summary>
    public const int MaxEntries = 500;

    /// <summary>Entries the chat window shows by default.</summary>
    public const int DefaultReadCount = 100;

    /// <summary>Longest stored error text.</summary>
    public const int MaxErrorLength = 300;

    /// <summary>Longest stored prompt.</summary>
    public const int MaxPromptLength = 8000;

    /// <summary>Most image paths stored on one entry.</summary>
    public const int MaxImages = 50;

    private const string TimeFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";

    private static readonly object Gate = new object();

    /// <summary>
    /// Appends one entry and drops the oldest entries past <see cref="MaxEntries"/>. Does nothing for a null entry or an empty path; every
    /// I/O error is swallowed (the entry is then lost).
    /// </summary>
    public static void Append(string? path, AgentChatEntry? entry)
    {
        if (string.IsNullOrWhiteSpace(path) || entry == null)
        {
            return;
        }

        try
        {
            string added = Serialize(entry);
            lock (Gate)
            {
                List<string> lines = ReadValidLines(path);
                lines.Add(added);
                int skip = Math.Max(0, lines.Count - MaxEntries);

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
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.Security.SecurityException || ex is NotSupportedException || ex is ArgumentException)
        {
        }
    }

    /// <summary>
    /// Empties the history: the file is replaced by an empty one through a temporary file (under the same lock as <see cref="Append"/>, so a
    /// concurrent append lands either before or after, never in between). Does nothing for an empty path or a missing file; every I/O error is swallowed.
    /// </summary>
    public static void Clear(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            lock (Gate)
            {
                if (!File.Exists(path))
                {
                    return;
                }

                string temp = path + ".tmp";
                File.WriteAllText(temp, string.Empty, new UTF8Encoding(false));
                MoveWithRetry(temp, path);
            }
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.Security.SecurityException || ex is NotSupportedException || ex is ArgumentException)
        {
        }
    }

    /// <summary>The newest <paramref name="count"/> valid entries, oldest first (so the last element is the newest). Missing or unreadable file: empty.</summary>
    public static IReadOnlyList<AgentChatEntry> ReadLast(string? path, int count = DefaultReadCount)
    {
        List<AgentChatEntry> result = new List<AgentChatEntry>();
        if (string.IsNullOrWhiteSpace(path) || count <= 0)
        {
            return result;
        }

        try
        {
            lock (Gate)
            {
                foreach (string line in ReadRawLines(path))
                {
                    AgentChatEntry? entry = TryParse(line);
                    if (entry != null)
                    {
                        result.Add(entry);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is NotSupportedException || ex is ArgumentException)
        {
            return new List<AgentChatEntry>();
        }

        if (result.Count > count)
        {
            result.RemoveRange(0, result.Count - count);
        }

        return result;
    }

    // A virus scanner or the search indexer can hold the freshly written file for a few milliseconds on Windows; retry briefly before giving up.
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

    /// <summary>The error text as stored: white space collapsed to single spaces, at most <see cref="MaxErrorLength"/> characters.</summary>
    public static string LimitError(string? text)
    {
        string collapsed = string.Join(" ", (text ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return collapsed.Length <= MaxErrorLength ? collapsed : collapsed.Substring(0, MaxErrorLength);
    }

    private static string Serialize(AgentChatEntry e)
    {
        DateTime utc = e.Time.Kind == DateTimeKind.Local ? e.Time.ToUniversalTime() : DateTime.SpecifyKind(e.Time, DateTimeKind.Utc);
        string prompt = e.Prompt ?? string.Empty;
        JArray images = new JArray();
        if (e.Images != null)
        {
            foreach (string image in e.Images.Where(i => !string.IsNullOrEmpty(i)).Take(MaxImages))
            {
                images.Add(image);
            }
        }

        JObject json = new JObject
        {
            ["time"] = utc.ToString(TimeFormat, CultureInfo.InvariantCulture),
            ["role"] = Token(e.Role),
            ["mode"] = Token(e.Mode),
            ["effort"] = Token(e.Effort),
            ["prompt"] = prompt.Length <= MaxPromptLength ? prompt : prompt.Substring(0, MaxPromptLength),
            ["images"] = images,
            ["proposed"] = Math.Max(0, e.Proposed),
            ["accepted"] = Math.Max(0, e.Accepted),
            ["rejected"] = Math.Max(0, e.Rejected),
            ["cost"] = e.Cost.HasValue && e.Cost.Value >= 0 ? new JValue(e.Cost.Value) : JValue.CreateNull(),
            ["turns"] = e.Turns.HasValue && e.Turns.Value >= 0 ? new JValue(e.Turns.Value) : JValue.CreateNull(),
            ["outcome"] = Token(e.Outcome),
            ["error"] = LimitError(e.Error),
        };
        return json.ToString(Formatting.None);
    }

    private static string Token(string? value) => (value ?? string.Empty).Trim().ToLowerInvariant();

    private static AgentChatEntry? TryParse(string line)
    {
        try
        {
            using JsonTextReader reader = new JsonTextReader(new StringReader(line)) { DateParseHandling = DateParseHandling.None };
            JObject json = JObject.Load(reader);

            string? time = Str(json, "time");
            string? role = Str(json, "role");
            if (time == null || role == null
                || !DateTime.TryParseExact(time, TimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime parsed))
            {
                return null;
            }

            List<string> images = new List<string>();
            if (json["images"] is JArray array)
            {
                foreach (JToken item in array)
                {
                    if (item.Type == JTokenType.String)
                    {
                        images.Add(item.Value<string>() ?? string.Empty);
                    }
                }
            }

            return new AgentChatEntry(
                DateTime.SpecifyKind(parsed, DateTimeKind.Utc),
                role,
                Str(json, "mode") ?? string.Empty,
                Str(json, "effort") ?? string.Empty,
                Str(json, "prompt") ?? string.Empty,
                images,
                Count(json, "proposed"),
                Count(json, "accepted"),
                Count(json, "rejected"),
                Money(json, "cost"),
                OptionalCount(json, "turns"),
                Str(json, "outcome") ?? string.Empty,
                LimitError(Str(json, "error")));
        }
        catch (Exception ex) when (ex is JsonException || ex is InvalidCastException || ex is FormatException || ex is ArgumentException || ex is OverflowException)
        {
            return null;
        }
    }

    private static string? Str(JObject json, string name)
    {
        JToken? token = json[name];
        return token != null && token.Type == JTokenType.String ? token.Value<string>() : null;
    }

    private static int Count(JObject json, string name) => OptionalCount(json, name) ?? 0;

    private static int? OptionalCount(JObject json, string name)
    {
        JToken? token = json[name];
        if (token != null && token.Type == JTokenType.Integer)
        {
            long value = token.Value<long>();
            return (int)Math.Max(0, Math.Min(int.MaxValue, value));
        }

        return null;
    }

    private static decimal? Money(JObject json, string name)
    {
        JToken? token = json[name];
        if (token != null && (token.Type == JTokenType.Float || token.Type == JTokenType.Integer))
        {
            decimal value = token.Value<decimal>();
            return value >= 0 ? value : null;
        }

        return null;
    }
}
