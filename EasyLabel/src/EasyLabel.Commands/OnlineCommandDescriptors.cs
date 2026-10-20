using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using EasyLabel.Commands.Discovery;
using EasyLabel.Commands.Results;

namespace EasyLabel.Commands
{
    /// <summary>CC-12 catalog reservations only; no Online runtime or configuration changes.</summary>
    public sealed class OnlineCommandDescriptors
    {
        private sealed class UnimplementedEndpoint : ICommandEndpoint
        {
            public bool CanExecute(ICommandContext context, JsonElement args) { return true; }
            public Task<CommandResult> ExecuteAsync(ICommandContext context, JsonElement args, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(CommandResult.Failure("NOT_IMPLEMENTED", "cmd.err.not_implemented"));
            }
        }

        private static ICommandEndpoint Reserved() { return new UnimplementedEndpoint(); }

        private readonly IOnlineControl? control;

        public OnlineCommandDescriptors() { }

        /// <summary>CC-16: ONL.STOP, ONL.STATUS and ONL.THRESHOLD.SET act on <paramref name="control"/>; the rest stay reserved.</summary>
        public OnlineCommandDescriptors(IOnlineControl? control) { this.control = control; }

        [Command("ONL.START", Cost = CommandCost.C0, Risk = CommandRisk.R3,
            Modes = new[] { CommandModes.Online },
            Args = "{\"type\":\"object\",\"properties\":{\"recipe\":{\"type\":\"string\"}},\"additionalProperties\":false}")]
        public ICommandEndpoint Start { get { return Reserved(); } }

        [Command("ONL.STOP", Cost = CommandCost.C0, Risk = CommandRisk.R1,
            Modes = new[] { CommandModes.Simple, CommandModes.Advanced, CommandModes.Online, CommandModes.OnlineRunning })]
        public ICommandEndpoint Stop { get { return control == null ? Reserved() : OnlineControlEndpoint.Stop(control); } }

        [Command("ONL.STATUS", Cost = CommandCost.C0, Risk = CommandRisk.R0,
            Modes = new[] { CommandModes.Simple, CommandModes.Advanced, CommandModes.Online })]
        public ICommandEndpoint Status { get { return control == null ? Reserved() : OnlineControlEndpoint.Status(control); } }

        [Command("ONL.SET.ENDPOINT", Cost = CommandCost.C0, Risk = CommandRisk.R3,
            Modes = new[] { CommandModes.Simple, CommandModes.Advanced, CommandModes.Online },
            Args = "{\"type\":\"object\",\"properties\":{\"kind\":{\"enum\":[\"local\",\"tcp\",\"http\",\"grpc\"]},\"host\":{\"type\":\"string\"},\"port\":{\"type\":\"integer\"},\"auth\":{\"type\":\"string\"}},\"required\":[\"kind\",\"host\",\"port\"],\"additionalProperties\":false}")]
        public ICommandEndpoint Endpoint { get { return Reserved(); } }

        [Command("ONL.SET.SOURCE", Cost = CommandCost.C0, Risk = CommandRisk.R2,
            Modes = new[] { CommandModes.Simple, CommandModes.Advanced, CommandModes.Online },
            Args = "{\"type\":\"object\",\"properties\":{\"kind\":{\"enum\":[\"camera\",\"folder\",\"socket\"]},\"id\":{\"type\":\"string\"}},\"required\":[\"kind\",\"id\"],\"additionalProperties\":false}")]
        public ICommandEndpoint Source { get { return Reserved(); } }

        [Command("ONL.SET.BUDGET", Cost = CommandCost.C0, Risk = CommandRisk.R1,
            Modes = new[] { CommandModes.Simple, CommandModes.Advanced, CommandModes.Online },
            Args = "{\"type\":\"object\",\"properties\":{\"ms\":{\"type\":\"integer\",\"minimum\":10,\"maximum\":500}},\"required\":[\"ms\"],\"additionalProperties\":false}")]
        public ICommandEndpoint Budget { get { return Reserved(); } }

        [Command("ONL.SET.OVERLOAD", Cost = CommandCost.C0, Risk = CommandRisk.R1,
            Modes = new[] { CommandModes.Simple, CommandModes.Advanced, CommandModes.Online },
            Args = "{\"type\":\"object\",\"properties\":{\"policy\":{\"enum\":[\"drop-oldest\",\"drop-newest\",\"skip-nth\"]}},\"required\":[\"policy\"],\"additionalProperties\":false}")]
        public ICommandEndpoint Overload { get { return Reserved(); } }

        [Command("ONL.SET.WIRE", Cost = CommandCost.C0, Risk = CommandRisk.R1,
            Modes = new[] { CommandModes.Simple, CommandModes.Advanced, CommandModes.Online },
            Args = "{\"type\":\"object\",\"properties\":{\"codec\":{\"enum\":[\"jpeg\",\"png\",\"raw\"]},\"quality\":{\"type\":\"number\"},\"roi\":{\"type\":\"object\",\"additionalProperties\":true},\"scale\":{\"type\":\"number\"}},\"required\":[\"codec\"],\"additionalProperties\":false}")]
        public ICommandEndpoint Wire { get { return Reserved(); } }

        [Command("ONL.THRESHOLD.SET", Cost = CommandCost.C0, Risk = CommandRisk.R2,
            Modes = new[] { CommandModes.Simple, CommandModes.Advanced, CommandModes.Online, CommandModes.OnlineRunning },
            Args = "{\"type\":\"object\",\"properties\":{\"class\":{\"type\":\"string\"},\"score\":{\"type\":\"number\"},\"nms\":{\"type\":\"number\"}},\"required\":[\"score\",\"nms\"],\"additionalProperties\":false}")]
        public ICommandEndpoint Threshold { get { return control == null ? Reserved() : OnlineControlEndpoint.Threshold(control); } }

        [Command("ONL.RECIPE.FREEZE", Cost = CommandCost.C0, Risk = CommandRisk.R2,
            Modes = new[] { CommandModes.Simple, CommandModes.Advanced, CommandModes.Online },
            Args = "{\"type\":\"object\",\"properties\":{\"from\":{\"enum\":[\"run\",\"current\"]},\"name\":{\"type\":\"string\"}},\"required\":[\"from\",\"name\"],\"additionalProperties\":false}")]
        public ICommandEndpoint Freeze { get { return Reserved(); } }

        [Command("ONL.RECIPE.LOAD", Cost = CommandCost.C0, Risk = CommandRisk.R2,
            Modes = new[] { CommandModes.Simple, CommandModes.Advanced, CommandModes.Online },
            Args = "{\"type\":\"object\",\"properties\":{\"id\":{\"type\":\"string\"}},\"required\":[\"id\"],\"additionalProperties\":false}")]
        public ICommandEndpoint Load { get { return Reserved(); } }

        [Command("ONL.RECIPE.SWAP", Cost = CommandCost.C0, Risk = CommandRisk.R2,
            Modes = new[] { CommandModes.Simple, CommandModes.Advanced, CommandModes.Online, CommandModes.OnlineRunning },
            Args = "{\"type\":\"object\",\"properties\":{\"id\":{\"type\":\"string\"}},\"required\":[\"id\"],\"additionalProperties\":false}")]
        public ICommandEndpoint Swap { get { return Reserved(); } }

        [Command("ONL.RECIPE.LIST", Cost = CommandCost.C0, Risk = CommandRisk.R2,
            Modes = new[] { CommandModes.Simple, CommandModes.Advanced, CommandModes.Online },
            Args = "{\"type\":\"object\",\"properties\":{\"id\":{\"type\":\"string\"}},\"required\":[\"id\"],\"additionalProperties\":false}")]
        public ICommandEndpoint List { get { return Reserved(); } }

        [Command("ONL.LOG.SET", Cost = CommandCost.C0, Risk = CommandRisk.R2,
            Modes = new[] { CommandModes.Simple, CommandModes.Advanced, CommandModes.Online },
            Args = "{\"type\":\"object\",\"properties\":{\"ngImages\":{\"type\":\"boolean\"},\"dir\":{\"type\":\"string\"},\"maxGB\":{\"type\":\"number\"}},\"required\":[\"ngImages\",\"dir\",\"maxGB\"],\"additionalProperties\":false}")]
        public ICommandEndpoint Log { get { return Reserved(); } }

        /// <summary>Composes CC-2 wrappers and CC-12 descriptors without altering wrapped commands.</summary>
        public static IReadOnlyList<DiscoveredCommand> Discover(params object[] handlers) { return Discover((IOnlineControl?)null, handlers); }

        public static IReadOnlyList<DiscoveredCommand> Discover(IOnlineControl? control, params object[] handlers)
        {
            var scanner = new CommandScanner();
            var commands = scanner.Scan(handlers.Concat(new object[] { new OnlineCommandDescriptors(control) }).ToArray());
            return Array.AsReadOnly(commands.Select(command =>
            {
                CommandDescriptor descriptor = command.Descriptor;
                if (descriptor.Id.Value != "INS.RUN" && descriptor.Id.Value != "SYS.SETTINGS.OPEN") return command;
                var onlineRead = new CommandDescriptor(descriptor.Id, descriptor.Kind, descriptor.ArgsSchema,
                    descriptor.When, descriptor.Cost, descriptor.Risk,
                    descriptor.Modes.Concat(new[] { CommandModes.Online }), descriptor.Aliases, descriptor.Undo);
                return new DiscoveredCommand(onlineRead, command.Endpoint, command.Headless);
            }).ToArray());
        }
    }
}
