$ErrorActionPreference = 'Stop'
$script = [System.IO.File]::ReadAllText((Join-Path (Get-Location) 'tmp/EAI-COST/followup.ps1'), [System.Text.Encoding]::UTF8)
. ([scriptblock]::Create($script.Substring(0, $script.IndexOf("`n`$file ="))))
$p = 'EasyLabel/src/EasyAI.Wpf/'
Replace-Once ($p+'ViewModels/AgentsSettingsViewModel.cs') @'
        if (_lastOffline != offline)
        {
            _lastOffline = offline;
            Claude.RefreshOfflineState();
            Codex.RefreshOfflineState();
            Prices.RefreshOfflineState();
        }

        OnPropertyChanged(nameof(AgentOfflineOnly));
'@ @'
        if (_lastOffline == offline) return;
        _lastOffline = offline;
        Claude.RefreshOfflineState();
        Codex.RefreshOfflineState();
        Prices.RefreshOfflineState();
        OnPropertyChanged(nameof(AgentOfflineOnly));
'@
$file = $p+'ViewModels/SettingsViewModel.cs'
Replace-Once $file '        Agents = new AgentsSettingsViewModel(main);' @'
        Agents = new AgentsSettingsViewModel(main);
        Agents.PropertyChanged -= OnAgentSettingsChanged;
        Agents.PropertyChanged += OnAgentSettingsChanged;
        SettingRowViewModel? offlineRow = RowById(SettingsAccessor.RowAgentOfflineOnly);
        if (offlineRow != null)
        {
            offlineRow.PropertyChanged -= OnOfflineRowChanged;
            offlineRow.PropertyChanged += OnOfflineRowChanged;
        }
'@
Replace-Once $file '    private void OnEasyVisionFolderPicked(object? sender, EventArgs e)' @'
    private void OnAgentSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AgentsSettingsViewModel.AgentOfflineOnly))
        {
            RowById(SettingsAccessor.RowAgentOfflineOnly)?.Refresh();
        }
    }

    private void OnOfflineRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SettingRowViewModel.BoolValue)) Agents.RefreshOfflineState();
    }

    private void OnEasyVisionFolderPicked(object? sender, EventArgs e)
'@
Replace-Once $file @'
    public void Detach()
    {
'@ @'
    public void Detach()
    {
        Agents.PropertyChanged -= OnAgentSettingsChanged;
        SettingRowViewModel? offlineRow = RowById(SettingsAccessor.RowAgentOfflineOnly);
        if (offlineRow != null) offlineRow.PropertyChanged -= OnOfflineRowChanged;
'@
