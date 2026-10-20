namespace EasyAI.Desktop.Services.Agent;

/// <summary>The kind of a chat input line.</summary>
public enum AgentSlashKind
{
    /// <summary>Not a command: the text is a normal prompt.</summary>
    Prompt,

    /// <summary>Empty input (nothing to do).</summary>
    Empty,

    /// <summary>A "/word" that is not a known command: shown in the chat and never sent to the agent.</summary>
    Unknown,

    Clear,
    Check,
    Suggest,
    Model,
    Mode,
    Cancel,
    Help,

    /// <summary>Clears the learned project vocabulary.</summary>
    Forget,
}

/// <summary>The parsed chat input line: the kind, the command word as typed (lower case, without the slash) and the text after it.</summary>
public sealed class AgentSlashCommand
{
    public AgentSlashCommand(AgentSlashKind kind, string name, string args)
    {
        Kind = kind;
        Name = name;
        Args = args;
    }

    public AgentSlashKind Kind { get; }

    /// <summary>The command word in lower case without the leading slash (empty for a prompt).</summary>
    public string Name { get; }

    /// <summary>The text after the command word, trimmed (empty when there is none).</summary>
    public string Args { get; }

    /// <summary>True for a known command and for an unknown "/word": the line is handled in the chat and not sent to the agent.</summary>
    public bool IsCommand => Kind != AgentSlashKind.Prompt && Kind != AgentSlashKind.Empty;
}

/// <summary>
/// Pure parser of the chat input box. A line starting with "/" is a command (<c>/clear /check /suggest /model /mode /cancel /help</c>); any other
/// "/word" is an unknown command. A line whose first token holds a second "/" (for example "/data/x.png please") is a path, hence a normal prompt.
/// Leading and trailing spaces are ignored and the command word is case-insensitive.
/// </summary>
public static class AgentSlashCommands
{
    /// <summary>Parses one input line.</summary>
    public static AgentSlashCommand Parse(string input)
    {
        string text = (input ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            return new AgentSlashCommand(AgentSlashKind.Empty, string.Empty, string.Empty);
        }

        if (text[0] != '/')
        {
            return new AgentSlashCommand(AgentSlashKind.Prompt, string.Empty, text);
        }

        int end = 1;
        while (end < text.Length && !char.IsWhiteSpace(text[end]))
        {
            end++;
        }

        string word = text.Substring(1, end - 1);
        if (word.IndexOf('/') >= 0)
        {
            return new AgentSlashCommand(AgentSlashKind.Prompt, string.Empty, text);
        }

        string name = word.ToLowerInvariant();
        string args = text.Substring(end).Trim();
        return new AgentSlashCommand(KindOf(name), name, args);
    }

    private static AgentSlashKind KindOf(string name)
    {
        switch (name)
        {
            case "clear": return AgentSlashKind.Clear;
            case "check": return AgentSlashKind.Check;
            case "suggest": return AgentSlashKind.Suggest;
            case "model": return AgentSlashKind.Model;
            case "mode": return AgentSlashKind.Mode;
            case "cancel": return AgentSlashKind.Cancel;
            case "help": return AgentSlashKind.Help;
            case "forget": return AgentSlashKind.Forget;
            default: return AgentSlashKind.Unknown;
        }
    }

    /// <summary>Reads the argument of <c>/mode</c>: <c>current</c>, <c>all</c> or <c>remaining</c> (case-insensitive). Anything else is false.</summary>
    public static bool TryParseMode(string args, out string key)
    {
        string text = (args ?? string.Empty).Trim().ToLowerInvariant();
        if (text == "current" || text == "all" || text == "remaining")
        {
            key = text;
            return true;
        }

        key = string.Empty;
        return false;
    }

    /// <summary>
    /// The rule for <c>/clear</c>: the history is wiped only while no run or batch is active (a running run still appends to the file).
    /// </summary>
    public static bool CanClearHistory(bool isRunning, bool isBatchRunning)
    {
        return !isRunning && !isBatchRunning;
    }
}
