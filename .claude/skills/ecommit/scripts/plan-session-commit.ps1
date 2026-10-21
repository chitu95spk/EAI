param(
    [Parameter(Mandatory = $true)][string]$SessionId,
    [string[]]$AlsoFile = @()    # files this session changed through Bash/Python/subagents (not visible in the transcript)
)
# Read-only. Decides what THIS session may commit: files it edited that are still uncommitted, grouped per task contract.
# Everything else that is dirty is left for the sessions that own it. It never stages or commits.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = Split-Path (Split-Path (Split-Path (Split-Path $PSScriptRoot)))
$AlsoFile = @($AlsoFile | ForEach-Object { $_ -split ',' } | Where-Object { $_ } | ForEach-Object { $_.Trim().Replace('\', '/') })
$tr = Join-Path (Join-Path (Join-Path $env:USERPROFILE '.claude\projects') ($root -replace '[:\\/ _.]', '-')) "$SessionId.jsonl"
if (-not (Test-Path -LiteralPath $tr)) { Write-Output "Transcript not found: $tr"; exit 1 }

function ToRel([string]$p) {
    $p = $p.Replace('\', '/'); $r = $root.Replace('\', '/')
    if ($p.StartsWith($r + '/', [StringComparison]::OrdinalIgnoreCase)) { return $p.Substring($r.Length + 1) }
    if ([IO.Path]::IsPathRooted($p)) { return $null }
    $p
}
$edited = @()
foreach ($hit in (Select-String -LiteralPath $tr -Pattern '"name":"(?:Edit|Write)"')) {
    try { $rec = $hit.Line | ConvertFrom-Json } catch { continue }
    if ($rec.PSObject.Properties['message'] -and $rec.message.content -is [array]) {
        foreach ($blk in $rec.message.content) {
            if ($blk.type -eq 'tool_use' -and ($blk.name -eq 'Edit' -or $blk.name -eq 'Write') -and $blk.input.file_path) { $edited += [string]$blk.input.file_path }
        }
    }
}
$edited = @($edited) + $AlsoFile
$mine = @{}
foreach ($e in $edited) { $r = ToRel $e; if ($r) { $mine[$r.ToLower()] = $r } }

# ---- dirty worktree ---------------------------------------------------------------------------------------------------
Push-Location $root
# Native stderr (e.g. "Filename too long" inside tmp/ venv trees) must not become a terminating error under Stop (L040): relax it for
# these calls, keep only string output, and do not walk the output folders at all.
$prevEap = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
$status = @(& git status --porcelain -uall -- . ':(exclude)tmp' ':(exclude)artifacts' ':(exclude).claude/worktrees' 2>&1 | Where-Object { $_ -is [string] -and $_ -notmatch '^(warning|fatal|error):' })
$stagedOther = @(& git diff --cached --name-only 2>&1 | Where-Object { $_ -is [string] -and $_ -notmatch '^(warning|fatal|error):' })
$ErrorActionPreference = $prevEap
Pop-Location
$dirty = @{}
foreach ($s in $status) {
    if ($s.Length -lt 4) { continue }
    $code = $s.Substring(0, 2); $path = ($s.Substring(3) -split ' -> ')[-1].Trim('"')
    $dirty[$path.ToLower()] = [pscustomobject]@{ Path = $path; Code = $code.Trim(); Staged = ($code.Substring(0, 1) -notin @(' ', '?')) }
}
$never = '(^|/)(bin|obj|x64|Debug|Release|\.vs|tmp|artifacts)/|\.(pdb|user)$|settings\.local\.json$'
$own = @(); $excluded = @()
foreach ($k in $mine.Keys) {
    if (-not $dirty.ContainsKey($k)) { continue }          # already committed or unchanged
    $d = $dirty[$k]
    if ($d.Path -match $never) { $excluded += $d.Path; continue }
    $own += $d
}

# ---- contracts: session-edited vs others ----------------------------------------------------------------------------------
function Get-InScope([string]$text) {
    $m = [regex]::Match($text, '(?ms)^\[task\]\s*$(.*?)(?=^\[|\z)')
    if (-not $m.Success) { return @() }
    $k = [regex]::Match($m.Groups[1].Value, '(?ms)^\s*in_scope\s*=\s*\[(.*?)\]')
    if (-not $k.Success) { return @() }
    @([regex]::Matches($k.Groups[1].Value, '"((?:[^"\\]|\\.)*)"') | ForEach-Object { ($_.Groups[1].Value -replace '`', '').Replace('\', '/') })
}
$committed = @{}
Push-Location $root
foreach ($s in (git log --format=%s -n 1500 2>$null)) { foreach ($m in [regex]::Matches($s, '\[([^\]]+)\]')) { foreach ($t in ($m.Groups[1].Value -split '[\s,/+]+')) { if ($t) { $committed[$t] = $true } } } }
Pop-Location
$contracts = @()
foreach ($dir in '.claude', '.codex') {
    foreach ($f in (Get-ChildItem (Join-Path $root $dir) -Filter 'task-contract.*.toml' -ErrorAction SilentlyContinue | Where-Object { $_.Name -notmatch 'template' })) {
        $text = [IO.File]::ReadAllText($f.FullName)
        $id = [regex]::Match($text, '(?m)^\s*id\s*=\s*"([^"]*)"').Groups[1].Value; if (-not $id) { $id = ($f.Name -replace '^task-contract\.|\.toml$', '') }
        $title = [regex]::Match($text, '(?m)^\s*title\s*=\s*"([^"]*)"').Groups[1].Value
        $rel = "$dir/$($f.Name)"
        $contracts += [pscustomobject]@{ Id = $id; Title = $title; Rel = $rel; Scope = @(Get-InScope $text); Mine = $mine.ContainsKey($rel.ToLower()); Committed = [bool]$committed[$id] }
    }
}
function InScope($c, [string]$p) { foreach ($s in $c.Scope) { $s = $s.TrimEnd('/'); if ($s -and ($p -eq $s -or $p.StartsWith($s + '/') -or $p -like $s)) { return $true } }; $false }
$mineContracts = @($contracts | Where-Object { $_.Mine })

$groups = @{}; $overlap = @()
foreach ($d in $own) {
    $owners = @($mineContracts | Where-Object { $_.Rel -eq $d.Path -or (InScope $_ $d.Path) })
    if ($d.Path -match '^\.(claude|codex)/task-contract\.(.+)\.toml$') { $owners = @($contracts | Where-Object { $_.Rel -eq $d.Path }) }
    $others = @($contracts | Where-Object { -not $_.Mine -and -not $_.Committed -and (InScope $_ $d.Path) })
    if ($d.Staged) { $overlap += "$($d.Path)  is already staged (index differs from HEAD) by someone else or earlier work; this session's edit cannot be isolated" }
    if ($others.Count) { $overlap += "$($d.Path)  also in scope of uncommitted contract(s): $((($others | ForEach-Object { $_.Id }) -join ', '))" }
    $key = if ($owners.Count) { $owners[0].Id } elseif ($d.Path -match '^(\.claude|\.agents|\.codex|docs|CLAUDE\.md|AGENTS\.md|tools/[^/]+\.(ps1|py)$)') { 'process' } else { 'UNSCOPED-CODE' }
    if (-not $groups.ContainsKey($key)) { $groups[$key] = New-Object System.Collections.Generic.List[object] }
    $groups[$key].Add($d)
}

Write-Output "== EV COMMIT PLAN (session $SessionId) =="
Write-Output ("session-edited files: {0} | still uncommitted: {1} | never-commit paths skipped: {2} | other dirty files left for other sessions: {3}" -f $mine.Count, $own.Count, $excluded.Count, @($dirty.Values | Where-Object { $_.Path -notmatch $never -and -not $mine.ContainsKey($_.Path.ToLower()) }).Count)
$otherStaged = @($stagedOther | Where-Object { -not $mine.ContainsKey($_.ToLower()) })
if ($otherStaged.Count) { Write-Output "NOTE: $($otherStaged.Count) file(s) already staged by someone else; commit with explicit pathspecs (git commit -m ... -- <paths>) so they stay out." }
$others = @($dirty.Values | Where-Object { $_.Path -notmatch $never -and -not $mine.ContainsKey($_.Path.ToLower()) })
if ($others.Count) {
    Write-Output '-- dirty files NOT attributed to this session (left alone). If some are YOUR edits made via Bash/Python/subagents, rerun with -AlsoFile:'
    foreach ($g in ($others | Group-Object { ($_.Path -split '/')[0] } | Sort-Object Count -Descending | Select-Object -First 10)) { Write-Output ('   {0,-28} {1}' -f $g.Name, $g.Count) }
}
if (-not $own.Count) { Write-Output 'Nothing to commit for this session.'; exit 0 }
foreach ($key in ($groups.Keys | Sort-Object)) {
    $c = $contracts | Where-Object { $_.Id -eq $key } | Select-Object -First 1
    $head = if ($c) { "contract $key  [$(if ($c.Committed) { 'already has a commit; this is a follow-up' } else { 'NOT committed yet' })]  $($c.Title.Substring(0, [Math]::Min(60, $c.Title.Length)))" }
    elseif ($key -eq 'process') { 'process / skills / docs / rules (no contract) - subject [process] ...' }
    else { 'code with no session contract - decide the task ID before committing (ask the user)' }
    Write-Output ''
    Write-Output "-- $head  ($($groups[$key].Count) files)"
    foreach ($d in ($groups[$key] | Select-Object -First 25)) { Write-Output ("   {0,-2} {1}" -f $d.Code, $d.Path) }
    if ($groups[$key].Count -gt 25) { Write-Output "   ... $($groups[$key].Count - 25) more" }
}
if ($overlap.Count) { Write-Output ''; Write-Output '-- OVERLAP (may contain another session''s hunks; do not commit until the user decides)'; $overlap | Select-Object -First 10 | ForEach-Object { Write-Output "   $_" } }
Write-Output ''
Write-Output 'Next: per group run the checks the contract requires, then stage only the listed paths and commit with explicit pathspecs. Nothing was staged or committed by this script.'
