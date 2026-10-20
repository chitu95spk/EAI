using System.Diagnostics;
using EasyAI.Desktop.Services;
using EasyAI.Desktop.Services.Agent;

namespace EasyLabel.SegAI.Tests;

/// <summary>
/// The streaming path of <see cref="AgentRunner"/> through its hidden fake-agent seam: a PowerShell script that streams stream-json lines with
/// delays and one 1 MB image tool result. Real Claude Code stays covered by the manual smoke run only.
/// </summary>
public sealed class AgentRunnerStreamTests
{
    private const string FakeAgent = """
        $a = @($args)
        $mcpIndex = [array]::IndexOf($a, '--mcp-config')
        $run = Split-Path -Parent $a[$mcpIndex + 1]
        $pIndex = [array]::IndexOf($a, '-p')
        $prompt = $a[$pIndex + 1]
        Set-Content -Path (Join-Path $run 'pid.txt') -Value $PID
        Set-Content -Path (Join-Path $run 'args.txt') -Value ($a -join '|')
        $o = [Console]::Out
        $o.WriteLine('{"type":"system","subtype":"init","model":"fake-model"}')
        Start-Sleep -Milliseconds 150
        $o.WriteLine('{"type":"assistant","message":{"content":[{"type":"text","text":"Looking at the image"},{"type":"tool_use","id":"t1","name":"mcp__easyai__get_crop","input":{"x":10,"y":20}}]}}')
        Start-Sleep -Milliseconds 150
        if ($prompt -eq 'hang') { Start-Sleep -Seconds 120; exit 0 }
        $big = 'QUFB' * 262144
        $o.WriteLine('{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"t1","content":[{"type":"text","text":"crop ok"},{"type":"image","source":{"type":"base64","media_type":"image/png","data":"' + $big + '"}}]}]}}')
        Start-Sleep -Milliseconds 150
        $o.WriteLine('{"type":"assistant","message":{"content":[{"type":"text","text":"Proposing one box"}]}}')
        $p = '{"version":1,"proposals":[{"id":"auto-1","label":"OK","shapeType":"rectangle","points":[[10,20],[110,220]],"holes":[],"score":0.9,"note":"clip","source":"agent"}]}'
        Set-Content -Path (Join-Path $run 'proposals.json') -Value $p
        $o.WriteLine('{"type":"result","subtype":"success","is_error":false,"result":"done","total_cost_usd":0.0456,"num_turns":4,"duration_ms":900,"usage":{"input_tokens":5,"cache_creation_input_tokens":100,"cache_read_input_tokens":200,"output_tokens":50}}')
        exit 0
        """;

    private sealed class ListProgress : IProgress<AgentStreamEvent>
    {
        private readonly object _lock = new object();
        private readonly List<AgentStreamEvent> _events = new List<AgentStreamEvent>();

        public void Report(AgentStreamEvent value)
        {
            lock (_lock)
            {
                _events.Add(value);
            }
        }

        public AgentStreamEvent[] Snapshot()
        {
            lock (_lock)
            {
                return _events.ToArray();
            }
        }
    }

    private sealed class Rig : IDisposable
    {
        private readonly AgentScratch _scratch;

        public Rig()
        {
            _scratch = new AgentScratch("stream");
            Env = new FakeAgentEnvironment();
            Env.AddFile(@"C:\app\agent\Skills\one.md", "skill one");

            string script = _scratch.Combine("fake-agent.ps1");
            File.WriteAllText(script, FakeAgent);
            ImagePath = _scratch.Combine("image.png");
            File.WriteAllBytes(ImagePath, new byte[] { 1, 2, 3, 4 });
            RunsRoot = _scratch.Combine("runs");

            // No claude and no MCP executable are registered: the fake-agent seam must not need them.
            Runner = new AgentRunner(Env, @"C:\app", RunsRoot, _scratch.Combine("cache"))
            {
                FakeAgentScript = script,
                McpServersPath = null,
                UserSkillsRoot = null,
            };
        }

        public FakeAgentEnvironment Env { get; }

        public AgentRunner Runner { get; }

        public string ImagePath { get; }

        public string RunsRoot { get; }

        public AgentRunInput Input(string prompt)
        {
            return new AgentRunInput(ImagePath, "image.png", 100, 100, new[] { "NG", "OK" }, Array.Empty<AgentShapeInfo>(), prompt, null, 1, true);
        }

        public string RunFolder()
        {
            return Directory.GetDirectories(RunsRoot).Single(d => AgentRunRetention.IsRunFolderName(Path.GetFileName(d)));
        }

        public void Dispose()
        {
            _scratch.Dispose();
        }
    }

    [Fact]
    public async Task StreamedRunReportsLiveEventsWithoutImageDataAndParsesTheFinalResult()
    {
        using Rig rig = new Rig();
        ListProgress progress = new ListProgress();

        AgentRunResult result = await rig.Runner.RunAsync(rig.Input("label it"), CancellationToken.None, progress);

        Assert.Equal(AgentOutcomeKind.Success, result.Kind);
        Assert.Equal(0.0456, result.CostUsd, 6);
        Assert.Equal(4, result.Turns);
        Assert.Equal("done", result.ResultText);
        Assert.Single(result.Proposals);

        AgentStreamEvent[] events = progress.Snapshot();
        Assert.Equal(
            new[]
            {
                AgentStreamEventKind.Text,
                AgentStreamEventKind.Text,
                AgentStreamEventKind.ToolCall,
                AgentStreamEventKind.ToolResult,
                AgentStreamEventKind.Text,
                AgentStreamEventKind.Result,
            },
            events.Select(e => e.Kind).ToArray());
        Assert.Equal("get_crop", events[2].ToolName);
        Assert.Equal("get_crop", events[3].ToolName);
        Assert.Equal(4, events[5].Turns);

        // Memory stays flat: no event carries image data, and every text is short (the 1 MB line is cut, never held or reported).
        Assert.DoesNotContain(events, e => e.Text.Contains("QUFB"));
        Assert.True(events.Max(e => e.Text.Length) <= AgentStreamParser.MaxTextLength);
        Assert.True(result.OutputLength < 20000, "the kept output must not hold the image line: " + result.OutputLength);

        string folder = rig.RunFolder();
        string timing = File.ReadAllText(Path.Combine(folder, AgentStreamTiming.FileName));
        Assert.Contains("kind,turn,tool,ms", timing);
        Assert.Contains("tool,1,get_crop,", timing);
        Assert.DoesNotContain("label it", timing);

        string args = File.ReadAllText(Path.Combine(folder, "args.txt"));
        Assert.Contains("--output-format|stream-json|--verbose", args);

        // log.csv keeps the prompt length and cost only.
        string log = File.ReadAllText(Path.Combine(rig.RunsRoot, "log.csv"));
        Assert.DoesNotContain("label it", log);
    }

    [Fact]
    public async Task RunAsyncWithoutProgressStillWorks()
    {
        using Rig rig = new Rig();

        AgentRunResult result = await rig.Runner.RunAsync(rig.Input("label it"), CancellationToken.None);

        Assert.Equal(AgentOutcomeKind.Success, result.Kind);
        Assert.Equal(4, result.Turns);

        // Method-group compatibility with callers that take Func<AgentRunInput, CancellationToken, Task<AgentRunResult>>.
        Func<AgentRunInput, CancellationToken, Task<AgentRunResult>> group = rig.Runner.RunAsync;
        Assert.NotNull(group);
    }

    [Fact]
    public async Task CancelMidStreamEndsAsCancelledAndKillsTheProcess()
    {
        using Rig rig = new Rig();
        ListProgress progress = new ListProgress();
        using CancellationTokenSource cts = new CancellationTokenSource();

        Task<AgentRunResult> run = rig.Runner.RunAsync(rig.Input("hang"), cts.Token, progress);
        DateTime limit = DateTime.UtcNow.AddSeconds(30);
        while (!progress.Snapshot().Any(e => e.Kind == AgentStreamEventKind.ToolCall))
        {
            Assert.True(DateTime.UtcNow < limit, "the fake agent never streamed its tool call");
            await Task.Delay(50);
        }

        int pid = int.Parse(File.ReadAllText(Path.Combine(rig.RunFolder(), "pid.txt")).Trim());
        Stopwatch watch = Stopwatch.StartNew();
        cts.Cancel();
        AgentRunResult result = await run;
        watch.Stop();

        Assert.Equal(AgentOutcomeKind.Cancelled, result.Kind);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(15), "cancel took " + watch.Elapsed);
        Assert.True(IsGone(pid), "the agent process is still running");
        Assert.True(File.Exists(Path.Combine(rig.RunFolder(), AgentStreamTiming.FileName)));
    }

    [Fact]
    public void FakeAgentFlagIsParsedFromTheStartupArguments()
    {
        Assert.Equal(@"C:	mpake agent.ps1", EasyAI.Desktop.Services.AgentRunner.FakeAgentFromArgs(new[] { "x.exe", "--fake-agent", @"C:	mpake agent.ps1", "--fake-camera" }));
        Assert.Null(EasyAI.Desktop.Services.AgentRunner.FakeAgentFromArgs(System.Array.Empty<string>()));
        Assert.Null(EasyAI.Desktop.Services.AgentRunner.FakeAgentFromArgs(new[] { "--fake-agent" }));
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
