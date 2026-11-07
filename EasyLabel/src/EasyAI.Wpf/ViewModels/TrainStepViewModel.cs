using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EasyAI.Desktop.Infrastructure;
using EasyAI.Desktop.Resources;
using EasyLabel.SegAI;
using EasyLabel.SegAI.Data;
using EasyLabel.SegAI.Native;
using EasyLabel.SegAI.Options;
using EasyLabel.SegAI.Training;
using EasyLabel.SegAI.Training.Ultralytics;
using InfoBarSeverity = Wpf.Ui.Controls.InfoBarSeverity;

namespace EasyAI.Desktop.ViewModels;

/// <summary>One row of the candidate list (a registry model with its parsed report summary).</summary>
public sealed class TrainCandidateRow
{
    public TrainCandidateRow(SegAiCandidateInfo info, CultureInfo culture)
    {
        Info = info;
        FileName = info.Entry.File;
        StateText = TrainStepRules.StateText(info);
        DetailText = TrainStepRules.CreatedText(info, culture)
            + " · " + Strings.MetricMap50 + " " + TrainStepRules.Map50Text(info, culture)
            + " · " + TrainStepRules.ElapsedText(info);
    }

    public SegAiCandidateInfo Info { get; }

    public string FileName { get; }

    public string StateText { get; }

    public string DetailText { get; }
}

/// <summary>One entry of the YOLO / RT-DETR family combo (the trainer's family plus its display text).</summary>
public sealed class UltraFamilyRow
{
    public UltraFamilyRow(UltraFamily family)
    {
        Family = family;
        Label = TrainStepRules.FamilyLabel(family, Strings.UltraSuffixExperimental, Strings.UltraSuffixNeedsVram);
    }

    public UltraFamily Family { get; }

    public string Label { get; }

    public override string ToString() => Label;
}

/// <summary>One row of the YOLO / RT-DETR runs list (a stored <c>ultra-run.json</c>).</summary>
public sealed class UltraRunRow
{
    public UltraRunRow(UltraRunRecord record, CultureInfo culture)
    {
        Record = record;
        NameText = record.RunName;
        string map = record.Map50 is double value && !double.IsNaN(value) && !double.IsInfinity(value) ? value.ToString("P1", culture) : "-";
        DetailText = string.Format(culture, Strings.UltraRunDetailFormat, record.Family, map, Strings.ByName(TrainStepRules.UltraStatusKey(record.Status)));
    }

    public UltraRunRecord Record { get; }

    public string NameText { get; }

    public string DetailText { get; }
}

/// <summary>One row of the candidate versus active comparison table.</summary>
public sealed class ComparisonRowViewModel
{
    public ComparisonRowViewModel(SegAiComparisonRow row, CultureInfo culture)
    {
        string name = Strings.ByName(TrainStepRules.MetricKey(row.Metric));
        MetricText = row.Metric == SegAiComparisonMetric.ClassAp50 ? string.Format(name, row.Label) : name;
        CandidateText = TrainStepRules.FormatValue(row.Metric, row.Candidate, culture);
        ActiveText = TrainStepRules.FormatValue(row.Metric, row.Active, culture);
        DeltaText = TrainStepRules.FormatDelta(row.Metric, row.Delta, culture);
        Verdict = row.Verdict;
        VerdictText = Strings.ByName(TrainStepRules.VerdictKey(row.Verdict));
    }

    public string MetricText { get; }

    public string CandidateText { get; }

    public string ActiveText { get; }

    public string DeltaText { get; }

    public SegAiVerdict Verdict { get; }

    public string VerdictText { get; }
}

/// <summary>
/// Step 3 (Train): dataset summary, Epochs/Input size, Start/Stop, live progress and curves, the model registry list with the
/// candidate versus active comparison and "Use this model". While a run is active the whole app is busy (<see cref="MainViewModel.IsBusy"/>
/// and canvas input blocked), so the Step 1 and Step 2 commands are disabled through their real CanExecute.
/// </summary>
public sealed partial class TrainStepViewModel : ObservableObject
{
    private readonly MainViewModel _host;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _ticker;
    private readonly List<SegEpochMetric> _epochs = new List<SegEpochMetric>();
    private readonly Stopwatch _clock = new Stopwatch();
    private SegAiTrainingService? _service;
    private CancellationTokenSource? _cts;
    private TaskCompletionSource<bool>? _idle;
    private SegAiTrainBlock _environmentBlock = SegAiTrainBlock.None;
    private IReadOnlyList<SegAiCandidateInfo> _candidates = new List<SegAiCandidateInfo>();
    private int _candidatesVersion;
    private int _environmentVersion;
    private int _runTotalEpochs;
    private int _lastPercent;
    private bool _stopRequested;
    private int _etaEpoch;

    // The hardware is read once per app run (static), on a background task, and shared by every panel instance.
    private static Task<EasyLabel.Core.Hardware.HardwareInfo?>? s_hardware;
    private EasyLabel.Core.Hardware.HardwareInfo? _hardwareInfo;

    [ObservableProperty]
    private double? _epochsValue = TrainStepRules.DefaultEpochs;

    // 0 = automatic; otherwise stop when the best validation loss has not improved for this many epochs (5..200).
    [ObservableProperty]
    private double? _earlyStopValue = 0;

    [ObservableProperty]
    private int _selectedInputSize = TrainStepRules.DefaultInputSize;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    [NotifyPropertyChangedFor(nameof(HasFix))]
    private bool _isRunning;

    [ObservableProperty]
    private double _progressValue;

    [ObservableProperty]
    private string _progressText = string.Empty;

    [ObservableProperty]
    private string _startToolTip = Strings.TrainStart;

    [ObservableProperty]
    private TrainCandidateRow? _selectedCandidate;

    [ObservableProperty]
    private bool _hasComparison;

    [ObservableProperty]
    private bool _differentValidationSet;

    [ObservableProperty]
    private bool _boxOverextension;

    [ObservableProperty]
    private bool _noActiveModel;

    /// <summary>The trainer choice of this session only (not persisted): false = Segment AI, true = YOLO / RT-DETR.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSegmentKind))]
    [NotifyPropertyChangedFor(nameof(ShowUltraRuns))]
    private bool _isUltraKind;

    [ObservableProperty]
    private UltraFamilyRow? _selectedFamily;

    /// <summary>Batch size of the YOLO run; empty or 0 = the family default.</summary>
    [ObservableProperty]
    private double? _batchValue;

    [ObservableProperty]
    private string _ultraStatusText = string.Empty;

    [ObservableProperty]
    private UltraRunRow? _selectedUltraRun;

    /// <summary>The one-click fix offered next to the block text; <see cref="TrainStepRules.TrainFixKind.None"/> hides the row.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFix))]
    [NotifyPropertyChangedFor(nameof(FixButtonText))]
    private TrainStepRules.TrainFixKind _fixKind;

    /// <summary>The block reason shown with the fix button.</summary>
    [ObservableProperty]
    private string _fixText = string.Empty;

    /// <summary>"Recommended for this PC: mode - reason"; empty while the hardware probe has no result.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRecommendation))]
    private string _recommendedText = string.Empty;

    /// <summary>"About N min left" after the first finished epoch; empty before it and after the run.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasEta))]
    private string _etaText = string.Empty;

    public bool HasRecommendation => RecommendedText.Length > 0;

    public bool HasEta => EtaText.Length > 0;

    public bool HasFix => FixKind != TrainStepRules.TrainFixKind.None && !IsRunning;

    /// <summary>Caption of the "Use recommended settings" button.</summary>
    public string UseRecommendedText => TrainRecommendationRules.Resolve(Strings.ByName, "TrainUseRecommended", "Use recommended settings");

    public string AutoAssignValText => Strings.ByName("TrainAutoAssignVal");

    /// <summary>Caption of the single fix button for the current <see cref="FixKind"/>.</summary>
    public string FixButtonText
    {
        get
        {
            string? key = TrainStepRules.FixButtonKey(FixKind);
            return key == null ? string.Empty : Strings.ByName(key);
        }
    }

    private UltraTrainService? _ultraService;
    private TrainStepRules.UltraEnvironment _ultraEnv = TrainStepRules.UltraEnvironment.Unknown;
    private int _ultraVersion;
    private string _ultraGpu = string.Empty;
    private int? _ultraVramMb;
    private string _ultraMessage = string.Empty;
    private bool _ultraRun;
    private UltraEvent? _ultraLast;
    private int _ultraRunsVersion;

    /// <summary>Minutes left of the current run after the last finished epoch (null before the first one); shown in the CPU status line.</summary>
    private int? _runEtaMinutes;

    public TrainStepViewModel(MainViewModel host)
    {
        _host = host;
        _dispatcher = Dispatcher.CurrentDispatcher;
        InputSizes = TrainStepRules.InputSizeChoices();
        Families = new ObservableCollection<UltraFamilyRow>();
        UltraRuns = new ObservableCollection<UltraRunRow>();
        Candidates = new ObservableCollection<TrainCandidateRow>();
        ComparisonRows = new ObservableCollection<ComparisonRowViewModel>();

        _ticker = new DispatcherTimer(DispatcherPriority.Background, _dispatcher) { Interval = TimeSpan.FromSeconds(1) };
        _ticker.Tick -= OnTick;
        _ticker.Tick += OnTick;

        // The Train/Val counts of the Step 1 summary decide the first two block reasons without touching the disk.
        host.Data.PropertyChanged -= OnDataPropertyChanged;
        host.Data.PropertyChanged += OnDataPropertyChanged;

        _ = LoadHardwareAsync();
    }

    /// <summary>Raised on the UI thread when the epoch series changed (the view redraws throttled).</summary>
    public event EventHandler? ChartInvalidated;

    /// <summary>The options can be edited (no run of this panel is active).</summary>
    public bool IsIdle => !IsRunning;

    public IReadOnlyList<int> InputSizes { get; }

    public bool IsSegmentKind => !IsUltraKind && !IsClassifyKind;

    public ObservableCollection<UltraFamilyRow> Families { get; }

    public ObservableCollection<UltraRunRow> UltraRuns { get; }

    /// <summary>The YOLO / RT-DETR runs block: while that trainer is chosen, or whenever stored runs exist.</summary>
    public bool ShowUltraRuns => IsUltraKind || UltraRuns.Count > 0;

    public ObservableCollection<TrainCandidateRow> Candidates { get; }

    public ObservableCollection<ComparisonRowViewModel> ComparisonRows { get; }

    /// <summary>The curves are shown during a run and after it until the next run or project change.</summary>
    public bool HasChart => IsRunning || _epochs.Count > 0;

    /// <summary>Epoch count the chart's X axis spans: the running (or last) run's total, at least the newest reported epoch.</summary>
    public int ChartTotalEpochs => Math.Max(_runTotalEpochs, TrainStepRules.EpochsDone(_epochs));

    /// <summary>Copy of the reported epochs (UI thread).</summary>
    public IReadOnlyList<SegEpochMetric> EpochSnapshot() => _epochs.ToArray();

    // ---------- session / state ----------

    /// <summary>Binds the project session (null = none): new service, empty chart, fresh candidate list.</summary>
    internal void Rebind(SegAiSession? session)
    {
        _cts?.Cancel();
        if (_service != null)
        {
            _service.Changed -= OnServiceChanged;
        }

        _service = session == null ? null : new SegAiTrainingService(session);
        if (_service != null)
        {
            _service.Changed -= OnServiceChanged;
            _service.Changed += OnServiceChanged;
        }

        _environmentBlock = SegAiTrainBlock.None;
        ClearRun();
        _candidatesVersion++;
        Candidates.Clear();
        _candidates = new List<SegAiCandidateInfo>();
        SelectedCandidate = null;
        RefreshState();
        if (session != null)
        {
            _ = RefreshCandidatesAsync(null);
            _ = RefreshEnvironmentAsync();
        }

        _ = RefreshUltraRunsAsync(null);
    }

    /// <summary>Busy flag, session or selection changed: re-evaluates the commands and the block reason.</summary>
    internal void NotifyStateChanged()
    {
        RefreshState();
        if (!_host.IsBusy && _host.CurrentStep == WorkflowStep.Train)
        {
            _ = RefreshEnvironmentAsync();
        }
    }

    /// <summary>The current step changed; this step re-reads the registry and the environment when it becomes the visible one.</summary>
    internal void OnStepChanged()
    {
        if (_host.CurrentStep != WorkflowStep.Train || !_host.HasSession)
        {
            return;
        }

        _ = RefreshCandidatesAsync(null);
        _ = RefreshEnvironmentAsync();
        _ = RefreshUltraRunsAsync(null);
        if (IsUltraKind)
        {
            _ = LoadUltraAsync();
        }

        TryAutoApplyRecommended();
    }

    // ---------- hardware recommendation ----------

    private async Task LoadHardwareAsync()
    {
        EasyLabel.Core.Hardware.HardwareInfo? info = null;
        try
        {
            // One probe per app run; never on the UI thread. A failure leaves the recommendation hidden.
            Task<EasyLabel.Core.Hardware.HardwareInfo?> probe = s_hardware ??= Task.Run<EasyLabel.Core.Hardware.HardwareInfo?>(() =>
            {
                try
                {
                    return new EasyLabel.Core.Hardware.HardwareProbe().Probe();
                }
                catch (Exception)
                {
                    return null;
                }
            });
            info = await probe;
        }
        catch (Exception ex)
        {
            _host.LogData("Hardware probe failed: " + ex.Message);
        }

        if (info == null)
        {
            return;
        }

        _hardwareInfo = info;
        RefreshRecommendation();
        TryAutoApplyRecommended();
    }

    private EasyLabel.Core.Hardware.RecommendedSettings? CurrentRecommendation(bool forSegment)
    {
        EasyLabel.Core.Hardware.HardwareInfo? info = _hardwareInfo;
        if (info == null)
        {
            return null;
        }

        return EasyLabel.Core.Hardware.RecommendedSettings.For(
            info,
            forSegment || !IsUltraKind ? EasyLabel.Core.Hardware.HardwareTask.SegmentAiTrain : EasyLabel.Core.Hardware.HardwareTask.YoloTrain);
    }

    private void RefreshRecommendation()
    {
        EasyLabel.Core.Hardware.RecommendedSettings? recommended = CurrentRecommendation(false);
        RecommendedText = recommended == null ? string.Empty : TrainRecommendationRules.Text(recommended, Strings.ByName);
        UseRecommendedCommand.NotifyCanExecuteChanged();
    }

    private TrainOptionValues CurrentOptionValues() => new TrainOptionValues(EpochsValue, SelectedInputSize, IsUltraKind, BatchValue);

    private void SetOptionValues(TrainOptionValues values)
    {
        // Set the trainer kind first: only options that exist for it were changed by Apply.
        if (IsUltraKind != values.IsUltra)
        {
            IsUltraKind = values.IsUltra;
        }

        EpochsValue = values.Epochs;
        SelectedInputSize = values.InputSize;
        BatchValue = values.Batch;
    }

    /// <summary>The first time the Train tab is shown with a recommendation: pre-fills default options once (never overwrites changed ones).</summary>
    private void TryAutoApplyRecommended()
    {
        if (_hardwareInfo == null || IsRunning || _host.CurrentStep != WorkflowStep.Train || !_host.HasSession || _host.Settings.TrainRecommendedApplied)
        {
            return;
        }

        EasyLabel.Core.Hardware.RecommendedSettings? recommended = CurrentRecommendation(true);
        if (recommended != null)
        {
            SetOptionValues(TrainRecommendationRules.Apply(recommended, CurrentOptionValues(), false));
        }

        _host.Settings.TrainRecommendedApplied = true;
        try
        {
            _host.PersistSettings();
        }
        catch (Exception ex)
        {
            _host.LogData("Saving the Train recommendation flag failed: " + ex.Message);
        }
    }

    private bool CanUseRecommended() => HasRecommendation && IsIdle;

    /// <summary>"Use recommended settings": sets the options the trainer has from the recommendation for this PC (Safe also picks Segment AI).</summary>
    [RelayCommand(CanExecute = nameof(CanUseRecommended))]
    private void UseRecommended()
    {
        EasyLabel.Core.Hardware.RecommendedSettings? recommended = CurrentRecommendation(false);
        if (recommended != null)
        {
            SetOptionValues(TrainRecommendationRules.Apply(recommended, CurrentOptionValues(), true));
        }
    }

    /// <summary>Easy mode step 3: Segment AI kind, the recommended settings (forced only in Simple mode), then Start. A block with a fix only shows the fix.</summary>
    internal async Task EasyTrainAsync(bool forceRecommended, bool classify = false)
    {
        IsUltraKind = false;
        IsClassifyKind = classify;

        RefreshState();
        if (!IsIdle || _host.IsBusy || HasFix)
        {
            return;
        }

        // The OK/NG run has no segmentation options; the recommendation only applies to Segment AI and YOLO.
        EasyLabel.Core.Hardware.RecommendedSettings? recommended = classify ? null : CurrentRecommendation(true);
        if (recommended != null)
        {
            SetOptionValues(TrainRecommendationRules.Apply(recommended, CurrentOptionValues(), forceRecommended));
        }

        RefreshState();
        if (CanStart())
        {
            await StartAsync();
        }
    }

    /// <summary>True when the registry marks a model as the active one (cached with the candidate list).</summary>
    internal bool HasActiveModel => _candidates.Any(c => c.IsActive);

    /// <summary>Easy / Advanced mode switched: the forwarded flag and the comparison rows (Map50-95 is hidden in Simple mode) follow.</summary>
    internal void OnUiModeChanged()
    {
        OnPropertyChanged(nameof(IsAdvancedMode));
        UpdateComparison();
    }

    /// <summary>Forwards the host mode for bindings of the Train tab.</summary>
    public bool IsAdvancedMode => _host.IsAdvancedMode;

    /// <summary>About N min left from the measured epoch time; recomputed once per finished epoch.</summary>
    private void UpdateEta(int epochsDone)
    {
        if (!TrainEtaCalculator.ShouldRefresh(_etaEpoch, epochsDone))
        {
            return;
        }

        _etaEpoch = epochsDone;
        int? minutes = TrainEtaCalculator.RemainingMinutes(_clock.Elapsed, epochsDone, _runTotalEpochs);
        EtaText = TrainRecommendationRules.EtaText(minutes, Strings.ByName);
        _runEtaMinutes = minutes;
        if (IsCpuRunNow())
        {
            UpdateUltraStatus();
        }
    }

    private void OnDataPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DataStepViewModel.Summary))
        {
            RefreshState();
        }
    }

    private void OnServiceChanged(object? sender, EventArgs e)
    {
        // Raised on the calling thread of Start and then on the training thread.
        _dispatcher.BeginInvoke(new Action(RefreshState));
    }

    private SegAiTrainBlock CurrentBlock()
    {
        if (!_host.HasSession)
        {
            return SegAiTrainBlock.Busy;
        }

        if (_host.IsBusy || IsRunning || (_service?.IsRunning ?? false))
        {
            return SegAiTrainBlock.Busy;
        }

        DataStepViewModel data = _host.Data;
        if (data.Summary.Train == 0)
        {
            return SegAiTrainBlock.NoTrainImages;
        }

        if (data.Summary.Val == 0)
        {
            return SegAiTrainBlock.NoValImages;
        }

        return _environmentBlock;
    }

    private void RefreshState()
    {
        string? key = null;
        string? text = null;
        TrainStepRules.TrainFixKind fix = TrainStepRules.TrainFixKind.None;
        if (IsUltraKind)
        {
            TrainStepRules.UltraBlock ultraBlock = CurrentUltraBlock();
            key = TrainStepRules.UltraBlockKey(ultraBlock);
            fix = TrainStepRules.UltraFixKindOf(ultraBlock);
        }
        else if (IsClassifyKind)
        {
            ClassifyBlock classifyBlock = CurrentClassifyBlock();
            text = ClassifyBlockText(classifyBlock);
            fix = ClassifyFixOf(classifyBlock);
        }
        else
        {
            SegAiTrainBlock block = CurrentBlock();
            int labelled = _host.Data.Summary.Train + _host.Data.Summary.Val;
            key = !_host.HasSession ? "TrainBlockNoSession" : TrainStepRules.SegmentBlockKey(block, labelled);
            fix = TrainStepRules.FixKindOf(block, labelled);
        }

        string? shown = text ?? (key == null ? null : Strings.ByName(key));
        StartToolTip = IsRunning || shown == null ? Strings.TrainStart : shown;
        FixKind = fix;
        FixText = fix == TrainStepRules.TrainFixKind.None || shown == null ? string.Empty : shown;
        AutoAssignValCommand.NotifyCanExecuteChanged();
        RunFixCommand.NotifyCanExecuteChanged();
        StartCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
        UseModelCommand.NotifyCanExecuteChanged();
        UseUltraRunCommand.NotifyCanExecuteChanged();
        SetKindCommand.NotifyCanExecuteChanged();
        SetInstanceQualityCommand.NotifyCanExecuteChanged();
        NotifyInstanceQualityChanged();
        UseRecommendedCommand.NotifyCanExecuteChanged();
        _host.RefreshEasyModeSteps();
    }

    /// <summary>Runs the service's own prerequisite check (native library, backbone) off the UI thread; the Train/Val counts come from the summary.</summary>
    private async Task RefreshEnvironmentAsync()
    {
        SegAiSession? session = _host.Session;
        if (session == null || IsRunning || _host.IsBusy)
        {
            return;
        }

        int version = ++_environmentVersion;
        SegTrainingOptions options = TrainStepRules.BuildOptions(TrainStepRules.NormalizeEpochs(EpochsValue), TrainStepRules.NormalizeInputSize(SelectedInputSize), TrainStepRules.NormalizeEarlyStop(EarlyStopValue));
        try
        {
            SegAiTrainBlock block = await Task.Run(() => SegAiTrainingService.Check(session, options));
            if (version != _environmentVersion || !ReferenceEquals(session, _host.Session))
            {
                return;
            }

            // NoTrain/NoVal from the disk state are shown through the summary; the environment reasons and the Position label rule are kept here.
            _environmentBlock = block == SegAiTrainBlock.NativeMissing || block == SegAiTrainBlock.BackboneMissing
                || block == SegAiTrainBlock.NoInstanceLabels ? block : SegAiTrainBlock.None;
            RefreshState();
        }
        catch (Exception ex)
        {
            _host.LogData("Training prerequisite check failed: " + ex.Message);
        }
    }

    // ---------- start / stop ----------

    private bool CanStart() => IsUltraKind
        ? TrainStepRules.CanStartUltra(CurrentUltraBlock())
        : IsClassifyKind
            ? CurrentClassifyBlock().CanTrain
            : TrainStepRules.CanStart(_host.HasSession && _service != null, _host.IsBusy, IsRunning, CurrentBlock());

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        if (IsUltraKind)
        {
            await StartUltraAsync();
            return;
        }

        if (IsClassifyKind)
        {
            await StartClassifyAsync();
            return;
        }

        SegAiTrainingService? service = _service;
        if (service == null || IsRunning || _host.IsBusy)
        {
            return;
        }

        int epochs = TrainStepRules.NormalizeEpochs(EpochsValue);
        int size = TrainStepRules.NormalizeInputSize(SelectedInputSize);
        EpochsValue = epochs;
        SegTrainingOptions options = TrainStepRules.BuildOptions(epochs, size, TrainStepRules.NormalizeEarlyStop(EarlyStopValue));

        CancellationTokenSource cts = BeginRun(epochs);
        SegAiTrainResult? result = null;
        SegAiTrainBlock blocked = SegAiTrainBlock.None;
        bool ok;
        try
        {
            CancellationToken token = cts.Token;
            ok = await _host.RunTrainingAsync(
                async session =>
                {
                    // The canvas edits are on disk (flushed by the host); the service re-checks the prerequisites authoritatively.
                    blocked = await Task.Run(() => SegAiTrainingService.Check(session, options));
                    if (blocked != SegAiTrainBlock.None)
                    {
                        return;
                    }

                    Progress<SegAiTrainProgress> progress = new Progress<SegAiTrainProgress>(OnProgress);
                    result = await service.StartAsync(options, progress, token);
                },
                Strings.TrainFailedTitle);
        }
        finally
        {
            EndRun(cts);
        }

        await ShowResultAsync(ok, blocked, result);
    }

    private async Task ShowResultAsync(bool ok, SegAiTrainBlock blocked, SegAiTrainResult? result)
    {
        try
        {
            if (blocked != SegAiTrainBlock.None)
            {
                string? key = TrainStepRules.BlockKey(blocked);
                if (blocked == SegAiTrainBlock.NativeMissing || blocked == SegAiTrainBlock.BackboneMissing)
                {
                    _environmentBlock = blocked;
                }

                _host.ShowDataNotice(Strings.TrainNoticeTitle, key == null ? string.Empty : Strings.ByName(key), InfoBarSeverity.Warning);
                RefreshState();
                return;
            }

            if (!ok || result == null)
            {
                // The host already showed the error notice for an exception.
                return;
            }

            if (result.Success)
            {
                string file = result.Entry?.File ?? System.IO.Path.GetFileName(result.CandidatePath ?? string.Empty);
                _host.ShowDataNotice(
                    Strings.TrainNoticeTitle,
                    string.Format(Strings.TrainDoneFormat, file, TrainStepRules.FormatDuration(result.Elapsed)),
                    InfoBarSeverity.Success);
                await RefreshCandidatesAsync(file);
                await _host.Label.SyncModelsAsync(false);
                // The Inspect tab offers the new candidate; the active model and the Inspect selection do not change.
                await _host.Inspect.OnModelsChangedAsync(file);
            }
            else if (result.Cancelled)
            {
                _host.ShowDataNotice(Strings.TrainNoticeTitle, Strings.TrainCancelled, InfoBarSeverity.Informational);
            }
            else if (result.Busy)
            {
                _host.ShowDataNotice(Strings.TrainNoticeTitle, Strings.TrainBlockBusy, InfoBarSeverity.Warning);
            }
            else
            {
                _host.ShowDataNotice(Strings.TrainFailedTitle, result.Message, InfoBarSeverity.Error);
            }
        }
        catch (Exception ex)
        {
            // The session can be gone (window closing); never let this reach the dispatcher.
            _host.LogData("Training result handling failed: " + ex.Message);
        }
    }

    private bool CanStop() => IsRunning && !_stopRequested;

    [RelayCommand(CanExecute = nameof(CanStop))]
    private void Stop()
    {
        _stopRequested = true;
        _cts?.Cancel();
        _classifyCts?.Cancel();
        StopCommand.NotifyCanExecuteChanged();
    }

    private CancellationTokenSource BeginRun(int epochs)
    {
        CancellationTokenSource cts = new CancellationTokenSource();
        _cts = cts;
        _stopRequested = false;
        _idle = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        ClearRun();
        _runTotalEpochs = epochs;
        _lastPercent = 0;
        _clock.Restart();
        IsRunning = true;
        UpdateProgressText();
        _ticker.Start();
        RefreshState();
        OnPropertyChanged(nameof(HasChart));
        return cts;
    }

    private void EndRun(CancellationTokenSource cts)
    {
        _ticker.Stop();
        _clock.Stop();
        if (ReferenceEquals(_cts, cts))
        {
            _cts = null;
        }

        cts.Dispose();
        IsRunning = false;
        _stopRequested = false;
        _etaEpoch = 0;
        _runEtaMinutes = null;
        EtaText = string.Empty;
        RefreshState();
        OnPropertyChanged(nameof(HasChart));
        _idle?.TrySetResult(true);
    }

    private void ClearRun()
    {
        _epochs.Clear();
        _ultraLast = null;
        _runTotalEpochs = 0;
        _etaEpoch = 0;
        _runEtaMinutes = null;
        EtaText = string.Empty;
        ProgressValue = 0;
        ProgressText = string.Empty;
        OnPropertyChanged(nameof(HasChart));
        OnPropertyChanged(nameof(ChartTotalEpochs));
        ChartInvalidated?.Invoke(this, EventArgs.Empty);
    }

    private void OnProgress(SegAiTrainProgress progress)
    {
        if (!IsRunning)
        {
            return;
        }

        _lastPercent = Math.Max(_lastPercent, progress.Percent);
        if (progress.Epoch != null)
        {
            AddEpoch(progress.Epoch);
        }

        ProgressValue = _lastPercent;
        UpdateEta(TrainStepRules.EpochsDone(_epochs));
        UpdateProgressText();
    }

    private void AddEpoch(SegEpochMetric metric)
    {
        int index = _epochs.FindIndex(e => e.Epoch == metric.Epoch);
        if (index >= 0)
        {
            _epochs[index] = metric;
        }
        else
        {
            _epochs.Add(metric);
        }

        OnPropertyChanged(nameof(HasChart));
        OnPropertyChanged(nameof(ChartTotalEpochs));
        ChartInvalidated?.Invoke(this, EventArgs.Empty);
    }

    private void OnTick(object? sender, EventArgs e)
    {
        UpdateProgressText();
    }

    private void UpdateProgressText()
    {
        TimeSpan elapsed = _clock.Elapsed;
        if (_ultraRun)
        {
            UltraEvent? last = _ultraLast;
            int reported = last?.Epoch ?? 0;
            ProgressText = string.Format(
                Strings.UltraProgressFormat,
                reported,
                _runTotalEpochs,
                TrainStepRules.UltraNumber(last?.BoxLoss),
                TrainStepRules.UltraNumber(last?.ClsLoss),
                TrainStepRules.UltraNumber(last?.Map50),
                TrainStepRules.FormatDuration(elapsed),
                TrainStepRules.FormatDuration(TrainStepRules.Eta(elapsed, reported, _runTotalEpochs)));
            return;
        }

        int done = TrainStepRules.EpochsDone(_epochs);
        ProgressText = string.Format(
            Strings.TrainProgressFormat,
            done,
            _runTotalEpochs,
            TrainStepRules.FormatDuration(elapsed),
            TrainStepRules.FormatDuration(TrainStepRules.Eta(elapsed, done, _runTotalEpochs)));
    }

    // ---------- YOLO / RT-DETR (Ultralytics trainer) ----------

    private bool CanSetKind() => IsIdle;

    [RelayCommand(CanExecute = nameof(CanSetKind))]
    private void SetKind(string? kind)
    {
        IsUltraKind = string.Equals(kind, "ultra", StringComparison.Ordinal);
        IsClassifyKind = string.Equals(kind, ClassifyRules.KindClassify, StringComparison.Ordinal);
        if (IsUltraKind)
        {
            _ = LoadUltraAsync();
        }
    }

    // ---------- Position: Performance | Quality of the instance head ----------

    /// <summary>
    /// The Performance | Quality choice is shown for a Position project, and for a Detect project only while its loaded model carries the
    /// instance head (box mode, EAI-POS P9). A polygon Detect model does not show it.
    /// </summary>
    public bool ShowInstanceQuality => IsSegmentKind
        && (_host.Canvas.Kind == EasyLabel.Core.Projects.ProjectKind.Position || _host.Session?.InstanceHeadLoaded == true);

    public bool IsQualityInstance => _host.Session?.InstanceQuality == SegInstanceQuality.Quality;

    public bool IsPerformanceInstance => _host.Session?.InstanceQuality == SegInstanceQuality.Performance;

    private bool CanSetInstanceQuality() => IsIdle && _host.HasSession && !_host.IsBusy;

    [RelayCommand(CanExecute = nameof(CanSetInstanceQuality))]
    private void SetInstanceQuality(string? quality)
    {
        SegAiSession? session = _host.Session;
        if (session == null)
        {
            return;
        }

        session.SetInstanceQuality(string.Equals(quality, "quality", StringComparison.Ordinal) ? SegInstanceQuality.Quality : SegInstanceQuality.Performance);
        NotifyInstanceQualityChanged();
    }

    private void NotifyInstanceQualityChanged()
    {
        OnPropertyChanged(nameof(ShowInstanceQuality));
        OnPropertyChanged(nameof(IsQualityInstance));
        OnPropertyChanged(nameof(IsPerformanceInstance));
    }

    partial void OnIsUltraKindChanged(bool value)
    {
        RefreshRecommendation();
        RefreshState();
    }

    partial void OnSelectedFamilyChanged(UltraFamilyRow? value)
    {
        RefreshState();
    }

    private TrainStepRules.UltraBlock CurrentUltraBlock()
    {
        bool busy = _host.IsBusy || (_service?.IsRunning ?? false);
        SegAiDataSummary summary = _host.Data.Summary;
        return TrainStepRules.UltraBlockOf(_host.HasSession, busy, IsRunning, EffectiveUltraEnv(), SelectedFamily != null, summary.Train > 0, summary.Train + summary.Val);
    }

    /// <summary>The probed environment with the CPU opt-in applied (Advanced mode and the Settings row); read live from the settings.</summary>
    private TrainStepRules.UltraEnvironment EffectiveUltraEnv()
    {
        return TrainStepRules.EffectiveUltraEnvironment(_ultraEnv, _host.IsAdvancedMode, _host.Settings.UltraAllowCpu);
    }

    private bool IsCpuRunNow() => TrainStepRules.IsCpuRun(EffectiveUltraEnv());

    /// <summary>CPU run: preselects the n-size detect family and moves the input size into 320..416. The user can change both afterwards.</summary>
    private void ApplyCpuPreselect()
    {
        if (!IsCpuRunNow())
        {
            return;
        }

        UltraFamilyRow? cpuFamily = Families.FirstOrDefault(f => TrainStepRules.IsCpuFamily(f.Family));
        if (cpuFamily != null)
        {
            SelectedFamily = cpuFamily;
        }

        SelectedInputSize = TrainStepRules.CpuInputSize(SelectedInputSize);
    }

    // ---------- fix actions ----------

    private bool CanRunFix() => HasFix && !_host.IsBusy;

    /// <summary>The one fix button: runs the action of the current <see cref="FixKind"/>. Never throws.</summary>
    [RelayCommand(CanExecute = nameof(CanRunFix))]
    private async Task RunFixAsync()
    {
        switch (FixKind)
        {
            case TrainStepRules.TrainFixKind.AutoAssignVal:
                await AutoAssignValAsync();
                break;
            case TrainStepRules.TrainFixKind.OpenModelFolder:
                OpenModelFolder();
                break;
            case TrainStepRules.TrainFixKind.UseSegmentAi:
                IsUltraKind = false;
                break;
            case TrainStepRules.TrainFixKind.GoToLabel:
                _host.ShowLabelTabCommand.Execute(null);
                break;
        }
    }

    /// <summary>Opens the folder of the missing backbone file (its nearest existing parent) in Explorer; a notice says what to do when it cannot be opened.</summary>
    private void OpenModelFolder()
    {
        string? target = null;
        try
        {
            string? native = NativeSegAiLibrary.LoadedFolder;
            string wanted = string.IsNullOrEmpty(native) ? _host.ModelsRoot : System.IO.Path.Combine(native, "Models", "DefectV2");
            target = TrainStepRules.NearestExistingFolder(wanted);
            if (target == null)
            {
                _host.ShowDataNotice(Strings.TrainNoticeTitle, Strings.ByName("TrainOpenModelFolderFailed"), InfoBarSeverity.Warning);
                return;
            }

            ProcessStartInfo info = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
            info.ArgumentList.Add(target);
            using Process? process = Process.Start(info);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception || ex is InvalidOperationException || ex is System.IO.IOException || ex is UnauthorizedAccessException)
        {
            _host.LogData("Open model folder failed: " + ex.Message);
            _host.ShowDataNotice(Strings.TrainNoticeTitle, Strings.ByName("TrainOpenModelFolderFailed"), InfoBarSeverity.Warning);
        }
    }

    // ---------- fix action: Auto-assign Val ----------

    private bool CanAutoAssignVal() => FixKind == TrainStepRules.TrainFixKind.AutoAssignVal && !IsRunning && !_host.IsBusy && _host.HasSession;

    /// <summary>
    /// Fix of the "No image is set to Val" block: puts about 20% of the labelled images (class-stratified, see
    /// <see cref="SegTrainingSplit.AutoAssign"/>) to Val through the same flag writer as the Train/Val buttons, then starts the run
    /// when nothing else blocks it.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanAutoAssignVal))]
    private async Task AutoAssignValAsync()
    {
        IReadOnlyList<string>? assigned = await _host.RunStep1Async<IReadOnlyList<string>>(AssignValCore, true);
        if (assigned == null)
        {
            return;
        }

        _host.ShowDataNotice(
            Strings.TrainNoticeTitle,
            string.Format(Strings.ByName("TrainAutoValDoneFormat"), assigned.Count),
            assigned.Count > 0 ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
        RefreshState();
        if (assigned.Count > 0 && CanStart())
        {
            await StartAsync();
        }
    }

    private static IReadOnlyList<string> AssignValCore(SegAiSession session)
    {
        List<SegAiSampleInfo> labelled = session.Samples.Where(s => s.AnnotationCount > 0).ToList();
        List<SegSplitItem> items = new List<SegSplitItem>();
        foreach (SegAiSampleInfo sample in labelled)
        {
            HashSet<int> classes = new HashSet<int>(session.GetAnnotations(sample.Rel).Select(a => (int)a.Label));
            items.Add(new SegSplitItem(sample.Rel, classes, sample.Mode));
        }

        SegSplitSide[] sides = SegTrainingSplit.AutoAssign(items, 1337);
        List<string> val = new List<string>();
        for (int i = 0; i < labelled.Count; i++)
        {
            if (sides[i] == SegSplitSide.Val)
            {
                session.SetMode(labelled[i].Rel, SegTrainingMode.Val);
                val.Add(labelled[i].Rel);
            }
        }

        return val;
    }

    private static string UltraRunsDirectory(EasyLabel.Core.ProjectPaths paths) => paths.UltraDir;

    /// <summary>
    /// Locates the trainer, asks it for the GPU and the families (off the UI thread), and caches the answer. Does nothing while a probe runs
    /// or once the trainer is ready; a failed probe is repeated the next time the kind is chosen or the step shown.
    /// </summary>
    private async Task LoadUltraAsync()
    {
        if (_ultraEnv == TrainStepRules.UltraEnvironment.Checking || _ultraEnv == TrainStepRules.UltraEnvironment.Ready)
        {
            return;
        }

        _ultraEnv = TrainStepRules.UltraEnvironment.Checking;
        UpdateUltraStatus();
        RefreshState();

        TrainStepRules.UltraEnvironment env;
        string message = string.Empty;
        string gpu = string.Empty;
        int? vram = null;
        IReadOnlyList<UltraFamily> families = Array.Empty<UltraFamily>();
        UltraTrainService? created = null;
        int ultraVersion = _ultraVersion;
        try
        {
            UltraTrainService? existing = _ultraService;
            (env, message, gpu, vram, families, created) = await Task.Run(() => ProbeUltraAsync(existing));
        }
        catch (Exception ex)
        {
            env = TrainStepRules.UltraEnvironment.Failed;
            message = TrainStepRules.FirstLine(ex.Message, 160);
            _host.LogData("YOLO trainer check failed: " + ex.Message);
        }

        if (ultraVersion != _ultraVersion)
        {
            // DisposeUltra ran while the probe was awaited: drop the new service instead of resurrecting state.
            created?.Dispose();
            return;
        }

        if (created != null)
        {
            _ultraService = created;
        }

        _ultraEnv = env;
        _ultraMessage = message;
        _ultraGpu = gpu;
        _ultraVramMb = vram;
        Families.Clear();
        foreach (UltraFamily family in families)
        {
            Families.Add(new UltraFamilyRow(family));
        }

        SelectedFamily = Families.FirstOrDefault(f => !f.Family.Experimental && f.Family.Fits4Gb) ?? Families.FirstOrDefault();
        ApplyCpuPreselect();
        UpdateUltraStatus();
        RefreshState();
    }

    private static async Task<(TrainStepRules.UltraEnvironment, string, string, int?, IReadOnlyList<UltraFamily>, UltraTrainService?)> ProbeUltraAsync(UltraTrainService? existing)
    {
        IReadOnlyList<UltraFamily> none = Array.Empty<UltraFamily>();
        UltraTrainService? created = null;
        UltraTrainService service = existing!;
        if (existing == null)
        {
            UltraTrainerLocation location = new UltraTrainerLocator().Locate();
            if (location.Status == UltraTrainerStatus.NoTrainerFolder)
            {
                return (TrainStepRules.UltraEnvironment.NoTrainerFolder, string.Empty, string.Empty, null, none, null);
            }

            if (location.Status == UltraTrainerStatus.NoVenv)
            {
                return (TrainStepRules.UltraEnvironment.NoVenv, string.Empty, string.Empty, null, none, null);
            }

            created = new UltraTrainService(location);
            service = created;
        }

        UltraQueryResult<UltraPreflight> preflight = await service.PreflightAsync(CancellationToken.None);
        if (!preflight.Success)
        {
            return (TrainStepRules.UltraEnvironment.Failed, TrainStepRules.FirstLine(preflight.ErrorMessage, 160), string.Empty, null, none, created);
        }

        UltraPreflight info = preflight.Value!;
        string gpu = info.Gpu ?? string.Empty;
        if (!info.Cuda || !info.DriverOk)
        {
            // The families are listed too: the Advanced CPU opt-in needs the n-size detect model. Without them the block is unchanged.
            UltraQueryResult<IReadOnlyList<UltraFamily>> cpuList = await service.ListFamiliesAsync(CancellationToken.None);
            return (TrainStepRules.UltraEnvironment.NoCuda, string.Empty, gpu, info.VramMb, cpuList.Success ? cpuList.Value! : none, created);
        }

        UltraQueryResult<IReadOnlyList<UltraFamily>> list = await service.ListFamiliesAsync(CancellationToken.None);
        if (!list.Success)
        {
            return (TrainStepRules.UltraEnvironment.Failed, TrainStepRules.FirstLine(list.ErrorMessage, 160), gpu, info.VramMb, none, created);
        }

        return (TrainStepRules.UltraEnvironment.Ready, string.Empty, gpu, info.VramMb, list.Value!, created);
    }

    private void UpdateUltraStatus()
    {
        if (IsCpuRunNow())
        {
            UltraStatusText = CpuStatusText();
            return;
        }

        switch (_ultraEnv)
        {
            case TrainStepRules.UltraEnvironment.Ready:
                UltraStatusText = string.Format(Strings.UltraStatusReadyFormat, TrainStepRules.UltraGpuText(_ultraGpu, _ultraVramMb));
                break;
            case TrainStepRules.UltraEnvironment.Failed:
                UltraStatusText = _ultraMessage.Length > 0
                    ? string.Format(Strings.UltraStatusFailedFormat, _ultraMessage)
                    : Strings.UltraBlockFailed;
                break;
            default:
                string? key = TrainStepRules.UltraBlockKey(TrainStepRules.UltraBlockOf(true, false, false, _ultraEnv, true));
                UltraStatusText = key == null ? string.Empty : Strings.ByName(key);
                break;
        }
    }

    /// <summary>The CPU run's status line: the existing "about N min" text once an epoch is timed, else a note that the time follows the first epoch.</summary>
    private string CpuStatusText()
    {
        if (_runEtaMinutes is int minutes && minutes > 0)
        {
            string span = string.Format(CultureInfo.CurrentCulture, CpuText("UltraCpuMinutes", "{0} min"), minutes);
            return string.Format(CultureInfo.CurrentCulture, CpuText("UltraCpuSlow", "No NVIDIA GPU: training on CPU, about {0}"), span);
        }

        return CpuText("UltraCpuNoEta", "No NVIDIA GPU: training on CPU; the time is shown after the first epoch");
    }

    /// <summary>Resource text with English fallback (the key is missing from a resource pack).</summary>
    private static string CpuText(string key, string english)
    {
        string text = Strings.ByName(key);
        return string.Equals(text, key, StringComparison.Ordinal) ? english : text;
    }

    private async Task StartUltraAsync()
    {
        UltraTrainService? service = _ultraService;
        UltraFamilyRow? row = SelectedFamily;
        if (service == null || row == null || IsRunning || _host.IsBusy || !TrainStepRules.CanStartUltra(CurrentUltraBlock()))
        {
            return;
        }

        int epochs = TrainStepRules.NormalizeEpochs(EpochsValue);
        int size = TrainStepRules.NormalizeInputSize(SelectedInputSize);
        int? batch = TrainStepRules.NormalizeBatch(BatchValue);
        EpochsValue = epochs;
        UltraFamily family = row.Family;
        DateTime created = DateTime.UtcNow;
        string runName = TrainStepRules.UltraRunName(created);

        _ultraRun = true;
        CancellationTokenSource cts = BeginRun(epochs);
        UltraTrainResult? result = null;
        bool ok;
        try
        {
            CancellationToken token = cts.Token;
            ok = await _host.RunTrainingAsync(
                async session => result = await RunUltraAsync(service, session.Paths, family, runName, created, epochs, size, batch, IsCpuRunNow(), token),
                Strings.TrainFailedTitle);
        }
        finally
        {
            EndRun(cts);
            _ultraRun = false;
        }

        try
        {
            if (ok && result != null)
            {
                ShowUltraResult(result, runName);
            }

            await RefreshUltraRunsAsync(runName);
        }
        catch (Exception ex)
        {
            _host.LogData("YOLO result handling failed: " + ex.Message);
        }
    }

    /// <summary>Exports the dataset, trains, and stores <c>ultra-run.json</c>; expected problems come back as a failed or cancelled result.</summary>
    private async Task<UltraTrainResult> RunUltraAsync(
        UltraTrainService service,
        EasyLabel.Core.ProjectPaths paths,
        UltraFamily family,
        string runName,
        DateTime created,
        int epochs,
        int size,
        int? batch,
        bool allowCpu,
        CancellationToken token)
    {
        string runsDir = UltraRunsDirectory(paths);
        UltraTrainResult result;
        try
        {
            UltraTask? task = UltraDatasetBuilder.ParseTask(family.Task);
            if (task == null)
            {
                result = new UltraTrainResult { ErrorCode = "data", ErrorMessage = "Unknown task of the family: " + family.Task };
            }
            else
            {
                string dataYaml = await Task.Run(() => new UltraDatasetBuilder().Build(task.Value, paths.ImageRoot, runsDir, runName, paths: paths), token);
                if (token.IsCancellationRequested)
                {
                    result = new UltraTrainResult { Cancelled = true, ErrorCode = "cancelled", ErrorMessage = "Training cancelled." };
                }
                else
                {
                    UltraTrainRequest request = new UltraTrainRequest
                    {
                        DataYaml = dataYaml,
                        FamilyId = family.Id,
                        Epochs = epochs,
                        ImgSize = size,
                        Batch = batch,
                        // Only when no GPU is found and the Advanced CPU opt-in is on (the trainer then gets --device=cpu --allow-cpu).
                        AllowCpu = allowCpu,
                        RunsDir = runsDir,
                        // A sub-folder: the run folder already holds the dataset, so Ultralytics would otherwise number the name.
                        RunName = runName + "/train",
                    };
                    Progress<UltraEvent> progress = new Progress<UltraEvent>(OnUltraEvent);
                    result = await service.RunAsync(request, progress, token);
                }
            }
        }
        catch (OperationCanceledException)
        {
            result = new UltraTrainResult { Cancelled = true, ErrorCode = "cancelled", ErrorMessage = "Training cancelled." };
        }
        catch (ObjectDisposedException)
        {
            result = new UltraTrainResult { Cancelled = true, ErrorCode = "cancelled", ErrorMessage = "Training cancelled." };
        }
        catch (UltraDatasetException ex)
        {
            string text = Strings.ByName(TrainStepRules.UltraDatasetProblemKey(ex.Problem));
            result = new UltraTrainResult
            {
                ErrorCode = "data",
                ErrorMessage = ex.Problem == UltraDatasetProblem.ClassNotInTrain
                    ? string.Format(text, ex.ClassName)
                    : string.Format(text, UltraDatasetBuilder.MinYoloImages, ex.LabelledImages),
            };
        }
        catch (Exception ex)
        {
            _host.LogData("YOLO run failed: " + ex);
            result = new UltraTrainResult { ErrorCode = "data", ErrorMessage = ex.Message };
        }

        try
        {
            UltraRunRecord record = TrainStepRules.BuildRunRecord(runName, created, family, epochs, size, batch, result);
            string runDir = System.IO.Path.Combine(runsDir, runName);
            await Task.Run(() => UltraRunStore.Write(runDir, record));
        }
        catch (Exception ex)
        {
            _host.LogData("Writing ultra-run.json failed: " + ex.Message);
        }

        return result;
    }

    private void OnUltraEvent(UltraEvent ev)
    {
        if (!IsRunning || !_ultraRun)
        {
            return;
        }

        if ((ev.Kind == UltraEventKind.Start || ev.Kind == UltraEventKind.Epoch) && ev.Epochs > 0)
        {
            _runTotalEpochs = ev.Epochs;
            OnPropertyChanged(nameof(ChartTotalEpochs));
        }

        if (ev.Kind != UltraEventKind.Epoch)
        {
            return;
        }

        _ultraLast = ev;
        _lastPercent = Math.Max(_lastPercent, TrainStepRules.UltraPercent(ev.Epoch, _runTotalEpochs));
        SegEpochMetric? metric = TrainStepRules.UltraEpochMetric(ev);
        if (metric != null)
        {
            AddEpoch(metric);
        }

        ProgressValue = _lastPercent;
        UpdateEta(ev.Epoch);
        UpdateProgressText();
    }

    private void ShowUltraResult(UltraTrainResult result, string runName)
    {
        CultureInfo culture = Strings.Culture ?? CultureInfo.CurrentUICulture;
        if (result.Success)
        {
            string map50 = result.Map50 is double a ? a.ToString("P1", culture) : "-";
            string map5095 = result.Map5095 is double b ? b.ToString("P1", culture) : "-";
            _host.ShowDataNotice(
                Strings.TrainNoticeTitle,
                string.Format(Strings.UltraDoneFormat, runName, map50, map5095, TrainStepRules.FormatDuration(TimeSpan.FromSeconds(result.ElapsedSeconds))),
                InfoBarSeverity.Success);
        }
        else if (result.Cancelled)
        {
            _host.ShowDataNotice(Strings.TrainNoticeTitle, Strings.TrainCancelled, InfoBarSeverity.Informational);
        }
        else
        {
            _host.ShowDataNotice(Strings.TrainFailedTitle, result.ErrorMessage, InfoBarSeverity.Error);
        }
    }

    /// <summary>Re-reads the stored YOLO / RT-DETR runs of the open project (newest first); selects <paramref name="selectName"/> when given.</summary>
    internal async Task RefreshUltraRunsAsync(string? selectName)
    {
        SegAiSession? session = _host.Session;
        int version = ++_ultraRunsVersion;
        IReadOnlyList<UltraRunRecord> list = Array.Empty<UltraRunRecord>();
        if (session != null)
        {
            string dir = UltraRunsDirectory(session.Paths);
            list = await Task.Run(() => UltraRunStore.Read(dir));
            if (version != _ultraRunsVersion || !ReferenceEquals(session, _host.Session))
            {
                return;
            }
        }

        string? keep = selectName ?? SelectedUltraRun?.Record.RunName;
        CultureInfo culture = Strings.Culture ?? CultureInfo.CurrentUICulture;
        UltraRuns.Clear();
        UltraRunRow? select = null;
        foreach (UltraRunRecord record in list)
        {
            UltraRunRow row = new UltraRunRow(record, culture);
            UltraRuns.Add(row);
            if (keep != null && string.Equals(record.RunName, keep, StringComparison.OrdinalIgnoreCase))
            {
                select = row;
            }
        }

        SelectedUltraRun = select;
        OnPropertyChanged(nameof(ShowUltraRuns));
        OpenUltraFolderCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedUltraRunChanged(UltraRunRow? value)
    {
        OpenUltraFolderCommand.NotifyCanExecuteChanged();
        UseUltraRunCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(UseUltraRunTip));
    }

    /// <summary>Tooltip of "Use this model" in the YOLO runs block: the action, or why the selected run cannot be used for auto-labelling.</summary>
    public string UseUltraRunTip
    {
        get
        {
            UltraRunRow? row = SelectedUltraRun;
            switch (row == null ? CustomModelProblem.None : CustomModelInstaller.Check(row.Record))
            {
                case CustomModelProblem.NotSuccess: return Strings.UltraUseNotFinishedTip;
                case CustomModelProblem.NotDetect: return Strings.UltraUseOnlyDetectTip;
                case CustomModelProblem.OnnxMissing: return Strings.UltraUseNoOnnxTip;
                default: return Strings.UltraUseTip;
            }
        }
    }

    private bool CanUseUltraRun()
    {
        UltraRunRow? row = SelectedUltraRun;
        return row != null
            && !IsRunning
            && !_host.IsBusy
            && CustomModelInstaller.Check(row.Record) == CustomModelProblem.None;
    }

    /// <summary>
    /// Installs the selected finished detect run as the Custom YOLO auto-label model (models\custom-detect, replacing the previous one
    /// after a confirmation) and selects it in the AI Model source of the Label step. The YOLO-World model is not touched.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanUseUltraRun))]
    private async Task UseUltraRunAsync()
    {
        UltraRunRow? row = SelectedUltraRun;
        if (row == null)
        {
            return;
        }

        UltraRunRecord record = row.Record;
        string root = _host.ModelsRoot;
        if (CustomModelInstaller.HasInstalled(root))
        {
            string? replaced = CustomModelInstaller.ReadInstalledRunName(root);
            Wpf.Ui.Controls.MessageBox box = new Wpf.Ui.Controls.MessageBox
            {
                Title = Strings.UltraReplaceTitle,
                Content = replaced == null
                    ? string.Format(Strings.UltraReplaceUnknownFormat, row.NameText)
                    : string.Format(Strings.UltraReplaceFormat, replaced, row.NameText),
                PrimaryButtonText = Strings.UseModel,
                CloseButtonText = Strings.Cancel,
                Owner = System.Windows.Application.Current?.MainWindow,
            };
            Wpf.Ui.Controls.MessageBoxResult answer = await box.ShowDialogAsync();
            if (answer != Wpf.Ui.Controls.MessageBoxResult.Primary)
            {
                return;
            }
        }

        // A loaded model keeps its file open; release it before the folder is replaced.
        _host.Label.ReleaseAiModelFiles();
        CustomModelInstallResult result = await Task.Run(() => CustomModelInstaller.Install(root, record));
        _host.RefreshAiModels();
        _host.Label.OnAiModelsChanged(result.Success ? AiModelCatalog.CustomDetectId : null);
        if (result.Success)
        {
            _host.ShowDataNotice(Strings.UseModel, string.Format(Strings.UltraUseDoneFormat, row.NameText), InfoBarSeverity.Success);
        }
        else
        {
            string detail = result.Message.Length > 0 ? result.Message : UseUltraRunTip;
            _host.ShowDataNotice(Strings.TrainNoticeTitle, string.Format(Strings.UltraUseFailedFormat, detail), InfoBarSeverity.Warning);
        }
    }

    private bool CanOpenUltraFolder() => UltraRuns.Count > 0;

    /// <summary>Opens the selected run folder (the newest one when nothing is selected) in Explorer.</summary>
    [RelayCommand(CanExecute = nameof(CanOpenUltraFolder))]
    private void OpenUltraFolder()
    {
        UltraRunRow? row = SelectedUltraRun ?? UltraRuns.FirstOrDefault();
        if (row == null)
        {
            return;
        }

        try
        {
            ProcessStartInfo info = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
            info.ArgumentList.Add(row.Record.RunDirectory);
            using Process? process = Process.Start(info);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception || ex is InvalidOperationException)
        {
            _host.ShowDataNotice(Strings.TrainNoticeTitle, Strings.UltraOpenFolderFailed + ": " + ex.Message, InfoBarSeverity.Warning);
        }
    }

    /// <summary>Kills every trainer process tree and forgets the cached probe (the app is closing or the trainer was stopped for close).</summary>
    private void DisposeUltra()
    {
        UltraTrainService? service = _ultraService;
        _ultraService = null;
        _ultraVersion++;
        _ultraEnv = TrainStepRules.UltraEnvironment.Unknown;
        service?.Dispose();
    }

    // ---------- close ----------

    /// <summary>
    /// Window close while a run is active: asks, then cancels the run and waits (bounded) for the pipeline to unwind without blocking the
    /// UI thread. Returns true when nothing is running any more; false when the user declined or the run did not stop in time.
    /// </summary>
    internal async Task<bool> ConfirmStopForCloseAsync()
    {
        if (!IsRunning)
        {
            DisposeUltra();
            return true;
        }

        Wpf.Ui.Controls.MessageBox box = new Wpf.Ui.Controls.MessageBox
        {
            Title = Strings.TrainCloseTitle,
            Content = Strings.TrainClosePrompt,
            PrimaryButtonText = Strings.TrainCloseStop,
            CloseButtonText = Strings.Cancel,
            Owner = System.Windows.Application.Current?.MainWindow,
        };
        Wpf.Ui.Controls.MessageBoxResult answer = await box.ShowDialogAsync();
        if (answer != Wpf.Ui.Controls.MessageBoxResult.Primary)
        {
            return false;
        }

        bool stopped = await StopAndWaitAsync(TrainStepRules.StopTimeout);
        if (stopped)
        {
            // Kills any trainer process tree that is still alive (the cancelled run already killed its own).
            DisposeUltra();
        }

        return stopped;
    }

    private async Task<bool> StopAndWaitAsync(TimeSpan timeout)
    {
        TaskCompletionSource<bool>? idle = _idle;
        if (!IsRunning || idle == null)
        {
            return true;
        }

        Stop();
        Task finished = await Task.WhenAny(idle.Task, Task.Delay(timeout));
        if (ReferenceEquals(finished, idle.Task))
        {
            return true;
        }

        _host.ShowDataNotice(Strings.TrainNoticeTitle, Strings.TrainStopTimeout, InfoBarSeverity.Error);
        return false;
    }

    // ---------- candidates / comparison ----------

    /// <summary>Re-reads the registry models with their reports; keeps the selection (or selects <paramref name="selectFile"/>).</summary>
    internal async Task RefreshCandidatesAsync(string? selectFile)
    {
        SegAiSession? session = _host.Session;
        int version = ++_candidatesVersion;
        if (session == null)
        {
            Candidates.Clear();
            _candidates = new List<SegAiCandidateInfo>();
            SelectedCandidate = null;
            return;
        }

        IReadOnlyList<SegAiCandidateInfo> list;
        try
        {
            list = await Task.Run(() => session.ReadCandidates());
        }
        catch (Exception ex)
        {
            _host.LogData("Read candidates failed: " + ex.Message);
            return;
        }

        if (version != _candidatesVersion || !ReferenceEquals(session, _host.Session))
        {
            return;
        }

        string? keep = selectFile ?? SelectedCandidate?.Info.Entry.File;
        CultureInfo culture = Strings.Culture ?? CultureInfo.CurrentUICulture;
        _candidates = list;
        Candidates.Clear();
        TrainCandidateRow? select = null;
        foreach (SegAiCandidateInfo info in TrainStepRules.NewestFirst(list))
        {
            TrainCandidateRow row = new TrainCandidateRow(info, culture);
            Candidates.Add(row);
            if (keep != null && string.Equals(info.Entry.File, keep, StringComparison.OrdinalIgnoreCase))
            {
                select = row;
            }
        }

        SelectedCandidate = select;
        UpdateComparison();
        RefreshState();
    }

    partial void OnSelectedCandidateChanged(TrainCandidateRow? value)
    {
        UpdateComparison();
        UseModelCommand.NotifyCanExecuteChanged();
    }

    private void UpdateComparison()
    {
        ComparisonRows.Clear();
        TrainCandidateRow? row = SelectedCandidate;
        if (row == null || row.Info.IsActive)
        {
            HasComparison = false;
            DifferentValidationSet = false;
            BoxOverextension = false;
            NoActiveModel = false;
            return;
        }

        SegAiCandidateInfo? active = _candidates.FirstOrDefault(c => c.IsActive);
        SegAiModelComparisonResult result = SegAiModelComparison.Compare(
            row.Info.Report,
            active?.Report,
            row.Info.Entry.DatasetFingerprint,
            active?.Entry.DatasetFingerprint);
        CultureInfo culture = Strings.Culture ?? CultureInfo.CurrentUICulture;
        foreach (SegAiComparisonRow comparison in result.Rows)
        {
            // Simple mode hides the Map50-95 row; Advanced keeps it.
            if (_host.IsSimpleMode && comparison.Metric == SegAiComparisonMetric.Map5095)
            {
                continue;
            }

            ComparisonRows.Add(new ComparisonRowViewModel(comparison, culture));
        }

        DifferentValidationSet = result.DifferentValidationSet;
        BoxOverextension = result.BoxOverextension;
        NoActiveModel = result.NoActiveModel;
        HasComparison = true;
    }

    private bool CanUseModel() => TrainStepRules.CanUseModel(_host.HasSession, _host.IsBusy, IsRunning, SelectedCandidate?.Info);

    [RelayCommand(CanExecute = nameof(CanUseModel))]
    private async Task UseModelAsync()
    {
        TrainCandidateRow? row = SelectedCandidate;
        if (row == null)
        {
            return;
        }

        string file = row.Info.Entry.File;
        bool ok = await _host.RunLabelAsync(session => Task.Run(() => session.SetActiveModel(file)), Strings.ModelErrorTitle);
        if (!ok)
        {
            return;
        }

        await _host.Label.SyncModelsAsync(true);
        await _host.Inspect.OnModelsChangedAsync(null);
        await RefreshCandidatesAsync(file);
        _host.ShowDataNotice(Strings.UseModel, string.Format(Strings.UseModelDoneFormat, file), InfoBarSeverity.Success);
    }
}
