using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using CommunityToolkit.Mvvm.Input;
using EasyAI.Desktop.Commands;
using EasyLabel.Commands.Discovery;
using EasyLabel.Commands.Results;
using Xunit;

namespace EasyLabel.Commands.Tests.Invocation
{
    public sealed class AgentSlashRegistryTests
    {
        private sealed class Host : IViewModelCommandSource, ICommandDispatcher
        {
            public int Calls;
            public bool Enabled = true;
            public string LastId = "";
            public string LastArgs = "";
            public ViewModelCommandTarget Resolve(string id, JsonElement args)
            {
                LastId = id;
                LastArgs = args.GetRawText();
                return ViewModelCommandTarget.FromCommand(new RelayCommand(() => Calls++, () => Enabled), args);
            }
            public T Invoke<T>(Func<T> action) { return action(); }
            public Task<T> InvokeAsync<T>(Func<Task<T>> action, CancellationToken cancellationToken) { return action(); }
        }

        public static IEnumerable<object[]> Commands()
        {
            yield return new object[] { "AGT.CLEAR", "clear", CommandRisk.R2, CommandCost.C0 };
            yield return new object[] { "AGT.CHECK", "check", CommandRisk.R3, CommandCost.C3 };
            yield return new object[] { "AGT.SUGGEST", "suggest", CommandRisk.R3, CommandCost.C3 };
            yield return new object[] { "AGT.CANCEL", "cancel", CommandRisk.R1, CommandCost.C0 };
            yield return new object[] { "AGT.MODEL.SET", "model", CommandRisk.R1, CommandCost.C0 };
            yield return new object[] { "AGT.MODE.SET", "mode", CommandRisk.R1, CommandCost.C0 };
            yield return new object[] { "AGT.FORGET", "forget", CommandRisk.R2, CommandCost.C0 };
            yield return new object[] { "AGT.HELP", "help", CommandRisk.R0, CommandCost.C0 };
        }

        private static IReadOnlyList<DiscoveredCommand> Catalog(Host host)
        {
            return OnlineCommandDescriptors.Discover(new ViewModelCommandHandlers(host, host), new RunModeHandlers(host, host));
        }

        [Theory]
        [MemberData(nameof(Commands))]
        public async Task AliasesInvokeTheProductionEndpoint(string id, string alias, CommandRisk risk, CommandCost cost)
        {
            var host = new Host();
            var catalog = Catalog(host);
            var descriptors = catalog.Select(item => item.Descriptor).ToArray();
            var descriptor = AgentSlashCatalog.Resolve(descriptors, alias.ToUpperInvariant());
            Assert.NotNull(descriptor);
            Assert.Equal(id, descriptor.Id.Value);
            Assert.Equal(risk, descriptor.Risk);
            Assert.Equal(cost, descriptor.Cost);
            Assert.Equal(CtxFlags.None, descriptor.When);
            Assert.Equal(new[] { CommandModes.Simple, CommandModes.Advanced }, descriptor.Modes);
            Assert.Equal(alias, Assert.Single(descriptor.Aliases));
            var registry = new CommandRegistry();
            new CommandScanner().Register(registry, catalog);
            CommandResult result = await new CommandInvoker(registry).ExecuteAsync(descriptor.Id,
                AgentSlashCatalog.Arguments(descriptor, ""), new CommandContext(CtxFlags.None));
            Assert.True(result.Ok);
            Assert.Equal(id, host.LastId);
            Assert.Equal(1, host.Calls);
        }

        [Theory]
        [InlineData(CommandModes.Simple, true)]
        [InlineData(CommandModes.Advanced, true)]
        [InlineData(CommandModes.Online, false)]
        [InlineData(CommandModes.OnlineRunning, false)]
        public async Task AllEightRespectCatalogAndInvocationModeGates(string mode, bool allowed)
        {
            var host = new Host();
            var catalog = Catalog(host);
            var registry = new CommandRegistry();
            new CommandScanner().Register(registry, catalog);
            var context = new CommandContext(CtxFlags.Busy, mode);
            Assert.Equal(allowed ? 8 : 0, registry.Query(context).Count(AgentSlashCatalog.IsSlashCommand));
            foreach (CommandDescriptor descriptor in catalog.Select(item => item.Descriptor).Where(AgentSlashCatalog.IsSlashCommand))
            {
                CommandResult result = await new CommandInvoker(registry).ExecuteAsync(descriptor.Id,
                    AgentSlashCatalog.Arguments(descriptor, ""), context);
                Assert.Equal(allowed, result.Ok);
                if (!allowed) Assert.Equal("MODE_BLOCKED", result.Code);
            }
            Assert.Equal(allowed ? 8 : 0, host.Calls);
        }

        [Theory]
        [InlineData("model", "name", "Some Model With Spaces")]
        [InlineData("model", "name", "")]
        [InlineData("mode", "mode", "REMAINING")]
        [InlineData("mode", "mode", "not-a-mode")]
        public async Task ArgumentsReachTheVmUnchangedForExistingValidation(string alias, string property, string text)
        {
            var host = new Host();
            var catalog = Catalog(host);
            var descriptor = AgentSlashCatalog.Resolve(catalog.Select(item => item.Descriptor), alias);
            Assert.NotNull(descriptor);
            var registry = new CommandRegistry();
            new CommandScanner().Register(registry, catalog);
            var result = await new CommandInvoker(registry).ExecuteAsync(descriptor.Id,
                AgentSlashCatalog.Arguments(descriptor, text), new CommandContext(CtxFlags.None));
            Assert.True(result.Ok);
            using (var document = JsonDocument.Parse(host.LastArgs))
                Assert.Equal(text, document.RootElement.GetProperty(property).GetString());
        }

        [Fact]
        public async Task DisabledVmCommandIsRejectedWithoutSideEffects()
        {
            var host = new Host { Enabled = false };
            var catalog = Catalog(host);
            var registry = new CommandRegistry();
            new CommandScanner().Register(registry, catalog);
            foreach (CommandDescriptor descriptor in catalog.Select(item => item.Descriptor).Where(AgentSlashCatalog.IsSlashCommand))
            {
                var command = catalog.Single(item => item.Descriptor.Id == descriptor.Id);
                JsonElement args = AgentSlashCatalog.Arguments(descriptor, "");
                Assert.False(command.Endpoint.CanExecute(new CommandContext(CtxFlags.None), args));
                var result = await new CommandInvoker(registry).ExecuteAsync(descriptor.Id, args, new CommandContext(CtxFlags.None));
                Assert.Equal("PRECONDITION_FAILED", result.Code);
            }
            Assert.Equal(0, host.Calls);
        }

        [Fact]
        public void UnknownWordsDoNotResolveAndHelpUsesOnlyRegisteredAliases()
        {
            var descriptors = Catalog(new Host()).Select(item => item.Descriptor).ToArray();
            Assert.Null(AgentSlashCatalog.Resolve(descriptors, "unknown"));
            Assert.Null(AgentSlashCatalog.Resolve(descriptors, "AGT.CLEAR"));
            Assert.Null(AgentSlashCatalog.Resolve(descriptors, ""));
            string template = ResourceText("Strings.resx", "AgentSlashHelp");
            string reduced = AgentSlashCatalog.Help(descriptors.Where(item => item.Id.Value != "AGT.SUGGEST"), template);
            Assert.Equal(template.Replace("/suggest  ", ""), reduced);
            Assert.Equal("", AgentSlashCatalog.Help(new CommandDescriptor[0], template));
        }

        [Theory]
        [InlineData("Strings.resx")]
        [InlineData("Strings.vi.resx")]
        public void HelpAndNoticeResourcesPreserveBothLanguages(string resource)
        {
            var descriptors = Catalog(new Host()).Select(item => item.Descriptor).ToArray();
            string template = ResourceText(resource, "AgentSlashHelp");
            Assert.Equal(template, AgentSlashCatalog.Help(descriptors, template));
            foreach (string key in new[] { "AgentSlashUnknownFormat", "AgentSlashClearBusy", "AgentSlashUnavailable",
                "AgentSlashModelFormat", "AgentSlashModelUnknownFormat", "AgentSlashModeFormat",
                "AgentSlashModeUnknownFormat", "AgentSlashForgetDone" })
                Assert.False(string.IsNullOrWhiteSpace(ResourceText(resource, key)));
        }

        private static string ResourceText(string file, string key)
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory != null)
            {
                string path = Path.Combine(directory.FullName, "EasyLabel", "src", "EasyAI.Wpf", "Resources", file);
                if (File.Exists(path))
                {
                    var root = XDocument.Load(path).Root ?? throw new InvalidDataException("Missing resource root.");
                    var value = root.Elements("data").Single(item => item.Attribute("name")?.Value == key).Element("value");
                    return (value ?? throw new InvalidDataException("Missing resource value.")).Value;
                }
                directory = directory.Parent;
            }
            throw new FileNotFoundException("Repository resource not found: " + file);
        }
    }
}
