<#
  EAI-1 S6: compares <ParityDir>\ref (EasyVision SegmentAI.DoWork via ref48) with <ParityDir>\easyai (EasyLabel SegAiSession).
  Usage: powershell -NoProfile -File compare.ps1 -ParityDir "artifacts\EAI\EAI-1\<run>\parity"
  Rules: native outputs (raw mask, confidence, normal confidence) bit-exact; if any differs the script reports and STOPS (exit 3, no tuning).
         filtered masks bit-exact; regions (bounds + area, sorted by Y,X,area) exact; scores |d| <= 1e-6; IsOK/width/height/counts equal.
  Exit codes: 0 all PASS, 1 at least one FAIL, 2 usage/missing folders, 3 native output differs.
#>
param(
    [Parameter(Mandatory = $true)][string]$ParityDir,
    [double]$ScoreTolerance = 1e-6
)

$ErrorActionPreference = 'Stop'
$inv = [System.Globalization.CultureInfo]::InvariantCulture
$ParityDir = (Resolve-Path -LiteralPath $ParityDir).Path
$refDir = Join-Path $ParityDir 'ref'
$easyDir = Join-Path $ParityDir 'easyai'
if (-not (Test-Path -LiteralPath $refDir) -or -not (Test-Path -LiteralPath $easyDir)) {
    Write-Host "ref or easyai folder missing under $ParityDir"; exit 2
}

function Read-Dump([string]$path) {
    $map = @{}; $regions = @(); $normals = @()
    foreach ($line in [System.IO.File]::ReadAllLines($path)) {
        if ($line.Length -eq 0) { continue }
        $sp = $line.IndexOf(' ')
        $key = if ($sp -lt 0) { $line } else { $line.Substring(0, $sp) }
        $val = if ($sp -lt 0) { '' } else { $line.Substring($sp + 1) }
        if ($key -eq 'r') { $regions += , $val } elseif ($key -eq 'n') { $normals += , $val } else { $map[$key] = $val }
    }
    return @{ Map = $map; R = $regions; N = $normals }
}

function Compare-Regions([string]$label, $a, $b, [double]$tol, [System.Collections.Generic.List[string]]$problems) {
    if ($a.Count -ne $b.Count) { $problems.Add("$label line count ref=$($a.Count) easyai=$($b.Count)"); return }
    for ($i = 0; $i -lt $a.Count; $i++) {
        $pa = $a[$i].Split(' '); $pb = $b[$i].Split(' ')
        $geoA = ($pa[0..4] -join ' '); $geoB = ($pb[0..4] -join ' ')
        if ($geoA -ne $geoB) { $problems.Add("$label[$i] geometry/area ref='$geoA' easyai='$geoB'"); continue }
        $sa = $pa[5]; $sb = $pb[5]
        if ($sa -eq '-' -or $sb -eq '-') {
            if ($sa -ne $sb) { $problems.Add("$label[$i] score presence ref=$sa easyai=$sb") }
            continue
        }
        $d = [math]::Abs([double]::Parse($sa, $inv) - [double]::Parse($sb, $inv))
        if ($d -gt $tol) { $problems.Add("$label[$i] score delta $d > $tol (ref=$sa easyai=$sb)") }
    }
}

function Get-Sha([string]$path) { (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash }

# Same native DLL on both sides?
$dllRef = Join-Path $refDir 'native-dll.txt'; $dllEasy = Join-Path $easyDir 'native-dll.txt'
if ((Test-Path $dllRef) -and (Test-Path $dllEasy)) {
    $shaRef = (Select-String -Path $dllRef -Pattern '^sha256 (.+)$').Matches[0].Groups[1].Value
    $shaEasy = (Select-String -Path $dllEasy -Pattern '^sha256 (.+)$').Matches[0].Groups[1].Value
    Write-Host ("native DLL ref   : " + ((Get-Content $dllRef) -join ' | '))
    Write-Host ("native DLL easyai: " + ((Get-Content $dllEasy) -join ' | '))
    if ($shaRef -ne $shaEasy) { Write-Host "WARNING: BeeNativeSegAI.dll differs between sides; parity result is not meaningful." }
} else { Write-Host "WARNING: native-dll.txt missing on a side; cannot confirm both sides used the same BeeNativeSegAI.dll." }

foreach ($f in 'models.txt') {
    $a = Join-Path $refDir $f; $b = Join-Path $easyDir $f
    if ((Test-Path $a) -and (Test-Path $b) -and ((Get-Content $a -Raw) -ne (Get-Content $b -Raw))) { Write-Host "WARNING: $f differs between sides" }
}

$names = @{}
Get-ChildItem -LiteralPath $refDir -File | Where-Object { $_.Extension -in '.txt', '.skip' -and $_.Name -notin 'models.txt', 'native-dll.txt' } | ForEach-Object { $names[[System.IO.Path]::GetFileNameWithoutExtension($_.Name)] = 1 }
Get-ChildItem -LiteralPath $easyDir -File | Where-Object { $_.Extension -in '.txt', '.skip' -and $_.Name -notin 'models.txt', 'native-dll.txt' } | ForEach-Object { $names[[System.IO.Path]::GetFileNameWithoutExtension($_.Name)] = 1 }

$pass = 0; $fail = 0; $skipped = 0; $nativeDiff = $false
$binKinds = @('raw', 'conf', 'nconf', 'mask', 'nmask')
$nativeKinds = @('raw', 'conf', 'nconf')

foreach ($case in ($names.Keys | Sort-Object)) {
    $problems = New-Object 'System.Collections.Generic.List[string]'
    $refTxt = Join-Path $refDir "$case.txt"; $easyTxt = Join-Path $easyDir "$case.txt"
    $refSkip = Test-Path (Join-Path $refDir "$case.skip"); $easySkip = Test-Path (Join-Path $easyDir "$case.skip")

    if ($refSkip -or $easySkip) {
        if ($refSkip -and $easySkip) { Write-Host "SKIP $case (invalid model on both sides)"; $skipped++ }
        else { $side = if ($refSkip) { 'ref' } else { 'easyai' }; Write-Host "FAIL $case : skip marker only on $side"; $fail++ }
        continue
    }
    if (-not (Test-Path $refTxt)) { $problems.Add('missing on ref') }
    if (-not (Test-Path $easyTxt)) { $problems.Add('missing on easyai') }
    if ($problems.Count -eq 0) {
        $r = Read-Dump $refTxt; $e = Read-Dump $easyTxt
        if ($r.Map['status'] -ne $e.Map['status']) { $problems.Add("status ref=$($r.Map['status']) easyai=$($e.Map['status'])") }
        elseif ($r.Map['status'] -eq 'error') {
            if ($r.Map['error'] -ne $e.Map['error']) { Write-Host "NOTE $case : both sides report an error, texts differ: ref='$($r.Map['error'])' easyai='$($e.Map['error'])'" }
        } else {
            foreach ($k in 'width', 'height', 'isok', 'regions', 'normalregions') {
                if ($r.Map[$k] -ne $e.Map[$k]) { $problems.Add("$k ref=$($r.Map[$k]) easyai=$($e.Map[$k])") }
            }
            if ($r.Map['usedgpu'] -ne $e.Map['usedgpu']) { $problems.Add("usedgpu ref=$($r.Map['usedgpu']) easyai=$($e.Map['usedgpu']) (GPU must be off)") }
            $d = [math]::Abs([double]::Parse($r.Map['score'], $inv) - [double]::Parse($e.Map['score'], $inv))
            if ($d -gt $ScoreTolerance) { $problems.Add("image score delta $d > $ScoreTolerance") }
            Compare-Regions 'region' $r.R $e.R $ScoreTolerance $problems
            Compare-Regions 'normalregion' $r.N $e.N $ScoreTolerance $problems

            foreach ($kind in $binKinds) {
                $pa = Join-Path $refDir "$case.$kind.bin"; $pb = Join-Path $easyDir "$case.$kind.bin"
                $ea = Test-Path $pa; $eb = Test-Path $pb
                if ($ea -ne $eb) { $problems.Add("$kind presence ref=$ea easyai=$eb"); if ($kind -in $nativeKinds) { $nativeDiff = $true }; continue }
                if (-not $ea) { continue }
                if ((Get-Item $pa).Length -ne (Get-Item $pb).Length -or (Get-Sha $pa) -ne (Get-Sha $pb)) {
                    $problems.Add("$kind bytes differ"); if ($kind -in $nativeKinds) { $nativeDiff = $true }
                }
            }
        }
    }

    if ($problems.Count -eq 0) { Write-Host "PASS $case"; $pass++ }
    else {
        Write-Host "FAIL $case"; $fail++
        foreach ($p in $problems) { Write-Host "     - $p" }
        if ($nativeDiff) {
            Write-Host "NATIVE OUTPUT DIFFERS for ${case}: stopping (no tuning). Check same BeeNativeSegAI.dll, OpenCV build, settings order, input bytes."
            break
        }
    }
}

Write-Host "SUMMARY pass=$pass fail=$fail skipped=$skipped"
if ($nativeDiff) { exit 3 }
if ($fail -gt 0 -or $pass -eq 0) { exit 1 }
exit 0
