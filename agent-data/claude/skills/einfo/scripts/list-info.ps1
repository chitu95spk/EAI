param(
    [ValidateSet('summary', 'pending', 'open', 'recent', 'group', 'id', 'skills', 'agents', 'deferred', 'tasks')][string]$View = 'summary',
    [string]$Id = '',
    [int]$Days = 0,                  # recent default 7, open default 30
    [ValidateSet('route', 'risk', 'category', 'backend', 'prefix', 'month')][string]$GroupBy = 'route',
    [string]$Filter = '',
    [int]$Limit = 25
)
# Smart read-only inventory of EasyVision contracts, skills, agents and handoffs.
$ErrorActionPreference = 'Stop'
$root = Split-Path (Split-Path (Split-Path (Split-Path $PSScriptRoot)))

function Clip([string]$s, [int]$n) { if ($null -eq $s) { return '' }; $s = ($s -replace '\s+', ' ').Trim(); if ($s.Length -gt $n) { $s.Substring(0, $n - 1) + '~' } else { $s } }
function HasFilter([string]$line) { [string]::IsNullOrEmpty($Filter) -or $line -match [regex]::Escape($Filter) }

# ---- load contracts -------------------------------------------------------------------------
function Read-Toml([string]$path) {
    $sec = ''; $v = @{}
    foreach ($l in [IO.File]::ReadLines($path)) {
        if ($l -match '^\[(.+)\]\s*$') { $sec = $Matches[1]; continue }
        if ($l -match '^\s*([A-Za-z_]+)\s*=\s*(.*?)\s*$') { $k = "$sec.$($Matches[1])"; if (-not $v.ContainsKey($k)) { $v[$k] = $Matches[2].Trim('"') } }
    }
    $v
}

$committed = @{}
try {
    Push-Location $root
    foreach ($s in (git log --format=%s -n 1500 2>$null)) {
        foreach ($m in [regex]::Matches($s, '\[([^\]]+)\]')) { foreach ($t in ($m.Groups[1].Value -split '[\s,/+]+')) { if ($t) { $committed[$t] = $true } } }
    }
} catch { } finally { Pop-Location }

# Status column of the map files (done / partial / ...), keyed by task ID.
$mapStatus = @{}
foreach ($m in '.vision-map.md', '.ui-map.md', '.optimization-map.md', '.interop-map.md', '.structure-map.md', '.code-review-map.md') {
    $mp = Join-Path $root $m
    if (-not (Test-Path $mp)) { continue }
    foreach ($ml in (Select-String -Path $mp -Pattern '^\|\s*[A-Z]+[0-9]+\s*\|' | ForEach-Object { $_.Line })) {
        $mc = $ml.Trim().Trim('|').Split('|') | ForEach-Object { $_.Trim() }
        if ($mc.Count -ge 2 -and -not $mapStatus.ContainsKey($mc[0])) { $mapStatus[$mc[0]] = ($mc[1] -replace '[^A-Za-z-].*$', '').ToLower() }
    }
}

$rows = New-Object System.Collections.Generic.List[object]
foreach ($dir in '.claude', '.codex') {
    $files = Get-ChildItem (Join-Path $root $dir) -Filter 'task-contract.*.toml' -ErrorAction SilentlyContinue | Where-Object { $_.Name -notmatch 'template' }
    foreach ($f in $files) {
        $v = Read-Toml $f.FullName
        $cid = $v['task.id']; if (-not $cid) { $cid = ($f.Name -replace '^task-contract\.|\.toml$', '') }
        $rows.Add([pscustomobject]@{
                Id = $cid; Backend = $dir.TrimStart('.'); Route = $v['dispatch.route']; Category = $v['dispatch.category']
                Risk = $v['task.risk']; Approved = ($v['approval.plan_approved'] -eq 'true'); Rev = $v['approval.approved_plan_revision']
                Build = $v['authorization.build']; Commit = $v['authorization.commit']; Title = $v['task.title']
                Date = $f.LastWriteTime; MapStatus = $mapStatus[$cid]; Done = ([bool]$committed[$cid] -or $mapStatus[$cid] -eq 'done'); Committed = [bool]$committed[$cid]; Path = $f.FullName
            })
    }
}
$all = @($rows | Sort-Object Date -Descending)

function State($r) {
    if (-not $r.Approved) { return 'NOT-APPROVED' }
    if ($r.Committed -and $r.MapStatus -eq 'done') { return 'done' }
    if ($r.Committed) { return 'committed' }
    if ($r.MapStatus -eq 'done') { return 'done(map)' }
    if ($r.MapStatus) { return "$($r.MapStatus)(map)" }
    'no-evidence'
}
function Line($r) { '{0,-18} {1,-6} {2,-8} {3,-6} {4,-12} {5}' -f $r.Id, $r.Backend, $r.Route, $r.Risk, (State $r), (Clip $r.Title 62) }
function Show-Rows($list, [string]$head) {
    $list = @($list | Where-Object { HasFilter (Line $_) })
    Write-Output "== $head ($($list.Count)) =="
    Write-Output ('{0,-18} {1,-6} {2,-8} {3,-6} {4,-12} {5}' -f 'ID', 'by', 'route', 'risk', 'state', 'title')
    foreach ($r in ($list | Select-Object -First $Limit)) { Write-Output (Line $r) }
    if ($list.Count -gt $Limit) { Write-Output "... $($list.Count - $Limit) more (raise -Limit or narrow with -Filter/-Days)" }
}

switch ($View) {
    'summary' {
        Write-Output "== EASYVISION SUMMARY ($($all.Count) contracts: claude=$(@($all | ? Backend -eq 'claude').Count), codex=$(@($all | ? Backend -eq 'codex').Count)) =="
        $na = @($all | Where-Object { -not $_.Approved })
        $nc = @($all | Where-Object { $_.Approved -and -not $_.Done })
        Write-Output ("plan approved: {0}  |  NOT approved: {1}  |  approved, no commit and map not done: {2}  |  done or committed: {3}" -f @($all | ? Approved).Count, $na.Count, $nc.Count, @($all | ? Done).Count)
        Write-Output ("route: " + ((($all | Group-Object Route | Sort-Object Count -Descending) | ForEach-Object { "$($_.Name)=$($_.Count)" }) -join ', '))
        Write-Output ("risk:  " + ((($all | Group-Object Risk | Sort-Object Count -Descending) | ForEach-Object { "$($_.Name)=$($_.Count)" }) -join ', '))
        $d7 = @($all | Where-Object { $_.Date -gt (Get-Date).AddDays(-7) })
        Write-Output ("last 7 days: {0} contracts touched" -f $d7.Count)
        Write-Output ''
        if ($na.Count) { Show-Rows ($na | Select-Object -First 8) 'ATTENTION: plan NOT approved' }
        $hi = @($nc | Where-Object { $_.Risk -match 'high' -and $_.Date -gt (Get-Date).AddDays(-14) })
        if ($hi.Count) { Write-Output ''; Show-Rows ($hi | Select-Object -First 8) 'ATTENTION: high-risk, approved, not done/committed (14 days)' }
        $def = @(Get-ChildItem (Join-Path $root 'tmp') -Recurse -Filter 'deferred.md' -ErrorAction SilentlyContinue)
        Write-Output ''
        Write-Output "deferred handoffs to resume: $($def.Count)"
        Write-Output ''
        Write-Output 'More: -View pending | open | recent [-Days N] | group [-GroupBy route|risk|category|backend|prefix|month] | id -Id <ID> | skills | agents | deferred | tasks'
        Write-Output 'done = map status done and commit [ID]; done(map) = map says done; committed = commit only; X(map) = map status such as partial; no-evidence = neither.'
    }
    'pending' { Show-Rows ($all | Where-Object { -not $_.Approved }) 'PLAN NOT APPROVED' }
    'open' {
        $d = if ($Days -gt 0) { $Days } else { 30 }
        Show-Rows ($all | Where-Object { $_.Approved -and -not $_.Done -and $_.Date -gt (Get-Date).AddDays(-$d) }) "APPROVED, NOT DONE/COMMITTED, last $d days"
    }
    'recent' {
        $d = if ($Days -gt 0) { $Days } else { 7 }
        $list = @($all | Where-Object { $_.Date -gt (Get-Date).AddDays(-$d) -and (HasFilter (Line $_)) })
        Write-Output "== RECENT: last $d days, $($list.Count) contracts, grouped by date =="
        foreach ($g in ($list | Group-Object { $_.Date.ToString('yyyy-MM-dd') } | Sort-Object Name -Descending)) {
            Write-Output "-- $($g.Name) ($($g.Count))"
            foreach ($r in ($g.Group | Select-Object -First $Limit)) { Write-Output ('  ' + (Line $r)) }
        }
    }
    'group' {
        $key = switch ($GroupBy) {
            'route' { { $_.Route } } 'risk' { { $_.Risk } } 'category' { { ($_.Category -split '/')[0] } } 'backend' { { $_.Backend } }
            'prefix' { { ($_.Id -replace '[0-9].*$', '') } } 'month' { { $_.Date.ToString('yyyy-MM') } }
        }
        Write-Output "== GROUPED BY $GroupBy =="
        foreach ($g in ($all | Where-Object { HasFilter (Line $_) } | Group-Object $key | Sort-Object Count -Descending)) {
            $ids = ($g.Group | Select-Object -First 12 | ForEach-Object { $_.Id }) -join ' '
            $more = if ($g.Count -gt 12) { " +$($g.Count - 12)" } else { '' }
            Write-Output ('{0,-24} {1,3}  {2}{3}' -f (Clip $g.Name 24), $g.Count, $ids, $more)
        }
    }
    'id' {
        if (-not $Id) { Write-Output 'Pass -Id <ID>'; break }
        $hit = @($all | Where-Object { $_.Id -eq $Id })
        if (-not $hit.Count) { $hit = @($all | Where-Object { $_.Id -like "*$Id*" }) }
        if (-not $hit.Count) { Write-Output "No contract matches '$Id'. Try -View recent or -Filter."; break }
        foreach ($r in ($hit | Select-Object -First 3)) {
            $v = Read-Toml $r.Path
            Write-Output "== $($r.Id) [$($r.Backend)] state=$(State $r) route=$($r.Route) category=$($r.Category) risk=$($r.Risk) =="
            Write-Output "file: $($r.Path.Substring($root.Length + 1))   modified: $($r.Date.ToString('yyyy-MM-dd HH:mm'))"
            Write-Output "title: $(Clip $r.Title 200)"
            Write-Output "goal: $(Clip $v['task.goal'] 300)"
            Write-Output "plan: approved=$($r.Approved) revision=$($r.Rev)"
            Write-Output "auth: build=$($r.Build) tests=$($v['authorization.run_tests']) commit=$($r.Commit) push=$($v['authorization.push']) hardware=$($v['authorization.hardware_test'])"
            Write-Output 'commits:'
            Push-Location $root
            $c = @(git log --oneline -5 --grep="\[$($r.Id)" 2>$null)
            Pop-Location
            if ($c.Count) { $c | ForEach-Object { Write-Output "  $_" } } else { Write-Output '  none found by [ID] subject' }
        }
    }
    'skills' {
        Write-Output '== SKILLS =='
        foreach ($pair in @(@('.claude/skills', 'claude'), @('.agents/skills', 'codex'))) {
            $names = @(Get-ChildItem (Join-Path $root $pair[0]) -Directory -ErrorAction SilentlyContinue | Where-Object { Test-Path (Join-Path $_.FullName 'SKILL.md') } | ForEach-Object { $_.Name })
            Write-Output "-- $($pair[1]) ($($names.Count)): $($names -join ', ')"
        }
        if ($Filter) {
            Write-Output '-- descriptions matching filter'
            foreach ($pair in @(@('.claude/skills', 'claude'), @('.agents/skills', 'codex'))) {
                foreach ($d in Get-ChildItem (Join-Path $root $pair[0]) -Directory -ErrorAction SilentlyContinue) {
                    $md = Join-Path $d.FullName 'SKILL.md'; if (-not (Test-Path $md)) { continue }
                    $desc = ''; foreach ($l in (Get-Content $md -TotalCount 8)) { if ($l -match '^description:\s*(.*)$') { $desc = $Matches[1]; break } }
                    $line = '{0,-24} {1,-6} | {2}' -f $d.Name, $pair[1], (Clip $desc 140)
                    if (HasFilter $line) { Write-Output $line }
                }
            }
        } else { Write-Output 'Add -Filter <word> to see descriptions of matching skills.' }
    }
    'agents' {
        Write-Output '== AGENTS (.claude/agents) =='
        foreach ($f in Get-ChildItem (Join-Path $root '.claude/agents') -Filter '*.md' -ErrorAction SilentlyContinue) {
            $n = ''; $m = ''; $t = ''
            foreach ($l in (Get-Content $f.FullName -TotalCount 8)) { if ($l -match '^name:\s*(.*)$') { $n = $Matches[1] } elseif ($l -match '^model:\s*(.*)$') { $m = $Matches[1] } elseif ($l -match '^tools:\s*(.*)$') { $t = $Matches[1] } }
            Write-Output ('{0,-24} {1,-7} {2}' -f $n, $m, $t)
        }
    }
    'deferred' {
        Write-Output '== DEFERRED HANDOFFS (tmp/*/deferred.md), newest first =='
        $f = @(Get-ChildItem (Join-Path $root 'tmp') -Recurse -Filter 'deferred.md' -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending)
        if (-not $f.Count) { Write-Output 'none' }
        foreach ($x in $f) { Write-Output ('{0:yyyy-MM-dd HH:mm}  {1}' -f $x.LastWriteTime, $x.FullName.Substring($root.Length + 1)) }
    }
    'tasks' {
        Write-Output '== TASK IDs in map files (ID | status | risk | summary) =='
        foreach ($m in '.vision-map.md', '.ui-map.md', '.optimization-map.md', '.interop-map.md', '.structure-map.md', '.code-review-map.md') {
            $p = Join-Path $root $m; if (-not (Test-Path $p)) { continue }
            $rs = @(Select-String -Path $p -Pattern '^\|\s*[A-Z]+[0-9]+\s*\|' | ForEach-Object { $_.Line } | Where-Object { HasFilter $_ })
            Write-Output "-- $m ($($rs.Count) rows, showing last $Limit)"
            foreach ($r in ($rs | Select-Object -Last $Limit)) {
                $c = $r.Trim('|').Split('|') | ForEach-Object { $_.Trim() }
                Write-Output ('{0,-8} {1,-8} {2,-7} {3}' -f $c[0], (Clip $c[1] 8), (Clip $c[2] 7), (Clip $c[3] 80))
            }
        }
    }
}
