using System.Globalization;

namespace EasyAI.Desktop.Services.Agent;

/// <summary>What a row of the model / effort pill menu is.</summary>
public enum AgentPillItemKind
{
    Model,
    Separator,
    Effort,
}

/// <summary>One row of the model / effort pill menu (the menu binds to these rows).</summary>
public sealed class AgentPillItem
{
    public AgentPillItem(AgentPillItemKind kind, string value, string header, bool isChecked, string automationId)
    {
        Kind = kind;
        Value = value;
        Header = header;
        IsChecked = isChecked;
        AutomationId = automationId;
    }

    public AgentPillItemKind Kind { get; }

    /// <summary>The model value (<c>--model</c>) or the effort level (<c>low</c>..<c>max</c>).</summary>
    public string Value { get; }

    public string Header { get; }

    public bool IsChecked { get; }

    public string AutomationId { get; }

    public bool IsSeparator => Kind == AgentPillItemKind.Separator;
}

/// <summary>Pure rules of the agent chat window: mode / effort text, the caps caption, the prompt-box key, the model / effort pill and the Check prompt.</summary>
public static class AgentChatRules
{
    /// <summary>The settings text of a batch mode: <c>current</c>, <c>all</c> or <c>remaining</c>.</summary>
    public static string ModeKey(AgentBatchMode mode)
    {
        switch (mode)
        {
            case AgentBatchMode.All: return "all";
            case AgentBatchMode.Remaining: return "remaining";
            default: return "current";
        }
    }

    /// <summary>The effort levels in ascending order, as the program names them and as the settings store them.</summary>
    public static readonly IReadOnlyList<string> EffortLevels = new[] { "low", "medium", "high", "xhigh", "max" };

    /// <summary>The settings text and program name of an effort: <c>low</c>, <c>medium</c>, <c>high</c>, <c>xhigh</c> or <c>max</c>.</summary>
    public static string EffortKey(AgentEffort effort)
    {
        switch (effort)
        {
            case AgentEffort.Economy: return "low";
            case AgentEffort.Best: return "high";
            case AgentEffort.XHigh: return "xhigh";
            case AgentEffort.Max: return "max";
            default: return "medium";
        }
    }

    /// <summary>Position of an effort in <see cref="EffortLevels"/>: 0 low, 1 medium, 2 high, 3 xhigh, 4 max.</summary>
    public static int EffortIndex(AgentEffort effort)
    {
        switch (effort)
        {
            case AgentEffort.Economy: return 0;
            case AgentEffort.Best: return 2;
            case AgentEffort.XHigh: return 3;
            case AgentEffort.Max: return 4;
            default: return 1;
        }
    }

    /// <summary>
    /// The effort levels the pill menu offers for a model: the levels the model reports (known ones only, in ascending order);
    /// low, medium and high when the model reports none.
    /// </summary>
    public static IReadOnlyList<string> SelectableEfforts(IReadOnlyList<string>? modelLevels)
    {
        List<string> result = new List<string>();
        foreach (string level in EffortLevels)
        {
            if (modelLevels != null && modelLevels.Any(m => string.Equals(m, level, StringComparison.OrdinalIgnoreCase)))
            {
                result.Add(level);
            }
        }

        return result.Count > 0 ? result : new List<string> { "low", "medium", "high" };
    }

    /// <summary>The level to keep when the model changes: the current one when offered, else medium when offered, else the highest offered one below it, else the first.</summary>
    public static string CoerceEffort(string level, IReadOnlyList<string> offered)
    {
        if (offered.Count == 0 || offered.Contains(level))
        {
            return level;
        }

        if (offered.Contains("medium"))
        {
            return "medium";
        }

        return offered[offered.Count - 1];
    }

    /// <summary>English name of an effort level (the window uses the resource text and falls back to this one).</summary>
    public static string EffortLabel(string level)
    {
        switch (level)
        {
            case "low": return "Low";
            case "high": return "High";
            case "xhigh": return "Extra high";
            case "max": return "Max";
            default: return "Medium";
        }
    }

    /// <summary>The text of the pill: the model name and the effort, for example "Sonnet 5.5 Medium".</summary>
    public static string PillText(string modelName, string effortLabel)
    {
        string name = (modelName ?? string.Empty).Trim();
        return name.Length == 0 ? effortLabel : name + " " + effortLabel;
    }

    /// <summary>
    /// The rows of the pill menu: the models (check on the one in use), a separator, then the offered effort levels (check on the one in use).
    /// <paramref name="effortLabelOf"/> turns a level into the text shown.
    /// </summary>
    public static IReadOnlyList<AgentPillItem> BuildPillItems(
        IReadOnlyList<AgentModelInfo> models,
        string currentModel,
        IReadOnlyList<string> offeredEfforts,
        string currentEffort,
        Func<string, string> effortLabelOf)
    {
        List<AgentPillItem> items = new List<AgentPillItem>();
        foreach (AgentModelInfo model in models)
        {
            string id = model.Value.Length == 0 ? "default" : model.Value;
            items.Add(new AgentPillItem(AgentPillItemKind.Model, model.Value, model.DisplayName, string.Equals(model.Value, currentModel, StringComparison.Ordinal), "AgentChatModel_" + id));
        }

        items.Add(new AgentPillItem(AgentPillItemKind.Separator, string.Empty, string.Empty, false, string.Empty));
        foreach (string level in offeredEfforts)
        {
            items.Add(new AgentPillItem(AgentPillItemKind.Effort, level, effortLabelOf(level), string.Equals(level, currentEffort, StringComparison.OrdinalIgnoreCase), "AgentChatEffort_" + level));
        }

        return items;
    }

    /// <summary>
    /// The read-only caps caption, for example "50 images · $2.00 max". <paramref name="format"/> takes {0} images and {1} the cost;
    /// <paramref name="noCostFormat"/> takes {0} images and is used when the cost cap is off (0, negative or not a number).
    /// </summary>
    public static string FormatCaps(string format, string noCostFormat, int maxImages, double maxCostUsd)
    {
        int images = Math.Max(1, maxImages);
        if (double.IsNaN(maxCostUsd) || double.IsInfinity(maxCostUsd) || maxCostUsd <= 0)
        {
            return string.Format(CultureInfo.InvariantCulture, noCostFormat, images);
        }

        return string.Format(CultureInfo.InvariantCulture, format, images, maxCostUsd.ToString("0.00", CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Enter in the prompt box runs; Shift+Enter is a new line. Ctrl or Alt with Enter is left to the box. (An Enter that an input method
    /// consumes while composing arrives as Key.ImeProcessed, not as Key.Enter, so it never reaches this rule.)
    /// </summary>
    public static bool IsSubmitKey(bool isEnter, bool shift, bool ctrl, bool alt)
    {
        return isEnter && !shift && !ctrl && !alt;
    }

    /// <summary>
    /// The text the app sends for the Check button: the existing shapes of the current image are reviewed (the "check" skill carries the steps);
    /// the prompt box, when not empty, is added as extra instructions.
    /// </summary>
    public static string ComposeCheckPrompt(string? userPrompt)
    {
        const string text = "Check the labels that already exist on this image (use the check skill). Do not label from scratch: "
            + "review every existing shape, and send a fix only for a shape that is wrong, as a replace or a remove with a short note. "
            + "Leave correct shapes alone. In your final answer say how many shapes you checked and how many you propose to change.";
        string extra = (userPrompt ?? string.Empty).Trim();
        return extra.Length == 0 ? text : text + " Extra instructions from the user: " + extra;
    }
}
