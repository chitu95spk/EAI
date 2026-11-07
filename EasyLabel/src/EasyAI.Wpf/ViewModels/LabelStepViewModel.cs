using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Windows.Input;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EasyAI.Desktop.Annotation;
using EasyAI.Desktop.Infrastructure;
using EasyAI.Desktop.Resources;
using EasyAI.Desktop.Services;
using EasyAI.Desktop.Services.Agent;
using EasyLabel.Ai.Onnx;
using EasyLabel.SegAI;
using EasyLabel.SegAI.AutoLabel;
using EasyLabel.SegAI.Data;
using EasyLabel.SegAI.Inspection;
using EasyLabel.SegAI.Model;
using InfoBarSeverity = Wpf.Ui.Controls.InfoBarSeverity;

namespace EasyAI.Desktop.ViewModels;

/// <summary>
/// Label tab: model choice/import, auto-label (current image or all unlabelled images), score threshold,
/// suggestion accept/reject and the Train/Val switch of the current image. Suggestions live in the canvas overlay layer, not in the
/// document, until they are accepted.
/// </summary>
public sealed partial class LabelStepViewModel : ObservableObject
{
    private const double DefaultThreshold = 0.5;
    private static readonly Lazy<AgentSpendLedger> SharedAgentLedger = new Lazy<AgentSpendLedger>(CreateSharedAgentLedger);

    private readonly MainViewModel _host;
    private readonly Dictionary<string, HashSet<SegAiSuggestion>> _dismissed = new Dictionary<string, HashSet<SegAiSuggestion>>(StringComparer.OrdinalIgnoreCase);
    private AutoLabelService? _service;
    private CancellationTokenSource? _cts;
    private bool _syncingModel;
    private int _unlabelled;
    private string? _activeModel;
    private AutoLabelSource _source;
    private string _aiModelId;
    private readonly AiRunTracker _aiRuns = new AiRunTracker();
    private readonly Dictionary<string, List<SegAiSuggestion>> _aiSuggestions = new Dictionary<string, List<SegAiSuggestion>>(StringComparer.OrdinalIgnoreCase);
    private HashSet<SegAiSuggestion> _liveAi = new HashSet<SegAiSuggestion>(ReferenceEqualityComparer.Instance);
    private AiBackendService? _aiService;
    private AiImage? _aiImage;
    private bool _aiRunActive;
    private bool _aiCommitPending;
    private AgentRunner? _agent;

    /// <summary>The runners of the extra workers of a parallel agent batch (the first worker is <see cref="_agent"/>); empty outside one.</summary>
    private List<AgentRunner> _batchWorkerRunners = new List<AgentRunner>();
    private string? _claudePath;
    private string? _mcpPath;

    /// <summary>The instruction for the Claude Code agent; stored in the settings (written to disk when a run starts or the source changes).</summary>
    [ObservableProperty]
    private string _agentPrompt = string.Empty;

    /// <summary>Result line of the last AI run: backend, where it ran ("DirectML" or the CPU fallback reason), count and time.</summary>
    [ObservableProperty]
    private string _aiStatusText = string.Empty;

    [ObservableProperty]
    private string? _selectedModel;

    [ObservableProperty]
    private string _activeModelText = Strings.ModelNone;

    [ObservableProperty]
    private double _threshold = DefaultThreshold;

    [ObservableProperty]
    private bool _showThreshold;

    [ObservableProperty]
    private int _suggestionCount;

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private bool _isProgressIndeterminate;

    [ObservableProperty]
    private double _progressValue;

    [ObservableProperty]
    private string _progressText = string.Empty;

    public LabelStepViewModel(MainViewModel host)
    {
        _host = host;
        ModelFiles = new ObservableCollection<string>();
        SuggestedPrompts.CollectionChanged -= OnSuggestedPromptsChanged;
        SuggestedPrompts.CollectionChanged += OnSuggestedPromptsChanged;
        InitPromptOptions(host.Canvas);
        host.AgentBackendChanged -= OnAgentBackendChanged;
        host.AgentBackendChanged += OnAgentBackendChanged;

        CanvasViewModel canvas = host.Canvas;
        canvas.SuggestionsChanged -= OnSuggestionsChanged;
        canvas.SuggestionsChanged += OnSuggestionsChanged;
        canvas.SuggestionsResolved -= OnSuggestionsResolved;
        canvas.SuggestionsResolved += OnSuggestionsResolved;
        canvas.SuggestionDecided -= OnSuggestionDecided;
        canvas.SuggestionDecided += OnSuggestionDecided;
        canvas.ReviewSkipped -= OnReviewSkipped;
        canvas.ReviewSkipped += OnReviewSkipped;
        canvas.DocumentReady -= OnDocumentReady;
        canvas.DocumentReady += OnDocumentReady;
        canvas.ImageReplaced -= OnImageReplaced;
        canvas.ImageReplaced += OnImageReplaced;
        canvas.ImageSaved -= OnCanvasImageSaved;
        canvas.ImageSaved += OnCanvasImageSaved;
        canvas.AiPromptRequested -= OnAiPromptRequested;
        canvas.AiPromptRequested += OnAiPromptRequested;
        canvas.AiPromptCommitted -= OnAiPromptCommitted;
        canvas.AiPromptCommitted += OnAiPromptCommitted;
        canvas.AiPromptCleared -= OnAiPromptCleared;
        canvas.AiPromptCleared += OnAiPromptCleared;

        // Train/Val shares the per-file rule of the Files panel; its enable state follows the selection, busy flag and image state.
        host.Data.ModeAvailabilityChanged -= OnModeAvailabilityChanged;
        host.Data.ModeAvailabilityChanged += OnModeAvailabilityChanged;

        // Stored source (an invalid or missing value is the Segment Model); the AI tools follow it from the start.
        _source = AutoLabelSourceRules.ReadSource(host.Settings);
        _aiModelId = AutoLabelSourceRules.ReadAiModel(host.Settings);
        _agentPrompt = AutoLabelSourceRules.ReadAgentPrompt(host.Settings);
        if (_source == AutoLabelSource.AgentPrompt)
        {
            RefreshAgentTools();
        }

        ApplyToolState();
    }

    public ObservableCollection<string> ModelFiles { get; }

    public bool CanChooseModel => _host.HasSession && !_host.IsBusy && ModelFiles.Count > 0;

    public string ThresholdText => Threshold.ToString("0.00", CultureInfo.CurrentCulture);

    public bool IsTrainMode => _host.SelectedImage is { HasRegions: true } item && item.Mode == SegTrainingMode.Train;

    public bool IsValMode => _host.SelectedImage is { HasRegions: true } item && item.Mode == SegTrainingMode.Val;

    private CanvasViewModel Canvas => _host.Canvas;

    // ---------- session / state ----------

    /// <summary>Binds the project session (null = none): new auto-label cache, model list, empty suggestion layer.</summary>
    internal void Rebind(SegAiSession? session)
    {
        _cts?.Cancel();
        ReleaseAi();
        _aiSuggestions.Clear();
        _dismissed.Clear();
        _service = session == null ? null : new AutoLabelService(session);
        _activeModel = null;
        Canvas.SuggestionModel = null;
        Threshold = session == null ? DefaultThreshold : Math.Min(1.0, Math.Max(AutoLabelService.FloorThreshold, session.Inference.DefectThreshold));
        ModelFiles.Clear();
        SetModelSelection(null);
        ActiveModelText = Strings.ModelNone;
        RefreshSuggestions();
        RefreshState();
        if (session != null)
        {
            _ = RefreshModelsAsync(session);
        }
    }

    /// <summary>Busy flag, session or selection changed: re-evaluates every command and the derived flags.</summary>
    internal void NotifyStateChanged()
    {
        RefreshState();
    }

    private void RefreshState()
    {
        _unlabelled = _host.Images.Count(i => i.HasInfo && !i.HasRegions);
        OnPropertyChanged(nameof(CanChooseModel));
        OnPropertyChanged(nameof(IsTrainMode));
        OnPropertyChanged(nameof(IsValMode));
        ImportModelCommand.NotifyCanExecuteChanged();
        AutoLabelCurrentCommand.NotifyCanExecuteChanged();
        AutoLabelAllCommand.NotifyCanExecuteChanged();
        CancelRunCommand.NotifyCanExecuteChanged();
        SetModeCommand.NotifyCanExecuteChanged();
        RunAiSourceCommand.NotifyCanExecuteChanged();
        RunAgentCommand.NotifyCanExecuteChanged();
        RunBatchCommand.NotifyCanExecuteChanged();
        NotifyChatCommands();
        SuggestPromptCommand.NotifyCanExecuteChanged();
        NotifySuggestionCommands();
    }

    partial void OnIsRunningChanged(bool value)
    {
        RunAiSourceCommand.NotifyCanExecuteChanged();
        RunAgentCommand.NotifyCanExecuteChanged();
        RunBatchCommand.NotifyCanExecuteChanged();
        NotifyChatCommands();
        SuggestPromptCommand.NotifyCanExecuteChanged();
        RefreshBatchReviewState();
    }

    partial void OnAgentPromptChanged(string value)
    {
        // Kept in the settings object at once; the file is written when a run starts or the source changes.
        _host.Settings.AgentPrompt = value;
        OnPropertyChanged(nameof(SourceReasonText));
        OnPropertyChanged(nameof(HasSourceReason));
        OnPropertyChanged(nameof(CanRunSource));
        RunAgentCommand.NotifyCanExecuteChanged();
        NotifyChatCommands();
    }

    partial void OnAiStatusTextChanged(string value)
    {
        OnPropertyChanged(nameof(HasAiStatus));
    }

    public bool HasAiStatus => AiStatusText.Length > 0;

    private void NotifySuggestionCommands()
    {
        AcceptAllCommand.NotifyCanExecuteChanged();
        AcceptSelectedCommand.NotifyCanExecuteChanged();
        RejectAllCommand.NotifyCanExecuteChanged();
    }

    private void OnModeAvailabilityChanged(object? sender, EventArgs e)
    {
        RefreshState();
    }

    private void OnDocumentReady(object? sender, EventArgs e)
    {
        RefreshSuggestions();
        RefreshState();
    }

    private void OnSuggestionsChanged(object? sender, EventArgs e)
    {
        SuggestionCount = Canvas.Suggestions.Count;
        NotifySuggestionCommands();
    }

    // The agent learns from the user's review: the decision on a suggestion of every source is recorded (tagged with its source), an accepted
    // shape is remembered for the edit check, and an accepted agent shape feeds the project vocabulary. A failing log never disturbs accept / reject.
    private void OnSuggestionDecided(SuggestionDecision decision)
    {
        string? path = _host.Project?.Paths.AgentOutcomesFile;
        if (!string.IsNullOrWhiteSpace(path))
        {
            EasyLabel.Core.Io.AgentOutcome? outcome = AgentOutcomeRecording.ToOutcome(decision, DateTime.UtcNow);
            if (outcome != null)
            {
                QueueOutcome(path, outcome);
            }
        }

        TrackAcceptance(decision);
        LearnVocabulary(decision);
    }

    // "Accept all" raises one decision per shape, and the log is rewritten on every write: the decisions of one gesture are collected and
    // written together once the UI thread is idle (at once when there is no dispatcher).
    private readonly List<EasyLabel.Core.Io.AgentOutcome> _pendingOutcomes = new List<EasyLabel.Core.Io.AgentOutcome>();
    private string? _pendingOutcomesPath;
    private bool _outcomeFlushScheduled;

    private void QueueOutcome(string path, EasyLabel.Core.Io.AgentOutcome outcome)
    {
        if (_pendingOutcomesPath != null && !string.Equals(_pendingOutcomesPath, path, StringComparison.OrdinalIgnoreCase))
        {
            FlushOutcomes();
        }

        _pendingOutcomesPath = path;
        _pendingOutcomes.Add(outcome);
        System.Windows.Threading.Dispatcher? dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher == null)
        {
            FlushOutcomes();
        }
        else if (!_outcomeFlushScheduled)
        {
            _outcomeFlushScheduled = true;
            dispatcher.BeginInvoke(new Action(FlushOutcomes), System.Windows.Threading.DispatcherPriority.Background);
        }
    }

    private void FlushOutcomes()
    {
        _outcomeFlushScheduled = false;
        string? path = _pendingOutcomesPath;
        List<EasyLabel.Core.Io.AgentOutcome> batch = _pendingOutcomes.ToList();
        _pendingOutcomes.Clear();
        _pendingOutcomesPath = null;
        if (string.IsNullOrWhiteSpace(path) || batch.Count == 0)
        {
            return;
        }

        try
        {
            EasyLabel.Core.Io.AgentOutcomeLog.AppendMany(path, batch);
        }
        catch (Exception ex) when (ex is System.IO.IOException || ex is UnauthorizedAccessException)
        {
        }
    }

    // One run's prompt text, shared by all suggestions that run produced; remembers which classes were already counted for the vocabulary.
    private sealed class RunPrompt
    {
        public RunPrompt(string prompt)
        {
            Prompt = prompt ?? string.Empty;
        }

        public string Prompt { get; }

        public HashSet<string> CountedLabels { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    // The accepted shapes of one image in the current session, with the document object they were added to (undo / redo keep that object).
    private sealed class AcceptedInImage
    {
        public AcceptedInImage(EasyLabel.Core.Model.LabelMeDocument document, int width, int height)
        {
            Document = document;
            Width = width;
            Height = height;
        }

        public EasyLabel.Core.Model.LabelMeDocument Document { get; }

        public int Width { get; }

        public int Height { get; }

        public List<AcceptedShape> Shapes { get; } = new List<AcceptedShape>();
    }

    private const int MaxTrackedAcceptances = 500;

    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<SegAiSuggestion, RunPrompt> _suggestionRuns = new System.Runtime.CompilerServices.ConditionalWeakTable<SegAiSuggestion, RunPrompt>();
    private readonly Dictionary<string, AcceptedInImage> _acceptedInImage = new Dictionary<string, AcceptedInImage>(StringComparer.OrdinalIgnoreCase);

    private void TrackAcceptance(SuggestionDecision decision)
    {
        AcceptedShape? accepted = AgentOutcomeRecording.ToAccepted(decision);
        EasyLabel.Core.Model.LabelMeDocument? document = Canvas.Document;
        if (accepted == null || document == null || string.IsNullOrEmpty(decision.ImageRel))
        {
            return;
        }

        if (!_acceptedInImage.TryGetValue(decision.ImageRel, out AcceptedInImage? tracked) || !ReferenceEquals(tracked.Document, document))
        {
            tracked = new AcceptedInImage(document, Canvas.ImageWidth, Canvas.ImageHeight);
            _acceptedInImage[decision.ImageRel] = tracked;
        }

        if (tracked.Shapes.Count < MaxTrackedAcceptances)
        {
            tracked.Shapes.Add(accepted);
        }
    }

    // Compares the accepted shapes of one image (or of all tracked images when rel is null, which also forgets them) with the shapes now in the
    // document and appends an "edit" record for each one the user changed. Only the shapes accepted in this image session are looked at.
    private void RecordEditsOfAccepted(string? rel)
    {
        // Decisions still waiting for their write come first, so the log keeps the order of events.
        FlushOutcomes();
        if (_acceptedInImage.Count == 0)
        {
            return;
        }

        string? path = _host.Project?.Paths.AgentOutcomesFile;
        List<string> keys = rel != null ? _acceptedInImage.Keys.Where(k => string.Equals(k, rel, StringComparison.OrdinalIgnoreCase)).ToList() : _acceptedInImage.Keys.ToList();
        foreach (string key in keys)
        {
            AcceptedInImage tracked = _acceptedInImage[key];
            if (rel == null)
            {
                _acceptedInImage.Remove(key);
            }

            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            try
            {
                IReadOnlyList<EasyLabel.Core.Io.AgentOutcome> edits = AgentOutcomeRecording.DetectEdits(tracked.Shapes, tracked.Document.Shapes, tracked.Width, tracked.Height, DateTime.UtcNow);
                if (edits.Count > 0)
                {
                    EasyLabel.Core.Io.AgentOutcomeLog.AppendMany(path, edits);
                }
            }
            catch (Exception ex) when (ex is System.IO.IOException || ex is UnauthorizedAccessException)
            {
            }
        }
    }

    private void OnCanvasImageSaved(string rel)
    {
        RecordEditsOfAccepted(rel);
    }

    // A (prompt word, class) pair is counted once per agent run, when the user accepts a shape of that class from the run.
    private void LearnVocabulary(SuggestionDecision decision)
    {
        if (!decision.Accepted || string.IsNullOrWhiteSpace(decision.Label)
            || AgentOutcomeRecording.SourceOf(decision.Suggestion) != EasyLabel.Core.Io.OutcomeSources.Agent
            || string.Equals(AgentOutcomeRecording.ActionOf(decision.Suggestion), SegAiSuggestion.ActionRemove, StringComparison.Ordinal)
            || !_suggestionRuns.TryGetValue(decision.Suggestion, out RunPrompt? run) || run == null || run.Prompt.Trim().Length == 0)
        {
            return;
        }

        string? path = _host.Project?.Paths.VocabularyFile;
        if (string.IsNullOrWhiteSpace(path) || !run.CountedLabels.Add(decision.Label))
        {
            return;
        }

        try
        {
            EasyLabel.Core.Io.ProjectVocabulary.Learn(path, run.Prompt, new[] { decision.Label }, DateTime.UtcNow);
        }
        catch (Exception ex) when (ex is System.IO.IOException || ex is UnauthorizedAccessException)
        {
        }
    }

    // Replace / remove suggestions whose target changed since the agent looked at it were not applied: tell the user.
    private void OnReviewSkipped(int skipped)
    {
        _host.ShowDataNotice(Strings.ReviewSkippedTitle, string.Format(Strings.ReviewSkippedFormat, skipped), InfoBarSeverity.Warning);
    }

    private void OnSuggestionsResolved(IReadOnlyList<SegAiSuggestion> resolved)
    {
        string? rel = Canvas.CurrentRel;
        if (rel == null)
        {
            return;
        }

        if (!_dismissed.TryGetValue(rel, out HashSet<SegAiSuggestion>? set))
        {
            set = new HashSet<SegAiSuggestion>(ReferenceEqualityComparer.Instance);
            _dismissed[rel] = set;
        }

        foreach (SegAiSuggestion suggestion in resolved)
        {
            set.Add(suggestion);
        }

        // AI results are not in the Segment model cache: accepted / rejected ones leave their own list too.
        if (_aiSuggestions.TryGetValue(rel, out List<SegAiSuggestion>? ai))
        {
            HashSet<SegAiSuggestion> gone = new HashSet<SegAiSuggestion>(resolved, ReferenceEqualityComparer.Instance);
            ai.RemoveAll(s => gone.Contains(s));
        }
    }

    partial void OnThresholdChanged(double value)
    {
        OnPropertyChanged(nameof(ThresholdText));
        RefreshSuggestions();
    }

    // ---------- suggestion layer ----------

    /// <summary>
    /// Shows the cached suggestions of the current image (score filter, minus the ones already accepted or rejected) on the canvas;
    /// empty when this step is not the current one, nothing is cached or the canvas has not loaded the image yet.
    /// </summary>
    internal void RefreshSuggestions()
    {
        ImageItemViewModel? item = _host.SelectedImage;
        AutoLabelService? service = _service;
        CanvasViewModel canvas = Canvas;
        bool ready = _host.CurrentStep == WorkflowStep.Label
            && item != null
            && service != null
            && canvas.Session != null
            && string.Equals(canvas.CurrentRel, item.Rel, StringComparison.OrdinalIgnoreCase);
        if (!ready || item == null || service == null)
        {
            ShowThreshold = false;
            canvas.SetSuggestions(Array.Empty<SegAiSuggestion>());
            return;
        }

        IReadOnlyList<SegAiSuggestion> cached = service.Filter(item.Rel, 0f);
        ShowThreshold = LabelStepRules.HasScores(cached);
        _dismissed.TryGetValue(item.Rel, out HashSet<SegAiSuggestion>? dismissed);
        IReadOnlyList<SegAiSuggestion> visible = LabelStepRules.Visible(cached, (float)Threshold, dismissed);
        if (_aiSuggestions.TryGetValue(item.Rel, out List<SegAiSuggestion>? ai) && ai.Count > 0)
        {
            // AI results share the layer; the score slider belongs to the Segment model, so they are not filtered by it.
            visible = visible.Concat(LabelStepRules.Visible(ai, 0f, dismissed)).ToList();
        }

        canvas.SetSuggestions(visible);
    }

    // ---------- model ----------

    private async Task RefreshModelsAsync(SegAiSession session)
    {
        try
        {
            (List<string> files, string? active) = await Task.Run(() =>
                (session.Models.Select(m => m.File).ToList(), session.ActiveModel));
            if (!ReferenceEquals(session, _host.Session))
            {
                return;
            }

            _syncingModel = true;
            try
            {
                ModelFiles.Clear();
                foreach (string file in files)
                {
                    ModelFiles.Add(file);
                }

                _activeModel = active;
                SelectedModel = active;
            }
            finally
            {
                _syncingModel = false;
            }

            Canvas.SuggestionModel = active;
            ActiveModelText = active ?? Strings.ModelNone;
            RefreshState();
        }
        catch (Exception ex)
        {
            _host.LogData("Read model registry failed: " + ex.Message);
        }
    }

    /// <summary>
    /// Step 3 trained a candidate or activated a model: re-reads the registry into the model list and, when the active model changed,
    /// drops the suggestions of the previous model.
    /// </summary>
    internal async Task SyncModelsAsync(bool activeChanged)
    {
        SegAiSession? session = _host.Session;
        if (session == null)
        {
            return;
        }

        if (activeChanged)
        {
            _service?.Clear();
            _dismissed.Clear();
        }

        await RefreshModelsAsync(session);
        if (activeChanged)
        {
            RefreshSuggestions();
        }
    }

    private void SetModelSelection(string? value)
    {
        _syncingModel = true;
        try
        {
            SelectedModel = value;
        }
        finally
        {
            _syncingModel = false;
        }
    }

    partial void OnSelectedModelChanged(string? value)
    {
        if (_syncingModel || value == null || string.Equals(value, _activeModel, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _ = ActivateModelAsync(value);
    }

    private async Task ActivateModelAsync(string file)
    {
        bool ok = await _host.RunLabelAsync(session => Task.Run(() => session.SetActiveModel(file)), Strings.ModelErrorTitle);
        if (ok)
        {
            _activeModel = file;
            Canvas.SuggestionModel = file;
            ActiveModelText = file;
            // Suggestions of the previous model no longer describe this model.
            _service?.Clear();
            _dismissed.Clear();
            RefreshSuggestions();
            await _host.Train.RefreshCandidatesAsync(null);
            await _host.Inspect.OnModelsChangedAsync(null);
        }
        else
        {
            SetModelSelection(_activeModel);
        }

        RefreshState();
    }

    private bool CanRun() => _host.HasSession && !_host.IsBusy;

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task ImportModelAsync()
    {
        Microsoft.Win32.OpenFileDialog dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = Strings.ImportModelDialogTitle,
            Filter = Strings.ImportModelFilter,
            CheckFileExists = true,
            Multiselect = false,
        };

        if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.FileName))
        {
            return;
        }

        string path = dialog.FileName;
        SegAiModelImportResult? result = null;
        bool ok = await _host.RunLabelAsync(
            async session => { result = await Task.Run(() => session.ImportModel(path)); },
            Strings.ModelErrorTitle);
        if (!ok || result == null)
        {
            return;
        }

        if (result.Problem != null)
        {
            CultureInfo culture = Strings.Culture ?? CultureInfo.CurrentUICulture;
            bool vietnamese = string.Equals(culture.TwoLetterISOLanguageName, "vi", StringComparison.OrdinalIgnoreCase);
            _host.ShowDataNotice(Strings.ImportModelRejectedTitle, vietnamese ? result.Problem.Vi : result.Problem.En, InfoBarSeverity.Warning);
            return;
        }

        // The imported model is only registered; the user activates it from the list.
        SegAiSession? current = _host.Session;
        if (current != null && result.Entry != null)
        {
            await RefreshModelsAsync(current);
            await _host.Train.RefreshCandidatesAsync(null);
            await _host.Inspect.OnModelsChangedAsync(null);
            _host.ShowDataNotice(
                Strings.ImportModelDialogTitle,
                string.Format(Strings.ImportModelDoneFormat, result.Entry.File),
                InfoBarSeverity.Success);
        }
    }

    // ---------- auto-label source ----------

    public bool IsSegmentSource => _source == AutoLabelSource.SegmentModel;

    public bool IsAiSource => _source == AutoLabelSource.AiModel;

    public bool IsAgentSource => _source == AutoLabelSource.AgentPrompt;

    public bool IsSam2Picked => _aiModelId == AiModelCatalog.Sam2Id;

    public bool IsSam3Picked => _aiModelId == AiModelCatalog.Sam3Id;

    public bool IsYoloWorldPicked => _aiModelId == AiModelCatalog.YoloWorldId;

    public bool IsCustomDetectPicked => _aiModelId == AiModelCatalog.CustomDetectId;

    /// <summary>"Unavailable: ..." reason of the AI Model / Agent Prompt run; empty when the source is the Segment Model or can run.</summary>
    public string SourceReasonText => IsAgentSource && _host.Settings.AgentOfflineOnly ? Strings.AgentOfflineExplanation : ReasonText(CurrentRunReason());

    public bool HasSourceReason => SourceReasonText.Length > 0;

    /// <summary>
    /// The Run button of the AI Model / Agent Prompt source is usable (YOLO-World with its files; false for SAM2, which needs a prompt;
    /// the Agent needs Claude Code, its tool executable and a prompt text).
    /// </summary>
    public bool CanRunSource => _source != AutoLabelSource.SegmentModel && (!IsAgentSource || !_host.Settings.AgentOfflineOnly) && CurrentRunReason() == AutoLabelReason.None;

    private AutoLabelReason CurrentRunReason()
    {
        return _source == AutoLabelSource.AgentPrompt
            ? AutoLabelSourceRules.AgentRunReason(_claudePath != null, _mcpPath != null, AgentPrompt)
            : AutoLabelSourceRules.RunReason(_source, _aiModelId, ModelFilesFound(), BackendImplemented());
    }

    /// <summary>For the AI Model source: its model files exist. For the Agent source: Claude Code and the MCP tool executable were found.</summary>
    private bool ModelFilesFound()
    {
        if (_source == AutoLabelSource.AgentPrompt)
        {
            return _claudePath != null && _mcpPath != null;
        }

        AiModelStatus? status = _host.AiModels.FirstOrDefault(m => m.Id == _aiModelId);
        return status != null && status.Found;
    }

    /// <summary>Looks for Claude Code and the MCP tool executable again (cheap file checks).</summary>
    private void RefreshAgentTools()
    {
        _agent ??= AgentRunner.CreateDefault();
        _agent.Tool = _host.AgentBackend;
        _agent.IsOffline = _host.Settings.AgentOfflineOnly;
        _agent.Options = CurrentOptions();
        _claudePath = _agent.FindAgent();
        _mcpPath = _agent.FindMcp();
    }

    internal void RefreshAgentOfflineState()
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

    private string AgentNotFoundText() => _host.AgentBackend == AgentTool.Codex ? Strings.ReasonCodexNotFound : Strings.ReasonClaudeNotFound;

    private string AgentNotSignedInText() => _host.AgentBackend == AgentTool.Codex ? Strings.AgentCodexNotSignedIn : Strings.AgentNotSignedIn;

    private string AgentToolName() => _host.AgentBackend == AgentTool.Codex ? "Codex" : "Claude Code";

    // The agent program was changed in Settings: look again, so the Run button and its reason follow the new choice.
    private void OnAgentBackendChanged(object? sender, EventArgs e)
    {
        RefreshAgentTools();
        RefreshSource();
        OnPropertyChanged(nameof(AgentModel));
        _ = LoadAgentModelsAsync();
    }

    private bool BackendImplemented() => AutoLabelSourceRules.IsBackendImplemented(_source);

    private string ReasonText(AutoLabelReason reason)
    {
        switch (reason)
        {
            case AutoLabelReason.NeedSam2: return Strings.ReasonNeedSam2;
            case AutoLabelReason.NeedAiOrAgent: return Strings.ReasonNeedAiOrAgent;
            case AutoLabelReason.NoBackend: return Strings.ReasonNoBackend;
            case AutoLabelReason.AgentNotConnected: return string.Format(Strings.ReasonAgentNotConnected, AgentToolName());
            case AutoLabelReason.NeedPrompt: return Strings.ReasonNeedPrompt;
            case AutoLabelReason.ClaudeNotFound: return AgentNotFoundText();
            case AutoLabelReason.NeedAgentPrompt: return Strings.ReasonNeedAgentPrompt;
            case AutoLabelReason.AgentToolsMissing: return Strings.ReasonAgentToolsMissing;
            case AutoLabelReason.AgentUsesPrompt: return Strings.ReasonAgentUsesPrompt;
            case AutoLabelReason.ModelFilesMissing:
                AiModelStatus? status = _host.AiModels.FirstOrDefault(m => m.Id == _aiModelId);
                return status == null ? Strings.ReasonNoBackend : SettingsCatalog.AiStatusText(Strings.ByName, status);
            default: return string.Empty;
        }
    }

    /// <summary>Pushes the source rules to the AI-Points / AI-Box tools (tool strip, menus): enable state and tooltip with the reason.</summary>
    private void ApplyToolState()
    {
        bool files = ModelFilesFound();
        bool backend = AutoLabelSourceRules.IsBackendAvailable(_source, files);
        bool implemented = BackendImplemented();
        CanvasViewModel canvas = Canvas;

        AutoLabelReason pointsReason = AutoLabelSourceRules.PointsReason(_source, _aiModelId, files, implemented);
        canvas.AiPointsEnabled = AutoLabelSourceRules.IsAiPointsEnabled(_source, _aiModelId, backend);
        canvas.AiPointsToolTip = ShortcutTable.WithShortcut(
            canvas.AiPointsEnabled ? Strings.MiAiPoints : ReasonText(pointsReason),
            ShortcutId.ToolAiPoints);

        AutoLabelReason boxReason = AutoLabelSourceRules.BoxReason(_source, _aiModelId, files, implemented);
        canvas.AiBoxEnabled = AutoLabelSourceRules.IsAiBoxEnabled(_source, _aiModelId, backend);
        canvas.AiBoxToolTip = ShortcutTable.WithShortcut(
            canvas.AiBoxEnabled ? Strings.MiAiBox : ReasonText(boxReason),
            ShortcutId.ToolAiBox);
        StartAiPreload();
    }

    private string? _aiPreloadKey;

    /// <summary>
    /// Loads SAM2 / SAM3 in the background as soon as the AI Model source shows them, so the first AI-Box run does not wait for the model.
    /// Once per model + folder (ReleaseAi resets it). A load error is left to the first run, which reports it.
    /// </summary>
    private async void StartAiPreload()
    {
        AiModelStatus? status = _host.AiModels.FirstOrDefault(m => m.Id == _aiModelId);
        if (_source != AutoLabelSource.AiModel || status == null || !status.Found
            || (_aiModelId != AiModelCatalog.Sam2Id && _aiModelId != AiModelCatalog.Sam3Id))
        {
            return;
        }

        string key = _aiModelId + "|" + status.Location;
        if (_aiPreloadKey == key)
        {
            return;
        }

        _aiPreloadKey = key;
        AiBackendService service = _aiService ??= new AiBackendService();
        string text = string.Format(Strings.AiLoadingFormat, status.DisplayName);
        AiStatusText = text;
        try
        {
            await service.PreloadAsync(_aiModelId, status.Location, null, CancellationToken.None);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
        }

        if (AiStatusText == text)
        {
            AiStatusText = string.Empty;
        }
    }

    private void RefreshSource()
    {
        OnPropertyChanged(nameof(IsSegmentSource));
        OnPropertyChanged(nameof(IsAiSource));
        OnPropertyChanged(nameof(IsAgentSource));
        OnPropertyChanged(nameof(IsSam2Picked));
        OnPropertyChanged(nameof(IsSam3Picked));
        OnPropertyChanged(nameof(IsYoloWorldPicked));
        OnPropertyChanged(nameof(IsCustomDetectPicked));
        OnPropertyChanged(nameof(SourceReasonText));
        OnPropertyChanged(nameof(HasSourceReason));
        OnPropertyChanged(nameof(CanRunSource));
        RunAiSourceCommand.NotifyCanExecuteChanged();
        RunAgentCommand.NotifyCanExecuteChanged();
        NotifyChatCommands();
        ApplyToolState();
    }

    private void PersistSource()
    {
        AutoLabelSourceRules.Write(_host.Settings, _source, _aiModelId);
        _host.PersistSettings();
    }

    /// <summary>Segmented source switch (AI Model | Segment Model | Agent Prompt); the choice is stored in the settings.</summary>
    [RelayCommand]
    private void SetSource(AutoLabelSource source)
    {
        if (_source == source)
        {
            return;
        }

        if (!ConfirmSourceSwitch())
        {
            // Declined: re-raise the source properties so the radio buttons go back to the current source; the task keeps running.
            RefreshSource();
            return;
        }

        _source = source;
        // Leaving or entering the AI Model source frees the model (the GPU is shared); the next AI run loads it again.
        ReleaseAi();
        if (source == AutoLabelSource.AgentPrompt)
        {
            // Entering the Agent source looks for Claude Code again, so an install made meanwhile is found.
            RefreshAgentTools();
        }

        _host.Settings.AgentPrompt = AgentPrompt;
        PersistSource();
        RefreshSource();
    }

    private bool IsSourceTaskActive() => IsRunning || IsBatchRunning || _aiRunActive;

    /// <summary>
    /// True when the source may be switched. While an agent run / batch, an auto-label or an AI job is active the user must confirm
    /// (the switch cancels it and the unaccepted results of that task are lost); ReleaseAi then stops it synchronously. The state is
    /// read again after the dialog because the task may have finished meanwhile, in which case nothing is asked a second time.
    /// </summary>
    private bool ConfirmSourceSwitch()
    {
        // ReleaseAi keeps the suggestions already shown on the canvas, so no pending suggestion is dropped by a switch.
        SourceSwitchDecision decision = SourceSwitchRules.Decide(IsSourceTaskActive(), 0);
        if (decision == SourceSwitchDecision.NoPrompt)
        {
            return true;
        }

        bool ask = decision == SourceSwitchDecision.AskCancelTask;
        IDialogService? dialogs = _host.Dialogs;
        bool yes = dialogs != null && dialogs.Confirm(
            Strings.ByName(ask ? "SourceSwitchCancelTitle" : "SourceSwitchDropTitle"),
            Strings.ByName(ask ? "SourceSwitchCancelMessage" : "SourceSwitchDropMessage"),
            Strings.ByName(ask ? "SourceSwitchCancelConfirm" : "SourceSwitchDropConfirm"));
        if (!yes)
        {
            return false;
        }

        // The dialog is modal but the run keeps going on its own thread: if it ended meanwhile, just switch.
        return true;
    }

    /// <summary>Segmented AI model choice (SAM2 | YOLO-World) of the AI Model source; an unknown id is ignored.</summary>
    [RelayCommand]
    private void SetAiModel(string? modelId)
    {
        if (!AutoLabelSourceRules.IsAiModelId(modelId) || modelId == _aiModelId)
        {
            return;
        }

        _aiModelId = modelId!;
        // One AI model is resident at a time: the other one is released now, the chosen one loads on its first run.
        ReleaseAi();
        PersistSource();
        RefreshSource();
    }

    /// <summary>Drops the resident AI model so its files can be replaced (the Train step installs the Custom YOLO model).</summary>
    internal void ReleaseAiModelFiles()
    {
        ReleaseAi();
    }

    /// <summary>
    /// The models folder changed ("Use this model" in the Train step): drops the resident model (its files changed), selects
    /// <paramref name="selectModelId"/> when given and refreshes the found / not-found state of the AI controls.
    /// </summary>
    internal void OnAiModelsChanged(string? selectModelId)
    {
        if (selectModelId != null && AutoLabelSourceRules.IsAiModelId(selectModelId))
        {
            _aiModelId = selectModelId;
        }

        ReleaseAi();
        PersistSource();
        RefreshSource();
    }

    // ---------- AI model runs (AI Model source) ----------

    private bool CanRunAiSource()
    {
        return CanRunSource
            && _source == AutoLabelSource.AiModel
            && Canvas.Session != null
            && _host.SelectedImage != null
            && !IsRunning
            && !_host.IsBusy;
    }

    /// <summary>
    /// Run of the AI Model source. YOLO-World detects all its classes on the whole image; SAM2 cannot run without a prompt (the rules keep
    /// this command disabled for it with the reason text), it is driven by the AI-Box / AI-Points tools.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRunAiSource))]
    private async Task RunAiSourceAsync()
    {
        await RunAiAsync(new AiPrompt(), true);
    }

    // ---------- Claude Code agent runs (Agent Prompt source) ----------

    private bool CanRunAgent()
    {
        return CanRunSource
            && _source == AutoLabelSource.AgentPrompt
            && Canvas.Session != null
            && _host.SelectedImage != null
            && !IsRunning
            && !_host.IsBusy;
    }

    /// <summary>
    /// Run of the Agent Prompt source: the Claude Code agent works on the CURRENT image only. Shift+click runs even when the same request
    /// has a cached answer.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRunAgent))]
    private async Task RunAgentAsync()
    {
        bool bypassCache = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        await RunAgentCoreAsync(bypassCache);
    }

    private AgentRunInput? BuildAgentInput(ImageItemViewModel item, string prompt, bool bypassCache, AgentRunKind kind = AgentRunKind.Label)
    {
        EasyLabel.Core.Model.LabelMeDocument? document = Canvas.Document;
        if (document == null)
        {
            return null;
        }

        FlushOutcomes();
        return BuildAgentInput(
            item,
            document,
            Canvas.ImageWidth,
            Canvas.ImageHeight,
            prompt,
            bypassCache,
            AgentRunFiles.OutcomeDigest(_host.Project != null ? _host.Project.Paths.AgentOutcomesFile : null),
            kind,
            AgentRunFiles.VocabularyBlockOf(_host.Project != null ? _host.Project.Paths.VocabularyFile : null));
    }

    /// <summary>The run input of one image from its document; <paramref name="outcomeDigest"/> is computed by the caller (once per batch).</summary>
    private AgentRunInput BuildAgentInput(ImageItemViewModel item, EasyLabel.Core.Model.LabelMeDocument document, int imageWidth, int imageHeight, string prompt, bool bypassCache, string outcomeDigest, AgentRunKind kind = AgentRunKind.Label, string? vocabularyBlock = null)
    {
        List<AgentShapeInfo> shapes = new List<AgentShapeInfo>();
        foreach (EasyLabel.Core.Model.LabelShape shape in document.Shapes)
        {
            List<double[]> points = shape.Points.Where(p => p != null && p.Length >= 2).Select(p => new[] { p[0], p[1] }).ToList();
            shapes.Add(new AgentShapeInfo(shape.Label ?? string.Empty, string.IsNullOrWhiteSpace(shape.ShapeType) ? "polygon" : shape.ShapeType!, points));
        }

        string? modelsFolder = null;
        string? first = _host.AiModels.Count > 0 ? _host.AiModels[0].Location : null;
        if (!string.IsNullOrWhiteSpace(first) && System.IO.Path.IsPathRooted(first))
        {
            modelsFolder = System.IO.Path.GetDirectoryName(first);
        }

        string[] classNames = Canvas.ClassNames.ToArray();
        return new AgentRunInput(
            item.FullPath,
            item.Rel,
            imageWidth,
            imageHeight,
            classNames,
            shapes,
            prompt,
            modelsFolder,
            Canvas.ActiveClassIndex,
            bypassCache,
            _host.Project != null ? _host.Project.Paths.AgentMemoryFile : null,
            outcomeDigest,
            null,
            PendingShapesOf(item.Rel, classNames),
            kind,
            vocabularyBlock,
            AttachmentPaths())
        {
            AllowOutOfBounds = _host.Settings.AllowOutOfBounds,
        };
    }

    private async Task RunAgentCoreAsync(bool bypassCache, bool check = false)
    {
        if (RefuseOfflineAgent()) return;
        ImageItemViewModel? item = _host.SelectedImage;
        string? rel = Canvas.CurrentRel;
        if (item == null || rel == null || !string.Equals(item.Rel, rel, StringComparison.OrdinalIgnoreCase)
            || _source != AutoLabelSource.AgentPrompt || _host.IsBusy || IsRunning)
        {
            return;
        }

        // Claude Code may have been installed since the app started: look again before refusing.
        RefreshAgentTools();
        string userPrompt = AgentPrompt.Trim();
        // Check reviews the existing shapes: the app composes the text (the prompt box only adds instructions), so an empty box is fine.
        string prompt = check ? AgentChatRules.ComposeCheckPrompt(userPrompt) : userPrompt;
        if (AutoLabelSourceRules.AgentRunReason(_claudePath != null, _mcpPath != null, prompt) != AutoLabelReason.None || _agent == null)
        {
            RefreshSource();
            return;
        }

        prompt = ComposeRunPrompt(prompt);
        AgentRunInput? input = BuildAgentInput(item, prompt, bypassCache, check ? AgentRunKind.Check : AgentRunKind.Label);
        if (input == null)
        {
            return;
        }

        _host.Settings.AgentPrompt = AgentPrompt;
        _host.PersistSettings();
        if (prompt.Length > AgentCommandLine.MaxPromptLength)
        {
            AiStatusText = string.Format(Strings.AgentPromptTooLongFormat, AgentCommandLine.MaxPromptLength);
            _host.ShowDataNotice(Strings.AgentRunErrorTitle, AiStatusText, InfoBarSeverity.Warning);
            return;
        }

        if (!await EnsureAgentConsentAsync())
        {
            AiStatusText = _host.Settings.AgentOfflineOnly ? Strings.AgentOfflineExplanation : Strings.AgentConsentDeclined;
            return;
        }

        AgentRunner agent = _agent;
        int generation = _aiRuns.Begin();
        CancellationTokenSource cts = BeginAiRun(string.Format(Strings.AgentRunning, AgentToolName()));
        CancellationToken token = cts.Token;
        AgentSpendSnapshot snapshot;
        try
        {
            snapshot = await LoadAgentSpendSnapshotAsync();
        }
        catch (Exception ex)
        {
            _host.LogData("Agent spend record could not be read: " + ex);
            EndAiRun(cts);
            return;
        }

        if (!_aiRuns.IsCurrent(generation) || token.IsCancellationRequested || _source != AutoLabelSource.AgentPrompt
            || _host.IsBusy || !ReferenceEquals(_agent, agent) || !ReferenceEquals(_host.SelectedImage, item)
            || !string.Equals(Canvas.CurrentRel, rel, StringComparison.OrdinalIgnoreCase) || RefuseAtDayCap(snapshot))
        {
            EndAiRun(cts);
            return;
        }

        ClearSuggestedPrompts();
        int classIndex = input.FallbackClassIndex;
        AgentSpendBudget? runBudget = null;
        bool liveStarted = false;
        System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
        const string chatMode = "current";
        string[] chatImages = { rel };
        try
        {
            agent.DisabledSkills = _host.Settings.DisabledRunSkills;
            runBudget = new AgentSpendBudget(_host.Settings.AgentMaxCostUsd, snapshot.DayRemainingUsd, 1);
            await ConfigureSpend(agent, runBudget, snapshot);
            if (RefuseOfflineAgent()) return;
            AiStatusText = string.Empty;
            RecordChatStart(chatMode, check && userPrompt.Length == 0 ? Strings.AgentChatCheck : userPrompt, chatImages);
            IProgress<AgentStreamEvent> live = BeginLive(0, null);
            liveStarted = true;
            AgentRunResult result = await agent.RunAsync(input, token, live);
            watch.Stop();
            if (!_aiRuns.IsCurrent(generation) || token.IsCancellationRequested
                || !string.Equals(Canvas.CurrentRel, rel, StringComparison.OrdinalIgnoreCase))
            {
                RecordChatEnd(chatMode, chatImages, 0, result.CostUsd, result.Turns, "cancelled", string.Empty);
                return;
            }

            int proposed = ApplyAgentResult(result, rel, input, classIndex, (long)watch.Elapsed.TotalSeconds);
            string chatOutcome = result.Kind == AgentOutcomeKind.Success ? (result.FromCache ? "cached" : "ok")
                : result.Kind == AgentOutcomeKind.Cancelled ? "cancelled" : "error";
            string chatError = result.Kind == AgentOutcomeKind.BudgetLimit
                ? Strings.AgentBudgetStopped
                : chatOutcome == "error" ? (result.Message.Length > 0 ? result.Message : result.Kind.ToString()) : string.Empty;
            RecordChatEnd(chatMode, chatImages, proposed,result.CostUsd, result.Turns, chatOutcome, chatError);
        }
        catch (OperationCanceledException)
        {
            RecordChatEnd(chatMode, chatImages, 0, null, null, "cancelled", string.Empty);
        }
        catch (Exception ex)
        {
            RecordChatEnd(chatMode, chatImages, 0, null, null, "error", ex.Message);
            _host.LogData("Agent run failed: " + ex);
            if (_aiRuns.IsCurrent(generation))
            {
                AiStatusText = string.Format(Strings.AgentFailedFormat, AgentToolName(), ex.Message);
                _host.ShowDataNotice(Strings.AgentRunErrorTitle, AiStatusText, InfoBarSeverity.Warning);
            }
        }
        finally
        {
            runBudget?.Close();
            if (liveStarted)
            {
                EndLive();
            }

            EndAiRun(cts);
        }
    }

    // ---------- agent batch: several images, one after the other ----------

    /// <summary>Cumulative cost of the running or last batch ("$0.000"); empty outside a batch. The chat window (S9) binds it.</summary>
    [ObservableProperty]
    private string _batchCostText = string.Empty;

    /// <summary>A batch is running (progress is in <see cref="ProgressValue"/> / <see cref="ProgressText"/>, Cancel is <see cref="CancelRunCommand"/>).</summary>
    [ObservableProperty]
    private bool _isBatchRunning;

    /// <summary>
    /// Runs the agent over several images with the same prompt. <paramref name="mode"/> is "current", "all" or "remaining" (null or anything
    /// else = the mode stored in the settings). The caps come from the settings; the review digest is read once and frozen for the batch.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRunAgent))]
    private async Task RunBatchAsync(string? mode)
    {
        if (RefuseOfflineAgent()) return;
        ImageItemViewModel? item = _host.SelectedImage;
        string? rel = Canvas.CurrentRel;
        SegAiSession? session = _host.Session;
        if (item == null || rel == null || session == null || !string.Equals(item.Rel, rel, StringComparison.OrdinalIgnoreCase)
            || _source != AutoLabelSource.AgentPrompt || _host.IsBusy || IsRunning)
        {
            return;
        }

        RefreshAgentTools();
        string prompt = AgentPrompt.Trim();
        if (AutoLabelSourceRules.AgentRunReason(_claudePath != null, _mcpPath != null, prompt) != AutoLabelReason.None || _agent == null)
        {
            RefreshSource();
            return;
        }

        prompt = ComposeRunPrompt(prompt);
        if (prompt.Length > AgentCommandLine.MaxPromptLength)
        {
            AiStatusText = string.Format(Strings.AgentPromptTooLongFormat, AgentCommandLine.MaxPromptLength);
            _host.ShowDataNotice(Strings.AgentRunErrorTitle, AiStatusText, InfoBarSeverity.Warning);
            return;
        }

        AgentBatchMode batchMode = AgentBatchPlanner.ParseMode(mode, AgentBatchPlanner.ParseMode(_host.Settings.AgentBatchMode, AgentBatchMode.Current));
        IReadOnlyList<string> rels = AgentBatchPlanner.Plan(BatchImages(), batchMode, _host.Settings.AgentRouteLocalFirst);
        if (rels.Count == 0)
        {
            AiStatusText = Strings.AgentBatchNothing;
            return;
        }

        CancellationTokenSource cts = BeginRun(false, Math.Min(rels.Count, _host.Settings.AgentMaxImages));
        IsBatchRunning = true;
        Canvas.AiBusy = true;
        AgentSpendBudget? batchBudget = null;
        AgentSam2Refiner? sam2Refiner = null;
        bool liveStarted = false;
        bool leaveLiveForChat = false;
        AgentBatchReport? finished = null;
        string chatModeName = string.Empty;
        List<string> chatImages = new List<string>();
        string chatOutcome = "error";
        string chatError = string.Empty;
        try
        {
            AgentSpendSnapshot snapshot;
            try
            {
                snapshot = await LoadAgentSpendSnapshotAsync();
            }
            catch (Exception ex)
            {
                _host.LogData("Agent spend record could not be read: " + ex);
                return;
            }

        if (cts.IsCancellationRequested || _source != AutoLabelSource.AgentPrompt || _host.IsBusy || _agent == null
            || !ReferenceEquals(_host.Session, session) || !ReferenceEquals(_host.SelectedImage, item)
            || !string.Equals(Canvas.CurrentRel, rel, StringComparison.OrdinalIgnoreCase) || RefuseAtDayCap(snapshot))
        {
            return;
        }

        if (!await EnsureAgentConsentAsync())
        {
            AiStatusText = _host.Settings.AgentOfflineOnly ? Strings.AgentOfflineExplanation : Strings.AgentConsentDeclined;
            return;
        }

        if (cts.IsCancellationRequested || _host.IsBusy || _agent == null || !ReferenceEquals(_host.Session, session) || !ReferenceEquals(_host.SelectedImage, item))
        {
            return;
        }

        double dayCapUsd = _host.Settings.AgentMaxUsdPerDay;
        double spentToday = SpentToday(snapshot, dayCapUsd);
        AgentBatchCaps caps =new AgentBatchCaps(_host.Settings.AgentMaxImages, _host.Settings.AgentMaxCostUsd, dayCapUsd, spentToday);
        _host.Settings.AgentPrompt = AgentPrompt;
        _host.PersistSettings();
        ClearSuggestedPrompts();

        // Computed once and frozen: every image of the batch (and its cache key) sees the same digest.
        string digest = AgentRunFiles.OutcomeDigest(_host.Project != null ? _host.Project.Paths.AgentOutcomesFile : null);
        string vocabulary = AgentRunFiles.VocabularyBlockOf(_host.Project != null ? _host.Project.Paths.VocabularyFile : null);
        int classIndex = Canvas.ActiveClassIndex;
        AgentRunner agent = _agent;
        int workerCount = Math.Min(EffectiveBatchWorkers, rels.Count);
        AgentSpendBudget activeBatchBudget = new AgentSpendBudget(_host.Settings.AgentMaxCostUsd, snapshot.DayRemainingUsd, workerCount);
        batchBudget = activeBatchBudget;
        agent.DisabledSkills = _host.Settings.DisabledRunSkills;
        await ConfigureSpend(agent, activeBatchBudget, snapshot);
        if (RefuseOfflineAgent()) return;
        // Worker 0 is the usual runner; every extra worker has its own (a runner holds one live process) with the same tool, options and skills.
        List<AgentRunner> workerRunners = new List<AgentRunner> { agent };
        for (int w = 1; w < workerCount; w++)
        {
            AgentRunner extra = AgentRunner.CreateDefault();
            extra.Tool = agent.Tool;
            extra.Options = agent.Options;
            extra.DisabledSkills = agent.DisabledSkills;
            await ConfigureSpend(extra, activeBatchBudget, snapshot);
            if (RefuseOfflineAgent()) return;
            workerRunners.Add(extra);
        }

        _batchWorkerRunners = workerRunners.Skip(1).ToList();
        // One SAM2 for the whole batch (calls serialised inside): each oriented_rectangle of a successful run is tightened locally, 0 USD.
        AiModelStatus? sam2Status = _host.AiModels.FirstOrDefault(m => m.Id == AiModelCatalog.Sam2Id);
        if (_host.Settings.AgentBatchSam2Refine && sam2Status != null && sam2Status.Found && !string.IsNullOrWhiteSpace(sam2Status.Location))
        {
            sam2Refiner = AgentSam2Refiner.CreateLocal(sam2Status.Location, (imagePath, loadToken) => AiImageLoader.Load(imagePath, AiImageLoader.KeyOf(imagePath), loadToken));
        }

        Func<AgentRunner, Func<AgentRunInput, CancellationToken, IProgress<AgentStreamEvent>?, Task<AgentRunResult>>> asRun = workerRunner =>
        {
            Func<AgentRunInput, CancellationToken, IProgress<AgentStreamEvent>?, Task<AgentRunResult>> plain = (batchInput, batchToken, batchProgress) =>
                batchProgress != null ? workerRunner.RunAsync(batchInput, batchToken, batchProgress) : workerRunner.RunAsync(batchInput, batchToken);
            return sam2Refiner != null ? sam2Refiner.Wrap(plain) : plain;
        };
        // Economy only: a failed image (turn limit, time-out) gets one more run at Normal effort on its own runner (same tool, skills, model).
        List<Func<AgentRunInput, CancellationToken, IProgress<AgentStreamEvent>?, Task<AgentRunResult>>>? retryRuns = null;
        if (_host.Settings.AgentBatchRetryNormal && agent.Options.Effort == AgentEffort.Economy)
        {
            retryRuns = new List<Func<AgentRunInput, CancellationToken, IProgress<AgentStreamEvent>?, Task<AgentRunResult>>>();
            for (int w = 0; w < workerRunners.Count; w++)
            {
                AgentRunner retryRunner = AgentRunner.CreateDefault();
                retryRunner.Tool = agent.Tool;
                retryRunner.Options = new AgentRunOptions(agent.Options.Precision, AgentEffort.Normal, agent.Options.Model);
                retryRunner.DisabledSkills = agent.DisabledSkills;
                await ConfigureSpend(retryRunner, activeBatchBudget, snapshot);
                if (RefuseOfflineAgent()) return;
                _batchWorkerRunners.Add(retryRunner);
                retryRuns.Add(asRun(retryRunner));
            }
        }

        AgentBatchRunner runner = new AgentBatchRunner(workerRunners.Select(asRun).ToList(), retryRuns);
        SegAiSession activeSession = session;

        async Task<AgentRunInput?> LoadInput(string imageRel)
        {
            ImageItemViewModel? target = _host.Images.FirstOrDefault(i => string.Equals(i.Rel, imageRel, StringComparison.OrdinalIgnoreCase));
            if (target == null)
            {
                return null;
            }

            // The open image is read from the canvas document (it may hold edits that are not saved); the others from their LabelMe file.
            EasyLabel.Core.Model.LabelMeDocument? open = Canvas.Document;
            if (open != null && string.Equals(Canvas.CurrentRel, imageRel, StringComparison.OrdinalIgnoreCase))
            {
                return BuildAgentInput(target, open, Canvas.ImageWidth, Canvas.ImageHeight, prompt, false, digest, AgentRunKind.Label, vocabulary);
            }

            SegAiEditDocument edit = await Task.Run(() => activeSession.LoadForEdit(imageRel));
            return BuildAgentInput(target, edit.Document, edit.Document.ImageWidth, edit.Document.ImageHeight, prompt, false, digest, AgentRunKind.Label, vocabulary);
        }

        int Apply(string imageRel, AgentRunInput input, AgentRunResult result)
        {
            int count = StoreAgentSuggestions(result, imageRel, input, classIndex);
            _batchReview.Add(imageRel, count);
            if (string.Equals(Canvas.CurrentRel, imageRel, StringComparison.OrdinalIgnoreCase))
            {
                RefreshSuggestions();
            }

            return count;
        }

        void Report(AgentBatchProgress progress)
        {
            string cost = progress.CostUsd.ToString("0.000", CultureInfo.InvariantCulture);
            ProgressValue = progress.Total <= 0 ? 0 : 100.0 * progress.Completed / progress.Total;
            ProgressText = string.Format(Strings.AgentBatchProgressFormat, progress.Completed, progress.Total, cost);
            BatchCostText = "$" + cost;
            OnLiveBatchProgress(progress);
        }

        AiStatusText = string.Empty;
        BatchCostText = "$0.000";
        ResetBatchReview();
        chatModeName = batchMode == AgentBatchMode.All ? "all" : batchMode == AgentBatchMode.Remaining ? "remaining" : "current";
        chatImages = rels.Take(Math.Max(0, caps.MaxImages)).ToList();
        RecordChatStart(chatModeName, AgentPrompt.Trim(), chatImages);
        IProgress<AgentStreamEvent> live = BeginLive(Math.Min(rels.Count, caps.MaxImages), caps.MaxCostUsd);
        liveStarted = true;
        try
        {
            finished = await runner.RunAsync(rels, caps, LoadInput, Apply, Report, cts.Token, live);
        }
        catch (Exception ex)
        {
            chatOutcome = ex is OperationCanceledException ? "cancelled" : "error";
            chatError = ex is OperationCanceledException ? string.Empty : ex.Message;
            _host.LogData("Agent batch failed: " + ex);
            AiStatusText = string.Format(Strings.AgentFailedFormat, AgentToolName(), ex.Message);
            _host.ShowDataNotice(Strings.AgentRunErrorTitle, AiStatusText, InfoBarSeverity.Warning);
        }

        leaveLiveForChat = true;
        }
        finally
        {
            if (liveStarted && !leaveLiveForChat)
            {
                EndLive();
            }

            batchBudget?.Close();
            sam2Refiner?.Dispose();
            if (ReferenceEquals(_cts, cts))
            {
                Canvas.AiBusy = false;
                IsBatchRunning = false;
            }

            EndRun(cts);
        }

        if (finished != null)
        {
            chatOutcome = finished.Stop == AgentBatchStop.Cancelled ? "cancelled" : finished.Stop == AgentBatchStop.Error ? "error" : "ok";
            chatError = chatOutcome == "error"
                ? (finished.Failures.Count > 0 ? finished.Failures[0].Message : (finished.FatalKind?.ToString() ?? string.Empty))
                : string.Empty;
            RecordChatEnd(chatModeName, chatImages, finished.Progress.ShapesProposed, finished.Progress.CostUsd, null, chatOutcome, chatError);
        }
        else
        {
            RecordChatEnd(chatModeName, chatImages, 0, null, null, chatOutcome, chatError);
        }

        RefreshSuggestions();
        RebuildBatchReviewRows();
        if (finished != null)
        {
            ShowBatchSummary(finished);
        }

        // Keep the result cache under its size cap (oldest files go first) and refresh the estimate from the new log lines.
        _ = Task.Run(() => AgentCache.Evict(AgentCache.DefaultRoot(), AgentCache.DefaultMaxBytes));
        RefreshChatCaps();
    }

    private IReadOnlyList<AgentBatchImage> BatchImages()
    {
        string? currentRel = Canvas.CurrentRel;
        List<AgentBatchImage> images = new List<AgentBatchImage>();
        foreach (ImageItemViewModel image in _host.Images)
        {
            bool isCurrent = string.Equals(image.Rel, currentRel, StringComparison.OrdinalIgnoreCase);
            bool hasShapes = image.RegionCount > 0 || (isCurrent && Canvas.Document != null && Canvas.Document.Shapes.Count > 0);
            images.Add(new AgentBatchImage(image.Rel, hasShapes, HasPendingSuggestions(image.Rel), isCurrent, IsScannedByAgent(image.Rel), _service?.CachedUncertainty(image.Rel)?.Bucket));
        }

        return images;
    }

    /// <summary>
    /// Uncertainty of an image for the list sort (memory only, no inference; the agent batch plan uses the segmentation value only): the larger of the cached segmentation result
    /// and the OK/NG classifier pass (<see cref="ImageListSortRules.Merge"/>); null when neither is known.
    /// </summary>
    public ImageUncertainty? CachedUncertaintyOf(string rel) => ImageListSortRules.Merge(_service?.CachedUncertainty(rel), ClassifierUncertaintyOf(rel));

    /// <summary>
    /// Fills the in-memory uncertainty cache from the disk cache of the loaded model: background task, no inference. Returns true when any
    /// image was restored (the caller re-sorts the list). Without a loaded model or any usable entry nothing changes and the result is unknown.
    /// </summary>
    internal async Task<bool> WarmUncertaintyFromDiskAsync()
    {
        AutoLabelService? service = _service;
        if (service == null)
        {
            return false;
        }

        string[] rels = _host.Images.Select(image => image.Rel).ToArray();
        int restored = await Task.Run(() => service.WarmFromDisk(rels));
        return restored > 0;
    }

    private bool HasPendingSuggestions(string rel)
    {
        _dismissed.TryGetValue(rel, out HashSet<SegAiSuggestion>? dismissed);
        if (_aiSuggestions.TryGetValue(rel, out List<SegAiSuggestion>? ai) && LabelStepRules.Visible(ai, 0f, dismissed).Count > 0)
        {
            return true;
        }

        AutoLabelService? service = _service;
        return service != null && LabelStepRules.Visible(service.Filter(rel, 0f), 0f, dismissed).Count > 0;
    }

    /// <summary>The result line of a finished batch (stop reason, counts, cost, first error) and the notice for a fatal error.</summary>
    private void ShowBatchSummary(AgentBatchReport report)
    {
        AgentBatchProgress progress = report.Progress;
        string stopText;
        switch (report.Stop)
        {
            case AgentBatchStop.ImageCap: stopText = Strings.AgentBatchStopImageCap; break;
            case AgentBatchStop.CostCap: stopText = Strings.AgentBatchStopCostCap; break;
            case AgentBatchStop.DayCap: stopText = SlashText("AgentBatchStopDayCap", "Stopped: the daily cost cap is reached."); break;
            case AgentBatchStop.Cancelled: stopText = Strings.AgentBatchStopCancelled; break;
            case AgentBatchStop.Error: stopText = Strings.AgentBatchStopError; break;
            default: stopText = Strings.AgentBatchStopDone; break;
        }

        string text = string.Format(
            Strings.AgentBatchSummaryFormat,
            stopText,
            progress.Completed,
            progress.ShapesProposed,
            progress.Failed,
            progress.CostUsd.ToString("0.000", CultureInfo.InvariantCulture));
        if (report.Failures.Count > 0)
        {
            AgentBatchFailure first = report.Failures[0];
            text += "\n" + string.Format(Strings.AgentBatchFirstErrorFormat, first.Rel, AgentOutcomeClassifier.Limit(first.Message));
            foreach (AgentBatchFailure failure in report.Failures.Take(5))
            {
                _host.LogData("Agent batch image failed: " + failure.Rel + ": " + AgentOutcomeClassifier.Limit(failure.Message));
            }
        }

        if (report.FatalKind == AgentOutcomeKind.NotInstalled)
        {
            RefreshAgentTools();
            RefreshSource();
            text = AgentNotFoundText();
        }
        else if (report.FatalKind == AgentOutcomeKind.NotSignedIn)
        {
            text = AgentNotSignedInText();
        }

        AiStatusText = text;
        _host.Panels.Files.RefreshOrder();
        if (report.Stop == AgentBatchStop.Error)
        {
            _host.ShowDataNotice(Strings.AgentRunErrorTitle, text, InfoBarSeverity.Warning);
        }

        if (report.NotProcessed.Count > 0)
        {
            string left = string.Join(", ", report.NotProcessed.Take(5)) + (report.NotProcessed.Count > 5 ? ", ..." : string.Empty);
            AddChatNotice(string.Format(CultureInfo.CurrentCulture, SlashText("AgentBatchLeftFormat", "Not processed ({0}): {1}"), report.NotProcessed.Count, left));
        }
    }

    // ----- spend guard: the ledger is shared by every agent run in this process -----
    private static string AgentLogPath() => System.IO.Path.Combine(AgentRunFolder.DefaultRoot(), AgentUsageLog.FileName);
    private sealed class AgentSpendSnapshot
    {
        public AgentSpendSnapshot(AgentSpendLedger ledger, AgentLedgerIntegrity integrity, double dayRemainingUsd, AgentPriceTable prices)
        {
            Ledger = ledger;
            Integrity = integrity;
            DayRemainingUsd = dayRemainingUsd;
            Prices = prices;
        }

        public AgentSpendLedger Ledger { get; }
        public AgentLedgerIntegrity Integrity { get; }
        public double DayRemainingUsd { get; }
        public AgentPriceTable Prices { get; }
    }

    private static AgentSpendLedger CreateSharedAgentLedger()
    {
        string ledgerPath = AgentLedgerFileStore.DefaultPath();
        string anchorPath = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(ledgerPath)!, AgentLedgerFileAnchor.FileName);
        AgentSpendLedger ledger = new AgentSpendLedger(
            new AgentLedgerFileStore(ledgerPath),
            new AgentLedgerDualAnchor(new AgentLedgerRegistryAnchor(), new AgentLedgerFileAnchor(anchorPath)));
        ledger.Load();
        return ledger;
    }

    private Task<AgentSpendSnapshot> LoadAgentSpendSnapshotAsync()
    {
        double dayCapUsd = _host.Settings.AgentMaxUsdPerDay;
        return Task.Run(() =>
        {
            AgentSpendLedger ledger = SharedAgentLedger.Value;
            if (ledger.Integrity == AgentLedgerIntegrity.Tampered)
            {
                return new AgentSpendSnapshot(ledger, AgentLedgerIntegrity.Tampered, 0, AgentPriceStore.Load());
            }

            double dayRemainingUsd = ledger.DayRemainingUsd(dayCapUsd);
            return new AgentSpendSnapshot(ledger, ledger.Integrity, dayRemainingUsd, AgentPriceStore.Load());
        });
    }

    // Sending images and prompts to the agent provider is covered by the product's data policy, so there is no per-project confirmation dialog.
    // The permission is recorded in the manifest silently; the offline-only setting still blocks every run. Only the manifest write leaves the UI thread.
    private async Task<bool> EnsureAgentConsentAsync()
    {
        if (RefuseOfflineAgent()) return false;
        AgentRunner? agent = _agent;
        if (agent == null)
        {
            return false;
        }

        return await AgentConsentGate.EnsureAsync(
            _host.Project?.Manifest,
            AgentConsentGate.ProviderKey(agent.Tool),
            () => true,
            DateTime.UtcNow).ConfigureAwait(true);
    }

    // Callers run EnsureAgentConsentAsync before they configure a run; the runner refuses a run without the flag (defence in depth).
    private async Task ConfigureSpend(AgentRunner runner, AgentSpendBudget budget, AgentSpendSnapshot snapshot)
    {
        runner.RequireConsent = true;
        runner.ConsentGranted = true;
        runner.Budget = budget;
        runner.Ledger = snapshot.Ledger;
        runner.Prices = snapshot.Prices;
        runner.IsOffline = _host.Settings.AgentOfflineOnly;
        runner.MaxBudgetFlagSupported = false;
        if (!runner.IsOffline && runner.Tool == AgentTool.Claude && string.IsNullOrWhiteSpace(runner.FakeAgentScript))
        {
            AgentCliCapabilities capabilities = new AgentCliCapabilities(() => _host.Settings.AgentOfflineOnly);
            runner.MaxBudgetFlagSupported = await capabilities.ProbeAsync(runner.FindClaude());
        }

        runner.IsOffline = _host.Settings.AgentOfflineOnly;
        if (runner.IsOffline) runner.MaxBudgetFlagSupported = false;
    }

    private bool RefuseAtDayCap(AgentSpendSnapshot snapshot)
    {
        if (snapshot.Integrity == AgentLedgerIntegrity.Tampered)
        {
            AiStatusText = Strings.AgentLedgerTampered;
            _host.ShowDataNotice(Strings.AgentRunErrorTitle, AiStatusText, InfoBarSeverity.Warning);
            return true;
        }

        double cap = _host.Settings.AgentMaxUsdPerDay;
        if (cap <= 0 || double.IsPositiveInfinity(snapshot.DayRemainingUsd) || snapshot.DayRemainingUsd > 0)
        {
            return false;
        }

        AiStatusText = string.Format(
            CultureInfo.CurrentCulture,
            SlashText("AgentDayCapReachedFormat", "The daily agent cost cap is reached (${0} of ${1} today). Raise it in Settings, Agent runs, or try again tomorrow."),
            SpentToday(snapshot, cap).ToString("0.00", CultureInfo.InvariantCulture),
            cap.ToString("0.00", CultureInfo.InvariantCulture));
        _host.ShowDataNotice(Strings.AgentRunErrorTitle, AiStatusText, InfoBarSeverity.Warning);
        return true;
    }

    private static double SpentToday(AgentSpendSnapshot snapshot, double dayCapUsd)
    {
        return dayCapUsd > 0 && !double.IsPositiveInfinity(snapshot.DayRemainingUsd)
            ? Math.Max(0, dayCapUsd - snapshot.DayRemainingUsd)
            : 0;
    }

    // ---------- prompt options: language, shape types and labels added to the prompt ----------

    private static readonly (string Key, string Name)[] ShapeChoices =
    {
        ("polygon", nameof(Strings.ToolPolygon)),
        ("rectangle", nameof(Strings.ToolRect)),
        ("oriented_rectangle", nameof(Strings.ToolRot)),
        ("circle", nameof(Strings.ToolCircle)),
        ("point", nameof(Strings.ToolPoint)),
        ("line", nameof(Strings.ToolLine)),
        ("linestrip", nameof(Strings.ToolLineStrip)),
    };

    private string _promptLanguage = "en";
    private AgentPrecision _agentPrecision = AgentPrecision.Normal;
    private AgentEffort _agentEffort = AgentEffort.Normal;

    // ----- box accuracy -----
    public bool IsPrecisionEasy { get => _agentPrecision == AgentPrecision.Easy; set { if (value) SetPrecision(AgentPrecision.Easy); } }

    public bool IsPrecisionNormal { get => _agentPrecision == AgentPrecision.Normal; set { if (value) SetPrecision(AgentPrecision.Normal); } }

    public bool IsPrecisionHard { get => _agentPrecision == AgentPrecision.Hard; set { if (value) SetPrecision(AgentPrecision.Hard); } }

    private void SetPrecision(AgentPrecision value)
    {
        if (_agentPrecision == value)
        {
            return;
        }

        _agentPrecision = value;
        _host.Settings.AgentPrecision = value.ToString().ToLowerInvariant();
        _host.PersistSettings();
        OnPropertyChanged(nameof(IsPrecisionEasy));
        OnPropertyChanged(nameof(IsPrecisionNormal));
        OnPropertyChanged(nameof(IsPrecisionHard));
    }

    // ----- effort -----
    public bool IsEffortEconomy { get => _agentEffort == AgentEffort.Economy; set { if (value) SetEffort(AgentEffort.Economy); } }

    public bool IsEffortNormal { get => _agentEffort == AgentEffort.Normal; set { if (value) SetEffort(AgentEffort.Normal); } }

    public bool IsEffortBest { get => _agentEffort == AgentEffort.Best; set { if (value) SetEffort(AgentEffort.Best); } }

    private void SetEffort(AgentEffort value)
    {
        if (_agentEffort == value)
        {
            return;
        }

        _agentEffort = value;
        _host.Settings.AgentEffort = value.ToString().ToLowerInvariant();
        _host.PersistSettings();
        OnPropertyChanged(nameof(IsEffortEconomy));
        OnPropertyChanged(nameof(IsEffortNormal));
        OnPropertyChanged(nameof(IsEffortBest));
    }

    // ----- model (one choice per agent program) -----
    private AgentModelService? _modelService;
    private readonly Dictionary<AgentTool, IReadOnlyList<AgentModelInfo>> _modelCache = new Dictionary<AgentTool, IReadOnlyList<AgentModelInfo>>();
    private int _modelLoad;

    /// <summary>The models the agent in use reports about itself (read from the program, see <see cref="AgentModelService"/>); the chosen model is always in the list.</summary>
    public ObservableCollection<AgentModelInfo> AgentModels { get; } = new ObservableCollection<AgentModelInfo>();

    /// <summary>Asks the agent in use for its model list (once per program and session unless <paramref name="force"/>) and fills <see cref="AgentModels"/>.</summary>
    public async Task LoadAgentModelsAsync(bool force = false)
    {
        if (_host.Settings.AgentOfflineOnly)
        {
            FillModels(Array.Empty<AgentModelInfo>());
            return;
        }
        AgentTool tool = _host.AgentBackend;
        int ticket = ++_modelLoad;
        if (force || !_modelCache.TryGetValue(tool, out IReadOnlyList<AgentModelInfo>? list))
        {
            _modelService ??= new AgentModelService(new AgentOfflineEnvironment(new SystemAgentEnvironment(), () => _host.Settings.AgentOfflineOnly), System.IO.Path.Combine(AgentRunFolder.DefaultRoot(), "models"));
            list = await _modelService.LoadAsync(tool, CancellationToken.None);
            if (list.Count > 0)
            {
                _modelCache[tool] = list;
            }
        }

        if (_host.Settings.AgentOfflineOnly || ticket != _modelLoad || tool != _host.AgentBackend)
        {
            return;
        }

        FillModels(list);
    }

    // While the list is rebuilt the combo box pushes null into AgentModel (its items are gone); that must not overwrite the stored choice.
    private bool _fillingModels;

    private void FillModels(IReadOnlyList<AgentModelInfo> list)
    {
        string current = AgentModel;
        _fillingModels = true;
        AgentModels.Clear();
        if (_host.AgentBackend == AgentTool.Codex)
        {
            AgentModels.Add(new AgentModelInfo(string.Empty, Strings.AgentModelDefault, string.Empty, Array.Empty<string>()));
        }

        foreach (AgentModelInfo model in list)
        {
            AgentModels.Add(model);
        }

        // A model chosen earlier that the program no longer lists stays selectable (a typed id of an older run).
        if (!AgentModels.Any(m => string.Equals(m.Value, current, StringComparison.Ordinal)))
        {
            AgentModels.Add(new AgentModelInfo(current, current.Length == 0 ? Strings.AgentModelDefault : current, string.Empty, Array.Empty<string>()));
        }

        _fillingModels = false;
        OnPropertyChanged(nameof(AgentModel));
        OnPropertyChanged(nameof(SelectedAgentModel));
    }

    /// <summary>The list entry of the model in use (the combo box binds to this one, so the selection never depends on a value lookup).</summary>
    public AgentModelInfo? SelectedAgentModel
    {
        get => AgentModels.FirstOrDefault(m => string.Equals(m.Value, AgentModel, StringComparison.Ordinal));
        set
        {
            if (value != null && !_fillingModels)
            {
                AgentModel = value.Value;
            }
        }
    }

    /// <summary>The model value of the agent in use (<c>--model</c> / <c>-m</c>); Claude Code runs <c>sonnet</c> until another one is chosen.</summary>
    public string AgentModel
    {
        get
        {
            string stored = _host.AgentBackend == AgentTool.Codex ? _host.Settings.AgentModelCodex : _host.Settings.AgentModelClaude;
            return stored.Length == 0 && _host.AgentBackend == AgentTool.Claude ? AgentCommandLine.Model : stored;
        }
        set
        {
            string next = (value ?? string.Empty).Trim();
            if (_fillingModels || string.Equals(next, AgentModel, StringComparison.Ordinal))
            {
                return;
            }

            if (_host.AgentBackend == AgentTool.Codex)
            {
                _host.Settings.AgentModelCodex = next;
            }
            else
            {
                _host.Settings.AgentModelClaude = next;
            }

            _host.PersistSettings();
            OnPropertyChanged();
            OnPropertyChanged(nameof(SelectedAgentModel));
        }
    }

    private AgentRunOptions CurrentOptions()
    {
        string model = AgentModel;
        AgentModelInfo? info = AgentModels.FirstOrDefault(m => string.Equals(m.Value, model, StringComparison.Ordinal));
        return new AgentRunOptions(_agentPrecision, _agentEffort, model, info?.EffortLevels);
    }

    /// <summary>Language of the suggested prompts: <c>en</c> or <c>vi</c> (starts from the UI language, then follows the ENG / VIE buttons).</summary>
    public string PromptLanguage
    {
        get => _promptLanguage;
        set
        {
            string next = string.Equals(value, "vi", StringComparison.OrdinalIgnoreCase) ? "vi" : "en";
            if (SetProperty(ref _promptLanguage, next))
            {
                OnPropertyChanged(nameof(IsPromptEnglish));
                OnPropertyChanged(nameof(IsPromptVietnamese));
            }
        }
    }

    public bool IsPromptEnglish
    {
        get => _promptLanguage == "en";
        set
        {
            if (value) PromptLanguage = "en";
        }
    }

    public bool IsPromptVietnamese
    {
        get => _promptLanguage == "vi";
        set
        {
            if (value) PromptLanguage = "vi";
        }
    }

    /// <summary>Shape types the user wants in the prompt (none on = no restriction).</summary>
    public ObservableCollection<PromptChoice> PromptShapes { get; } = new ObservableCollection<PromptChoice>();

    /// <summary>Labels (classes) the user wants in the prompt (none on = no restriction); follows the class table.</summary>
    public ObservableCollection<PromptChoice> PromptLabels { get; } = new ObservableCollection<PromptChoice>();

    /// <summary>Right-click "New label..." on the label chips (the main view model's command).</summary>
    public IRelayCommand NewLabelCommand => _host.NewLabelCommand;

    private void InitPromptOptions(CanvasViewModel canvas)
    {
        _promptLanguage = string.Equals((Strings.Culture ?? CultureInfo.CurrentUICulture).TwoLetterISOLanguageName, "vi", StringComparison.OrdinalIgnoreCase) ? "vi" : "en";
        foreach ((string key, string name) in ShapeChoices)
        {
            PromptShapes.Add(new PromptChoice(key, Strings.ByName(name)));
        }

        canvas.PropertyChanged -= OnCanvasClassesChanged;
        canvas.PropertyChanged += OnCanvasClassesChanged;
        RebuildPromptLabels();
        _agentPrecision = AgentRunOptions.ParsePrecision(_host.Settings.AgentPrecision);
        _agentEffort = AgentRunOptions.ParseEffort(_host.Settings.AgentEffort);
        FillModels(Array.Empty<AgentModelInfo>());
        _ = LoadAgentModelsAsync();
    }

    private void OnCanvasClassesChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CanvasViewModel.ClassNames))
        {
            RebuildPromptLabels();
        }
    }

    private void RebuildPromptLabels()
    {
        HashSet<string> on = new HashSet<string>(SelectedKeys(PromptLabels), StringComparer.Ordinal);
        PromptLabels.Clear();
        foreach (string name in Canvas.ClassNames)
        {
            if (!string.IsNullOrWhiteSpace(name))
            {
                PromptLabels.Add(new PromptChoice(name, name) { IsSelected = on.Contains(name) });
            }
        }
    }

    private static IReadOnlyList<string> SelectedKeys(IEnumerable<PromptChoice> choices)
        => choices.Where(c => c.IsSelected).Select(c => c.Key).ToList();

    /// <summary>The prompt that is sent to the agent: the text of the box plus the shape types and labels switched on (the box itself is not changed).</summary>
    private string ComposeRunPrompt(string prompt)
    {
        IReadOnlyList<string> shapes = SelectedKeys(PromptShapes);
        IReadOnlyList<string> labels = SelectedKeys(PromptLabels);
        string suffix = CurrentOptions().PromptSuffix;
        if (shapes.Count == 0 && labels.Count == 0 && suffix.Length == 0)
        {
            return prompt;
        }

        string text = prompt;
        if (shapes.Count > 0)
        {
            text += " Draw only these shape types: " + string.Join(", ", shapes) + ".";
            if (shapes.Contains("oriented_rectangle"))
            {
                text += " Give the outline points of each object; the app fits the rotated rectangle.";
            }
        }

        if (labels.Count > 0)
        {
            text += " Use only these labels: " + string.Join(", ", labels.Select(l => "\"" + l + "\"")) + ".";
        }

        return suffix.Length > 0 ? text + " " + suffix : text;
    }

    // ---------- "Suggest prompt" (Agent Prompt source) ----------

    /// <summary>Prompts Claude Code suggested for the current image; a click on one fills the prompt box (it does not run).</summary>
    public ObservableCollection<string> SuggestedPrompts { get; } = new ObservableCollection<string>();

    public bool HasSuggestedPrompts => SuggestedPrompts.Count > 0;

    private void OnSuggestedPromptsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(HasSuggestedPrompts));
    }

    private void ClearSuggestedPrompts()
    {
        if (SuggestedPrompts.Count > 0)
        {
            SuggestedPrompts.Clear();
        }
    }

    private bool CanSuggestPrompt()
    {
        return !_host.Settings.AgentOfflineOnly && AutoLabelSourceRules.CanSuggestPrompt(
            Canvas.Session != null, _host.SelectedImage != null, IsRunning, _host.IsBusy);
    }

    /// <summary>Asks Claude Code to look at the current image and propose prompts. Shares the busy / cancel state of Run (one agent process at a time).</summary>
    [RelayCommand(CanExecute = nameof(CanSuggestPrompt))]
    private async Task SuggestPromptAsync()
    {
        if (RefuseOfflineAgent()) return;
        ImageItemViewModel? item = _host.SelectedImage;
        string? rel = Canvas.CurrentRel;
        if (item == null || rel == null || !string.Equals(item.Rel, rel, StringComparison.OrdinalIgnoreCase))
        {
            AiStatusText = Strings.SuggestNoImage;
            return;
        }

        if (!CanSuggestPrompt())
        {
            return;
        }

        RefreshAgentTools();
        if (_agent == null || _claudePath == null || _mcpPath == null)
        {
            RefreshSource();
            return;
        }

        AgentRunInput? input = BuildAgentInput(item, string.Empty, true);
        if (input == null)
        {
            AiStatusText = Strings.SuggestNoImage;
            return;
        }

        if (!await EnsureAgentConsentAsync())
        {
            AiStatusText = _host.Settings.AgentOfflineOnly ? Strings.AgentOfflineExplanation : Strings.AgentConsentDeclined;
            return;
        }

        if (_agent == null || _host.IsBusy || !string.Equals(Canvas.CurrentRel, rel, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        ClearSuggestedPrompts();
        string language = PromptLanguage;
        IReadOnlyList<string> shapeTypes = SelectedKeys(PromptShapes);
        IReadOnlyList<string> labels = SelectedKeys(PromptLabels);
        AgentRunner agent = _agent;
        int generation = _aiRuns.Begin();
        CancellationTokenSource cts = BeginAiRun(string.Format(Strings.SuggestRunning, AgentToolName()));
        CancellationToken token = cts.Token;
        AgentSpendBudget? suggestBudget = null;
        AiStatusText = string.Empty;
        try
        {
            AgentSpendSnapshot snapshot = await LoadAgentSpendSnapshotAsync();
            if (!_aiRuns.IsCurrent(generation) || token.IsCancellationRequested || !ReferenceEquals(_agent, agent)
                || !ReferenceEquals(_host.SelectedImage, item) || !string.Equals(Canvas.CurrentRel, rel, StringComparison.OrdinalIgnoreCase)
                || RefuseAtDayCap(snapshot))
            {
                return;
            }

            suggestBudget = new AgentSpendBudget(AgentSpendBudget.SingleCallCeilingUsd, snapshot.DayRemainingUsd, 1);
            await ConfigureSpend(agent, suggestBudget, snapshot);
            if (RefuseOfflineAgent()) return;
            AgentRunResult result = await agent.SuggestAsync(input, language, token, shapeTypes, labels);
            if (!_aiRuns.IsCurrent(generation) || token.IsCancellationRequested
                || !string.Equals(Canvas.CurrentRel, rel, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            ApplySuggestResult(result);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _host.LogData("Suggest prompt failed: " + ex);
            if (_aiRuns.IsCurrent(generation))
            {
                AiStatusText = string.Format(Strings.SuggestFailedFormat, ex.Message);
            }
        }
        finally
        {
            suggestBudget?.Close();
            EndAiRun(cts);
        }
    }

    private void ApplySuggestResult(AgentRunResult result)
    {
        switch (result.Kind)
        {
            case AgentOutcomeKind.Success:
                AgentSuggestionParse parsed = AgentSuggestions.Parse(result.ResultText);
                if (!parsed.Ok)
                {
                    AiStatusText = string.Format(Strings.SuggestFailedFormat, Strings.SuggestBadAnswer);
                    return;
                }

                foreach (string prompt in parsed.Prompts)
                {
                    SuggestedPrompts.Add(prompt);
                }

                return;
            case AgentOutcomeKind.NotInstalled:
                RefreshAgentTools();
                RefreshSource();
                return;
            case AgentOutcomeKind.NotSignedIn:
                AiStatusText = AgentNotSignedInText();
                return;
            case AgentOutcomeKind.TimedOut:
                AiStatusText = string.Format(Strings.SuggestTimedOut, AgentToolName());
                return;
            case AgentOutcomeKind.BudgetLimit:
                AiStatusText = Strings.AgentBudgetStopped + (result.IsCostEstimated ? " " + Strings.AgentCostEstimated : string.Empty);
                return;
            case AgentOutcomeKind.Failed:
                _host.LogData("Suggest prompt failed: exit code " + (result.ExitCode?.ToString(CultureInfo.InvariantCulture) ?? "none") + ", " + result.Message);
                string failure = result.Message.Length > 0
                    ? result.Message
                    : string.Format(Strings.AgentExitNoOutputFormat, result.ExitCode ?? -1);
                AiStatusText = string.Format(Strings.SuggestFailedFormat, failure);
                return;
            default:
                return;
        }
    }

    /// <summary>A click on a suggested prompt: it becomes the prompt text; nothing runs.</summary>
    [RelayCommand]
    private void UseSuggestedPrompt(string? prompt)
    {
        if (!string.IsNullOrWhiteSpace(prompt))
        {
            AgentPrompt = prompt!;
        }
    }

    /// <summary>
    /// Stores the proposals of a successful run in the suggestion store of <paramref name="rel"/> (any image, open or not) and returns how many
    /// suggestions were made. For the open image a new answer replaces the previous live answer; for another image it replaces the agent
    /// suggestions it still holds. Showing them on the canvas is <see cref="RefreshSuggestions"/>, done by the caller for the open image.
    /// </summary>
    private int StoreAgentSuggestions(AgentRunResult result, string rel, AgentRunInput input, int classIndex)
    {
        IReadOnlyList<SegAiSuggestion> made = AgentProposals.ToSuggestions(result.Proposals, rel, input.ClassNames, classIndex, result.Model, input.Prompt);
        // Position: agent boxes stay oriented, agent rectangles become oriented boxes, agent polygons are refused (removals stay).
        made = SegAiSuggestionBuilder.ForProjectKind(Canvas.Kind, made, out int positionRefused);
        if (!_aiSuggestions.TryGetValue(rel, out List<SegAiSuggestion>? list))
        {
            list = new List<SegAiSuggestion>();
            _aiSuggestions[rel] = list;
        }

        bool isOpenImage = string.Equals(Canvas.CurrentRel, rel, StringComparison.OrdinalIgnoreCase);
        if (isOpenImage)
        {
            // A new answer replaces the previous (not yet accepted) answer of the agent.
            list.RemoveAll(s => _liveAi.Contains(s));
        }
        else
        {
            list.RemoveAll(AgentOutcomeRecording.IsAgentSuggestion);
        }

        // Safety net (F1): a plain new shape that repeats a labelled part or a suggestion still waiting for review is dropped, also when the
        // answer came from the cache. What is left in the list is exactly the pending set that was sent to the agent.
        made = AgentDuplicateFilter.Filter(made, input.ExistingShapes, list, input.ClassNames, out int dropped);
        if (isOpenImage)
        {
            _liveAi = new HashSet<SegAiSuggestion>(made, ReferenceEqualityComparer.Instance);
        }

        // The words of this run's prompt are matched with the classes the user accepts from it (project vocabulary).
        RunPrompt runPrompt = new RunPrompt(input.Prompt);
        foreach (SegAiSuggestion suggestion in made)
        {
            _suggestionRuns.AddOrUpdate(suggestion, runPrompt);
        }

        list.AddRange(made);
        if (dropped > 0)
        {
            _host.ShowDataNotice(
                Strings.ByName("AgentDuplicatesDroppedTitle"),
                string.Format(CultureInfo.CurrentCulture, Strings.ByName("AgentDuplicatesDroppedFormat"), dropped),
                InfoBarSeverity.Informational);
        }

        if (positionRefused > 0)
        {
            _host.ShowDataNotice(Strings.PositionRefusedTitle, string.Format(CultureInfo.CurrentCulture, Strings.PositionRefusedFormat, positionRefused), InfoBarSeverity.Informational);
        }

        return made.Count;
    }

    /// <summary>
    /// The suggestions of <paramref name="rel"/> that stay pending while a new agent run answers (the ones that run will not replace), as shape
    /// infos for the agent: it must not propose them again. Empty when there are none.
    /// </summary>
    private IReadOnlyList<AgentShapeInfo> PendingShapesOf(string rel, IReadOnlyList<string> classNames)
    {
        if (!_aiSuggestions.TryGetValue(rel, out List<SegAiSuggestion>? list) || list.Count == 0)
        {
            return Array.Empty<AgentShapeInfo>();
        }

        bool isOpenImage = string.Equals(Canvas.CurrentRel, rel, StringComparison.OrdinalIgnoreCase);
        IEnumerable<SegAiSuggestion> kept = isOpenImage
            ? list.Where(s => !_liveAi.Contains(s))
            : list.Where(s => !AgentOutcomeRecording.IsAgentSuggestion(s));
        return AgentDuplicateFilter.ToShapeInfos(kept, classNames);
    }

    /// <summary>Puts a finished agent run into the dashed suggestion layer and the status line; failures become the status line and a notice.</summary>
    private int ApplyAgentResult(AgentRunResult result, string rel, AgentRunInput input, int classIndex, long seconds)
    {
        int proposedCount = 0;
        switch (result.Kind)
        {
            case AgentOutcomeKind.Success:
                int madeCount = StoreAgentSuggestions(result, rel, input, classIndex);
                string cost = "$" + result.CostUsd.ToString("0.000", CultureInfo.InvariantCulture);
                if (result.FromCache)
                {
                    AiStatusText = string.Format(Strings.AgentRunCachedFormat, madeCount);
                }
                else if (madeCount > 0)
                {
                    AiStatusText = string.Format(Strings.AgentRunDoneFormat, madeCount, cost, result.Turns, seconds);
                }
                else
                {
                    AiStatusText = string.Format(Strings.AgentRunNothingFormat, cost, result.Turns, seconds);
                    if (result.ResultText.Length > 0)
                    {
                        AiStatusText += "\n" + string.Format(Strings.AgentSaidFormat, AgentOutcomeClassifier.Limit(result.ResultText));
                    }
                }

                if (result.IsCostEstimated)
                {
                    AiStatusText += " " + Strings.AgentCostEstimated;
                }

                RefreshSuggestions();
                proposedCount = madeCount;
                break;
            case AgentOutcomeKind.NotInstalled:
                RefreshAgentTools();
                RefreshSource();
                AiStatusText = AgentNotFoundText();
                break;
            case AgentOutcomeKind.NotSignedIn:
                AiStatusText = AgentNotSignedInText();
                _host.ShowDataNotice(Strings.AgentRunErrorTitle, AiStatusText, InfoBarSeverity.Warning);
                break;
            case AgentOutcomeKind.TimedOut:
                AiStatusText = string.Format(Strings.AgentTimedOut, AgentToolName());
                _host.ShowDataNotice(Strings.AgentRunErrorTitle, AiStatusText, InfoBarSeverity.Warning);
                break;
            case AgentOutcomeKind.BudgetLimit:
                AiStatusText = Strings.AgentBudgetStopped + (result.IsCostEstimated ? " " + Strings.AgentCostEstimated : string.Empty);
                _host.ShowDataNotice(Strings.AgentRunErrorTitle, AiStatusText, InfoBarSeverity.Warning);
                break;
            case AgentOutcomeKind.Failed:
                // The persistent log gets the exit code and the output length only; the status line may show the first 300 characters.
                _host.LogData(result.ExitCode.HasValue
                    ? "Agent run failed: exit code " + result.ExitCode.Value.ToString(CultureInfo.InvariantCulture) + ", output length " + result.OutputLength.ToString(CultureInfo.InvariantCulture)
                    : "Agent run failed: " + result.Message);
                string failure = result.Message.Length > 0
                    ? result.Message
                    : string.Format(Strings.AgentExitNoOutputFormat, result.ExitCode ?? -1);
                AiStatusText = string.Format(Strings.AgentFailedFormat, AgentToolName(), failure);
                _host.ShowDataNotice(Strings.AgentRunErrorTitle, AiStatusText, InfoBarSeverity.Warning);
                break;
            default:
                AiStatusText = string.Empty;
                break;
        }

        return proposedCount;
    }

    // ---------- chat history (user view only; never read into a prompt) ----------

    /// <summary>Appends the "user" entry at the start of a run. A failing history never disturbs the run (the log swallows I/O errors).</summary>
    private void RecordChatStart(string mode, string prompt, IReadOnlyList<string> images)
    {
        EasyLabel.Core.Io.AgentChatLog.Append(
            _host.Project?.Paths.AgentChatFile,
            new EasyLabel.Core.Io.AgentChatEntry(DateTime.UtcNow, "user", mode, _host.Settings.AgentEffort, prompt, images, 0, 0, 0, null, null, string.Empty, string.Empty));
        ReloadChat();
    }

    /// <summary>Appends the "agent" entry at the end of a run. Accepted / rejected stay 0: the review decisions are not added to the history.</summary>
    private void RecordChatEnd(string mode, IReadOnlyList<string> images, int proposed, double? cost, int? turns, string outcome, string error)
    {
        decimal? usd = cost.HasValue && cost.Value >= 0 && !double.IsNaN(cost.Value) && !double.IsInfinity(cost.Value) && cost.Value < 1e6
            ? (decimal)cost.Value
            : (decimal?)null;
        // The live row ends here: the final entry (with the agent's closing text, at most 500 characters) takes its place, so nothing shows twice.
        string closingText = EndLive();
        EasyLabel.Core.Io.AgentChatLog.Append(
            _host.Project?.Paths.AgentChatFile,
            new EasyLabel.Core.Io.AgentChatEntry(DateTime.UtcNow, "agent", mode, _host.Settings.AgentEffort, closingText, images, proposed, 0, 0, usd, turns, outcome, error));
        NoteAgentScanned(images, proposed, outcome);
        ReloadChat();
    }

    private void OnAiPromptRequested(object? sender, AiPromptEventArgs e)
    {
        if (e.IsBox)
        {
            // A box starts a new object: the point results so far are final.
            _liveAi = new HashSet<SegAiSuggestion>(ReferenceEqualityComparer.Instance);
        }

        _ = RunAiAsync(e.Prompt, !e.IsBox);
    }

    private void OnAiPromptCommitted(object? sender, EventArgs e)
    {
        if (_aiRunActive)
        {
            // The run in flight computes the final clicks: it ends the live set when it lands.
            _aiCommitPending = true;
        }
        else
        {
            _liveAi = new HashSet<SegAiSuggestion>(ReferenceEqualityComparer.Instance);
        }
    }

    /// <summary>The pixels of one image were replaced (Capture to current): its auto-label result and the suggestions kept for it are stale.</summary>
    internal void InvalidateImage(string rel)
    {
        _service?.Invalidate(rel);
        _aiSuggestions.Remove(rel);
        _dismissed.Remove(rel);
    }

    private void OnAiPromptCleared(object? sender, EventArgs e)
    {
        // Esc: stop the run, forget its result and take the live results of this prompt out of the layer.
        _aiRuns.Invalidate();
        CancelAiRunIfActive();
        string? rel = Canvas.CurrentRel;
        if (rel != null && _aiSuggestions.TryGetValue(rel, out List<SegAiSuggestion>? list))
        {
            list.RemoveAll(s => _liveAi.Contains(s));
        }

        _liveAi = new HashSet<SegAiSuggestion>(ReferenceEqualityComparer.Instance);
        _aiCommitPending = false;
        AiStatusText = string.Empty;
        RefreshSuggestions();
    }

    private void OnImageReplaced(object? sender, EventArgs e)
    {
        // The user left the image: what they changed in the shapes they accepted is the last word on those suggestions.
        RecordEditsOfAccepted(null);

        // A result for the image that was shown must never land on the next one.
        _aiRuns.Invalidate();
        CancelAiRunIfActive();
        _liveAi = new HashSet<SegAiSuggestion>(ReferenceEqualityComparer.Instance);
        _aiCommitPending = false;
        _aiImage = null;
        AiStatusText = string.Empty;
        ClearSuggestedPrompts();
    }

    private void CancelAiRunIfActive()
    {
        if (_aiRunActive)
        {
            _cts?.Cancel();
        }
    }

    /// <summary>Stops an AI run, drops the decoded image and the loaded model (GPU memory); suggestions already shown stay.</summary>
    private void ReleaseAi()
    {
        _aiRuns.Invalidate();
        CancelAiRunIfActive();
        // Synchronous: claude.exe and its MCP child (SAM2 loaded) must be gone before the project is released or the app closes.
        _agent?.CancelAndKill();
        foreach (AgentRunner extra in _batchWorkerRunners)
        {
            extra.CancelAndKill();
        }

        _liveAi = new HashSet<SegAiSuggestion>(ReferenceEqualityComparer.Instance);
        _aiCommitPending = false;
        _aiImage = null;
        _aiPreloadKey = null;
        AiStatusText = string.Empty;
        AiBackendService? service = _aiService;
        _aiService = null;
        service?.Dispose();
    }

    private async Task<AiImage> GetAiImageAsync(string path, int generation, CancellationToken ct)
    {
        string key = AiImageLoader.KeyOf(path);
        AiImage? cached = _aiImage;
        if (cached != null && string.Equals(cached.CacheKey, key, StringComparison.Ordinal))
        {
            return cached;
        }

        AiImage image = await Task.Run(() => AiImageLoader.Load(path, key, ct), ct);
        if (_aiRuns.IsCurrent(generation))
        {
            _aiImage = image;
        }

        return image;
    }

    private CancellationTokenSource BeginAiRun(string text)
    {
        CancellationTokenSource cts = new CancellationTokenSource();
        CancellationTokenSource? previous = _cts;
        _cts = cts;
        _aiRunActive = true;
        _aiCommitPending = false;
        previous?.Cancel();
        IsProgressIndeterminate = true;
        ProgressValue = 0;
        ProgressText = text;
        IsRunning = true;
        Canvas.AiBusy = true;
        CancelRunCommand.NotifyCanExecuteChanged();
        return cts;
    }

    private void EndAiRun(CancellationTokenSource cts)
    {
        if (ReferenceEquals(_cts, cts))
        {
            _cts = null;
            _aiRunActive = false;
            IsRunning = false;
            Canvas.AiBusy = false;
            CancelRunCommand.NotifyCanExecuteChanged();
            AutoLabelCurrentCommand.NotifyCanExecuteChanged();
            AutoLabelAllCommand.NotifyCanExecuteChanged();
        }

        cts.Dispose();
    }

    private string AiModelDisplayName()
    {
        AiModelStatus? status = _host.AiModels.FirstOrDefault(m => m.Id == _aiModelId);
        return status == null ? _aiModelId : status.DisplayName;
    }

    /// <summary>
    /// Runs the active AI model with a prompt on the current image and puts the result into the dashed suggestion layer. A newer prompt,
    /// another image or Esc makes this run stale: its result is dropped. <paramref name="replaceLive"/> replaces the results of the
    /// previous run of the same object (point clicks, YOLO-World Run) instead of adding to them.
    /// </summary>
    private async Task RunAiAsync(AiPrompt prompt, bool replaceLive)
    {
        ImageItemViewModel? item = _host.SelectedImage;
        string? rel = Canvas.CurrentRel;
        if (item == null || rel == null || !string.Equals(item.Rel, rel, StringComparison.OrdinalIgnoreCase)
            || _source != AutoLabelSource.AiModel || _host.IsBusy
            || !AutoLabelSourceRules.IsBackendAvailable(_source, ModelFilesFound()))
        {
            return;
        }

        AiModelStatus? status = _host.AiModels.FirstOrDefault(m => m.Id == _aiModelId);
        if (status == null)
        {
            return;
        }

        string modelId = _aiModelId;
        string folder = status.Location;
        string path = item.FullPath;
        string displayName = status.DisplayName;
        int classIndex = Canvas.ActiveClassIndex;
        int generation = _aiRuns.Begin();
        CancellationTokenSource cts = BeginAiRun(string.Format(Strings.AiRunningFormat, displayName));
        CancellationToken token = cts.Token;
        Progress<string> loading = new Progress<string>(_ =>
        {
            if (_aiRuns.IsCurrent(generation))
            {
                ProgressText = string.Format(Strings.AiLoadingFormat, displayName);
            }
        });

        try
        {
            AiImage image = await GetAiImageAsync(path, generation, token);
            AiPrompt effective = prompt;
            int offsetX = 0;
            int offsetY = 0;
            if ((modelId == AiModelCatalog.YoloWorldId || modelId == AiModelCatalog.CustomDetectId) && prompt.Box.HasValue)
            {
                // YOLO-World / Custom YOLO with a dragged box: detect inside the box only (the crop is detected, results are shifted back).
                AiBox box = prompt.Box.Value;
                AiCrop crop = await Task.Run(() => AiSuggestionMapper.Crop(image, box), token);
                image = crop.Image;
                offsetX = crop.OffsetX;
                offsetY = crop.OffsetY;
                effective = new AiPrompt();
            }

            _aiService ??= new AiBackendService();
            AiRunOutcome outcome = await _aiService.RunAsync(modelId, folder, image, effective, loading, token);
            if (!_aiRuns.IsCurrent(generation) || token.IsCancellationRequested
                || !string.Equals(Canvas.CurrentRel, rel, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            List<SegAiSuggestion> made = new List<SegAiSuggestion>();
            foreach (AiProposal proposal in outcome.Proposals)
            {
                SegAiSuggestion? suggestion = AiSuggestionMapper.ToSuggestion(
                    AiSuggestionMapper.Offset(proposal, offsetX, offsetY), rel, classIndex, outcome.BackendName, outcome.ModelFile);
                if (suggestion != null)
                {
                    made.Add(suggestion);
                }
            }

            // Position: a box result becomes an oriented box; a polygon result is refused (no suggestion, one notice).
            made = SegAiSuggestionBuilder.ForProjectKind(Canvas.Kind, made, out int positionRefused);
            if (positionRefused > 0)
            {
                _host.ShowDataNotice(Strings.PositionRefusedTitle, string.Format(CultureInfo.CurrentCulture, Strings.PositionRefusedFormat, positionRefused), InfoBarSeverity.Informational);
            }

            if (!_aiSuggestions.TryGetValue(rel, out List<SegAiSuggestion>? list))
            {
                list = new List<SegAiSuggestion>();
                _aiSuggestions[rel] = list;
            }

            if (replaceLive)
            {
                list.RemoveAll(s => _liveAi.Contains(s));
                _liveAi = new HashSet<SegAiSuggestion>(made, ReferenceEqualityComparer.Instance);
            }

            list.AddRange(made);
            if (_aiCommitPending)
            {
                _liveAi = new HashSet<SegAiSuggestion>(ReferenceEqualityComparer.Instance);
                _aiCommitPending = false;
            }

            string provider = AiSuggestionMapper.ProviderText(outcome.Provider, outcome.FallbackReason, Strings.AiProviderFallbackFormat);
            AiStatusText = made.Count == 0
                ? string.Format(Strings.AiRunNothingFormat, outcome.BackendName, provider, outcome.Milliseconds)
                : string.Format(Strings.AiRunDoneFormat, outcome.BackendName, provider, made.Count, outcome.Milliseconds);
            if (modelId == AiModelCatalog.CustomDetectId)
            {
                AiStatusText += "\n" + Strings.AiCustomDetectNote;
            }

            RefreshSuggestions();
        }
        catch (OperationCanceledException)
        {
        }
        catch (AiModelException ex)
        {
            _host.LogData("AI model failed: " + ex.Message);
            if (_aiRuns.IsCurrent(generation))
            {
                _host.ShowDataNotice(Strings.AiRunErrorTitle, string.Format(Strings.AiModelErrorFormat, ex.Message), InfoBarSeverity.Warning);
            }
        }
        catch (Exception ex)
        {
            _host.LogData("AI run failed: " + ex);
            if (_aiRuns.IsCurrent(generation))
            {
                _host.ShowDataNotice(Strings.AiRunErrorTitle, string.Format(Strings.AiRunFailedFormat, ex.Message), InfoBarSeverity.Warning);
            }
        }
        finally
        {
            EndAiRun(cts);
        }
    }

    // ---------- auto-label ----------

    private bool CanAutoLabelCurrent() => !_aiRunActive && LabelStepRules.CanAutoLabelCurrent(
        _host.HasSession, _host.IsBusy, _activeModel != null, _host.SelectedImage != null);

    private bool CanAutoLabelAll() => !_aiRunActive && LabelStepRules.CanAutoLabelAll(
        _host.HasSession, _host.IsBusy, _activeModel != null, _unlabelled);

    private static void EnsureModelLoaded(SegAiSession session)
    {
        // A project opens without a loaded model, and the Inspect tab may have loaded another one (the session holds a single model).
        // Auto-label always works with the active model, so load it whenever the loaded model is a different one.
        string? active = session.ActiveModel;
        if (active == null)
        {
            return;
        }

        SegAiModelInfo? loaded = session.LoadedModel;
        if (loaded == null || !string.Equals(System.IO.Path.GetFileName(loaded.Path), active, StringComparison.OrdinalIgnoreCase))
        {
            session.SetActiveModel(active);
        }
    }

    private CancellationTokenSource BeginRun(bool indeterminate, int total)
    {
        CancellationTokenSource cts = new CancellationTokenSource();
        _cts = cts;
        IsProgressIndeterminate = indeterminate;
        ProgressValue = 0;
        ProgressText = indeterminate ? string.Empty : string.Format(Strings.ProgressFormat, 0, total);
        IsRunning = true;
        CancelRunCommand.NotifyCanExecuteChanged();
        return cts;
    }

    private void EndRun(CancellationTokenSource cts)
    {
        if (ReferenceEquals(_cts, cts))
        {
            _cts = null;
        }

        cts.Dispose();
        IsRunning = false;
        CancelRunCommand.NotifyCanExecuteChanged();
    }

    /// <summary>The localized text of an auto-label error; the one known Position error has its own string, others are shown as they are.</summary>
    private static string AutoLabelErrorText(string error)
    {
        return string.Equals(error, SegAiSession.InstanceHeadMissingMessage, StringComparison.Ordinal) ? Strings.AutoLabelNeedInstanceHead : error;
    }

    [RelayCommand(CanExecute = nameof(CanAutoLabelCurrent))]
    private async Task AutoLabelCurrentAsync()
    {
        ImageItemViewModel? item = _host.SelectedImage;
        AutoLabelService? service = _service;
        if (item == null || service == null)
        {
            return;
        }

        string rel = item.Rel;
        _dismissed.Remove(rel);
        AutoLabelImageResult? result = null;
        CancellationTokenSource cts = BeginRun(true, 1);
        bool ok;
        try
        {
            CancellationToken token = cts.Token;
            ok = await _host.RunLabelAsync(
                async session =>
                {
                    await Task.Run(() => EnsureModelLoaded(session), token);
                    result = await service.RunAsync(rel, token);
                },
                Strings.AutoLabelErrorTitle);
        }
        finally
        {
            EndRun(cts);
        }

        RefreshSuggestions();
        _host.Panels.Files.RefreshOrder();
        if (!ok || result == null)
        {
            return;
        }

        if (result.Error != null)
        {
            _host.ShowDataNotice(Strings.AutoLabelErrorTitle, AutoLabelErrorText(result.Error), InfoBarSeverity.Warning);
        }
        else if (result.Suggestions.Count == 0)
        {
            _host.ShowDataNotice(Strings.AutoLabelHeader, Strings.AutoLabelNoneFound, InfoBarSeverity.Informational);
        }

        await RefreshSelectedItemAsync();
    }

    [RelayCommand(CanExecute = nameof(CanAutoLabelAll))]
    private async Task AutoLabelAllAsync()
    {
        AutoLabelService? service = _service;
        if (service == null)
        {
            return;
        }

        List<string> rels = _host.Images.Where(i => i.HasInfo && !i.HasRegions).Select(i => i.Rel).ToList();
        if (rels.Count == 0)
        {
            return;
        }

        foreach (string rel in rels)
        {
            _dismissed.Remove(rel);
        }

        int total = rels.Count;
        IReadOnlyList<AutoLabelImageResult>? results = null;
        CancellationTokenSource cts = BeginRun(false, total);
        bool ok;
        try
        {
            CancellationToken token = cts.Token;
            Progress<AutoLabelProgress> progress = new Progress<AutoLabelProgress>(p =>
            {
                ProgressValue = p.Total <= 0 ? 0 : 100.0 * p.Completed / p.Total;
                ProgressText = string.Format(Strings.ProgressFormat, p.Completed, p.Total);
            });
            ok = await _host.RunLabelAsync(
                async session =>
                {
                    await Task.Run(() => EnsureModelLoaded(session), token);
                    results = await service.RunAsync(rels, progress, token);
                },
                Strings.AutoLabelErrorTitle);
        }
        finally
        {
            EndRun(cts);
        }

        // Cancelled or finished: whatever was computed stays cached and shows when its image is opened.
        RefreshSuggestions();
        _host.Panels.Files.RefreshOrder();
        if (ok && results != null)
        {
            List<AutoLabelImageResult> failed = results.Where(r => r.Error != null).ToList();
            if (failed.Count > 0)
            {
                _host.ShowDataNotice(
                    Strings.AutoLabelErrorTitle,
                    string.Format(Strings.AutoLabelSkippedFormat, failed.Count, AutoLabelErrorText(failed[0].Error!)),
                    InfoBarSeverity.Warning);
            }
        }

        await RefreshSelectedItemAsync();
    }

    /// <summary>Edits made while a run held the busy flag were not re-read; read the open image's state back once it is free.</summary>
    private async Task RefreshSelectedItemAsync()
    {
        ImageItemViewModel? selected = _host.SelectedImage;
        if (selected != null)
        {
            await _host.Data.RefreshItemAsync(selected);
        }
    }

    // A single-image Segment run is indeterminate and cannot be interrupted once the native call started, so only list runs offer Cancel.
    // An AI model run is cancelled through its token.
    private bool CanCancel() => IsRunning && (!IsProgressIndeterminate || _aiRunActive);

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void CancelRun()
    {
        _cts?.Cancel();
    }

    // ---------- suggestions ----------

    private bool CanAcceptAll() => LabelStepRules.CanAccept(Canvas.Session != null, Canvas.InputBlocked, Canvas.Suggestions.Count);

    private bool CanAcceptSelected() => LabelStepRules.CanAccept(Canvas.Session != null, Canvas.InputBlocked, Canvas.SelectedSuggestions.Count);

    private bool CanRejectAll() => Canvas.Suggestions.Count > 0;

    [RelayCommand(CanExecute = nameof(CanAcceptAll))]
    private void AcceptAll()
    {
        Canvas.AcceptSuggestions(false);
    }

    [RelayCommand(CanExecute = nameof(CanAcceptSelected))]
    private void AcceptSelected()
    {
        Canvas.AcceptSuggestions(true);
    }

    [RelayCommand(CanExecute = nameof(CanRejectAll))]
    private void RejectAll()
    {
        Canvas.RejectSuggestions(false);
    }

    // ---------- Train / Val ----------

    private bool CanSetMode() => _host.Data.CanSetMode(_host.SelectedImage);

    /// <summary>Segmented Train/Val of the current image: the same helper as the file rows, only when the image is not already in the requested set.</summary>
    [RelayCommand(CanExecute = nameof(CanSetMode))]
    private async Task SetModeAsync(SegTrainingMode target)
    {
        ImageItemViewModel? item = _host.SelectedImage;
        if (item == null || item.Mode == target)
        {
            return;
        }

        await _host.Data.SetModeAsync(item, target);
        OnPropertyChanged(nameof(IsTrainMode));
        OnPropertyChanged(nameof(IsValMode));
    }
}
