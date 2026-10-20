using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using EasyAI.Desktop.Commands;
using EasyLabel.Commands.Discovery;
using EasyLabel.Commands.Policy;
using EasyLabel.Commands.Results;
using Xunit;

namespace EasyLabel.Commands.Tests.Invocation
{
    public sealed class RunModeGatingTests
    {
        private sealed class Host : IViewModelCommandSource, ICommandDispatcher
        {
            public bool Enabled = true;
            public int Calls;
            public int Resolutions;
            public string LastId = "";
            public string LastMode = "";
            private readonly ICommand _command;

            public Host() { _command = new RelayCommand<string>(mode => { Calls++; LastMode = mode ?? ""; }, mode => Enabled); }
            public ViewModelCommandTarget Resolve(string id, JsonElement args)
            {
                Resolutions++;
                LastId = id;
                JsonElement mode;
                string parameter = args.TryGetProperty("mode", out mode) ? mode.GetString() ?? "" : "";
                return ViewModelCommandTarget.FromCommand(_command, parameter);
            }
            public T Invoke<T>(Func<T> action) { return action(); }
            public Task<T> InvokeAsync<T>(Func<Task<T>> action, CancellationToken cancellationToken) { return action(); }
        }

        private static JsonElement Args(string json = "{}")
        {
            using (JsonDocument document = JsonDocument.Parse(json)) return document.RootElement.Clone();
        }

        private static readonly Dictionary<string, string> ReservedArgs = new Dictionary<string, string>
        {
            { "ONL.START", "{}" }, { "ONL.STOP", "{}" }, { "ONL.STATUS", "{}" },
            { "ONL.SET.ENDPOINT", "{\"kind\":\"local\",\"host\":\"localhost\",\"port\":1234}" },
            { "ONL.SET.SOURCE", "{\"kind\":\"camera\",\"id\":\"camera-1\"}" },
            { "ONL.SET.BUDGET", "{\"ms\":50}" }, { "ONL.SET.OVERLOAD", "{\"policy\":\"drop-oldest\"}" },
            { "ONL.SET.WIRE", "{\"codec\":\"jpeg\"}" },
            { "ONL.THRESHOLD.SET", "{\"score\":0.5,\"nms\":0.4}" },
            { "ONL.RECIPE.FREEZE", "{\"from\":\"current\",\"name\":\"recipe\"}" },
            { "ONL.RECIPE.LOAD", "{\"id\":\"recipe-1\"}" }, { "ONL.RECIPE.SWAP", "{\"id\":\"recipe-1\"}" },
            { "ONL.RECIPE.LIST", "{\"id\":\"recipe-1\"}" },
            { "ONL.LOG.SET", "{\"ngImages\":true,\"dir\":\"unused\",\"maxGB\":1}" }
        };

        public static IEnumerable<object[]> ReservedMatrix()
        {
            foreach (string id in ReservedArgs.Keys)
                foreach (RunMode mode in new[] { RunMode.Simple, RunMode.Advanced, RunMode.Online })
                    foreach (bool running in new[] { false, true })
                    {
                        string rejection = "NOT_IMPLEMENTED";
                        if (id == "ONL.START" && mode != RunMode.Online) rejection = "MODE_BLOCKED";
                        else if (running && (id == "ONL.START" || id == "ONL.SET.ENDPOINT" ||
                            id == "ONL.SET.SOURCE" || id == "ONL.RECIPE.FREEZE" || id == "ONL.RECIPE.LOAD" ||
                            id == "ONL.RECIPE.LIST" || id == "ONL.LOG.SET")) rejection = "ONLINE_RUNNING_BLOCKED";
                        yield return new object[] { id, mode, running, rejection };
                    }
        }

        [Theory]
        [MemberData(nameof(ReservedMatrix))]
        public async Task ReservedCommandsFollowTheModeAndRunningMatrix(string id, RunMode mode, bool running, string code)
        {
            var scanner = new CommandScanner();
            var catalog = scanner.Scan(new OnlineCommandDescriptors());
            var registry = new CommandRegistry();
            scanner.Register(registry, catalog);
            var context = new CommandContext(running ? CtxFlags.OnlineRunning : CtxFlags.None, mode);
            CommandResult result = await new CommandInvoker(registry).ExecuteAsync(new CommandId(id), Args(ReservedArgs[id]), context);
            Assert.False(result.Ok);
            Assert.Equal(code, result.Code);
            Assert.Equal(code == "NOT_IMPLEMENTED", registry.Query(context).Any(d => d.Id.Value == id));
            Assert.Equal(0, result.Cost.Usd);
            Assert.Equal(0, result.Cost.Turns);
            Assert.Null(result.UndoToken);
        }

        [Fact]
        public void ReservationsMatchSection71CostRiskAndRunningOptIns()
        {
            var riskById = new Dictionary<string, CommandRisk>
            {
                { "ONL.START", CommandRisk.R3 }, { "ONL.STOP", CommandRisk.R1 }, { "ONL.STATUS", CommandRisk.R0 },
                { "ONL.SET.ENDPOINT", CommandRisk.R3 }, { "ONL.SET.SOURCE", CommandRisk.R2 },
                { "ONL.SET.BUDGET", CommandRisk.R1 }, { "ONL.SET.OVERLOAD", CommandRisk.R1 }, { "ONL.SET.WIRE", CommandRisk.R1 },
                { "ONL.THRESHOLD.SET", CommandRisk.R2 }, { "ONL.RECIPE.FREEZE", CommandRisk.R2 },
                { "ONL.RECIPE.LOAD", CommandRisk.R2 }, { "ONL.RECIPE.SWAP", CommandRisk.R2 },
                { "ONL.RECIPE.LIST", CommandRisk.R2 }, { "ONL.LOG.SET", CommandRisk.R2 }
            };
            var commands = new CommandScanner().Scan(new OnlineCommandDescriptors());
            Assert.Equal(riskById.Count, commands.Count);
            foreach (DiscoveredCommand command in commands)
            {
                Assert.Equal(CommandCost.C0, command.Descriptor.Cost);
                Assert.Equal(riskById[command.Descriptor.Id.Value], command.Descriptor.Risk);
                Assert.True(command.Headless);
            }
            Assert.Equal(new[] { "ONL.RECIPE.SWAP", "ONL.STOP", "ONL.THRESHOLD.SET" },
                commands.Where(c => c.Descriptor.Modes.Contains(CommandModes.OnlineRunning)).Select(c => c.Descriptor.Id.Value).ToArray());
        }

        [Theory]
        [InlineData(CommandRisk.R0, false, true)]
        [InlineData(CommandRisk.R1, false, true)]
        [InlineData(CommandRisk.R2, false, false)]
        [InlineData(CommandRisk.R3, false, false)]
        [InlineData(CommandRisk.R4, false, false)]
        [InlineData(CommandRisk.R2, true, true)]
        [InlineData(CommandRisk.R3, true, true)]
        [InlineData(CommandRisk.R4, true, true)]
        public async Task InvokerAndCatalogShareTheRunningRiskGate(CommandRisk risk, bool listed, bool allowed)
        {
            string[] modes = listed ? new[] { CommandModes.Online, CommandModes.OnlineRunning } : new[] { CommandModes.Online };
            var descriptor = new CommandDescriptor(new CommandId("SYS.SHOW"), risk: risk, modes: modes);
            var registry = new CommandRegistry();
            int calls = 0;
            registry.Register(descriptor, (context, args, token) => { calls++; return Task.FromResult(CommandResult.Success()); });
            var state = new CommandContext(CtxFlags.OnlineRunning, RunMode.Online);
            var result = await new CommandInvoker(registry).ExecuteAsync(descriptor.Id, Args(), state,
                evidence: new CommandPolicyEvidence(Confirmed: true));
            Assert.Equal(allowed, result.Ok);
            Assert.Equal(allowed ? 1 : 0, calls);
            Assert.Equal(allowed ? 1 : 0, registry.Query(state).Count);
            if (!allowed) Assert.Equal("ONLINE_RUNNING_BLOCKED", result.Code);
        }

        [Theory]
        [InlineData(CtxFlags.Busy)]
        [InlineData(CtxFlags.OnlineRunning)]
        [InlineData(CtxFlags.Busy | CtxFlags.OnlineRunning)]
        public async Task RunModeSetIsRefusedBeforeTouchingTheViewModel(CtxFlags flags)
        {
            var host = new Host();
            var catalog = new CommandScanner().Scan(new RunModeHandlers(host, host));
            var registry = new CommandRegistry();
            new CommandScanner().Register(registry, catalog);
            foreach (RunMode current in new[] { RunMode.Simple, RunMode.Advanced, RunMode.Online })
                foreach (string target in new[] { "simple", "advanced", "online" })
                {
                    var state = new CommandContext(flags, current);
                    var args = Args("{\"mode\":\"" + target + "\"}");
                    var result = await new CommandInvoker(registry).ExecuteAsync(new CommandId("UI.RUNMODE.SET"), args, state);
                    Assert.Equal("PRECONDITION_FAILED", result.Code);
                    Assert.False(catalog[0].Endpoint.CanExecute(state, args));
                    Assert.Equal("PRECONDITION_FAILED", (await catalog[0].Endpoint.ExecuteAsync(state, args, CancellationToken.None)).Code);
                    Assert.Empty(registry.Query(state));
                }
            Assert.Equal(0, host.Resolutions);
            Assert.Equal(0, host.Calls);
        }

        [Theory]
        [InlineData("simple")]
        [InlineData("advanced")]
        public async Task RunModeSetReusesExistingUiModeCommandAndCanExecute(string mode)
        {
            var host = new Host();
            var catalog = new CommandScanner().Scan(new RunModeHandlers(host, host));
            var registry = new CommandRegistry();
            new CommandScanner().Register(registry, catalog);
            var invoker = new CommandInvoker(registry);
            var args = Args("{\"mode\":\"" + mode + "\"}");
            var state = new CommandContext(CtxFlags.None);
            host.Enabled = false;
            Assert.False(catalog[0].Endpoint.CanExecute(state, args));
            Assert.Equal("PRECONDITION_FAILED", (await invoker.ExecuteAsync(new CommandId("UI.RUNMODE.SET"), args, state)).Code);
            host.Enabled = true;
            Assert.True(catalog[0].Endpoint.CanExecute(state, args));
            Assert.True((await invoker.ExecuteAsync(new CommandId("UI.RUNMODE.SET"), args, state)).Ok);
            Assert.Equal("UI.MODE.SET", host.LastId);
            Assert.Equal(mode, host.LastMode);
            Assert.Equal(1, host.Calls);
        }

        [Fact]
        public async Task OnlineRunModeIsUnavailableAndUnknownModesAreInvalid()
        {
            var host = new Host();
            var catalog = new CommandScanner().Scan(new RunModeHandlers(host, host));
            var registry = new CommandRegistry();
            new CommandScanner().Register(registry, catalog);
            var invoker = new CommandInvoker(registry);
            var id = new CommandId("UI.RUNMODE.SET");
            var state = new CommandContext(CtxFlags.None);
            Assert.Equal("NOT_AVAILABLE_YET", (await invoker.ExecuteAsync(id, Args("{\"mode\":\"online\"}"), state)).Code);
            Assert.Equal("INVALID_ARGS", (await invoker.ExecuteAsync(id, Args("{\"mode\":\"unknown\"}"), state)).Code);
            Assert.Equal(0, host.Resolutions);
            Assert.Equal(0, host.Calls);
        }

        [Theory]
        [InlineData(RunMode.Simple, "simple")]
        [InlineData(RunMode.Advanced, "advanced")]
        [InlineData(RunMode.Online, "online")]
        public void TypedModesPreserveTheStringContextContract(RunMode mode, string name)
        {
            var context = new CommandContext(CtxFlags.None, mode);
            Assert.Equal(name, context.Mode);
            Assert.Equal(mode, context.RunMode);
            Assert.Equal(mode, new CommandContext(CtxFlags.None, name).RunMode);
            Assert.Throws<ArgumentOutOfRangeException>(() => new CommandContext(CtxFlags.None, (RunMode)99));
        }

        [Theory]
        [InlineData(RunMode.Simple, false)]
        [InlineData(RunMode.Advanced, false)]
        [InlineData(RunMode.Online, false)]
        [InlineData(RunMode.Online, true)]
        public void ProductionCatalogSlicesKeepOfflineCommandsAndOnlyOptInOnlineReads(RunMode mode, bool running)
        {
            var host = new Host();
            var catalog = OnlineCommandDescriptors.Discover(new ViewModelCommandHandlers(host, host), new RunModeHandlers(host, host));
            var registry = new CommandRegistry();
            new CommandScanner().Register(registry, catalog);
            var state = new CommandContext(running ? CtxFlags.OnlineRunning : CtxFlags.None, mode);
            string[] visible = registry.Query(state).Select(d => d.Id.Value).ToArray();
            foreach (DiscoveredCommand command in catalog.Where(c => !c.Descriptor.Id.Value.StartsWith("ONL.", StringComparison.Ordinal)))
            {
                string id = command.Descriptor.Id.Value;
                bool allowed = mode != RunMode.Online || id == "INS.RUN" || id == "SYS.SETTINGS.OPEN" || id == "UI.RUNMODE.SET";
                if (running && (id == "INS.RUN" || id == "UI.RUNMODE.SET")) allowed = false;
                Assert.Equal(allowed, visible.Contains(id));
            }
            Assert.Equal(0, host.Resolutions);
        }
    }
}
