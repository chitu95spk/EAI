using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using EasyLabel.Commands;
using EasyLabel.Commands.Discovery;
using EasyLabel.Commands.Results;

namespace EasyAI.Desktop.Commands
{
    public sealed class RunModeHandlers
    {
        private readonly ICommandEndpoint _setMode;

        public RunModeHandlers(IViewModelCommandSource source, ICommandDispatcher dispatcher)
        {
            _setMode = new RunModeEndpoint(new ViewModelCommandEndpoint("UI.MODE.SET", source, dispatcher));
        }

        [Command("UI.RUNMODE.SET", Cost = CommandCost.C0, Risk = CommandRisk.R1,
            Modes = new[] { CommandModes.Simple, CommandModes.Advanced, CommandModes.Online },
            Args = "{\"type\":\"object\",\"properties\":{\"mode\":{\"enum\":[\"simple\",\"advanced\",\"online\"]}},\"required\":[\"mode\"],\"additionalProperties\":false}")]
        public ICommandEndpoint SetRunMode { get { return _setMode; } }

        private sealed class RunModeEndpoint : ICommandEndpoint
        {
            private readonly ICommandEndpoint _existing;
            public RunModeEndpoint(ICommandEndpoint existing) { _existing = existing; }

            private static bool Locked(ICommandContext context)
            {
                return (context.Flags & (CtxFlags.Busy | CtxFlags.OnlineRunning)) != 0 ||
                    context.Mode == CommandModes.OnlineRunning;
            }

            public bool CanExecute(ICommandContext context, JsonElement args)
            {
                return !Locked(context) &&
                    (args.GetProperty("mode").GetString() == CommandModes.Online || _existing.CanExecute(context, args));
            }

            public Task<CommandResult> ExecuteAsync(ICommandContext context, JsonElement args, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Locked(context))
                    return Task.FromResult(CommandResult.Failure("PRECONDITION_FAILED", "cmd.err.precondition"));
                if (args.GetProperty("mode").GetString() == CommandModes.Online)
                    return Task.FromResult(CommandResult.Failure("NOT_AVAILABLE_YET", "cmd.err.not_available_yet"));
                return _existing.ExecuteAsync(context, args, cancellationToken);
            }
        }
    }
}
