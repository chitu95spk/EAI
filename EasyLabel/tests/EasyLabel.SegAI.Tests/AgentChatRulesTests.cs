namespace EasyLabel.SegAI.Tests
{
    public sealed class AgentChatRulesTests
    {
        [Xunit.Fact]
        public void ModeAndEffortKeysMatchTheSettingsText()
        {
            Xunit.Assert.Equal("current", EasyAI.Desktop.Services.Agent.AgentChatRules.ModeKey(EasyAI.Desktop.Services.Agent.AgentBatchMode.Current));
            Xunit.Assert.Equal("all", EasyAI.Desktop.Services.Agent.AgentChatRules.ModeKey(EasyAI.Desktop.Services.Agent.AgentBatchMode.All));
            Xunit.Assert.Equal("remaining", EasyAI.Desktop.Services.Agent.AgentChatRules.ModeKey(EasyAI.Desktop.Services.Agent.AgentBatchMode.Remaining));
            Xunit.Assert.Equal("low", EasyAI.Desktop.Services.Agent.AgentChatRules.EffortKey(EasyAI.Desktop.Services.Agent.AgentEffort.Economy));
            Xunit.Assert.Equal("medium", EasyAI.Desktop.Services.Agent.AgentChatRules.EffortKey(EasyAI.Desktop.Services.Agent.AgentEffort.Normal));
            Xunit.Assert.Equal("high", EasyAI.Desktop.Services.Agent.AgentChatRules.EffortKey(EasyAI.Desktop.Services.Agent.AgentEffort.Best));
            Xunit.Assert.Equal("xhigh", EasyAI.Desktop.Services.Agent.AgentChatRules.EffortKey(EasyAI.Desktop.Services.Agent.AgentEffort.XHigh));
            Xunit.Assert.Equal("max", EasyAI.Desktop.Services.Agent.AgentChatRules.EffortKey(EasyAI.Desktop.Services.Agent.AgentEffort.Max));
        }

        [Xunit.Fact]
        public void TheMenuOffersTheModelLevelsAndCoercesWhenTheModelChanges()
        {
            System.Collections.Generic.IReadOnlyList<string> offered = EasyAI.Desktop.Services.Agent.AgentChatRules.SelectableEfforts(new[] { "max", "low", "high", "bogus" });
            Xunit.Assert.Equal(new[] { "low", "high", "max" }, System.Linq.Enumerable.ToArray(offered));
            Xunit.Assert.Equal(new[] { "low", "medium", "high" }, System.Linq.Enumerable.ToArray(EasyAI.Desktop.Services.Agent.AgentChatRules.SelectableEfforts(null)));
            Xunit.Assert.Equal("high", EasyAI.Desktop.Services.Agent.AgentChatRules.CoerceEffort("high", offered));
            Xunit.Assert.Equal("max", EasyAI.Desktop.Services.Agent.AgentChatRules.CoerceEffort("xhigh", offered));
            Xunit.Assert.Equal("medium", EasyAI.Desktop.Services.Agent.AgentChatRules.CoerceEffort("xhigh", new[] { "low", "medium", "high" }));
            Xunit.Assert.Equal("Sonnet 5.5 Medium", EasyAI.Desktop.Services.Agent.AgentChatRules.PillText("Sonnet 5.5", "Medium"));
        }

        [Xunit.Fact]
        public void TheMenuRowsAreModelsSeparatorEfforts()
        {
            EasyAI.Desktop.Services.Agent.AgentModelInfo[] models =
            {
                new EasyAI.Desktop.Services.Agent.AgentModelInfo("sonnet", "Sonnet", string.Empty, new[] { "low", "medium" }),
                new EasyAI.Desktop.Services.Agent.AgentModelInfo("opus", "Opus", string.Empty, new[] { "low", "medium", "max" }),
            };
            System.Collections.Generic.IReadOnlyList<EasyAI.Desktop.Services.Agent.AgentPillItem> items = EasyAI.Desktop.Services.Agent.AgentChatRules.BuildPillItems(
                models, "opus", new[] { "low", "medium", "max" }, "medium", level => level);
            Xunit.Assert.Equal(6, items.Count);
            Xunit.Assert.False(items[0].IsChecked);
            Xunit.Assert.True(items[1].IsChecked);
            Xunit.Assert.Equal("AgentChatModel_opus", items[1].AutomationId);
            Xunit.Assert.True(items[2].IsSeparator);
            Xunit.Assert.Equal("AgentChatEffort_low", items[3].AutomationId);
            Xunit.Assert.True(items[4].IsChecked);
            Xunit.Assert.False(items[5].IsChecked);
        }

        [Xunit.Fact]
        public void TheKeysRoundTripThroughTheParsers()
        {
            foreach (EasyAI.Desktop.Services.Agent.AgentBatchMode mode in new[]
            {
                EasyAI.Desktop.Services.Agent.AgentBatchMode.Current,
                EasyAI.Desktop.Services.Agent.AgentBatchMode.All,
                EasyAI.Desktop.Services.Agent.AgentBatchMode.Remaining,
            })
            {
                string key = EasyAI.Desktop.Services.Agent.AgentChatRules.ModeKey(mode);
                Xunit.Assert.Equal(mode, EasyAI.Desktop.Services.Agent.AgentBatchPlanner.ParseMode(key, EasyAI.Desktop.Services.Agent.AgentBatchMode.Current));
            }

            foreach (EasyAI.Desktop.Services.Agent.AgentEffort effort in new[]
            {
                EasyAI.Desktop.Services.Agent.AgentEffort.Economy,
                EasyAI.Desktop.Services.Agent.AgentEffort.Normal,
                EasyAI.Desktop.Services.Agent.AgentEffort.Best,
                EasyAI.Desktop.Services.Agent.AgentEffort.XHigh,
                EasyAI.Desktop.Services.Agent.AgentEffort.Max,
            })
            {
                string key = EasyAI.Desktop.Services.Agent.AgentChatRules.EffortKey(effort);
                Xunit.Assert.Equal(effort, EasyAI.Desktop.Services.Agent.AgentRunOptions.ParseEffort(key));
            }
        }

        [Xunit.Fact]
        public void TheEffortIndexFollowsTheSegmentOrder()
        {
            Xunit.Assert.Equal(0, EasyAI.Desktop.Services.Agent.AgentChatRules.EffortIndex(EasyAI.Desktop.Services.Agent.AgentEffort.Economy));
            Xunit.Assert.Equal(1, EasyAI.Desktop.Services.Agent.AgentChatRules.EffortIndex(EasyAI.Desktop.Services.Agent.AgentEffort.Normal));
            Xunit.Assert.Equal(2, EasyAI.Desktop.Services.Agent.AgentChatRules.EffortIndex(EasyAI.Desktop.Services.Agent.AgentEffort.Best));
        }

        [Xunit.Fact]
        public void TheCapsCaptionShowsImagesAndCost()
        {
            string format = "{0} images · ${1} max";
            string noCost = "{0} images · no cost cap";
            Xunit.Assert.Equal("50 images · $2.00 max", EasyAI.Desktop.Services.Agent.AgentChatRules.FormatCaps(format, noCost, 50, 2.0));
            Xunit.Assert.Equal("7 images · $0.50 max", EasyAI.Desktop.Services.Agent.AgentChatRules.FormatCaps(format, noCost, 7, 0.5));
            Xunit.Assert.Equal("50 images · no cost cap", EasyAI.Desktop.Services.Agent.AgentChatRules.FormatCaps(format, noCost, 50, 0));
            Xunit.Assert.Equal("50 images · no cost cap", EasyAI.Desktop.Services.Agent.AgentChatRules.FormatCaps(format, noCost, 50, double.NaN));
            Xunit.Assert.Equal("1 images · $1.00 max", EasyAI.Desktop.Services.Agent.AgentChatRules.FormatCaps(format, noCost, -3, 1.0));
        }

        [Xunit.Fact]
        public void TheCapsCaptionIsCultureIndependent()
        {
            System.Globalization.CultureInfo old = System.Globalization.CultureInfo.CurrentCulture;
            try
            {
                System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("vi-VN");
                Xunit.Assert.Equal("50 images · $2.50 max", EasyAI.Desktop.Services.Agent.AgentChatRules.FormatCaps("{0} images · ${1} max", "{0}", 50, 2.5));
            }
            finally
            {
                System.Globalization.CultureInfo.CurrentCulture = old;
            }
        }

        [Xunit.Fact]
        public void OnlyAPlainEnterSubmits()
        {
            Xunit.Assert.True(EasyAI.Desktop.Services.Agent.AgentChatRules.IsSubmitKey(true, false, false, false));
            Xunit.Assert.False(EasyAI.Desktop.Services.Agent.AgentChatRules.IsSubmitKey(true, true, false, false));
            Xunit.Assert.False(EasyAI.Desktop.Services.Agent.AgentChatRules.IsSubmitKey(true, false, true, false));
            Xunit.Assert.False(EasyAI.Desktop.Services.Agent.AgentChatRules.IsSubmitKey(true, false, false, true));
            Xunit.Assert.False(EasyAI.Desktop.Services.Agent.AgentChatRules.IsSubmitKey(false, false, false, false));
        }

        [Xunit.Fact]
        public void TheCheckPromptNeedsNoUserText()
        {
            string empty = EasyAI.Desktop.Services.Agent.AgentChatRules.ComposeCheckPrompt("   ");
            Xunit.Assert.Contains("check skill", empty);
            Xunit.Assert.DoesNotContain("Extra instructions", empty);
            Xunit.Assert.Equal(empty, EasyAI.Desktop.Services.Agent.AgentChatRules.ComposeCheckPrompt(null));

            string extra = EasyAI.Desktop.Services.Agent.AgentChatRules.ComposeCheckPrompt("  focus on the pads  ");
            Xunit.Assert.StartsWith(empty, extra);
            Xunit.Assert.EndsWith("Extra instructions from the user: focus on the pads", extra);
        }

        [Xunit.Fact]
        public void TheCheckPromptSatisfiesTheAgentRunRule()
        {
            // The Run rule refuses an empty prompt; Check composes its own text, so it passes with an empty prompt box.
            string prompt = EasyAI.Desktop.Services.Agent.AgentChatRules.ComposeCheckPrompt(string.Empty);
            Xunit.Assert.Equal(
                EasyAI.Desktop.ViewModels.AutoLabelReason.None,
                EasyAI.Desktop.ViewModels.AutoLabelSourceRules.AgentRunReason(true, true, prompt));
        }
    }
}
