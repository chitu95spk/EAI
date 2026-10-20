using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using EasyAI.Desktop.Commands;
using EasyLabel.Commands.Catalog;
using EasyLabel.Commands.Discovery;
using EasyLabel.Commands.Results;
using Xunit;

namespace EasyLabel.Commands.Tests.Catalog
{
    public sealed class CommandCatalogSnapshotTests
    {
        private sealed class UnusedSource : IViewModelCommandSource, ICommandDispatcher
        {
            public ViewModelCommandTarget Resolve(string id, JsonElement args) { throw new InvalidOperationException("Catalog scan must not read VM state."); }
            public T Invoke<T>(Func<T> action) { return action(); }
            public Task<T> InvokeAsync<T>(Func<Task<T>> action, CancellationToken cancellationToken) { return action(); }
        }

        private static ViewModelCommandHandlers Handlers()
        {
            var host = new UnusedSource();
            return new ViewModelCommandHandlers(host, host);
        }

        private static IReadOnlyList<DiscoveredCommand> ProductionCatalog()
        {
            var host = new UnusedSource();
            return OnlineCommandDescriptors.Discover(Handlers(), new RunModeHandlers(host, host));
        }

        [Fact]
        public void AllProductionHandlersMatchCheckedInCatalog()
        {
            var catalog = ProductionCatalog();
            string snapshot = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Catalog", "commands.catalog.json"))
                .TrimEnd('\r', '\n');
            Assert.Equal(snapshot, new CatalogWriter().Write(catalog));
            Assert.Equal(snapshot, new CatalogWriter().Write(catalog.Reverse().ToArray()));
            Assert.Equal(65, catalog.Count);
        }

        [Fact]
        public void DuplicateIdsAcrossAllHandlerInstancesAreRejected()
        {
            var scanner = new CommandScanner();
            Assert.Throws<InvalidOperationException>(() => scanner.Scan(Handlers(), Handlers()));
            var registry = new CommandRegistry();
            var catalog = ProductionCatalog();
            scanner.Register(registry, catalog);
            Assert.Throws<InvalidOperationException>(() => scanner.Register(registry, catalog));
            Assert.Equal(64, registry.Query(new CommandContext(CtxFlags.None)).Count);
        }

        [Fact]
        public void ExistingRegistrationCollisionDoesNotPartiallyRegisterCatalog()
        {
            var registry = new CommandRegistry();
            registry.Register(new CommandDescriptor(new CommandId("UI.MODE.SET")),
                (context, args, cancellationToken) => Task.FromResult(CommandResult.Success()));
            var scanner = new CommandScanner();
            Assert.Throws<InvalidOperationException>(() => scanner.Register(registry, ProductionCatalog()));
            Assert.Single(registry.Query(new CommandContext(CtxFlags.None)));
        }

        private sealed class InvalidHandler
        {
            [Command("SYS.SHOW")]
            public string WrongType { get { return "invalid"; } }
        }

        [Fact]
        public void InvalidEndpointDeclarationIsRejectedBeforeRegistration()
        {
            Assert.Throws<InvalidOperationException>(() => new CommandScanner().Scan(new InvalidHandler()));
        }

        [Fact]
        public void SchemaObjectAndMetadataOrderingAreCanonical()
        {
            var scanner = new CommandScanner();
            var writer = new CatalogWriter();
            Assert.Equal(writer.Write(scanner.Scan(new SchemaA())), writer.Write(scanner.Scan(new SchemaB())));
        }

        private sealed class SchemaA
        {
            [Command("SYS.SHOW", Aliases = new[] { "b", "a" }, Args =
                "{\"properties\":{\"x\":{\"maximum\":2,\"minimum\":0,\"type\":\"integer\"}},\"type\":\"object\"}")]
            public ICommandEndpoint Show { get { return new NoOpEndpoint(); } }
        }

        private sealed class SchemaB
        {
            [Command("SYS.SHOW", Aliases = new[] { "a", "b" }, Args =
                "{\"type\":\"object\",\"properties\":{\"x\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":2}}}")]
            public ICommandEndpoint Show { get { return new NoOpEndpoint(); } }
        }

        private sealed class NoOpEndpoint : ICommandEndpoint
        {
            public bool CanExecute(ICommandContext context, JsonElement args) { return true; }
            public Task<CommandResult> ExecuteAsync(ICommandContext context, JsonElement args, CancellationToken cancellationToken)
            { return Task.FromResult(CommandResult.Success()); }
        }
    }
}
