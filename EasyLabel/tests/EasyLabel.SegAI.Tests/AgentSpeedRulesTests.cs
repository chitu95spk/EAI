using EasyAI.Desktop.Services.Agent;

namespace EasyLabel.SegAI.Tests;

public sealed class AgentSpeedRulesTests
{
    [Theory]
    [InlineData(2, 8.0, 2)]
    [InlineData(16, 32.0, 16)]
    [InlineData(32, 128.0, 32)]
    [InlineData(64, 256.0, 32)]
    [InlineData(16, 8.0, 4)]
    [InlineData(12, 64.0, 8)]
    [InlineData(4, 1.0, 1)]
    [InlineData(1, 64.0, 1)]
    [InlineData(0, 0.0, 1)]
    public void MaxWorkersFollowsCoresAndMemory(int cores, double ramGb, int expected)
    {
        Assert.Equal(expected, AgentSpeedRules.MaxWorkers(cores, ramGb));
    }

    [Fact]
    public void AvailableStopsStopAtTheCap()
    {
        Assert.Equal(new[] { 1, 2 }, AgentSpeedRules.AvailableStops(2));
        Assert.Equal(new[] { 1, 2, 4, 8, 16 }, AgentSpeedRules.AvailableStops(16));
        Assert.Equal(new[] { 1 }, AgentSpeedRules.AvailableStops(0));
        Assert.Equal(6, AgentSpeedRules.AvailableStops(32).Count);
    }

    [Theory]
    [InlineData(1, 2, 1)]
    [InlineData(3, 8, 2)]
    [InlineData(32, 2, 2)]
    [InlineData(32, 16, 16)]
    [InlineData(0, 16, 1)]
    [InlineData(-5, 16, 1)]
    public void StoredValueIsClampedToTheCapAndSnappedToAStop(int stored, int cap, int expected)
    {
        Assert.Equal(expected, AgentSpeedRules.ClampToCap(stored, cap));
    }

    [Fact]
    public void IndexAndTextOfAStop()
    {
        System.Collections.Generic.IReadOnlyList<int> stops = AgentSpeedRules.AvailableStops(8);
        Assert.Equal(3, AgentSpeedRules.IndexOf(8, stops));
        Assert.Equal(1, AgentSpeedRules.IndexOf(2, stops));
        Assert.Equal(0, AgentSpeedRules.IndexOf(1, stops));
        Assert.Equal("x16", AgentSpeedRules.StopText(16));
    }
}
