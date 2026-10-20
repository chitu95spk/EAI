$ErrorActionPreference = 'Stop'
$root = (Get-Location).Path
$scratch = Join-Path $root 'tmp/EAI-COST'
$baseline = Join-Path $scratch 'baseline'
$utf8 = New-Object System.Text.UTF8Encoding($false, $true)
$newFiles = @('EasyLabel/src/EasyAI.Wpf/Services/Agent/AgentCliCapabilities.cs', 'EasyLabel/tests/EasyLabel.SegAI.Tests/AgentOfflineAndCapabilityTests.cs')
$files = @((Get-ChildItem $baseline -File -Recurse | ForEach-Object { $_.FullName.Substring($baseline.Length+1).Replace('\','/') })) + $newFiles
$report = [System.Collections.Generic.List[string]]::new()
$failures = [System.Collections.Generic.List[string]]::new()
$diffDirectory = Join-Path $scratch 'diffs'
[IO.Directory]::CreateDirectory($diffDirectory) | Out-Null
foreach ($file in $files) {
    $current = Join-Path $root $file
    $lines = [IO.File]::ReadAllLines($current, $utf8)
    $bytes = [IO.File]::ReadAllBytes($current)
    $text = $utf8.GetString($bytes)
    $added = [System.Collections.Generic.List[object]]::new()
    $ranges = [System.Collections.Generic.List[string]]::new()
    $previous = Join-Path $baseline $file
    if (Test-Path -LiteralPath $previous) {
        $beforeBytes = [IO.File]::ReadAllBytes($previous)
        $beforeText = $utf8.GetString($beforeBytes)
        if ($beforeText.Contains("`r`n") -and [regex]::IsMatch($text, '(?<!\r)\n') -and ![regex]::IsMatch($beforeText, '(?<!\r)\n')) { $failures.Add("New LF endings in $file") }
        if (!$beforeText.Contains("`r`n") -and $text.Contains("`r`n")) { $failures.Add("New CRLF endings in $file") }
        if (($beforeBytes[0..2] -join ',') -eq '239,187,191' -and ($bytes[0..2] -join ',') -ne '239,187,191') { $failures.Add("BOM lost in $file") }
        $oldLines = [IO.File]::ReadAllLines($previous, $utf8)
        $oldAt=0; $newAt=0
        $hunks = [System.Collections.Generic.List[string]]::new()
        while ($oldAt -lt $oldLines.Count -or $newAt -lt $lines.Count) {
            if ($oldAt -lt $oldLines.Count -and $newAt -lt $lines.Count -and $oldLines[$oldAt] -ceq $lines[$newAt]) { $oldAt++; $newAt++; continue }
            $bestOld=$oldLines.Count; $bestNew=$lines.Count; $bestCost=[int]::MaxValue
            $maxOld=[Math]::Min($oldLines.Count-1, $oldAt+160)
            $maxNew=[Math]::Min($lines.Count-1, $newAt+200)
            for ($oi=$oldAt; $oi -le $maxOld; $oi++) {
                for ($ni=$newAt; $ni -le $maxNew; $ni++) {
                    $cost=$oi-$oldAt+$ni-$newAt
                    if ($cost -ge $bestCost -or $oldLines[$oi] -cne $lines[$ni]) { continue }
                    $match=$true
                    for ($look=1; $look -lt 3; $look++) {
                        if ($oi+$look -ge $oldLines.Count -or $ni+$look -ge $lines.Count) { break }
                        if ($oldLines[$oi+$look] -cne $lines[$ni+$look]) { $match=$false; break }
                    }
                    if ($match) { $bestOld=$oi; $bestNew=$ni; $bestCost=$cost }
                }
            }
            $hunks.Add("@@ old $($oldAt+1)-$bestOld new $($newAt+1)-$bestNew @@")
            for ($i=$oldAt; $i -lt $bestOld; $i++) { $hunks.Add('-'+$oldLines[$i]) }
            for ($i=$newAt; $i -lt $bestNew; $i++) { $hunks.Add('+'+$lines[$i]); $added.Add(@{ Number=$i+1; Text=$lines[$i] }) }
            if ($newAt -lt $bestNew) { $ranges.Add("$($newAt+1)-$bestNew") } else { $ranges.Add("$($newAt+1) (deletion)") }
            $oldAt=$bestOld; $newAt=$bestNew
        }
        [IO.File]::WriteAllLines((Join-Path $diffDirectory (($file.Replace('/','_'))+'.txt')), $hunks, $utf8)
    } else {
        $ranges.Add("1-$($lines.Count) (new)")
        for ($i=0; $i -lt $lines.Count; $i++) { $added.Add(@{ Number=$i+1; Text=$lines[$i] }) }
    }
    foreach ($line in $added) {
        if ($line.Text -match '[ \t]+$') { $failures.Add("Trailing whitespace ${file}:$($line.Number)") }
        if ($line.Text -match '^(<<<<<<<|=======|>>>>>>>)') { $failures.Add("Conflict marker ${file}:$($line.Number)") }
    }
    if ($file.EndsWith('.cs')) {
        $tokens = [regex]::Replace($text, '(?s)/\*.*?\*/|//[^\r\n]*|""".*?"""|@"(?:[^"]|"")*"|"(?:\\.|[^"\\])*"|''(?:\\.|[^''\\])*''', '')
        $stack = [System.Collections.Generic.Stack[char]]::new()
        $pairs = @{')'='(';']'='[';'}'='{'}
        foreach ($character in $tokens.ToCharArray()) {
            if ('([{'.Contains($character)) { $stack.Push($character) }
            elseif (')]}'.Contains($character)) {
                if ($stack.Count -eq 0 -or $stack.Pop() -ne $pairs[[string]$character]) { $failures.Add("Delimiter mismatch in $file"); break }
            }
        }
        if ($stack.Count -ne 0) { $failures.Add("Unclosed delimiters in $file") }
    }
    if ($file.EndsWith('.resx') -or $file.EndsWith('.xaml') -or $file.EndsWith('.csproj')) {
        try { [xml]$xml = $text } catch { $failures.Add("XML invalid in $file : $_") }
    }
    $report.Add($file + ': ' + ($ranges -join ', '))
}
[xml]$english = [IO.File]::ReadAllText((Join-Path $root 'EasyLabel/src/EasyAI.Wpf/Resources/Strings.resx'), $utf8)
[xml]$vietnamese = [IO.File]::ReadAllText((Join-Path $root 'EasyLabel/src/EasyAI.Wpf/Resources/Strings.vi.resx'), $utf8)
$keyDifferences = @(Compare-Object @($english.root.data.name | Sort-Object) @($vietnamese.root.data.name | Sort-Object))
if ($keyDifferences.Count -gt 0) { $failures.Add('Resource key sets differ') }
$report.Add("Resource keys EN=$($english.root.data.Count), VI=$($vietnamese.root.data.Count), differences=$($keyDifferences.Count)")
$report.Add('C# assumptions: net9.0, LangVersion12.0, nullable enabled; no compiler invoked.')
$report.Add('Static checks: ' + $(if ($failures.Count -eq 0) { 'PASS' } else { 'FAIL' }))
$report.AddRange($failures)
[IO.File]::WriteAllLines((Join-Path $scratch 'static-audit.txt'), $report, $utf8)
$report
if ($failures.Count -gt 0) { exit 1 }
