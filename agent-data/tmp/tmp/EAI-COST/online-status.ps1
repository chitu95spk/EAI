$ErrorActionPreference = 'Stop'
$script = [System.IO.File]::ReadAllText((Join-Path (Get-Location) 'tmp/EAI-COST/followup.ps1'), [System.Text.Encoding]::UTF8)
. ([scriptblock]::Create($script.Substring(0, $script.IndexOf("`n`$file ="))))
Replace-Once 'EasyLabel/src/EasyAI.Wpf/ViewModels/AgentsSettingsViewModel.cs' @'
        if (_lastOffline == offline) return;
        _lastOffline = offline;
        Claude.RefreshOfflineState();
        Codex.RefreshOfflineState();
        Prices.RefreshOfflineState();
        OnPropertyChanged(nameof(AgentOfflineOnly));
'@ @'
        if (_lastOffline == offline) return;
        bool wasOffline = _lastOffline == true;
        _lastOffline = offline;
        Claude.RefreshOfflineState();
        Codex.RefreshOfflineState();
        Prices.RefreshOfflineState();
        OnPropertyChanged(nameof(AgentOfflineOnly));
        if (wasOffline && !offline) _ = Task.WhenAll(Claude.RefreshAsync(), Codex.RefreshAsync());
'@
