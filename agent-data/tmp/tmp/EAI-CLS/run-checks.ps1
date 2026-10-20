param([ValidateSet('new','regression','wpf')][string]$Check, [string]$Run = 'run2')
$ErrorActionPreference = 'Continue'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
Set-Location -LiteralPath $repo
$env:TEMP = Join-Path $repo 'tmp/EAI-ONL-MODE'
$env:TMP = $env:TEMP
$env:APPDATA = Join-Path $env:TEMP 'cc17-appdata'
$env:LOCALAPPDATA = Join-Path $env:TEMP 'cc17-localappdata'
$env:MSBUILDSDKREFERENCEDIRECTORY = Join-Path $env:TEMP 'cc17-sdk-roots'
$env:NUGET_PACKAGES = Join-Path $env:TEMP 'nuget'
$env:NUGET_HTTP_CACHE_PATH = Join-Path $env:TEMP 'cc17-http-cache'
$env:NUGET_PLUGINS_CACHE_PATH = Join-Path $env:TEMP 'cc17-plugin-cache'
$env:EAI_CLS_TEST_ROOT = Join-Path $repo 'tmp/EAI-CLS/synthetic'
$output = Join-Path $repo ('artifacts/EAI-CLS/' + $Run)
New-Item -ItemType Directory -Force -Path $output | Out-Null
$common = @('-c', 'Release', ('-p:RestoreConfigFile=' + (Join-Path $repo 'tmp/EAI-ONL-MODE/codex/cc17-nuget.config')), '-p:NuGetAudit=false', ('-bl:' + (Join-Path $output ($Check + '.binlog'))))
if ($Check -eq 'wpf') {
    & dotnet build EasyLabel/src/EasyAI.Wpf/EasyAI.Wpf.csproj @common *> (Join-Path $output ($Check + '.log'))
} else {
    $filter = if ($Check -eq 'new') { 'FullyQualifiedName~ClassTableSyncTests' } else { 'FullyQualifiedName~ClassTable|FullyQualifiedName~PanelRules|FullyQualifiedName~ShapeList|FullyQualifiedName~ClassRenameTests' }
    & dotnet test EasyLabel/tests/EasyLabel.SegAI.Tests/EasyLabel.SegAI.Tests.csproj @common --filter $filter --logger ('trx;LogFileName=' + $Check + '.trx') --results-directory $output *> (Join-Path $output ($Check + '.log'))
}
$result = $LASTEXITCODE
Write-Output ('check=' + $Check + '; exit=' + $result)
Select-String -Path (Join-Path $output ($Check + '.log')) -Pattern ': error |Failed |Passed!|Failed!|Total tests:|Error Message:|Expected:|Actual:|Warning\(s\)|Error\(s\)' | Select-Object -First 22
exit $result
