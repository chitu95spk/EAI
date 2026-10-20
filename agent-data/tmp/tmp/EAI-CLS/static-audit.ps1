$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
Set-Location -LiteralPath $repo
$env:TEMP = Join-Path $repo 'tmp/EAI-ONL-MODE'
$env:TMP = $env:TEMP
$env:CODEGRAPH_NO_DAEMON = '1'
$output = Join-Path $repo 'artifacts/EAI-CLS/static'
New-Item -ItemType Directory -Force $output | Out-Null
$newSources = @('EasyLabel/src/EasyLabel.SegAI/ClassTableSyncRules.cs', 'EasyLabel/src/EasyLabel.SegAI/SegAiSession.ClassSync.cs', 'EasyLabel/src/EasyAI.Wpf/ViewModels/MainViewModel.ClassSync.cs', 'EasyLabel/tests/EasyLabel.SegAI.Tests/ClassTableSyncTests.cs')
$shared = @('EasyLabel/src/EasyLabel.SegAI/SegAiSession.cs', 'EasyLabel/src/EasyAI.Wpf/ViewModels/MainViewModel.cs', 'EasyLabel/src/EasyAI.Wpf/Resources/Strings.resx', 'EasyLabel/src/EasyAI.Wpf/Resources/Strings.vi.resx', 'EasyLabel/src/EasyAI.Wpf/Resources/Strings.Designer.cs')
$sdk = 'C:/Program Files/dotnet/sdk/9.0.304/Roslyn/bincore'
$compilerArgs = @('/nologo', '/target:exe', '/langversion:7.3', '/nullable:disable', ('/out:"' + (Join-Path $output 'SyntaxAudit.dll') + '"'))
$compilerArgs += Get-ChildItem 'C:/Program Files/dotnet/packs/Microsoft.NETCore.App.Ref/9.0.8/ref/net9.0' -Filter '*.dll' | ForEach-Object { '/r:"' + $_.FullName + '"' }
$compilerArgs += @('/r:"' + $sdk + '/Microsoft.CodeAnalysis.dll"', '/r:"' + $sdk + '/Microsoft.CodeAnalysis.CSharp.dll"', '"tmp/EAI-CLS/SyntaxAudit.cs"')
$response = Join-Path $output 'syntax.rsp'
[IO.File]::WriteAllLines($response, $compilerArgs)
& dotnet (Join-Path $sdk 'csc.dll') ('@' + $response) *> (Join-Path $output 'syntax-compile.log')
if ($LASTEXITCODE -ne 0) { throw 'Syntax audit compiler failed; see syntax-compile.log' }
Copy-Item -LiteralPath (Join-Path $sdk 'Microsoft.CodeAnalysis.dll'), (Join-Path $sdk 'Microsoft.CodeAnalysis.CSharp.dll') -Destination $output
[IO.File]::WriteAllText((Join-Path $output 'SyntaxAudit.runtimeconfig.json'), '{"runtimeOptions":{"tfm":"net9.0","framework":{"name":"Microsoft.NETCore.App","version":"9.0.8"}}}')
& dotnet (Join-Path $output 'SyntaxAudit.dll') @newSources *> (Join-Path $output 'syntax.log')
if ($LASTEXITCODE -ne 0) { throw 'C# 7.3 source syntax audit failed' }
$ErrorActionPreference = 'Continue'
& git -c core.safecrlf=false diff --check -- @shared *> (Join-Path $output 'whitespace.log')
$whiteExit = $LASTEXITCODE
& git diff --no-index -- tmp/EAI-CLS/before/MainViewModel.cs EasyLabel/src/EasyAI.Wpf/ViewModels/MainViewModel.cs *> (Join-Path $output 'MainViewModel.task.diff')
& git diff --no-index -- tmp/EAI-CLS/before/SegAiSession.cs EasyLabel/src/EasyLabel.SegAI/SegAiSession.cs *> (Join-Path $output 'SegAiSession.task.diff')
$ErrorActionPreference = 'Stop'
$findings = @()
foreach ($path in $newSources) {
    $line = 0
    foreach ($value in [IO.File]::ReadAllLines((Join-Path $repo $path))) {
        $line++
        if ($value -match '\s+$' -or $value -match '^(<<<<<<<|=======|>>>>>>>)') { $findings += ($path + ':' + $line) }
    }
}
foreach ($path in $shared) {
    if (Select-String -LiteralPath $path -Pattern '^(<<<<<<<|=======|>>>>>>>)' -Quiet) { $findings += ($path + ': conflict marker') }
}
foreach ($path in @('EasyLabel/src/EasyAI.Wpf/Resources/Strings.resx', 'EasyLabel/src/EasyAI.Wpf/Resources/Strings.vi.resx')) {
    [xml]$resource = [IO.File]::ReadAllText((Join-Path $repo $path))
    $matches = @($resource.root.data | Where-Object { $_.name -eq 'ClassTableSyncAddedFormat' })
    if ($matches.Count -ne 1 -or $matches[0].value -notmatch '\{0\}') { $findings += ($path + ': resource parity') }
}
$summary = @('Scoped git diff --check exit=' + $whiteExit, 'New source whitespace/conflict/resource findings=' + $findings.Count) + $findings
$summary | Set-Content (Join-Path $output 'summary.log')
Get-Content (Join-Path $output 'syntax.log')
$summary
if ($whiteExit -ne 0 -or $findings.Count -ne 0) { exit 1 }
