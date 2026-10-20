namespace EasyLabel.Commands;

[Flags]
public enum CtxFlags
{
    None = 0,
    ProjectOpen = 1 << 0,
    ImageSelected = 1 << 1,
    ShapeSelected = 1 << 2,
    Busy = 1 << 3,
    CameraLive = 1 << 4,
    OnlineRunning = 1 << 5,
    NotBusy = 1 << 6
}

public static class CommandModes
{
    public const string Simple = "simple";
    public const string Advanced = "advanced";
    public const string Online = "online";
    public const string OnlineRunning = "online-running";

    public static bool IsValid(string? mode) =>
        mode is Simple or Advanced or Online or OnlineRunning;
}

public interface ICommandContext
{
    CtxFlags Flags { get; }
    string Mode { get; }
}

public sealed record CommandContext(CtxFlags Flags, string Mode = CommandModes.Simple) : ICommandContext
{
    public CommandContext(CtxFlags flags, RunMode mode) : this(flags, RunModeNames.ToMode(mode)) { }

    public RunMode RunMode { get { return RunModeNames.Parse(Mode); } }
}

internal static class CommandPreconditions
{
    private const CtxFlags AllFlags = CtxFlags.ProjectOpen | CtxFlags.ImageSelected |
        CtxFlags.ShapeSelected | CtxFlags.Busy | CtxFlags.CameraLive |
        CtxFlags.OnlineRunning | CtxFlags.NotBusy;

    internal static void ValidateFlags(CtxFlags flags)
    {
        if ((flags & ~AllFlags) != 0 ||
            (flags & (CtxFlags.Busy | CtxFlags.NotBusy)) == (CtxFlags.Busy | CtxFlags.NotBusy))
            throw new ArgumentException("Unknown or contradictory context flags.", nameof(flags));
    }

    internal static string? Rejection(CommandDescriptor descriptor, ICommandContext context)
    {
        if (!CommandModes.IsValid(context.Mode) || !descriptor.Modes.Contains(context.Mode, StringComparer.Ordinal))
            return "MODE_BLOCKED";

        CtxFlags required = descriptor.When & ~CtxFlags.NotBusy;
        if ((context.Flags & required) != required ||
            ((descriptor.When & CtxFlags.NotBusy) != 0 && (context.Flags & CtxFlags.Busy) != 0))
            return "PRECONDITION_FAILED";
        return StateRejection(descriptor, context);
    }

    [return: System.Diagnostics.CodeAnalysis.MaybeNull]
    internal static string StateRejection(CommandDescriptor descriptor, ICommandContext context)
    {
        bool running = context.Mode == CommandModes.OnlineRunning ||
            (context.Flags & CtxFlags.OnlineRunning) != 0;
        if (descriptor.Id.Value == "UI.RUNMODE.SET" &&
            (running || (context.Flags & CtxFlags.Busy) != 0))
            return "PRECONDITION_FAILED";

        if (descriptor.Id.Value == "ONL.START" && context.Mode != CommandModes.Online)
            return "MODE_BLOCKED";

        bool explicitlyRunning = descriptor.Modes.Contains(CommandModes.OnlineRunning, StringComparer.Ordinal);
        if (running && descriptor.Risk >= CommandRisk.R2 && !explicitlyRunning)
            return "ONLINE_RUNNING_BLOCKED";

        // The reserved start/endpoint commands must be reachable for idle Online setup.
        // Keep CC-1's high-risk restriction for all other commands.
        if ((context.Mode == CommandModes.Online || running) && descriptor.Risk >= CommandRisk.R3 &&
            !(running && explicitlyRunning) && descriptor.Id.Value != "ONL.STOP" &&
            descriptor.Id.Value != "ONL.START" && descriptor.Id.Value != "ONL.SET.ENDPOINT")
            return "ONLINE_RISK_BLOCKED";

        return null;
    }
}
