namespace EasyAI.Desktop.Services.Agent;

/// <summary>How exact the boxes of an agent run must be: loose (fast), the default, or hugging the object.</summary>
public enum AgentPrecision
{
    Easy,
    Normal,
    Hard,
}

/// <summary>
/// How much the agent may spend on a run. The first three members keep their old names (they are stored in the view model and the tests):
/// <c>Economy</c> = level low, <c>Normal</c> = medium (the default), <c>Best</c> = high; <c>XHigh</c> and <c>Max</c> are the two levels above.
/// </summary>
public enum AgentEffort
{
    Economy,
    Normal,
    Best,
    XHigh,
    Max,
}

/// <summary>
/// The three choices of the Agent Prompt panel (box accuracy, effort, model) and what each one changes in a run: an instruction added to the
/// prompt, the turn limit, the effort flag of the program and the model. <see cref="Default"/> reproduces the original run exactly.
/// </summary>
public sealed class AgentRunOptions
{
    private readonly IReadOnlyList<string>? _supportedEfforts;

    /// <param name="supportedEfforts">Effort levels the chosen model reports; an effort the model does not support is not sent. Null = unknown, send it.</param>
    public AgentRunOptions(AgentPrecision precision, AgentEffort effort, string? model, IReadOnlyList<string>? supportedEfforts = null)
    {
        Precision = precision;
        Effort = effort;
        Model = (model ?? string.Empty).Trim();
        _supportedEfforts = supportedEfforts;
    }

    private string? Level(string level)
    {
        return _supportedEfforts == null || _supportedEfforts.Count == 0 || _supportedEfforts.Contains(level, StringComparer.OrdinalIgnoreCase) ? level : null;
    }

    // True for a single-pass label run (image facts + one propose): low effort then needs fewer turns.
    private bool _singlePassLabel;

    /// <summary>Turn limit of a low-effort label run (overview, one batch of crops, propose, closing message, with room to spare).</summary>
    public const int LowLabelTurns = 6;

    /// <summary>
    /// These options for a run of <paramref name="kind"/>: the same choices, but a label run at low effort gets <see cref="LowLabelTurns"/>
    /// instead of 8 turns. Every other combination returns this instance. The cache tag does not change.
    /// </summary>
    public AgentRunOptions ForKind(AgentRunKind kind)
    {
        if (kind != AgentRunKind.Label || Effort != AgentEffort.Economy)
        {
            return this;
        }

        return new AgentRunOptions(Precision, Effort, Model, _supportedEfforts) { _singlePassLabel = true };
    }

    public static AgentRunOptions Default { get; } = new AgentRunOptions(AgentPrecision.Normal, AgentEffort.Normal, null);

    public AgentPrecision Precision { get; }

    public AgentEffort Effort { get; }

    /// <summary>The chosen model (alias or id); empty = the program's own default.</summary>
    public string Model { get; }

    /// <summary>The model Claude Code is started with: the choice, else <see cref="AgentCommandLine.Model"/>.</summary>
    public string ClaudeModel => Model.Length > 0 && IsSafeModel(Model) ? Model : AgentCommandLine.Model;

    /// <summary>The model Codex is started with, or null for the account default.</summary>
    public string? CodexModel => Model.Length > 0 && IsSafeModel(Model) ? Model : null;

    /// <summary>Turn limit of a labelling run (a suggestion run always keeps its own small limit).</summary>
    public int MaxTurns
    {
        get
        {
            int turns = Effort == AgentEffort.Economy ? (_singlePassLabel ? LowLabelTurns : 8) : Effort == AgentEffort.Normal ? AgentCommandLine.MaxTurns : 22;
            return Precision == AgentPrecision.Hard ? turns + 4 : turns;
        }
    }

    /// <summary>The effort as the program names it: <c>low</c>, <c>medium</c>, <c>high</c>, <c>xhigh</c> or <c>max</c>.</summary>
    public string EffortLevel => AgentChatRules.EffortKey(Effort);

    /// <summary><c>--effort</c> of Claude Code, always sent explicitly; null only when the chosen model reports levels and does not list this one.</summary>
    public string? ClaudeEffort => Level(EffortLevel);

    /// <summary><c>model_reasoning_effort</c> of Codex; null keeps the default (the medium effort adds no flag; Codex has no level above xhigh).</summary>
    public string? CodexEffort => Effort == AgentEffort.Economy ? Level("low")
        : Effort == AgentEffort.Best ? Level("high")
        : Effort == AgentEffort.XHigh || Effort == AgentEffort.Max ? Level("xhigh") : null;

    /// <summary>Added to the task text of a labelling run; empty for Normal accuracy and Normal effort.</summary>
    public string PromptSuffix
    {
        get
        {
            List<string> parts = new List<string>();
            if (Precision == AgentPrecision.Easy)
            {
                parts.Add("Box accuracy: easy. A loose box with a small margin is fine; prefer speed and as few crops as possible.");
            }
            else if (Precision == AgentPrecision.Hard)
            {
                parts.Add("Box accuracy: hard. Every box must hug the object within a few pixels: after a first estimate, zoom into each object with get_crop and correct every corner before you propose; use sam2_segment outlines when they are available.");
            }

            if (Effort == AgentEffort.Economy)
            {
                parts.Add("Economy: use at most two get_crop calls in total and do not re-check.");
            }
            else if (Effort == AgentEffort.Best || Effort == AgentEffort.XHigh || Effort == AgentEffort.Max)
            {
                parts.Add("Best effort: be thorough, check every region and re-verify uncertain objects before you propose.");
            }

            return string.Join(" ", parts);
        }
    }

    /// <summary>The line a "Suggest prompt" run gets so its prompts ask for the chosen accuracy; empty for Normal.</summary>
    public string SuggestHint => Precision == AgentPrecision.Easy
        ? "Each prompt may allow loose boxes."
        : Precision == AgentPrecision.Hard ? "Each prompt must ask for tight boxes that hug the object." : string.Empty;

    /// <summary>Text for the result cache key: a different model, effort or accuracy is a different request.</summary>
    public string CacheTag => ClaudeModel + "|" + CodexModel + "|" + Effort + "|" + Precision;

    // A model id goes on the command line as one argument; refuse anything that is not a plain id (letters, digits, '.', '-', '_', ':', '/').
    private static bool IsSafeModel(string value)
    {
        foreach (char c in value)
        {
            if (!(char.IsLetterOrDigit(c) || c == '.' || c == '-' || c == '_' || c == ':' || c == '/'))
            {
                return false;
            }
        }

        return !value.StartsWith('-');
    }

    public static AgentPrecision ParsePrecision(string? value)
    {
        return string.Equals(value, "easy", StringComparison.OrdinalIgnoreCase) ? AgentPrecision.Easy
            : string.Equals(value, "hard", StringComparison.OrdinalIgnoreCase) ? AgentPrecision.Hard : AgentPrecision.Normal;
    }

    public static AgentEffort ParseEffort(string? value)
    {
        // Old settings say economy / normal / best; the new ones say low / medium / high / xhigh / max. Anything else is the default (medium).
        string text = (value ?? string.Empty).Trim();
        if (string.Equals(text, "economy", StringComparison.OrdinalIgnoreCase) || string.Equals(text, "low", StringComparison.OrdinalIgnoreCase))
        {
            return AgentEffort.Economy;
        }

        if (string.Equals(text, "best", StringComparison.OrdinalIgnoreCase) || string.Equals(text, "high", StringComparison.OrdinalIgnoreCase))
        {
            return AgentEffort.Best;
        }

        if (string.Equals(text, "xhigh", StringComparison.OrdinalIgnoreCase))
        {
            return AgentEffort.XHigh;
        }

        return string.Equals(text, "max", StringComparison.OrdinalIgnoreCase) ? AgentEffort.Max : AgentEffort.Normal;
    }
}
