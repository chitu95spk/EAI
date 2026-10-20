param([string]$Culture = 'vi')
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing, System.Windows.Forms, UIAutomationClient, UIAutomationTypes
$root = (Resolve-Path '.').Path
$out = Join-Path $root 'artifacts\EAI\EAI-AI\import-json'
$exe = Join-Path $root 'EasyLabel\src\EasyAI.Wpf\bin\x64\Release\net9.0-windows\EasyAI.exe'
$env:TEMP = Join-Path $root 'tmp\EAI\EAI-AI\tmpdir'; $env:TMP = $env:TEMP
$sd = Join-Path $env:TEMP "settings-ij-$Culture"
if (Test-Path $sd) { Remove-Item $sd -Recurse -Force }
New-Item -ItemType Directory -Force $sd | Out-Null
Add-Type -TypeDefinition @'
using System;using System.Runtime.InteropServices;
public struct RC2{public int L,T,R,B;}
public static class W2{
 [DllImport("user32.dll")]public static extern bool GetWindowRect(IntPtr h, out RC2 r);
 [DllImport("user32.dll")]public static extern bool PrintWindow(IntPtr h, IntPtr dc, uint f);
}
'@
function Shot($hwnd,$name){ $rc=New-Object RC2; [W2]::GetWindowRect($hwnd,[ref]$rc)|Out-Null; $w=$rc.R-$rc.L;$h=$rc.B-$rc.T
 $b=New-Object System.Drawing.Bitmap $w,$h; $g=[System.Drawing.Graphics]::FromImage($b); $dc=$g.GetHdc(); [W2]::PrintWindow($hwnd,$dc,2)|Out-Null; $g.ReleaseHdc($dc); $g.Dispose()
 $b.Save((Join-Path $out "$Culture-$name.png")); $b.Dispose() }
$p = Start-Process -FilePath $exe -ArgumentList ('--settings-dir "{0}" --culture {1}' -f $sd,$Culture) -PassThru
try {
  $d=(Get-Date).AddSeconds(25); do { Start-Sleep -Milliseconds 400; $p.Refresh() } while ($p.MainWindowHandle -eq 0 -and (Get-Date) -lt $d)
  $hwnd=$p.MainWindowHandle; Start-Sleep -Seconds 4
  $r=[System.Windows.Automation.AutomationElement]::FromHandle($hwnd)
  function Find($id){ $c=New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty,$id); $r.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$c) }
  $b=Find 'ToolbarImportJson'; $o=Find 'ToolbarOpenFolder'
  if ($b -eq $null) { Write-Host 'ToolbarImportJson MISSING' } else {
    Write-Host ("ImportJson enabled=" + $b.Current.IsEnabled + " name=" + $b.Current.Name + " x=" + [int]$b.Current.BoundingRectangle.X + " | OpenDir x=" + [int]$o.Current.BoundingRectangle.X) }
  Shot $hwnd 'no-project'
  if ($b -ne $null -and $b.Current.IsEnabled) {
    $b.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); Start-Sleep -Seconds 3
    # the folder picker is a separate top-level window owned by the app
    $dlg=[System.Windows.Automation.AutomationElement]::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children,(New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty,$p.Id)))
    $all=[System.Windows.Automation.AutomationElement]::FromHandle($hwnd).FindAll([System.Windows.Automation.TreeScope]::Descendants,(New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::Window)))
    foreach($w in $all){ Write-Host ("descendant window: '" + $w.Current.Name + "'") }
    foreach($w in $dlg){ Write-Host ("top-level window: '" + $w.Current.Name + "' class=" + $w.Current.ClassName) }
    $sh = New-Object -ComObject WScript.Shell; $sh.AppActivate($p.Id)|Out-Null; Start-Sleep -Milliseconds 300; [System.Windows.Forms.SendKeys]::SendWait('{ESC}'); Start-Sleep -Seconds 1
  }
  $p.Refresh(); Write-Host ("alive after cancel: " + (-not $p.HasExited))
} finally { if (-not $p.HasExited) { $p.CloseMainWindow()|Out-Null; if (-not $p.WaitForExit(15000)) { $p.Kill() } } }
