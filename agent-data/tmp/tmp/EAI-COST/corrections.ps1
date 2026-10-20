$ErrorActionPreference = 'Stop'
$script = [System.IO.File]::ReadAllText((Join-Path (Get-Location) 'tmp/EAI-COST/followup.ps1'), [System.Text.Encoding]::UTF8)
. ([scriptblock]::Create($script.Substring(0, $script.IndexOf("`n`$file ="))))
$p = 'EasyLabel/src/EasyAI.Wpf/'
Replace-Once ($p+'ViewModels/AgentsSettingsViewModel.cs') '            StatusText = string.Format(System.Globalization.CultureInfo.CurrentCulture, Text("AgentsPricesFailed", "Refresh failed: {0}"), ex.Message);' '            StatusText = _isOffline() ? Strings.AgentOfflineExplanation : string.Format(System.Globalization.CultureInfo.CurrentCulture, Text("AgentsPricesFailed", "Refresh failed: {0}"), ex.Message);'
$file = $p+'ViewModels/LabelStepViewModel.cs'
foreach ($pair in @(
    @('            await ConfigureSpend(agent, runBudget, snapshot);', "            await ConfigureSpend(agent, runBudget, snapshot);`n            if (RefuseOfflineAgent()) return;"),
    @('        await ConfigureSpend(agent, activeBatchBudget, snapshot);', "        await ConfigureSpend(agent, activeBatchBudget, snapshot);`n        if (RefuseOfflineAgent()) return;"),
    @('            await ConfigureSpend(extra, activeBatchBudget, snapshot);', "            await ConfigureSpend(extra, activeBatchBudget, snapshot);`n            if (RefuseOfflineAgent()) return;"),
    @('                await ConfigureSpend(retryRunner, activeBatchBudget, snapshot);', "                await ConfigureSpend(retryRunner, activeBatchBudget, snapshot);`n                if (RefuseOfflineAgent()) return;"),
    @('            await ConfigureSpend(agent, suggestBudget, snapshot);', "            await ConfigureSpend(agent, suggestBudget, snapshot);`n            if (RefuseOfflineAgent()) return;")
)) { Replace-Once $file $pair[0] $pair[1] }
Replace-Once ($p+'ViewModels/LabelStepViewModel.AgentChat.cs') '            await ConfigureSpend(agent, refineBudget, snapshot);' "            await ConfigureSpend(agent, refineBudget, snapshot);`n            if (RefuseOfflineAgent()) return false;"
foreach ($name in @('ViewModels/LabelStepViewModel.cs', 'ViewModels/LabelStepViewModel.AgentChat.cs')) {
    $path = Join-Path (Get-Location) ($p+$name)
    $text = [System.IO.File]::ReadAllText($path, [System.Text.Encoding]::UTF8)
    $text = $text.Replace('AiStatusText = Strings.AgentConsentDeclined;', 'AiStatusText = _host.Settings.AgentOfflineOnly ? Strings.AgentOfflineExplanation : Strings.AgentConsentDeclined;')
    [System.IO.File]::WriteAllText($path, $text, (New-Object System.Text.UTF8Encoding($false)))
}
