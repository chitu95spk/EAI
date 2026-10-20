[CmdletBinding()]
param(
    [switch]$Execute,      # without it the script only prints the plan and checks; nothing is built
    [switch]$Rebuild,      # /t:Rebuild (slow, clean). Default is incremental /t:Build, which is the fastest correct choice
    [switch]$SkipSegAI,    # leave BeeNativeSegAI.dll in the live folders untouched (use when EasyAI/another process locks it)
    [switch]$Binlog,       # also write a binlog (slower, larger); off by default to save time and disk
    [string]$MsBuildPath = 'C:\Program Files\Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\MSBuild.exe'
)
# Build the BeeMain solution target as Release|Any CPU; native dependencies retain their x64 solution mappings.
# Console stays silent; errors and warnings go to log files under artifacts\verification\release-build\<run>\.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (Test-Path variable:PSNativeCommandUseErrorActionPreference) { $PSNativeCommandUseErrorActionPreference = $false }

$root = Split-Path (Split-Path (Split-Path (Split-Path $PSScriptRoot)))
$proj = Join-Path $root 'EasyVision.sln'
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$runDir = Join-Path $root "artifacts\verification\release-build\$stamp"
$errLog = Join-Path $runDir 'errors.log'
$warnLog = Join-Path $runDir 'warnings.log'
$folders = @('bin\Release', 'bin\x64\Release') | ForEach-Object { Join-Path $root $_ }
$target = if ($Rebuild) { 'BeeMain:Rebuild' } else { 'BeeMain' }

$msbArgs = @($proj, "/t:$target", '/p:Configuration=Release', '/p:Platform=Any CPU', '/m', '/nr:false', '/nologo', '/noconlog',
    "/flp1:logfile=$errLog;errorsonly", "/flp2:logfile=$warnLog;warningsonly")
if ($SkipSegAI) { $msbArgs += '/p:EasyVisionSkipSegAIRuntimeCopy=true' }
if ($Binlog) { $msbArgs += "/bl:$(Join-Path $runDir 'build.binlog')" }

$running = @(Get-Process -Name 'EasyVision' -ErrorAction SilentlyContinue)
Write-Output "== EV RELEASE BUILD ($(if ($Execute) { 'EXECUTE' } else { 'PLAN ONLY - add -Execute to build' })) =="
Write-Output "project : $proj"
Write-Output "target  : $target  Release|Any CPU  (parallel /m, incremental unless -Rebuild)"
Write-Output "outputs : BeeMain in bin\Release; x64 dependencies follow solution mappings"
Write-Output "logs    : $runDir"
Write-Output "EasyVision.exe running: $($running.Count)   MSBuild present: $(Test-Path -LiteralPath $MsBuildPath)"
if (-not (Test-Path -LiteralPath $proj)) { Write-Output "ABORT: $proj not found"; exit 1 }
if (-not (Test-Path -LiteralPath $MsBuildPath)) { Write-Output 'ABORT: MSBuild not found; pass -MsBuildPath'; exit 1 }
if (-not $Execute) { Write-Output 'Not built. This build writes into the live folders; run with -Execute only when the user asked for it.'; exit 0 }
if ($running.Count) { Write-Output 'ABORT: EasyVision.exe is running and would lock DLLs. Close it first (this script never kills it).'; exit 2 }

# --- BeeLicense.dll guard: a rebuild must never replace the activation DLL ---------------------------------------------
New-Item -ItemType Directory -Force $runDir | Out-Null
# Keep the printed summary inside the project too (resume-critical evidence, rule 0.2b).
try { Start-Transcript -Path (Join-Path $runDir 'summary.txt') -Force | Out-Null } catch { }
$guard = @{}
foreach ($f in $folders) {
    $p = Join-Path $f 'BeeLicense.dll'
    if (Test-Path -LiteralPath $p) {
        $bk = Join-Path $runDir ('license-backup\' + ($f.Substring($root.Length + 1) -replace '[\\/]', '_'))
        New-Item -ItemType Directory -Force $bk | Out-Null
        Copy-Item -LiteralPath $p -Destination (Join-Path $bk 'BeeLicense.dll')
        $guard[$p] = @{ Hash = (Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash; Backup = (Join-Path $bk 'BeeLicense.dll') }
    }
}

$sw = [Diagnostics.Stopwatch]::StartNew()
& $MsBuildPath @msbArgs
$exit = $LASTEXITCODE
$sw.Stop()

function Read-Unique([string]$path, [string]$pattern) {
    if (-not (Test-Path -LiteralPath $path)) { return @() }
    @([IO.File]::ReadLines($path) | Where-Object { $_ -match $pattern } | ForEach-Object { $_.Trim() } | Sort-Object -Unique)
}
$errs = @(Read-Unique $errLog ': error ')
$warns = @(Read-Unique $warnLog ': warning ')
Write-Output ("exit code: {0} | elapsed: {1:n0} s | unique errors: {2} | unique warnings: {3}" -f $exit, $sw.Elapsed.TotalSeconds, $errs.Count, $warns.Count)
foreach ($e in ($errs | Select-Object -First 10)) { Write-Output ("  ERR  " + ($e.Substring(0, [Math]::Min(230, $e.Length)))) }
if ($errs.Count -gt 10) { Write-Output "  ... $($errs.Count - 10) more in $errLog" }
Write-Output "  warnings compared with docs/architecture/baseline_build.md: must not exceed the baseline."

# --- guard result -----------------------------------------------------------------------------------------------------
foreach ($p in $guard.Keys) {
    $now = if (Test-Path -LiteralPath $p) { (Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash } else { '' }
    if ($now -ne $guard[$p].Hash) {
        Copy-Item -LiteralPath $guard[$p].Backup -Destination $p -Force
        Write-Output "LICENSE GUARD: $($p.Substring($root.Length + 1)) was changed by the build; ORIGINAL RESTORED from $($guard[$p].Backup.Substring($root.Length + 1))"
    } else { Write-Output "license guard: $($p.Substring($root.Length + 1)) unchanged" }
}

# --- freshness of both launch folders and the native mirror -----------------------------------------------------------
Write-Output '-- freshness (LastWriteTime, bytes)'
foreach ($rel in 'EasyVision.exe', 'BeeInterface.dll', 'BeeGlobal.dll', 'BeeCore.dll') {
    $line = foreach ($f in $folders) { $p = Join-Path $f $rel; if (Test-Path -LiteralPath $p) { $i = Get-Item -LiteralPath $p; '{0:MM-dd HH:mm:ss}/{1}' -f $i.LastWriteTime, $i.Length } else { 'missing' } }
    Write-Output ("  {0,-18} bin\Release={1}   bin\x64\Release={2}" -f $rel, $line[0], $line[1])
}
$nativeA = Join-Path $root 'bin\Release\EtherCatCommunication.dll'
$nativeB = Join-Path $root 'lib\native\x64\Release\EtherCatCommunication.dll'
if ((Test-Path -LiteralPath $nativeA) -and (Test-Path -LiteralPath $nativeB)) {
    $a = Get-Item -LiteralPath $nativeA; $b = Get-Item -LiteralPath $nativeB
    $same = ($a.Length -eq $b.Length) -and ($a.LastWriteTime -eq $b.LastWriteTime)
    Write-Output ("  EtherCatCommunication.dll bin vs lib/native mirror identical: {0}" -f $same)
}
Write-Output ("status: " + $(if ($exit -eq 0 -and $errs.Count -eq 0) { 'BUILD OK (compile only; not a runtime or hardware check)' } else { 'BUILD FAILED' }))
try { Stop-Transcript | Out-Null } catch { }
exit $exit
