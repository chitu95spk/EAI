from pathlib import Path
import json

root = Path.cwd()
scratch = root / 'tmp/EAI-COST'
baseline = scratch / 'baseline'
changed = []

def edit(name, replacements):
    path = root / name
    raw = path.read_bytes()
    original = raw
    newline = b'\r\n' if b'\r\n' in raw else b'\n'
    for old, new in replacements:
        a = old.replace('\r\n', '\n').encode('utf-8').replace(b'\n', newline)
        b = new.replace('\r\n', '\n').encode('utf-8').replace(b'\n', newline)
        assert raw.count(a) == 1, (name, old[:100], raw.count(a))
        raw = raw.replace(a, b, 1)
    assert path.read_bytes() == original, 'Concurrent edit: ' + name
    saved = baseline / name
    saved.parent.mkdir(parents=True, exist_ok=True)
    assert not saved.exists(), 'Already edited: ' + name
    saved.write_bytes(original)
    path.write_bytes(raw)
    changed.append(name)

p = 'EasyLabel/src/EasyAI.Wpf/'
edit(p+'Infrastructure/AppSettings.cs', [('    public bool AgentAutoRefinePrompt { get; set; }', '''    public bool AgentAutoRefinePrompt { get; set; }

    /// <summary>Refuse agent processes and online agent services; local workflows remain available. Default off.</summary>
    public bool AgentOfflineOnly { get; set; }''')])
edit(p+'Infrastructure/SettingsAccessor.cs', [
('    public const string RowAgentMaxImages =', '    public const string RowAgentOfflineOnly = "agentOfflineOnly";\n    public const string RowAgentMaxImages ='),
('            case RowAgentRouteLocalFirst: value =', '            case RowAgentOfflineOnly: value = s.AgentOfflineOnly; return true;\n            case RowAgentRouteLocalFirst: value ='),
('            case RowAgentRouteLocalFirst: s.AgentRouteLocalFirst =', '            case RowAgentOfflineOnly: s.AgentOfflineOnly = value; return true;\n            case RowAgentRouteLocalFirst: s.AgentRouteLocalFirst =')])
edit(p+'Infrastructure/SettingsModel.cs', [('        // Caps of an agent batch (EAI-AGENT S7).', '''        SettingPageModel agentsPage = Page(pages, PageAgents, text("SetPageAgents"));
        SettingRowModel offline = Row(agentsPage, SettingsAccessor.RowAgentOfflineOnly, text("SetPageAgents"), text("AgentOfflineOnly"), SettingKind.Toggle, true, null);
        offline.Description = text("AgentOfflineExplanation");

        // Caps of an agent batch (EAI-AGENT S7).''')])
edit(p+'ViewModels/SettingsViewModel.cs', [
('        _pages.Add(new SettingPageModel(SettingsCatalog.PageAgents, Strings.SetPageAgents));\n', ''),
('                _main.NotifySettingChanged();\n            }\n\n            OnPropertyChanged();', '''                if (_model.Id == SettingsAccessor.RowAgentOfflineOnly)
                {
                    _main.Label?.RefreshAgentOfflineState();
                }

                _main.NotifySettingChanged();
            }

            OnPropertyChanged();''')])

edit(p+'Services/Agent/AgentPrices.cs', [
('RefreshAsync(HttpClient http, string? path, CancellationToken ct)\n    {\n        AgentPriceTable', '''RefreshAsync(HttpClient http, string? path, CancellationToken ct, Func<bool>? isOffline = null)
    {
        if (isOffline?.Invoke() == true)
        {
            return new AgentPriceRefreshResult(0, 0, EasyAI.Desktop.Services.AgentRunner.OfflineMessage);
        }

        AgentPriceTable'''),
('        List<AgentPriceEntry> openAiEntries = await FetchAsync', '''        if (isOffline?.Invoke() == true)
        {
            return new AgentPriceRefreshResult(0, 0, EasyAI.Desktop.Services.AgentRunner.OfflineMessage);
        }

        List<AgentPriceEntry> openAiEntries = await FetchAsync''')])

edit(p+'ViewModels/AgentsSettingsViewModel.cs', [
('    private readonly Action<AgentTool> _use;', '    private readonly Action<AgentTool> _use;\n    private readonly Func<bool> _isOffline;'),
('AgentSectionViewModel(AgentTool tool, string title, AgentAuthService auth, Action<AgentTool> use)', 'AgentSectionViewModel(AgentTool tool, string title, AgentAuthService auth, Action<AgentTool> use, Func<bool>? isOffline = null)'),
('        _use = use;\n        _tool = tool;', '        _use = use;\n        _isOffline = isOffline ?? (() => false);\n        _tool = tool;'),
('    public bool CanSignIn => IsInstalled && !IsSignedIn;', '    public bool CanSignIn => !_isOffline() && IsInstalled && !IsSignedIn;'),
('    public bool CanSignOut => IsInstalled && IsSignedIn;', '    public bool CanSignOut => !_isOffline() && IsInstalled && IsSignedIn;'),
('    public async Task RefreshAsync()\n    {\n        IsBusy = true;', '    public async Task RefreshAsync()\n    {\n        if (RefuseOffline()) return;\n        IsBusy = true;'),
('    private async Task LoginAsync()\n    {', '    private async Task LoginAsync()\n    {\n        if (RefuseOffline()) return;'),
('    private async Task LogoutAsync()\n    {', '    private async Task LogoutAsync()\n    {\n        if (RefuseOffline()) return;'),
('    private void Apply(AgentAuthStatus status)', '''    internal void RefreshOfflineState()
    {
        if (_isOffline())
        {
            _loginCts?.Cancel();
            StatusText = Strings.AgentOfflineExplanation;
        }
        else
        {
            StatusText = Strings.AgentsChecking;
        }

        RaiseButtons();
    }

    private bool RefuseOffline()
    {
        if (!_isOffline()) return false;
        StatusText = Strings.AgentOfflineExplanation;
        return true;
    }

    private void Apply(AgentAuthStatus status)'''),
('    private void Apply(AgentAuthStatus status)\n    {', '    private void Apply(AgentAuthStatus status)\n    {\n        if (RefuseOffline()) return;'),
('public sealed partial class AgentPricesViewModel : ObservableObject\n{', 'public sealed partial class AgentPricesViewModel : ObservableObject\n{\n    private readonly Func<bool> _isOffline;'),
('    public AgentPricesViewModel()\n    {\n        StatusText = Describe', '    public AgentPricesViewModel(Func<bool>? isOffline = null)\n    {\n        _isOffline = isOffline ?? (() => false);\n        StatusText = _isOffline() ? Strings.AgentOfflineExplanation : Describe'),
('    private bool CanRefresh() => !IsRefreshing;', '''    internal void RefreshOfflineState()
    {
        StatusText = _isOffline() ? Strings.AgentOfflineExplanation : Describe(AgentPriceStore.Load(), null);
    }

    private bool CanRefresh() => !IsRefreshing;'''),
('    private async Task RefreshAsync()\n    {\n        IsRefreshing = true;', '''    private async Task RefreshAsync()
    {
        if (_isOffline())
        {
            StatusText = Strings.AgentOfflineExplanation;
            return;
        }

        IsRefreshing = true;'''),
('await AgentPriceStore.RefreshAsync(Http, null, CancellationToken.None);\n            StatusText = Describe(AgentPriceStore.Load(), result);', 'await AgentPriceStore.RefreshAsync(Http, null, CancellationToken.None, _isOffline);\n            StatusText = _isOffline() ? Strings.AgentOfflineExplanation : Describe(AgentPriceStore.Load(), result);'),
('public sealed class AgentsSettingsViewModel\n{', 'public sealed class AgentsSettingsViewModel : ObservableObject\n{'),
('AgentTool.Claude, Strings.AgentsClaude, auth, UseAgent);', 'AgentTool.Claude, Strings.AgentsClaude, auth, UseAgent, () => main.Settings.AgentOfflineOnly);'),
('AgentTool.Codex, Strings.AgentsCodex, auth, UseAgent);', 'AgentTool.Codex, Strings.AgentsCodex, auth, UseAgent, () => main.Settings.AgentOfflineOnly);'),
('        Prices = new AgentPricesViewModel();', '        Prices = new AgentPricesViewModel(() => main.Settings.AgentOfflineOnly);'),
('    /// <summary>The "Refresh price" block under the two agents.</summary>', '''    public string OfflineCaption => Strings.AgentOfflineOnly;

    public string OfflineExplanation => Strings.AgentOfflineExplanation;

    public bool AgentOfflineOnly
    {
        get => _main.Settings.AgentOfflineOnly;
        set
        {
            if (_main.Settings.AgentOfflineOnly == value) return;
            _main.Settings.AgentOfflineOnly = value;
            _main.PersistSettings();
            _main.Label?.RefreshAgentOfflineState();
            Claude.RefreshOfflineState();
            Codex.RefreshOfflineState();
            Prices.RefreshOfflineState();
            OnPropertyChanged();
        }
    }

    /// <summary>The "Refresh price" block under the two agents.</summary>'''),
])

edit(p+'Views/AgentsSettingsView.xaml', [('    <StackPanel>\n        <ItemsControl ItemsSource="{Binding Sections}"', '''    <StackPanel>
        <StackPanel Margin="0,12,0,16">
            <ui:ToggleSwitch IsChecked="{Binding AgentOfflineOnly, Mode=TwoWay}"
                             HorizontalAlignment="Left"
                             AutomationProperties.AutomationId="AgentOfflineOnly"
                             AutomationProperties.Name="{Binding OfflineCaption}" />
            <ui:TextBlock Margin="0,6,0,0"
                          Text="{Binding OfflineCaption}"
                          TextWrapping="Wrap" />
            <ui:TextBlock Margin="0,6,0,0"
                          Text="{Binding OfflineExplanation}"
                          TextWrapping="Wrap"
                          Foreground="{DynamicResource TextFillColorSecondaryBrush}" />
        </StackPanel>
        <ItemsControl ItemsSource="{Binding Sections}"''')])

for name, caption, explanation in [
    ('Strings.resx', 'Offline only', 'Offline-only mode blocks agent actions, sign-in checks and price refreshes; local labeling, training and testing stay available.'),
    ('Strings.vi.resx', 'Chỉ dùng ngoại tuyến', 'Chế độ ngoại tuyến chặn tác vụ trợ lý, kiểm tra đăng nhập và cập nhật giá; gán nhãn, huấn luyện và kiểm thử cục bộ vẫn dùng được.'),
]:
    edit(p+'Resources/'+name, [('</root>', f'''  <data name="AgentOfflineOnly" xml:space="preserve">
    <value>{caption}</value>
  </data>
  <data name="AgentOfflineExplanation" xml:space="preserve">
    <value>{explanation}</value>
  </data>
</root>''')])
edit(p+'Resources/Strings.Designer.cs', [('    public static string ByName(string name)', '''    public static string AgentOfflineOnly => Get(nameof(AgentOfflineOnly));
    public static string AgentOfflineExplanation => Get(nameof(AgentOfflineExplanation));
    public static string ByName(string name)''')])

edit(p+'ViewModels/LabelStepViewModel.cs', [
('    public string SourceReasonText => ReasonText(CurrentRunReason());', '    public string SourceReasonText => IsAgentSource && _host.Settings.AgentOfflineOnly ? Strings.AgentOfflineExplanation : ReasonText(CurrentRunReason());'),
('public bool CanRunSource => _source != AutoLabelSource.SegmentModel && CurrentRunReason() == AutoLabelReason.None;', 'public bool CanRunSource => _source != AutoLabelSource.SegmentModel && (!IsAgentSource || !_host.Settings.AgentOfflineOnly) && CurrentRunReason() == AutoLabelReason.None;'),
('        _agent.Tool = _host.AgentBackend;\n        _agent.Options', '        _agent.Tool = _host.AgentBackend;\n        _agent.IsOffline = _host.Settings.AgentOfflineOnly;\n        _agent.Options'),
('    private string AgentNotFoundText()', '''    internal void RefreshAgentOfflineState()
    {
        bool offline = _host.Settings.AgentOfflineOnly;
        if (_agent != null)
        {
            _agent.IsOffline = offline;
            if (offline) _agent.CancelAndKill();
        }

        foreach (AgentRunner runner in _batchWorkerRunners)
        {
            runner.IsOffline = offline;
            if (offline) runner.CancelAndKill();
        }

        ++_modelLoad;
        if (offline) AiStatusText = Strings.AgentOfflineExplanation;
        OnPropertyChanged(nameof(SourceReasonText));
        OnPropertyChanged(nameof(HasSourceReason));
        OnPropertyChanged(nameof(CanRunSource));
        RefreshState();
    }

    private bool RefuseOfflineAgent()
    {
        if (!_host.Settings.AgentOfflineOnly) return false;
        AiStatusText = Strings.AgentOfflineExplanation;
        return true;
    }

    private string AgentNotFoundText()'''),
('    private async Task RunAgentCoreAsync(bool bypassCache, bool check = false)\n    {', '    private async Task RunAgentCoreAsync(bool bypassCache, bool check = false)\n    {\n        if (RefuseOfflineAgent()) return;'),
('    private async Task RunBatchAsync(string? mode)\n    {', '    private async Task RunBatchAsync(string? mode)\n    {\n        if (RefuseOfflineAgent()) return;'),
('    private async Task<bool> EnsureAgentConsentAsync()\n    {', '    private async Task<bool> EnsureAgentConsentAsync()\n    {\n        if (RefuseOfflineAgent()) return false;'),
('    private static void ConfigureSpend(AgentRunner runner, AgentSpendBudget budget, AgentSpendSnapshot snapshot)', '    private async Task ConfigureSpend(AgentRunner runner, AgentSpendBudget budget, AgentSpendSnapshot snapshot)'),
('        runner.Prices = snapshot.Prices;\n    }', '''        runner.Prices = snapshot.Prices;
        runner.IsOffline = _host.Settings.AgentOfflineOnly;
        runner.MaxBudgetFlagSupported = false;
        if (!runner.IsOffline && runner.Tool == AgentTool.Claude && string.IsNullOrWhiteSpace(runner.FakeAgentScript))
        {
            AgentCliCapabilities capabilities = new AgentCliCapabilities(() => _host.Settings.AgentOfflineOnly);
            runner.MaxBudgetFlagSupported = await capabilities.ProbeAsync(runner.FindClaude());
        }

        runner.IsOffline = _host.Settings.AgentOfflineOnly;
        if (runner.IsOffline) runner.MaxBudgetFlagSupported = false;
    }'''),
('            ConfigureSpend(agent, runBudget, snapshot);', '            await ConfigureSpend(agent, runBudget, snapshot);'),
('        ConfigureSpend(agent, activeBatchBudget, snapshot);', '        await ConfigureSpend(agent, activeBatchBudget, snapshot);'),
('            ConfigureSpend(extra, activeBatchBudget, snapshot);', '            await ConfigureSpend(extra, activeBatchBudget, snapshot);'),
('                ConfigureSpend(retryRunner, activeBatchBudget, snapshot);', '                await ConfigureSpend(retryRunner, activeBatchBudget, snapshot);'),
('            ConfigureSpend(agent, suggestBudget, snapshot);', '            await ConfigureSpend(agent, suggestBudget, snapshot);'),
('    public async Task LoadAgentModelsAsync(bool force = false)\n    {', '''    public async Task LoadAgentModelsAsync(bool force = false)
    {
        if (_host.Settings.AgentOfflineOnly)
        {
            FillModels(Array.Empty<AgentModelInfo>());
            return;
        }'''),
('        return AutoLabelSourceRules.CanSuggestPrompt(', '        return !_host.Settings.AgentOfflineOnly && AutoLabelSourceRules.CanSuggestPrompt('),
('    private async Task SuggestPromptAsync()\n    {', '    private async Task SuggestPromptAsync()\n    {\n        if (RefuseOfflineAgent()) return;')])

edit(p+'ViewModels/LabelStepViewModel.AgentChat.cs', [
('    private async Task RunChatAsync()\n    {', '    private async Task RunChatAsync()\n    {\n        if (RefuseOfflineAgent()) return;'),
('    private async Task<bool> RefineChatPromptAsync()\n    {', '    private async Task<bool> RefineChatPromptAsync()\n    {\n        if (RefuseOfflineAgent()) return false;'),
('            ConfigureSpend(agent, refineBudget, snapshot);', '            await ConfigureSpend(agent, refineBudget, snapshot);'),
('    private bool CanRunCheck()\n    {\n        return _source', '    private bool CanRunCheck()\n    {\n        return !_host.Settings.AgentOfflineOnly && _source')])

(scratch/'edited.json').write_text(json.dumps(changed), encoding='utf-8')
