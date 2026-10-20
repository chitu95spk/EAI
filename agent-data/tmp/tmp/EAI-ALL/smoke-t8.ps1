# T8 Easy-mode UI smoke. Run from project root: powershell -NoProfile -File tmp\EAI-ALL\smoke-t8.ps1 -Culture en -Width 880 -Height 560
param([string]$Culture = 'en', [int]$Width = 880, [int]$Height = 560)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing, System.Windows.Forms, UIAutomationClient, UIAutomationTypes
$root = (Resolve-Path '.').Path
$tag = "$Culture-${Width}x$Height"
$out = Join-Path $root 'artifacts\EAI-AGENT\t8\smoke'
$exe = Join-Path $root 'artifacts\EAI-AGENT\t8\out\EasyAI.exe'
New-Item -ItemType Directory -Force $out | Out-Null
$fixture = Join-Path $root 'tmp\EAI-ALL\ab\project'
$env:TEMP = Join-Path $root 'tmp\EAI-PROJ\temp'; $env:TMP = $env:TEMP
$base = Join-Path $root "tmp\EAI-AGENT\t8-settings\$tag"
if (Test-Path $base) { Remove-Item $base -Recurse -Force }
$log = New-Object System.Collections.Generic.List[string]
function Note($m) { Write-Host $m; $script:log.Add($m) }
function Check($name, $ok) { Note ("{0}: {1}" -f $(if ($ok) { 'PASS' } else { 'FAIL' }), $name) }
Add-Type -TypeDefinition @'
using System;using System.Runtime.InteropServices;
public struct RC{public int L,T,R,B;}
public static class W{
 [DllImport("user32.dll")]public static extern bool GetWindowRect(IntPtr h, out RC r);
 [DllImport("user32.dll")]public static extern bool PrintWindow(IntPtr h, IntPtr dc, uint f);
 [DllImport("user32.dll")]public static extern bool ShowWindow(IntPtr h,int c);
 [DllImport("user32.dll")]public static extern bool SetForegroundWindow(IntPtr h);
 [DllImport("user32.dll")]public static extern bool MoveWindow(IntPtr h,int x,int y,int w,int ht,bool r);
}
'@
Add-Type -TypeDefinition 'using System;using System.Runtime.InteropServices;public static class M{[DllImport("user32.dll")]public static extern bool SetCursorPos(int x,int y);[DllImport("user32.dll")]public static extern void mouse_event(uint f,int x,int y,uint d,UIntPtr e);public static void Click(int x,int y){SetCursorPos(x,y);System.Threading.Thread.Sleep(200);mouse_event(2,0,0,0,UIntPtr.Zero);mouse_event(4,0,0,0,UIntPtr.Zero);}}'
$AE = [System.Windows.Automation.AutomationElement]
function RealClick($e) { $r = $e.Current.BoundingRectangle; [M]::Click([int]($r.X + $r.Width/2), [int]($r.Y + $r.Height/2)); Start-Sleep -Milliseconds 800 }
function Find($scope, $id) {
  $c = New-Object System.Windows.Automation.PropertyCondition($AE::AutomationIdProperty, $id)
  $scope.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c)
}
function Wait-Find($scope, $id, $sec = 8) {
  $end = (Get-Date).AddSeconds($sec)
  do { $e = Find $scope $id; if ($e) { return $e }; Start-Sleep -Milliseconds 250 } while ((Get-Date) -lt $end)
  return $null
}
function Has($win, $id) { return [bool](Find $win $id) }
function Activate($e) {
  try { $e.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select(); return } catch {}
  try { $e.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); return } catch {}
  try { $e.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle(); return } catch {}
  throw 'no activation pattern'
}
function Shot($hwnd, $name) {
  Start-Sleep -Milliseconds 800
  $rc = New-Object RC; [W]::GetWindowRect($hwnd, [ref]$rc) | Out-Null
  $w = $rc.R - $rc.L; $h = $rc.B - $rc.T
  $bmp = New-Object System.Drawing.Bitmap $w, $h
  $g = [System.Drawing.Graphics]::FromImage($bmp); $dc = $g.GetHdc()
  $ok = [W]::PrintWindow($hwnd, $dc, 2); $g.ReleaseHdc($dc); $g.Dispose()
  $bmp.Save((Join-Path $out "$tag-$name.png")); $bmp.Dispose(); Note "shot $tag-$name ${w}x${h} pw=$ok"
}
function Launch($sd, $extra) {
  $a = '--settings-dir "{0}" --culture {1} {2}' -f $sd, $Culture, $extra
  $p = Start-Process -FilePath $exe -ArgumentList $a -PassThru
  $end = (Get-Date).AddSeconds(30)
  do { Start-Sleep -Milliseconds 400; $p.Refresh() } while ($p.MainWindowHandle -eq 0 -and (Get-Date) -lt $end)
  if ($p.MainWindowHandle -eq 0) { throw 'main window did not appear' }
  Start-Sleep -Seconds 3
  [W]::ShowWindow($p.MainWindowHandle, 9) | Out-Null
  [W]::MoveWindow($p.MainWindowHandle, 20, 20, $Width, $Height, $true) | Out-Null
  (New-Object -ComObject WScript.Shell).AppActivate($p.Id) | Out-Null; [W]::SetForegroundWindow($p.MainWindowHandle) | Out-Null
  Start-Sleep -Seconds 2
  return $p
}
function Stop($p) { if ($p -and -not $p.HasExited) { $p.Kill(); $p.WaitForExit(5000) | Out-Null } }
function UiModeOf($sd) { $f = Join-Path $sd 'settings.json'; if (Test-Path $f) { try { return (Get-Content $f -Raw | ConvertFrom-Json).UiMode } catch { return 'ERR' } } return '<nofile>' }
function TrainTabChecks($win, $expectSimple) {
  $t = Wait-Find $win 'StepTabTrain' 5
  if (-not $t) { Note 'StepTabTrain not found'; return }
  Activate $t; Start-Sleep -Seconds 1
  $start = Has $win 'StartTrainButton'; $ep = Has $win 'EpochsBox'; $ku = Has $win 'KindUltra'
  Note "train tab: StartTrainButton=$start EpochsBox=$ep KindUltra=$ku TrainRecommendedText=$(Has $win 'TrainRecommendedText')"
  if ($expectSimple) { Check 'simple: Train lacks StartTrainButton/EpochsBox/KindUltra' (-not $start -and -not $ep -and -not $ku) }
  else { Check 'advanced: StartTrainButton present' $start }
}
$p = $null
try {
  # ---- Scenario 1: no settings.json -> Simple
  $sd1 = Join-Path $base 's1'; New-Item -ItemType Directory -Force $sd1 | Out-Null
  $p = Launch $sd1 ''
  $win = $AE::FromHandle($p.MainWindowHandle)
  Check 'S1 UiModeSimple exists' (Has $win 'UiModeSimple'); Check 'S1 UiModeAdvanced exists' (Has $win 'UiModeAdvanced')
  foreach ($i in 1..5) { Check "S1 EasyStep$i exists" (Has $win "EasyStep$i") }
  Check 'S1 settings.json UiMode=simple' ((UiModeOf $sd1) -eq 'simple')
  Note ('S1 UiMode in file=' + (UiModeOf $sd1))
  Shot $p.MainWindowHandle 's1-start'
  TrainTabChecks $win $true
  Shot $p.MainWindowHandle 's1-train'
  # ---- Scenario 3: toggle live (same process)
  $adv = Find $win 'UiModeAdvanced'; RealClick $adv; Start-Sleep -Seconds 1
  Check 'S3 after Advanced click: EasyStep1 absent' (-not (Has $win 'EasyStep1'))
  TrainTabChecks $win $false
  Shot $p.MainWindowHandle 's3-advanced'
  Check 'S3 settings.json UiMode=advanced' ((UiModeOf $sd1) -eq 'advanced')
  $sim = Find $win 'UiModeSimple'; RealClick $sim; Start-Sleep -Seconds 1
  Check 'S3 after Simple click: EasyStep1 present' (Has $win 'EasyStep1')
  TrainTabChecks $win $true
  Shot $p.MainWindowHandle 's3-simple-again'
  Stop $p; $p = $null

  # ---- Scenario 2: existing settings.json without UiMode -> Advanced
  $sd2 = Join-Path $base 's2'; New-Item -ItemType Directory -Force $sd2 | Out-Null
  '{ "Theme": "dark" }' | Set-Content (Join-Path $sd2 'settings.json') -Encoding utf8
  $p = Launch $sd2 ''
  $win = $AE::FromHandle($p.MainWindowHandle)
  Check 'S2 EasyStep* absent' (-not (Has $win 'EasyStep1') -and -not (Has $win 'EasyStep3'))
  Check 'S2 UiMode toggle present' ((Has $win 'UiModeSimple') -and (Has $win 'UiModeAdvanced'))
  Note ('S2 UiMode in file=' + (UiModeOf $sd2))
  TrainTabChecks $win $false
  Shot $p.MainWindowHandle 's2-advanced-train'
  Stop $p; $p = $null

  # ---- Scenario 4: fixture project in Simple
  $sd4 = Join-Path $base 's4'; New-Item -ItemType Directory -Force $sd4 | Out-Null
  $p = Launch $sd4 ('--project "{0}"' -f $fixture)
  $win = $AE::FromHandle($p.MainWindowHandle)
  Start-Sleep -Seconds 4
  foreach ($i in 1..5) { $e = Find $win "EasyStep$i"; if ($e) { Note ("S4 EasyStep$i name='" + $e.Current.Name + "' help='" + $e.Current.HelpText + "' enabled=" + $e.Current.IsEnabled) } else { Note "S4 EasyStep$i MISSING" } }
  Shot $p.MainWindowHandle 's4-open'
  $lab = Find $win 'StepTabLabel'; if ($lab) { Activate $lab; Start-Sleep -Seconds 2 }
  $md = Find $win 'FileMostDoubtful'
  Check 'S4 Label: FileMostDoubtful present' ([bool]$md)
  if ($md) { Note ("S4 FileMostDoubtful name='" + $md.Current.Name + "' offscreen=" + $md.Current.IsOffscreen + " rect=" + $md.Current.BoundingRectangle) }
  Shot $p.MainWindowHandle 's4-label'
  $ins = Find $win 'StepTabInspect'; if ($ins) { Activate $ins; Start-Sleep -Seconds 2 }
  $flt = Find $win 'InspectFilter'
  Check 'S4 Inspect: InspectFilter present' ([bool]$flt)
  if ($flt) {
    try { $flt.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand(); Start-Sleep -Milliseconds 700 } catch { Note 'filter expand failed' }
    $items = $flt.FindAll([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)))
    $names = @(); foreach ($it in $items) { $names += $it.Current.Name }
    if ($names.Count -eq 0) { $all = $AE::RootElement.FindAll([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem))); foreach ($it in $all) { $names += $it.Current.Name } }
    Note ('S4 filter items: ' + ($names -join ' | '))
    $want = if ($Culture -eq 'en') { 'Review doubtful' } else { $null }
    if ($want) { Check "S4 filter has '$want'" ([bool]($names -match [regex]::Escape($want))) } else { Check 'S4 filter has >= 6 items (vi: verify name in log)' ($names.Count -ge 6) }
    try { $flt.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Collapse() } catch {}
  }
  Shot $p.MainWindowHandle 's4-inspect'
  $tr = Find $win 'StepTabTrain'; if ($tr) { Activate $tr; Start-Sleep -Seconds 3 }
  $rt = Find $win 'TrainRecommendedText'
  Check 'S4 Train: TrainRecommendedText present' ([bool]$rt)
  if ($rt) { Note ("S4 recommended text='" + $rt.Current.Name + "'") }
  Shot $p.MainWindowHandle 's4-train'
  Stop $p; $p = $null
} catch { Note ('EXCEPTION: ' + $_.Exception.Message + ' @' + $_.InvocationInfo.ScriptLineNumber) }
finally { Stop $p; $log | Set-Content (Join-Path $out "$tag-smoke.log") }
