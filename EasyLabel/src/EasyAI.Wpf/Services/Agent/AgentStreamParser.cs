using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace EasyAI.Desktop.Services.Agent;

/// <summary>
/// Turns one line of <c>claude -p --output-format stream-json --verbose</c> into short readable <see cref="AgentStreamEvent"/>s. Pure: no I/O.
/// Shapes read: <c>{"type":"system","subtype":"init","model":...}</c>, <c>{"type":"assistant","message":{"content":[{"type":"text","text":...},
/// {"type":"tool_use","id":...,"name":...,"input":{...}}]}}</c>, <c>{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":...,
/// "content":"text" | [{"type":"text","text":...},{"type":"image","source":{"data":"base64..."}}]}]}}</c> and
/// <c>{"type":"result","is_error":false,"result":...,"total_cost_usd":...,"num_turns":...}</c>. Unknown and broken lines give no event.
/// Image data (<c>source.data</c>) is never read into an event or a kept line.
/// </summary>
public static class AgentStreamParser
{
    /// <summary>Longest text of one event.</summary>
    public const int MaxTextLength = 300;

    private const int MaxToolResultText = 160;
    private const int MaxArgumentText = 24;
    private const int MaxArgumentsText = 100;
    private const string ToolPrefix = "mcp__easyai__";

    private static readonly Regex ToolUseIdPattern = new Regex("\"tool_use_id\"\\s*:\\s*\"([^\"\\\\]{1,100})\"", RegexOptions.CultureInvariant);

    private static readonly IReadOnlyList<AgentStreamEvent> NoEvents = Array.Empty<AgentStreamEvent>();

    /// <summary>
    /// The events of one complete line. <paramref name="keep"/> is true when the line may matter for the final result (a result line, a line
    /// that is not a stream event, broken text) and must be kept; every stream event line is false so it can be dropped at once.
    /// </summary>
    public static IReadOnlyList<AgentStreamEvent> ParseLine(string? line, out bool keep)
    {
        string text = (line ?? string.Empty).Trim();
        keep = false;
        if (text.Length == 0)
        {
            return NoEvents;
        }

        if (text[0] != '{')
        {
            keep = true;
            return NoEvents;
        }

        JObject root;
        try
        {
            root = JObject.Parse(text);
        }
        catch (Newtonsoft.Json.JsonException)
        {
            keep = true;
            return NoEvents;
        }

        string? type = Str(root["type"]);
        switch (type)
        {
            case "result":
                keep = true;
                return new[] { ResultEvent(root) };
            case "system":
                return string.Equals(Str(root["subtype"]), "init", StringComparison.Ordinal)
                    ? new[] { new AgentStreamEvent(AgentStreamEventKind.Text, "Session started" + (Str(root["model"]) is string model && model.Length > 0 ? " (" + Limit(model, 60) + ")" : string.Empty)) }
                    : NoEvents;
            case "assistant":
                return AssistantEvents((root["message"] as JObject)?["content"]);
            case "user":
                return UserEvents((root["message"] as JObject)?["content"]);
            case null:
                // An object without a type is not a stream event (old single-JSON output); the final parser decides.
                keep = true;
                return NoEvents;
            default:
                return NoEvents;
        }
    }

    /// <summary>
    /// A line longer than <see cref="AgentStreamReader.MaxLineLength"/> was cut; only its start is known. A cut user line is a big tool result
    /// (an image), reported without any of its data; any other cut line gives no event.
    /// </summary>
    public static IReadOnlyList<AgentStreamEvent> ParseTruncated(string? prefix)
    {
        string head = (prefix ?? string.Empty).TrimStart();
        if (head.StartsWith("{\"type\":\"user\"", StringComparison.Ordinal) || head.StartsWith("{\"type\": \"user\"", StringComparison.Ordinal))
        {
            Match id = ToolUseIdPattern.Match(head);
            return new[] { new AgentStreamEvent(AgentStreamEventKind.ToolResult, "(large result, data dropped)") { ToolUseId = id.Success ? id.Groups[1].Value : string.Empty } };
        }

        return NoEvents;
    }

    /// <summary>The tool name without the <c>mcp__easyai__</c> prefix (other servers keep <c>server:tool</c>).</summary>
    public static string ShortToolName(string? name)
    {
        string value = name ?? string.Empty;
        if (value.StartsWith(ToolPrefix, StringComparison.Ordinal))
        {
            return value.Substring(ToolPrefix.Length);
        }

        if (value.StartsWith("mcp__", StringComparison.Ordinal))
        {
            string rest = value.Substring(5);
            int split = rest.IndexOf("__", StringComparison.Ordinal);
            return split > 0 ? rest.Substring(0, split) + ":" + rest.Substring(split + 2) : rest;
        }

        return value;
    }

    private static AgentStreamEvent ResultEvent(JObject root)
    {
        bool isError = root["is_error"] is JToken flag && flag.Type == JTokenType.Boolean && flag.Value<bool>();
        string text = root["result"]?.Type == JTokenType.String ? root.Value<string>("result") ?? string.Empty : string.Empty;
        double cost = root["total_cost_usd"] is JToken c && (c.Type == JTokenType.Float || c.Type == JTokenType.Integer) ? c.Value<double>() : 0d;
        int turns = root["num_turns"] is JToken t && t.Type == JTokenType.Integer ? (int)Math.Min(int.MaxValue, t.Value<long>()) : 0;
        return new AgentStreamEvent(isError ? AgentStreamEventKind.Error : AgentStreamEventKind.Result, Limit(text, MaxTextLength))
        {
            Cost = cost,
            Turns = turns,
        };
    }

    private static IReadOnlyList<AgentStreamEvent> AssistantEvents(JToken? content)
    {
        if (content is not JArray blocks)
        {
            return NoEvents;
        }

        List<AgentStreamEvent> events = new List<AgentStreamEvent>();
        foreach (JToken item in blocks)
        {
            if (item is not JObject block)
            {
                continue;
            }

            string? kind = Str(block["type"]);
            if (string.Equals(kind, "text", StringComparison.Ordinal))
            {
                string text = Limit(Str(block["text"]) ?? string.Empty, MaxTextLength);
                if (text.Length > 0)
                {
                    events.Add(new AgentStreamEvent(AgentStreamEventKind.Text, text));
                }
            }
            else if (string.Equals(kind, "tool_use", StringComparison.Ordinal))
            {
                string name = ShortToolName(Str(block["name"]));
                string arguments = ArgumentSummary(block["input"] as JObject);
                events.Add(new AgentStreamEvent(AgentStreamEventKind.ToolCall, arguments.Length > 0 ? name + " (" + arguments + ")" : name)
                {
                    ToolName = name,
                    ToolUseId = Str(block["id"]) ?? string.Empty,
                });
            }
        }

        return events;
    }

    private static IReadOnlyList<AgentStreamEvent> UserEvents(JToken? content)
    {
        if (content is not JArray blocks)
        {
            return NoEvents;
        }

        List<AgentStreamEvent> events = new List<AgentStreamEvent>();
        foreach (JToken item in blocks)
        {
            if (item is not JObject block || !string.Equals(Str(block["type"]), "tool_result", StringComparison.Ordinal))
            {
                continue;
            }

            bool failed = block["is_error"] is JToken flag && flag.Type == JTokenType.Boolean && flag.Value<bool>();
            string text = ToolResultText(block["content"]);
            events.Add(new AgentStreamEvent(AgentStreamEventKind.ToolResult, (failed ? "error: " : string.Empty) + text)
            {
                ToolUseId = Str(block["tool_use_id"]) ?? string.Empty,
            });
        }

        return events;
    }

    // Text parts only; an image part becomes "[image]" and its data is not read.
    private static string ToolResultText(JToken? content)
    {
        if (content == null)
        {
            return string.Empty;
        }

        if (content.Type == JTokenType.String)
        {
            return Limit(content.Value<string>() ?? string.Empty, MaxToolResultText);
        }

        if (content is not JArray parts)
        {
            return string.Empty;
        }

        StringBuilder builder = new StringBuilder();
        int images = 0;
        foreach (JToken item in parts)
        {
            if (item is not JObject part)
            {
                continue;
            }

            string? kind = Str(part["type"]);
            if (string.Equals(kind, "text", StringComparison.Ordinal))
            {
                if (builder.Length < MaxToolResultText)
                {
                    builder.Append(' ').Append(Str(part["text"]));
                }
            }
            else if (string.Equals(kind, "image", StringComparison.Ordinal))
            {
                images++;
            }
        }

        string result = Limit(builder.ToString(), MaxToolResultText);
        if (images > 0)
        {
            string note = images == 1 ? "[image]" : "[" + images.ToString(CultureInfo.InvariantCulture) + " images]";
            result = result.Length > 0 ? result + " " + note : note;
        }

        return result;
    }

    // "key=value" of the scalar arguments only, short; a long value or a nested object is left out.
    private static string ArgumentSummary(JObject? input)
    {
        if (input == null)
        {
            return string.Empty;
        }

        StringBuilder builder = new StringBuilder();
        foreach (JProperty property in input.Properties())
        {
            if (!(property.Value is JValue value) || value.Type == JTokenType.Null)
            {
                continue;
            }

            string text = Convert.ToString(value.Value, CultureInfo.InvariantCulture) ?? string.Empty;
            if (text.Length > MaxArgumentText)
            {
                continue;
            }

            if (builder.Length > 0)
            {
                builder.Append(", ");
            }

            builder.Append(property.Name).Append('=').Append(text);
            if (builder.Length >= MaxArgumentsText)
            {
                break;
            }
        }

        return Limit(builder.ToString(), MaxArgumentsText);
    }

    private static string? Str(JToken? token)
    {
        return token != null && token.Type == JTokenType.String ? token.Value<string>() : null;
    }

    private static string Limit(string text, int max)
    {
        string collapsed = Regex.Replace(text, @"\s+", " ").Trim();
        return collapsed.Length <= max ? collapsed : collapsed.Substring(0, max);
    }
}

/// <summary>What <see cref="AgentStreamReader.ReadAsync"/> found.</summary>
public sealed class AgentStreamRead
{
    /// <summary>The lines worth keeping for the final result (result lines, non-event lines), at most <see cref="AgentStreamReader.MaxKeptLength"/> characters.</summary>
    public string Kept { get; init; } = string.Empty;

    /// <summary>The longest line held in memory at once (never above <see cref="AgentStreamReader.MaxLineLength"/>).</summary>
    public int PeakLineLength { get; init; }

    /// <summary>Lines that were longer than the limit and cut.</summary>
    public int CutLines { get; init; }
}

/// <summary>Reads agent output line by line with a bounded line buffer, so a huge image line is never held whole.</summary>
public static class AgentStreamReader
{
    /// <summary>A longer line is cut at this many characters (the rest is skipped unread).</summary>
    public const int MaxLineLength = 256 * 1024;

    /// <summary>The kept text stops growing at this size.</summary>
    public const int MaxKeptLength = 2 * 1024 * 1024;

    /// <summary>Reads until the end of the stream. <paramref name="onEvent"/> gets every event at once; it must not throw (an exception is swallowed).</summary>
    public static async Task<AgentStreamRead> ReadAsync(TextReader reader, Action<AgentStreamEvent>? onEvent, Action<string>? onLine = null)
    {
        StringBuilder kept = new StringBuilder();
        StringBuilder line = new StringBuilder();
        char[] buffer = new char[8192];
        bool cut = false;
        int peak = 0;
        int cutLines = 0;
        while (true)
        {
            int count = await reader.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
            if (count <= 0)
            {
                break;
            }

            for (int i = 0; i < count; i++)
            {
                char c = buffer[i];
                if (c == '\n')
                {
                    peak = Math.Max(peak, line.Length);
                    cutLines += Finish(line, cut, kept, onEvent, onLine) ? 1 : 0;
                    cut = false;
                }
                else if (c != '\r')
                {
                    if (line.Length < MaxLineLength)
                    {
                        line.Append(c);
                    }
                    else
                    {
                        cut = true;
                    }
                }
            }
        }

        if (line.Length > 0)
        {
            peak = Math.Max(peak, line.Length);
            cutLines += Finish(line, cut, kept, onEvent, onLine) ? 1 : 0;
        }

        return new AgentStreamRead { Kept = kept.ToString(), PeakLineLength = peak, CutLines = cutLines };
    }

    // Returns true when the line had been cut.
    private static bool Finish(StringBuilder line, bool cut, StringBuilder kept, Action<AgentStreamEvent>? onEvent, Action<string>? onLine)
    {
        string text = line.ToString();
        line.Clear();
        if (onLine != null && !cut)
        {
            try
            {
                // The raw line of a run (usage for the budget watchdog); a faulty listener must not break the read.
                onLine(text);
            }
            catch (Exception)
            {
            }
        }

        IReadOnlyList<AgentStreamEvent> events;
        if (cut)
        {
            events = AgentStreamParser.ParseTruncated(text);
        }
        else
        {
            events = AgentStreamParser.ParseLine(text, out bool keep);
            if (keep && kept.Length + text.Length + 1 <= MaxKeptLength)
            {
                kept.Append(text).Append('\n');
            }
        }

        if (onEvent != null)
        {
            foreach (AgentStreamEvent item in events)
            {
                try
                {
                    onEvent(item);
                }
                catch (Exception)
                {
                    // A faulty listener must not break the read of the final result.
                }
            }
        }

        return cut;
    }
}

/// <summary>
/// Per-turn and per-tool wall times of one run, written to <c>timing.csv</c> in the run folder: <c>kind,turn,tool,ms</c>. A turn is the wait from the
/// previous tool result (or the start) to the first thing the agent wrote or called; a tool row is the time from its call to its result.
/// Holds names and numbers only, never the prompt or any tool output.
/// </summary>
public sealed class AgentStreamTiming
{
    public const string FileName = "timing.csv";

    private readonly object _lock = new object();
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private readonly List<string> _rows = new List<string>();
    private readonly Dictionary<string, KeyValuePair<string, long>> _open = new Dictionary<string, KeyValuePair<string, long>>(StringComparer.Ordinal);
    private long _mark;
    private int _turn;
    private bool _turnOpen;

    /// <summary>Records the event and returns it; a tool result gets the tool name of its call.</summary>
    public AgentStreamEvent Observe(AgentStreamEvent item)
    {
        lock (_lock)
        {
            long now = _clock.ElapsedMilliseconds;
            switch (item.Kind)
            {
                case AgentStreamEventKind.Text:
                case AgentStreamEventKind.ToolCall:
                    if (!_turnOpen)
                    {
                        _turn++;
                        _turnOpen = true;
                        _rows.Add("turn," + _turn.ToString(CultureInfo.InvariantCulture) + ",," + (now - _mark).ToString(CultureInfo.InvariantCulture));
                    }

                    if (item.Kind == AgentStreamEventKind.ToolCall && item.ToolUseId.Length > 0 && _open.Count < 1000)
                    {
                        _open[item.ToolUseId] = new KeyValuePair<string, long>(item.ToolName, now);
                    }

                    return item;
                case AgentStreamEventKind.ToolResult:
                    _mark = now;
                    _turnOpen = false;
                    if (item.ToolUseId.Length > 0 && _open.TryGetValue(item.ToolUseId, out KeyValuePair<string, long> call))
                    {
                        _open.Remove(item.ToolUseId);
                        _rows.Add("tool," + _turn.ToString(CultureInfo.InvariantCulture) + "," + Clean(call.Key) + "," + (now - call.Value).ToString(CultureInfo.InvariantCulture));
                        return item with { ToolName = call.Key };
                    }

                    return item;
                default:
                    _rows.Add("total," + _turn.ToString(CultureInfo.InvariantCulture) + ",," + now.ToString(CultureInfo.InvariantCulture));
                    return item;
            }
        }
    }

    /// <summary>The csv lines so far (header first).</summary>
    public IReadOnlyList<string> Lines()
    {
        lock (_lock)
        {
            List<string> lines = new List<string> { "kind,turn,tool,ms" };
            lines.AddRange(_rows);
            return lines;
        }
    }

    /// <summary>Writes <see cref="Lines"/> to the file; a file problem is ignored (timing is a diagnostic aid).</summary>
    public void TryWrite(string path)
    {
        try
        {
            File.WriteAllLines(path, Lines());
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
        }
    }

    private static string Clean(string name)
    {
        return name.Replace(',', '_').Replace('\n', ' ').Replace('\r', ' ');
    }
}
