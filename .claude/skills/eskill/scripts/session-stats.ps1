param(
    [Parameter(Mandatory = $true)][string]$SessionId,
    [int]$Top = 8
)
# Objective cost/risk signals for one Claude session transcript. Read-only; writes only under tmp/eskill.
$ErrorActionPreference = 'Stop'
$root = Split-Path (Split-Path (Split-Path (Split-Path $PSScriptRoot)))
$proj = Join-Path (Join-Path $env:USERPROFILE '.claude\projects') ($root -replace '[:\\/ _.]', '-')
$file = Join-Path $proj "$SessionId.jsonl"
if (-not (Test-Path -LiteralPath $file)) { Write-Output "Transcript not found: $file"; exit 1 }

$toolName = @{}          # tool_use id -> name
$toolInput = @{}         # tool_use id -> short input description
$calls = @{}             # name -> count
$usage = @{}             # message id -> usage (keep last per message)
$results = New-Object System.Collections.Generic.List[object]
$errors = New-Object System.Collections.Generic.List[object]
$reads = @{}
$first = $null; $last = $null; $turns = 0

foreach ($line in [IO.File]::ReadLines($file)) {
    if ($line.Length -lt 20) { continue }
    try { $o = $line | ConvertFrom-Json } catch { continue }
    if ($o.timestamp) { $ts = [datetime]$o.timestamp; if (-not $first) { $first = $ts }; $last = $ts }
    $m = $o.message
    if (-not $m) { continue }
    if ($o.type -eq 'assistant') {
        if ($m.id -and $m.usage) { $usage[$m.id] = $m.usage }
        if ($m.content -is [array]) {
            foreach ($b in $m.content) {
                if ($b.type -eq 'tool_use') {
                    $toolName[$b.id] = $b.name
                    $calls[$b.name] = 1 + [int]$calls[$b.name]
                    $desc = ''
                    if ($b.input.file_path) { $desc = $b.input.file_path } elseif ($b.input.command) { $desc = $b.input.command } elseif ($b.input.pattern) { $desc = $b.input.pattern } elseif ($b.input.description) { $desc = $b.input.description }
                    $toolInput[$b.id] = [string]$desc
                    if ($b.name -eq 'Read' -and $b.input.file_path) { $reads[$b.input.file_path] = 1 + [int]$reads[$b.input.file_path] }
                }
            }
        }
    }
    elseif ($o.type -eq 'user' -and $m.content -is [array]) {
        $turns++
        foreach ($b in $m.content) {
            if ($b.type -ne 'tool_result') { continue }
            $txt = if ($b.content -is [string]) { $b.content } else { ($b.content | ConvertTo-Json -Depth 4 -Compress) }
            $name = $toolName[$b.tool_use_id]
            $results.Add([pscustomobject]@{ Tool = $name; Size = $txt.Length; Input = $toolInput[$b.tool_use_id]; Head = ($txt.Substring(0, [Math]::Min(90, $txt.Length)) -replace '\s+', ' ') })
            if ($b.is_error) { $errors.Add([pscustomobject]@{ Tool = $name; Input = $toolInput[$b.tool_use_id]; Head = ($txt.Substring(0, [Math]::Min(140, $txt.Length)) -replace '\s+', ' ') }) }
        }
    }
}

function Clip([string]$s, [int]$n) { $s = ($s -replace '\s+', ' ').Trim(); if ($s.Length -gt $n) { $s.Substring(0, $n - 1) + '~' } else { $s } }
$out = New-Object System.Collections.Generic.List[string]
$sumIn = 0; $sumOut = 0; $sumCC = 0; $sumCR = 0
foreach ($u in $usage.Values) { $sumIn += [long]$u.input_tokens; $sumOut += [long]$u.output_tokens; $sumCC += [long]$u.cache_creation_input_tokens; $sumCR += [long]$u.cache_read_input_tokens }
$mins = if ($first -and $last) { [Math]::Round(($last - $first).TotalMinutes, 0) } else { 0 }
$out.Add("== SESSION STATS $SessionId ==")
$out.Add("duration ~${mins} min | assistant messages: $($usage.Count) | tool results: $($results.Count) | errors: $($errors.Count)")
$out.Add("tokens (sum over requests): output=$sumOut, new-input=$sumIn, cache-write=$sumCC, cache-read=$sumCR")
$out.Add("  note: cache-read repeats the whole context every request, so long sessions and big early outputs cost most.")
$out.Add('-- tool calls: ' + (($calls.GetEnumerator() | Sort-Object Value -Descending | ForEach-Object { "$($_.Key)=$($_.Value)" }) -join ', '))
$out.Add("-- biggest tool results (chars) - each stays in context for every later request")
foreach ($r in ($results | Sort-Object Size -Descending | Select-Object -First $Top)) { $out.Add(("  {0,7}  {1,-10} {2} | {3}" -f $r.Size, $r.Tool, (Clip $r.Input 70), (Clip $r.Head 50))) }
$big = @($results | Where-Object { $_.Size -gt 8000 })
$out.Add("-- results over 8000 chars: $($big.Count), total $([long](($big | Measure-Object Size -Sum).Sum)) chars")
$out.Add("-- tool errors ($($errors.Count))")
foreach ($e in ($errors | Select-Object -First 10)) { $out.Add(("  {0,-10} {1} | {2}" -f $e.Tool, (Clip $e.Input 60), (Clip $e.Head 100))) }
$rep = @($reads.GetEnumerator() | Where-Object { $_.Value -ge 2 } | Sort-Object Value -Descending | Select-Object -First 8)
$out.Add("-- files Read 2+ times ($($rep.Count))")
foreach ($x in $rep) { $out.Add(("  {0}x {1}" -f $x.Value, $x.Key)) }
$dir = Join-Path $root 'tmp\eskill'
New-Item -ItemType Directory -Force $dir | Out-Null
$path = Join-Path $dir "$SessionId.stats.txt"
[IO.File]::WriteAllLines($path, $out)
$out | ForEach-Object { Write-Output $_ }
Write-Output "saved: tmp/eskill/$SessionId.stats.txt"
