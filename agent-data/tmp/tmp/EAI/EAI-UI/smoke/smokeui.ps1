# EAI-UI layout smoke. Run: powershell -NoProfile -File tmp\EAI\EAI-UI\smoke\smokeui.ps1 -Culture en
param([string]$Culture = 'en')
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing, System.Windows.Forms
$root = (Resolve-Path '.').Path
$out = Join-Path $root 'artifacts\EAI\EAI-UI\smoke'
$fixture = Join-Path $root ('tmp\EAI\EAI-UI\fixture-' + $Culture)
$exe = Join-Path $root 'EasyLabel\src\EasyAI.Wpf\bin\x64\Release\net9.0-windows\EasyAI.exe'
New-Item -ItemType Directory -Force $out | Out-Null
if (Test-Path $fixture) { Remove-Item $fixture -Recurse -Force }
Copy-Item (Join-Path $root 'tmp\EAI\EAI-0\fixture-project') $fixture -Recurse; Copy-Item (Join-Path $root 'tmp\EAI\EAI-3\fixture-src\*.png') $fixture
$env:TEMP = Join-Path $root 'tmp\EAI\EAI-UI\temp'; $env:TMP = $env:TEMP
New-Item -ItemType Directory -Force $env:TEMP | Out-Null
$hwnd = [IntPtr]::Zero
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
 [DllImport("user32.dll")]public static extern bool SetCursorPos(int x,int y);
 [DllImport("user32.dll")]public static extern void mouse_event(uint f,int x,int y,uint d,UIntPtr e);
}
'@
function Ensure($p) {
  # Never send input unless our window is restored and in the foreground (else clicks hit other apps).
  for ($i = 0; $i -lt 5; $i++) {
    if ([W]::IsIconic($hwnd)) { [W]::ShowWindow($hwnd, 9) | Out-Null; Start-Sleep -Milliseconds 400 }
    (New-Object -ComObject WScript.Shell).AppActivate($p.Id) | Out-Null; [W]::SetForegroundWindow($hwnd) | Out-Null; Start-Sleep -Milliseconds 300
    if (-not [W]::IsIconic($hwnd) -and [W]::GetForegroundWindow() -eq $hwnd) { return $true }
  }
  Note 'ENSURE FAILED: window not foreground, input skipped'; return $false
}
function Keys($p, $k) { if (Ensure $p) { [System.Windows.Forms.SendKeys]::SendWait($k); Start-Sleep -Milliseconds 300 } }
function Shot($p, $name) {
  if ([W]::IsIconic($hwnd)) { Note "WINDOW MINIMIZED before shot $name (restoring)"; [W]::ShowWindow($hwnd, 9) | Out-Null; Start-Sleep -Milliseconds 500 }
  Start-Sleep -Milliseconds 600
  $rc = New-Object RC; [W]::GetWindowRect($hwnd, [ref]$rc) | Out-Null
  $w = $rc.R - $rc.L; $h = $rc.B - $rc.T
  $bmp = New-Object System.Drawing.Bitmap $w, $h
  $g = [System.Drawing.Graphics]::FromImage($bmp); $dc = $g.GetHdc()
  $ok = [W]::PrintWindow($hwnd, $dc, 2); $g.ReleaseHdc($dc); $g.Dispose()
  $bmp.Save((Join-Path $out ("$Culture-$name.png"))); $bmp.Dispose(); Note ("shot $Culture-$name ${w}x${h} printwindow=$ok")
}
function Drag($p, $fx1, $fy1, $fx2, $fy2) {
  if (-not (Ensure $p)) { return }
  $rc = New-Object RC; [W]::GetWindowRect($hwnd, [ref]$rc) | Out-Null
  $w = $rc.R - $rc.L; $h = $rc.B - $rc.T
  $x1 = $rc.L + [int]($w*$fx1); $y1 = $rc.T + [int]($h*$fy1); $x2 = $rc.L + [int]($w*$fx2); $y2 = $rc.T + [int]($h*$fy2)
  [W]::SetCursorPos($x1,$y1) | Out-Null; Start-Sleep -Milliseconds 150
  [W]::mouse_event(0x2,0,0,0,[UIntPtr]::Zero); Start-Sleep -Milliseconds 150   # left down
  [W]::SetCursorPos([int](($x1+$x2)/2),[int](($y1+$y2)/2)) | Out-Null; Start-Sleep -Milliseconds 150
  [W]::SetCursorPos($x2,$y2) | Out-Null; Start-Sleep -Milliseconds 150
  [W]::mouse_event(0x4,0,0,0,[UIntPtr]::Zero); Start-Sleep -Milliseconds 400   # left up
}
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$model = Join-Path $root 'artifacts\V133\native-checks\ng-only\model.segai'
function Root { [System.Windows.Automation.AutomationElement]::FromHandle($hwnd) }
function ById($id, $scope = $null) {
  if ($scope -eq $null) { $scope = Root }
  $c = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id)
  $scope.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c)
}
function ByName($name, $type) {
  $c1 = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $name)
  $c2 = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, $type)
  (Root).FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.AndCondition($c1, $c2)))
}
function Invoke-El($el, $what) {
  if ($el -eq $null) { Note "MISSING control: $what"; return $false }
  if (-not $el.Current.IsEnabled) { Note "DISABLED control: $what"; return $false }
  $pat = $null
  if ($el.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$pat)) { $pat.Invoke(); Note "invoked $what"; return $true }
  if ($el.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$pat)) { $pat.Select(); Note "selected $what"; return $true }
  Note "NO PATTERN: $what"; return $false
}
function Text-Of($id) { $e = ById $id; if ($e -eq $null) { return '<absent>' }; $e.Current.Name }
function Wait-Idle($sec) {
  $end = (Get-Date).AddSeconds($sec)
  do { Start-Sleep -Milliseconds 500; $cancel = ById 'AutoLabelCancelButton'; $busy = ($cancel -ne $null -and -not $cancel.Current.IsOffscreen) } while ($busy -and (Get-Date) -lt $end)
  return (-not $busy)
}

function Radio($index) {
  $radios = (ById 'Stepper').FindAll([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::RadioButton)))
  if ($radios.Count -gt $index) { return $radios[$index] } else { return $null }
}
function SelectImage($index) {
  $list = ById 'ImageList'
  $items = $list.FindAll([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)))
  if ($items.Count -gt $index) { $items[$index].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select(); Note "image $index selected" } else { Note "image $index NOT FOUND" }
  Start-Sleep -Milliseconds 800
}
function Wait-Label($sec) {
  $end = (Get-Date).AddSeconds($sec)
  do { Start-Sleep -Milliseconds 500; $cancel = ById 'AutoLabelCancelButton'; $busy = ($cancel -ne $null -and -not $cancel.Current.IsOffscreen) } while ($busy -and (Get-Date) -lt $end)
}
function Wait-Train($sec) {
  $end = (Get-Date).AddSeconds($sec)
  do { Start-Sleep -Milliseconds 700; $stop = ById 'StopTrainButton'; $busy = ($stop -ne $null -and $stop.Current.IsEnabled) } while ($busy -and (Get-Date) -lt $end)
  return (-not $busy)
}
function Set-Combo($id, $itemIndex) {
  $c = ById $id
  if ($c -eq $null) { Note "combo $id MISSING"; return }
  $ec = $c.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern); $ec.Expand(); Start-Sleep -Milliseconds 500
  $items = $c.FindAll([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)))
  if ($items.Count -gt $itemIndex) { $items[$itemIndex].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select(); Note "combo $id item $itemIndex (of $($items.Count)) selected" } else { Note "combo $id has only $($items.Count) items" }
  Start-Sleep -Milliseconds 400
  try { $ec.Collapse() } catch {}
}
function Set-Epochs($value) {
  $e = ById 'EpochsBox'
  if ($e -eq $null) { Note 'EpochsBox MISSING'; return }
  $pat = $null
  if ($e.TryGetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern, [ref]$pat)) { $pat.SetValue($value); Note "epochs set via RangeValue $value" }
  elseif ($e.TryGetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern, [ref]$pat)) { $pat.SetValue([string]$value); Note "epochs set via Value $value" }
  else { Note 'EpochsBox has no value pattern' }
  Start-Sleep -Milliseconds 400
}
$sd = Join-Path $env:TEMP "settings-$Culture"; if (Test-Path $sd) { Remove-Item $sd -Recurse -Force }
$a = '--settings-dir "{0}" --project "{1}" --culture {2}' -f (Join-Path $env:TEMP "settings-$Culture"), $fixture, $Culture
$p = Start-Process -FilePath $exe -ArgumentList $a -PassThru
try {
  $deadline = (Get-Date).AddSeconds(25)
  do { Start-Sleep -Milliseconds 400; $p.Refresh() } while ($p.MainWindowHandle -eq 0 -and (Get-Date) -lt $deadline)
  if ($p.MainWindowHandle -eq 0) { throw 'main window did not appear' }
  $hwnd = $p.MainWindowHandle
  Start-Sleep -Seconds 4
  [W]::ShowWindow($hwnd, 9) | Out-Null; Start-Sleep -Milliseconds 500
  (New-Object -ComObject WScript.Shell).AppActivate($p.Id) | Out-Null; [W]::SetForegroundWindow($hwnd) | Out-Null; Start-Sleep -Milliseconds 600
  Shot $p 'u1-open'
  function MenuTop($name) {
    $c1 = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::MenuItem)
    foreach ($m in (Root).FindAll([System.Windows.Automation.TreeScope]::Descendants, $c1)) { if ($m.Current.Name -eq $name) { return $m } }
    return $null
  }
  foreach ($n in 'File','Edit','View','Help','Tệp','Chỉnh sửa','Xem','Trợ giúp') {
    $m = MenuTop $n
    if ($m -ne $null) {
      $ec = $m.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern); $ec.Expand(); Start-Sleep -Milliseconds 700
      Shot $p ('u2-menu-' + $n)
      Note ("menu $n items: " + (@($m.FindAll([System.Windows.Automation.TreeScope]::Children, [System.Windows.Automation.Condition]::TrueCondition) | ForEach-Object { $_.Current.Name + $(if (-not $_.Current.IsEnabled) {'(off)'} else {''}) }) -join ' | '))
      $ec.Collapse(); Start-Sleep -Milliseconds 300
    }
  }
  # toggle panels from the View menu
  $view = if ($Culture -eq 'vi') { MenuTop 'Xem' } else { MenuTop 'View' }
  $view.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand(); Start-Sleep -Milliseconds 600
  $kids = $view.FindAll([System.Windows.Automation.TreeScope]::Children, [System.Windows.Automation.Condition]::TrueCondition)
  $i = 0
  foreach ($k in $kids) { $pat = $null; if ($k.TryGetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern, [ref]$pat) -and $i -lt 2) { $pat.Toggle(); $i++; Note ("toggled: " + $k.Current.Name); Start-Sleep -Milliseconds 700; break } }
  $view.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Collapse()
  Shot $p 'u3-panel-hidden'
  Invoke-El (ById 'ResetLayoutButton') 'Reset layout' | Out-Null
  Start-Sleep -Milliseconds 800
  Shot $p 'u4-after-reset'
  # About
  $help = if ($Culture -eq 'vi') { MenuTop 'Trợ giúp' } else { MenuTop 'Help' }
  $help.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand(); Start-Sleep -Milliseconds 600
  $first = $help.FindFirst([System.Windows.Automation.TreeScope]::Children, [System.Windows.Automation.Condition]::TrueCondition)
  if ($first -ne $null) { $pat = $null; if ($first.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$pat)) { $pat.Invoke(); Note 'About invoked' } }
  Start-Sleep -Seconds 2
  Shot $p 'u5-about'
  [System.Windows.Forms.SendKeys]::SendWait('{ESC}'); Start-Sleep -Milliseconds 800
  # narrow window (compact mode)
  $wp = (Root).GetCurrentPattern([System.Windows.Automation.TransformPattern]::Pattern)
  try { $wp.Resize(820, 700); Start-Sleep -Seconds 1; Shot $p 'u6-narrow'; Note 'resized 820x700' } catch { Note ('resize failed: ' + $_.Exception.Message) }
  Start-Sleep -Seconds 2   # let the save queue write
  $p.Refresh(); Note ("process alive: " + (-not $p.HasExited))
}
finally {
  if (-not $p.HasExited) { $p.CloseMainWindow() | Out-Null; if (-not $p.WaitForExit(20000)) { $p.Kill() } }
  Note ("exit code: " + $p.ExitCode)
}
Get-ChildItem $fixture -Recurse -Include *.json | ForEach-Object { Note ("json: " + $_.FullName.Substring($root.Length) + " " + $_.Length + " bytes") }
Get-ChildItem (Join-Path $fixture '.easyai') -Recurse -ErrorAction SilentlyContinue | ForEach-Object { Note ("easyai: " + $_.FullName.Substring($root.Length)) }
$log | Set-Content (Join-Path $out "$Culture-smoke.log")
