# Scratch batch runner for EAI-OPEN smokes. Runs existing smoke scripts sequentially, 6-minute limit each.
$ErrorActionPreference = 'Continue'
$Root = 'E:\Code\EasyVision_Unisen - NoService'
Set-Location $Root
$run2 = Join-Path $Root 'artifacts\EAI\EAI-OPEN\run2'
New-Item -ItemType Directory -Force -Path $run2 | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $run2 'smokeg') | Out-Null
$log = Join-Path $run2 'runner.log'
$tsv = Join-Path $run2 'runs.tsv'
function Log($m) { Add-Content -Path $log -Value ((Get-Date -Format 'HH:mm:ss') + ' ' + $m) -Encoding ascii }
function KillEasyAI($why) {
  $ps = @(Get-Process -Name 'EasyAI*' -ErrorAction SilentlyContinue)
  foreach ($x in $ps) { Log ("  stop EasyAI pid=" + $x.Id + " (" + $why + ")"); Stop-Process -Id $x.Id -Force -ErrorAction SilentlyContinue }
}
$exe = Join-Path $Root 'EasyLabel\src\EasyAI.Wpf\bin\x64\Release\net9.0-windows\EasyAI.exe'
Log ("exe exists: " + (Test-Path $exe) + " TEMP=" + $env:TEMP)
KillEasyAI 'left from before batch'
$entries = @(
  @{tag='EAI-2_smoke';   file='tmp\EAI\EAI-2\smoke\smoke.ps1';        scan=@('artifacts\EAI\EAI-2\smoke-a');  cult=$true},
  @{tag='EAI-2_smoke2';  file='tmp\EAI\EAI-2\smoke\smoke2.ps1';       scan=@('artifacts\EAI\EAI-2\smoke-b');  cult=$true},
  @{tag='EAI-5_smoke5';  file='tmp\EAI\EAI-5\smoke5.ps1';             scan=@('artifacts\EAI\EAI-5\smoke');    cult=$true},
  @{tag='EAI-DATA_smokex'; file='tmp\EAI\EAI-DATA\smokex.ps1';        scan=@('artifacts\EAI\EAI-UI\smoke');   cult=$true},
  @{tag='EAI-UI_smokeh'; file='tmp\EAI\EAI-UI\smoke\smokeh.ps1';      scan=@('artifacts\EAI\EAI-UI\smoke');   cult=$true},
  @{tag='EAI-UI_panels'; file='tmp\EAI\EAI-UI\smoke\smokepanels.ps1'; scan=@('artifacts\EAI\EAI-UI\smoke');   cult=$true},
  @{tag='EAI-UI_ui2';    file='tmp\EAI\EAI-UI\smoke\smokeui2.ps1';    scan=@('artifacts\EAI\EAI-UI\smoke');   cult=$true},
  @{tag='EAI-UI_smokef'; file='tmp\EAI\EAI-UI\smoke\smokef.ps1';      scan=@('artifacts\EAI\EAI-UI\smoke');   cult=$true},
  @{tag='EAI-UI_smokeg'; file='tmp\EAI\EAI-UI\smoke\smokeg.ps1';      scan=@('artifacts\EAI\EAI-OPEN\run2\smokeg'); cult=$false},
  @{tag='EAI-PLUS_smoke'; file='tmp\EAI-PLUS\smoke\smoke.ps1';        scan=@('artifacts\EAI-PLUS\smoke'); cult=$true; plus=$true}
)
$runs = @()
foreach ($e in $entries) {
  if ($e.cult) { $runs += @{e=$e; c='en'}; $runs += @{e=$e; c='vi'} } else { $runs += @{e=$e; c='none'} }
}
Add-Content -Path $tsv -Value "tag`tculture`tstatus`texit`tseconds`tcopied" -Encoding ascii
foreach ($r in $runs) {
  $e = $r.e; $c = $r.c
  $start = Get-Date
  if ($e.cult) {
    if ($e.plus) { $argStr = '-NoProfile -File ' + $e.file + ' -Run open-' + $c + ' -Culture ' + $c }
    else { $argStr = '-NoProfile -File ' + $e.file + ' -Culture ' + $c }
  } else { $argStr = '-NoProfile -File ' + $e.file + ' -Out artifacts\EAI\EAI-OPEN\run2\smokeg' }
  KillEasyAI 'before run'
  Log ("START " + $e.tag + " " + $c + " :: " + $argStr)
  $p = Start-Process -FilePath 'powershell.exe' -ArgumentList $argStr -WorkingDirectory $Root -PassThru -WindowStyle Normal
  $done = $p.WaitForExit(360000)
  if ($done) { $status = 'DONE'; $code = $p.ExitCode }
  else {
    $status = 'TIMEOUT'; $code = 'n/a'
    Log ("  TIMEOUT after 360s, killing child " + $p.Id)
    & taskkill /T /F /PID $p.Id 2>$null | Out-Null
    KillEasyAI 'timeout'
  }
  $sec = [int]((Get-Date) - $start).TotalSeconds
  $dest = Join-Path $run2 ($e.tag + '-' + $c)
  $n = 0
  foreach ($d in $e.scan) {
    $full = Join-Path $Root $d
    if (Test-Path $full) {
      Get-ChildItem -Path $full -Recurse -File | Where-Object { $_.LastWriteTime -ge $start } | ForEach-Object {
        $rel = $_.FullName.Substring($full.Length).TrimStart('\')
        $to = Join-Path (Join-Path $dest (Split-Path $d -Leaf)) $rel
        New-Item -ItemType Directory -Force -Path (Split-Path $to) | Out-Null
        Copy-Item -Path $_.FullName -Destination $to -Force
        $n++
      }
    }
  }
  Log ("END " + $e.tag + " " + $c + " status=" + $status + " exit=" + $code + " sec=" + $sec + " copied=" + $n)
  Add-Content -Path $tsv -Value ($e.tag + "`t" + $c + "`t" + $status + "`t" + $code + "`t" + $sec + "`t" + $n) -Encoding ascii
}
KillEasyAI 'batch end'
Log 'BATCH COMPLETE'
