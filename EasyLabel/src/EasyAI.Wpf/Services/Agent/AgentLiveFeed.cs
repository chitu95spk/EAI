using System.Globalization;
using System.Text;

namespace EasyAI.Desktop.Services.Agent;

/// <summary>
/// The user-visible words of the live agent transcript. English defaults keep the mapping testable without resources;
/// <see cref="FromLookup"/> fills them from the localized resources (key, English fallback).
/// </summary>
public sealed class AgentLiveLabels
{
    public string LookImage { get; init; } = "Looking at the image";

    public string ListShapes { get; init; } = "Checking the existing shapes";

    /// <summary>Format with {0} x, {1} y, {2} width, {3} height.</summary>
    public string CropRegion { get; init; } = "Cropping region {0},{1},{2},{3}";

    public string GridCrop { get; init; } = "Inspecting a region with a grid";

    public string Overlay { get; init; } = "Checking the drawn result";

    public string Measure { get; init; } = "Measuring";

    public string Sam2 { get; init; } = "Outlining the object";

    public string Proposing { get; init; } = "Proposing shapes";

    public string MemoryAdd { get; init; } = "Saving a note to the project memory";

    public string MemoryRead { get; init; } = "Reading the project memory";

    /// <summary>Format with {0} = the tool name.</summary>
    public string UsingTool { get; init; } = "Using {0}";

    /// <summary>Format with {0} = the error text.</summary>
    public string ErrorFormat { get; init; } = "Error: {0}";

    /// <summary>Format with {0} index (1-based), {1} total, {2} file name.</summary>
    public string ImageHeader { get; init; } = "Image {0}/{1}: {2}";

    public string Working { get; init; } = "Working...";

    /// <summary>Format with {0} = the number of tool calls so far.</summary>
    public string StatusStep { get; init; } = "step {0}";

    /// <summary>Format with {0} index (1-based), {1} total.</summary>
    public string StatusImage { get; init; } = "image {0}/{1}";

    /// <summary>Format with {0} = the cost so far, {1} = the cost cap.</summary>
    public string StatusCostCap { get; init; } = "{0} of {1}";

    /// <summary>Labels from a lookup <c>(key, english) =&gt; text</c>; the keys are listed in the task report (all start with "AgentLive").</summary>
    public static AgentLiveLabels FromLookup(Func<string, string, string> lookup)
    {
        AgentLiveLabels d = new AgentLiveLabels();
        return new AgentLiveLabels
        {
            LookImage = lookup("AgentLiveLookImage", d.LookImage),
            ListShapes = lookup("AgentLiveListShapes", d.ListShapes),
            CropRegion = lookup("AgentLiveCropRegion", d.CropRegion),
            GridCrop = lookup("AgentLiveGridCrop", d.GridCrop),
            Overlay = lookup("AgentLiveOverlay", d.Overlay),
            Measure = lookup("AgentLiveMeasure", d.Measure),
            Sam2 = lookup("AgentLiveSam2", d.Sam2),
            Proposing = lookup("AgentLiveProposing", d.Proposing),
            MemoryAdd = lookup("AgentLiveMemoryAdd", d.MemoryAdd),
            MemoryRead = lookup("AgentLiveMemoryRead", d.MemoryRead),
            UsingTool = lookup("AgentLiveUsingTool", d.UsingTool),
            ErrorFormat = lookup("AgentLiveError", d.ErrorFormat),
            ImageHeader = lookup("AgentLiveImageHeader", d.ImageHeader),
            Working = lookup("AgentLiveWorking", d.Working),
            StatusStep = lookup("AgentLiveStatusStep", d.StatusStep),
            StatusImage = lookup("AgentLiveStatusImage", d.StatusImage),
            StatusCostCap = lookup("AgentLiveStatusCostCap", d.StatusCostCap),
        };
    }
}

/// <summary>
/// Turns the stream events of an agent run into the readable lines of the live chat row: assistant text as it is, tool calls as short sentences,
/// tool results not at all, errors as one error line, and one header line per batch image. Keeps at most <see cref="MaxLines"/> lines of
/// <see cref="MaxLineLength"/> characters. Pure (no WPF, no I/O); not thread-safe (call it from one thread).
/// </summary>
public sealed class AgentLiveFeed
{
    public const int MaxLines = 200;

    public const int MaxLineLength = 300;

    /// <summary>Longest final agent text kept in the chat history.</summary>
    public const int MaxFinalTextLength = 500;

    private readonly AgentLiveLabels _labels;
    private readonly List<string> _lines = new List<string>();
    private string _lastText = string.Empty;
    private string _resultText = string.Empty;

    public AgentLiveFeed(AgentLiveLabels? labels = null)
    {
        _labels = labels ?? new AgentLiveLabels();
    }

    /// <summary>The kept lines, oldest first.</summary>
    public IReadOnlyList<string> Lines => _lines;

    /// <summary>Counts every change of the lines (the caller refreshes only when it moved).</summary>
    public int Version { get; private set; }

    /// <summary>Tool calls seen so far.</summary>
    public int ToolCalls { get; private set; }

    /// <summary>Total cost of the last result line (0 until one arrives).</summary>
    public double Cost { get; private set; }

    /// <summary>Turns of the last result line.</summary>
    public int Turns { get; private set; }

    /// <summary>A result or error line arrived.</summary>
    public bool HasResult { get; private set; }

    /// <summary>The lines joined by new lines.</summary>
    public string Text => string.Join("\n", _lines);

    /// <summary>
    /// The closing text of the run for the chat history: the text of the result line, else the last text the agent wrote; at most
    /// <see cref="MaxFinalTextLength"/> characters (empty when the agent wrote nothing).
    /// </summary>
    public string FinalText => Cap(_resultText.Length > 0 ? _resultText : _lastText, MaxFinalTextLength);

    /// <summary>Adds the header line of one batch image ("Image 2/5: name.png").</summary>
    public void AddHeader(int index, int total, string rel)
    {
        string name = string.IsNullOrEmpty(rel) ? string.Empty : System.IO.Path.GetFileName(rel);
        if (name.Length == 0)
        {
            name = rel ?? string.Empty;
        }

        AddLine(string.Format(CultureInfo.InvariantCulture, _labels.ImageHeader, index, total, name));
    }

    /// <summary>Adds the line of one event; returns true when the visible text changed.</summary>
    public bool Add(AgentStreamEvent? item)
    {
        if (item == null)
        {
            return false;
        }

        switch (item.Kind)
        {
            case AgentStreamEventKind.Text:
            {
                string text = Collapse(item.Text);
                if (text.Length == 0)
                {
                    return false;
                }

                _lastText = text;
                AddLine(text);
                return true;
            }

            case AgentStreamEventKind.ToolCall:
                ToolCalls++;
                AddLine(MapTool(item.ToolName, item.Text, _labels));
                return true;

            case AgentStreamEventKind.Result:
                HasResult = true;
                Cost = item.Cost;
                Turns = item.Turns;
                _resultText = Collapse(item.Text);
                return false;

            case AgentStreamEventKind.Error:
            {
                HasResult = true;
                Cost = item.Cost;
                Turns = item.Turns;
                string text = Collapse(item.Text);
                AddLine(string.Format(CultureInfo.InvariantCulture, _labels.ErrorFormat, text.Length > 0 ? text : "?"));
                return true;
            }

            default:
                // Tool results are not shown (their text can be long and holds no news for the user).
                return false;
        }
    }

    /// <summary>The short readable line of a tool call. <paramref name="callText"/> is the event text "name (key=value, ...)" (may be empty).</summary>
    public static string MapTool(string? toolName, string? callText, AgentLiveLabels? labels = null)
    {
        AgentLiveLabels words = labels ?? new AgentLiveLabels();
        string name = (toolName ?? string.Empty).Trim();
        switch (name)
        {
            case "get_image_info": return words.LookImage;
            case "list_shapes": return words.ListShapes;
            case "get_crop":
            {
                Dictionary<string, string> args = ParseArguments(callText);
                if (args.TryGetValue("x", out string? x) && args.TryGetValue("y", out string? y)
                    && args.TryGetValue("width", out string? w) && args.TryGetValue("height", out string? h))
                {
                    return string.Format(CultureInfo.InvariantCulture, words.CropRegion, x, y, w, h);
                }

                return words.LookImage;
            }

            case "grid_crop": return words.GridCrop;
            case "render_overlay": return words.Overlay;
            case "measure": return words.Measure;
            case "sam2_segment": return words.Sam2;
            case "propose_shapes": return words.Proposing;
            case "memory_add": return words.MemoryAdd;
            case "memory_read": return words.MemoryRead;
            default:
                return string.Format(CultureInfo.InvariantCulture, words.UsingTool, name.Length > 0 ? name : "?");
        }
    }

    /// <summary>"key=value" pairs of a tool-call text "name (k=v, k=v)"; empty when there are none.</summary>
    public static Dictionary<string, string> ParseArguments(string? callText)
    {
        Dictionary<string, string> result = new Dictionary<string, string>(StringComparer.Ordinal);
        string text = callText ?? string.Empty;
        int open = text.IndexOf('(');
        int close = text.LastIndexOf(')');
        if (open < 0 || close <= open)
        {
            return result;
        }

        foreach (string part in text.Substring(open + 1, close - open - 1).Split(','))
        {
            int eq = part.IndexOf('=');
            if (eq > 0)
            {
                result[part.Substring(0, eq).Trim()] = part.Substring(eq + 1).Trim();
            }
        }

        return result;
    }

    /// <summary>The compact status line: "Working... $0.034 of $1.00 - image 2/5 - step 3". Parts without a value are left out.</summary>
    /// <param name="cost">Cost so far in USD (0 = unknown, left out).</param>
    /// <param name="steps">Tool calls of the current run (0 = left out).</param>
    /// <param name="imageIndex">1-based batch image index (0 = not a batch).</param>
    /// <param name="imageTotal">Batch image count.</param>
    /// <param name="costCap">Batch cost cap in USD, or null when there is none.</param>
    public static string FormatStatus(AgentLiveLabels? labels, double cost, int steps, int imageIndex, int imageTotal, double? costCap)
    {
        AgentLiveLabels words = labels ?? new AgentLiveLabels();
        StringBuilder text = new StringBuilder(words.Working);
        string costText = cost > 0 ? "$" + cost.ToString("0.000", CultureInfo.InvariantCulture) : string.Empty;
        if (costCap.HasValue && imageIndex > 0)
        {
            string cap = "$" + costCap.Value.ToString("0.00", CultureInfo.InvariantCulture);
            text.Append(' ').Append(string.Format(CultureInfo.InvariantCulture, words.StatusCostCap, costText.Length > 0 ? costText : "$0.000", cap));
        }
        else if (costText.Length > 0)
        {
            text.Append(' ').Append(costText);
        }

        if (imageIndex > 0 && imageTotal > 0)
        {
            text.Append(" - ").Append(string.Format(CultureInfo.InvariantCulture, words.StatusImage, imageIndex, imageTotal));
        }

        if (steps > 0)
        {
            text.Append(" - ").Append(string.Format(CultureInfo.InvariantCulture, words.StatusStep, steps));
        }

        return text.ToString();
    }

    /// <summary>White space collapsed to single spaces, cut to <paramref name="max"/> characters (with "..." when cut; the result never exceeds max).</summary>
    public static string Cap(string? text, int max)
    {
        string value = Collapse(text);
        if (value.Length <= max)
        {
            return value;
        }

        return max <= 3 ? value.Substring(0, Math.Max(0, max)) : value.Substring(0, max - 3) + "...";
    }

    private static string Collapse(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        StringBuilder builder = new StringBuilder(text.Length);
        bool space = false;
        foreach (char c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                space = builder.Length > 0;
                continue;
            }

            if (space)
            {
                builder.Append(' ');
                space = false;
            }

            builder.Append(c);
        }

        return builder.ToString();
    }

    private void AddLine(string line)
    {
        _lines.Add(Cap(line, MaxLineLength));
        while (_lines.Count > MaxLines)
        {
            _lines.RemoveAt(0);
        }

        Version++;
    }
}

/// <summary>Decides when the live row is redrawn: at most once per <see cref="IntervalMs"/> (about 10 per second).</summary>
public sealed class AgentLiveThrottle
{
    public const long IntervalMs = 100;

    private long _last = long.MinValue;

    /// <summary>True when a redraw is due at <paramref name="nowMs"/> (a monotonic clock); the call counts as the redraw.</summary>
    public bool ShouldFlush(long nowMs)
    {
        if (_last == long.MinValue || nowMs - _last >= IntervalMs || nowMs < _last)
        {
            _last = nowMs;
            return true;
        }

        return false;
    }
}
