namespace EasyLabel.SegAI.Tests
{
    public sealed class AgentChatEntryViewModelTests
    {
        private static readonly System.TimeZoneInfo Utc = System.TimeZoneInfo.Utc;

        private static EasyLabel.Core.Io.AgentChatEntry Make(string role, string mode, string effort, int proposed, int accepted, int rejected, decimal? cost, int? turns, string outcome, string error = "")
        {
            return new EasyLabel.Core.Io.AgentChatEntry(
                new System.DateTime(2026, 10, 11, 8, 30, 15, System.DateTimeKind.Utc),
                role, mode, effort, "find pads", new[] { "a.png", "b.png" }, proposed, accepted, rejected, cost, turns, outcome, error);
        }

        [Xunit.Fact]
        public void AnAgentEntryFormatsEveryLine()
        {
            EasyAI.Desktop.Services.Agent.AgentChatEntryViewModel vm = new EasyAI.Desktop.Services.Agent.AgentChatEntryViewModel(
                Make("agent", "remaining", "best", 4, 2, 1, 0.12m, 9, "ok"), null, Utc);
            Xunit.Assert.Equal("2026-10-11 08:30", vm.TimeText);
            Xunit.Assert.Equal("Remaining images · High", vm.HeaderText);
            Xunit.Assert.Equal("4 proposed · 2 accepted · 1 rejected", vm.SummaryText);
            Xunit.Assert.Equal("$0.12", vm.CostText);
            Xunit.Assert.Equal("9", vm.TurnsText);
            Xunit.Assert.Equal("Done", vm.OutcomeText);
            Xunit.Assert.Equal("Agent", vm.RoleText);
            Xunit.Assert.True(vm.IsAgent);
            Xunit.Assert.True(vm.HasSummary);
            Xunit.Assert.Equal("a.png, b.png", vm.ImagesText);
            Xunit.Assert.Equal("find pads", vm.Prompt);
        }

        [Xunit.Fact]
        public void AUserEntryHasNoSummaryCostOrOutcome()
        {
            EasyAI.Desktop.Services.Agent.AgentChatEntryViewModel vm = new EasyAI.Desktop.Services.Agent.AgentChatEntryViewModel(
                Make("user", "all", "normal", 0, 0, 0, null, null, string.Empty), null, Utc);
            Xunit.Assert.True(vm.IsUser);
            Xunit.Assert.False(vm.HasSummary);
            Xunit.Assert.Equal(string.Empty, vm.CostText);
            Xunit.Assert.Equal(string.Empty, vm.TurnsText);
            Xunit.Assert.Equal(string.Empty, vm.OutcomeText);
            Xunit.Assert.Equal("All images · Normal", vm.HeaderText);
            Xunit.Assert.Equal("You", vm.RoleText);
        }

        [Xunit.Fact]
        public void ErrorOutcomeShowsTheShortError()
        {
            Xunit.Assert.Equal("Error: boom", new EasyAI.Desktop.Services.Agent.AgentChatEntryViewModel(
                Make("agent", "current", "economy", 0, 0, 0, null, null, "error", "boom"), null, Utc).OutcomeText);
            Xunit.Assert.Equal("Error", new EasyAI.Desktop.Services.Agent.AgentChatEntryViewModel(
                Make("agent", "current", "economy", 0, 0, 0, null, null, "error"), null, Utc).OutcomeText);
            Xunit.Assert.Equal("Cancelled", new EasyAI.Desktop.Services.Agent.AgentChatEntryViewModel(
                Make("agent", "current", "low", 0, 0, 0, null, null, "cancelled"), null, Utc).OutcomeText);
            Xunit.Assert.Equal("From cache", new EasyAI.Desktop.Services.Agent.AgentChatEntryViewModel(
                Make("agent", "current", "economy", 1, 0, 0, 0m, 0, "cached"), null, Utc).OutcomeText);
        }

        [Xunit.Fact]
        public void CostTextKeepsTinyCostsVisibleAndUnknownValuesAsStored()
        {
            Xunit.Assert.Equal("$0.004", new EasyAI.Desktop.Services.Agent.AgentChatEntryViewModel(
                Make("agent", "weird", "", 0, 0, 0, 0.004m, 1, "ok"), null, Utc).CostText);
            Xunit.Assert.Equal("$0.00", new EasyAI.Desktop.Services.Agent.AgentChatEntryViewModel(
                Make("agent", "weird", "", 0, 0, 0, 0m, 1, "ok"), null, Utc).CostText);
            Xunit.Assert.Equal("weird", new EasyAI.Desktop.Services.Agent.AgentChatEntryViewModel(
                Make("agent", "weird", "", 0, 0, 0, 0m, 1, "ok"), null, Utc).HeaderText);
        }
    }
}
