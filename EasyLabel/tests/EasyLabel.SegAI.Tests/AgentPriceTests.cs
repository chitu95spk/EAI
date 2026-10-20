using EasyAI.Desktop.Services.Agent;

namespace EasyLabel.SegAI.Tests;

public sealed class AgentPriceTests
{
    private const string ClaudeHtml =
        "<table><tr><th>Model</th><th>Base tokens</th><th>Prompt caching</th></tr>"
        + "<tr><th>Name</th><th>Input</th><th>Output</th><th>5m writes</th><th>1h writes</th><th>Hits and refreshes</th></tr>"
        + "<tr><td><div><a href=\"/x\">Claude Sonnet 5.5</a><span>The best</span></div></td><td>$2 / MTok</td><td>$10 / MTok</td><td>$2.50 / MTok</td><td>$4 / MTok</td><td>$0.10 / MTok</td></tr>"
        + "<tr><td rowSpan=\"2\"><a>Claude Haiku 5.5</a></td><td>$0.10 / MTok<br>for prompts up to 100k</td><td>$0.50 / MTok</td><td>$0.125 / MTok</td><td>$0.20 / MTok</td><td>$0.01 / MTok</td></tr>"
        + "<tr><td>$0.20 / MTok for longer</td><td>$1 / MTok</td><td>$1</td><td>$1</td><td>$1</td></tr></table>";

    private const string OpenAiHtml =
        "<table><tr><th></th><th>Short context</th><th>Long context</th></tr>"
        + "<tr><th>Model</th><th>Input</th><th>Cached input</th><th>Cache writes</th><th>Output</th><th>Input</th><th>Cached input</th><th>Cache writes</th><th>Output</th></tr>"
        + "<tr><td>gpt-6.1-sol</td><td>$2.00</td><td>$0.10</td><td>$2.50</td><td>$10.00</td><td>$4.00</td><td>$0.20</td><td>$5.00</td><td>$15.00</td></tr></table>"
        + "<table><tr><th>Model</th><th>Input</th><th>Cached input</th><th>Cache writes</th><th>Output</th></tr><tr><td>gpt-6.1-sol</td><td>$1</td><td>$1</td><td>$1</td><td>$5</td></tr></table>";

    [Fact]
    public void ParseClaude_ReadsNameAnchorAndFirstPrice_SkipsSubRows()
    {
        List<AgentPriceEntry> list = AgentPriceParser.ParseClaude(ClaudeHtml);
        Assert.Equal(2, list.Count);
        Assert.Equal("Claude Sonnet 5.5", list[0].Model);
        Assert.Equal(2d, list[0].InputPerMTok);
        Assert.Equal(10d, list[0].OutputPerMTok);
        Assert.Equal(0.1d, list[0].CachedInputPerMTok);
        Assert.Equal(0.1d, list[1].InputPerMTok);
        Assert.Equal(0.5d, list[1].OutputPerMTok);
    }

    [Fact]
    public void ParseOpenAi_UsesShortContextOfTheFirstTable()
    {
        List<AgentPriceEntry> list = AgentPriceParser.ParseOpenAi(OpenAiHtml);
        AgentPriceEntry only = Assert.Single(list);
        Assert.Equal("gpt-6.1-sol", only.Model);
        Assert.Equal(2d, only.InputPerMTok);
        Assert.Equal(10d, only.OutputPerMTok);
        Assert.Equal(0.1d, only.CachedInputPerMTok);
    }

    [Fact]
    public void Parse_NoTable_GivesEmpty()
    {
        Assert.Empty(AgentPriceParser.ParseClaude("<html></html>"));
        Assert.Empty(AgentPriceParser.ParseOpenAi(null));
    }

    [Fact]
    public void Find_MatchesCliNamesAndFamilies()
    {
        AgentPriceTable table = new AgentPriceTable();
        table.Entries.AddRange(AgentPriceParser.ParseClaude(ClaudeHtml));
        table.Entries.AddRange(AgentPriceParser.ParseOpenAi(OpenAiHtml));
        Assert.Equal("Claude Sonnet 5.5", table.Find("claude", "claude-sonnet-5-5")!.Model);
        Assert.Equal("Claude Sonnet 5.5", table.Find("claude", "sonnet")!.Model);
        Assert.Equal("gpt-6.1-sol", table.Find("openai", "gpt-6.1-sol")!.Model);
        Assert.Null(table.Find("claude", "unknown-model"));
    }

    [Fact]
    public void CostOf_AndEstimateFallback_UseThePrices()
    {
        AgentPriceTable table = new AgentPriceTable();
        table.Entries.AddRange(AgentPriceParser.ParseClaude(ClaudeHtml));
        Assert.Equal(12d, table.CostOf("claude", "sonnet", 1_000_000, 0, 1_000_000));
        // 20000 in x $2 + 2000 out x $10 per million = $0.06 per image.
        Assert.Equal(0.06d, table.NominalCostPerImage("sonnet")!.Value, 6);
        AgentSpendTracker tracker = AgentSpendTracker.FromFile(new AgentScratch("price-log").Combine("missing.csv"));
        AgentSpendEstimate estimate = tracker.Estimate("sonnet", 10, table);
        Assert.True(estimate.IsKnown);
        Assert.Equal(0.6d, estimate.Usd, 6);
        Assert.False(tracker.Estimate("sonnet", 10).IsKnown);
    }

    [Fact]
    public void Store_RoundTripsThroughAFile()
    {
        using AgentScratch scratch = new AgentScratch("price-store");
        string path = scratch.Combine("sub", "agent-prices.json");
        AgentPriceTable table = new AgentPriceTable { FetchedUtc = new DateTime(2026, 10, 16, 0, 0, 0, DateTimeKind.Utc) };
        table.Entries.AddRange(AgentPriceParser.ParseClaude(ClaudeHtml));
        AgentPriceStore.Save(table, path);
        AgentPriceTable loaded = AgentPriceStore.Load(path);
        Assert.Equal(2, loaded.Entries.Count);
        Assert.Equal(table.FetchedUtc, loaded.FetchedUtc);
        Assert.True(AgentPriceStore.Load(path + ".missing").IsEmpty);
    }

    // Reads pages saved by hand into tmp/EAI-PRICE (not in git); does nothing when they are not there.
    [Fact]
    public void SavedRealPages_GiveModels()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir != null && !System.IO.Directory.Exists(System.IO.Path.Combine(dir, "tmp", "EAI-PRICE")))
        {
            dir = System.IO.Path.GetDirectoryName(dir);
        }

        if (dir == null)
        {
            return;
        }

        string claude = System.IO.Path.Combine(dir, "tmp", "EAI-PRICE", "claude.html");
        string openAi = System.IO.Path.Combine(dir, "tmp", "EAI-PRICE", "openai.html");
        if (System.IO.File.Exists(claude))
        {
            List<AgentPriceEntry> list = AgentPriceParser.ParseClaude(System.IO.File.ReadAllText(claude));
            System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "tmp", "EAI-PRICE", "claude.parsed.txt"), string.Join(Environment.NewLine, list.Select(e => e.Model + " | " + e.InputPerMTok + " | " + e.OutputPerMTok + " | " + e.CachedInputPerMTok)));
            Assert.True(list.Count >= 3);
        }

        if (System.IO.File.Exists(openAi))
        {
            List<AgentPriceEntry> list = AgentPriceParser.ParseOpenAi(System.IO.File.ReadAllText(openAi));
            System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "tmp", "EAI-PRICE", "openai.parsed.txt"), string.Join(Environment.NewLine, list.Select(e => e.Model + " | " + e.InputPerMTok + " | " + e.OutputPerMTok + " | " + e.CachedInputPerMTok)));
            Assert.True(list.Count >= 3);
        }
    }
}
