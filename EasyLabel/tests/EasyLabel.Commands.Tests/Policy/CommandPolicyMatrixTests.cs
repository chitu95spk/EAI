using EasyLabel.Commands.Policy;

namespace EasyLabel.Commands.Tests.Policy;

public sealed class CommandPolicyMatrixTests
{
    [Theory]
    [InlineData(CommandRisk.R0, false, true)]
    [InlineData(CommandRisk.R1, false, true)]
    [InlineData(CommandRisk.R2, false, false)]
    [InlineData(CommandRisk.R3, false, false)]
    [InlineData(CommandRisk.R4, false, false)]
    [InlineData(CommandRisk.R2, true, true)]
    [InlineData(CommandRisk.R3, true, true)]
    [InlineData(CommandRisk.R4, true, true)]
    public void OnlineRunningRequiresExplicitModeForRiskAtLeastR2(CommandRisk risk, bool listed, bool allowed)
    {
        string[] modes = listed
            ? [CommandModes.Online, CommandModes.OnlineRunning]
            : [CommandModes.Online];
        var descriptor = new CommandDescriptor(new("ONL.SET.SOURCE"), risk: risk, modes: modes);
        var policy = new CommandPolicyMatrix();
        var evidence = new CommandPolicyEvidence(Confirmed: true);
        CommandPolicyDecision decision = policy.Evaluate(descriptor,
            new CommandContext(CtxFlags.OnlineRunning, CommandModes.Online), CommandOrigin.UserDirect, evidence);
        Assert.Equal(allowed, decision.Allowed);
        if (!allowed) Assert.Equal("ONLINE_RUNNING_BLOCKED", decision.Code);
    }

    [Fact]
    public void OnlineRunningStringCannotBypassTheRunningFlagGate()
    {
        var descriptor = new CommandDescriptor(new("TRN.START"), risk: CommandRisk.R3,
            modes: [CommandModes.Online]);
        CommandPolicyDecision decision = new CommandPolicyMatrix().Evaluate(descriptor,
            new CommandContext(CtxFlags.None, CommandModes.OnlineRunning), CommandOrigin.UserDirect);
        Assert.Equal("ONLINE_RUNNING_BLOCKED", decision.Code);
    }

    [Theory]
    [InlineData(CommandRisk.R0, true, "OK")]
    [InlineData(CommandRisk.R1, true, "OK")]
    [InlineData(CommandRisk.R2, false, "UNDO_REQUIRED")]
    [InlineData(CommandRisk.R3, false, "CAP_CHECK_REQUIRED")]
    [InlineData(CommandRisk.R4, false, "CONFIRMATION_REQUIRED")]
    public void AgentRiskTableFailsClosedWithoutEvidence(CommandRisk risk, bool allowed, string code)
    {
        var descriptor = new CommandDescriptor(new("LBL.RUN.AGENT"), risk: risk, undo: true);
        CommandPolicyDecision decision = new CommandPolicyMatrix().Evaluate(descriptor,
            new CommandContext(CtxFlags.None), CommandOrigin.Agent);
        Assert.Equal(allowed, decision.Allowed);
        Assert.Equal(code, decision.Code);
    }

    [Fact]
    public void AgentR2NeedsBothUndoCapabilityAndAvailableUndo()
    {
        var policy = new CommandPolicyMatrix();
        var context = new CommandContext(CtxFlags.None);
        var evidence = new CommandPolicyEvidence(UndoAvailable: true);
        var descriptor = new CommandDescriptor(new("IMG.SET.CLASS"), risk: CommandRisk.R2, undo: true);
        Assert.True(policy.Evaluate(descriptor, context, CommandOrigin.Agent, evidence).Allowed);
        Assert.False(policy.Evaluate(new(new("IMG.SET.CLASS"), risk: CommandRisk.R2),
            context, CommandOrigin.Agent, evidence).Allowed);
    }

    [Fact]
    public void AgentR3ChecksCapsBeforeConfirmation()
    {
        var descriptor = new CommandDescriptor(new("LBL.RUN.AGENT"), risk: CommandRisk.R3);
        var policy = new CommandPolicyMatrix();
        var context = new CommandContext(CtxFlags.None);
        Assert.Equal("CAP_EXCEEDED", policy.Evaluate(descriptor, context, CommandOrigin.Agent,
            new(CapsChecked: true, WithinCaps: false, Confirmed: true)).Code);
        Assert.Equal("CONFIRMATION_REQUIRED", policy.Evaluate(descriptor, context, CommandOrigin.Agent,
            new(CapsChecked: true, WithinCaps: true)).Code);
        Assert.True(policy.Evaluate(descriptor, context, CommandOrigin.Agent,
            new(CapsChecked: true, WithinCaps: true, Confirmed: true)).Allowed);
    }

    [Theory]
    [InlineData(CommandOrigin.UserDirect)]
    [InlineData(CommandOrigin.Agent)]
    public void R4RequiresConfirmationForEachOrigin(CommandOrigin origin)
    {
        var descriptor = new CommandDescriptor(new("IMG.DELETE"), risk: CommandRisk.R4);
        var policy = new CommandPolicyMatrix();
        var context = new CommandContext(CtxFlags.None);
        Assert.False(policy.Evaluate(descriptor, context, origin).Allowed);
        Assert.True(policy.Evaluate(descriptor, context, origin, new(Confirmed: true)).Allowed);
    }

    [Theory]
    [InlineData(CommandRisk.R0, true)]
    [InlineData(CommandRisk.R1, true)]
    [InlineData(CommandRisk.R2, true)]
    [InlineData(CommandRisk.R3, false)]
    [InlineData(CommandRisk.R4, false)]
    public void OnlineSetupBlocksHighRiskEvenWithApproval(CommandRisk risk, bool allowed)
    {
        var descriptor = new CommandDescriptor(new("ONL.SET.SOURCE"), risk: risk,
            modes: [CommandModes.Online, CommandModes.OnlineRunning]);
        Assert.Equal(allowed, new CommandPolicyMatrix().Evaluate(descriptor,
            new CommandContext(CtxFlags.None, CommandModes.Online), CommandOrigin.UserDirect,
            new(Confirmed: true)).Allowed);
    }

    [Fact]
    public void ToolKindRequiresLabellerOriginAndItsAllowList()
    {
        var descriptor = new CommandDescriptor(new("TOL.IMG.CROP"), kind: CommandKind.Tol);
        var context = new CommandContext(CtxFlags.None);
        var policy = new CommandPolicyMatrix([descriptor.Id]);
        Assert.False(policy.Evaluate(descriptor, context, CommandOrigin.UserDirect).Allowed);
        Assert.False(policy.Evaluate(descriptor, context, CommandOrigin.Agent).Allowed);
        Assert.True(policy.Evaluate(descriptor, context, CommandOrigin.LabellerAgent).Allowed);
        Assert.False(new CommandPolicyMatrix().Evaluate(descriptor, context, CommandOrigin.LabellerAgent).Allowed);
        Assert.False(policy.Evaluate(new(new("IMG.NEXT")), context, CommandOrigin.LabellerAgent).Allowed);
    }

    [Theory]
    [InlineData(CtxFlags.Busy)]
    [InlineData(CtxFlags.OnlineRunning)]
    public void RunModeCannotBeChangedWhileBusyOrRunning(CtxFlags flags)
    {
        var descriptor = new CommandDescriptor(new("UI.RUNMODE.SET"), risk: CommandRisk.R1);
        Assert.Equal("PRECONDITION_FAILED", new CommandPolicyMatrix().Evaluate(descriptor,
            new CommandContext(flags), CommandOrigin.UserDirect).Code);
    }
}
