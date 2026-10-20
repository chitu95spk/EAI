param(
    [Parameter(Mandatory = $true)][string]$SessionId,
    [string[]]$AlsoFile = @(),   # code files edited by Bash/Python/subagents (not visible in the transcript)
    [switch]$Execute,            # build the affected projects AND run the applicable tests, then print a verdict
    [switch]$BuildOnly,          # with -Execute: skip the test stage
    [string]$TestScope = '',     # Persistence harness scope named by the contract: U562|V147|V148|V150|V161|R286
    [int]$HarnessTimeoutSec = 120,
    [int]$MaxHarness = 4,
    [string]$MsBuildPath = 'C:\Program Files\Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\MSBuild.exe'
)
# Session-scoped verification: map this session's edited code files to projects, build back then front in isolation,
# run the matching test harnesses, and conclude PASS / FAIL / INCONCLUSIVE / UNVERIFIED. Never touches bin\Release.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (Test-Path variable:PSNativeCommandUseErrorActionPreference) { $PSNativeCommandUseErrorActionPreference = $false }

$root = Split-Path (Split-Path (Split-Path (Split-Path $PSScriptRoot)))
$AlsoFile = @($AlsoFile | ForEach-Object { $_ -split ',' } | Where-Object { $_ } | ForEach-Object { $_.Trim().Replace('/', '\') })
$tr = Join-Path (Join-Path (Join-Path $env:USERPROFILE '.claude\projects') ($root -replace '[:\\/ _.]', '-')) "$SessionId.jsonl"
if (-not (Test-Path -LiteralPath $tr)) { Write-Output "Transcript not found: $tr"; exit 1 }

# ---- files this session edited (Edit/Write tool calls) ---------------------------------------------------------------
$edited = @()
foreach ($hit in (Select-String -LiteralPath $tr -Pattern '"name":"(?:Edit|Write)"')) {
    try { $rec = $hit.Line | ConvertFrom-Json } catch { continue }
    if ($rec.PSObject.Properties['message'] -and $rec.message.content -is [array]) {
        foreach ($blk in $rec.message.content) {
            if ($blk.type -eq 'tool_use' -and ($blk.name -eq 'Edit' -or $blk.name -eq 'Write') -and $blk.input.file_path) { $edited += [string]$blk.input.file_path }
        }
    }
}
$edited = @($edited) + $AlsoFile | Sort-Object -Unique

$codeExt = '\.(cs|cpp|c|h|hpp|cu|vcxproj|csproj|resx|props|targets|config)$'
$skipDir = '^(\.claude|\.agents|\.codex|docs|tmp|artifacts|bin|obj)(\\|$)'
$rel = New-Object System.Collections.Generic.List[string]
foreach ($f in $edited) {
    $full = $f; if (-not [IO.Path]::IsPathRooted($full)) { $full = Join-Path $root $full }
    if (-not $full.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) { continue }
    $r = $full.Substring($root.Length).TrimStart('\')
    if ($r -match $skipDir -or $r -notmatch $codeExt -or $r -match '\\(bin|obj)\\') { continue }
    $rel.Add($r)
}

Write-Output "== EV TEST (session $SessionId; $(if ($Execute) { if ($BuildOnly) { 'EXECUTE build only' } else { 'EXECUTE build + tests + verdict' } } else { 'PLAN ONLY' })) =="
Write-Output "session edits: $($edited.Count) files, of which product/test code: $($rel.Count)"
if (-not $rel.Count) { Write-Output 'Nothing to build or test: this session changed no code files (docs, skills, rules and logs need no build).'; Write-Output 'verdict: NOT APPLICABLE (no code change)'; exit 0 }

# ---- map file -> owning project, classify front/back/test -------------------------------------------------------------
$front = 'BeeInterface', 'BeeInterfaceControls', 'BeeUi', 'BeeMain', 'BeeHmi', 'BeeLadderEditor', 'ECAT_HMI', 'EasyLabel', 'EasyUpdate', 'BeeUpdate'
function Find-Project([string]$relPath) {
    $d = Split-Path (Join-Path $root $relPath) -Parent
    while ($d -and $d.Length -ge $root.Length) {
        $hit = @(Get-ChildItem -LiteralPath $d -File -ErrorAction SilentlyContinue | Where-Object { $_.Extension -in '.csproj', '.vcxproj' })
        if ($hit.Count) { return $hit[0].FullName }
        if ($d -eq $root) { break }
        $d = Split-Path $d -Parent
    }
    $null
}
$byProj = @{}
$unmapped = New-Object System.Collections.Generic.List[string]
foreach ($r in $rel) {
    $p = if ($r -match '\.(csproj|vcxproj)$') { Join-Path $root $r } else { Find-Project $r }
    if (-not $p) { $unmapped.Add($r); continue }
    if (-not $byProj.ContainsKey($p)) { $byProj[$p] = New-Object System.Collections.Generic.List[string] }
    $byProj[$p].Add($r)
}
function Side([string]$projPath) {
    $n = [IO.Path]::GetFileNameWithoutExtension($projPath)
    $rp = $projPath.Substring($root.Length).TrimStart('\')
    if ($rp -match '^(tests|tools)\\') { return 'test' }
    if ($front -contains $n) { return 'front' }
    'back'
}
$items = @($byProj.Keys | ForEach-Object { [pscustomobject]@{ Path = $_; Name = [IO.Path]::GetFileNameWithoutExtension($_); Side = (Side $_); Files = $byProj[$_].Count; Native = ($_ -like '*.vcxproj') } })
$order = @{ back = 0; test = 1; front = 2 }
$items = @($items | Sort-Object { $order[$_.Side] }, Name)
Write-Output '-- affected projects (build order: back, test harness, front)'
foreach ($i in $items) { Write-Output ("  {0,-5} {1,-26} {2,3} file(s){3}" -f $i.Side, $i.Name, $i.Files, $(if ($i.Native) { '  [native C++]' } else { '' })) }
if ($unmapped.Count) { Write-Output "-- unmapped (no owning project): $($unmapped -join ', ')" }

# ---- candidate harnesses: tests/tools projects referencing an affected project, minus hardware/activation tools -------------
$names = @($items.Name)
$skipHarness = 'Plc|Ecat|Ds5|Camera|Pylon|Activation|Active|Publish|License|Persistence'   # hardware, licensing, or has its own scoped runner
$harness = New-Object System.Collections.Generic.List[object]
$skippedH = New-Object System.Collections.Generic.List[string]
foreach ($d in 'tests', 'tools') {
    foreach ($cp in (Get-ChildItem (Join-Path $root $d) -Recurse -Filter '*.csproj' -ErrorAction SilentlyContinue | Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' })) {
        $txt = [IO.File]::ReadAllText($cp.FullName); $ref = $null
        foreach ($m in [regex]::Matches($txt, 'ProjectReference\s+Include="[^"]*?([^\\"]+)\.(?:cs|vcx)proj"')) { if ($names -contains $m.Groups[1].Value) { $ref = $m.Groups[1].Value; break } }
        if (-not $ref) { continue }
        $hn = [IO.Path]::GetFileNameWithoutExtension($cp.Name)
        if ($hn -match $skipHarness) { $skippedH.Add("$hn (hardware/licensing/own runner)"); continue }
        if ($names -contains $hn) { continue }
        $harness.Add([pscustomobject]@{ Name = $hn; Path = $cp.FullName; Ref = $ref })
    }
}
$toRun = @($harness | Select-Object -First $MaxHarness)
Write-Output "-- harnesses to run ($($toRun.Count) of $($harness.Count)): $((($toRun | ForEach-Object { $_.Name + '<-' + $_.Ref }) -join ', '))"
if ($harness.Count -gt $MaxHarness) { Write-Output "   not run (over -MaxHarness $MaxHarness): $((($harness | Select-Object -Skip $MaxHarness | ForEach-Object { $_.Name }) -join ', '))" }
if ($skippedH.Count) { Write-Output "   excluded: $($skippedH -join '; ')" }
if ($TestScope) { Write-Output "-- Persistence harness scope $TestScope will be built and run via invoke-check.ps1 (close EasyVision first)" }

$runDir = Join-Path $root ('artifacts\verification\etest\' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
if (-not $Execute) {
    Write-Output "Plan only. Run with -Execute to build, run the harnesses and get a verdict (logs: $($runDir.Substring($root.Length + 1)))."
    exit 0
}
if (-not (Test-Path -LiteralPath $MsBuildPath)) { Write-Output 'ABORT: MSBuild not found; pass -MsBuildPath'; exit 1 }
$appRunning = @(Get-Process -Name 'EasyVision' -ErrorAction SilentlyContinue).Count -gt 0
if ($appRunning) { Write-Output 'NOTE: EasyVision.exe is running; isolated builds do not touch it, the Persistence harness is skipped.' }
New-Item -ItemType Directory -Force $runDir | Out-Null
try { Start-Transcript -Path (Join-Path $runDir 'summary.txt') -Force | Out-Null } catch { }

function Invoke-IsolatedBuild($projPath, $name) {
    $err = Join-Path $runDir "$name.errors.log"; $warn = Join-Path $runDir "$name.warnings.log"
    $a = @($projPath, '/t:Build', '/p:Configuration=Release', '/p:Platform=x64', '/p:EasyVisionIsolatedVerification=true',
        '/p:EasyVisionDisableRuntimeMirror=true', '/p:EasyVisionDisableNativeDeployment=true', '/p:EasyVisionDisableVersionBump=true',
        '/m', '/nr:false', '/nologo', '/noconlog', "/flp1:logfile=$err;errorsonly", "/flp2:logfile=$warn;warningsonly")
    $sw = [Diagnostics.Stopwatch]::StartNew()
    & $MsBuildPath @a | Out-Null
    $code = $LASTEXITCODE; $sw.Stop()
    $errs = @(if (Test-Path -LiteralPath $err) { @([IO.File]::ReadLines($err) | Where-Object { $_ -match ': error ' } | ForEach-Object { $_.Trim() } | Sort-Object -Unique) } else { @() })
    $ws = if (Test-Path -LiteralPath $warn) { @([IO.File]::ReadLines($warn) | Where-Object { $_ -match ': warning ' } | Sort-Object -Unique).Count } else { 0 }
    [pscustomobject]@{ Exit = $code; Errors = $errs; Warnings = $ws; Secs = $sw.Elapsed.TotalSeconds }
}

# ---- stage 1: build affected projects ------------------------------------------------------------------------------------
$buildFail = 0
Write-Output '-- BUILD (isolated, compile only)'
foreach ($i in ($items | Where-Object { $_.Side -ne 'test' })) {
    $b = Invoke-IsolatedBuild $i.Path $i.Name
    Write-Output ("  {0,-5} {1,-26} exit={2} errors={3} warnings={4} {5:n0}s" -f $i.Side, $i.Name, $b.Exit, $b.Errors.Count, $b.Warnings, $b.Secs)
    foreach ($e in ($b.Errors | Select-Object -First 5)) { Write-Output ("        ERR " + $e.Substring(0, [Math]::Min(220, $e.Length))) }
    if ($b.Exit -ne 0 -or $b.Errors.Count) { $buildFail++ }
}
if ($buildFail) {
    Write-Output "verdict: FAIL - build failed in $buildFail project(s); tests not run. Logs: $($runDir.Substring($root.Length + 1))"
    try { Stop-Transcript | Out-Null } catch { }
    exit 1
}
if ($BuildOnly) {
    Write-Output 'verdict: BUILD OK (isolated, compile only; tests skipped by -BuildOnly, so runtime behavior is UNVERIFIED)'
    try { Stop-Transcript | Out-Null } catch { }
    exit 0
}

# ---- stage 2: run harnesses ----------------------------------------------------------------------------------------------
$results = New-Object System.Collections.Generic.List[object]
Write-Output "-- TEST (each harness is built isolated, run with a $HarnessTimeoutSec s limit, output saved)"
foreach ($h in $toRun) {
    $b = Invoke-IsolatedBuild $h.Path $h.Name
    if ($b.Exit -ne 0 -or $b.Errors.Count) { $results.Add([pscustomobject]@{ Name = $h.Name; State = 'FAIL'; Note = "harness build failed ($($b.Errors.Count) errors)" }); Write-Output "  $($h.Name): FAIL (harness build)"; continue }
    $outDir = Join-Path $root "artifacts\verification\Release\x64\bin\$($h.Name)"
    $exe = @(Get-ChildItem -LiteralPath $outDir -Filter '*.exe' -ErrorAction SilentlyContinue | Where-Object { $_.Name -notmatch 'vshost|Update' } | Select-Object -First 1)
    if (-not $exe.Count) { $results.Add([pscustomobject]@{ Name = $h.Name; State = 'UNVERIFIED'; Note = 'no exe produced' }); Write-Output "  $($h.Name): UNVERIFIED (no exe in $outDir)"; continue }
    # Isolated builds do not mirror native DLLs (BeeCpp.dll, OpenVINO, ...). Link the ones that are missing from the live
    # app folders into the harness output folder (read-only use of bin\; managed DLLs built in isolation are never replaced).
    $mirrored = 0; $mirrorFrom = ''
    foreach ($srcRel in 'bin\x64\Release', 'bin\Release', 'lib\native\x64\Release') {
        $src = Join-Path $root $srcRel
        if (-not (Test-Path -LiteralPath $src)) { continue }
        foreach ($dll in (Get-ChildItem -LiteralPath $src -Filter '*.dll' -File -ErrorAction SilentlyContinue)) {
            $dst = Join-Path $outDir $dll.Name
            if (Test-Path -LiteralPath $dst) { continue }
            try { New-Item -ItemType HardLink -Path $dst -Target $dll.FullName -ErrorAction Stop | Out-Null } catch { Copy-Item -LiteralPath $dll.FullName -Destination $dst }
            $mirrored++; if (-not $mirrorFrom) { $mirrorFrom = $srcRel }
        }
    }
    if ($mirrored) { Write-Output "  $($h.Name): linked $mirrored missing native/runtime DLL(s) from live folder $mirrorFrom (timestamps may differ from this session's build)" }
    $so = Join-Path $runDir "$($h.Name).stdout.txt"; $se = Join-Path $runDir "$($h.Name).stderr.txt"
    $pr = Start-Process -FilePath $exe[0].FullName -WorkingDirectory $outDir -RedirectStandardOutput $so -RedirectStandardError $se -PassThru -WindowStyle Hidden
    $null = $pr.Handle   # keeps the handle so ExitCode is available after exit (otherwise it can be $null)
    if (-not $pr.WaitForExit($HarnessTimeoutSec * 1000)) {
        try { $pr.Kill() } catch { }
        $results.Add([pscustomobject]@{ Name = $h.Name; State = 'INCONCLUSIVE'; Note = "timed out after $HarnessTimeoutSec s (killed this harness only; it may be waiting for UI or input)" })
        Write-Output "  $($h.Name): INCONCLUSIVE (timeout)"; continue
    }
    $text = ''; foreach ($f in $so, $se) { if (Test-Path -LiteralPath $f) { $text += [IO.File]::ReadAllText($f) + "`n" } }
    $pass = ([regex]::Matches($text, '(?m)\bPASS\b')).Count
    $failN = ([regex]::Matches($text, '(?m)\b(FAIL|FAILED)\b|Unhandled exception')).Count
    $code = $pr.ExitCode
    # A harness that never started (missing dependency, wrong bitness) says nothing about the code under test.
    $envRx = 'Could not load file or assembly|BadImageFormatException|DllNotFoundException|FileNotFoundException|Unable to load DLL'
    $envLine = ([regex]::Match($text, "(?m)^.*($envRx).*$")).Value.Trim()
    $state = if ($envLine -and $pass -eq 0) { 'UNVERIFIED' }
    elseif ($code -eq 0 -and $failN -eq 0 -and $pass -gt 0) { 'PASS' }
    elseif ($code -ne 0 -or $failN -gt 0) { 'FAIL' } else { 'UNVERIFIED' }
    $codeText = if ($null -eq $code) { 'unknown' } else { "$code" }
    $note = "exit=$codeText PASS-lines=$pass FAIL-lines=$failN" + $(if ($envLine) { " ENVIRONMENT: harness could not start - $($envLine.Substring(0, [Math]::Min(160, $envLine.Length)))" } elseif ($state -eq 'UNVERIFIED') { ' (clean exit but no PASS line: not counted as a pass)' } else { '' })
    $results.Add([pscustomobject]@{ Name = $h.Name; State = $state; Note = $note })
    Write-Output "  $($h.Name): $state ($note)"
    if ($state -eq 'FAIL') {
        $msg = ([regex]::Match($text, '(?m)FAIL:\s*(.+)$')).Groups[1].Value.Trim()
        Write-Output "    harness message: $msg"
        Write-Output "    stack: powershell -NoProfile -File .claude\skills\etest\scripts\diag-harness-exception.ps1 -HarnessExe `"$($exe[0].FullName)`" -ExpectMessage `"$msg`""
    }
}
if ($TestScope) {
    if ($appRunning) { $results.Add([pscustomobject]@{ Name = "Persistence/$TestScope"; State = 'UNVERIFIED'; Note = 'skipped: EasyVision.exe running' }) }
    else {
        $ic = Join-Path $root '.agents\skills\ebuild\scripts\invoke-check.ps1'
        $o1 = & powershell -NoProfile -File $ic -Action Build -Target Persistence -Execute 2>&1 | Out-String
        $c1 = $LASTEXITCODE
        if ($c1 -ne 0) { $results.Add([pscustomobject]@{ Name = "Persistence/$TestScope"; State = 'FAIL'; Note = 'Persistence build failed' }) }
        else {
            $o2 = & powershell -NoProfile -File $ic -Action Test -Target Persistence -TestScope $TestScope -Execute 2>&1 | Out-String
            $c2 = $LASTEXITCODE
            [IO.File]::WriteAllText((Join-Path $runDir "Persistence.$TestScope.txt"), $o1 + $o2)
            $st = if ($c2 -eq 0 -and $o2 -match "PASS $TestScope") { 'PASS' } else { 'FAIL' }
            $results.Add([pscustomobject]@{ Name = "Persistence/$TestScope"; State = $st; Note = "exit=$c2" })
        }
        Write-Output "  Persistence/$TestScope`: $($results[$results.Count - 1].State)"
    }
}

# ---- verdict -------------------------------------------------------------------------------------------------------------
$fails = @($results | Where-Object State -eq 'FAIL'); $inc = @($results | Where-Object State -eq 'INCONCLUSIVE')
$passes = @($results | Where-Object State -eq 'PASS'); $unv = @($results | Where-Object State -eq 'UNVERIFIED')
Write-Output '-- VERDICT'
if ($fails.Count) { $v = "FAIL - $(($fails | ForEach-Object { $_.Name }) -join ', ')" }
elseif ($inc.Count) { $v = "INCONCLUSIVE - $(($inc | ForEach-Object { $_.Name }) -join ', ') timed out; builds OK" }
elseif ($passes.Count) { $v = "PASS - builds OK and $($passes.Count) harness(es) passed ($(($passes | ForEach-Object { $_.Name }) -join ', ')); live UI, DPI, language and hardware remain UNVERIFIED" }
else { $v = 'BUILD OK, NO AUTOMATED TEST APPLICABLE - runtime, UI and hardware behavior UNVERIFIED' }
Write-Output "verdict: $v"
if ($unv.Count) { Write-Output "unverified: $(($unv | ForEach-Object { $_.Name + ' (' + $_.Note + ')' }) -join '; ')" }
Write-Output "logs: $($runDir.Substring($root.Length + 1))"
try { Stop-Transcript | Out-Null } catch { }
exit $(if ($fails.Count) { 1 } else { 0 })
