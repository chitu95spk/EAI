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
using EasyLabel.Commands.Results;
using Xunit;

namespace EasyLabel.Commands.Tests.Invocation
{
    public sealed class ViewModelCommandParityTests
    {
        private sealed class Host : IViewModelCommandSource, ICommandDispatcher
        {
            public bool Enabled { get; set; }
            public int Calls { get; private set; }
            public string LastId { get; private set; } = "";
            public string LastParameter { get; private set; } = "";
            public ICommand Command { get; private set; }
            public int Dispatches { get; private set; }
            public Func<Task> Run { get; set; } = () => Task.CompletedTask;

            public Host()
            {
                Command = new AsyncRelayCommand<object>(async p => { Calls++; await Run(); },
                    p => Enabled && string.Equals(p as string, LastParameter, StringComparison.Ordinal));
            }

            public ViewModelCommandTarget Resolve(string id, JsonElement args)
            {
                LastId = id;
                LastParameter = args.GetRawText();
                return ViewModelCommandTarget.FromCommand(Command, LastParameter);
            }
            public T Invoke<T>(Func<T> action) { Dispatches++; return action(); }
            public Task<T> InvokeAsync<T>(Func<Task<T>> action, CancellationToken cancellationToken)
            { Dispatches++; cancellationToken.ThrowIfCancellationRequested(); return action(); }
        }

        // Every declared enum option, boolean branch, default and required argument participates.
        private static IEnumerable<JsonElement> Arguments(DiscoveredCommand command)
        {
            JsonElement properties = command.Descriptor.ArgsSchema.Definition.GetProperty("properties");
            var baseline = new Dictionary<string, object>();
            foreach (JsonProperty property in properties.EnumerateObject())
            {
                JsonElement value;
                if (property.Value.TryGetProperty("default", out value)) baseline[property.Name] = value.Clone();
                else if (property.Value.TryGetProperty("enum", out value)) baseline[property.Name] = value[0].Clone();
                else
                {
                    string type = property.Value.GetProperty("type").GetString() ?? "";
                    baseline[property.Name] = type == "boolean" ? (object)true : type == "integer" ? (object)0 : "value";
                }
            }
            yield return JsonSerializer.SerializeToElement(baseline);
            foreach (JsonProperty property in properties.EnumerateObject())
            {
                JsonElement choices;
                if (property.Value.TryGetProperty("enum", out choices))
                    foreach (JsonElement choice in choices.EnumerateArray())
                    {
                        var variant = new Dictionary<string, object>(baseline);
                        variant[property.Name] = choice.Clone();
                        yield return JsonSerializer.SerializeToElement(variant);
                    }
                else if (property.Value.GetProperty("type").GetString() == "boolean")
                {
                    var variant = new Dictionary<string, object>(baseline);
                    variant[property.Name] = false;
                    yield return JsonSerializer.SerializeToElement(variant);
                }
            }
            // Also exercise omitted optional arguments and schema defaults.
            using (JsonDocument empty = JsonDocument.Parse("{}"))
            {
                JsonElement normalized;
                if (command.Descriptor.ArgsSchema.TryValidate(empty.RootElement, out normalized, out _))
                    yield return normalized;
            }
        }

        [Fact]
        public void EveryHandlerHasExactWrappedCanExecuteParityAcrossStateChanges()
        {
            var host = new Host();
            var commands = new CommandScanner().Scan(new ViewModelCommandHandlers(host, host));
            foreach (DiscoveredCommand command in commands)
                foreach (JsonElement args in Arguments(command))
                    foreach (bool enabled in new[] { false, true, false })
                    {
                        host.Enabled = enabled;
                        ViewModelCommandTarget target = host.Resolve(command.Descriptor.Id.Value, args);
                        bool expected = target.Command.CanExecute(target.Parameter);
                        // Coarse catalog flags must not introduce extra gates on top of VM CanExecute.
                        Assert.Equal(CtxFlags.None, command.Descriptor.When);
                        Assert.Equal(expected, command.Endpoint.CanExecute(new CommandContext(CtxFlags.Busy), args));
                        Assert.Equal(command.Descriptor.Id.Value, host.LastId);
                        Assert.Equal(args.GetRawText(), host.LastParameter);
                    }
            Assert.True(host.Dispatches > 0);
            Assert.Equal(0, host.Calls);
        }

        [Fact]
        public async Task InvocationRechecksCanExecuteAndAwaitsTheWrappedAsyncOperation()
        {
            var host = new Host();
            DiscoveredCommand command = new CommandScanner().Scan(new ViewModelCommandHandlers(host, host))
                .Single(c => c.Descriptor.Id.Value == "LBL.RUN.AGENT");
            JsonElement args = Arguments(command).First();
            var context = new CommandContext(CtxFlags.None);
            CommandResult denied = await command.Endpoint.ExecuteAsync(context, args, CancellationToken.None);
            Assert.Equal("PRECONDITION_FAILED", denied.Code);
            Assert.Equal(0, host.Calls);

            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            host.Enabled = true;
            host.Run = () => completion.Task;
            Task<CommandResult> pending = command.Endpoint.ExecuteAsync(context, args, CancellationToken.None);
            Assert.False(pending.IsCompleted);
            Assert.Equal(1, host.Calls);
            Assert.False(command.Endpoint.CanExecute(context, args));
            completion.SetResult(true);
            Assert.True((await pending).Ok);
        }

        [Fact]
        public async Task SynchronousRelayCommandReceivesTheExactParameter()
        {
            string received = "";
            var command = new RelayCommand<string>(p => received = p ?? "", p => p == "quality");
            var target = ViewModelCommandTarget.FromCommand(command, "quality");
            Assert.True(target.Command.CanExecute(target.Parameter));
            await target.ExecuteAsync();
            Assert.Equal("quality", received);
        }

        [Fact]
        public async Task ScannerRegistrationInvokesTheProductionEndpoint()
        {
            var host = new Host();
            var scanner = new CommandScanner();
            var registry = new CommandRegistry();
            scanner.Register(registry, scanner.Scan(new ViewModelCommandHandlers(host, host)));
            var invoker = new CommandInvoker(registry);
            var context = new CommandContext(CtxFlags.None);
            JsonElement args = JsonSerializer.SerializeToElement(new Dictionary<string, object>());
            var id = new CommandId("IMG.NEXT");
            Assert.Equal("PRECONDITION_FAILED", (await invoker.ExecuteAsync(id, args, context)).Code);
            host.Enabled = true;
            Assert.True((await invoker.ExecuteAsync(id, args, context)).Ok);
            Assert.Equal("IMG.NEXT", host.LastId);
            Assert.Equal(1, host.Calls);
        }

        [Fact]
        public async Task CancelledInvocationDoesNotTouchTheViewModel()
        {
            var host = new Host { Enabled = true };
            DiscoveredCommand command = new CommandScanner().Scan(new ViewModelCommandHandlers(host, host)).First();
            using (var cancellation = new CancellationTokenSource())
            {
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => command.Endpoint.ExecuteAsync(
                    new CommandContext(CtxFlags.None), Arguments(command).First(), cancellation.Token));
                Assert.Equal("", host.LastId);
                Assert.Equal(0, host.Calls);
            }
        }
    }
}
