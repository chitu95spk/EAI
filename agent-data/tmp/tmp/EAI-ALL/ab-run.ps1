# EAI-PROJ P4 UI smoke. Run from the project root: powershell -NoProfile -File tmp\EAI-PROJ\smoke\smoke-p4.ps1 -Culture en
param([string]$Culture = 'en')
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing, System.Windows.Forms, UIAutomationClient, UIAutomationTypes
$root = (Resolve-Path '.').Path
$out = Join-Path $root 'artifacts\EAI-AGENT\ab\smoke'
$work = Join-Path $root "tmp\EAI-AGENT\smoke\work-$Culture"
$exe = Join-Path $root 'artifacts\EAI-AGENT\w3\out\EasyAI.exe'
New-Item -ItemType Directory -Force $out | Out-Null
if (Test-Path $work) { Remove-Item $work -Recurse -Force }
New-Item -ItemType Directory -Force $work | Out-Null
$fixture = Join-Path $root 'tmp\EAI-ALL\ab\project'

$locDir = Join-Path $work 'projects'; New-Item -ItemType Directory -Force $locDir | Out-Null
$env:TEMP = Join-Path $root 'tmp\EAI-PROJ\temp'; $env:TMP = $env:TEMP
$sd = Join-Path $root 'tmp\EAI-ALL\ab\settings'
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
  $p = Launch ('--project "{0}"' -f $fixture)
  $win = $AE::FromHandle($p.MainWindowHandle)
  $b = Find $win 'AgentChatButton'
  Note ('AgentChatButton present=' + [bool]$b)
  Shot $p.MainWindowHandle 'a-main-with-button'
  if ($b) { Invoke $b; Start-Sleep -Seconds 2 }
  $w = Wait-Find $AE::RootElement 'AgentChatPrompt' 6
  Note ('chat prompt present=' + [bool]$w)
  if ($w) {
    foreach ($id in 'AgentChatRun','AgentChatCheck','AgentChatCancel','AgentChatMode_Current','AgentChatMode_All','AgentChatMode_Remaining','AgentChatEffort_0','AgentChatCaps','AgentChatList') { Note ("$id present=" + [bool](Find $AE::RootElement $id)) }
    $e = $w; $wk = [System.Windows.Automation.TreeWalker]::ControlViewWalker
    while ($e -and $e.Current.NativeWindowHandle -eq 0) { $e = $wk.GetParent($e) }
    if ($e) {
      $ch = [IntPtr]$e.Current.NativeWindowHandle
      $pb = Find $AE::RootElement 'AgentChatPrompt'
      $pb.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('Mark every blower housing part in the center of the image with an oriented_rectangle labelled OK. There should be five.')
      $all = Find $AE::RootElement 'AgentChatMode_All'
      try { $all.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select() } catch { try { $all.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle() } catch { $r = $all.Current.BoundingRectangle; [M]::Click([int]($r.X + $r.Width/2), [int]($r.Y + $r.Height/2)) } }
      Start-Sleep -Milliseconds 500
      $t0 = Get-Date
      $run = Find $AE::RootElement 'AgentChatRun'
      Note ('run enabled=' + $run.Current.IsEnabled)
      Invoke $run
      $seen = $false; $deadline = (Get-Date).AddMinutes(9)
      while ((Get-Date) -lt $deadline) {
        Start-Sleep -Seconds 3
        $c = Find $AE::RootElement 'AgentChatCancel'
        if ($c -and -not $seen) { $seen = $true; Note ('run started after ' + [int]((Get-Date) - $t0).TotalSeconds + ' s') }
        if ($seen -and -not $c) { break }
      }
      Note ('batch wall time s=' + [int]((Get-Date) - $t0).TotalSeconds + ' finished=' + $seen)
      Shot $ch 'z-chat-final'
      Shot $p.MainWindowHandle 'z-main-final'
    }
    Note ('main bar old AgentPromptBox present=' + [bool](Find $win 'AgentPromptBox'))
  }
  $p.Kill()
} catch { Note ('EXCEPTION: ' + $_.Exception.Message); if ($p -and -not $p.HasExited) { $p.Kill() } }
finally { $log | Set-Content (Join-Path $out "$Culture-smoke.log") }
