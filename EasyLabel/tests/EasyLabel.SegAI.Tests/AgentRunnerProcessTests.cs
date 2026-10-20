using System.Diagnostics;
using EasyAI.Desktop.Services;
using EasyAI.Desktop.Services.Agent;

namespace EasyLabel.SegAI.Tests;

/// <summary>
/// Process lifecycle of <see cref="AgentRunner"/> against a fake "claude": a PowerShell script started through the injectable start-info
/// factory. The real Claude Code is only covered by the manual smoke run.
/// </summary>
public sealed class AgentRunnerProcessTests
{
    private const string FakeScript = """
        $a = @($args)
        $mcpIndex = [array]::IndexOf($a, '--mcp-config')
        $run = Split-Path -Parent $a[$mcpIndex + 1]
        $pIndex = [array]::IndexOf($a, '-p')
        $prompt = $a[$pIndex + 1]
        Set-Content -Path (Join-Path $run 'pid.txt') -Value $PID
        Set-Content -Path (Join-Path $run 'temp.txt') -Value ($env:TEMP + '|' + $env:TMP + '|' + (Get-Location).Path)
        if ($prompt -eq 'sleep') { Start-Sleep -Seconds 120; exit 0 }
        if ($prompt -eq 'login') {
          [Console]::Out.Write('{"type":"result","is_error":true,"result":"Not logged in - Please run /login","total_cost_usd":0,"num_turns":1}')
          exit 1
        }
        if ($prompt -eq 'garbage') { [Console]::Out.Write('hello world'); exit 3 }
        $p = '{"version":1,"proposals":[{"id":"auto-1","label":"OK","shapeType":"rectangle","points":[[10,20],[110,220]],"holes":[],"score":0.9,"note":"clip","source":"agent"}]}'
        Set-Content -Path (Join-Path $run 'proposals.json') -Value $p
        [Console]::Out.Write('{"type":"result","subtype":"success","is_error":false,"result":"done","total_cost_usd":0.0123,"num_turns":3,"duration_ms":1500,"usage":{"input_tokens":5,"cache_creation_input_tokens":100,"cache_read_input_tokens":200,"output_tokens":50}}')
        exit 0
        """;

    private sealed class Rig : IDisposable
    {
        private readonly AgentScratch _scratch;
        private int _starts;

        public Rig(TimeSpan? timeout = null)
        {
            _scratch = new AgentScratch("runner");
            Env = new FakeAgentEnvironment();
            Env.Variables[ClaudeLocator.EnvironmentName] = @"C:\fake\claude.exe";
            Env.AddFile(@"C:\fake\claude.exe");
            Env.AddFile(@"C:\app\agent\EasyLabel.Ai.Mcp.exe");
            Env.AddFile(@"C:\app\agent\Skills\detect.md", "skill one");
            Env.AddFile(@"C:\app\agent\Skills\find-objects.md", "skill two");

            string script = _scratch.Combine("fake-claude.ps1");
            File.WriteAllText(script, FakeScript);
            ImagePath = _scratch.Combine("image.png");
            File.WriteAllBytes(ImagePath, new byte[] { 1, 2, 3, 4 });
            RunsRoot = _scratch.Combine("runs");
            CacheRoot = _scratch.Combine("cache");

            Runner = new AgentRunner(
                Env,
                @"C:\app",
                RunsRoot,
                CacheRoot,
                (claude, args, workingDirectory, tempPath) =>
                {
                    Interlocked.Increment(ref _starts);
                    List<string> all = new List<string> { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script };
                    all.AddRange(args);
                    return AgentRunner.DefaultStartInfo("powershell.exe", all, workingDirectory, tempPath);
                },
                timeout);
        }

        public FakeAgentEnvironment Env { get; }

        public AgentRunner Runner { get; }

        public string ImagePath { get; }

        public string RunsRoot { get; }

        public string CacheRoot { get; }

        public int Starts => _starts;

        public AgentRunInput Input(string prompt, bool bypass = false)
        {
            return new AgentRunInput(ImagePath, "image.png", 100, 100, new[] { "NG", "OK" }, Array.Empty<AgentShapeInfo>(), prompt, null, 1, bypass);
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

    [Fact]
    public async Task SuccessReadsProposalsLogsTheRunAndCachesTheAnswer()
    {
        using Rig rig = new Rig();

        AgentRunResult result = await rig.Runner.RunAsync(rig.Input("label it"), CancellationToken.None);

        Assert.Equal(AgentOutcomeKind.Success, result.Kind);
        Assert.False(result.FromCache);
        Assert.Single(result.Proposals);
        Assert.Equal("rectangle", result.Proposals[0].ShapeType);
        Assert.Equal(0.0123, result.CostUsd, 6);
        Assert.Equal(3, result.Turns);
        Assert.Equal(200, result.Usage.CacheReadTokens);

        string folder = Assert.Single(rig.RunFolders());
        string[] temp = File.ReadAllText(Path.Combine(folder, "temp.txt")).Trim().Split('|');
        Assert.Equal(Path.Combine(folder, "t"), temp[0], ignoreCase: true);
        Assert.Equal(Path.Combine(folder, "t"), temp[1], ignoreCase: true);
        Assert.Equal(folder, temp[2], ignoreCase: true);
        Assert.True(File.Exists(Path.Combine(folder, "request.json")));
        Assert.Contains("skill one", File.ReadAllText(Path.Combine(folder, "system.md")));

        string[] log = File.ReadAllLines(Path.Combine(rig.RunsRoot, "log.csv"));
        Assert.Equal(2, log.Length);
        Assert.Contains(",image.png,sonnet,3,0.0123,5,100,200,50,", log[1]);
        Assert.EndsWith(",1,Success,8,0", log[1]);

        // The same request again is answered from the cache: no process, no cost, a "cached" log line.
        AgentRunResult again = await rig.Runner.RunAsync(rig.Input("label it"), CancellationToken.None);
        Assert.True(again.FromCache);
        Assert.Single(again.Proposals);
        Assert.Equal(1, rig.Starts);
        Assert.EndsWith(",1,cached,8,0", File.ReadAllLines(Path.Combine(rig.RunsRoot, "log.csv"))[2]);

        // Bypassing the cache (Shift+Run) or changing the prompt really runs the agent again.
        Assert.False((await rig.Runner.RunAsync(rig.Input("label it", true), CancellationToken.None)).FromCache);
        Assert.False((await rig.Runner.RunAsync(rig.Input("label it please"), CancellationToken.None)).FromCache);
        Assert.Equal(3, rig.Starts);
    }

    [Fact]
    public async Task SignedOutIsReportedAsNotSignedIn()
    {
        using Rig rig = new Rig();

        AgentRunResult result = await rig.Runner.RunAsync(rig.Input("login"), CancellationToken.None);

        Assert.Equal(AgentOutcomeKind.NotSignedIn, result.Kind);
        Assert.Empty(result.Proposals);
    }

    [Fact]
    public async Task GarbageOutputIsFailedWithTheRawText()
    {
        using Rig rig = new Rig();

        AgentRunResult result = await rig.Runner.RunAsync(rig.Input("garbage"), CancellationToken.None);

        Assert.Equal(AgentOutcomeKind.Failed, result.Kind);
        Assert.Equal("hello world", result.Message);
    }

    [Fact]
    public async Task MissingClaudeIsNotInstalledAndMissingToolsIsFailed()
    {
        using Rig rig = new Rig();
        rig.Env.Files.Remove(@"C:\fake\claude.exe");
        AgentRunResult none = await rig.Runner.RunAsync(rig.Input("label it"), CancellationToken.None);
        Assert.Equal(AgentOutcomeKind.NotInstalled, none.Kind);
        Assert.Equal(0, rig.Starts);

        rig.Env.AddFile(@"C:\fake\claude.exe");
        rig.Env.Files.Remove(@"C:\app\agent\EasyLabel.Ai.Mcp.exe");
        AgentRunResult tools = await rig.Runner.RunAsync(rig.Input("label it"), CancellationToken.None);
        Assert.Equal(AgentOutcomeKind.Failed, tools.Kind);
        Assert.Contains("EasyLabel.Ai.Mcp.exe", tools.Message);
        Assert.Equal(0, rig.Starts);
    }

    [Fact]
    public async Task MissingImageIsFailedBeforeAnyProcessStarts()
    {
        using Rig rig = new Rig();
        File.Delete(rig.ImagePath);

        AgentRunResult result = await rig.Runner.RunAsync(rig.Input("label it"), CancellationToken.None);

        Assert.Equal(AgentOutcomeKind.Failed, result.Kind);
        Assert.Equal(0, rig.Starts);
    }

    [Fact]
    public async Task CancelKillsTheProcessAndReturnsCancelled()
    {
        using Rig rig = new Rig();
        using CancellationTokenSource cts = new CancellationTokenSource();

        Task<AgentRunResult> run = rig.Runner.RunAsync(rig.Input("sleep"), cts.Token);
        int pid = await WaitForPidAsync(rig);
        Stopwatch watch = Stopwatch.StartNew();
        cts.Cancel();
        AgentRunResult result = await run;
        watch.Stop();

        Assert.Equal(AgentOutcomeKind.Cancelled, result.Kind);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(15), "cancel took " + watch.Elapsed);
        Assert.True(IsGone(pid), "the agent process is still running");
    }

    [Fact]
    public async Task TimeoutKillsTheProcessAndReturnsTimedOut()
    {
        using Rig rig = new Rig(TimeSpan.FromSeconds(10));

        AgentRunResult result = await rig.Runner.RunAsync(rig.Input("sleep"), CancellationToken.None);

        Assert.Equal(AgentOutcomeKind.TimedOut, result.Kind);
        int pid = int.Parse(File.ReadAllText(Path.Combine(Assert.Single(rig.RunFolders()), "pid.txt")).Trim());
        Assert.True(IsGone(pid), "the agent process is still running");
    }

    [Fact]
    public async Task AlreadyCancelledTokenStartsNothing()
    {
        using Rig rig = new Rig();
        using CancellationTokenSource cts = new CancellationTokenSource();
        cts.Cancel();

        AgentRunResult result = await rig.Runner.RunAsync(rig.Input("label it"), cts.Token);

        Assert.Equal(AgentOutcomeKind.Cancelled, result.Kind);
        Assert.Equal(0, rig.Starts);
    }

    private static async Task<int> WaitForPidAsync(Rig rig)
    {
        DateTime limit = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < limit)
        {
            foreach (string folder in rig.RunFolders())
            {
                string file = Path.Combine(folder, "pid.txt");
                try
                {
                    if (File.Exists(file) && int.TryParse(File.ReadAllText(file).Trim(), out int pid))
                    {
                        return pid;
                    }
                }
                catch (IOException)
                {
                }
            }

            await Task.Delay(100);
        }

        throw new TimeoutException("The fake agent never wrote its pid.");
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
