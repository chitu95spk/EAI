# Negative test: invalid --settings-dir (relative) and --project (missing) must not crash or write outside artifacts.
$root=(Resolve-Path '.').Path
$exe=Join-Path $root 'EasyLabel\src\EasyAI.Wpf\bin\Release\net9.0-windows\EasyAI.exe'
$env:EASYAI_SETTINGS_DIR=Join-Path $root 'artifacts\EAI\EAI-0\r1\smoke\settings-bad'
$env:TEMP=Join-Path $root 'tmp\EAI\EAI-0\temp'; $env:TMP=$env:TEMP
$p=Start-Process -FilePath $exe -ArgumentList '--settings-dir "relative\dir" --project "Z:\does\not\exist"' -PassThru
$d=(Get-Date).AddSeconds(20); do{Start-Sleep -Milliseconds 400;$p.Refresh()}while($p.MainWindowHandle -eq 0 -and -not $p.HasExited -and (Get-Date) -lt $d)
Start-Sleep -Seconds 3; $p.Refresh()
if($p.HasExited){"BAD exited code=$($p.ExitCode)"}else{"BAD window='$($p.MainWindowTitle)'"; $null=$p.CloseMainWindow(); if(-not $p.WaitForExit(6000)){$p.Kill();'BAD killed'}else{'BAD closed cleanly'}}
