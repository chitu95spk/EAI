using EasyAI.Desktop.Services;
using EasyAI.Desktop.Services.Agent;
using Xunit.Abstractions;

namespace EasyLabel.SegAI.Tests;

/// <summary>
/// One real Claude Code run through <see cref="AgentRunner"/> (real claude.exe, the built EasyLabel.Ai.Mcp.exe, SAM2 models).
/// Opt-in: set EAI_REAL_AGENT=1 (it spends a few cents of the signed-in Claude account); otherwise it reports SKIPPED and proves nothing.
/// Env: EAI_AGENT_APPDIR = folder of the built EasyAI.exe (has the agent subfolder), EAI_AGENT_IMAGE = the image to label,
/// EAI_AGENT_RUNS = where run folders and the cache go (a project-local folder).
/// </summary>
[Trait("Category", "Agent")]
public sealed class RealAgentRunTests
{
    private readonly ITestOutputHelper _out;

    public RealAgentRunTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public async Task RealClaudeProposesAPolygon()
    {
        string? appDir = Environment.GetEnvironmentVariable("EAI_AGENT_APPDIR");
        string? image = Environment.GetEnvironmentVariable("EAI_AGENT_IMAGE");
        string? runs = Environment.GetEnvironmentVariable("EAI_AGENT_RUNS");
        if (Environment.GetEnvironmentVariable("EAI_REAL_AGENT") != "1" || string.IsNullOrEmpty(appDir) || string.IsNullOrEmpty(image) || string.IsNullOrEmpty(runs))
        {
            _out.WriteLine("SKIPPED (nothing proven): set EAI_REAL_AGENT=1, EAI_AGENT_APPDIR, EAI_AGENT_IMAGE, EAI_AGENT_RUNS");
            return;
        }

        var runner = new AgentRunner(new SystemAgentEnvironment(), appDir, Path.Combine(runs, "runs"), Path.Combine(runs, "cache"));
        _out.WriteLine("claude=" + runner.FindClaude() + " mcp=" + runner.FindMcp());
        Assert.NotNull(runner.FindClaude());
        Assert.NotNull(runner.FindMcp());

        var input = new AgentRunInput(
            image, Path.GetFileName(image), 2520, 2600, new[] { "NG", "OK" }, Array.Empty<AgentShapeInfo>(),
            "Find the top-left-most metal clip component in the image (a small bracket on the board) and propose its outline as a polygon labelled OK.",
            Environment.ExpandEnvironmentVariables(@"%LOCALAPPDATA%\EasyAI\models"), 1, true);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        AgentRunResult result = await runner.RunAsync(input, CancellationToken.None);
        _out.WriteLine($"kind={result.Kind} proposals={result.Proposals.Count} cost=${result.CostUsd:F4} turns={result.Turns} wall={sw.Elapsed.TotalSeconds:F1}s");
        _out.WriteLine("agent said: " + result.ResultText);
        Assert.Equal(AgentOutcomeKind.Success, result.Kind);
        Assert.NotEmpty(result.Proposals);
    }
}
