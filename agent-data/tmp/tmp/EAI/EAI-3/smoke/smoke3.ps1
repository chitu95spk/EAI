# EAI-3 Step 2 UI smoke. Run: powershell -NoProfile -File tmp\EAI\EAI-3\smoke\smoke3.ps1 -Culture en
param([string]$Culture = 'en')
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing, System.Windows.Forms
$root = (Resolve-Path '.').Path
$out = Join-Path $root 'artifacts\EAI\EAI-3\smoke'
$fixture = Join-Path $root ('tmp\EAI\EAI-3\fixture-' + $Culture)
$exe = Join-Path $root 'EasyLabel\src\EasyAI.Wpf\bin\x64\Release\net9.0-windows\EasyAI.exe'
New-Item -ItemType Directory -Force $out | Out-Null
if (Test-Path $fixture) { Remove-Item $fixture -Recurse -Force }
Copy-Item (Join-Path $root 'tmp\EAI\EAI-0\fixture-project') $fixture -Recurse; Copy-Item (Join-Path $root 'tmp\EAI\EAI-3\fixture-src\*.png') $fixture
$env:TEMP = Join-Path $root 'tmp\EAI\EAI-3\temp'; $env:TMP = $env:TEMP
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
  Shot $p 's1-open'
  $radios = (ById 'Stepper').FindAll([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::RadioButton)))
  $radio = if ($radios.Count -ge 2) { $radios[1] } else { $null }
  Invoke-El $radio 'step Label' | Out-Null
  Start-Sleep -Milliseconds 1200
  Shot $p 's2-step2'
  # import model through the real file dialog
  Invoke-El (ById 'ImportModelButton') 'Import model' | Out-Null
  Start-Sleep -Seconds 2
  $desk = [System.Windows.Automation.AutomationElement]::RootElement
  $dlg = $null
  for ($i = 0; $i -lt 12 -and $dlg -eq $null; $i++) {
    $cn = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Window)
    foreach ($w in (Root).FindAll([System.Windows.Automation.TreeScope]::Descendants, $cn)) { if ($w.Current.NativeWindowHandle -ne [int]$hwnd -and $w.Current.NativeWindowHandle -ne 0) { $dlg = $w } }
    if ($dlg -eq $null) {
      $c = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $p.Id)
      foreach ($w in $desk.FindAll([System.Windows.Automation.TreeScope]::Children, $c)) { if ($w.Current.NativeWindowHandle -ne [int]$hwnd -and $w.Current.NativeWindowHandle -ne 0) { $dlg = $w } }
    }
    if ($dlg -eq $null) { Start-Sleep -Milliseconds 500 }
  }
  if ($dlg -eq $null) { Note 'import dialog NOT FOUND' } else {
    $dh = [IntPtr]$dlg.Current.NativeWindowHandle
    [W]::SetForegroundWindow($dh) | Out-Null; Start-Sleep -Milliseconds 600
    if ([W]::GetForegroundWindow() -eq $dh) {
      [System.Windows.Forms.SendKeys]::SendWait($model.Replace('{','{{}').Replace('}','{}}').Replace('+','{+}').Replace('^','{^}').Replace('%','{%}').Replace('~','{~}'))
      Start-Sleep -Milliseconds 500
      [System.Windows.Forms.SendKeys]::SendWait('{ENTER}')
      Note 'dialog: path typed and confirmed'
    } else { Note 'ENSURE FAILED: dialog not foreground, input skipped' }
  }
  Start-Sleep -Seconds 3
  Shot $p 's3-imported'
  Note ('active model text: ' + (Text-Of 'ActiveModelName'))
  # activate through the model combo
  $combo = ById 'ModelCombo'
  if ($combo -ne $null) {
    $ec = $combo.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern); $ec.Expand(); Start-Sleep -Milliseconds 600
    $item = $combo.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)))
    if ($item -ne $null) { $item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select(); Note 'model selected in combo' } else { Note 'combo has no items' }
    Start-Sleep -Seconds 3
    try { $ec.Collapse() } catch {}
  } else { Note 'ModelCombo MISSING' }
  Note ('active model text: ' + (Text-Of 'ActiveModelName'))
  Shot $p 's4-model-active'
  # auto-label the current image
  Invoke-El (ById 'AutoLabelCurrentButton') 'Auto-label current' | Out-Null
  $idle = Wait-Idle 180
  Note ("idle after run: $idle; suggestions: " + (Text-Of 'SuggestionCount') + "; threshold: " + (Text-Of 'ThresholdValue'))
  Shot $p 's5-suggestions'
  $slider = ById 'ThresholdSlider'
  if ($slider -ne $null) {
    $rv = $slider.GetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern)
    $rv.SetValue([Math]::Min($rv.Current.Maximum, $rv.Current.Minimum + ($rv.Current.Maximum - $rv.Current.Minimum) * 0.9)); Start-Sleep -Milliseconds 800
    Note ("high threshold -> suggestions: " + (Text-Of 'SuggestionCount')); Shot $p 's6-threshold-high'
    $rv.SetValue($rv.Current.Minimum); Start-Sleep -Milliseconds 800
    Note ("low threshold -> suggestions: " + (Text-Of 'SuggestionCount')); Shot $p 's7-threshold-low'
  } else { Note 'ThresholdSlider absent (model gave no scores or hidden)' }
  Invoke-El (ById 'AcceptAllButton') 'Accept all' | Out-Null
  Start-Sleep -Seconds 2
  Note ("after accept suggestions: " + (Text-Of 'SuggestionCount'))
  Shot $p 's8-accepted'
  $undo = $null
  foreach ($n in 'Undo','Undo (Ctrl+Z)') { if ($undo -eq $null) { $undo = ByName $n ([System.Windows.Automation.ControlType]::Button) } }
  if ($undo -ne $null) { Invoke-El $undo 'Undo' | Out-Null; Start-Sleep -Milliseconds 800; Shot $p 's9-undo' } else { Note 'Undo button not found by name' }
  Invoke-El (ById 'SplitVal') 'Split Val' | Out-Null
  Start-Sleep -Seconds 2
  Shot $p 's10-val'
  Invoke-El (ById 'AutoLabelAllButton') 'Auto-label all' | Out-Null
  Start-Sleep -Seconds 2
  Invoke-El (ById 'AutoLabelCancelButton') 'Cancel run' | Out-Null
  $idle = Wait-Idle 120
  Note ("idle after cancel: $idle")
  Shot $p 's11-cancelled'
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
