[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('preflight', 'train', 'check')]
    [string]$Mode,
    [Parameter(Mandatory = $true)]
    [string]$Batch,
    [string]$Project,
    [string]$Model,
    [string]$Accepted,
    [string]$Test,
    [ValidateRange(1, 1000)]
    [int]$Repetitions = 2,
    [switch]$RequestGpu,
    [switch]$BuildHarness
)

$ErrorActionPreference = 'Stop'
$root = 'E:\Code\EasyVision_Unisen - NoService'
$taskRoot = Join-Path $root 'artifacts\EAI-LOGO'
$harness = Join-Path $root 'tmp\EAI-LOGO\harness\EaiLogoHarness.csproj'
$out = Join-Path $taskRoot $Batch
$buildRoot = Join-Path $taskRoot 'harness-build'
$binRoot = ((Join-Path $buildRoot 'bin').Replace('\', '/')) + '/'
$objRoot = ((Join-Path $buildRoot 'obj').Replace('\', '/')) + '/'

if ([string]::IsNullOrWhiteSpace($Batch) -or $Batch -match '[\\/:*?"<>|]') { throw 'Batch must be a non-empty folder name.' }
if (Test-Path -LiteralPath $out) { throw "Refusing to reuse an existing output folder: $out" }
New-Item -ItemType Directory -Path $out | Out-Null
$env:TEMP = Join-Path $out 'temp'
$env:TMP = $env:TEMP
New-Item -ItemType Directory -Path $env:TEMP | Out-Null

if ($BuildHarness) {
    $buildLog = Join-Path $out 'harness-build.log'
    $binlog = Join-Path $out 'harness-build.binlog'
    & dotnet build $harness -c Release "-p:BaseOutputPath=$binRoot" "-p:BaseIntermediateOutputPath=$objRoot" "/bl:$binlog" *> $buildLog
    if ($LASTEXITCODE -ne 0) { throw "Harness build failed. See $buildLog and $binlog" }
}

$dll = Join-Path $binRoot 'Release\net9.0-windows\EasyLabel.SegAI.Tests.dll'
if (-not (Test-Path -LiteralPath $dll -PathType Leaf)) {
    throw "Harness DLL is absent: $dll. Re-run with -BuildHarness only when harness build is authorized."
}

$harnessArguments = @($dll, $Mode, '--out', $out)
if ($Mode -ne 'preflight') {
    if ([string]::IsNullOrWhiteSpace($Project)) { throw '-Project is required for train/check.' }
    $harnessArguments += @('--project', $Project)
}
if ($Mode -eq 'train' -and -not [string]::IsNullOrWhiteSpace($Accepted)) { $harnessArguments += @('--accepted', $Accepted) }
if ($Mode -eq 'check') {
    if ([string]::IsNullOrWhiteSpace($Model)) { throw '-Model is required for check.' }
    $harnessArguments += @('--model', $Model, '--repetitions', $Repetitions.ToString([System.Globalization.CultureInfo]::InvariantCulture))
    if ($RequestGpu) { $harnessArguments += @('--gpu', 'true') }
    if (-not [string]::IsNullOrWhiteSpace($Test)) { $harnessArguments += @('--test', $Test) }
}

$runLog = Join-Path $out 'harness-run.log'
$priorErrorActionPreference = $ErrorActionPreference
$childExitCode = $null
try {
    $ErrorActionPreference = 'Continue'
    & dotnet @harnessArguments *> $runLog
    $childExitCode = $LASTEXITCODE
}
finally {
    $ErrorActionPreference = $priorErrorActionPreference
}
if ($childExitCode -ne 0) { throw "Harness $Mode failed. See $runLog" }
Write-Host "Harness $Mode completed: $out"
