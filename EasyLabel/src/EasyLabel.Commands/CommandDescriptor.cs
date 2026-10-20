using EasyLabel.Commands.Schema;

namespace EasyLabel.Commands;

public enum CommandKind { Cmd, Set, Tol }
public enum CommandCost { C0, C1, C2, C3 }
public enum CommandRisk { R0, R1, R2, R3, R4 }

public sealed class CommandDescriptor
{
    public CommandDescriptor(
        CommandId id,
        CommandKind kind = CommandKind.Cmd,
        CommandArgsSchema? argsSchema = null,
        CtxFlags when = CtxFlags.None,
        CommandCost cost = CommandCost.C0,
        CommandRisk risk = CommandRisk.R0,
        IEnumerable<string>? modes = null,
        IEnumerable<string>? aliases = null,
        bool undo = false)
    {
        if (!CommandId.IsValid(id.Value))
            throw new ArgumentException("A valid command ID is required.", nameof(id));
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        if (!Enum.IsDefined(cost)) throw new ArgumentOutOfRangeException(nameof(cost));
        if (!Enum.IsDefined(risk)) throw new ArgumentOutOfRangeException(nameof(risk));
        CommandPreconditions.ValidateFlags(when);

        string[] allowedModes = (modes ?? [CommandModes.Simple, CommandModes.Advanced]).Distinct(StringComparer.Ordinal).ToArray();
        if (allowedModes.Length == 0 || allowedModes.Any(mode => !CommandModes.IsValid(mode)))
            throw new ArgumentException("Modes must contain supported command mode strings.", nameof(modes));
        string[] aliasList = (aliases ?? []).ToArray();
        if (aliasList.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Aliases must not be empty.", nameof(aliases));

        Id = id;
        Kind = kind;
        ArgsSchema = argsSchema ?? CommandArgsSchema.Empty;
        When = when;
        Cost = cost;
        Risk = risk;
        Modes = Array.AsReadOnly(allowedModes);
        Aliases = Array.AsReadOnly(aliasList);
        Undo = undo;
    }

    public CommandId Id { get; }
    public CommandKind Kind { get; }
    public CommandArgsSchema ArgsSchema { get; }
    public CtxFlags When { get; }
    public CommandCost Cost { get; }
    public CommandRisk Risk { get; }
    public IReadOnlyList<string> Modes { get; }
    public IReadOnlyList<string> Aliases { get; }
    public bool Undo { get; }
    public string NameKey => $"cmd.{Id}.name";
    public string DescriptionKey => $"cmd.{Id}.desc";
}
