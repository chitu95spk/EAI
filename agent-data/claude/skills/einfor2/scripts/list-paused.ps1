param(
    [string]$Id = '',          # one TaskId (or part of it) for the detailed view
    [int]$Limit = 20
)
# Read-only. Lists paused work: every tmp\<TaskId>\deferred.md with the contract name, pause time and what is left.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = Split-Path (Split-Path (Split-Path (Split-Path $PSScriptRoot)))

function Clip([string]$s, [int]$n) { $s = ($s -replace '\s+', ' ').Trim(); if ($s.Length -gt $n) { $s.Substring(0, $n - 1) + '~' } else { $s } }
function Find-Contract([string]$task) {
    $cands = @($task, ($task -replace '-r\d+.*$', ''), ($task -replace '-.*$', ''))
    foreach ($c in ($cands | Select-Object -Unique)) {
        foreach ($d in '.claude', '.codex') {
            $p = Join-Path $root "$d\task-contract.$c.toml"
            if (Test-Path -LiteralPath $p) { return $p }
        }
    }
    $null
}
function Toml-Value([string]$text, [string]$key) { $m = [regex]::Match($text, "(?m)^\s*$key\s*=\s*""([^""]*)"""); if ($m.Success) { $m.Groups[1].Value } else { '' } }
function Last-Section([string]$text) {
    $parts = [regex]::Split($text, '(?m)^## Pause ')
    if ($parts.Count -gt 1) { 'Pause ' + $parts[-1] } else { $text }
}
function Section-Body([string]$sec, [string]$head) {
    $m = [regex]::Match($sec, ('(?ms)^### ' + [regex]::Escape($head) + '\s*$' + '(.*?)(?=^### |\z)'))
    if ($m.Success) { $m.Groups[1].Value.Trim() } else { '' }
}

$files = @(Get-ChildItem (Join-Path $root 'tmp') -Recurse -Filter 'deferred.md' -ErrorAction SilentlyContinue |
        Where-Object { $_.Directory.Name -notmatch '^_selftest' } | Sort-Object LastWriteTime -Descending)
if ($Id) { $files = @($files | Where-Object { $_.Directory.Name -like "*$Id*" }) }
Write-Output "== PAUSED WORK ($($files.Count) handoff file(s) under tmp\) =="
if (-not $files.Count) { Write-Output 'Nothing paused. A pause creates tmp\<TaskId>\deferred.md (use /epause).'; exit 0 }

$rows = foreach ($f in $files) {
    $task = $f.Directory.Name
    $text = [IO.File]::ReadAllText($f.FullName)
    $sec = Last-Section $text
    $cp = Find-Contract $task
    $ctext = if ($cp) { [IO.File]::ReadAllText($cp) } else { '' }
    $open = ([regex]::Matches($sec, 'TODO\(claude\)')).Count
    $pauseAt = [regex]::Match($sec, '^Pause (\d{4}-\d\d-\d\d \d\d:\d\d)').Groups[1].Value
    $reason = ([regex]::Match($sec, '^Pause [\d\- :]+- (.*)$', 'Multiline')).Groups[1].Value.Trim()
    $usage = ([regex]::Match($sec, '(?m)^- usage: (.*)$')).Groups[1].Value
    $uncommitted = ([regex]::Match($sec, 'still uncommitted: (\d+)')).Groups[1].Value
    $remaining = Section-Body $sec 'Remaining (in order)'
    $first = ''
    if ($remaining -and $remaining -notmatch 'TODO\(claude\)') { $first = ($remaining -split "`n" | Where-Object { $_.Trim() } | Select-Object -First 1) }
    $issues = Join-Path $f.Directory.FullName 'issues.md'
    $openIssues = -1
    if (Test-Path -LiteralPath $issues) { $openIssues = @(Select-String -LiteralPath $issues -Pattern '\b(open|fix|ask-user)\b' | Where-Object { $_.Line -match '^\|' }).Count }
    [pscustomobject]@{
        Task = $task; Contract = $(if ($cp) { $cp.Substring($root.Length + 1) } else { '(no contract file found)' }); Title = (Toml-Value $ctext 'title')
        Route = (Toml-Value $ctext 'route'); Risk = (Toml-Value $ctext 'risk'); Modified = $f.LastWriteTime; PauseAt = $pauseAt; Reason = $reason
        Open = $open; Usage = $usage; Uncommitted = $uncommitted; First = $first; IssuesOpen = $openIssues; Path = $f.FullName.Substring($root.Length + 1)
        Pauses = ([regex]::Matches($text, '(?m)^## Pause ')).Count
    }
}

if (-not $Id) {
    Write-Output ('{0,-22} {1,-16} {2,-9} {3,-7} {4,-10} {5}' -f 'TASK', 'PAUSED', 'ROUTE', 'RISK', 'HANDOFF', 'CONTRACT NAME')
    foreach ($r in ($rows | Select-Object -First $Limit)) {
        $state = if ($r.Open -gt 0) { "INCOMPLETE($($r.Open))" } else { 'READY' }
        $when = if ($r.PauseAt) { $r.PauseAt } else { $r.Modified.ToString('yyyy-MM-dd HH:mm') }
        $name = if ($r.Title) { Clip $r.Title 70 } else { '(no title: contract missing)' }
        Write-Output ('{0,-22} {1,-16} {2,-9} {3,-7} {4,-10} {5}' -f (Clip $r.Task 22), $when, $r.Route, $r.Risk, $state, $name)
    }
    if ($rows.Count -gt $Limit) { Write-Output "... $($rows.Count - $Limit) more (-Limit N)" }
    Write-Output ''
    Write-Output 'READY = every TODO(claude) in the latest pause section is filled; INCOMPLETE(n) = n placeholders still open, so the handoff is not trustworthy yet.'
    Write-Output 'Details: -Id <TaskId>. Resume: /clear (or a new session), then /eresume <TaskId>.'
    exit 0
}

foreach ($r in $rows) {
    Write-Output ''
    Write-Output "### $($r.Task)  [$(if ($r.Open -gt 0) { "handoff INCOMPLETE: $($r.Open) placeholders" } else { 'handoff READY' })]"
    Write-Output "contract : $($r.Contract)"
    Write-Output "name     : $($r.Title)"
    Write-Output "route/risk: $($r.Route) / $($r.Risk)"
    Write-Output "paused   : $(if ($r.PauseAt) { $r.PauseAt } else { 'unknown' }) | reason: $(Clip $r.Reason 80) | pauses recorded: $($r.Pauses)"
    Write-Output "usage at pause: $(if ($r.Usage) { Clip $r.Usage 160 } else { 'not recorded' })"
    Write-Output "uncommitted session files at pause: $(if ($r.Uncommitted) { $r.Uncommitted } else { 'not recorded' })"
    Write-Output "first remaining step: $(if ($r.First) { Clip $r.First 160 } else { '(handoff does not list one yet)' })"
    Write-Output "open issues in ledger: $(if ($r.IssuesOpen -ge 0) { $r.IssuesOpen } else { 'no ledger' })"
    Write-Output "handoff  : $($r.Path)"
    Write-Output "resume   : /clear (or new session) then /eresume $($r.Task)"
}
