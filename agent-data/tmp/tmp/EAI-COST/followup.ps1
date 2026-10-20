$ErrorActionPreference = 'Stop'
$root = (Get-Location).Path
$utf8 = New-Object System.Text.UTF8Encoding($false, $true)
function Replace-Once([string] $name, [string] $old, [string] $new) {
    $path = Join-Path $root $name
    $raw = [System.IO.File]::ReadAllBytes($path)
    $text = $utf8.GetString($raw)
    $ending = if ($text.Contains("`r`n")) { "`r`n" } else { "`n" }
    $old = $old.Replace("`r`n", "`n").Replace("`n", $ending)
    $new = $new.Replace("`r`n", "`n").Replace("`n", $ending)
    if ([regex]::Matches($text, [regex]::Escape($old)).Count -ne 1) { throw "Anchor not unique in $name : $old" }
    $saved = Join-Path (Join-Path $root 'tmp/EAI-COST/baseline') $name
    if (!(Test-Path -LiteralPath $saved)) {
        [System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($saved)) | Out-Null
        [System.IO.File]::WriteAllBytes($saved, $raw)
    }
    if ([Convert]::ToBase64String([System.IO.File]::ReadAllBytes($path)) -ne [Convert]::ToBase64String($raw)) { throw "Concurrent edit: $name" }
    [System.IO.File]::WriteAllBytes($path, $utf8.GetBytes($text.Replace($old, $new)))
}
$file = 'EasyLabel/tests/EasyLabel.SegAI.Tests/AgentRunnerBudgetTests.cs'
$old = @'
    [Fact]
    public async Task OfflineModeStartsNoProcess()
    {
        using BudgetRig rig = new BudgetRig();
        AgentRunner runner = rig.NewRunner(null, null);
        runner.IsOffline = true;

        AgentRunResult result = await runner.RunAsync(rig.Input("label it"), CancellationToken.None);

        Assert.Equal(AgentOutcomeKind.Failed, result.Kind);
        Assert.Equal(AgentRunner.OfflineMessage, result.Message);
        Assert.Equal(0, rig.Starts);
    }
'@
$new = @'
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
'@
Replace-Once $file $old $new
$project = 'EasyLabel/tests/EasyLabel.SegAI.Tests/EasyLabel.SegAI.Tests.csproj'
$anchor = '    <Compile Include="..\..\src\EasyAI.Wpf\Services\Agent\AgentPrices.cs" Link="Linked\AgentPrices.cs" />'
Replace-Once $project $anchor ($anchor + "`n" + '    <Compile Include="..\..\src\EasyAI.Wpf\Services\Agent\AgentCliCapabilities.cs" Link="Linked\AgentCliCapabilities.cs" />')
