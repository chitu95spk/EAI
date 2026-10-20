import re
base = r'E:\Code\EasyVision_Unisen - NoService\tmp\EAI\EAI-3\smoke\smoke3.ps1'
s = open(base, encoding='utf-8').read()
a = s.index("  Shot $p 's1-open'")
b = s.index("  Start-Sleep -Seconds 2   #")
head = s[:a].replace(r'artifacts\EAI\EAI-3\smoke', r'artifacts\EAI\EAI-4\smoke').replace(r'tmp\EAI\EAI-3\fixture-', r'tmp\EAI\EAI-4\fixture-').replace(r'tmp\EAI\EAI-3\temp', r'tmp\EAI\EAI-4\temp')
head = head.replace(r"tmp\EAI\EAI-3\fixture-src", r"tmp\EAI\EAI-3\fixture-src")
head = head.replace('EAI-3 Step 2 UI smoke', 'EAI-4 Step 3 UI smoke').replace(r'tmp\EAI\EAI-3\smoke\smoke3.ps1', r'tmp\EAI\EAI-4\smoke\smoke4.ps1')
tail = s[b:]
helpers = r'''
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
'''
k = head.index("$sd = Join-Path $env:TEMP")
head = head[:k] + helpers + head[k:]
body = r'''  Shot $p 't1-open'
  Invoke-El (Radio 1) 'step Label' | Out-Null
  Start-Sleep -Milliseconds 1000
  # import + activate the detecting model through the real UI
  Invoke-El (ById 'ImportModelButton') 'Import model' | Out-Null
  Start-Sleep -Seconds 2
  $desk = [System.Windows.Automation.AutomationElement]::RootElement
  $dlg = $null
  for ($i = 0; $i -lt 12 -and $dlg -eq $null; $i++) {
    $cn = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Window)
    foreach ($w in (Root).FindAll([System.Windows.Automation.TreeScope]::Descendants, $cn)) { if ($w.Current.NativeWindowHandle -ne [int]$hwnd -and $w.Current.NativeWindowHandle -ne 0) { $dlg = $w } }
    if ($dlg -eq $null) { Start-Sleep -Milliseconds 500 }
  }
  if ($dlg -ne $null) {
    $dh = [IntPtr]$dlg.Current.NativeWindowHandle
    [W]::SetForegroundWindow($dh) | Out-Null; Start-Sleep -Milliseconds 600
    if ([W]::GetForegroundWindow() -eq $dh) {
      [System.Windows.Forms.SendKeys]::SendWait($model.Replace('{','{{}').Replace('}','{}}').Replace('+','{+}').Replace('^','{^}').Replace('%','{%}').Replace('~','{~}'))
      Start-Sleep -Milliseconds 500; [System.Windows.Forms.SendKeys]::SendWait('{ENTER}'); Note 'dialog: model path confirmed'
    } else { Note 'ENSURE FAILED: dialog not foreground' }
  } else { Note 'import dialog NOT FOUND' }
  Start-Sleep -Seconds 3
  $combo = ById 'ModelCombo'
  if ($combo -ne $null) { Set-Combo 'ModelCombo' 0; Start-Sleep -Seconds 3 }
  Note ('active model: ' + (Text-Of 'ActiveModelName'))
  # label image 0 (Train) and image 1 (Val) with the model
  foreach ($idx in 0,1) {
    SelectImage $idx
    Invoke-El (ById 'AutoLabelCurrentButton') "auto-label image $idx" | Out-Null
    Wait-Label 180
    Invoke-El (ById 'AcceptAllButton') "accept all image $idx" | Out-Null
    Start-Sleep -Seconds 2
  }
  Keys $p ' '
  Note 'Space pressed on image 1 (Train -> Val)'
  Start-Sleep -Seconds 2
  Shot $p 't2-labelled'
  # Step 3
  Invoke-El (Radio 2) 'step Train' | Out-Null
  Start-Sleep -Milliseconds 1500
  Shot $p 't3-step3'
  Note ('summary train=' + (Text-Of 'TrainSummaryTrain') + ' val=' + (Text-Of 'TrainSummaryVal'))
  $start = ById 'StartTrainButton'
  Note ('start enabled: ' + $(if ($start -ne $null) { $start.Current.IsEnabled } else { 'absent' }))
  Set-Epochs 2
  Set-Combo 'InputSizeCombo' 0
  Shot $p 't4-options'
  Invoke-El (ById 'StartTrainButton') 'Start training' | Out-Null
  Start-Sleep -Seconds 3
  Note ('progress text: ' + (Text-Of 'TrainProgressText'))
  Shot $p 't5-running'
  Note ('Step1/2 locked during run? ImportModel enabled=' + $(if ((ById 'ImportModelButton') -ne $null) { (ById 'ImportModelButton').Current.IsEnabled } else { 'n/a (other step)' }))
  $done = Wait-Train 600
  Note ("training finished: $done; progress text: " + (Text-Of 'TrainProgressText'))
  Start-Sleep -Seconds 2
  Shot $p 't6-finished'
  $list = ById 'CandidateList'
  if ($list -ne $null) {
    $items = $list.FindAll([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)))
    Note ("candidates: " + $items.Count)
    if ($items.Count -gt 0) { $items[0].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select(); Start-Sleep -Milliseconds 1000 }
    Shot $p 't7-compare'
    Invoke-El (ById 'UseModelButton') 'Use this model' | Out-Null
    Start-Sleep -Seconds 3
    Shot $p 't8-activated'
  } else { Note 'CandidateList MISSING' }

  # second short run (300 epochs) -> TRAINED candidate, then compare it with the active TRAINED model
  Set-Epochs 300
  Invoke-El (ById 'StartTrainButton') 'Start training (300 epochs)' | Out-Null
  Start-Sleep -Seconds 2
  Wait-Train 300 | Out-Null
  Start-Sleep -Seconds 2
  $items = (ById 'CandidateList').FindAll([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)))
  Note ('candidates after second run: ' + $items.Count)
  if ($items.Count -gt 0) { $items[0].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select(); Start-Sleep -Milliseconds 1000 }
  $tbl = ById 'CompareTable'
  if ($tbl -ne $null) { $sp = $null; if ($tbl.TryGetCurrentPattern([System.Windows.Automation.ScrollItemPattern]::Pattern, [ref]$sp)) { $sp.ScrollIntoView() }; Start-Sleep -Milliseconds 600; Note 'compare table present' } else { Note 'CompareTable ABSENT' }
  # scroll every scrollable ancestor of the candidate list to the bottom so the comparison table is on screen
  $walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
  $anc = $walker.GetParent((ById 'CandidateList'))
  while ($anc -ne $null) {
    $sp2 = $null
    if ($anc.TryGetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern, [ref]$sp2) -and $sp2.Current.VerticallyScrollable) { try { $sp2.SetScrollPercent(-1, 100) } catch {} }
    $anc = $walker.GetParent($anc)
  }
  Start-Sleep -Milliseconds 800
  Shot $p 't9-compare-table'
  # long run: lock coupling + Stop
  Set-Epochs 2000
  $candBefore = $items.Count
  Invoke-El (ById 'StartTrainButton') 'Start training (2000 epochs)' | Out-Null
  Start-Sleep -Seconds 4
  Note ('during run: Start enabled=' + (ById 'StartTrainButton').Current.IsEnabled + ' Stop enabled=' + (ById 'StopTrainButton').Current.IsEnabled + ' progress=' + (Text-Of 'TrainProgressText'))
  Shot $p 't10-long-running'
  Invoke-El (Radio 1) 'step Label (during run)' | Out-Null
  Start-Sleep -Milliseconds 1000
  foreach ($id in 'ImportModelButton','AutoLabelCurrentButton','AutoLabelAllButton') { $e = ById $id; Note ("during run: $id enabled=" + $(if ($e -ne $null) { $e.Current.IsEnabled } else { 'absent' })) }
  Shot $p 't11-label-locked'
  Invoke-El (Radio 2) 'step Train' | Out-Null
  Start-Sleep -Milliseconds 800
  Invoke-El (ById 'StopTrainButton') 'Stop' | Out-Null
  $done = Wait-Train 120
  Start-Sleep -Seconds 2
  $items = (ById 'CandidateList').FindAll([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)))
  Note ("after Stop: idle=$done candidates before=$candBefore after=" + $items.Count + ' start enabled=' + (ById 'StartTrainButton').Current.IsEnabled)
  Shot $p 't12-stopped'
  Invoke-El (Radio 1) 'step Label (after run)' | Out-Null
  Start-Sleep -Milliseconds 800
  foreach ($id in 'ImportModelButton','AutoLabelCurrentButton') { $e = ById $id; Note ("after run: $id enabled=" + $(if ($e -ne $null) { $e.Current.IsEnabled } else { 'absent' })) }
'''
open(r'E:\Code\EasyVision_Unisen - NoService\tmp\EAI\EAI-4\smoke\smoke4.ps1', 'w', encoding='utf-8').write(head + body + tail)
print('ok')
