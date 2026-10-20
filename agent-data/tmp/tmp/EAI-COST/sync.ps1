$ErrorActionPreference = 'Stop'
$script = [System.IO.File]::ReadAllText((Join-Path (Get-Location) 'tmp/EAI-COST/followup.ps1'), [System.Text.Encoding]::UTF8)
. ([scriptblock]::Create($script.Substring(0, $script.IndexOf("`n`$file ="))))
$p = 'EasyLabel/src/EasyAI.Wpf/'
$file = $p+'ViewModels/AgentsSettingsViewModel.cs'
Replace-Once $file '        PromptSubAgent = new PromptSubAgentViewModel(main);' "        PromptSubAgent = new PromptSubAgentViewModel(main);`n        RefreshOfflineState();"
Replace-Once $file '    public string OfflineCaption => Strings.AgentOfflineOnly;' @'
    private bool? _lastOffline;

    internal void RefreshOfflineState()
    {
        bool offline = _main.Settings.AgentOfflineOnly;
        if (_lastOffline != offline)
        {
            _lastOffline = offline;
            Claude.RefreshOfflineState();
            Codex.RefreshOfflineState();
            Prices.RefreshOfflineState();
        }

        OnPropertyChanged(nameof(AgentOfflineOnly));
    }

    public string OfflineCaption => Strings.AgentOfflineOnly;
'@
Replace-Once $file @'
            _main.Label?.RefreshAgentOfflineState();
            Claude.RefreshOfflineState();
            Codex.RefreshOfflineState();
            Prices.RefreshOfflineState();
            OnPropertyChanged();
'@ @'
            _main.Label?.RefreshAgentOfflineState();
            RefreshOfflineState();
'@
Replace-Once ($p+'ViewModels/SettingsViewModel.cs') @'
        if (value.Id == SettingsCatalog.PageAgents)
        {
            if (!_agentsChecked)
'@ @'
        if (value.Id == SettingsCatalog.PageAgents)
        {
            Agents.RefreshOfflineState();
            if (!_agentsChecked)
'@
