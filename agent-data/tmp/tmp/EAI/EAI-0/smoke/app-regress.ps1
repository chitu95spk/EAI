# Regression: legacy WinForms EasyLabel.App must still start on net9, show a window, and close.
$root=(Resolve-Path '.').Path
$exe=Join-Path $root 'EasyLabel\src\EasyLabel.App\bin\Release\net9.0-windows\EasyLabel.App.exe'
$env:TEMP=Join-Path $root 'tmp\EAI\EAI-0\temp'; $env:TMP=$env:TEMP
$p=Start-Process -FilePath $exe -PassThru
$d=(Get-Date).AddSeconds(20); do{Start-Sleep -Milliseconds 400;$p.Refresh()}while($p.MainWindowHandle -eq 0 -and -not $p.HasExited -and (Get-Date) -lt $d)
Start-Sleep -Seconds 3; $p.Refresh()
if($p.HasExited){"RESULT exited early code=$($p.ExitCode)"}else{"RESULT window='$($p.MainWindowTitle)' handle=$($p.MainWindowHandle)"; $null=$p.CloseMainWindow(); if(-not $p.WaitForExit(6000)){$p.Kill();'RESULT killed'}else{'RESULT closed cleanly'}}
