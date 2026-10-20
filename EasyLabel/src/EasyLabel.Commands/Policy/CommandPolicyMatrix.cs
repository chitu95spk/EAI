namespace EasyLabel.Commands.Policy;

public enum CommandOrigin { UserDirect, Agent, LabellerAgent }

// Evidence is supplied by trusted hosts, never read from command arguments.
// No dialog, cap service or application state is wired in CC-1.
public sealed record CommandPolicyEvidence(
    bool UndoAvailable = false,
    bool CapsChecked = false,
    bool WithinCaps = false,
    bool Confirmed = false);

public sealed record CommandPolicyDecision(bool Allowed, string Code)
{
    internal static CommandPolicyDecision Permit { get; } = new(true, "OK");
    internal static CommandPolicyDecision Deny(string code) => new(false, code);
}

public sealed class CommandPolicyMatrix
{
    private readonly HashSet<CommandId> _labellerAllowList;

    public CommandPolicyMatrix(IEnumerable<CommandId>? labellerAllowList = null) =>
        _labellerAllowList = new(labellerAllowList ?? []);

    public CommandPolicyDecision Evaluate(CommandDescriptor descriptor, ICommandContext context,
        CommandOrigin origin, CommandPolicyEvidence? evidence = null)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(context);
        evidence ??= new();
        if (!Enum.IsDefined(origin)) return CommandPolicyDecision.Deny("ORIGIN_BLOCKED");

        if ((descriptor.Kind == CommandKind.Tol && origin != CommandOrigin.LabellerAgent) ||
            (origin == CommandOrigin.LabellerAgent &&
                (descriptor.Kind != CommandKind.Tol || !_labellerAllowList.Contains(descriptor.Id))))
            return CommandPolicyDecision.Deny("ORIGIN_BLOCKED");

        var stateRejection = CommandPreconditions.StateRejection(descriptor, context);
        if (stateRejection != null) return CommandPolicyDecision.Deny(stateRejection);

        // One origin/risk table, shared by every caller of the invoker.
        return (origin, descriptor.Risk) switch
        {
            (CommandOrigin.UserDirect, CommandRisk.R4) => Confirm(evidence),
            (CommandOrigin.UserDirect, _) => CommandPolicyDecision.Permit,
            (_, CommandRisk.R0 or CommandRisk.R1) => CommandPolicyDecision.Permit,
            (_, CommandRisk.R2) => descriptor.Undo && evidence.UndoAvailable
                ? CommandPolicyDecision.Permit : CommandPolicyDecision.Deny("UNDO_REQUIRED"),
            (_, CommandRisk.R3) => !evidence.CapsChecked
                ? CommandPolicyDecision.Deny("CAP_CHECK_REQUIRED")
                : !evidence.WithinCaps ? CommandPolicyDecision.Deny("CAP_EXCEEDED") : Confirm(evidence),
            (_, CommandRisk.R4) => Confirm(evidence),
            _ => CommandPolicyDecision.Deny("POLICY_BLOCKED")
        };
    }

    private static CommandPolicyDecision Confirm(CommandPolicyEvidence evidence) =>
        evidence.Confirmed ? CommandPolicyDecision.Permit : CommandPolicyDecision.Deny("CONFIRMATION_REQUIRED");
}
