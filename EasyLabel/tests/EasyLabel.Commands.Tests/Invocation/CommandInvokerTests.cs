using System.Text.Json;
using EasyLabel.Commands.Policy;
using EasyLabel.Commands.Results;
using EasyLabel.Commands.Schema;

namespace EasyLabel.Commands.Tests.Invocation;

public sealed class CommandInvokerTests
{
    private static JsonElement Args(string json = "{}")
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    [Fact]
    public async Task CommandNotAllowedInOnlineModeIsRejectedBeforeHandler()
    {
        bool called = false;
        var descriptor = new CommandDescriptor(new("IMG.NEXT"));
        var registry = new CommandRegistry();
        registry.Register(descriptor, (_, _, _) => { called = true; return Task.FromResult(CommandResult.Success()); });
        CommandResult result = await new CommandInvoker(registry).ExecuteAsync(descriptor.Id, Args(),
            new CommandContext(CtxFlags.None, CommandModes.Online));
        Assert.Equal("MODE_BLOCKED", result.Code);
        Assert.Equal("cmd.err.mode", result.MessageKey);
        Assert.False(result.Ok);
        Assert.False(called);
    }

    [Theory]
    [InlineData(CtxFlags.None, false)]
    [InlineData(CtxFlags.ProjectOpen, false)]
    [InlineData(CtxFlags.ProjectOpen | CtxFlags.ImageSelected, true)]
    [InlineData(CtxFlags.ProjectOpen | CtxFlags.ImageSelected | CtxFlags.Busy, false)]
    [InlineData(CtxFlags.ProjectOpen | CtxFlags.ImageSelected | CtxFlags.Busy | CtxFlags.NotBusy, false)]
    public async Task RequiredContextFlagsAndDerivedNotBusyAreEnforced(CtxFlags flags, bool allowed)
    {
        bool called = false;
        var descriptor = new CommandDescriptor(new("LBL.RUN.AUTO"),
            when: CtxFlags.ProjectOpen | CtxFlags.ImageSelected | CtxFlags.NotBusy);
        var registry = new CommandRegistry();
        registry.Register(descriptor, (_, _, _) => { called = true; return Task.FromResult(CommandResult.Success()); });
        CommandResult result = await new CommandInvoker(registry).ExecuteAsync(descriptor.Id, Args(), new CommandContext(flags));
        Assert.Equal(allowed, result.Ok);
        Assert.Equal(allowed, called);
        if (!allowed) Assert.Equal("PRECONDITION_FAILED", result.Code);
    }

    [Fact]
    public async Task ValidationPrecedesContextAndPolicy()
    {
        int calls = 0;
        var descriptor = new CommandDescriptor(new("LBL.RUN.AGENT"),
            when: CtxFlags.ProjectOpen, risk: CommandRisk.R3);
        var registry = new CommandRegistry();
        registry.Register(descriptor, (_, _, _) => { calls++; return Task.FromResult(CommandResult.Success()); });
        var invoker = new CommandInvoker(registry);
        CommandResult invalid = await invoker.ExecuteAsync(descriptor.Id, Args("{\"unknown\":1}"),
            new CommandContext(CtxFlags.None, CommandModes.Online), CommandOrigin.Agent);
        Assert.Equal("INVALID_ARGS", invalid.Code);
        CommandResult precondition = await invoker.ExecuteAsync(descriptor.Id, Args(),
            new CommandContext(CtxFlags.None), CommandOrigin.Agent);
        Assert.Equal("PRECONDITION_FAILED", precondition.Code);
        CommandResult policy = await invoker.ExecuteAsync(descriptor.Id, Args(),
            new CommandContext(CtxFlags.ProjectOpen), CommandOrigin.Agent);
        Assert.Equal("CAP_CHECK_REQUIRED", policy.Code);
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task OnlineRunningPolicyIsAppliedByInvoker(bool listed, bool allowed)
    {
        int calls = 0;
        string[] modes = listed ? [CommandModes.Online, CommandModes.OnlineRunning] : [CommandModes.Online];
        var descriptor = new CommandDescriptor(new("ONL.SET.SOURCE"), risk: CommandRisk.R3, modes: modes);
        var registry = new CommandRegistry();
        registry.Register(descriptor, (_, _, _) => { calls++; return Task.FromResult(CommandResult.Success()); });
        CommandResult result = await new CommandInvoker(registry).ExecuteAsync(descriptor.Id, Args(),
            new CommandContext(CtxFlags.OnlineRunning, CommandModes.Online));
        Assert.Equal(allowed, result.Ok);
        Assert.Equal(allowed ? 1 : 0, calls);
        if (!allowed) Assert.Equal("ONLINE_RUNNING_BLOCKED", result.Code);
    }

    [Fact]
    public async Task HandlerReceivesNormalizedArgumentsAndResultEnvelopeIsPreserved()
    {
        var schema = CommandArgsSchema.Parse("""
            {"type":"object","properties":{"scope":{"enum":["current","all"],"default":"current"}}}
            """);
        var descriptor = new CommandDescriptor(new("LBL.RUN.AUTO"), argsSchema: schema, undo: true);
        var registry = new CommandRegistry();
        registry.Register(descriptor, (_, args, _) =>
        {
            Assert.Equal("current", args.GetProperty("scope").GetString());
            return Task.FromResult(CommandResult.Success(Args("{\"count\":2}"), "undo-1", new(0.01m, 1)));
        });
        CommandResult result = await new CommandInvoker(registry).ExecuteAsync(descriptor.Id, Args(), new CommandContext(CtxFlags.None));
        Assert.True(result.Ok);
        Assert.Equal("undo-1", result.UndoToken);
        Assert.Equal(0.01m, result.Cost.Usd);
        Assert.Equal(1, result.Cost.Turns);
        Assert.Equal(2, result.Data!.Value.GetProperty("count").GetInt32());
        Assert.True(result.Ms >= 0);
        JsonElement envelope = JsonSerializer.SerializeToElement(result);
        Assert.True(envelope.GetProperty("ok").GetBoolean());
        Assert.Equal("cmd.ok", envelope.GetProperty("message_key").GetString());
        Assert.Equal("undo-1", envelope.GetProperty("undoToken").GetString());
        Assert.Equal(1, envelope.GetProperty("cost").GetProperty("turns").GetInt32());
        Assert.True(envelope.TryGetProperty("ms", out _));
    }

    [Fact]
    public async Task ExceptionsCancellationAndUnknownIdReturnEnvelopes()
    {
        var descriptor = new CommandDescriptor(new("TRN.START"));
        var registry = new CommandRegistry();
        registry.Register(descriptor, (_, _, _) => throw new InvalidOperationException("private detail"));
        var invoker = new CommandInvoker(registry);
        var context = new CommandContext(CtxFlags.None);
        Assert.Equal("HANDLER_FAILED", (await invoker.ExecuteAsync(descriptor.Id, Args(), context)).Code);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Equal("CANCELLED", (await invoker.ExecuteAsync(descriptor.Id, Args(), context,
            cancellationToken: cancellation.Token)).Code);
        Assert.Equal("COMMAND_NOT_FOUND", (await invoker.ExecuteAsync(new("TRN.STOP"), Args(), context)).Code);
    }
}
