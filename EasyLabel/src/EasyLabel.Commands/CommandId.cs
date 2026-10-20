using System.Text.RegularExpressions;

namespace EasyLabel.Commands;

public readonly record struct CommandId
{
    private static readonly Regex Grammar = new(
        @"\A[A-Z][A-Z0-9_]*(?:\.[A-Z][A-Z0-9_]*){1,4}\z",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    public CommandId(string value)
    {
        if (!IsValid(value))
            throw new ArgumentException("Command IDs require 2–5 uppercase ASCII segments and at most 24 characters.", nameof(value));
        Value = value;
    }

    public string Value { get; }

    // Verb semantics belong to the catalog: documented settings/tools and
    // CAN.TOOL.RECT do not use the action verb vocabulary in segment two.
    public static bool IsValid(string? value) =>
        value is { Length: >= 3 and <= 24 } && Grammar.IsMatch(value);

    public static CommandId Parse(string value) => new(value);

    public static bool TryParse(string? value, out CommandId id)
    {
        if (IsValid(value))
        {
            id = new CommandId(value!);
            return true;
        }
        id = default;
        return false;
    }

    public override string ToString() => Value ?? string.Empty;
}
