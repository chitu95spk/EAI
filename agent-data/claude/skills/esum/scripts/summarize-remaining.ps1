param(
    [string]$SessionId = '',     # Claude session GUID; default = most recently written transcript of this project
    [datetime]$Since,            # explicit session start (overrides -SessionId)
    [string]$Id = '',            # optional: narrow to these IDs (comma list) within the session
    [int]$Limit = 8,             # max contracts
    [switch]$NoHistory           # skip the history/session grep (faster)
)
# Summarize what is still open for the CURRENT SESSION only: contracts touched since the session began, commits and files
# changed since then. Read-only.
$ErrorActionPreference = 'Stop'
$root = Split-Path (Split-Path (Split-Path (Split-Path $PSScriptRoot)))

# ---- session window -------------------------------------------------------------------------
$projDir = Join-Path (Join-Path $env:USERPROFILE '.claude\projects') ($root -replace '[:\\/ _.]', '-')
$sessionNote = ''
$tr = $null
if (-not $PSBoundParameters.ContainsKey('Since')) {
    if ($SessionId) { $tr = Get-Item (Join-Path $projDir "$SessionId.jsonl") -ErrorAction SilentlyContinue }
    elseif (Test-Path $projDir) { $tr = Get-ChildItem $projDir -Filter '*.jsonl' -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1 }
    if ($tr) { $Since = $tr.CreationTime; $sessionNote = "session transcript $($tr.BaseName) started $($Since.ToString('yyyy-MM-dd HH:mm'))" + $(if (-not $SessionId) { ' (guessed: newest transcript; pass -SessionId if other sessions are active)' } else { '' }) }
    else { Write-Output 'Cannot find this session transcript. Pass -Since "yyyy-MM-dd HH:mm" or -SessionId <guid>.'; exit 1 }
} else { $sessionNote = "explicit start $($Since.ToString('yyyy-MM-dd HH:mm'))" }

# Files this session edited itself (Edit/Write tool calls in the main transcript; subagent edits are not counted).
$myEdits = @()
if ($tr) {
    $myEdits = @()
    foreach ($hit in (Select-String -LiteralPath $tr.FullName -Pattern '"name":"(?:Edit|Write)"')) {
        try { $rec = $hit.Line | ConvertFrom-Json } catch { continue }
        if ($rec.message.content -is [array]) {
            foreach ($blk in $rec.message.content) {
                if ($blk.type -eq 'tool_use' -and ($blk.name -eq 'Edit' -or $blk.name -eq 'Write') -and $blk.input.file_path) { $myEdits += [string]$blk.input.file_path }
            }
        }
    }
    $myEdits = @($myEdits | Sort-Object -Unique)
}

function Clip([string]$s, [int]$n) { if ($null -eq $s) { return '' }; $s = ($s -replace '\s+', ' ').Trim(); if ($s.Length -gt $n) { $s.Substring(0, $n - 1) + '~' } else { $s } }
function Get-Scalar([string]$text, [string]$section, [string]$key) {
    $m = [regex]::Match($text, ('(?ms)^\[' + [regex]::Escape($section) + '\]\s*$' + '(.*?)(?=^\[|\z)'))
    if (-not $m.Success) { return '' }
    $k = [regex]::Match($m.Groups[1].Value, "(?m)^\s*$key\s*=\s*(.*?)\s*$")
    if ($k.Success) { $k.Groups[1].Value.Trim('"') } else { '' }
}
function Get-List([string]$text, [string]$section, [string]$key) {
    $m = [regex]::Match($text, ('(?ms)^\[' + [regex]::Escape($section) + '\]\s*$' + '(.*?)(?=^\[|\z)'))
    if (-not $m.Success) { return @() }
    $k = [regex]::Match($m.Groups[1].Value, "(?ms)^\s*$key\s*=\s*\[(.*?)\]")
    if (-not $k.Success) { return @() }
    @([regex]::Matches($k.Groups[1].Value, '"((?:[^"\\]|\\.)*)"') | ForEach-Object { $_.Groups[1].Value })
}

# commit evidence + map status
$committed = @{}
try {
    Push-Location $root
    foreach ($s in (git log --format=%s -n 1500 2>$null)) { foreach ($m in [regex]::Matches($s, '\[([^\]]+)\]')) { foreach ($t in ($m.Groups[1].Value -split '[\s,/+]+')) { if ($t) { $committed[$t] = $true } } } }
} catch { } finally { Pop-Location }
$mapRow = @{}
foreach ($mf in '.vision-map.md', '.ui-map.md', '.optimization-map.md', '.interop-map.md', '.structure-map.md', '.code-review-map.md') {
    $mp = Join-Path $root $mf; if (-not (Test-Path $mp)) { continue }
    foreach ($ml in (Select-String -Path $mp -Pattern '^\|\s*[A-Z]+[0-9]+\s*\|' | ForEach-Object { $_.Line })) {
        $c = $ml.Trim().Trim('|').Split('|') | ForEach-Object { $_.Trim() }
        if (-not $mapRow.ContainsKey($c[0])) { $mapRow[$c[0]] = [pscustomobject]@{ Map = $mf; Status = ($c[1] -replace '[^A-Za-z-].*$', '').ToLower(); Text = (Clip ($c[3..($c.Count - 1)] -join ' | ') 220) } }
    }
}

$cands = New-Object System.Collections.Generic.List[object]
foreach ($dir in '.claude', '.codex') {
    foreach ($f in (Get-ChildItem (Join-Path $root $dir) -Filter 'task-contract.*.toml' -ErrorAction SilentlyContinue | Where-Object { $_.Name -notmatch 'template' })) {
        $text = [IO.File]::ReadAllText($f.FullName)
        $cid = Get-Scalar $text 'task' 'id'; if (-not $cid) { $cid = ($f.Name -replace '^task-contract\.|\.toml$', '') }
        $ms = if ($mapRow.ContainsKey($cid)) { $mapRow[$cid].Status } else { '' }
        $cands.Add([pscustomobject]@{ Id = $cid; Backend = $dir.TrimStart('.'); File = $f; Text = $text; Date = $f.LastWriteTime
                Approved = ((Get-Scalar $text 'approval' 'plan_approved') -eq 'true'); Done = ($committed[$cid] -and $ms -eq 'done') -or ($ms -eq 'done'); Committed = [bool]$committed[$cid]; MapStatus = $ms })
    }
}

$sel = @($cands | Where-Object { $c0 = $_; $_.Date -ge $Since -and (@($myEdits | Where-Object { $_ -like "*task-contract.$($c0.Id).toml" }).Count -gt 0) } | Sort-Object Date -Descending)
if ($Id) { $want = $Id -split '[,\s]+' | Where-Object { $_ }; $sel = @($cands | Where-Object { $want -contains $_.Id } | Sort-Object Date -Descending) }
$selAll = $sel.Count
$sel = @($sel | Select-Object -First $Limit)
Write-Output "== SESSION SUMMARY: $sessionNote =="
Push-Location $root
$sc = @(git log --since=$($Since.ToString('yyyy-MM-ddTHH:mm:ss')) --format='%h %s' 2>$null)
$wt = @(git status --porcelain 2>$null | ForEach-Object { ($_.Substring(3) -split ' -> ')[-1].Trim('"') } | Where-Object { (Test-Path -LiteralPath $_) -and ((Get-Item -LiteralPath $_ -ErrorAction SilentlyContinue).LastWriteTime -ge $Since) })
Pop-Location
Write-Output "commits since session start, any author/session: $($sc.Count)"
foreach ($x in ($sc | Select-Object -First 8)) { Write-Output "  $(Clip $x 120)" }
Write-Output "files edited by THIS session (Edit/Write calls): $($myEdits.Count)"
foreach ($g in ($myEdits | Group-Object { (Split-Path (Split-Path $_ -Parent) -Leaf) } | Sort-Object Count -Descending | Select-Object -First 8)) { Write-Output ("  {0,-28} {1}" -f $g.Name, $g.Count) }
Write-Output "worktree files modified since start, any session or tool: $($wt.Count) (may include concurrent sessions/Codex)"
foreach ($g in ($wt | Group-Object { ($_ -split '[\/]')[0] } | Sort-Object Count -Descending | Select-Object -First 8)) { Write-Output ("  {0,-28} {1}" -f $g.Name, $g.Count) }
$def = @(Get-ChildItem (Join-Path $root 'tmp') -Recurse -Filter 'deferred.md' -ErrorAction SilentlyContinue | Where-Object { $_.LastWriteTime -ge $Since })
Write-Output "deferred handoffs written this session: $($def.Count)"
foreach ($d in $def) { Write-Output "  $($d.FullName.Substring($root.Length + 1))" }
if (-not $sel.Count) { Write-Output 'contracts edited by this session: 0 (this session made no task-contract change; use -Id to look up one).'; exit 0 }
Write-Output "contracts edited by this session: $selAll (showing $($sel.Count))"

$histFiles = @('CODEX_HISTORY.md', '.codex-session.md') | ForEach-Object { Join-Path $root $_ } | Where-Object { Test-Path $_ }
$owed = '(?i)\b(owed|unverified|blocker|blocked|remaining|not run|not yet|pending|todo|hardware)\b'

foreach ($c in $sel) {
    $t = $c.Text
    $state = if (-not $c.Approved) { 'PLAN NOT APPROVED' } elseif ($c.MapStatus -eq 'done') { 'done' } elseif ($c.Committed) { 'committed (map: ' + $(if ($c.MapStatus) { $c.MapStatus } else { 'n/a' }) + ')' } elseif ($c.MapStatus) { "$($c.MapStatus) (map)" } else { 'no evidence of completion' }
    Write-Output ''
    Write-Output "### $($c.Id) [$($c.Backend)] state: $state | route=$(Get-Scalar $t 'dispatch' 'route') risk=$(Get-Scalar $t 'task' 'risk') | file modified $($c.Date.ToString('yyyy-MM-dd'))"
    Write-Output "title: $(Clip (Get-Scalar $t 'task' 'title') 160)"
    Write-Output "goal: $(Clip (Get-Scalar $t 'task' 'goal') 260)"
    $acc = @(Get-List $t 'acceptance' 'behavior'); $ev = @(Get-List $t 'acceptance' 'evidence_required'); $perf = @(Get-List $t 'acceptance' 'performance')
    Write-Output "acceptance: behavior=$($acc.Count) evidence_required=$($ev.Count) performance=$($perf.Count)"
    foreach ($e in ($ev | Select-Object -First 4)) { Write-Output "  evidence: $(Clip $e 170)" }
    if ($ev.Count -gt 4) { Write-Output "  ... $($ev.Count - 4) more evidence items in the contract" }
    Write-Output ("authorization: build={0} tests={1} hardware={2} commit={3}" -f (Get-Scalar $t 'authorization' 'build'), (Get-Scalar $t 'authorization' 'run_tests'), (Get-Scalar $t 'authorization' 'hardware_test'), (Get-Scalar $t 'authorization' 'commit'))
    if ($mapRow.ContainsKey($c.Id)) { Write-Output "map ($($mapRow[$c.Id].Map), $($mapRow[$c.Id].Status)): $($mapRow[$c.Id].Text)" }
    if (-not $NoHistory -and $histFiles.Count) {
        $hits = @($histFiles | ForEach-Object { Select-String -Path $_ -Pattern "\b$([regex]::Escape($c.Id))\b" } | Where-Object { $_.Line -match $owed } | Select-Object -Last 3)
        foreach ($h in $hits) { Write-Output ("  note ({0}:{1}): {2}" -f (Split-Path $h.Path -Leaf), $h.LineNumber, (Clip $h.Line 230)) }
        if (-not $hits.Count) { Write-Output '  note: no owed/blocker line mentions this ID in history/session logs' }
    }
    Push-Location $root
    $cm = @(git log --oneline -3 --grep="\[$($c.Id)" 2>$null)
    Pop-Location
    if ($cm.Count) { Write-Output "commits: $($cm -join ' ; ')" } else { Write-Output 'commits: none carrying [ID]' }
    Write-Output "next: /eresume $($c.Id)   |   detail: /einfo -View id -Id $($c.Id)"
}
