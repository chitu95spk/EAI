using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EasyAI.Desktop.Infrastructure;
using EasyAI.Desktop.Resources;
using EasyAI.Desktop.Services;
using EasyAI.Desktop.Services.Agent;
using InfoBarSeverity = Wpf.Ui.Controls.InfoBarSeverity;

namespace EasyAI.Desktop.ViewModels;

/// <summary>One run-skill row: name, description, source caption, the on / off switch (built-in and user skills) and Edit / Delete (user skills).</summary>
public sealed class RunSkillItemViewModel : ObservableObject
{
    private readonly RunSkillsViewModel _owner;
    private bool _isOn;

    public RunSkillItemViewModel(RunSkillsViewModel owner, RunSkillInfo info)
    {
        _owner = owner;
        Info = info;
        _isOn = info.Enabled;
    }

    public RunSkillInfo Info { get; }

    public string Name => Info.Name;

    public string Description => Info.Description;

    public bool HasDescription => Info.Description.Length > 0;

    /// <summary>Built-in and user skills have the switch; a skill of the agent program's own folder is only listed.</summary>
    public bool CanToggle => !Info.ReadOnly;

    public bool IsReadOnly => Info.ReadOnly;

    public bool IsUser => Info.Kind == RunSkillKind.User;

    public string SourceText
    {
        get
        {
            switch (Info.Kind)
            {
                case RunSkillKind.User:
                    return Strings.AgentsSkillUser;
                case RunSkillKind.Home:
                    return Strings.AgentsSkillReadOnly;
                default:
                    return Strings.AgentsSkillBuiltIn;
            }
        }
    }

    /// <summary>The Enable switch: the name goes into (or out of) the disabled list of the app settings.</summary>
    public bool IsOn
    {
        get => _isOn;
        set
        {
            if (_isOn == value || Info.ReadOnly)
            {
                return;
            }

            _isOn = value;
            OnPropertyChanged();
            _owner.SetEnabled(Info.Name, value);
        }
    }
}

/// <summary>
/// The skills of an EasyAI run on the Settings page: built-in and user-folder skills with an Enable switch (the disabled names are kept in
/// <see cref="EasyAI.Desktop.Infrastructure.AppSettings.DisabledRunSkills"/>), and the agent program's own skill folders listed read-only.
/// </summary>
public sealed partial class RunSkillsViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private readonly IAgentEnvironment _env;
    private readonly AgentSkillStore _store;

    public RunSkillsViewModel(MainViewModel main, IAgentEnvironment env)
    {
        _main = main;
        _env = env;
        _store = new AgentSkillStore(RunSkillCatalog.DefaultUserRoot());
        Reload();
    }

    public ObservableCollection<RunSkillItemViewModel> Items { get; } = new ObservableCollection<RunSkillItemViewModel>();

    public bool HasNoSkills => Items.Count == 0;

    /// <summary>Folder of the user skills (shown as a tooltip so the user can find the files).</summary>
    public string UserFolder => _store.Root;

    public void Reload()
    {
        Items.Clear();
        IReadOnlyList<string> homeRoots = new[] { AgentSkillRoots.For(AgentTool.Claude, _env), AgentSkillRoots.For(AgentTool.Codex, _env) };
        foreach (RunSkillInfo info in RunSkillCatalog.List(_env, McpLocator.AgentFolder(AppContext.BaseDirectory), _store.Root, _main.Settings.DisabledRunSkills, homeRoots))
        {
            Items.Add(new RunSkillItemViewModel(this, info));
        }

        OnPropertyChanged(nameof(HasNoSkills));
    }

    internal void SetEnabled(string name, bool enabled)
    {
        List<string> disabled = _main.Settings.DisabledRunSkills ?? new List<string>();
        disabled.RemoveAll(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
        if (!enabled)
        {
            disabled.Add(name);
        }

        _main.Settings.DisabledRunSkills = disabled;
        _main.PersistSettings();
    }

    [RelayCommand]
    private void AddSkill()
    {
        IDialogService? dialogs = _main.Dialogs;
        if (dialogs == null)
        {
            return;
        }

        SkillEditResult? result = dialogs.EditSkill(string.Format(Strings.AgentsSkillAddTitleFormat, Strings.AgentsRunSkills), string.Empty, AgentSkillStore.Template(string.Empty, string.Empty), true);
        if (result == null)
        {
            return;
        }

        try
        {
            _store.Create(result.Name, AgentSkillStore.WithName(result.Text, result.Name));
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException)
        {
            Notice(ex.Message);
        }

        Reload();
    }

    [RelayCommand]
    private void EditSkill(RunSkillItemViewModel? item)
    {
        IDialogService? dialogs = _main.Dialogs;
        if (item == null || !item.IsUser || dialogs == null)
        {
            return;
        }

        try
        {
            AgentSkillEntry entry = EntryOf(item);
            SkillEditResult? result = dialogs.EditSkill(string.Format(Strings.AgentsSkillEditTitleFormat, item.Name), item.Name, _store.ReadText(entry), false);
            if (result != null)
            {
                _store.WriteText(entry, result.Text);
                Reload();
            }
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException)
        {
            Notice(ex.Message);
        }
    }

    [RelayCommand]
    private void DeleteSkill(RunSkillItemViewModel? item)
    {
        IDialogService? dialogs = _main.Dialogs;
        if (item == null || !item.IsUser || dialogs == null || !dialogs.ConfirmDeleteSkill(item.Name))
        {
            return;
        }

        try
        {
            _store.Delete(EntryOf(item));
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException)
        {
            Notice(ex.Message);
        }

        Reload();
    }

    private static AgentSkillEntry EntryOf(RunSkillItemViewModel item)
    {
        return new AgentSkillEntry(item.Name, item.Description, System.IO.Path.GetDirectoryName(item.Info.File) ?? string.Empty, true);
    }

    private void Notice(string message)
    {
        _main.ShowDataNotice(Strings.AgentsRunSkills, string.Format(Strings.AgentsFailedFormat, message), InfoBarSeverity.Warning);
    }
}

/// <summary>One external MCP server row: name, version, approved / not approved, and Approve (while not approved), Edit, Remove.</summary>
public sealed class McpServerItemViewModel
{
    public McpServerItemViewModel(McpServerEntry entry)
    {
        Entry = entry;
        IsApproved = McpServerRegistry.IsApproved(entry);
    }

    public McpServerEntry Entry { get; }

    public string Name => Entry.Name;

    public string VersionText => string.Format(Strings.McpVersionFormat, Entry.Version);

    public bool IsApproved { get; }

    public bool CanApprove => !IsApproved;

    public string StateText => IsApproved ? Strings.McpStateApproved : Strings.McpStateNotApproved;
}

/// <summary>
/// The external MCP servers on the Settings page (<see cref="McpServerRegistry"/>). The app only keeps the list: nothing is downloaded,
/// installed or started here, and a server is used by a run only after the user clicked Approve in the approval dialog.
/// </summary>
public sealed partial class McpServersViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private readonly string _path;

    public McpServersViewModel(MainViewModel main, string? path = null)
    {
        _main = main;
        _path = string.IsNullOrWhiteSpace(path) ? McpServerRegistry.DefaultPath() : path!;
        Reload();
    }

    public ObservableCollection<McpServerItemViewModel> Items { get; } = new ObservableCollection<McpServerItemViewModel>();

    public bool HasNoServers => Items.Count == 0;

    public void Reload()
    {
        Items.Clear();
        foreach (McpServerEntry entry in McpServerRegistry.Load(_path).Entries)
        {
            Items.Add(new McpServerItemViewModel(entry));
        }

        OnPropertyChanged(nameof(HasNoServers));
    }

    [RelayCommand]
    private void AddServer()
    {
        IDialogService? dialogs = _main.Dialogs;
        McpServerEntry? entry = dialogs?.EditMcpServer(Strings.McpAddTitle, null);
        if (entry == null)
        {
            return;
        }

        Apply(registry => registry.Add(entry));
    }

    [RelayCommand]
    private void EditServer(McpServerItemViewModel? item)
    {
        IDialogService? dialogs = _main.Dialogs;
        if (item == null || dialogs == null)
        {
            return;
        }

        McpServerEntry? entry = dialogs.EditMcpServer(string.Format(Strings.McpEditTitleFormat, item.Name), item.Entry);
        if (entry != null)
        {
            // Update clears the stored approval, whatever was edited.
            Apply(registry => registry.Update(item.Name, entry));
        }
    }

    [RelayCommand]
    private void RemoveServer(McpServerItemViewModel? item)
    {
        IDialogService? dialogs = _main.Dialogs;
        if (item == null || dialogs == null || !dialogs.Confirm(Strings.McpServers, string.Format(Strings.McpRemoveMessageFormat, item.Name), Strings.McpRemove))
        {
            return;
        }

        Apply(registry => registry.Remove(item.Name));
    }

    [RelayCommand]
    private void ApproveServer(McpServerItemViewModel? item)
    {
        IDialogService? dialogs = _main.Dialogs;
        if (item == null || dialogs == null)
        {
            return;
        }

        // Show the entry as it is on disk now, so the user approves exactly what the hash will cover.
        McpServerEntry? current = McpServerRegistry.Load(_path).Find(item.Name);
        if (current == null || !McpServerRegistry.IsComplete(current) || !dialogs.ApproveMcpServer(current))
        {
            return;
        }

        Apply(registry => registry.Approve(current.Name));
    }

    private void Apply(Action<McpServerRegistry> change)
    {
        try
        {
            change(McpServerRegistry.Load(_path));
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException)
        {
            _main.ShowDataNotice(Strings.McpServers, string.Format(Strings.AgentsFailedFormat, ex.Message), InfoBarSeverity.Warning);
        }

        Reload();
    }
}

/// <summary>Settings section of one agent (Claude Code or Codex): account status with Sign in / Sign out.</summary>
public sealed partial class AgentSectionViewModel : ObservableObject
{
    private readonly AgentTool _tool;
    private readonly AgentAuthService _auth;
    private readonly Action<AgentTool> _use;
    private readonly Func<bool> _isOffline;
    private CancellationTokenSource? _loginCts;
    private bool _isInUse;

    [ObservableProperty]
    private string _statusText = Strings.AgentsChecking;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _isInstalled = true;

    [ObservableProperty]
    private bool _isSignedIn;

    /// <summary>True while a sign-in waits for the browser (the "Stop waiting" button shows).</summary>
    [ObservableProperty]
    private bool _isWaitingLogin;

    public AgentSectionViewModel(AgentTool tool, string title, AgentAuthService auth, Action<AgentTool> use, Func<bool>? isOffline = null)
    {
        _use = use;
        _isOffline = isOffline ?? (() => false);
        _tool = tool;
        Title = title;
        _auth = auth;
    }

    public string Title { get; }

    /// <summary>The "Use this agent" choice: only one of the two agents is in use (Run and Suggest prompt call it).</summary>
    public bool IsInUse
    {
        get => _isInUse;
        set
        {
            if (value && !_isInUse)
            {
                _isInUse = true;
                OnPropertyChanged();
                OnPropertyChanged(nameof(UseButtonText));
                _use(_tool);
            }
            else if (!value && _isInUse)
            {
                // A click on the agent in use keeps it selected (radio behaviour): put the check mark back.
                OnPropertyChanged();
            }
        }
    }

    public string UseButtonText => _isInUse ? Strings.AgentsInUse : Strings.AgentsUseThis;

    internal void SetInUse(bool value)
    {
        _isInUse = value;
        OnPropertyChanged(nameof(IsInUse));
        OnPropertyChanged(nameof(UseButtonText));
    }

    public string Id => _tool == AgentTool.Claude ? "Claude" : "Codex";

    public bool CanSignIn => !_isOffline() && IsInstalled && !IsSignedIn;

    public bool CanSignOut => !_isOffline() && IsInstalled && IsSignedIn;

    partial void OnIsInstalledChanged(bool value) => RaiseButtons();

    partial void OnIsSignedInChanged(bool value) => RaiseButtons();

    partial void OnIsBusyChanged(bool value)
    {
        LoginCommand.NotifyCanExecuteChanged();
        LogoutCommand.NotifyCanExecuteChanged();
        RefreshCommand.NotifyCanExecuteChanged();
        CancelLoginCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(IsNotBusy));
    }

    public bool IsNotBusy => !IsBusy;

    private void RaiseButtons()
    {
        OnPropertyChanged(nameof(CanSignIn));
        OnPropertyChanged(nameof(CanSignOut));
        LoginCommand.NotifyCanExecuteChanged();
        LogoutCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Reads the sign-in state (the first time the page is shown and on Refresh).</summary>
    [RelayCommand(CanExecute = nameof(IsNotBusy))]
    public async Task RefreshAsync()
    {
        if (RefuseOffline()) return;
        IsBusy = true;
        StatusText = Strings.AgentsChecking;
        try
        {
            Apply(await _auth.StatusAsync(_tool, CancellationToken.None));
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanLogin() => IsNotBusy && CanSignIn;

    [RelayCommand(CanExecute = nameof(CanLogin))]
    private async Task LoginAsync()
    {
        if (RefuseOffline()) return;
        _loginCts = new CancellationTokenSource();
        IsBusy = true;
        IsWaitingLogin = true;
        CancelLoginCommand.NotifyCanExecuteChanged();
        StatusText = Strings.AgentsLoginWaiting;
        try
        {
            Apply(await _auth.LoginAsync(_tool, _loginCts.Token));
        }
        finally
        {
            _loginCts.Dispose();
            _loginCts = null;
            IsWaitingLogin = false;
            IsBusy = false;
        }
    }

    private bool CanCancelLogin() => IsBusy && _loginCts != null;

    /// <summary>Stops a sign-in that waits for the browser.</summary>
    [RelayCommand(CanExecute = nameof(CanCancelLogin))]
    private void CancelLogin()
    {
        _loginCts?.Cancel();
    }

    private bool CanLogout() => IsNotBusy && CanSignOut;

    [RelayCommand(CanExecute = nameof(CanLogout))]
    private async Task LogoutAsync()
    {
        if (RefuseOffline()) return;
        IsBusy = true;
        try
        {
            Apply(await _auth.LogoutAsync(_tool, CancellationToken.None));
        }
        finally
        {
            IsBusy = false;
        }
    }

    internal void RefreshOfflineState()
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

    private void Apply(AgentAuthStatus status)
    {
        if (RefuseOffline()) return;
        IsInstalled = status.State != AgentAuthState.NotInstalled;
        IsSignedIn = status.State == AgentAuthState.SignedIn;
        switch (status.State)
        {
            case AgentAuthState.NotInstalled:
                StatusText = Strings.AgentsNotInstalled;
                break;
            case AgentAuthState.SignedIn:
                StatusText = status.Detail.Length > 0 ? string.Format(Strings.AgentsSignedInFormat, status.Detail) : Strings.AgentsSignedIn;
                break;
            default:
                StatusText = Strings.AgentsSignedOut;
                break;
        }
    }

}

/// <summary>The Claude Code &amp; Codex page of the Settings window.</summary>
/// <summary>
/// Model prices of Claude Code and Codex. Neither vendor has a price API, so Refresh reads the tables of their pricing pages and keeps
/// them in a file; the agent uses them for the cost estimate of a batch.
/// </summary>
public sealed partial class AgentPricesViewModel : ObservableObject
{
    private readonly Func<bool> _isOffline;
    private static readonly System.Net.Http.HttpClient Http = CreateClient();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    private bool _isRefreshing;

    [ObservableProperty]
    private string _statusText = string.Empty;

    public AgentPricesViewModel(Func<bool>? isOffline = null)
    {
        _isOffline = isOffline ?? (() => false);
        StatusText = _isOffline() ? Strings.AgentOfflineExplanation : Describe(AgentPriceStore.Load(), null);
    }

    public string TitleText => Text("AgentsPrices", "Model prices");

    public string HintText => Text("AgentsPricesHint", "Claude Code and Codex have no price API. Refresh reads the price tables of platform.claude.com and developers.openai.com so the agent can estimate the cost of a batch.");

    public string RefreshText => Text("AgentsPricesRefresh", "Refresh price");

    private static System.Net.Http.HttpClient CreateClient()
    {
        System.Net.Http.HttpClient client = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) EasyAI");
        return client;
    }

    private static string Text(string key, string english)
    {
        return SettingsCatalog.Localized(Strings.ByName, key, english);
    }

    internal void RefreshOfflineState()
    {
        StatusText = _isOffline() ? Strings.AgentOfflineExplanation : Describe(AgentPriceStore.Load(), null);
    }

    private bool CanRefresh() => !IsRefreshing;

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private async Task RefreshAsync()
    {
        if (_isOffline())
        {
            StatusText = Strings.AgentOfflineExplanation;
            return;
        }

        IsRefreshing = true;
        StatusText = Text("AgentsPricesBusy", "Reading the pricing pages...");
        try
        {
            AgentPriceRefreshResult result = await AgentPriceStore.RefreshAsync(Http, null, CancellationToken.None, _isOffline);
            StatusText = _isOffline() ? Strings.AgentOfflineExplanation : Describe(AgentPriceStore.Load(), result);
        }
        catch (Exception ex)
        {
            StatusText = _isOffline() ? Strings.AgentOfflineExplanation : string.Format(System.Globalization.CultureInfo.CurrentCulture, Text("AgentsPricesFailed", "Refresh failed: {0}"), ex.Message);
        }
        finally
        {
            IsRefreshing = false;
        }
    }

    private static string Describe(AgentPriceTable table, AgentPriceRefreshResult? result)
    {
        string stored = table.IsEmpty
            ? Text("AgentsPricesNone", "No prices stored yet.")
            : string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                Text("AgentsPricesStored", "{0} Claude and {1} OpenAI models, read {2:g}."),
                table.Count(AgentPriceTable.ClaudeProvider),
                table.Count(AgentPriceTable.OpenAiProvider),
                table.FetchedUtc.ToLocalTime());
        return result?.Error == null ? stored : stored + " " + result.Error;
    }
}

/// <summary>
/// The prompt sub-agent: a short text-only run that rewrites what the person typed into a clear English command for the labelling agent
/// and explains it back in the language of the UI. This block switches it on (the chat "Auto-edit prompt" button shows the same switch)
/// and holds an optional extra instruction for it.
/// </summary>
public sealed class PromptSubAgentViewModel : ObservableObject
{
    private readonly MainViewModel _main;

    public PromptSubAgentViewModel(MainViewModel main)
    {
        _main = main;
    }

    public string TitleText => Text("AgentsRefine", "Prompt sub-agent");

    public string HintText => Text("AgentsRefineHint", "Before the main agent starts, a short sub-agent run checks what you typed, turns it into a clear command the agent understands, translates it to English when needed and explains it back to you in the language of the app.");

    public string EnableText => Text("AgentsRefineEnable", "Auto-edit my prompt before sending");

    public string InstructionCaption => Text("AgentsRefineInstruction", "Extra instruction for the sub-agent (optional)");

    public bool Enabled
    {
        get => _main.Settings.AgentAutoRefinePrompt;
        set
        {
            if (_main.Settings.AgentAutoRefinePrompt == value)
            {
                return;
            }

            // The label step view model owns the switch (the chat button binds to it), so both places stay in step.
            if (_main.Label != null)
            {
                _main.Label.AutoRefinePrompt = value;
            }
            else
            {
                _main.Settings.AgentAutoRefinePrompt = value;
                _main.PersistSettings();
            }

            OnPropertyChanged();
        }
    }

    public string Instruction
    {
        get => _main.Settings.AgentRefineHint ?? string.Empty;
        set
        {
            string next = (value ?? string.Empty).Trim();
            if (string.Equals(_main.Settings.AgentRefineHint ?? string.Empty, next, StringComparison.Ordinal))
            {
                return;
            }

            _main.Settings.AgentRefineHint = next;
            _main.PersistSettings();
            OnPropertyChanged();
        }
    }

    private static string Text(string key, string english)
    {
        return SettingsCatalog.Localized(Strings.ByName, key, english);
    }
}

public sealed class AgentsSettingsViewModel : ObservableObject
{
    public AgentsSettingsViewModel(MainViewModel main)
    {
        _main = main;
        IAgentEnvironment env = new SystemAgentEnvironment();
        AgentAuthService auth = new AgentAuthService(new AgentOfflineEnvironment(env, () => main.Settings.AgentOfflineOnly));
        Claude = new AgentSectionViewModel(AgentTool.Claude, Strings.AgentsClaude, auth, UseAgent, () => main.Settings.AgentOfflineOnly);
        Codex = new AgentSectionViewModel(AgentTool.Codex, Strings.AgentsCodex, auth, UseAgent, () => main.Settings.AgentOfflineOnly);
        Sections = new[] { Claude, Codex };
        Claude.SetInUse(main.AgentBackend == AgentTool.Claude);
        Codex.SetInUse(main.AgentBackend == AgentTool.Codex);
        Memory = new AgentMemoryViewModel(main);
        RunSkills = new RunSkillsViewModel(main, env);
        McpServers = new McpServersViewModel(main);
        Vocabulary = new VocabularyViewModel(main);
        Prices = new AgentPricesViewModel(() => main.Settings.AgentOfflineOnly);
        PromptSubAgent = new PromptSubAgentViewModel(main);
        RefreshOfflineState();
    }

    private bool? _lastOffline;

    internal void RefreshOfflineState()
    {
        bool offline = _main.Settings.AgentOfflineOnly;
        if (_lastOffline == offline) return;
        bool wasOffline = _lastOffline == true;
        _lastOffline = offline;
        Claude.RefreshOfflineState();
        Codex.RefreshOfflineState();
        Prices.RefreshOfflineState();
        OnPropertyChanged(nameof(AgentOfflineOnly));
        if (wasOffline && !offline) _ = Task.WhenAll(Claude.RefreshAsync(), Codex.RefreshAsync());
    }

    public string OfflineCaption => Strings.AgentOfflineOnly;

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
            RefreshOfflineState();
        }
    }

    /// <summary>The "Refresh price" block under the two agents.</summary>
    public AgentPricesViewModel Prices { get; }

    /// <summary>The prompt sub-agent block (switch and extra instruction).</summary>
    public PromptSubAgentViewModel PromptSubAgent { get; }

    /// <summary>The words this project learned from the user's prompts: list, Delete per word and Clear.</summary>
    public VocabularyViewModel Vocabulary { get; }

    /// <summary>The external MCP servers: list, Add / Edit / Remove and the Approve dialog.</summary>
    public McpServersViewModel McpServers { get; }

    private readonly MainViewModel _main;

    /// <summary>The EasyAI run skills (built-in and user folder, with Enable) and the agent program's own skill folders (read-only).</summary>
    public RunSkillsViewModel RunSkills { get; }

    // Picking one agent clears the other: the app calls only the chosen one.
    private void UseAgent(AgentTool tool)
    {
        Claude.SetInUse(tool == AgentTool.Claude);
        Codex.SetInUse(tool == AgentTool.Codex);
        _main.SetAgentBackend(tool);
    }

    /// <summary>The project memory block under the two agents.</summary>
    public AgentMemoryViewModel Memory { get; }

    public AgentSectionViewModel Claude { get; }

    public AgentSectionViewModel Codex { get; }

    public IReadOnlyList<AgentSectionViewModel> Sections { get; }

    /// <summary>Checks both sign-ins (run once when the page is first shown).</summary>
    public async Task RefreshAsync()
    {
        Memory.Reload();
        RunSkills.Reload();
        McpServers.Reload();
        Vocabulary.Reload();
        await Task.WhenAll(Claude.RefreshAsync(), Codex.RefreshAsync());
    }
}

/// <summary>One learned word row: the word, the class it stands for and its position (for the automation ids).</summary>
public sealed class VocabularyItemViewModel
{
    public VocabularyItemViewModel(int index, EasyLabel.Core.Io.VocabularyEntry entry)
    {
        Index = index;
        Entry = entry;
    }

    public int Index { get; }

    public EasyLabel.Core.Io.VocabularyEntry Entry { get; }

    public string Term => Entry.Term;

    /// <summary>"quat" -> FAN</summary>
    public string DisplayText => "\"" + Entry.Term + "\"  ->  " + Entry.Label;
}

/// <summary>
/// The project vocabulary (<c>.easyai/vocabulary.json</c>) on the Settings page: the words the agent prompt uses for classes, each with a Delete
/// button, and Clear. Texts use <c>Strings.ByName</c> with an English fallback until the resources carry them.
/// </summary>
public sealed partial class VocabularyViewModel : ObservableObject
{
    private readonly MainViewModel _main;

    [ObservableProperty]
    private bool _hasItems;

    [ObservableProperty]
    private bool _hasProject;

    public VocabularyViewModel(MainViewModel main)
    {
        _main = main;
        Reload();
    }

    public ObservableCollection<VocabularyItemViewModel> Items { get; } = new ObservableCollection<VocabularyItemViewModel>();

    public bool HasNoItems => Items.Count == 0;

    public string TitleText => Text("AgentsVocabulary", "Project vocabulary");

    public string EmptyText => Text("AgentsVocabularyEmpty", "No words learned yet. A word is learned after you accepted its class from three prompts that used it.");

    public string DeleteText => Text("AgentsVocabularyDelete", "Delete");

    public string ClearText => Text("AgentsVocabularyClear", "Clear");

    private string? FilePath => _main.Project == null ? null : _main.Project.Paths.VocabularyFile;

    private static string Text(string key, string english)
    {
        string text = Strings.ByName(key);
        return string.Equals(text, key, StringComparison.Ordinal) ? english : text;
    }

    public void Reload()
    {
        string? path = FilePath;
        HasProject = path != null;
        Items.Clear();
        int index = 0;
        foreach (EasyLabel.Core.Io.VocabularyEntry entry in EasyLabel.Core.Io.ProjectVocabulary.Learned(path))
        {
            Items.Add(new VocabularyItemViewModel(index++, entry));
        }

        HasItems = Items.Count > 0;
        OnPropertyChanged(nameof(HasNoItems));
        ClearCommand.NotifyCanExecuteChanged();
    }

    private bool CanClear() => HasProject && HasItems;

    [RelayCommand]
    private void DeleteTerm(VocabularyItemViewModel? item)
    {
        string? path = FilePath;
        if (item == null || path == null)
        {
            return;
        }

        try
        {
            EasyLabel.Core.Io.ProjectVocabulary.Remove(path, item.Term);
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            _main.ShowDataNotice(TitleText, string.Format(Strings.AgentsFailedFormat, ex.Message), InfoBarSeverity.Warning);
        }

        Reload();
    }

    [RelayCommand(CanExecute = nameof(CanClear))]
    private void Clear()
    {
        string? path = FilePath;
        IDialogService? dialogs = _main.Dialogs;
        if (path == null || dialogs == null
            || !dialogs.Confirm(TitleText, Text("AgentsVocabularyClearMessage", "Forget all words this project learned from your prompts?"), ClearText))
        {
            return;
        }

        try
        {
            EasyLabel.Core.Io.ProjectVocabulary.Clear(path);
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            _main.ShowDataNotice(TitleText, string.Format(Strings.AgentsFailedFormat, ex.Message), InfoBarSeverity.Warning);
        }

        Reload();
    }
}

/// <summary>The agent memory of the open project (<c>.easyai/agent-memory.md</c>): what the agent has noted, with Edit and Clear.</summary>
public sealed partial class AgentMemoryViewModel : ObservableObject
{
    private readonly MainViewModel _main;

    [ObservableProperty]
    private string _summaryText = string.Empty;

    [ObservableProperty]
    private bool _hasProject;

    public AgentMemoryViewModel(MainViewModel main)
    {
        _main = main;
        Reload();
    }

    private string? FilePath => _main.Project == null ? null : _main.Project.Paths.AgentMemoryFile;

    public void Reload()
    {
        string? path = FilePath;
        HasProject = path != null;
        if (path == null)
        {
            SummaryText = Strings.AgentsMemoryNoProject;
        }
        else
        {
            string text = EasyLabel.Core.Io.AgentMemoryFile.Read(path);
            int notes = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Count(l => l.Trim().Length > 0);
            SummaryText = notes == 0 ? Strings.AgentsMemoryEmpty : string.Format(Strings.AgentsMemorySizeFormat, notes, text.Length);
        }

        EditCommand.NotifyCanExecuteChanged();
        ClearCommand.NotifyCanExecuteChanged();
    }

    private bool CanUse() => HasProject;

    [RelayCommand(CanExecute = nameof(CanUse))]
    private void Edit()
    {
        string? path = FilePath;
        IDialogService? dialogs = _main.Dialogs;
        if (path == null || dialogs == null)
        {
            return;
        }

        SkillEditResult? result = dialogs.EditSkill(Strings.AgentsMemoryEditTitle, "agent-memory.md", EasyLabel.Core.Io.AgentMemoryFile.Read(path), false);
        if (result == null)
        {
            return;
        }

        try
        {
            EasyLabel.Core.Io.AgentMemoryFile.Write(path, result.Text);
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            _main.ShowDataNotice(Strings.AgentsMemory, string.Format(Strings.AgentsFailedFormat, ex.Message), InfoBarSeverity.Warning);
        }

        Reload();
    }

    [RelayCommand(CanExecute = nameof(CanUse))]
    private void Clear()
    {
        string? path = FilePath;
        IDialogService? dialogs = _main.Dialogs;
        if (path == null || dialogs == null || !dialogs.Confirm(Strings.AgentsMemory, Strings.AgentsMemoryClearMessage, Strings.AgentsMemoryClear))
        {
            return;
        }

        try
        {
            EasyLabel.Core.Io.AgentMemoryFile.Clear(path);
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            _main.ShowDataNotice(Strings.AgentsMemory, string.Format(Strings.AgentsFailedFormat, ex.Message), InfoBarSeverity.Warning);
        }

        Reload();
    }
}
