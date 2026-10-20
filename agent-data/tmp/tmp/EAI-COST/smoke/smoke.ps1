# EAI-COST UI smoke: Offline-only switch, consent dialog, withdraw button.
# Run: powershell -NoProfile -File tmp\EAI-COST\smoke\smoke.ps1 -Culture en   (cwd = repo root)
param([string]$Culture = 'en')
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path '.').Path
$script:root = $root; $script:Culture = $Culture
$script:out = Join-Path $root 'artifacts\EAI-COST\ui-smoke'
$script:exe = Join-Path $root 'EasyLabel\src\EasyAI.Wpf\bin\Release\net9.0-windows\EasyAI.exe'
$script:log = New-Object System.Collections.Generic.List[string]
$smoke = Join-Path $root 'tmp\EAI-COST\smoke'
New-Item -ItemType Directory -Force $script:out | Out-Null
$env:TEMP = Join-Path $smoke 'temp'; $env:TMP = $env:TEMP
# Real claude/codex must never run: the locators resolve to a harmless system exe, the chat run goes to the fake script (--fake-agent).
$env:EASYAI_CLAUDE = 'C:\Windows\System32\hostname.exe'; $env:EASYAI_CODEX = 'C:\Windows\System32\hostname.exe'
. (Join-Path $smoke 'lib.ps1')

$fixture = Join-Path $smoke ("fixture-$Culture")
if (Test-Path $fixture) { Remove-Item $fixture -Recurse -Force }
New-Item -ItemType Directory -Force (Join-Path $fixture 'Smoke.images') | Out-Null
Get-ChildItem 'E:\EAI_TEST' -Filter *.png | Select-Object -First 3 | Copy-Item -Destination (Join-Path $fixture 'Smoke.images')
$now = (Get-Date).ToUniversalTime().ToString('o')
$mf = '{ "schema": 3, "name": "Smoke", "createdUtc": "' + $now + '", "lastOpenedUtc": "' + $now + '", "imageRoot": "Smoke.images", "dataDir": "Smoke.easyai.data" }'
$proj = Join-Path $fixture 'Smoke.easyai'
[System.IO.File]::WriteAllText($proj, $mf, (New-Object System.Text.UTF8Encoding($false)))
$sd = Join-Path $smoke "settings-$Culture"; if (Test-Path $sd) { Remove-Item $sd -Recurse -Force }
$fakeLog = Join-Path $smoke 'fake-agent-invoked.log'
if (Test-Path $fakeLog) { Remove-Item $fakeLog }
$agentsBefore = AgentPids
Note "agent pids before ($Culture): $agentsBefore"

function Go-Label {
  $st = ById 'Stepper'
  $c = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::RadioButton)
  $radios = $st.FindAll($TS::Descendants, $c)
  Invoke-El $radios[1] 'step Label' | Out-Null; Start-Sleep -Milliseconds 1500
}
function Open-Chat {
  $w = Win 'AgentChatWindow'
  if ($w -eq $null) { Invoke-El (ById 'AgentChatButton') 'agent chat button' | Out-Null; Start-Sleep -Milliseconds 1500; $w = Win 'AgentChatWindow' }
  if ($w -eq $null) { Note 'chat window not found' }
  return $w
}
function Process-Check($label) {
  Note ("proc[$label] app descendants: [" + (AppDescendants) + "]; agent pids now: " + (AgentPids) + "; fake agent log: " + $(if (Test-Path $fakeLog) { (Get-Content $fakeLog) -join ' / ' } else { '<none>' }))
}
try {
  # ---------- A: Offline switch, default + toggle ----------
  Start-App $sd $proj $Culture
  $main = $AE::FromHandle($script:hwnd)
  Shot $main 'a0-open'
  $sw = @(Open-Settings-Agents)[-1]
  if ($sw -ne $null) {
    $t = ById 'AgentOfflineOnly' $sw
    Note ('A1 offline toggle present=' + ($t -ne $null) + ' state=' + $(if ($t) { ToggleState $t } else { '-' }) + ' name=' + $(if ($t) { $t.Current.Name } else { '-' }))
    Note ('A1 agents page text: ' + (Texts $sw))
    Shot $sw 'a1-agents-default'
    if ($t -ne $null) {
      $t.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle(); Start-Sleep -Milliseconds 1500
      Note ('A2 after toggle state=' + (ToggleState $t))
      Note ('A2 agents page text: ' + (Texts $sw))
      Shot $sw 'a2-offline-on'
    }
    Close-Window $sw
  }
  Shot $main 'a3-label-offline'
  $r = ById 'SourceAgentPrompt'; Invoke-El $r 'source Agent prompt' | Out-Null; Start-Sleep -Milliseconds 1000
  $reason = ById 'SourceReasonText'
  Note ('A3 label SourceReasonText=' + $(if ($reason) { $reason.Current.Name } else { '<absent>' }))
  $chat = Open-Chat
  if ($chat -ne $null) {
    $sg = ById 'AgentSuggestPromptButton' $chat
    Note ('A3 suggest button enabled=' + $(if ($sg) { $sg.Current.IsEnabled } else { 'absent' }))
    Note ('A3 chat text: ' + (Texts $chat))
    Shot $chat 'a3-chat-offline'
    if ($sg -ne $null -and $sg.Current.IsEnabled -and $t -ne $null -and (ToggleState $t) -eq 'On') { Invoke-El $sg 'suggest (offline)' | Out-Null; Start-Sleep -Seconds 2; Note ('A3 chat text after click: ' + (Texts $chat)) }
    Close-Window $chat
  }
  Process-Check 'A-offline'
  Note ('A settings.json AgentOfflineOnly: ' + ((Get-Content (Join-Path $sd 'settings.json') -Raw) -split "`n" | Where-Object { $_ -match 'AgentOfflineOnly' }))
  Stop-App

  # ---------- B: persistence ----------
  Start-App $sd $proj $Culture
  $sw = @(Open-Settings-Agents)[-1]
  $t = ById 'AgentOfflineOnly' $sw
  Note ('B1 after restart toggle state=' + $(if ($t) { ToggleState $t } else { '<absent>' }))
  Shot $sw 'b1-restart-offline-still-on'
  if ($t -ne $null) { $t.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle(); Start-Sleep -Milliseconds 1500; Note ('B2 toggled off, state=' + (ToggleState $t)) }
  Close-Window $sw
  Stop-App

  # ---------- C: consent dialog, decline ----------
  Start-App $sd $proj $Culture
  Invoke-El (ById 'SourceAgentPrompt') 'source Agent prompt' | Out-Null; Start-Sleep -Milliseconds 800
  $chat = Open-Chat
  $sg = ById 'AgentSuggestPromptButton' $chat
  Note ('C1 suggest enabled=' + $(if ($sg) { $sg.Current.IsEnabled } else { 'absent' }))
  Invoke-El $sg 'suggest prompt' | Out-Null; Start-Sleep -Seconds 2
  $dlg = Win 'PromptWindow'
  if ($dlg -eq $null) { Note 'C1 consent dialog NOT shown' } else {
    Note ('C1 dialog text: ' + (Texts $dlg))
    Shot $dlg 'c1-consent-dialog'
    Invoke-El (ById 'PromptCancel' $dlg) 'dialog Decline/Cancel' | Out-Null; Start-Sleep -Seconds 3
    Note ('C2 dialog still open after decline=' + ((Win 'PromptWindow') -ne $null))
    Note ('C2 chat text after decline: ' + (Texts $chat))
    Shot $chat 'c2-after-decline'
  }
  Process-Check 'C-after-decline'
  Note ('C2 manifest: ' + (Manifest-Consent $proj))

  # ---------- D: accept, then the Agents page ----------
  Invoke-El (ById 'AgentSuggestPromptButton' $chat) 'suggest prompt again' | Out-Null; Start-Sleep -Seconds 2
  $dlg = Win 'PromptWindow'
  if ($dlg -eq $null) { Note 'D1 consent dialog NOT shown the second time' } else {
    Invoke-El (ById 'PromptPrimary' $dlg) 'dialog Allow for this project' | Out-Null; Start-Sleep -Seconds 8
  }
  Process-Check 'D-after-accept'
  Note ('D1 manifest: ' + (Manifest-Consent $proj))
  Close-Window (Win 'AgentChatWindow')
  $sw = @(Open-Settings-Agents)[-1]
  if ($sw -ne $null) {
    $cs = ById 'AgentConsentStatus' $sw; $rv = ById 'AgentConsentRevoke' $sw
    Note ('D2 consent status=' + $(if ($cs) { $cs.Current.Name } else { '<absent>' }) + '; withdraw button present=' + ($rv -ne $null) + ' enabled=' + $(if ($rv) { $rv.Current.IsEnabled } else { '-' }))
    Scroll-End $sw
    Shot $sw 'd2-consent-granted'
    Invoke-El $rv 'Withdraw' | Out-Null; Start-Sleep -Seconds 2
    $cs = ById 'AgentConsentStatus' $sw
    Note ('D3 after withdraw status=' + $(if ($cs) { $cs.Current.Name } else { '<absent>' }))
    Shot $sw 'd3-consent-withdrawn'
    Close-Window $sw
  }
  Note ('D3 manifest: ' + (Manifest-Consent $proj))
}
catch { Note ('SCRIPT ERROR: ' + $_ + ' @ ' + $_.ScriptStackTrace) }
finally {
  Stop-App
  Process-Check 'end'
  $script:log | Set-Content (Join-Path $script:out "$Culture-smoke.log")
}
