param(
    [Parameter(Mandatory = $true)][string]$SessionId,
    [Parameter(Mandatory = $true)][string]$TaskId,     # folder name under tmp\, usually the contract ID (e.g. V176) or a short slug
    [string]$Reason = 'token budget nearly exhausted',
    [switch]$Check                                     # only verify that the handoff has no TODO(claude) placeholders left
)
# Creates (or appends a section to) tmp\<TaskId>\deferred.md with every machine-readable fact filled in, and leaves
# TODO(claude) placeholders for the judgment parts. Nothing outside tmp\ is written. Read-only on git and the transcript.
Set-Location -LiteralPath (Split-Path (Split-Path (Split-Path (Split-Path $PSScriptRoot))))
$root = (Get-Location).Path
$dir = Join-Path $root "tmp\$TaskId"
$file = Join-Path $dir 'deferred.md'

if ($Check) {
    if (-not (Test-Path -LiteralPath $file)) { Write-Output "MISSING: $file"; exit 1 }
    $todo = @(Select-String -LiteralPath $file -Pattern 'TODO\(claude\)')
    if ($todo.Count) { Write-Output "NOT READY: $($todo.Count) placeholder(s) still open in tmp\$TaskId\deferred.md"; $todo | Select-Object -First 8 | ForEach-Object { Write-Output ("  line {0}: {1}" -f $_.LineNumber, $_.Line.Trim()) }; exit 1 }
    Write-Output "READY: tmp\$TaskId\deferred.md has no open placeholders"; exit 0
}

$tr = Join-Path (Join-Path (Join-Path $env:USERPROFILE '.claude\projects') ($root -replace '[:\\/ _.]', '-')) "$SessionId.jsonl"
$now = Get-Date

# ---- usage snapshot (written by tools\statusline_usage.ps1) -----------------------------------------------------------------
$usageLine = 'usage: unknown (tmp\usage\statusline.json missing; ask the user for /usage)'
$uf = Join-Path $root 'tmp\usage\statusline.json'
if (Test-Path -LiteralPath $uf) {
    try {
        $u = Get-Content -LiteralPath $uf -Raw | ConvertFrom-Json
        $age = [int](($now - (Get-Item -LiteralPath $uf).LastWriteTime).TotalMinutes)
        $five = $null; if ($u.PSObject.Properties['rate_limits'] -and $u.rate_limits.PSObject.Properties['five_hour']) { $five = $u.rate_limits.five_hour }
        $ctx = $null; if ($u.PSObject.Properties['context_window']) { $ctx = $u.context_window.used_percentage }
        $parts = @("snapshot age ${age} min")
        if ($five) { $reset = [DateTimeOffset]::FromUnixTimeSeconds([long]$five.resets_at).LocalDateTime; $parts += "5h window used $($five.used_percentage)% (resets $($reset.ToString('yyyy-MM-dd HH:mm')))" } else { $parts += '5h window: not reported' }
        if ($null -ne $ctx) { $parts += "context used $ctx%" }
        $stale = ($age -gt 10) -or ($five -and [DateTimeOffset]::FromUnixTimeSeconds([long]$five.resets_at).LocalDateTime -lt $now)
        $usageLine = 'usage: ' + ($parts -join '; ') + $(if ($stale) { ' [STALE snapshot: do not trust; ask the user for /usage]' } else { '' })
    } catch { $usageLine = 'usage: snapshot unreadable' }
}

# ---- what this session edited / committed ------------------------------------------------------------------------------------
$edited = @()
if (Test-Path -LiteralPath $tr) {
    foreach ($hit in (Select-String -LiteralPath $tr -Pattern '"name":"(?:Edit|Write)"')) {
        try { $rec = $hit.Line | ConvertFrom-Json } catch { continue }
        if ($rec.PSObject.Properties['message'] -and $rec.message.content -is [array]) {
            foreach ($b in $rec.message.content) { if ($b.type -eq 'tool_use' -and ($b.name -eq 'Edit' -or $b.name -eq 'Write') -and $b.input.file_path) { $edited += [string]$b.input.file_path } }
        }
    }
}
$edited = @($edited | Sort-Object -Unique | ForEach-Object { if ($_.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) { $_.Substring($root.Length).TrimStart('\') } else { $_ } })
$start = if (Test-Path -LiteralPath $tr) { (Get-Item -LiteralPath $tr).CreationTime } else { $now.AddHours(-5) }
$dirtyHash = @{}
foreach ($s in @(git status --porcelain 2>$null)) { if ($s.Length -gt 3) { $dirtyHash[(($s.Substring(3) -split ' -> ')[-1].Trim('"')).Replace('/', '\').ToLower()] = $s.Substring(0, 2).Trim() } }
$uncommitted = @($edited | Where-Object { $dirtyHash.ContainsKey($_.ToLower()) })
$commits = @(git log --since=$($start.ToString('yyyy-MM-ddTHH:mm:ss')) --format='%h %s' 2>$null)
$contracts = @($edited | Where-Object { $_ -match '^\.(claude|codex)\\task-contract\.(.+)\.toml$' } | ForEach-Object { $Matches[2] })

New-Item -ItemType Directory -Force $dir | Out-Null
$sb = New-Object System.Text.StringBuilder
if (-not (Test-Path -LiteralPath $file)) {
    [void]$sb.AppendLine("# Deferred work: $TaskId")
    [void]$sb.AppendLine('')
    [void]$sb.AppendLine("Written in English by /epause. Resume with /eresume $TaskId. Append new sections; never rewrite earlier ones.")
    [void]$sb.AppendLine('')
}
[void]$sb.AppendLine("## Pause $($now.ToString('yyyy-MM-dd HH:mm')) - $Reason")
[void]$sb.AppendLine('')
[void]$sb.AppendLine('### Facts (filled by script)')
[void]$sb.AppendLine("- session: $SessionId (started $($start.ToString('yyyy-MM-dd HH:mm')))")
[void]$sb.AppendLine("- $usageLine")
[void]$sb.AppendLine("- contracts edited this session: $(if ($contracts.Count) { $contracts -join ', ' } else { 'none' })")
[void]$sb.AppendLine("- commits since session start (any session): $($commits.Count)")
foreach ($c in ($commits | Select-Object -First 6)) { [void]$sb.AppendLine("  - $c") }
[void]$sb.AppendLine("- files edited by this session: $($edited.Count); still uncommitted: $($uncommitted.Count)")
foreach ($p in ($uncommitted | Select-Object -First 30)) { [void]$sb.AppendLine("  - $p [$($dirtyHash[$p.ToLower()])]") }
if ($uncommitted.Count -gt 30) { [void]$sb.AppendLine("  - ... $($uncommitted.Count - 30) more (run /ecommit plan)") }
[void]$sb.AppendLine('')
foreach ($sec in @(
        @('Task and scope', 'goal, contract ID and plan revision, in_scope / out_of_scope in two or three lines'),
        @('Done (verified)', 'what is finished with its evidence: path:line, build/test log paths under artifacts/'),
        @('Remaining (in order)', 'numbered slices, each small enough to finish inside one 5-hour window; mark the first one to start'),
        @('Decisions already made', 'choices and rejected options with the reason, so they are not re-litigated or re-reviewed'),
        @('Do NOT redo', 'files already read or reviewed and found fine, checks already passed'),
        @('In-flight agents and background work', 'agent roles stopped or unfinished, their partial output paths, anything that must be re-run'),
        @('Next commands', 'exact commands to run first (copy-paste), with authorization state (build/test/commit allowed?)'),
        @('Risks and unverified', 'hardware/UI evidence owed, known failures, overlap with other sessions'),
        @('Acceptance still owed', 'items from the contract acceptance not yet proven'))) {
    [void]$sb.AppendLine("### $($sec[0])")
    [void]$sb.AppendLine("TODO(claude): $($sec[1])")
    [void]$sb.AppendLine('')
}
[IO.File]::AppendAllText($file, $sb.ToString(), (New-Object System.Text.UTF8Encoding($false)))
Write-Output "handoff written: tmp\$TaskId\deferred.md"
Write-Output $usageLine
Write-Output "uncommitted session files: $($uncommitted.Count); contracts edited: $(if ($contracts.Count) { $contracts -join ', ' } else { 'none' })"
Write-Output "Next: replace every TODO(claude) line with real content, then run with -Check."
