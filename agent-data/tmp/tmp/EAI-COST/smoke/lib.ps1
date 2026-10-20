# EAI-COST UI smoke helpers (adapted from tmp/EAI/EAI-3/smoke/smoke3.ps1). Dot-sourced from smoke.ps1.
Add-Type -AssemblyName System.Drawing, System.Windows.Forms, UIAutomationClient, UIAutomationTypes
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
 [DllImport("user32.dll")]public static extern uint GetDpiForWindow(IntPtr h);
}
'@
$AE = [System.Windows.Automation.AutomationElement]
$TS = [System.Windows.Automation.TreeScope]
function Note($m) { Write-Host $m; $script:log.Add($m) }
function TopWindows {
  $c = New-Object System.Windows.Automation.PropertyCondition($AE::ProcessIdProperty, $script:p.Id)
  $AE::RootElement.FindAll($TS::Children, $c)
}
function ById0($id, $scope = $null) {
  $cond = New-Object System.Windows.Automation.PropertyCondition($AE::AutomationIdProperty, $id)
  if ($scope -ne $null) { return $scope.FindFirst($TS::Descendants, $cond) }
  foreach ($w in (TopWindows)) {
    try {
      if ($w.Current.AutomationId -eq $id) { return $w }
      $e = $w.FindFirst($TS::Descendants, $cond); if ($e -ne $null) { return $e }
    } catch { }
  }
  return $null
}
function ById($id, $scope = $null) {
  for ($i = 0; $i -lt 6; $i++) {
    try { $e = ById0 $id $scope; if ($e -ne $null) { return $e } } catch { }
    Start-Sleep -Milliseconds 500
  }
  return $null
}
function Win($id) { ById $id }
function Texts($scope) {
  $c = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text)
  ($scope.FindAll($TS::Descendants, $c) | ForEach-Object { $_.Current.Name }) -join ' | '
}
function Shot($el, $name) {
  Start-Sleep -Milliseconds 700
  $h = [IntPtr]$el.Current.NativeWindowHandle
  if ($h -eq [IntPtr]::Zero) { Note "SHOT $name : no hwnd"; return }
  $rc = New-Object RC; [W]::GetWindowRect($h, [ref]$rc) | Out-Null
  $w = $rc.R - $rc.L; $hh = $rc.B - $rc.T
  $bmp = New-Object System.Drawing.Bitmap $w, $hh
  $g = [System.Drawing.Graphics]::FromImage($bmp); $dc = $g.GetHdc()
  $ok = [W]::PrintWindow($h, $dc, 2); $g.ReleaseHdc($dc); $g.Dispose()
  $f = Join-Path $script:out ("$($script:Culture)-$name.png")
  $bmp.Save($f); $bmp.Dispose()
  & powershell -NoProfile -File (Join-Path $script:root 'tools\ev-thumb.ps1') -Path $f | Out-Null
  Note ("shot $($script:Culture)-$name ${w}x${hh} dpi=$([W]::GetDpiForWindow($h)) printwindow=$ok")
}
function Invoke-El($el, $what) {
  if ($el -eq $null) { Note "MISSING: $what"; return $false }
  if (-not $el.Current.IsEnabled) { Note "DISABLED: $what"; return $false }
  $pat = $null
  if ($el.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$pat)) { $pat.Invoke(); Note "invoked $what"; return $true }
  if ($el.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$pat)) { $pat.Select(); Note "selected $what"; return $true }
  Note "NO PATTERN: $what"; return $false
}
function ToggleState($el) { $pat = $el.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern); $pat.Current.ToggleState.ToString() }
function Fore($hwnd) {
  for ($i = 0; $i -lt 5; $i++) {
    if ([W]::IsIconic($hwnd)) { [W]::ShowWindow($hwnd, 9) | Out-Null; Start-Sleep -Milliseconds 400 }
    [W]::SetForegroundWindow($hwnd) | Out-Null; Start-Sleep -Milliseconds 300
    if ([W]::GetForegroundWindow() -eq $hwnd) { return $true }
  }
  Note 'ENSURE FAILED: window not foreground, input skipped'; return $false
}
function AppDescendants {
  $all = Get-CimInstance Win32_Process | Select-Object ProcessId, ParentProcessId, Name
  $set = @{}; $set[[int]$script:p.Id] = $true; $changed = $true
  while ($changed) { $changed = $false; foreach ($x in $all) { if ($set.ContainsKey([int]$x.ParentProcessId) -and -not $set.ContainsKey([int]$x.ProcessId)) { $set[[int]$x.ProcessId] = $true; $changed = $true } } }
  ($all | Where-Object { $set.ContainsKey([int]$_.ProcessId) -and $_.ProcessId -ne $script:p.Id } | ForEach-Object { "$($_.Name)#$($_.ProcessId)" }) -join ','
}
function AgentPids { (Get-Process claude, codex -ErrorAction SilentlyContinue | ForEach-Object { $_.Id } | Sort-Object) -join ',' }
function Open-Settings-Agents {
  $main = $AE::FromHandle($script:hwnd)
  $mc = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::MenuItem)
  for ($try = 0; $try -lt 4 -and (Win 'SettingsWindow') -eq $null; $try++) {
    $file = $null; $ms = $null
    foreach ($m in $main.FindAll($TS::Descendants, $mc)) { if ($file -eq $null) { $file = $m } }
    if ($file -ne $null) {
      $ec = $file.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
      if ($ec.Current.ExpandCollapseState.ToString() -ne 'Expanded') { $ec.Expand() }
      for ($k = 0; $k -lt 10; $k++) { Start-Sleep -Milliseconds 500; $ms = ById 'MenuSettings'; if ($ms -ne $null) { break } }
      if ($ms -ne $null) { $ms.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); Note "MenuSettings invoked (try $try)"; for ($k = 0; $k -lt 12 -and (Win 'SettingsWindow') -eq $null; $k++) { Start-Sleep -Milliseconds 500 } }
      else { Note "MenuSettings not found (try $try)"; try { $ec.Collapse() } catch {} }
    }
    if ((Win 'SettingsWindow') -eq $null -and $try -eq 2 -and (Fore $script:hwnd)) { [System.Windows.Forms.SendKeys]::SendWait('^+,'); Start-Sleep -Milliseconds 2000 }
  }
  $sw = Win 'SettingsWindow'
  if ($sw -eq $null) { Note 'SettingsWindow did not open via Ctrl+Shift+,'; return $null }
  $list = ById 'SettingsPages' $sw
  $c = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)
  $target = $null
  foreach ($it in $list.FindAll($TS::Descendants, $c)) { if (($it.Current.Name + ' ' + (Texts $it)) -like '*Codex*') { $target = $it } }
  if ($target -eq $null) { Note 'Agents page item not found'; return $null }
  $target.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select(); Start-Sleep -Milliseconds 2500
  $sw = Win 'SettingsWindow'
  Note ('settings window hwnd=' + $sw.Current.NativeWindowHandle + ' toggle found=' + ((ById0 'AgentOfflineOnly' $sw) -ne $null))
  return $sw
}
function Close-Window($el) {
  if ($el -eq $null) { return }
  $pat = $null
  if ($el.TryGetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern, [ref]$pat)) { $pat.Close() }
  Start-Sleep -Milliseconds 700
}
function Start-App($settingsDir, $fixture, $culture) {
  $a = '--settings-dir "{0}" --project "{1}" --culture {2} --fake-agent "{3}"' -f $settingsDir, $fixture, $culture, (Join-Path $script:root 'tmp\EAI-COST\smoke\fake-agent.ps1')
  Note "launch: $a"
  $script:p = Start-Process -FilePath $script:exe -ArgumentList $a -PassThru
  $deadline = (Get-Date).AddSeconds(30)
  do { Start-Sleep -Milliseconds 400; $script:p.Refresh() } while ($script:p.MainWindowHandle -eq 0 -and (Get-Date) -lt $deadline)
  if ($script:p.MainWindowHandle -eq 0) { throw 'main window did not appear' }
  Start-Sleep -Seconds 4
  for ($i = 0; $i -lt 12; $i++) {
    $script:p.Refresh(); $h = $script:p.MainWindowHandle; if ($h -eq 0) { Note "start-app: no main window yet (try $i, exited=$($script:p.HasExited))"; Start-Sleep -Seconds 2; continue }; $script:hwnd = [IntPtr]$h
    try { [W]::ShowWindow($script:hwnd, 9) | Out-Null; Start-Sleep -Milliseconds 500; $main = $AE::FromHandle($script:hwnd); Note ('main window title: ' + $main.Current.Name); break } catch { Note ("start-app retry " + $i + " hwnd=" + $script:hwnd + " : " + $_.Exception.Message); Start-Sleep -Seconds 1 }
  }
}
function Stop-App {
  if ($script:p -ne $null -and -not $script:p.HasExited) {
    $script:p.CloseMainWindow() | Out-Null
    if (-not $script:p.WaitForExit(6000)) {
      foreach ($w in (TopWindows)) { Note ('  open window at exit: ' + $w.Current.AutomationId + ' / ' + $w.Current.Name + ' :: ' + (Texts $w)) }
      $d = Win 'PromptWindow'
      if ($d -ne $null) { foreach ($b in 'PromptCancel') { $e = ById $b $d; if ($e -ne $null -and $e.Current.IsEnabled) { Note ('  exit prompt: clicking ' + $e.Current.Name); Invoke-El $e 'exit prompt button' | Out-Null; break } } }
      if (-not $script:p.WaitForExit(10000)) { $script:p.Kill(); Note 'app had to be killed' }
    }
  }
  Note ("app exit code: " + $script:p.ExitCode)
}
function Manifest-Consent($fixture) {
  $j = Get-Content $fixture -Raw
  if ($j -match 'agentConsent') { ($j -replace '\s+', ' ') } else { '<no AgentConsent in manifest>' }
}

function Scroll-End($sw) {
  $sc = ById 'SettingsAgentsScroll' $sw
  if ($sc -eq $null) { Note 'SettingsAgentsScroll not found'; return }
  $pat = $null
  if ($sc.TryGetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern, [ref]$pat)) { $pat.SetScrollPercent(-1, 100); Start-Sleep -Milliseconds 800; Note ('scrolled to end, vpercent=' + $pat.Current.VerticalScrollPercent) } else { Note 'no ScrollPattern' }
}
