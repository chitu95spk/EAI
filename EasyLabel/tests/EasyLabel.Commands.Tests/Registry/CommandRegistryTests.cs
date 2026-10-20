using EasyLabel.Commands.Results;

namespace EasyLabel.Commands.Tests.Registry;

public sealed class CommandRegistryTests
{
    [Fact]
    public void DuplicateIdIsRejectedAcrossKinds()
    {
        var registry = new CommandRegistry();
        var id = new CommandId("TRN.START");
        registry.Register(new(id), (_, _, _) => Task.FromResult(CommandResult.Success()));
        Assert.Throws<InvalidOperationException>(() => registry.Register(
            new(id, kind: CommandKind.Set), (_, _, _) => Task.FromResult(CommandResult.Success())));
        Assert.Single(registry.Query(new CommandContext(CtxFlags.None)));
    }

    [Fact]
    public void QueryFiltersContextAndModeAndReturnsAnIndependentSnapshot()
    {
        var registry = new CommandRegistry();
        registry.Register(new(new("PRJ.OPEN")), (_, _, _) => Task.FromResult(CommandResult.Success()));
        registry.Register(new(new("IMG.NEXT"), when: CtxFlags.ProjectOpen | CtxFlags.NotBusy),
            (_, _, _) => Task.FromResult(CommandResult.Success()));
        registry.Register(new(new("ONL.GET.STATUS"), modes: [CommandModes.Online]),
            (_, _, _) => Task.FromResult(CommandResult.Success()));

        IReadOnlyList<CommandDescriptor> snapshot = registry.Query(new CommandContext(CtxFlags.None));
        Assert.Equal("PRJ.OPEN", Assert.Single(snapshot).Id.Value);
        Assert.Equal(2, registry.Query(new CommandContext(CtxFlags.ProjectOpen)).Count);
        Assert.Single(registry.Query(new CommandContext(CtxFlags.ProjectOpen | CtxFlags.Busy)));
        Assert.Equal("ONL.GET.STATUS", Assert.Single(registry.Query(
            new CommandContext(CtxFlags.None, CommandModes.Online))).Id.Value);
        registry.Register(new(new("SYS.SHOW.HELP")), (_, _, _) => Task.FromResult(CommandResult.Success()));
        Assert.Single(snapshot);
    }

    [Fact]
    public void DescriptorCopiesListsAndAcceptsBothOnlineStrings()
    {
        string[] modes = [CommandModes.Online, CommandModes.OnlineRunning];
        string[] aliases = ["stop"];
        var descriptor = new CommandDescriptor(new("ONL.STOP"), modes: modes, aliases: aliases);
        modes[0] = "invalid";
        aliases[0] = "changed";
        Assert.Equal(CommandModes.Online, descriptor.Modes[0]);
        Assert.Equal("stop", descriptor.Aliases[0]);
        Assert.Throws<ArgumentException>(() => new CommandDescriptor(new("SYS.SHOW"), modes: ["unknown"]));
    }
}
