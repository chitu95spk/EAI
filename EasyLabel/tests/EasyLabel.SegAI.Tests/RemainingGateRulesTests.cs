using EasyAI.Desktop.Services.Agent;

namespace EasyLabel.SegAI.Tests;

public sealed class RemainingGateRulesTests
{
    private static EasyLabel.Core.Io.AgentChatEntry Agent(string outcome, int proposed, params string[] images)
    {
        return new EasyLabel.Core.Io.AgentChatEntry(
            System.DateTime.UtcNow, "agent", "current", "normal", string.Empty, images, proposed, 0, 0, null, null, outcome, string.Empty);
    }

    private static EasyLabel.Core.Io.AgentChatEntry User(params string[] images)
    {
        return new EasyLabel.Core.Io.AgentChatEntry(
            System.DateTime.UtcNow, "user", "current", "normal", "p", images, 0, 0, 0, null, null, string.Empty, string.Empty);
    }

    private static readonly string[] NoSession = new string[0];
    private static readonly EasyLabel.Core.Io.AgentChatEntry[] NoHistory = new EasyLabel.Core.Io.AgentChatEntry[0];

    [Fact]
    public void NoRunYetKeepsTheModeDisabled()
    {
        Assert.False(RemainingGateRules.Decide(NoSession, "a.png", NoHistory));
        Assert.False(RemainingGateRules.Decide(null, "a.png", null));
    }

    [Fact]
    public void NoOpenImageKeepsTheModeDisabled()
    {
        Assert.False(RemainingGateRules.Decide(new[] { "a.png" }, null, NoHistory, true));
        Assert.False(RemainingGateRules.Decide(new[] { "a.png" }, " ", NoHistory, true));
    }

    [Fact]
    public void SessionScanOfTheOpenImageEnablesItIgnoringCase()
    {
        Assert.True(RemainingGateRules.Decide(new[] { "A.PNG" }, "a.png", NoHistory));
        Assert.False(RemainingGateRules.Decide(new[] { "b.png" }, "a.png", NoHistory));
    }

    [Fact]
    public void HistoryRunOkOrCachedOfTheOpenImageEnablesIt()
    {
        Assert.True(RemainingGateRules.Decide(NoSession, "a.png", new[] { Agent("ok", 3, "a.png") }));
        Assert.True(RemainingGateRules.Decide(NoSession, "a.png", new[] { Agent("cached", 0, "x.png", "a.png") }));
    }

    [Fact]
    public void ErrorCancelledAndUserEntriesDoNotCount()
    {
        Assert.False(RemainingGateRules.Decide(NoSession, "a.png", new[] { Agent("error", 0, "a.png") }));
        Assert.False(RemainingGateRules.Decide(NoSession, "a.png", new[] { Agent("cancelled", 0, "a.png") }));
        Assert.False(RemainingGateRules.Decide(NoSession, "a.png", new[] { Agent(string.Empty, 0, "a.png") }));
        Assert.False(RemainingGateRules.Decide(NoSession, "a.png", new[] { User("a.png") }));
        Assert.False(RemainingGateRules.IsScanSuccess("ok", -1));
    }

    [Fact]
    public void HistoryRunOfAnotherImageDoesNotEnableTheOpenImage()
    {
        Assert.False(RemainingGateRules.Decide(NoSession, "a.png", new[] { Agent("ok", 2, "b.png") }));
    }

    [Fact]
    public void PendingSuggestionsOfTheOpenImageEnableItWithoutAnyRun()
    {
        Assert.True(RemainingGateRules.Decide(NoSession, "a.png", NoHistory, true));
    }

    [Fact]
    public void ProjectChangeEmptySessionAndHistoryDisablesItAgain()
    {
        // A project change clears the session set and loads the other project's (here empty) history.
        Assert.True(RemainingGateRules.Decide(new[] { "a.png" }, "a.png", NoHistory));
        Assert.False(RemainingGateRules.Decide(NoSession, "a.png", NoHistory));
    }

    [Fact]
    public void ClearedHistoryStillEnablesThroughTheSessionSet()
    {
        // /clear empties the history but not the in-session set.
        Assert.True(RemainingGateRules.Decide(new[] { "a.png" }, "a.png", NoHistory));
    }

    [Fact]
    public void ScannedRelsCollectsOnlySuccessfulAgentEntries()
    {
        System.Collections.Generic.ISet<string> rels = RemainingGateRules.ScannedRels(new[]
        {
            Agent("ok", 1, "a.png"), Agent("error", 0, "b.png"), Agent("cached", 0, "C.png"), User("d.png"), Agent("cancelled", 0, "e.png"),
        });

        Assert.Equal(2, rels.Count);
        Assert.True(rels.Contains("a.png"));
        Assert.True(rels.Contains("c.png"));
        Assert.True(RemainingGateRules.IsScanned("c.PNG", NoSession, new[] { Agent("cached", 0, "C.png") }));
    }

    [Fact]
    public void ModeRemainingSlashIsBlockedExactlyWhenTheGateIsClosed()
    {
        // HandleModeCommand shows the AgentSlashUnavailable notice for "remaining" when Decide is false; "all" and "current" never depend on it.
        Assert.True(AgentSlashCommands.TryParseMode("remaining", out string key));
        Assert.Equal(AgentBatchMode.Remaining, AgentBatchPlanner.ParseMode(key, AgentBatchMode.Current));
        Assert.False(RemainingGateRules.Decide(NoSession, "a.png", NoHistory));
        Assert.True(RemainingGateRules.Decide(NoSession, "a.png", new[] { Agent("ok", 0, "a.png") }));
    }
}
