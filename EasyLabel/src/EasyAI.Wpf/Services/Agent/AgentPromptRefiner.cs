using System.Globalization;
using System.Text;

namespace EasyAI.Desktop.Services.Agent;

/// <summary>What the prompt sub-agent answered: the command for the labelling agent (English) and a short explanation for the person.</summary>
public sealed class AgentPromptRefineResult
{
    public AgentPromptRefineResult(string refined, string summary)
    {
        Refined = refined;
        Summary = summary;
    }

    /// <summary>The rewritten command, ready to send to the main agent.</summary>
    public string Refined { get; }

    /// <summary>What the main agent will do, in the language of the person; may be empty.</summary>
    public string Summary { get; }
}

/// <summary>
/// The "prompt sub-agent": a short text-only run that turns what the person typed (any language) into a clear command for the labelling agent,
/// translates it to English and explains it back in the person's language. No I/O, so it is unit tested.
/// </summary>
public static class AgentPromptRefiner
{
    public const string RefinedMarker = "REFINED:";
    public const string SummaryMarker = "SUMMARY:";

    /// <summary>System prompt of a refine run. It replaces the label skill pack, so a refine run never labels and never calls a tool.</summary>
    public const string SystemPrompt =
        "You rewrite instructions for an image labelling agent. You never label anything and you never call a tool. "
        + "Take the person's request (it may be Vietnamese, English or mixed, informal or incomplete) and write ONE clear command "
        + "for the labelling agent: say which defects or objects to mark, which class names to use, the shape type when the person gave one, "
        + "and any limits (count, size, area). Keep every fact the person gave; do not invent classes, numbers or areas. "
        + "Write the command in English, short and direct. "
        + "Then write a short explanation for the person, in the language they asked for, that says in plain words what the agent will do. "
        + "Answer in exactly two parts and nothing else: a line that starts with REFINED: and then the English command, "
        + "and a line that starts with SUMMARY: and then the explanation.";

    /// <summary>The user message: the person's text, the language of the summary and the project classes.</summary>
    public static string BuildUserPrompt(string prompt, string languageCode, IReadOnlyList<string> classNames, string? hint = null)
    {
        string language = string.Equals(languageCode, "vi", StringComparison.OrdinalIgnoreCase) ? "Vietnamese" : "English";
        string classes = string.Join(", ", classNames.Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => "\"" + c.Trim() + "\""));
        StringBuilder text = new StringBuilder();
        text.Append("Rewrite this request for the labelling agent. Write the SUMMARY in ").Append(language).Append(". ");
        if (classes.Length > 0)
        {
            text.Append("The project classes are ").Append(classes).Append(". ");
        }

        text.Append("The command must be at most ").Append(AgentCommandLine.MaxPromptLength.ToString(CultureInfo.InvariantCulture)).Append(" characters. ");
        if (!string.IsNullOrWhiteSpace(hint))
        {
            text.Append(hint!.Trim()).Append(' ');
        }

        text.Append("Request: ").Append(prompt.Trim());
        return text.ToString();
    }

    /// <summary>Reads the two parts of the answer. Null when there is no usable command (the caller then keeps the original prompt).</summary>
    public static AgentPromptRefineResult? Parse(string? answer)
    {
        if (string.IsNullOrWhiteSpace(answer))
        {
            return null;
        }

        string text = answer!.Replace("\r\n", "\n");
        int refinedAt = text.IndexOf(RefinedMarker, StringComparison.OrdinalIgnoreCase);
        if (refinedAt < 0)
        {
            return null;
        }

        int summaryAt = text.IndexOf(SummaryMarker, refinedAt, StringComparison.OrdinalIgnoreCase);
        string refined = (summaryAt < 0 ? text.Substring(refinedAt + RefinedMarker.Length) : text.Substring(refinedAt + RefinedMarker.Length, summaryAt - refinedAt - RefinedMarker.Length)).Trim();
        string summary = summaryAt < 0 ? string.Empty : text.Substring(summaryAt + SummaryMarker.Length).Trim();
        refined = Clean(refined);
        summary = Clean(summary);
        if (refined.Length == 0 || refined.Length > AgentCommandLine.MaxPromptLength)
        {
            return null;
        }

        return new AgentPromptRefineResult(refined, summary);
    }

    // Drops code fences and wrapping quotes the model may add around a part.
    private static string Clean(string part)
    {
        string text = string.Join("\n", part.Split('\n').Where(line => !line.TrimStart().StartsWith("```", StringComparison.Ordinal))).Trim();

        if (text.Length >= 2 && ((text[0] == '"' && text[text.Length - 1] == '"') || (text[0] == '“' && text[text.Length - 1] == '”')))
        {
            text = text.Substring(1, text.Length - 2).Trim();
        }

        return text;
    }
}
