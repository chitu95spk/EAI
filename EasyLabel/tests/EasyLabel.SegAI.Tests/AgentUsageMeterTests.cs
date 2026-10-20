namespace EasyLabel.SegAI.Tests;

public sealed class AgentUsageMeterTests
{
    private static EasyAI.Desktop.Services.Agent.AgentPriceTable Prices()
    {
        return new EasyAI.Desktop.Services.Agent.AgentPriceTable
        {
            Entries = new List<EasyAI.Desktop.Services.Agent.AgentPriceEntry>
            {
                new EasyAI.Desktop.Services.Agent.AgentPriceEntry
                {
                    Provider = EasyAI.Desktop.Services.Agent.AgentPriceTable.ClaudeProvider,
                    Model = "Claude Sonnet 5.5",
                    InputPerMTok = 3,
                    OutputPerMTok = 15,
                    CachedInputPerMTok = 0.3,
                },
            },
        };
    }

    private static string AssistantLine(string id, long input, long cacheWrite, long cacheRead, long output)
    {
        return "{\"type\":\"assistant\",\"message\":{\"id\":\"" + id + "\",\"model\":\"claude-sonnet-5-5\",\"usage\":{"
            + "\"input_tokens\":" + input
            + ",\"cache_creation_input_tokens\":" + cacheWrite
            + ",\"cache_read_input_tokens\":" + cacheRead
            + ",\"output_tokens\":" + output + "}}}";
    }

    [Fact]
    public void RepeatedLinesOfOneMessageAreCountedOnce()
    {
        EasyAI.Desktop.Services.Agent.AgentUsageMeter meter = new EasyAI.Desktop.Services.Agent.AgentUsageMeter(Prices(), "claude-sonnet-5-5");
        string line = AssistantLine("msg_1", 1_000_000, 0, 0, 0);

        Assert.True(meter.ObserveLine(line));
        Assert.True(meter.ObserveLine(line));
        Assert.True(meter.ObserveLine(line));

        Assert.Equal(1, meter.Turns);
        Assert.Equal(1_000_000L, meter.InputTokens);
        Assert.Equal(3.0, meter.TokenCostUsd, 6);
    }

    [Fact]
    public void LastLineOfAMessageReplacesItsEarlierUsage()
    {
        EasyAI.Desktop.Services.Agent.AgentUsageMeter meter = new EasyAI.Desktop.Services.Agent.AgentUsageMeter(Prices(), "claude-sonnet-5-5");

        meter.ObserveLine(AssistantLine("msg_1", 500_000, 0, 0, 0));
        meter.ObserveLine(AssistantLine("msg_1", 1_000_000, 0, 0, 0));

        Assert.Equal(1, meter.Turns);
        Assert.Equal(3.0, meter.TokenCostUsd, 6);
    }

    [Fact]
    public void DistinctMessageIdsAreDifferentTurns()
    {
        EasyAI.Desktop.Services.Agent.AgentUsageMeter meter = new EasyAI.Desktop.Services.Agent.AgentUsageMeter(Prices(), "claude-sonnet-5-5");

        meter.ObserveLine(AssistantLine("msg_1", 1_000_000, 0, 0, 0));
        meter.ObserveLine(AssistantLine("msg_2", 1_000_000, 0, 0, 0));

        Assert.Equal(2, meter.Turns);
        Assert.Equal(6.0, meter.TokenCostUsd, 6);
    }

    [Fact]
    public void CacheWritesAreBilledAboveInputAndCacheReadsAtTheCachedRate()
    {
        EasyAI.Desktop.Services.Agent.AgentUsageMeter writes = new EasyAI.Desktop.Services.Agent.AgentUsageMeter(Prices(), "claude-sonnet-5-5");
        writes.AddUsage("w", "claude-sonnet-5-5", 0, 1_000_000, 0, 0);
        Assert.Equal(3.75, writes.TokenCostUsd, 6);

        EasyAI.Desktop.Services.Agent.AgentUsageMeter reads = new EasyAI.Desktop.Services.Agent.AgentUsageMeter(Prices(), "claude-sonnet-5-5");
        reads.AddUsage("r", "claude-sonnet-5-5", 0, 0, 1_000_000, 0);
        Assert.Equal(0.3, reads.TokenCostUsd, 6);
    }

    [Fact]
    public void OutputTokensArePricedAtTheOutputRate()
    {
        EasyAI.Desktop.Services.Agent.AgentUsageMeter meter = new EasyAI.Desktop.Services.Agent.AgentUsageMeter(Prices(), "claude-sonnet-5-5");

        meter.AddUsage("o", "claude-sonnet-5-5", 0, 0, 0, 1_000_000);

        Assert.Equal(15.0, meter.TokenCostUsd, 6);
        Assert.Equal(1_000_000L, meter.OutputTokens);
    }

    [Fact]
    public void TurnsWithoutPriceCountTheFloorAndAreEstimated()
    {
        EasyAI.Desktop.Services.Agent.AgentUsageMeter meter = new EasyAI.Desktop.Services.Agent.AgentUsageMeter(null, "unknown-model", 0.05);

        meter.AddUsage(null, "unknown-model", 100, 0, 0, 100);
        meter.AddUsage(null, "unknown-model", 100, 0, 0, 100);
        meter.AddUsage(null, "unknown-model", 100, 0, 0, 100);

        Assert.Equal(3, meter.Turns);
        Assert.Equal(0.15, meter.CostUsd, 6);
        Assert.True(meter.IsEstimated);
    }

    [Fact]
    public void CostIsTheLargerOfPricedTokensAndTheTurnFloor()
    {
        EasyAI.Desktop.Services.Agent.AgentUsageMeter meter = new EasyAI.Desktop.Services.Agent.AgentUsageMeter(Prices(), "claude-sonnet-5-5", 0.05);
        meter.AddUsage("m1", "claude-sonnet-5-5", 1_000_000, 0, 0, 0);

        Assert.Equal(3.0, meter.CostUsd, 6);
    }

    [Fact]
    public void ResultLineCostReplacesTheEstimate()
    {
        EasyAI.Desktop.Services.Agent.AgentUsageMeter meter = new EasyAI.Desktop.Services.Agent.AgentUsageMeter(Prices(), "claude-sonnet-5-5", 0.05);
        meter.AddUsage("m1", "claude-sonnet-5-5", 1_000_000, 0, 0, 0);
        Assert.True(meter.IsEstimated);

        Assert.True(meter.ObserveLine("{\"type\":\"result\",\"is_error\":false,\"total_cost_usd\":0.42,\"num_turns\":2}"));

        Assert.True(meter.HasResultCost);
        Assert.False(meter.IsEstimated);
        Assert.Equal(0.42, meter.CostUsd, 6);
    }

    [Fact]
    public void InvalidResultCostIsIgnored()
    {
        EasyAI.Desktop.Services.Agent.AgentUsageMeter meter = new EasyAI.Desktop.Services.Agent.AgentUsageMeter(null, null, 0.05);

        meter.SetResultCost(-1);
        meter.SetResultCost(double.NaN);
        meter.SetResultCost(double.PositiveInfinity);

        Assert.False(meter.HasResultCost);
        Assert.True(meter.IsEstimated);
    }

    [Fact]
    public void BrokenAndUnrelatedLinesChangeNothing()
    {
        EasyAI.Desktop.Services.Agent.AgentUsageMeter meter = new EasyAI.Desktop.Services.Agent.AgentUsageMeter(Prices(), "claude-sonnet-5-5");

        Assert.False(meter.ObserveLine(null));
        Assert.False(meter.ObserveLine(string.Empty));
        Assert.False(meter.ObserveLine("{\"type\":\"assistant\",\"message\":{\"id\":\"x\",\"usage\":"));
        Assert.False(meter.ObserveLine("{\"type\":\"user\",\"message\":{\"content\":[]}}"));
        Assert.False(meter.ObserveLine("plain text line"));

        Assert.Equal(0, meter.Turns);
        Assert.Equal(0.0, meter.CostUsd, 6);
    }

    [Fact]
    public void InitLineSetsTheModelWithoutCountingAnything()
    {
        EasyAI.Desktop.Services.Agent.AgentUsageMeter meter = new EasyAI.Desktop.Services.Agent.AgentUsageMeter(Prices());

        bool changed = meter.ObserveLine("{\"type\":\"system\",\"subtype\":\"init\",\"model\":\"claude-sonnet-5-5\"}");

        Assert.False(changed);
        Assert.Equal("claude-sonnet-5-5", meter.Model);
        Assert.Equal(0, meter.Turns);
    }

    [Fact]
    public void NegativeTokenCountsCountAsZero()
    {
        EasyAI.Desktop.Services.Agent.AgentUsageMeter meter = new EasyAI.Desktop.Services.Agent.AgentUsageMeter(Prices(), "claude-sonnet-5-5");

        meter.AddUsage("m1", "claude-sonnet-5-5", -5, 0, 0, -1);

        Assert.Equal(0L, meter.InputTokens);
        Assert.Equal(0L, meter.OutputTokens);
        Assert.Equal(0.0, meter.TokenCostUsd, 6);
    }
}
