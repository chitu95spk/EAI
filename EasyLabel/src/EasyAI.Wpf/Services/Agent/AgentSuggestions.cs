using System.Globalization;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace EasyAI.Desktop.Services.Agent;

/// <summary>Outcome of <see cref="AgentSuggestions.Parse"/>: the prompts, or an error key when there is no usable list.</summary>
public sealed class AgentSuggestionParse
{
    public AgentSuggestionParse(IReadOnlyList<string> prompts, string? errorKey)
    {
        Prompts = prompts;
        ErrorKey = errorKey;
    }

    /// <summary>1 to <see cref="AgentSuggestions.MaxCount"/> cleaned prompts; empty on failure.</summary>
    public IReadOnlyList<string> Prompts { get; }

    /// <summary>Null on success; otherwise <see cref="AgentSuggestions.ErrorNoJson"/> or <see cref="AgentSuggestions.ErrorNoPrompts"/>.</summary>
    public string? ErrorKey { get; }

    public bool Ok => ErrorKey == null;
}

/// <summary>
/// The "Suggest prompt" run of the Agent tab: the instruction text sent to Claude Code and the tolerant reader of its answer
/// (<c>{"prompts":["...", ...]}</c>, possibly inside prose or a code fence).
/// </summary>
public static class AgentSuggestions
{
    public const int MaxCount = 5;

    public const string ErrorNoJson = "NoJson";
    public const string ErrorNoPrompts = "NoPrompts";

    /// <summary>System prompt of a suggest run. It replaces the label skill pack, so a suggest run never labels.</summary>
    public const string SystemPrompt =
        "You help a person write instructions for an image labelling agent, through the easyai MCP tools. "
        + "You may call only get_image_info and get_crop. Never call any other tool. "
        + "Call get_crop exactly once, with the whole image and max_side 1024, then look at the picture. "
        + "Answer with one JSON object and nothing else: {\"prompts\":[\"...\", \"...\"]}. "
        + "Give 3 to 5 short prompts. Each prompt tells a labelling agent which boxes or defects to mark on THIS image, "
        + "using the class names of the project. Do not write coordinates or pixel numbers. "
        + "Do not add explanations before or after the JSON.";

    /// <summary>
    /// The user message: language of the prompts, the length limit and the class list. <paramref name="shapeTypes"/> (LabelMe shape types,
    /// e.g. <c>oriented_rectangle</c>) and <paramref name="labels"/> narrow the prompts to those shapes / classes; null or empty = no limit.
    /// </summary>
    public static string BuildUserPrompt(string languageCode, IReadOnlyList<string> classNames, IReadOnlyList<string>? shapeTypes = null, IReadOnlyList<string>? labels = null, string? hint = null)
    {
        string language = string.Equals(languageCode, "vi", StringComparison.OrdinalIgnoreCase) ? "Vietnamese" : "English";
        string classes = string.Join(", ", classNames.Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => "\"" + c.Trim() + "\""));
        StringBuilder text = new StringBuilder();
        text.Append("Suggest 3 to 5 prompts for the current image. Write every prompt in ").Append(language).Append(". ");
        text.Append("Each prompt must be at most ").Append(AgentCommandLine.MaxPromptLength.ToString(CultureInfo.InvariantCulture)).Append(" characters, and short. ");
        if (!string.IsNullOrWhiteSpace(hint))
        {
            text.Append(hint!.Trim()).Append(' ');
        }

        text.Append("Class list: ").Append(classes.Length > 0 ? classes : "(none)").Append(". ");
        string shapes = shapeTypes == null ? string.Empty : string.Join(", ", shapeTypes.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()));
        if (shapes.Length > 0)
        {
            text.Append("Every prompt must ask for these shape types only: ").Append(shapes).Append(". ");
        }

        string only = labels == null ? string.Empty : string.Join(", ", labels.Where(l => !string.IsNullOrWhiteSpace(l)).Select(l => "\"" + l.Trim() + "\""));
        if (only.Length > 0)
        {
            text.Append("Every prompt must mention only these classes: ").Append(only).Append(". ");
        }

        text.Append("Return only {\"prompts\":[...]}.");
        return text.ToString();
    }

    /// <summary>
    /// Reads the prompts from the agent's closing message. Accepts the bare object, an object inside prose or a <c>```json</c> fence.
    /// Entries are trimmed; empty, duplicate (case-insensitive), non-string and over-long entries are dropped; more than
    /// <see cref="MaxCount"/> keep the first ones; none left is a failure.
    /// </summary>
    public static AgentSuggestionParse Parse(string? text)
    {
        string source = text ?? string.Empty;
        JArray? array = FindPrompts(source);
        if (array == null)
        {
            return new AgentSuggestionParse(Array.Empty<string>(), ErrorNoJson);
        }

        List<string> prompts = new List<string>();
        HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (JToken item in array)
        {
            if (item.Type != JTokenType.String)
            {
                continue;
            }

            string prompt = (item.Value<string>() ?? string.Empty).Trim();
            if (prompt.Length == 0 || prompt.Length > AgentCommandLine.MaxPromptLength || !seen.Add(prompt))
            {
                continue;
            }

            prompts.Add(prompt);
            if (prompts.Count >= MaxCount)
            {
                break;
            }
        }

        return prompts.Count == 0
            ? new AgentSuggestionParse(Array.Empty<string>(), ErrorNoPrompts)
            : new AgentSuggestionParse(prompts, null);
    }

    private static JArray? FindPrompts(string source)
    {
        int start = source.IndexOf('{');
        while (start >= 0)
        {
            try
            {
                using JsonTextReader reader = new JsonTextReader(new StringReader(source.Substring(start)));
                JObject candidate = JObject.Load(reader);
                if (candidate["prompts"] is JArray array)
                {
                    return array;
                }
            }
            catch (JsonException)
            {
                // Not an object that starts here: try the next brace.
            }

            start = source.IndexOf('{', start + 1);
        }

        return null;
    }
}
