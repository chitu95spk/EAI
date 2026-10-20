using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using EasyAI.Desktop.Infrastructure;
using EasyAI.Desktop.Services;
using EasyAI.Desktop.Services.Agent;

namespace EasyLabel.SegAI.Tests;

public sealed class AgentOfflineAndCapabilityTests
{
    private sealed class NoRequestsHandler : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            throw new InvalidOperationException("Offline refresh attempted an HTTP request.");
        }
    }

    [Fact]
    public async Task OfflinePriceRefreshDoesNotRequestOrWriteAnything()
    {
        using EaiCostScratch scratch = new();
        string path = scratch.File("prices.json");
        const string original = "{\"Entries\":[],\"FetchedUtc\":\"2026-01-01T00:00:00Z\"}";
        File.WriteAllText(path, original);
        using NoRequestsHandler handler = new();
        using HttpClient http = new(handler);

        AgentPriceRefreshResult result = await AgentPriceStore.RefreshAsync(http, path, CancellationToken.None, () => true);

        Assert.Equal(0, handler.Requests);
        Assert.False(result.Updated);
        Assert.Equal(AgentRunner.OfflineMessage, result.Error);
        Assert.Equal(original, File.ReadAllText(path));
    }

    [Fact]
    public void OfflineSettingDefaultsToFalseAndLoadsAnOldFileWithUnknownFields()
    {
        Assert.False(new AppSettings().AgentOfflineOnly);
        using EaiCostScratch scratch = new();
        string path = scratch.File("settings.json");
        File.WriteAllText(path, "{\"SchemaVersion\":1,\"AgentPrompt\":\"Keep this\",\"FutureSetting\":42}");

        AppSettings settings = Assert.IsType<AppSettings>(JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path)));

        Assert.False(settings.AgentOfflineOnly);
        Assert.Equal("Keep this", settings.AgentPrompt);
        Assert.True(SettingsAccessor.TrySetBool(settings, SettingsAccessor.RowAgentOfflineOnly, true));
        File.WriteAllText(path, JsonSerializer.Serialize(settings));
        AppSettings loaded = Assert.IsType<AppSettings>(JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path)));
        Assert.True(SettingsAccessor.TryGetBool(loaded, SettingsAccessor.RowAgentOfflineOnly, out bool offline));
        Assert.True(offline);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("Usage: claude --model --help", false)]
    [InlineData("  --max-budget-usd <amount>  Maximum spend", true)]
    [InlineData("Usage\nOptions:\n--max-budget-usd\n--model", true)]
    public void HelpParserRecognizesTheBudgetFlag(string? help, bool expected)
    {
        Assert.Equal(expected, AgentCliCapabilities.ParseSupportsMaxBudget(help));
    }

    [Fact]
    public async Task OfflineCapabilityProbeNeverCallsTheProcessFactory()
    {
        int starts = 0;
        AgentCliCapabilities capabilities = new(() => true, _ =>
        {
            Interlocked.Increment(ref starts);
            throw new InvalidOperationException("Offline capability probe attempted to start a process.");
        });

        Assert.False(await capabilities.ProbeAsync("claude.exe"));
        Assert.False(capabilities.SupportsMaxBudget);
        Assert.Equal(0, starts);
    }

    [Theory]
    [InlineData(AgentTool.Claude)]
    [InlineData(AgentTool.Codex)]
    public async Task LiveOfflineGuardPreventsDiscoveryByAuthenticationFollowUps(AgentTool tool)
    {
        FakeAgentEnvironment env = new();
        env.Variables[ClaudeLocator.EnvironmentName] = @"C:\fake\claude.exe";
        env.Variables[CodexLocator.EnvironmentName] = @"C:\fake\codex.exe";
        env.AddFile(@"C:\fake\claude.exe");
        env.AddFile(@"C:\fake\codex.exe");
        bool offline = false;
        AgentAuthService auth = new(new AgentOfflineEnvironment(env, () => offline));
        Assert.NotNull(auth.Locate(tool));

        offline = true;

        Assert.Null(auth.Locate(tool));
        Assert.Equal(AgentAuthState.NotInstalled, (await auth.StatusAsync(tool, CancellationToken.None)).State);
        offline = false;
        Assert.NotNull(auth.Locate(tool));
    }

    [Fact]
    public async Task ProbeFailureIsCachedByPathAndWriteTimeButOfflineSkipsAreNot()
    {
        using EaiCostScratch scratch = new();
        string path = scratch.File("claude.exe");
        File.WriteAllText(path, "not an executable");
        bool offline = true;
        int starts = 0;
        ProcessStartInfo FailStart(string _)
        {
            Interlocked.Increment(ref starts);
            throw new InvalidOperationException("An unavailable CLI is treated as unsupported.");
        }

        AgentCliCapabilities capabilities = new(() => offline, FailStart);
        Assert.False(await capabilities.ProbeAsync(path));
        Assert.Equal(0, starts);
        offline = false;
        Task<bool>[] probes = Enumerable.Range(0, 4).Select(_ => capabilities.ProbeAsync(path)).ToArray();
        await Task.WhenAll(probes);
        Assert.All(probes, probe => Assert.False(probe.Result));
        Assert.Equal(1, starts);
        Assert.False(await new AgentCliCapabilities(() => false, FailStart).ProbeAsync(path));
        Assert.Equal(1, starts);
        File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddMinutes(1));
        Assert.False(await capabilities.ProbeAsync(path));
        Assert.Equal(2, starts);
    }
}

/// <summary>All new cost/offline fixtures stay under the repository's task scratch directory.</summary>
internal sealed class EaiCostScratch : IDisposable
{
    private readonly string _directory;

    public EaiCostScratch()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root != null && !Directory.Exists(Path.Combine(root.FullName, "EasyLabel", "src"))) root = root.Parent;
        if (root == null) throw new InvalidOperationException("The EasyLabel repository root was not found.");
        _directory = Path.Combine(root.FullName, "tmp", "EAI-COST", "tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    public string File(string name) => Path.Combine(_directory, name);

    public void Dispose() => Directory.Delete(_directory, true);
}
