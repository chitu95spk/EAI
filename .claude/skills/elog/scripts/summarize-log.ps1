param(
    [Parameter(Mandatory = $true)][string]$Path,
    [string]$ErrorPattern = '(?i)\b(error|exception|fail(ed|ure)?|fatal|E-\d{3}|WorkCounter)\b',
    [string]$WarnPattern = '(?i)\bwarn(ing)?\b',
    [int]$Top = 15
)
# Summarize a large log without reading it whole. Output stays inside the project.
$ErrorActionPreference = 'Stop'
$root = Split-Path (Split-Path (Split-Path (Split-Path $PSScriptRoot)))
$full = (Resolve-Path -LiteralPath $Path).Path
$outDir = Join-Path $root 'tmp\log-summaries'
New-Item -ItemType Directory -Force $outDir | Out-Null
$out = Join-Path $outDir ([IO.Path]::GetFileName($full) + '.summary.txt')
$lines = [IO.File]::ReadAllLines($full)
$err = @($lines | Where-Object { $_ -match $ErrorPattern })
$warn = @($lines | Where-Object { $_ -match $WarnPattern })
# Normalize timestamps/hex/digits so repeated messages group together.
function Norm($s) {
    ($s -replace '\d{4}-\d\d-\d\d[ T][\d:.,]+', '' -replace '0x[0-9a-fA-F]+', '0x#' -replace '\d+', '#').Trim()
}
$groups = $err | Group-Object { Norm $_ } | Sort-Object Count -Descending | Select-Object -First $Top
$sb = New-Object Text.StringBuilder
[void]$sb.AppendLine("file: $full")
[void]$sb.AppendLine("size: $((Get-Item $full).Length) bytes, lines: $($lines.Count)")
[void]$sb.AppendLine("error-like lines: $($err.Count), warning-like lines: $($warn.Count)")
[void]$sb.AppendLine('--- first error-like line (line no.) ---')
for ($i = 0; $i -lt $lines.Count; $i++) {
    if ($lines[$i] -match $ErrorPattern) { [void]$sb.AppendLine(("{0}: {1}" -f ($i + 1), $lines[$i])); break }
}
[void]$sb.AppendLine("--- top $Top grouped error patterns (count x sample) ---")
foreach ($g in $groups) { [void]$sb.AppendLine(("{0} x {1}" -f $g.Count, $g.Group[0])) }
[void]$sb.AppendLine('--- last 10 lines ---')
$lines | Select-Object -Last 10 | ForEach-Object { [void]$sb.AppendLine($_) }
[IO.File]::WriteAllText($out, $sb.ToString())
Write-Output $sb.ToString()
Write-Output "summary saved: $out"
