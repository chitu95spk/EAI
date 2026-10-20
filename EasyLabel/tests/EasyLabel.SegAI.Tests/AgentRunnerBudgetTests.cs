using System.Diagnostics;
using EasyAI.Desktop.Services;
using EasyAI.Desktop.Services.Agent;

namespace EasyLabel.SegAI.Tests;

/// <summary>
/// The spend path of <see cref="AgentRunner"/> (reservation, watchdog, settlement, log line) against a fake agent. The fake prints usage lines
/// like stream-json, so the meter and the watchdog see real input. Prompts select the behaviour: "spend" prints 1M input tokens (3 USD at the
/// test price) and sleeps; "slow" prints a small usage and sleeps; anything else prints a result with a reported cost of 0.02 USD.
/// </summary>
public sealed class AgentRunnerBudgetTests
{
    private const string BudgetScript = """
        $a = @($args)
        $mcpIndex = [array]::IndexOf($a, '--mcp-config')
        $run = Split-Path -Parent $a[$mcpIndex + 1]
        $pIndex = [array]::IndexOf($a, '-p')
        $prompt = $a[$pIndex + 1]
        Set-Content -Path (Join-Path $run 'pid.txt') -Value $PID
        if ($prompt -eq 'spend') {
          [Console]::Out.WriteLine('{"type":"assistant","message":{"id":"msg_1","model":"claude-sonnet-5-5","usage":{"input_tokens":1000000,"cache_creation_input_tokens":0,"cache_read_input_tokens":0,"output_tokens":0}}}')
          Start-Sleep -Seconds 120
          exit 0
        }
        if ($prompt -eq 'slow') {
          [Console]::Out.WriteLine('{"type":"assistant","message":{"id":"msg_1","model":"claude-sonnet-5-5","usage":{"input_tokens":1000,"cache_creation_input_tokens":0,"cache_read_input_tokens":0,"output_tokens":100}}}')
          Start-Sleep -Seconds 120
          exit 0
        }
        [Console]::Out.WriteLine('{"type":"result","is_error":false,"result":"done","total_cost_usd":0.02,"num_turns":1}')
        exit 0
        """;

    private sealed class BudgetRig : IDisposable
    {
        private readonly AgentScratch _scratch;
        private int _starts;

        public BudgetRig()
        {
            _scratch = new AgentScratch("budget-runner");
            Env = new FakeAgentEnvironment();
            Env.Variables[ClaudeLocator.EnvironmentName] = @"C:\fake\claude.exe";
            Env.AddFile(@"C:\fake\claude.exe");
            Env.AddFile(@"C:\app\agent\EasyLabel.Ai.Mcp.exe");
            Env.AddFile(@"C:\app\agent\Skills\detect.md", "skill one");
            Script = _scratch.Combine("fake-budget.ps1");
            File.WriteAllText(Script, BudgetScript);
            ImagePath = _scratch.Combine("image.png");
            File.WriteAllBytes(ImagePath, new byte[] { 1, 2, 3, 4 });
            RunsRoot = _scratch.Combine("runs");
            CacheRoot = _scratch.Combine("cache");
        }

        public FakeAgentEnvironment Env { get; }

        public string Script { get; }

        public string ImagePath { get; }

        public string RunsRoot { get; }

        public string CacheRoot { get; }

        public int Starts => _starts;

        public AgentRunner NewRunner(AgentSpendBudget? budget, AgentPriceTable? prices)
        {
            AgentRunner runner = new AgentRunner(
                Env,
                @"C:\app",
                RunsRoot,
                CacheRoot,
                (claude, args, workingDirectory, tempPath) =>
                {
                    Interlocked.Increment(ref _starts);
                    List<string> all = new List<string> { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", Script };
                    all.AddRange(args);
                    return AgentRunner.DefaultStartInfo("powershell.exe", all, workingDirectory, tempPath);
                },
                TimeSpan.FromMinutes(2));
            runner.Budget = budget;
            runner.Prices = prices;
            return runner;
        }

        // The cache is bypassed so every call really starts the fake agent.
        public AgentRunInput Input(string prompt)
        {
            return new AgentRunInput(ImagePath, "image.png", 100, 100, new[] { "NG", "OK" }, Array.Empty<AgentShapeInfo>(), prompt, null, 1, true);
        }

        public string[] RunFolders()
        {
            return Directory.Exists(RunsRoot)
                ? Directory.GetDirectories(RunsRoot).Where(d => AgentRunRetention.IsRunFolderName(Path.GetFileName(d))).ToArray()
                : Array.Empty<string>();
        }

        public void Dispose()
        {
            _scratch.Dispose();
        }
    }

    // Claude Sonnet at 3 USD per million input tokens and 15 per million output tokens.
    private static AgentPriceTable Prices()
    {
        return new AgentPriceTable
        {
            Entries = new List<AgentPriceEntry>
            {
                new AgentPriceEntry { Provider = AgentPriceTable.ClaudeProvider, Model = "Claude Sonnet 5.5", InputPerMTok = 3, OutputPerMTok = 15, CachedInputPerMTok = 0.3 },
            },
        };
    }

    [Fact]
    public async Task WatchdogKillsTheRunAtItsReservationAndRecordsTheSpend()
    {
        using BudgetRig rig = new BudgetRig();
        AgentSpendBudget budget = new AgentSpendBudget(1.0, double.PositiveInfinity, 1);
        AgentRunner runner = rig.NewRunner(budget, Prices());

        AgentRunResult result = await runner.RunAsync(rig.Input("spend"), CancellationToken.None);

        Assert.Equal(AgentOutcomeKind.BudgetLimit, result.Kind);
        Assert.True(result.IsCostEstimated);
        Assert.Equal(3.0, result.CostUsd, 6);
        Assert.Equal(0, budget.ActiveReservations);
        Assert.Equal(3.0, budget.SettledUsd, 6);
        Assert.Equal(0.0, budget.ReservedUsd, 6);

        string folder = Assert.Single(rig.RunFolders());
        int pid = int.Parse(File.ReadAllText(Path.Combine(folder, "pid.txt")).Trim());
        Assert.True(IsGone(pid), "the agent process is still running");

        string[] log = File.ReadAllLines(Path.Combine(rig.RunsRoot, "log.csv"));
        Assert.EndsWith(",BudgetLimit,5,1", log[log.Length - 1]);
    }

    [Fact]
    public async Task CancelledRunStillRecordsAPositiveEstimate()
    {
        using BudgetRig rig = new BudgetRig();
        using CancellationTokenSource cts = new CancellationTokenSource();
        AgentSpendBudget budget = new AgentSpendBudget(1.0, double.PositiveInfinity, 1);
        AgentRunner runner = rig.NewRunner(budget, Prices());

        Task<AgentRunResult> run = runner.RunAsync(rig.Input("slow"), cts.Token);
        await WaitUntilAsync(() => budget.ActiveReservations == 1);
        await Task.Delay(1500);
        cts.Cancel();
        AgentRunResult result = await run;

        Assert.Equal(AgentOutcomeKind.Cancelled, result.Kind);
        Assert.True(result.IsCostEstimated);
        Assert.True(result.CostUsd >= AgentUsageMeter.DefaultFloorUsdPerTurn - 1e-9, "cancel cost " + result.CostUsd.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(0, budget.ActiveReservations);
        Assert.True(budget.SettledUsd > 0);
    }

    [Fact]
    public async Task TwoWorkersShareOneBudgetWithoutOvershootingIt()
    {
        using BudgetRig rig = new BudgetRig();
        using CancellationTokenSource cts = new CancellationTokenSource();
        AgentSpendBudget budget = new AgentSpendBudget(1.0, double.PositiveInfinity, 2);
        AgentRunner first = rig.NewRunner(budget, Prices());
        AgentRunner second = rig.NewRunner(budget, Prices());

        Task<AgentRunResult> a = first.RunAsync(rig.Input("slow"), cts.Token);
        Task<AgentRunResult> b = second.RunAsync(rig.Input("slow"), cts.Token);
        await WaitUntilAsync(() => budget.ActiveReservations == 2);

        // Fair share: the first takes 1.0 / 2 slots = 0.5, the second takes the remaining 0.5 / 1 slot.
        Assert.Equal(1.0, budget.ReservedUsd, 6);
        Assert.True(budget.RemainingUsd <= 1e-9);

        cts.Cancel();
        AgentRunResult ra = await a;
        AgentRunResult rb = await b;

        Assert.Equal(AgentOutcomeKind.Cancelled, ra.Kind);
        Assert.Equal(AgentOutcomeKind.Cancelled, rb.Kind);
        Assert.Equal(0, budget.ActiveReservations);
        Assert.True(budget.SettledUsd <= 1.0 + 1e-9);
    }

    [Fact]
    public async Task SuggestRunIsReservedSettledAndLogged()
    {
        using BudgetRig rig = new BudgetRig();
        AgentSpendBudget budget = new AgentSpendBudget(1.0, double.PositiveInfinity, 1);
        AgentRunner runner = rig.NewRunner(budget, Prices());

        AgentRunResult result = await runner.SuggestAsync(rig.Input("label it"), "en", CancellationToken.None);

        Assert.Equal(AgentOutcomeKind.Success, result.Kind);
        Assert.False(result.IsCostEstimated);
        Assert.Equal(0.02, budget.SettledUsd, 6);
        Assert.Equal(0, budget.ActiveReservations);

        string[] log = File.ReadAllLines(Path.Combine(rig.RunsRoot, "log.csv"));
        Assert.Equal(2, log.Length);
        Assert.Contains(",image.png,", log[1]);
        Assert.Contains(",Success,", log[1]);
        Assert.EndsWith(",0", log[1]);
    }

    [Theory]
    [InlineData(AgentTool.Claude)]
    [InlineData(AgentTool.Codex)]
    public async Task OfflineModeStartsNoProcess(AgentTool tool)
    {
        using EaiCostScratch scratch = new();
        FakeAgentEnvironment env = new();
        env.Variables[ClaudeLocator.EnvironmentName] = @"C:\fake\claude.exe";
        env.Variables[CodexLocator.EnvironmentName] = @"C:\fake\codex.exe";
        env.AddFile(@"C:\fake\claude.exe");
        env.AddFile(@"C:\fake\codex.exe");
        env.AddFile(@"C:\app\agent\EasyLabel.Ai.Mcp.exe");
        env.AddFile(@"C:\app\agent\Skills\detect.md", "skill one");
        string image = scratch.File("image.png");
        File.WriteAllBytes(image, new byte[] { 1, 2, 3, 4 });
        int starts = 0;
        AgentRunner runner = new(env, @"C:\app", scratch.File("runs"), scratch.File("cache"),
            (exe, args, workingDirectory, tempPath) =>
            {
                Interlocked.Increment(ref starts);
                throw new InvalidOperationException("Offline action attempted to start a process.");
            })
        {
            IsOffline = true,
            Tool = tool,
            UserSkillsRoot = null,
            McpServersPath = null,
        };
        AgentRunInput input = new(image, "image.png", 100, 100, new[] { "NG", "OK" }, Array.Empty<AgentShapeInfo>(), "label it", null, 1, true);

        AgentRunResult[] results =
        {
            await runner.RunAsync(input, CancellationToken.None),
            await runner.SuggestAsync(input, "en", CancellationToken.None),
            await runner.RefinePromptAsync(input, "label it", "en", null, CancellationToken.None),
        };

        Assert.All(results, result =>
        {
            Assert.Equal(AgentOutcomeKind.Failed, result.Kind);
            Assert.Equal(AgentRunner.OfflineMessage, result.Message);
        });
        Assert.Equal(0, starts);
    }

    [Fact]
    public async Task ExhaustedBudgetRefusesBeforeAnyProcessStarts()
    {
        using BudgetRig rig = new BudgetRig();
        AgentSpendBudget budget = new AgentSpendBudget(0.01, double.PositiveInfinity, 1);
        AgentRunner runner = rig.NewRunner(budget, Prices());

        AgentRunResult result = await runner.RunAsync(rig.Input("label it"), CancellationToken.None);

        Assert.Equal(AgentOutcomeKind.BudgetLimit, result.Kind);
        Assert.Equal(AgentRunner.BudgetRefusedMessage, result.Message);
        Assert.Equal(0, rig.Starts);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        DateTime limit = DateTime.UtcNow.AddSeconds(30);
        while (!condition())
        {
            if (DateTime.UtcNow > limit)
            {
                throw new TimeoutException("The condition was not reached.");
            }

            await Task.Delay(50);
        }
    }

    private static bool IsGone(int pid)
    {
        try
        {
            using Process process = Process.GetProcessById(pid);
            return process.WaitForExit(5000);
        }
        catch (ArgumentException)
        {
            return true;
        }
    }
}
