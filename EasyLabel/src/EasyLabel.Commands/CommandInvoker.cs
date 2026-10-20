using System.Diagnostics;
using System.Text.Json;
using EasyLabel.Commands.Policy;
using EasyLabel.Commands.Results;

namespace EasyLabel.Commands;

public sealed class CommandInvoker
{
    private readonly CommandRegistry _registry;
    private readonly CommandPolicyMatrix _policy;

    public CommandInvoker(CommandRegistry registry, CommandPolicyMatrix? policy = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _policy = policy ?? new();
    }

    public async Task<CommandResult> ExecuteAsync(CommandId id, JsonElement args,
        ICommandContext context, CommandOrigin origin = CommandOrigin.UserDirect,
        CommandPolicyEvidence? evidence = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        long started = Stopwatch.GetTimestamp();
        CommandResult Finish(CommandResult result) => result with
        {
            Ms = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds
        };

        if (!_registry.TryGet(id, out CommandRegistry.Registration? registration) || registration is null)
            return Finish(CommandResult.Failure("COMMAND_NOT_FOUND", "cmd.err.not_found"));

        CommandDescriptor descriptor = registration.Descriptor;
        if (!descriptor.ArgsSchema.TryValidate(args, out JsonElement validated, out string? error))
            return Finish(CommandResult.Failure("INVALID_ARGS", "cmd.err.args",
                JsonSerializer.SerializeToElement(new { detail = error })));

        // Snapshot the host state once so gating, policy and handler agree on it.
        ICommandContext snapshot = new CommandContext(context.Flags, context.Mode);
        string? rejection = CommandPreconditions.Rejection(descriptor, snapshot);
        if (rejection is not null)
            return Finish(CommandResult.Failure(rejection,
                rejection == "MODE_BLOCKED" ? "cmd.err.mode" : "cmd.err.precondition"));

        CommandPolicyDecision decision = _policy.Evaluate(descriptor, snapshot, origin, evidence);
        if (!decision.Allowed)
            return Finish(CommandResult.Failure(decision.Code, "cmd.err.policy"));

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            CommandResult result = await registration.Handler(snapshot, validated, cancellationToken).ConfigureAwait(false);
            return Finish(result ?? CommandResult.Failure("HANDLER_FAILED", "cmd.err.handler"));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Finish(CommandResult.Failure("CANCELLED", "cmd.err.cancelled"));
        }
        catch (Exception)
        {
            return Finish(CommandResult.Failure("HANDLER_FAILED", "cmd.err.handler"));
        }
    }
}
