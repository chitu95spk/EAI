# OK/NG UI smoke. Run from project root: powershell -NoProfile -File tmp\EAI-OKNG\smoke-okng.ps1 -Culture en -Width 1280 -Height 800
param([string]$Culture = 'en', [int]$Width = 1280, [int]$Height = 800, [int]$TrainTimeoutSec = 900)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing, System.Windows.Forms, UIAutomationClient, UIAutomationTypes
$root = (Resolve-Path '.').Path
$tag = "$Culture-${Width}x$Height"
$out = Join-Path $root 'artifacts\EAI-OKNG\smoke'
$exe = Join-Path $root 'artifacts\EAI-OKNG\b5\out\EasyAI.exe'
New-Item -ItemType Directory -Force $out | Out-Null
$env:TEMP = Join-Path $root 'tmp\EAI-PROJ\temp'; $env:TMP = $env:TEMP
$sd = Join-Path $root "tmp\EAI-OKNG\smoke-settings\$tag"
if (Test-Path $sd) { Remove-Item $sd -Recurse -Force }
New-Item -ItemType Directory -Force (Join-Path $sd 'models') | Out-Null
Copy-Item (Join-Path $root 'artifacts\EAI-OKNG\classify-backbone') (Join-Path $sd 'models\classify-backbone') -Recurse
$proj = Join-Path $root "tmp\EAI-OKNG\smoke-run\$tag"
if (Test-Path $proj) { Remove-Item $proj -Recurse -Force }
New-Item -ItemType Directory -Force $proj | Out-Null
Copy-Item (Join-Path $root 'tmp\EAI-OKNG\smoke-project\*') $proj
$exportOut = Join-Path $root "tmp\EAI-OKNG\smoke-export\$tag"
if (Test-Path $exportOut) { Remove-Item $exportOut -Recurse -Force }
New-Item -ItemType Directory -Force $exportOut | Out-Null
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
$TS = [System.Windows.Automation.TreeScope]
$CT = [System.Windows.Automation.ControlType]
function CtCond($ct) { New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $ct) }
function RealClick($e) { $r = $e.Current.BoundingRectangle; [M]::Click([int]($r.X + $r.Width/2), [int]($r.Y + $r.Height/2)); Start-Sleep -Milliseconds 800 }
function Find($scope, $id) { $scope.FindFirst($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::AutomationIdProperty, $id))) }
function FindAllId($scope, $id) { $scope.FindAll($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::AutomationIdProperty, $id))) }
function Has($win, $id) { return [bool](Find $win $id) }
function NameOf($win, $id) { $e = Find $win $id; if ($e) { return $e.Current.Name } else { return '<missing>' } }
function Activate($e) {
  try { $e.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select(); return } catch {}
  try { $e.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); return } catch {}
  try { $e.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle(); return } catch {}
  RealClick $e
}
function Shot($hwnd, $name) {
  Start-Sleep -Milliseconds 800
  $rc = New-Object RC; [W]::GetWindowRect($hwnd, [ref]$rc) | Out-Null
  $w = $rc.R - $rc.L; $h = $rc.B - $rc.T
  if ($w -le 0 -or $h -le 0) { Note "shot $name skipped (no window)"; return }
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
function Badges($win) { $r = @(); foreach ($b in (FindAllId $win 'FileClassBadge')) { $r += $b.Current.Name }; return $r }
function ListItems($win) { $l = Find $win 'ImageList'; if (-not $l) { return @() }; return @($l.FindAll($TS::Children, (CtCond $CT::ListItem))) }
function AllTexts($win) { $r = @(); foreach ($t in $win.FindAll($TS::Descendants, (CtCond $CT::Text))) { if ($t.Current.Name) { $r += $t.Current.Name } }; return $r }
function Wait-Train($win, $pattern, $idleLimit = 0) {
  $end = (Get-Date).AddSeconds($TrainTimeoutSec); $last = ''; $t0 = Get-Date
  do {
    Start-Sleep -Seconds 3
    $pt = NameOf $win 'TrainProgressText'
    if ($pt -ne $last) { Note "  train progress: $pt"; $last = $pt }
    $hit = @(AllTexts $win | ? { $_ -match $pattern })
    if ($hit.Count) { return $hit[0] }
    if ($idleLimit -gt 0 -and $pt -eq '<missing>' -and ((Get-Date) - $t0).TotalSeconds -gt $idleLimit) { Note '  train never started'; return $null }
  } while ((Get-Date) -lt $end)
  return $null
}
function SendKey($hwnd, $keys) { [W]::SetForegroundWindow($hwnd) | Out-Null; Start-Sleep -Milliseconds 300; [System.Windows.Forms.SendKeys]::SendWait($keys); Start-Sleep -Seconds 1 }
$p = $null
try {
  $p = Launch $sd ('--project "{0}"' -f $proj)
  $h = $p.MainWindowHandle
  $win = $AE::FromHandle($h)
  Start-Sleep -Seconds 4
  Check 'Simple mode (EasyStep1 present)' (Has $win 'EasyStep1')
  # ---- A: Label list badges + counts
  $lab = Find $win 'StepTabLabel'; if ($lab) { Activate $lab; Start-Sleep -Seconds 2 }
  $b = Badges $win
  Note ("badges (" + $b.Count + "): " + (($b | select -First 14) -join ' | '))
  Check 'A badges present' ($b.Count -ge 1)
  $cnt = NameOf $win 'FileClassCounts'
  Note "FileClassCounts='$cnt'"
  $want = if ($Culture -eq 'en') { 'OK 6 · NG 6 · Not marked 72' } else { 'OK 6 · NG 6 · Chưa đánh dấu 72' }
  Check "A count line == '$want'" ($cnt -eq $want)
  Shot $h 'a-label'
  # ---- B: select untagged image, press G / B
  function StatusImage($win) { $m = @(AllTexts $win | ? { $_ -match '^(Image|Ảnh) \d+/\d+' }); if ($m.Count) { return $m[0] } else { return '?' } }
  $nx = $null; foreach ($btn in $win.FindAll($TS::Descendants, (CtCond $CT::Button))) { if ($btn.Current.Name -eq 'Next Image') { $nx = $btn; break } }
  if (-not $nx) { foreach ($btn in $win.FindAll($TS::Descendants, (CtCond $CT::Button))) { if ($btn.Current.Name -match 'Next|Tiếp|Sau') { $nx = $btn; break } } }
  Note ("B next button: " + $(if ($nx) { $nx.Current.Name } else { 'missing' }) + " status=" + (StatusImage $win))
  if ($nx) { 1..6 | % { Activate $nx; Start-Sleep -Milliseconds 700 } }
  Note ("B at: " + (StatusImage $win) + " (expect image 7 = first untagged)")
  $s0 = StatusImage $win
  SendKey $h 'g'
  $cnt2 = NameOf $win 'FileClassCounts'
  $s1 = StatusImage $win
  Note "after G: counts='$cnt2' status '$s0' -> '$s1'"
  Check 'B G adds OK mark (OK 7, Not marked 71)' ($cnt2 -match 'OK 7' -and $cnt2 -match ' 71')
  Check 'B selection advanced to next untagged (image 8)' ($s1 -match '8/')
  Shot $h 'b-after-g'
  SendKey $h 'b'
  $cnt3 = NameOf $win 'FileClassCounts'
  Note "after B: counts='$cnt3' status '$(StatusImage $win)'"
  Check 'B B adds NG mark (NG 7, Not marked 70)' ($cnt3 -match 'NG 7' -and $cnt3 -match ' 70')
  Note ("B badges visible: " + ((Badges $win) -join ','))
  Shot $h 'b-after-b'
  # ---- C: Simple train
  $s3 = Find $win 'EasyStep3'
  if ($s3) { Note ("EasyStep3 name='" + $s3.Current.Name + "' enabled=" + $s3.Current.IsEnabled); Activate $s3; Start-Sleep -Seconds 5 }
  Note ('C fix text: ' + (NameOf $win 'TrainFixText') + ' | start enabled: ' + $(if (Find $win 'StartTrainButton') { 'present' } else { 'absent' }))
  Shot $h 'c-train-running'
  Note ("C progress text: " + (NameOf $win 'TrainProgressText'))
  $done = Wait-Train $win 'model ready|sẵn sàng' 120
  Note "C done text: $done"
  Check 'C train finished' ([bool]$done)
  if ($done) { Check 'C done text has no accuracy (Simple)' ($done -notmatch 'correct|đúng|%') }
  Shot $h 'c-train-done'
  # ---- D: Inspect
  $ins = Find $win 'StepTabInspect'; if (-not $ins) { $ins = Find $win 'EasyStep4' }
  if ($ins) { Activate $ins; Start-Sleep -Seconds 3 }
  $mc = Find $win 'InspectModelCombo'
  Check 'D InspectModelCombo present' ([bool]$mc)
  if ($mc) {
    try { $mc.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand(); Start-Sleep -Milliseconds 700 } catch { Note 'combo expand failed' }
    $mis = $mc.FindAll($TS::Descendants, (CtCond $CT::ListItem))
    $mi = @($mis | % { $_.Current.Name })
    Note ('D model items: ' + ($mi -join ' | '))
    Shot $h 'd-model-combo'
    $pick = $null; foreach ($x in $mis) { if ($x.Current.Name -match 'OK/NG|eaicls|classif') { $pick = $x; break } }
    if (-not $pick -and $mis.Count) { $pick = $mis[0] }
    if ($pick) { Note ("D selecting: " + $pick.Current.Name); Activate $pick; Start-Sleep -Seconds 1 }
    try { $mc.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Collapse() } catch {}
    Check 'D OK/NG model item present' ([bool]($mi -match 'OK/NG|eaicls|classif'))
  }
  $af = Find $win 'InspectAddFolder'; if (-not $af) { $af = Find $win 'InspectStartAddFolder' }
  if ($af) { [W]::SetForegroundWindow($h) | Out-Null; RealClick $af; Start-Sleep -Seconds 5; [System.Windows.Forms.SendKeys]::SendWait($proj + '{ENTER}'); Start-Sleep -Seconds 2; [System.Windows.Forms.SendKeys]::SendWait('{ENTER}'); Start-Sleep -Seconds 6; Note ('D InspectCount after add: ' + (NameOf $win 'InspectCount')) } else { Note 'D add folder button missing' }
  $ra = Find $win 'InspectRunAll'
  if ($ra) {
    Activate $ra; Start-Sleep -Seconds 2
    $end = (Get-Date).AddSeconds(300)
    do { Start-Sleep -Seconds 3; $c = Find $win 'InspectCancel'; $busy = $c -and $c.Current.IsEnabled } while ((Get-Date) -lt $end -and $busy)
    Note ("D summary='" + (NameOf $win 'InspectSummary') + "' progress='" + (NameOf $win 'InspectProgressText') + "'")
  } else { Check 'D InspectRunAll present' $false }
  $ilist = Find $win 'InspectList'
  if ($ilist) {
    $rows = @($ilist.FindAll($TS::Descendants, (CtCond $CT::DataItem)))
    if ($rows.Count -eq 0) { $rows = @($ilist.FindAll($TS::Descendants, (CtCond $CT::ListItem))) }
    Note ("D inspect rows: " + $rows.Count)
    foreach ($r in ($rows | select -First 4)) { $tx = @($r.FindAll($TS::Descendants, (CtCond $CT::Text)) | % { $_.Current.Name }); Note ("   row: " + $r.Current.Name + ' => ' + ($tx -join ' | ')) }
    Check 'D inspect rows filled' ($rows.Count -gt 0)
  }
  Shot $h 'd-inspect-run'
  $flt = Find $win 'InspectFilter'
  if ($flt) {
    try { $flt.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand(); Start-Sleep -Milliseconds 700 } catch { Note 'filter expand failed' }
    $fis = $flt.FindAll($TS::Descendants, (CtCond $CT::ListItem))
    Note ('D filter items: ' + (@($fis | % { $_.Current.Name }) -join ' | '))
    $pat = if ($Culture -eq 'en') { 'Review doubtful' } else { 'nghi ngờ|Xem lại' }
    $f = $null; foreach ($x in $fis) { if ($x.Current.Name -match $pat) { $f = $x; break } }
    Check "D filter has doubtful item" ([bool]$f)
    if ($f) { Activate $f; Start-Sleep -Seconds 1 }
    try { $flt.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Collapse() } catch {}
    Note ("D after filter: InspectCount='" + (NameOf $win 'InspectCount') + "'")
    Shot $h 'd-inspect-filter'
  }
  # ---- E: Export window
  [W]::SetForegroundWindow($h) | Out-Null
  $mf = Find $win 'MenuFile'
  if ($mf) { try { $mf.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand() } catch { RealClick $mf }; Start-Sleep -Milliseconds 800 }
  $me = Find $win 'MenuExport'
  if (-not $me) { $me = Find $AE::RootElement 'MenuExport' }
  if ($me) { Activate $me; Start-Sleep -Seconds 2 } else { Note 'E MenuExport not found' }
  $ex = $null
  foreach ($w in $AE::RootElement.FindAll($TS::Children, (New-Object System.Windows.Automation.PropertyCondition($AE::ProcessIdProperty, $p.Id)))) { if (Find $w 'ExportDialog') { $ex = $w; break } }
  Check 'E Export window opened' ([bool]$ex)
  if ($ex) {
    $exh = [IntPtr]$ex.Current.NativeWindowHandle
    $sm = Find $ex 'ExportSendModel'
    if ($sm) { try { $sm.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand(); Start-Sleep -Milliseconds 700 } catch { Note 'E send-model expand failed' }
      $sis = $sm.FindAll($TS::Descendants, (CtCond $CT::ListItem)); Note ('E send models: ' + (@($sis | % { $_.Current.Name }) -join ' | '))
      foreach ($x in $sis) { if ($x.Current.Name -match 'OK/NG|eaicls') { Activate $x; Start-Sleep -Seconds 1; break } }
      try { $sm.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Collapse() } catch {} }
    $ob = Find $ex 'ExportOkNgModel'
    Check 'E ExportOkNgModel present' ([bool]$ob)
    $o = Find $ex 'ExportOutput'
    if ($o) { try { $o.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($exportOut) } catch { Note 'E set output failed' } }
    Start-Sleep -Milliseconds 500
    $sv = Find $ex 'ExportSendToEasyVision'
    Note ("E SendBlock='" + (NameOf $ex 'ExportSendBlock') + "' SendToEV enabled=" + $(if ($sv) { $sv.Current.IsEnabled } else { 'missing' }))
    Shot $exh 'e-export-before'
    if ($ob) { Note ("E OkNg button name='" + $ob.Current.Name + "' enabled=" + $ob.Current.IsEnabled); Activate $ob; Start-Sleep -Seconds 4 }
    Note ("E ExportResult='" + (NameOf $ex 'ExportResult') + "'")
    $files = @(Get-ChildItem $exportOut -Recurse -File -ErrorAction SilentlyContinue | % { $_.FullName.Substring($exportOut.Length) })
    Note ('E output files: ' + ($files -join ', '))
    Check 'E .eaicls written' ([bool]($files -match '\.eaicls$'))
    Check 'E trace json written' ([bool]($files -match '\.json$'))
    $sv = Find $ex 'ExportSendToEasyVision'
    Check 'E Send to EasyVision blocked with reason' ($sv -and -not $sv.Current.IsEnabled -and ((NameOf $ex 'ExportSendBlock').Length -gt 0))
    Shot $exh 'e-export-after'
    $cl = Find $ex 'ExportClose'; if ($cl) { Activate $cl } else { try { $ex.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close() } catch {} }
    Start-Sleep -Seconds 1
  }
  # ---- F: Advanced
  [W]::SetForegroundWindow($h) | Out-Null
  $adv = Find $win 'UiModeAdvanced'; if ($adv) { RealClick $adv; Start-Sleep -Seconds 1 }
  $tr = Find $win 'StepTabTrain'; if ($tr) { Activate $tr; Start-Sleep -Seconds 2 }
  $kc = Find $win 'KindClassify'
  Check 'F KindClassify visible in Advanced' ([bool]$kc -and -not $kc.Current.IsOffscreen)
  Shot $h 'f-advanced-train'
  if ($kc) {
    Activate $kc; Start-Sleep -Seconds 1
    $sb = Find $win 'StartTrainButton'
    if ($sb -and $sb.Current.IsEnabled) {
      Activate $sb; Start-Sleep -Seconds 5
      $done2 = Wait-Train $win 'correct on|đúng'
      Note "F done text: $done2"
      Check 'F Advanced done text shows accuracy' ([bool]$done2)
    } else { Check 'F StartTrainButton enabled' $false }
    Shot $h 'f-advanced-done'
  }
} catch { Note ('EXCEPTION: ' + $_.Exception.Message + ' @' + $_.InvocationInfo.ScriptLineNumber) }
finally { Stop $p; $log | Set-Content (Join-Path $out "$tag-smoke.log") }
