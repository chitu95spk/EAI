using System.Collections.ObjectModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using EasyAI.Desktop.Resources;
using EasyAI.Desktop.Services;
using EasyAI.Desktop.Services.Agent;
using EasyLabel.Core.Io;

namespace EasyAI.Desktop.ViewModels;

/// <summary>Agent chat window state: history rows, the image mode, the caps caption and the Run / Check / open commands.</summary>
public sealed partial class LabelStepViewModel
{
    /// <summary>The newest history rows of the project, oldest first (the last one is the newest). Rebuilt by <see cref="ReloadChat"/>.</summary>
    public ObservableCollection<AgentChatEntryViewModel> AgentChatEntries { get; } = new ObservableCollection<AgentChatEntryViewModel>();

    /// <summary>The chat button was pressed: the main window shows (or raises) the chat window.</summary>
    public event EventHandler? AgentChatOpenRequested;

    /// <summary>The history rows were rebuilt: the window scrolls to the newest row.</summary>
    public event EventHandler? AgentChatReloaded;

    /// <summary>Reads the last rows of the project's chat history (none without a project) into <see cref="AgentChatEntries"/>.</summary>
    internal void ReloadChat()
    {
        string? chatFile = _host.Project?.Paths.AgentChatFile;
        if (!string.Equals(chatFile, _chatNoticeFile, StringComparison.OrdinalIgnoreCase))
        {
            _chatNotices.Clear();
            _chatNoticeFile = chatFile;
            _sessionScannedRels.Clear();
            ResetBatchReview();
        }

        // The whole kept history feeds the Remaining gate; only the newest rows are shown.
        IReadOnlyList<AgentChatEntry> entries = AgentChatLog.ReadLast(chatFile, AgentChatLog.MaxEntries);
        _chatHistory = entries;
        AgentChatLabels labels = AgentChatLabelsFactory.FromStrings();
        List<AgentChatEntryViewModel> rows = new List<AgentChatEntryViewModel>();
        for (int e = Math.Max(0, entries.Count - AgentChatLog.DefaultReadCount); e < entries.Count; e++)
        {
            rows.Add(new AgentChatEntryViewModel(entries[e], labels));
        }

        // One continuous session: the system lines of slash commands stay in the view, in time order, until /clear or the next project.
        foreach (AgentChatEntryViewModel notice in _chatNotices)
        {
            int at = rows.Count;
            while (at > 0 && rows[at - 1].Entry.Time > notice.Entry.Time)
            {
                at--;
            }

            rows.Insert(at, notice);
        }

        // The running agent's live row is session-only: it is always the newest row and is dropped when the final entry is written.
        if (_liveRow != null)
        {
            rows.Add(_liveRow);
        }

        int skip = Math.Max(0, rows.Count - AgentChatLog.DefaultReadCount);
        AgentChatEntries.Clear();
        for (int i = skip; i < rows.Count; i++)
        {
            AgentChatEntries.Add(rows[i]);
        }

        EnsurePillHooks();
        OnPropertyChanged(nameof(AgentPillText));
        OpenAgentChatCommand.NotifyCanExecuteChanged();
        RefreshChatModeGate();
        AgentChatReloaded?.Invoke(this, EventArgs.Empty);
    }

    // ----- live transcript (F9): one growing agent row per run or batch, replaced by the final history entry when the run ends -----
    private AgentChatEntryViewModel? _liveRow;
    private AgentLiveFeed? _liveFeed;
    private AgentLiveLabels? _liveLabels;
    private readonly AgentLiveThrottle _liveThrottle = new AgentLiveThrottle();
    private readonly System.Diagnostics.Stopwatch _liveClock = System.Diagnostics.Stopwatch.StartNew();
    private System.Windows.Threading.DispatcherTimer? _liveTimer;
    private bool _liveDirty;
    private int _liveImageIndex;
    private int _liveImageTotal;
    private int _liveStepBase;
    private double _liveBatchCost;
    private double? _liveCostCap;
    private string? _liveLastRel;

    /// <summary>The compact status line of a running agent ("Working... $0.012 - step 3"); empty when nothing runs. The chat window shows it instead of a progress bar.</summary>
    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
    private string _agentLiveStatusText = string.Empty;

    public bool HasAgentLiveStatus => AgentLiveStatusText.Length > 0;

    partial void OnAgentLiveStatusTextChanged(string value)
    {
        OnPropertyChanged(nameof(HasAgentLiveStatus));
    }

    // Starts the live row. imageTotal = 0 for a single run; costCap is the batch cost cap (null = none). The returned progress posts to the
    // UI thread (it is created here, on the UI thread), so every handler below runs on the UI thread.
    private IProgress<AgentStreamEvent> BeginLive(int imageTotal, double? costCap)
    {
        EndLive();
        _liveLabels = AgentLiveLabels.FromLookup(SlashText);
        AgentLiveFeed feed = new AgentLiveFeed(_liveLabels);
        _liveFeed = feed;
        _liveRow = AgentChatEntryViewModel.CreateLive(AgentChatLabelsFactory.FromStrings().RoleAgent);
        _liveImageIndex = 0;
        _liveImageTotal = imageTotal;
        _liveStepBase = 0;
        _liveBatchCost = 0;
        _liveCostCap = imageTotal > 0 ? costCap : null;
        _liveLastRel = null;
        _liveDirty = false;
        RefreshLiveStatus();
        ReloadChat();
        return new Progress<AgentStreamEvent>(item => OnLiveEvent(feed, item));
    }

    private void OnLiveEvent(AgentLiveFeed feed, AgentStreamEvent item)
    {
        if (!ReferenceEquals(feed, _liveFeed))
        {
            return;
        }

        if (feed.Add(item))
        {
            RequestLiveFlush();
        }
    }

    // A batch image is about to start: its header line, and the status counts from here.
    private void OnLiveBatchProgress(AgentBatchProgress progress)
    {
        AgentLiveFeed? feed = _liveFeed;
        if (feed == null)
        {
            return;
        }

        _liveBatchCost = progress.CostUsd;
        string? rel = progress.CurrentRel;
        if (!string.IsNullOrEmpty(rel) && !string.Equals(rel, _liveLastRel, StringComparison.OrdinalIgnoreCase))
        {
            _liveLastRel = rel;
            _liveImageIndex++;
            feed.AddHeader(_liveImageIndex, Math.Max(progress.Total, _liveImageIndex), rel!);
            _liveStepBase = feed.ToolCalls;
        }

        RequestLiveFlush();
    }

    // At most about 10 redraws per second: a due one runs now, otherwise a one-shot timer runs it.
    private void RequestLiveFlush()
    {
        _liveDirty = true;
        if (_liveThrottle.ShouldFlush(_liveClock.ElapsedMilliseconds))
        {
            FlushLive();
            return;
        }

        if (_liveTimer == null)
        {
            _liveTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(AgentLiveThrottle.IntervalMs) };
            _liveTimer.Tick -= OnLiveTimerTick;
            _liveTimer.Tick += OnLiveTimerTick;
        }

        _liveTimer.Start();
    }

    private void OnLiveTimerTick(object? sender, EventArgs e)
    {
        _liveTimer?.Stop();
        if (_liveDirty)
        {
            _liveThrottle.ShouldFlush(_liveClock.ElapsedMilliseconds);
            FlushLive();
        }
    }

    private void FlushLive()
    {
        AgentChatEntryViewModel? row = _liveRow;
        AgentLiveFeed? feed = _liveFeed;
        if (row == null || feed == null)
        {
            return;
        }

        _liveDirty = false;
        row.LiveText = feed.Text;
        RefreshLiveStatus();
        AgentChatReloaded?.Invoke(this, EventArgs.Empty);
    }

    private void RefreshLiveStatus()
    {
        AgentLiveFeed? feed = _liveFeed;
        if (feed == null)
        {
            AgentLiveStatusText = string.Empty;
            return;
        }

        double cost = _liveImageTotal > 0 ? _liveBatchCost : feed.Cost;
        AgentLiveStatusText = AgentLiveFeed.FormatStatus(_liveLabels, cost, Math.Max(0, feed.ToolCalls - _liveStepBase), _liveImageIndex, _liveImageTotal, _liveCostCap);
    }

    // Drops the live row (the caller writes the final entry and reloads) and returns the closing text of the run (<= 500 characters).
    private string EndLive()
    {
        string text = _liveFeed?.FinalText ?? string.Empty;
        _liveTimer?.Stop();
        _liveFeed = null;
        _liveRow = null;
        _liveDirty = false;
        if (AgentLiveStatusText.Length > 0)
        {
            AgentLiveStatusText = string.Empty;
        }

        return text;
    }

    // ----- Remaining mode gate (F7): only after the open image was scanned by the agent, or when it has pending suggestions -----
    private readonly HashSet<string> _sessionScannedRels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<AgentChatEntry> _chatHistory = Array.Empty<AgentChatEntry>();

    /// <summary>
    /// The Remaining radio is enabled. Scanned images of this session are kept until the project changes (/clear does not reset them);
    /// the history adds the images of its successful agent entries.
    /// </summary>
    public bool IsChatModeRemainingEnabled
    {
        get
        {
            string? rel = Canvas.CurrentRel;
            return RemainingGateRules.Decide(_sessionScannedRels, rel, _chatHistory, rel != null && HasPendingSuggestions(rel));
        }
    }

    // The agent finished a run: its images count as scanned for this session when the outcome was ok or cached.
    private void NoteAgentScanned(IReadOnlyList<string> images, int proposed, string outcome)
    {
        if (!RemainingGateRules.IsScanSuccess(outcome, proposed))
        {
            return;
        }

        foreach (string image in images)
        {
            if (!string.IsNullOrWhiteSpace(image))
            {
                _sessionScannedRels.Add(image);
            }
        }
    }

    private bool IsScannedByAgent(string rel) => RemainingGateRules.IsScanned(rel, _sessionScannedRels, _chatHistory);

    // The open image, its suggestions, the history or the session set changed: tells the window and drops a Remaining selection that is no longer allowed.
    private void RefreshChatModeGate()
    {
        OnPropertyChanged(nameof(IsChatModeRemainingEnabled));
        if (ChatMode == AgentBatchMode.Remaining && !IsChatModeRemainingEnabled)
        {
            SetChatMode(AgentBatchMode.Current);
        }
    }

    private void OnChatGateSourceChanged(object? sender, EventArgs e) => RefreshChatModeGate();

    // ----- slash commands of the chat input box -----
    private const int MaxChatNotices = 30;
    private readonly List<AgentChatEntryViewModel> _chatNotices = new List<AgentChatEntryViewModel>();
    private string? _chatNoticeFile;

    // Localized text with an English fallback (Strings.ByName answers the key itself when the resource is missing).
    private static string SlashText(string key, string english)
    {
        string text = Strings.ByName(key);
        return string.Equals(text, key, StringComparison.Ordinal) ? english : text;
    }

    // A system line in the chat view only (not written to the history file); the view scrolls to it.
    private void AddChatNotice(string text)
    {
        _chatNotices.Add(AgentChatEntryViewModel.CreateNotice(text, SlashText("AgentChatRoleNotice", "System")));
        while (_chatNotices.Count > MaxChatNotices)
        {
            _chatNotices.RemoveAt(0);
        }

        ReloadChat();
    }

    private string ChatModeDisplay(AgentBatchMode mode)
    {
        switch (mode)
        {
            case AgentBatchMode.All: return Strings.ChatModeAll;
            case AgentBatchMode.Remaining: return Strings.ChatModeRemaining;
            default: return Strings.ChatModeCurrent;
        }
    }

    /// <summary>
    /// Enter in the chat input box: a "/command" is handled here and returns true (the box is emptied, nothing goes to the agent; an unknown
    /// "/word" only shows a system line); any other text returns false and the caller runs it as a prompt.
    /// </summary>
    public bool TryHandleSlashCommand()
    {
        AgentSlashCommand command = AgentSlashCommands.Parse(AgentPrompt);
        if (!command.IsCommand)
        {
            return false;
        }

        AgentPrompt = string.Empty;
        _ = DispatchSlashCommandAsync(command);
        return true;
    }

    private void HandleModelCommand(string name)
    {
        string format = Strings.ByName("AgentSlashModelFormat");
        if (name.Length > 0)
        {
            AgentModelInfo? found = AgentModels.FirstOrDefault(m =>
                string.Equals(m.Value, name, StringComparison.OrdinalIgnoreCase) || string.Equals(m.DisplayName, name, StringComparison.OrdinalIgnoreCase));
            if (found == null && AgentModels.Count > 0)
            {
                AddChatNotice(string.Format(System.Globalization.CultureInfo.CurrentCulture, Strings.ByName("AgentSlashModelUnknownFormat"), name));
                return;
            }

            AgentModel = found != null ? found.Value : name;
            RefreshPill();
        }

        AddChatNotice(string.Format(System.Globalization.CultureInfo.CurrentCulture, format, AgentPillText));
    }

    private void HandleModeCommand(string args)
    {
        if (args.Length > 0)
        {
            if (!AgentSlashCommands.TryParseMode(args, out string key))
            {
                AddChatNotice(string.Format(System.Globalization.CultureInfo.CurrentCulture, Strings.ByName("AgentSlashModeUnknownFormat"), args));
                return;
            }

            AgentBatchMode requested = AgentBatchPlanner.ParseMode(key, AgentBatchMode.Current);
            if (requested == AgentBatchMode.Remaining && !IsChatModeRemainingEnabled)
            {
                AddChatNotice(Strings.ByName("AgentSlashUnavailable"));
                return;
            }

            SetChatMode(requested);
        }

        AddChatNotice(string.Format(System.Globalization.CultureInfo.CurrentCulture, Strings.ByName("AgentSlashModeFormat"), ChatModeDisplay(ChatMode)));
    }

    // ----- which images a run covers (stored in the settings) -----
    private AgentBatchMode ChatMode => AgentBatchPlanner.ParseMode(_host.Settings.AgentBatchMode, AgentBatchMode.Current);

    public bool IsChatModeCurrent { get => ChatMode == AgentBatchMode.Current; set { if (value) SetChatMode(AgentBatchMode.Current); } }

    public bool IsChatModeAll { get => ChatMode == AgentBatchMode.All; set { if (value) SetChatMode(AgentBatchMode.All); } }

    public bool IsChatModeRemaining { get => ChatMode == AgentBatchMode.Remaining; set { if (value && IsChatModeRemainingEnabled) SetChatMode(AgentBatchMode.Remaining); } }

    private void SetChatMode(AgentBatchMode mode)
    {
        if (ChatMode == mode)
        {
            return;
        }

        _host.Settings.AgentBatchMode = AgentChatRules.ModeKey(mode);
        _host.PersistSettings();
        OnPropertyChanged(nameof(IsChatModeCurrent));
        OnPropertyChanged(nameof(IsChatModeAll));
        OnPropertyChanged(nameof(IsChatModeRemaining));
        OnPropertyChanged(nameof(AgentChatCapsText));
    }

    /// <summary>Read-only caps caption, for example "50 images · $2.00 max" (the values are edited in Settings).</summary>
    public string AgentChatCapsText
    {
        get
        {
            string caps = AgentChatRules.FormatCaps(
                Strings.AgentChatCapsFormat,
                Strings.AgentChatCapsNoCostFormat,
                _host.Settings.AgentMaxImages,
                _host.Settings.AgentMaxCostUsd);
            string estimate = AgentChatEstimateLine();
            return estimate.Length == 0 ? caps : caps + " · " + estimate;
        }
    }

    // "about $0.84 for 12 images (estimate)" for All / Remaining when the log has runs of this model; empty for Current or without history.
    private string AgentChatEstimateLine()
    {
        try
        {
            AgentBatchMode mode = ChatMode;
            if (mode == AgentBatchMode.Remaining && !IsChatModeRemainingEnabled)
            {
                mode = AgentBatchMode.Current;
            }

            if (mode == AgentBatchMode.Current || !_host.HasProject)
            {
                return string.Empty;
            }

            int images = Math.Min(AgentBatchPlanner.Plan(BatchImages(), mode, _host.Settings.AgentRouteLocalFirst).Count, _host.Settings.AgentMaxImages);
            string model = AgentModel.Length > 0 ? AgentModel : (_host.AgentBackend == AgentTool.Codex ? AgentCommandLine.CodexModel : AgentCommandLine.Model);
            AgentSpendEstimate estimate = AgentSpendTracker.FromFile(AgentLogPath()).Estimate(model, images, AgentPriceStore.Load());
            if (!estimate.IsKnown)
            {
                return string.Empty;
            }

            return string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                SlashText("AgentChatEstimateFormat", "about ${0} for {1} images (estimate)"),
                estimate.Usd.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture),
                estimate.Images);
        }
        catch (Exception ex) when (ex is InvalidOperationException || ex is ArgumentException || ex is FormatException)
        {
            return string.Empty;
        }
    }

    /// <summary>Settings may have changed the caps: re-reads the caption.</summary>
    public void RefreshChatCaps()
    {
        OnPropertyChanged(nameof(AgentChatCapsText));
    }

    // ----- model / effort pill (one button, one menu) -----
    private bool _pillHooked;

    // The pill follows the model list (it loads after the window opens) and the stored model; hooked once, when the chat is first opened.
    private void EnsurePillHooks()
    {
        if (_pillHooked)
        {
            return;
        }

        _pillHooked = true;
        AgentModels.CollectionChanged -= OnPillModelsChanged;
        AgentModels.CollectionChanged += OnPillModelsChanged;
        PropertyChanged -= OnPillSourceChanged;
        PropertyChanged += OnPillSourceChanged;

        // The Remaining gate follows the open image and its pending suggestions.
        Canvas.ImageReplaced -= OnChatGateSourceChanged;
        Canvas.ImageReplaced += OnChatGateSourceChanged;
        Canvas.SuggestionsChanged -= OnChatGateSourceChanged;
        Canvas.SuggestionsChanged += OnChatGateSourceChanged;
    }

    private void OnPillModelsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (!_fillingModels)
        {
            RefreshPill();
        }
    }

    private void OnPillSourceChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AgentModel) || e.PropertyName == nameof(SelectedAgentModel))
        {
            RefreshPill();
        }
    }

    // Keeps the stored effort one the model offers (a model without "xhigh" falls back like Claude Code does), then tells the window.
    private void RefreshPill()
    {
        AgentModelInfo? info = SelectedAgentModel;
        if (info != null && info.EffortLevels.Count > 0)
        {
            string key = AgentChatRules.EffortKey(_agentEffort);
            string coerced = AgentChatRules.CoerceEffort(key, AgentChatRules.SelectableEfforts(info.EffortLevels));
            if (!string.Equals(key, coerced, StringComparison.Ordinal))
            {
                StoreEffort(coerced);
            }
        }

        OnPropertyChanged(nameof(AgentPillText));
        OnPropertyChanged(nameof(AgentModelText));
        OnPropertyChanged(nameof(AgentChatCapsText));

        // Max first, then the value; the slider may clamp the old value meanwhile, which must not be written back as a choice.
        _effortSliderSync = true;
        try
        {
            OnPropertyChanged(nameof(EffortSliderMax));
            OnPropertyChanged(nameof(EffortSliderValue));
            OnPropertyChanged(nameof(EffortText));
        }
        finally
        {
            _effortSliderSync = false;
        }
    }

    private bool _effortSliderSync;

    private IReadOnlyList<string> OfferedEfforts => AgentChatRules.SelectableEfforts(SelectedAgentModel?.EffortLevels);

    /// <summary>Last slider position of the Effort slider: one stop per effort level the model offers (up to Low, Medium, High, Extra high, Max).</summary>
    public double EffortSliderMax => OfferedEfforts.Count - 1;

    public double EffortSliderValue
    {
        get
        {
            IReadOnlyList<string> offered = OfferedEfforts;
            int index = offered.ToList().IndexOf(AgentChatRules.CoerceEffort(AgentChatRules.EffortKey(_agentEffort), offered));
            return Math.Max(0, index);
        }
        set
        {
            if (_effortSliderSync)
            {
                return;
            }

            IReadOnlyList<string> offered = OfferedEfforts;
            int index = Math.Max(0, Math.Min(offered.Count - 1, (int)Math.Round(value)));
            if (!string.Equals(offered[index], AgentChatRules.CoerceEffort(AgentChatRules.EffortKey(_agentEffort), offered), StringComparison.Ordinal))
            {
                StoreEffort(offered[index]);
                RefreshPill();
            }
        }
    }

    /// <summary>"Effort (Medium)".</summary>
    public string EffortText
    {
        get
        {
            IReadOnlyList<string> offered = OfferedEfforts;
            string level = AgentChatRules.CoerceEffort(AgentChatRules.EffortKey(_agentEffort), offered);
            return Strings.AgentEffortCaption + " (" + PillEffortLabel(level) + ")";
        }
    }

    /// <summary>The model name alone, for the model button (the effort has its own slider).</summary>
    public string AgentModelText
    {
        get
        {
            AgentModelInfo? info = SelectedAgentModel;
            return info != null && info.DisplayName.Length > 0
                ? info.DisplayName
                : AgentModel.Length == 0 ? Strings.AgentModelDefault : AgentModel;
        }
    }

    private void StoreEffort(string level)
    {
        _agentEffort = AgentRunOptions.ParseEffort(level);
        _host.Settings.AgentEffort = AgentChatRules.EffortKey(_agentEffort);
        _host.PersistSettings();
    }

    private static string PillEffortLabel(string level)
    {
        string key = "AgentEffortLabel_" + level;
        string text = Strings.ByName(key);
        return string.Equals(text, key, StringComparison.Ordinal) ? AgentChatRules.EffortLabel(level) : text;
    }

    /// <summary>The text of the pill: "&lt;model name&gt; &lt;effort&gt;", for example "Sonnet 5.5 Medium".</summary>
    public string AgentPillText
    {
        get
        {
            AgentModelInfo? info = SelectedAgentModel;
            string model = info != null && info.DisplayName.Length > 0
                ? info.DisplayName
                : AgentModel.Length == 0 ? Strings.AgentModelDefault : AgentModel;
            string level = AgentChatRules.EffortKey(_agentEffort);
            if (info != null && info.EffortLevels.Count > 0)
            {
                level = AgentChatRules.CoerceEffort(level, AgentChatRules.SelectableEfforts(info.EffortLevels));
            }

            return AgentChatRules.PillText(model, PillEffortLabel(level));
        }
    }

    /// <summary>The rows of the pill menu now: the models (check on the one in use), a separator, the efforts the model supports (check on the one in use).</summary>
    public IReadOnlyList<AgentPillItem> AgentPillItems
    {
        get
        {
            IReadOnlyList<string> offered = AgentChatRules.SelectableEfforts(SelectedAgentModel?.EffortLevels);
            string level = AgentChatRules.CoerceEffort(AgentChatRules.EffortKey(_agentEffort), offered);
            return AgentChatRules.BuildPillItems(AgentModels.ToList(), AgentModel, offered, level, PillEffortLabel);
        }
    }

    /// <summary>A menu row was chosen: a model row changes the model (the effort follows what that model supports), an effort row changes the effort.</summary>
    [RelayCommand]
    private void SelectPillItem(AgentPillItem? item)
    {
        if (item == null || item.IsSeparator)
        {
            return;
        }

        if (item.Kind == AgentPillItemKind.Model)
        {
            AgentModel = item.Value;
        }
        else
        {
            StoreEffort(item.Value);
        }

        RefreshPill();
    }

    private void NotifyChatCommands()
    {
        RunChatCommand.NotifyCanExecuteChanged();
        RunCheckCommand.NotifyCanExecuteChanged();
    }

    private bool CanOpenAgentChat() => _host.HasProject;

    /// <summary>The round chat button: selects the Agent source and asks the main window to show the chat window (needs a project: the history belongs to it).</summary>
    [RelayCommand(CanExecute = nameof(CanOpenAgentChat))]
    private void OpenAgentChat()
    {
        if (_source != AutoLabelSource.AgentPrompt)
        {
            SetSource(AutoLabelSource.AgentPrompt);
        }

        ReloadChat();
        RefreshChatCaps();
        AgentChatOpenRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Run of the chat window: the current image alone (like the old Run button, Shift bypasses the cache) or a batch in the stored mode.</summary>
    [RelayCommand(CanExecute = nameof(CanRunAgent))]
    private async Task RunChatAsync()
    {
        if (RefuseOfflineAgent()) return;
        if (TryHandleSlashCommand())
        {
            return;
        }

        if (AutoRefinePrompt && !string.IsNullOrWhiteSpace(AgentPrompt) && !await RefineChatPromptAsync())
        {
            return;
        }

        AgentBatchMode mode = ChatMode;
        if (mode == AgentBatchMode.Remaining && !IsChatModeRemainingEnabled)
        {
            mode = AgentBatchMode.Current;
        }

        if (mode == AgentBatchMode.Current)
        {
            await RunAgentCoreAsync((Keyboard.Modifiers & ModifierKeys.Shift) != 0);
        }
        else
        {
            await RunBatchAsync(AgentChatRules.ModeKey(mode));
        }
    }

    /// <summary>The chat "Auto-edit prompt" button (a toggle, kept in the settings): the prompt sub-agent rewrites the prompt before the main agent gets it.</summary>
    public bool AutoRefinePrompt
    {
        get => _host.Settings.AgentAutoRefinePrompt;
        set
        {
            if (_host.Settings.AgentAutoRefinePrompt == value)
            {
                return;
            }

            _host.Settings.AgentAutoRefinePrompt = value;
            _host.PersistSettings();
            OnPropertyChanged();
        }
    }

    /// <summary>Tooltip of the toggle.</summary>
    public string AutoRefinePromptTip => SlashText("AgentRefineTip", "Auto-edit prompt: a sub-agent rewrites your request as a clear English command and explains it before the agent runs");

    /// <summary>
    /// Runs the prompt sub-agent on the text of the prompt box. On success the box holds the English command and the chat shows the
    /// explanation in the language of the UI; when the sub-agent gives nothing usable the original prompt stays and the run goes on.
    /// Returns false only when the person cancelled while it ran.
    /// </summary>
    private async Task<bool> RefineChatPromptAsync()
    {
        if (RefuseOfflineAgent()) return false;
        ImageItemViewModel? item = _host.SelectedImage;
        RefreshAgentTools();
        if (item == null || _agent == null || _claudePath == null || _mcpPath == null)
        {
            return true;
        }

        AgentRunInput? input = BuildAgentInput(item, string.Empty, true);
        if (input == null)
        {
            return true;
        }

        string original = AgentPrompt.Trim();
        System.Globalization.CultureInfo culture = Strings.Culture ?? System.Globalization.CultureInfo.CurrentUICulture;
        string language = string.Equals(culture.TwoLetterISOLanguageName, "vi", StringComparison.OrdinalIgnoreCase) ? "vi" : "en";
        if (!await EnsureAgentConsentAsync())
        {
            AiStatusText = _host.Settings.AgentOfflineOnly ? Strings.AgentOfflineExplanation : Strings.AgentConsentDeclined;
            return false;
        }

        AgentRunner agent = _agent;
        int generation = _aiRuns.Begin();
        CancellationTokenSource cts = BeginAiRun(SlashText("AgentRefineRunning", "Prompt sub-agent is editing your prompt..."));
        CancellationToken token = cts.Token;
        AgentSpendBudget? refineBudget = null;
        try
        {
            AgentSpendSnapshot snapshot = await LoadAgentSpendSnapshotAsync();
            if (!_aiRuns.IsCurrent(generation) || token.IsCancellationRequested || !ReferenceEquals(_agent, agent)
                || !ReferenceEquals(_host.SelectedImage, item) || RefuseAtDayCap(snapshot))
            {
                return false;
            }

            refineBudget = new AgentSpendBudget(AgentSpendBudget.SingleCallCeilingUsd, snapshot.DayRemainingUsd, 1);
            await ConfigureSpend(agent, refineBudget, snapshot);
            if (RefuseOfflineAgent()) return false;
            AgentRunResult result = await agent.RefinePromptAsync(input, original, language, _host.Settings.AgentRefineHint, token);
            if (!_aiRuns.IsCurrent(generation) || token.IsCancellationRequested || result.Kind == AgentOutcomeKind.Cancelled)
            {
                return false;
            }

            if (result.Kind == AgentOutcomeKind.BudgetLimit)
            {
                AddChatNotice(Strings.AgentBudgetStopped + (result.IsCostEstimated ? " " + Strings.AgentCostEstimated : string.Empty));
                return false;
            }

            AgentPromptRefineResult? parsed = result.Kind == AgentOutcomeKind.Success ? AgentPromptRefiner.Parse(result.ResultText) : null;
            if (parsed == null)
            {
                AddChatNotice(SlashText("AgentRefineFailed", "The prompt sub-agent gave no usable answer, so your original prompt is sent."));
                return true;
            }

            AgentPrompt = parsed.Refined;
            AddChatNotice(string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                SlashText("AgentRefineNotice", "Prompt sub-agent: {0}\nSent to the agent: {1}"),
                parsed.Summary.Length > 0 ? parsed.Summary : original,
                parsed.Refined));
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            _host.LogData("Prompt sub-agent failed: " + ex);
            AddChatNotice(SlashText("AgentRefineFailed", "The prompt sub-agent gave no usable answer, so your original prompt is sent."));
            return true;
        }
        finally
        {
            refineBudget?.Close();
            EndAiRun(cts);
        }
    }

    private bool CanRunCheck()
    {
        return !_host.Settings.AgentOfflineOnly && _source == AutoLabelSource.AgentPrompt
            && Canvas.Session != null
            && _host.SelectedImage != null
            && !IsRunning
            && !_host.IsBusy;
    }

    /// <summary>Check: the agent reviews the existing shapes of the current image and proposes replace / remove fixes (the prompt box may stay empty).</summary>
    [RelayCommand(CanExecute = nameof(CanRunCheck))]
    private async Task RunCheckAsync()
    {
        await RunAgentCoreAsync((Keyboard.Modifiers & ModifierKeys.Shift) != 0, true);
    }

    // ----- per-image review of a finished batch run (chat list): pick the images, accept or discard their proposals -----
    private AgentBatchReview _batchReview = new AgentBatchReview(null);
    private bool _batchReviewBusy;
    private string? _batchOpenRel;
    private TaskCompletionSource<bool>? _batchOpenReady;
    private static readonly TimeSpan BatchOpenTimeout = TimeSpan.FromSeconds(20);

    /// <summary>One row per image of the last batch run that succeeded (proposal count, state, check). Rebuilt when the run ends.</summary>
    public ObservableCollection<AgentBatchReviewRow> BatchReviewRows { get; } = new ObservableCollection<AgentBatchReviewRow>();

    /// <summary>True when the last batch run has rows: the chat list is shown.</summary>
    public bool HasBatchReview => BatchReviewRows.Count > 0;

    private static AgentBatchReviewLabels BatchReviewWords()
    {
        return new AgentBatchReviewLabels
        {
            ProposedFormat = Strings.AgentBatchReviewProposedFormat,
            Accepted = Strings.AgentBatchReviewAccepted,
            Discarded = Strings.AgentBatchReviewDiscarded,
        };
    }

    private void ResetBatchReview()
    {
        _batchReview = new AgentBatchReview(BatchReviewWords());
        ClearBatchReviewRows();
    }

    private void ClearBatchReviewRows()
    {
        foreach (AgentBatchReviewRow row in BatchReviewRows)
        {
            row.PropertyChanged -= OnBatchReviewRowChanged;
        }

        BatchReviewRows.Clear();
        RefreshBatchReviewState();
    }

    private void RebuildBatchReviewRows()
    {
        ClearBatchReviewRows();
        foreach (AgentBatchReviewRow row in _batchReview.Rows)
        {
            row.PropertyChanged -= OnBatchReviewRowChanged;
            row.PropertyChanged += OnBatchReviewRowChanged;
            BatchReviewRows.Add(row);
        }

        RefreshBatchReviewState();
    }

    private void OnBatchReviewRowChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        RefreshBatchReviewState();
    }

    private void RefreshBatchReviewState()
    {
        OnPropertyChanged(nameof(HasBatchReview));
        AcceptSelectedImagesCommand.NotifyCanExecuteChanged();
        DiscardSelectedImagesCommand.NotifyCanExecuteChanged();
        ToggleSelectAllImagesCommand.NotifyCanExecuteChanged();
    }

    private bool CanApplyBatchReview()
    {
        return !_batchReviewBusy && !IsRunning && !IsBatchRunning && !_host.IsBusy && _host.CurrentStep == WorkflowStep.Label && _batchReview.CanApply;
    }

    partial void OnIsBatchRunningChanged(bool value)
    {
        RefreshBatchReviewState();
    }

    private bool CanToggleBatchSelection()
    {
        return !_batchReviewBusy && !IsRunning && _batchReview.CanSelectAny;
    }

    /// <summary>Accept the proposals of the checked images (each image once; an accepted image is final).</summary>
    [RelayCommand(CanExecute = nameof(CanApplyBatchReview))]
    private async Task AcceptSelectedImagesAsync()
    {
        await ApplyBatchReviewAsync(true);
    }

    /// <summary>Drop the proposals of the checked images (each image once; a discarded image is final).</summary>
    [RelayCommand(CanExecute = nameof(CanApplyBatchReview))]
    private async Task DiscardSelectedImagesAsync()
    {
        await ApplyBatchReviewAsync(false);
    }

    [RelayCommand(CanExecute = nameof(CanToggleBatchSelection))]
    private void ToggleSelectAllImages()
    {
        _batchReview.ToggleSelectAll();
    }

    /// <summary>A click on an image name shows that image on the canvas (the same navigation as the image list).</summary>
    [RelayCommand]
    private void OpenBatchImage(AgentBatchReviewRow? row)
    {
        if (row == null || _batchReviewBusy)
        {
            return;
        }

        ImageItemViewModel? item = _host.Images.FirstOrDefault(i => string.Equals(i.Rel, row.Rel, StringComparison.OrdinalIgnoreCase));
        if (item != null)
        {
            _host.SelectedImage = item;
        }
    }

    /// <summary>A click on an image path of a chat answer: that image opens on the canvas (its proposals are there to accept or reject).</summary>
    [RelayCommand]
    private void OpenChatImage(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || _batchReviewBusy)
        {
            return;
        }

        string wanted = path!.Trim();
        ImageItemViewModel? item = _host.Images.FirstOrDefault(i => string.Equals(i.Rel, wanted, StringComparison.OrdinalIgnoreCase))
            ?? _host.Images.FirstOrDefault(i => string.Equals(i.DisplayName, wanted, StringComparison.OrdinalIgnoreCase))
            ?? _host.Images.FirstOrDefault(i => string.Equals(System.IO.Path.GetFileName(i.Rel), System.IO.Path.GetFileName(wanted), StringComparison.OrdinalIgnoreCase));
        if (item != null)
        {
            _host.SelectedImage = item;
        }
    }

    private async Task ApplyBatchReviewAsync(bool accept)
    {
        if (!CanApplyBatchReview())
        {
            return;
        }

        _batchReviewBusy = true;
        RefreshBatchReviewState();
        AgentBatchReviewResult result;
        try
        {
            result = await AgentBatchReviewApply.RunAsync(_batchReview, accept, OpenBatchImageForReviewAsync, ApplyBatchImageForReview, SaveOpenBatchImageAsync);
        }
        finally
        {
            _batchReviewBusy = false;
            RefreshBatchReviewState();
        }

        if (result.OpenFailed.Count > 0)
        {
            AiStatusText = string.Format(Strings.AgentBatchReviewOpenFailedFormat, result.OpenFailed[0]);
        }
    }

    // Accept / reject of the open image: the canvas path of the Accept all / Reject all buttons, limited to the agent proposals.
    private int ApplyBatchImageForReview(string rel, bool accept)
    {
        if (_host.CurrentStep != WorkflowStep.Label || !IsBatchImageOpen(rel))
        {
            return 0;
        }

        return accept
            ? Canvas.AcceptSuggestionsWhere(s => AgentOutcomeRecording.IsAgentSuggestion(s))
            : Canvas.RejectSuggestionsWhere(s => AgentOutcomeRecording.IsAgentSuggestion(s));
    }

    // Writes the open image's accepted shapes now, so the navigation to the next image does not raise the unsaved prompt.
    private async Task SaveOpenBatchImageAsync()
    {
        Canvas.SaveNow();
        await Canvas.FlushAsync();
    }

    private bool IsBatchImageOpen(string rel)
    {
        return Canvas.Session != null && string.Equals(Canvas.CurrentRel, rel, StringComparison.OrdinalIgnoreCase);
    }

    // Shows the image on the canvas and waits until its document and suggestions are ready. False when the image is unknown, the navigation
    // was refused (unsaved changes) or the load does not finish in time.
    private async Task<bool> OpenBatchImageForReviewAsync(string rel)
    {
        if (IsBatchImageOpen(rel))
        {
            return true;
        }

        if (_host.CurrentStep != WorkflowStep.Label)
        {
            return false;
        }

        ImageItemViewModel? item = _host.Images.FirstOrDefault(i => string.Equals(i.Rel, rel, StringComparison.OrdinalIgnoreCase));
        if (item == null)
        {
            return false;
        }

        _batchOpenRel = rel;
        _batchOpenReady = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Canvas.DocumentReady -= OnBatchReviewDocumentReady;
        Canvas.DocumentReady += OnBatchReviewDocumentReady;
        try
        {
            _host.SelectedImage = item;
            if (!ReferenceEquals(_host.SelectedImage, item))
            {
                return false;
            }

            if (IsBatchImageOpen(rel))
            {
                return true;
            }

            Task finished = await Task.WhenAny(_batchOpenReady.Task, Task.Delay(BatchOpenTimeout));
            return finished == _batchOpenReady.Task && IsBatchImageOpen(rel);
        }
        finally
        {
            Canvas.DocumentReady -= OnBatchReviewDocumentReady;
            _batchOpenRel = null;
            _batchOpenReady = null;
        }
    }

    private void OnBatchReviewDocumentReady(object? sender, EventArgs e)
    {
        string? rel = _batchOpenRel;
        if (rel != null && IsBatchImageOpen(rel))
        {
            _batchOpenReady?.TrySetResult(true);
        }
    }
}
