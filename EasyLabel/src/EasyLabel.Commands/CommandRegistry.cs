using System.Text.Json;
using EasyLabel.Commands.Results;

namespace EasyLabel.Commands;

public delegate Task<CommandResult> CommandHandler(
    ICommandContext context, JsonElement args, CancellationToken cancellationToken);

public sealed class CommandRegistry
{
    private readonly Dictionary<CommandId, Registration> _commands = new();

    public void Register(CommandDescriptor descriptor, CommandHandler handler)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(handler);
        if (!_commands.TryAdd(descriptor.Id, new Registration(descriptor, handler)))
            throw new InvalidOperationException($"Command '{descriptor.Id}' is already registered.");
    }

    // Catalog slicing and invocation use the same context/mode preconditions.
    // Origin, cap and confirmation policy remains the invoker's responsibility.
    public IReadOnlyList<CommandDescriptor> Query(ICommandContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return Array.AsReadOnly(_commands.Values
            .Select(registration => registration.Descriptor)
            .Where(descriptor => CommandPreconditions.Rejection(descriptor, context) is null)
            .OrderBy(descriptor => descriptor.Id.Value, StringComparer.Ordinal)
            .ToArray());
    }

    internal bool TryGet(CommandId id, out Registration? registration) =>
        _commands.TryGetValue(id, out registration);

    internal sealed record Registration(CommandDescriptor Descriptor, CommandHandler Handler);
}
