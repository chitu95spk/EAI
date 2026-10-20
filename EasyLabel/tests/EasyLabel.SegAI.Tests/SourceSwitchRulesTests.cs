namespace EasyLabel.SegAI.Tests;

/// <summary>Source-switch confirmation rule (linked from EasyAI.Wpf, no WPF types).</summary>
public sealed class SourceSwitchRulesTests
{
    [Theory]
    [InlineData(false, 0, EasyAI.Desktop.ViewModels.SourceSwitchDecision.NoPrompt)]
    [InlineData(false, 1, EasyAI.Desktop.ViewModels.SourceSwitchDecision.AskDropPending)]
    [InlineData(false, 7, EasyAI.Desktop.ViewModels.SourceSwitchDecision.AskDropPending)]
    [InlineData(true, 0, EasyAI.Desktop.ViewModels.SourceSwitchDecision.AskCancelTask)]
    [InlineData(true, 3, EasyAI.Desktop.ViewModels.SourceSwitchDecision.AskCancelTask)]
    public void Decide_FollowsTheRuleTable(bool busy, int pending, EasyAI.Desktop.ViewModels.SourceSwitchDecision expected)
    {
        Assert.Equal(expected, EasyAI.Desktop.ViewModels.SourceSwitchRules.Decide(busy, pending));
    }
}
