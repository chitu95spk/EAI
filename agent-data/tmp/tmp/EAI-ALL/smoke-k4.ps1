# EAI-PROJ P4 UI smoke. Run from the project root: powershell -NoProfile -File tmp\EAI-PROJ\smoke\smoke-p4.ps1 -Culture en
param([string]$Culture = 'en')
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing, System.Windows.Forms, UIAutomationClient, UIAutomationTypes
$root = (Resolve-Path '.').Path
$out = Join-Path $root 'artifacts\EAI-CAM\k4\smoke'
$work = Join-Path $root "tmp\EAI-CAM\smoke\work-$Culture"
$exe = Join-Path $root 'artifacts\EAI-CAM\k4\out\EasyAI.exe'
New-Item -ItemType Directory -Force $out | Out-Null
if (Test-Path $work) { Remove-Item $work -Recurse -Force }
New-Item -ItemType Directory -Force $work | Out-Null
$fixture = Join-Path $work 'legacy'
Copy-Item (Join-Path $root 'tmp\EAI\EAI-0\fixture-project') $fixture -Recurse
$locDir = Join-Path $work 'projects'; New-Item -ItemType Directory -Force $locDir | Out-Null
$env:TEMP = Join-Path $root 'tmp\EAI-PROJ\temp'; $env:TMP = $env:TEMP
$sd = Join-Path $work 'settings'; New-Item -ItemType Directory -Force $sd | Out-Null; '{"ImageSource":"cameraMvs"}' | Set-Content (Join-Path $sd 'settings.json') -Encoding ascii
$log = New-Object System.Collections.Generic.List[string]
function Note($m) { Write-Host $m; $script:log.Add($m) }
Add-Type -TypeDefinition @'
using System;using System.Runtime.InteropServices;
public struct RC{public int L,T,R,B;}
public static class W{
 [DllImport("user32.dll")]public static extern bool GetWindowRect(IntPtr h, out RC r);
 [DllImport("user32.dll")]public static extern bool PrintWindow(IntPtr h, IntPtr dc, uint f);
 [DllImport("user32.dll")]public static extern IntPtr GetForegroundWindow();
 [DllImport("user32.dll")]public static extern bool IsIconic(IntPtr h);
 [DllImport("user32.dll")]public static extern bool ShowWindow(IntPtr h,int c);
 [DllImport("user32.dll")]public static extern bool SetForegroundWindow(IntPtr h);
}
'@
Add-Type -TypeDefinition 'using System;using System.Runtime.InteropServices;public static class M{[DllImport("user32.dll")]public static extern bool SetCursorPos(int x,int y);[DllImport("user32.dll")]public static extern void mouse_event(uint f,int x,int y,uint d,UIntPtr e);public static void Click(int x,int y){SetCursorPos(x,y);System.Threading.Thread.Sleep(200);mouse_event(2,0,0,0,UIntPtr.Zero);mouse_event(4,0,0,0,UIntPtr.Zero);}}'
$AE = [System.Windows.Automation.AutomationElement]
function Find($scope, $id) {
  $c = New-Object System.Windows.Automation.PropertyCondition($AE::AutomationIdProperty, $id)
  $scope.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c)
}
function Wait-Find($scope, $id, $sec = 8) {
  $end = (Get-Date).AddSeconds($sec)
  do { $e = Find $scope $id; if ($e) { return $e }; Start-Sleep -Milliseconds 250 } while ((Get-Date) -lt $end)
  return $null
}
function OpenFileMenu($win) {
  $c = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::MenuItem)
  $first = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c)
  if (-not $first) { Note 'no MenuItem found'; return }
  Note ('first menu item: ' + $first.Current.Name + ' / ' + $first.Current.AutomationId)
  try { $first.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand() } catch { Invoke $first }
  Start-Sleep -Milliseconds 600
}
function Invoke($e) { $e.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
function SetText($e, $t) { $e.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($t) }
function Shot($hwnd, $name) {
  Start-Sleep -Milliseconds 700
  $rc = New-Object RC; [W]::GetWindowRect($hwnd, [ref]$rc) | Out-Null
  $w = $rc.R - $rc.L; $h = $rc.B - $rc.T
  $bmp = New-Object System.Drawing.Bitmap $w, $h
  $g = [System.Drawing.Graphics]::FromImage($bmp); $dc = $g.GetHdc()
  $ok = [W]::PrintWindow($hwnd, $dc, 2); $g.ReleaseHdc($dc); $g.Dispose()
  $bmp.Save((Join-Path $out "$Culture-$name.png")); $bmp.Dispose(); Note "shot $Culture-$name ${w}x${h} printwindow=$ok"
}
function DlgHwnd() {
  $e = Find $AE::RootElement 'ProjectDialogName'
  $w = [System.Windows.Automation.TreeWalker]::ControlViewWalker
  while ($e -and $e.Current.NativeWindowHandle -eq 0) { $e = $w.GetParent($e) }
  if ($e) { return [IntPtr]$e.Current.NativeWindowHandle } else { return [IntPtr]::Zero }
}
function ShotScreen($hwnd, $name) {
  # Popup menus are separate windows PrintWindow cannot see: crop the foreground app window from the screen.
  Start-Sleep -Milliseconds 700
  if ([W]::GetForegroundWindow() -ne $hwnd) { Note "SKIP screen shot $name (app not foreground)"; return }
  $rc = New-Object RC; [W]::GetWindowRect($hwnd, [ref]$rc) | Out-Null
  $bmp = New-Object System.Drawing.Bitmap ($rc.R-$rc.L), ($rc.B-$rc.T)
  $g = [System.Drawing.Graphics]::FromImage($bmp); $g.CopyFromScreen($rc.L, $rc.T, 0, 0, $bmp.Size); $g.Dispose()
  $bmp.Save((Join-Path $out "$Culture-$name.png")); $bmp.Dispose(); Note "screen shot $Culture-$name"
}
function ShotDlg($name) { $h = DlgHwnd; if ($h -ne [IntPtr]::Zero) { Shot $h $name } else { Note "no dialog hwnd for $name" } }
function Launch($extra) {
  $a = '--settings-dir "{0}" --culture {1} {2}' -f $sd, $Culture, $extra
  $p = Start-Process -FilePath $exe -ArgumentList $a -PassThru
  $end = (Get-Date).AddSeconds(30)
  do { Start-Sleep -Milliseconds 400; $p.Refresh() } while ($p.MainWindowHandle -eq 0 -and (Get-Date) -lt $end)
  if ($p.MainWindowHandle -eq 0) { throw 'main window did not appear' }
  Start-Sleep -Seconds 4
  [W]::ShowWindow($p.MainWindowHandle, 9) | Out-Null
  (New-Object -ComObject WScript.Shell).AppActivate($p.Id) | Out-Null; [W]::SetForegroundWindow($p.MainWindowHandle) | Out-Null
  Start-Sleep -Milliseconds 600
  return $p
}
function Keys($k) { [System.Windows.Forms.SendKeys]::SendWait($k); Start-Sleep -Milliseconds 500 }
$p = $null
try {
  $p = Launch ('--fake-camera --project "{0}"' -f $fixture)
  $win = $AE::FromHandle($p.MainWindowHandle)
  Note ('StepTabLive present=' + [bool](Find $win 'StepTabLive'))
  Keys '^+,'; Start-Sleep -Seconds 2
  $nc = New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, 'Camera')
  $item = $AE::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $nc)
  if ($item) { $r = $item.Current.BoundingRectangle; [M]::Click([int]($r.X + $r.Width/2), [int]($r.Y + $r.Height/2)) }
  Start-Sleep -Seconds 2
  $c = Wait-Find $AE::RootElement 'SettingsCameraConnect' 6
  Note ('connect present=' + [bool]$c + ' enabled=' + $(if ($c) { $c.Current.IsEnabled }))
  if ($c -and $c.Current.IsEnabled) { Invoke $c; Start-Sleep -Seconds 3 }
  $st = Find $AE::RootElement 'SettingsCameraStatus'; if ($st) { Note ('status: ' + $st.Current.Name + ' / ' + $st.Current.HelpText) }
  $cl = $AE::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::AutomationIdProperty, 'Close')))
  Keys '{ESC}'; Start-Sleep -Seconds 1
  [W]::SetForegroundWindow($p.MainWindowHandle) | Out-Null; Start-Sleep -Milliseconds 500
  $win = $AE::FromHandle($p.MainWindowHandle)
  $tab = Find $win 'StepTabLive'
  Note ('StepTabLive present=' + [bool]$tab)
  if ($tab) { try { $tab.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select() } catch { $r = $tab.Current.BoundingRectangle; [M]::Click([int]($r.X + $r.Width/2), [int]($r.Y + $r.Height/2)) } }
  Start-Sleep -Seconds 3
  foreach ($id in 'LiveCameraView','LiveCameraPreview','LiveCameraStart','LiveCameraStop','LiveCameraSnapshot','LiveCameraParams','LiveParam_ExposureTime','LiveParam_Gain','LiveParam_Width') { Note ("$id present=" + [bool](Find $win $id)) }
  Shot $p.MainWindowHandle 'live-tab'
  $p.Kill()
} catch { Note ('EXCEPTION: ' + $_.Exception.Message); if ($p -and -not $p.HasExited) { $p.Kill() } }
finally { $log | Set-Content (Join-Path $out "$Culture-smoke.log") }
