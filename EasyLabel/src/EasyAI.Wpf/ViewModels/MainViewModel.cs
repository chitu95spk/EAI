using System.Collections.ObjectModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EasyAI.Desktop.Infrastructure;
using EasyAI.Desktop.Resources;
using EasyAI.Desktop.Services;
using EasyLabel.Camera.Mvs;
using EasyLabel.Core;
using EasyLabel.Core.Model;
using EasyLabel.Core.Projects;
using EasyLabel.SegAI;
using EasyLabel.SegAI.Model;
using EasyLabel.SegAI.Native;
using InfoBarSeverity = Wpf.Ui.Controls.InfoBarSeverity;

namespace EasyAI.Desktop.ViewModels;

/// <summary>One step of the Simple-mode strip. Status is "Locked", "Ready", "Running" or "Done".</summary>
public sealed partial class EasyStepViewModel : ObservableObject
{
    public EasyStepViewModel(System.Windows.Input.ICommand command)
    {
        Command = command;
    }

    [ObservableProperty]
    private string _text = string.Empty;

    [ObservableProperty]
    private bool _isEnabled;

    [ObservableProperty]
    private bool _isCurrent;

    [ObservableProperty]
    private string _status = "Locked";

    /// <summary>Click action of the step; it does nothing while the step is disabled.</summary>
    public System.Windows.Input.ICommand Command { get; }
}

public sealed partial class MainViewModel : ObservableObject
{
    private readonly ProjectService _projects;
    private readonly ProjectManager _manager;
    private readonly SettingsStore _store;
    private readonly AppSettings _settings;
    private readonly StartupOptions _options;
    private readonly EngineStatusService _engines;
    private readonly ImagePreviewLoader _loader;
    private SegAiSession? _segSession;
    private string _baseTitle = Strings.AppName;
    private bool _revertingSelection;

    [ObservableProperty]
    private Project? _project;

    /// <summary>Left side of the status bar: the last save/delete/output message.</summary>
    [ObservableProperty]
    private string _statusMessage = string.Empty;

    /// <summary>Auto-save (File menu and Settings). ON writes every edit at once; OFF holds edits until Save.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private bool _autoSave = true;

    /// <summary>Embed the image in the LabelMe JSON (imageData) when annotations are written.</summary>
    [ObservableProperty]
    private bool _embedImageData = true;

    /// <summary>Folder that also receives a copy of every written LabelMe JSON, or null.</summary>
    [ObservableProperty]
    private string? _outputDirectory;

    [ObservableProperty]
    private string _themeMode = ThemeService.FollowSystem;

    [ObservableProperty]
    private string _uiCultureName = "vi";

    [ObservableProperty]
    private ObservableCollection<ImageItemViewModel> _images = new ObservableCollection<ImageItemViewModel>();

    [ObservableProperty]
    private ImageItemViewModel? _selectedImage;

    [ObservableProperty]
    private ImageSource? _previewImage;

    [ObservableProperty]
    private WorkflowStep _currentStep = WorkflowStep.Label;

    [ObservableProperty]
    private string _projectPath = string.Empty;

    [ObservableProperty]
    private int _imageCount;

    [ObservableProperty]
    private int _classCount;

    [ObservableProperty]
    private string _titleText = Strings.AppName;

    [ObservableProperty]
    private string _imagePositionText = string.Empty;

    [ObservableProperty]
    private string _imageSizeText = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenFolderCommand))]
    [NotifyCanExecuteChangedFor(nameof(NewProjectCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenProjectFileDialogCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenRecentCommand))]
    [NotifyCanExecuteChangedFor(nameof(SaveProjectCommand))]
    [NotifyCanExecuteChangedFor(nameof(SaveProjectAsCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private bool _isNoticeOpen;

    [ObservableProperty]
    private string _noticeTitle = string.Empty;

    [ObservableProperty]
    private string _noticeMessage = string.Empty;

    [ObservableProperty]
    private InfoBarSeverity _noticeSeverity = InfoBarSeverity.Informational;

    private enum NoticeSource
    {
        Other,
        Preview,
    }

    private NoticeSource _noticeSource = NoticeSource.Other;

    public MainViewModel(
        SettingsStore store,
        AppSettings settings,
        StartupOptions options,
        EngineStatusService engines,
        ImagePreviewLoader loader)
    {
        _store = store;
        _settings = settings;
        _options = options;
        _engines = engines;
        _loader = loader;
        _projects = new ProjectService();
        _manager = new ProjectManager(_projects);
        Recent = new RecentProjectsService(settings, store, System.Windows.Threading.Dispatcher.CurrentDispatcher);
        Recent.Items.CollectionChanged -= OnRecentItemsChanged;
        Recent.Items.CollectionChanged += OnRecentItemsChanged;

        // Backing fields, so loading the stored values does not write the settings file back.
        _autoSave = settings.AutoSave;
        _embedImageData = settings.EmbedImageData;
        _outputDirectory = string.IsNullOrWhiteSpace(settings.OutputDirectory) ? null : settings.OutputDirectory;
        _themeMode = ThemeService.Normalize(settings.Theme);
        _uiCultureName = string.Equals(settings.UiCulture, "en", StringComparison.OrdinalIgnoreCase) ? "en" : "vi";
        AgentRunner.PreviewWriter = EasyAI.Desktop.Services.Agent.AgentPreview.TryWrite;
        AiModels = AiModelCatalog.Detect(ModelsRoot);
        Camera = new EasyLabel.Camera.CameraService(options.FakeCamera
            ? new EasyLabel.Camera.Fake.FakeCameraProvider()
            : new EasyLabel.Camera.Mvs.MvsCameraProvider());

        Canvas = new CanvasViewModel(
            System.Windows.Threading.Dispatcher.CurrentDispatcher,
            (title, message) => ShowNotice(title, message, InfoBarSeverity.Error));
        Canvas.Options = settings;
        Canvas.PersistSettings = PersistSettings;
        Canvas.PropertyChanged -= OnCanvasPropertyChanged;
        Canvas.PropertyChanged += OnCanvasPropertyChanged;
        Canvas.DocumentReady -= OnCanvasStateChanged;
        Canvas.DocumentReady += OnCanvasStateChanged;
        Canvas.ImageReplaced -= OnCanvasStateChanged;
        Canvas.ImageReplaced += OnCanvasStateChanged;

        Data = new DataStepViewModel(this);
        Label = new LabelStepViewModel(this);
        Train = new TrainStepViewModel(this);
        Inspect = new InspectViewModel(this);
        Live = new LiveCameraViewModel(this, Camera);
        Capture = new CaptureViewModel(this, Camera);
        Canvas.ImageSaved -=OnCanvasImageSaved;
        Canvas.ImageSaved += OnCanvasImageSaved;
        Canvas.DocumentEdited -= OnCanvasDocumentEdited;
        Canvas.DocumentEdited += OnCanvasDocumentEdited;
        Panels = new PanelsViewModel(this);

        SegAi =new EngineStatusViewModel(Strings.SegAi, true);
        Mvs = new EngineStatusViewModel(Strings.Mvs, false);
        EngineList = new[] { SegAi, Mvs };

        EasyModeSteps = CreateEasyModeSteps();
        Data.PropertyChanged -= OnEasyStateSourceChanged;
        Data.PropertyChanged += OnEasyStateSourceChanged;
        Inspect.PropertyChanged -= OnEasyStateSourceChanged;
        Inspect.PropertyChanged += OnEasyStateSourceChanged;
        RefreshEasyModeSteps();

        UpdatePosition();
    }

    /// <summary>Raised when the window must restore the default layout.</summary>
    public event EventHandler? LayoutResetRequested;

    /// <summary>Raised when the File menu asks the window to close.</summary>
    public event EventHandler? ExitRequested;

    /// <summary>Raised when the Help menu asks the window to show the About dialog.</summary>
    public event EventHandler? AboutRequested;

    /// <summary>View menu: the Files panel is shown (kept in sync with the docking layout by the window).</summary>
    [ObservableProperty]
    private bool _isImageListVisible = true;

    /// <summary>View menu: the merged Label / Shape panel is shown.</summary>
    [ObservableProperty]
    private bool _isLabelsVisible = true;

    /// <summary>View models of the Label / Shape and Files panels.</summary>
    public PanelsViewModel Panels { get; }

    /// <summary>True once a project folder is open (the Add images entries need it).</summary>
    public bool HasProject => Project != null;

    /// <summary>Recent projects of the File menu and the start screen (owned here; created on the UI thread).</summary>
    public RecentProjectsService Recent { get; }

    /// <summary>The start screen lists recent projects when there are any.</summary>
    public bool HasRecentProjects => Recent.Items.Count > 0;

    /// <summary>The canvas area shows the start buttons instead of an image while no image is selected.</summary>
    public bool IsStartScreenVisible => CurrentStep != WorkflowStep.Inspect && SelectedImage == null;

    /// <summary>The Inspect tab shows its own start buttons while its list is empty.</summary>
    public bool IsInspectStartVisible => CurrentStep == WorkflowStep.Inspect && Inspect.IsListEmpty;

    /// <summary>Annotation canvas state (tools, undo, background saving).</summary>
    public CanvasViewModel Canvas { get; }

    /// <summary>Data operations (import, add images, per-image Train/Val).</summary>
    public DataStepViewModel Data { get; }

    /// <summary>Label tab state (model, auto-label, suggestions).</summary>
    public LabelStepViewModel Label { get; }

    /// <summary>Train tab state.</summary>
    public TrainStepViewModel Train { get; }

    /// <summary>Inspect tab state (own image list, model selector, run, results).</summary>
    public InspectViewModel Inspect { get; }

    /// <summary>Live camera tab state (preview, Start / Stop / Snapshot, parameter rows).</summary>
    public LiveCameraViewModel Live { get; }

    /// <summary>Capture buttons of the Label and Inspect tabs (grab one frame from the shared camera into the project).</summary>
    public CaptureViewModel Capture { get; }

    /// <summary>Tabs of the step strip, in order (the app still opens on Label); Live is the last one and exists only for the camera source.</summary>
    private WorkflowStep[] TabSteps => LiveCameraRules.TabSteps(IsCameraSourceActive);

    /// <summary>Selected tab of the step strip (index into <see cref="TabSteps"/>).</summary>
    public int StepTabIndex
    {
        get => LiveCameraRules.TabIndex(TabSteps, CurrentStep);
        set
        {
            WorkflowStep[] steps = TabSteps;
            if (value >= 0 && value < steps.Length && CurrentStep != steps[value] && !TryShowStep(steps[value]))
            {
                // Cancel in the unsaved prompt: the strip goes back to the tab that is still shown.
                System.Windows.Application.Current?.Dispatcher.BeginInvoke(
                    new Action(() => OnPropertyChanged(nameof(StepTabIndex))));
            }
        }
    }

    /// <summary>
    /// Switches the tab. The Inspect tab replaces the canvas image, so held (Auto-save OFF) edits are saved or discarded first;
    /// false means the user cancelled and the current tab stays.
    /// </summary>
    private bool TryShowStep(WorkflowStep target)
    {
        if (target == CurrentStep)
        {
            return true;
        }

        if (target == WorkflowStep.Inspect && !ConfirmLeaveImage(true))
        {
            return false;
        }

        CurrentStep = target;
        return true;
    }

    [RelayCommand]
    private void ShowInspectTab()
    {
        TryShowStep(WorkflowStep.Inspect);
    }

    [RelayCommand]
    private void ShowLabelTab()
    {
        TryShowStep(WorkflowStep.Label);
    }

    [RelayCommand]
    private void ShowTrainTab()
    {
        TryShowStep(WorkflowStep.Train);
    }

    // ---------- Simple (Easy) mode ----------

    private bool _easySentThisSession;

    /// <summary>Simple mode: the guided step strip is shown and the Train tab uses the one-click flow.</summary>
    public bool IsSimpleMode => string.Equals(_settings.UiMode, EasyModeRules.UiModeSimple, StringComparison.Ordinal);

    /// <summary>Advanced mode: the full UI. Also the reading when no mode is stored.</summary>
    public bool IsAdvancedMode => !IsSimpleMode;

    /// <summary>The five guided steps of the Simple strip (Open, Label, Train, Check, Send). Refreshed by <see cref="RefreshEasyModeSteps"/>.</summary>
    public IReadOnlyList<EasyStepViewModel> EasyModeSteps { get; }

    public string EasyStripName => EasyText("EasyStripName", "Guided steps");

    public string UiModeToggleName => EasyText("UiModeToggleName", "Interface mode");

    public string UiModeSimpleText => EasyText("UiModeSimple", "Simple");

    public string UiModeAdvancedText => EasyText("UiModeAdvanced", "Advanced");

    /// <summary>Switches the mode ("simple" or "advanced"; anything else means Advanced), saves it and refreshes the Easy state.</summary>
    [RelayCommand]
    private void SetUiMode(string? mode)
    {
        string value = EasyModeRules.ResolveMode(mode, true);
        if (string.Equals(_settings.UiMode, value, StringComparison.Ordinal))
        {
            return;
        }

        _settings.UiMode = value;
        PersistSettings();
        OnPropertyChanged(nameof(IsSimpleMode));
        OnPropertyChanged(nameof(IsAdvancedMode));
        Train.OnUiModeChanged();
        RefreshEasyModeSteps();
    }

    /// <summary>Easy step 3: opens the Train tab, then runs the one-click train flow of <see cref="TrainStepViewModel.EasyTrainAsync"/>.</summary>
    [RelayCommand]
    private async Task EasyTrainAsync()
    {
        if (!TryShowStep(WorkflowStep.Train))
        {
            return;
        }

        await Train.EasyTrainAsync(IsSimpleMode, EasyKind() == ClassifyRules.KindClassify);
    }

    /// <summary>Records that a send to EasyVision happened in this session (step 5 done). Not yet wired to the send path.</summary>
    internal void NoteEasySent()
    {
        _easySentThisSession = true;
        RefreshEasyModeSteps();
    }

    private IReadOnlyList<EasyStepViewModel> CreateEasyModeSteps()
    {
        var steps = new List<EasyStepViewModel>(EasyModeRules.StepCount);
        for (int i = 0; i < EasyModeRules.StepCount; i++)
        {
            int index = i;
            steps.Add(new EasyStepViewModel(new RelayCommand(() => RunEasyStep(index))));
        }

        return steps;
    }

    /// <summary>Click of a Simple step; a disabled step does nothing.</summary>
    private void RunEasyStep(int index)
    {
        if (index < 0 || index >= EasyModeSteps.Count || !EasyModeSteps[index].IsEnabled)
        {
            return;
        }

        switch (index)
        {
            case 0:
                if (!HasProject)
                {
                    OpenFolderCommand.Execute(null);
                }

                break;
            case 1:
                TryShowStep(WorkflowStep.Label);
                break;
            case 2:
                EasyTrainCommand.Execute(null);
                break;
            case 3:
                if (TryShowStep(WorkflowStep.Inspect) && Inspect.RunAllCommand.CanExecute(null))
                {
                    Inspect.RunAllCommand.Execute(null);
                }

                break;
            case 4:
                _ = ExportCoreAsync(null);
                break;
        }
    }

    /// <summary>Re-evaluates the five steps and writes their text, enabled, current and status.</summary>
    internal void RefreshEasyModeSteps()
    {
        EasyInputs input = BuildEasyInputs();
        EasyStepState[] states = EasyModeRules.Evaluate(input);
        int current = EasyModeRules.CurrentIndex(states);
        System.Globalization.CultureInfo culture = Strings.Culture ?? System.Globalization.CultureInfo.CurrentUICulture;
        for (int i = 0; i < states.Length && i < EasyModeSteps.Count; i++)
        {
            EasyStepViewModel step = EasyModeSteps[i];
            step.Status = StatusText(states[i].Status);
            step.IsEnabled = states[i].IsEnabled;
            step.IsCurrent = i == current;
            step.Text = EasyStepText(i, states[i], input, culture);
        }
    }

    /// <summary>Simple mode kind: OK/NG when some image is OK/NG-tagged and no image has regions (<see cref="ClassifyRules.KindFor"/>).</summary>
    private string EasyKind()
    {
        return ClassifyRules.KindFor(Images.Count(i => i.HasClass), Images.Count(i => i.RegionCount > 0));
    }

    private EasyInputs BuildEasyInputs()
    {
        SegAiDataSummary summary = Data.Summary;
        string kind = EasyKind();
        // OK/NG kind: the labelled images are the tagged ones; the segmentation counts do not apply.
        int labelled = kind == ClassifyRules.KindClassify ? Images.Count(i => i.HasClass) : summary.Train + summary.Val;
        bool inspectRun = Inspect.HasSummary;
        // Counts cover the whole Inspect list (ignoring the filter and search); unknown (-1) until a run exists.
        int ok = inspectRun ? Inspect.OkCount : -1;
        int ng = inspectRun ? Inspect.NgCount : -1;

        return new EasyInputs
        {
            HasProject = HasProject,
            TotalImages = ImageCount,
            Labelled = labelled,
            Kind = kind,
            HostBusy = IsBusy,
            TrainRunning = Train.IsRunning,
            HasActiveModel = Train.HasActiveModel,
            InspectRun = inspectRun ? 1 : 0,
            Ok = ok,
            Ng = ng,
            SentThisSession = _easySentThisSession,
        };
    }

    private string EasyStepText(int index, EasyStepState state, EasyInputs input, System.Globalization.CultureInfo culture)
    {
        bool done = state.Status == EasyStepStatus.Done;
        switch (index)
        {
            case 0:
                return done
                    ? EasyFormat("EasyStepOpenDoneFormat", "1 {0} images", culture, input.TotalImages)
                    : EasyText("EasyStepOpen", "1 Open folder");
            case 1:
                return done
                    ? EasyFormat("EasyStepLabelDoneFormat", "2 {0} labelled", culture, input.Labelled)
                    : EasyFormat("EasyStepLabelFormat", "2 Label {0} more", culture, state.Remaining);
            case 2:
                if (state.Status == EasyStepStatus.Running)
                {
                    string eta = Train.HasEta ? Train.EtaText : "...";
                    return EasyFormat("EasyStepTrainRunningFormat", "3 Training · {0}", culture, eta);
                }

                return done ? EasyText("EasyStepTrainDone", "3 Model ready") : EasyText("EasyStepTrain", "3 Train");
            case 3:
                return done && input.Ok >= 0
                    ? EasyFormat("EasyStepCheckDoneFormat", "4 OK {0} · NG {1}", culture, input.Ok, input.Ng)
                    : EasyText("EasyStepCheck", "4 Check OK/NG");
            default:
                return done ? EasyText("EasyStepSendDone", "5 Sent") : EasyText("EasyStepSend", "5 Send to EasyVision");
        }
    }

    private static string StatusText(EasyStepStatus status)
    {
        switch (status)
        {
            case EasyStepStatus.Ready:
                return "Ready";
            case EasyStepStatus.Running:
                return "Running";
            case EasyStepStatus.Done:
                return "Done";
            default:
                return "Locked";
        }
    }

    private static string EasyText(string key, string fallback)
    {
        return TrainRecommendationRules.Resolve(Strings.ByName, key, fallback);
    }

    private static string EasyFormat(string key, string fallback, System.Globalization.CultureInfo culture, params object[] args)
    {
        return string.Format(culture, EasyText(key, fallback), args);
    }

    private void OnEasyStateSourceChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DataStepViewModel.Summary)
            || e.PropertyName == nameof(InspectViewModel.HasSummary)
            || e.PropertyName == nameof(InspectViewModel.OkCount)
            || e.PropertyName == nameof(InspectViewModel.NgCount))
        {
            RefreshEasyModeSteps();
        }
    }

    public EngineStatusViewModel SegAi { get; }

    public EngineStatusViewModel Mvs { get; }

    internal bool HasSession => _segSession != null;

    internal SegAiSession? Session => _segSession;

    public IReadOnlyList<EngineStatusViewModel> EngineList { get; }

    /// <summary>Starts the engine loads and opens the --project folder, if any. Never throws.</summary>
    public async Task InitializeAsync()
    {
        try
        {
            if (_options.Warnings.Count > 0)
            {
                ShowNotice(Strings.StartupWarningTitle, string.Join(Environment.NewLine, _options.Warnings), InfoBarSeverity.Warning);
            }

            Task<NativeSegAiLoadResult> segAiTask = _engines.LoadSegAiAsync(_settings.NativeSegAiFolder);
            Task<MvsSdkLoadResult> mvsTask = _engines.LoadMvsAsync();

            Recent.Refresh();
            if (!string.IsNullOrWhiteSpace(_options.ProjectFile))
            {
                await OpenProjectFileAsync(_options.ProjectFile!);
            }
            else if (!string.IsNullOrWhiteSpace(_options.ProjectFolder))
            {
                await OpenProjectAsync(_options.ProjectFolder!);
            }

            ApplySegAi(await segAiTask);
            ApplyMvs(await mvsTask);
        }
        catch (Exception ex)
        {
            _store.Log("Initialize failed: " + ex);
            ShowNotice(Strings.UnhandledErrorTitle, ex.Message, InfoBarSeverity.Error);
        }
    }

    public void ReportUnhandled(Exception exception)
    {
        ShowNotice(Strings.UnhandledErrorTitle, exception.Message, InfoBarSeverity.Error);
    }

    private void OnCanvasPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CanvasViewModel.IsDirty))
        {
            RefreshTitle();
            SaveCommand.NotifyCanExecuteChanged();
        }
    }

    private void OnCanvasStateChanged(object? sender, EventArgs e)
    {
        SaveAsCommand.NotifyCanExecuteChanged();
        SaveCommand.NotifyCanExecuteChanged();
    }

    private void RefreshTitle()
    {
        TitleText = SavePolicy.FormatTitle(_baseTitle, Canvas.IsDirty);
    }

    private void OnCanvasImageSaved(string rel)
    {
        StatusMessage = string.Format(Strings.SbSavedFormat, rel);
        // Space and the badges depend on the image state, so follow the canvas as soon as its edits are written.
        ImageItemViewModel? item = Images.FirstOrDefault(i => string.Equals(i.Rel, rel, StringComparison.OrdinalIgnoreCase));
        if (item != null)
        {
            _ = Data.RefreshItemAsync(item);
        }
    }

    private void OnRecentItemsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(HasRecentProjects));
        ClearRecentCommand.NotifyCanExecuteChanged();
    }

    partial void OnProjectChanged(Project? value)
    {
        OnPropertyChanged(nameof(HasProject));
        SaveProjectCommand.NotifyCanExecuteChanged();
        SaveProjectAsCommand.NotifyCanExecuteChanged();
        ExportCommand.NotifyCanExecuteChanged();
        ExportLabelMeCommand.NotifyCanExecuteChanged();
        ImportLabelMeCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(ExportDisabledReason));
        Capture?.NotifyStateChanged();
        // The agent chat history belongs to the project: show the one of the project that was just opened (or none).
        Label?.ReloadChat();
        RefreshEasyModeSteps();
    }

    partial void OnSelectedImageChanged(ImageItemViewModel? oldValue, ImageItemViewModel? newValue)
    {
        if (_revertingSelection)
        {
            return;
        }

        if (oldValue != null && !ReferenceEquals(oldValue, newValue) && !ConfirmLeaveImage(true))
        {
            // Cancel in the unsaved prompt: stay on the image that still holds the edits (the canvas was not touched yet).
            _revertingSelection = true;
            try
            {
                SelectedImage = oldValue;
            }
            finally
            {
                _revertingSelection = false;
            }

            // The list already moved to the new row; re-publish the old selection after the binding update finished.
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(
                new Action(() => OnPropertyChanged(nameof(SelectedImage))));
            return;
        }

        OnPropertyChanged(nameof(IsStartScreenVisible));
        CloseImageCommand.NotifyCanExecuteChanged();
        DeleteAnnotationFilesCommand.NotifyCanExecuteChanged();
        DeleteImageCommand.NotifyCanExecuteChanged();
        SaveAsCommand.NotifyCanExecuteChanged();
        UpdatePosition();
        _ = LoadPreviewAsync(newValue);
        Data.NotifyStateChanged();
        Label.NotifyStateChanged();
        Capture?.NotifyStateChanged();
        if (oldValue != null && !ReferenceEquals(oldValue, newValue))
        {
            // The edits of the image just left reach disk through the save queue; read its state back once they did.
            _ = Data.RefreshItemAsync(oldValue);
        }
    }

    partial void OnIsBusyChanged(bool value)
    {
        SaveAsCommand.NotifyCanExecuteChanged();
        DeleteAnnotationFilesCommand.NotifyCanExecuteChanged();
        DeleteImageCommand.NotifyCanExecuteChanged();
        ExportCommand.NotifyCanExecuteChanged();
        ExportLabelMeCommand.NotifyCanExecuteChanged();
        ImportLabelMeCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(ExportDisabledReason));
        Data.NotifyStateChanged();
        Label.NotifyStateChanged();
        Train.NotifyStateChanged();
        Inspect.NotifyStateChanged();
        Capture?.NotifyStateChanged();
        RefreshEasyModeSteps();
    }

    /// <summary>
    /// Runs a training run while holding the busy flag and blocking canvas input, so no project can be opened, no Step 1 or Step 2
    /// operation started and no annotation edited meanwhile. The canvas edits are flushed to disk first. Returns false when there is
    /// no session, something else is running, or the work failed (notice shown with <paramref name="errorTitle"/>).
    /// </summary>
    internal async Task<bool> RunTrainingAsync(Func<SegAiSession, Task> work, string errorTitle)
    {
        SegAiSession? session = _segSession;
        if (session == null || IsBusy)
        {
            return false;
        }

        // Training reads the files on disk, so held (Auto-save OFF) edits must be written first.
        if (!ConfirmLeaveImage(false))
        {
            return false;
        }

        // Input first, so the commands re-evaluated by the busy flag already see the blocked canvas.
        Canvas.InputBlocked = true;
        IsBusy = true;
        try
        {
            await Canvas.FlushAsync();
            await work(session);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            _store.Log("Training failed: " + ex);
            ShowNotice(errorTitle, ex.Message, InfoBarSeverity.Error);
            return false;
        }
        finally
        {
            Canvas.InputBlocked = false;
            IsBusy = false;
        }
    }

    /// <summary>
    /// Runs a Step 2 operation (model change/import, auto-label) while holding the busy flag, so no project can be opened or
    /// Step 1 operation started meanwhile. Returns false when there is no session, something else is running, the work was
    /// cancelled, or it failed (notice shown with <paramref name="errorTitle"/>).
    /// </summary>
    internal async Task<bool> RunLabelAsync(Func<SegAiSession, Task> work, string errorTitle)
    {
        SegAiSession? session = _segSession;
        if (session == null || IsBusy)
        {
            return false;
        }

        // Auto-label, accept and import rewrite files on disk: held (Auto-save OFF) edits must be written or discarded first.
        if (!ConfirmLeaveImage(false))
        {
            return false;
        }

        IsBusy = true;
        try
        {
            await work(session);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            _store.Log("Label step operation failed: " + ex);
            ShowNotice(errorTitle, ex.Message, InfoBarSeverity.Error);
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Runs a Step 1 operation that writes to the project: flushes the canvas save queue, blocks canvas input, runs
    /// <paramref name="work"/> on a background task and, when <paramref name="reload"/> is set, refreshes the image list and
    /// the summary. Returns null when there is no session, another operation is running, or the work failed (notice shown).
    /// </summary>
    internal async Task<T?> RunStep1Async<T>(Func<SegAiSession, T> work, bool reload) where T : class
    {
        SegAiSession? session = _segSession;
        if (session == null || IsBusy)
        {
            return null;
        }

        if (!ConfirmLeaveImage(false))
        {
            return null;
        }

        IsBusy = true;
        Canvas.InputBlocked = true;
        try
        {
            await Canvas.FlushAsync();
            T result = await Task.Run(() => work(session));
            if (reload && ReferenceEquals(session, _segSession))
            {
                await ReloadImagesAsync(session);
            }

            return result;
        }
        catch (Exception ex)
        {
            _store.Log("Data step operation failed: " + ex);
            ShowNotice(Strings.DataErrorTitle, ex.Message, InfoBarSeverity.Error);
            if (reload && ReferenceEquals(session, _segSession))
            {
                // The work may have copied files before failing; resync the list and summary while still busy.
                try
                {
                    await ReloadImagesAsync(session);
                }
                catch (Exception reloadEx)
                {
                    _store.Log("Data step reload after failure failed: " + reloadEx);
                }
            }

            return null;
        }
        finally
        {
            Canvas.InputBlocked = false;
            IsBusy = false;
        }
    }

    internal void ShowDataNotice(string title, string message, InfoBarSeverity severity)
    {
        ShowNotice(title, message, severity);
    }

    internal void LogData(string message)
    {
        _store.Log(message);
    }

    /// <summary>
    /// Re-reads the image list and the Segment AI state of every image. Existing items are kept (and updated in place) so the
    /// selection, the open image and its undo stack survive; new images are inserted in path order.
    /// </summary>
    private async Task ReloadImagesAsync(SegAiSession session)
    {
        await SynchronizeAnnotationClassesAsync(session);
        Dictionary<string, ImageItemViewModel> existing = new Dictionary<string, ImageItemViewModel>(StringComparer.OrdinalIgnoreCase);
        foreach (ImageItemViewModel item in Images)
        {
            existing[item.DisplayName] = item;
        }

        Project? current = Project;
        (Project project, List<ImageItemViewModel> items, Dictionary<string, SegAiSampleInfo> samples) = await Task.Run(() =>
        {
            // Reload keeps the manifest of a named project (title, Save Project); a legacy project has none.
            bool sameProject = current != null
                && string.Equals(current.Paths.ImageRoot, session.Paths.ImageRoot, StringComparison.OrdinalIgnoreCase)
                && string.Equals(current.Paths.MetaDir, session.Paths.MetaDir, StringComparison.OrdinalIgnoreCase);
            Project opened = sameProject ? _manager.Reload(current!) : _projects.Open(session.Paths);
            List<ImageItemViewModel> list = new List<ImageItemViewModel>(opened.Images.Count);
            foreach (ImageItem image in opened.Images)
            {
                ImageItemViewModel created = new ImageItemViewModel(image, opened.RootDir);
                list.Add(existing.TryGetValue(created.DisplayName, out ImageItemViewModel? kept) ? kept : created);
            }

            return (opened, list, ReadSamples(session));
        });

        ApplySamples(items, samples);

        HashSet<ImageItemViewModel> keep = new HashSet<ImageItemViewModel>(items);
        for (int i = Images.Count - 1; i >= 0; i--)
        {
            if (!keep.Contains(Images[i]))
            {
                Images.RemoveAt(i);
            }
        }

        for (int i = 0; i < items.Count; i++)
        {
            if (i < Images.Count && ReferenceEquals(Images[i], items[i]))
            {
                continue;
            }

            Images.Insert(i, items[i]);
        }

        Project = project;
        ImageCount = Images.Count;
        ClassCount = project.Classes.Count;
        RefreshEasyModeSteps();
        UpdatePosition();
        Data.Rebuild();
    }

    /// <summary>Reads the Segment AI state of every image of the project (background-safe). Null session or failure gives an empty map.</summary>
    private Dictionary<string, SegAiSampleInfo> ReadSamples(SegAiSession? session)
    {
        Dictionary<string, SegAiSampleInfo> byRel = new Dictionary<string, SegAiSampleInfo>(StringComparer.OrdinalIgnoreCase);
        if (session == null)
        {
            return byRel;
        }

        try
        {
            foreach (SegAiSampleInfo sample in session.Samples)
            {
                byRel[sample.Rel] = sample;
            }
        }
        catch (Exception ex)
        {
            _store.Log("Read sample states failed: " + ex.Message);
        }

        return byRel;
    }

    /// <summary>Copies sample states onto the matching image items (UI thread).</summary>
    private static void ApplySamples(IEnumerable<ImageItemViewModel> items, Dictionary<string, SegAiSampleInfo> byRel)
    {
        foreach (ImageItemViewModel item in items)
        {
            if (byRel.TryGetValue(item.Rel, out SegAiSampleInfo? info))
            {
                item.Info = info;
            }
        }
    }

    partial void OnImagesChanged(ObservableCollection<ImageItemViewModel> value)
    {
        UpdatePosition();
    }

    /// <summary>The drawing/editing commands are only on the Label tab (the Inspect canvas is read-only, like the Train tab).</summary>
    public bool IsLabelTab => CurrentStep != WorkflowStep.Train && CurrentStep != WorkflowStep.Inspect && CurrentStep != WorkflowStep.Live;

    /// <summary>The Inspect tab is the current one; image-list commands of the training files are unavailable then.</summary>
    public bool IsInspectTab => CurrentStep == WorkflowStep.Inspect;

    /// <summary>True while the canvas shows an Inspect image instead of the selected training image.</summary>
    private bool _canvasShowsInspect;

    partial void OnCurrentStepChanged(WorkflowStep oldValue, WorkflowStep newValue)
    {
        OnPropertyChanged(nameof(IsLabelTab));
        OnPropertyChanged(nameof(IsInspectTab));
        OnPropertyChanged(nameof(IsStartScreenVisible));
        OnPropertyChanged(nameof(IsInspectStartVisible));
        OnPropertyChanged(nameof(StepTabIndex));
        NextImageCommand.NotifyCanExecuteChanged();
        PreviousImageCommand.NotifyCanExecuteChanged();
        CloseImageCommand.NotifyCanExecuteChanged();
        SaveAsCommand.NotifyCanExecuteChanged();
        DeleteAnnotationFilesCommand.NotifyCanExecuteChanged();
        DeleteImageCommand.NotifyCanExecuteChanged();
        Label.RefreshSuggestions();
        Train.OnStepChanged();
        Inspect.OnStepChanged(oldValue, newValue);
        Live.OnStepChanged(newValue);
        if (newValue == WorkflowStep.Label && _canvasShowsInspect)
        {
            // The canvas still holds an Inspect image: bring back the selected training image and its document.
            _ = LoadPreviewAsync(SelectedImage);
        }
    }

    internal void NotifyInspectStartChanged()
    {
        OnPropertyChanged(nameof(IsInspectStartVisible));
    }

    /// <summary>
    /// Shows an image of the Inspect list on the canvas without a document (nothing is editable, nothing is saved); null clears the canvas.
    /// <paramref name="isCurrent"/> says whether the request is still the wanted one when the decode finished. Returns true when the image is shown.
    /// </summary>
    internal async Task<bool> ShowInspectImageAsync(string? fullPath, Func<bool> isCurrent)
    {
        try
        {
            _canvasShowsInspect = true;
            if (fullPath == null)
            {
                PreviewImage = null;
                ImageSizeText = string.Empty;
                await Canvas.ShowAsync(null, null, 0, 0);
                return false;
            }

            ImagePreviewResult? result = await _loader.LoadAsync(fullPath);
            if (result == null || !isCurrent())
            {
                return false;
            }

            if (result.Image == null)
            {
                PreviewImage = null;
                ImageSizeText = string.Empty;
                await Canvas.ShowAsync(null, null, 0, 0);
                ShowNotice(Strings.PreviewErrorTitle, System.IO.Path.GetFileName(fullPath) + ": " + (result.Error ?? string.Empty), InfoBarSeverity.Warning, NoticeSource.Preview);
                return false;
            }

            if (IsNoticeOpen && _noticeSource == NoticeSource.Preview)
            {
                IsNoticeOpen = false;
            }

            PreviewImage = result.Image;
            ImageSizeText = string.Format(Strings.ImageSizeFormat, result.PixelWidth, result.PixelHeight);
            await Canvas.ShowAsync(null, result.Image, result.PixelWidth, result.PixelHeight);
            return isCurrent();
        }
        catch (Exception ex)
        {
            _store.Log("Inspect preview failed: " + ex);
            ShowNotice(Strings.PreviewErrorTitle, ex.Message, InfoBarSeverity.Error, NoticeSource.Preview);
            return false;
        }
    }

    private bool CanOpenFolder() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanOpenFolder))]
    private async Task OpenFolderAsync()
    {
        Microsoft.Win32.OpenFolderDialog dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = Strings.OpenFolderDialogTitle,
            Multiselect = false,
        };

        if (Project != null && Directory.Exists(Project.RootDir))
        {
            dialog.InitialDirectory = Project.RootDir;
        }

        if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.FolderName))
        {
            return;
        }

        await OpenProjectAsync(dialog.FolderName);
    }

    // ---------- named projects: New, Open, Open Recent, Save, Save As ----------

    private string DefaultProjectLocation()
    {
        return ProjectDefaults.Location(_settings.LastProjectLocation, Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
    }

    private void RememberProjectLocation(string? location)
    {
        if (string.IsNullOrWhiteSpace(location))
        {
            return;
        }

        _settings.LastProjectLocation = location;
        _store.SaveSettings(_settings);
    }

    /// <summary>File &gt; New Project: name, location and an optional image folder; the project is created on a worker and opened.</summary>
    [RelayCommand(CanExecute = nameof(CanOpenFolder))]
    private async Task NewProjectAsync()
    {
        IDialogService? dialogs = Dialogs;
        if (dialogs == null || !ConfirmLeaveImage(true))
        {
            return;
        }

        string location = DefaultProjectLocation();
        NewProjectInput? input = dialogs.AskNewProject(ProjectDefaults.FreeName(Strings.ProjDefaultName, location), location);
        if (input == null)
        {
            return;
        }

        RememberProjectLocation(input.Location);
        await OpenCoreAsync(input.Name, () =>
        {
            ProjectOpenResult created = _manager.Create(input.Name, input.Location, input.ImageFolder, input.Kind);
            return (created.Project, created);
        });
    }

    /// <summary>File &gt; Open Project: picks a <c>.easyai</c> file and opens it.</summary>
    [RelayCommand(CanExecute = nameof(CanOpenFolder))]
    private async Task OpenProjectFileDialogAsync()
    {
        IDialogService? dialogs = Dialogs;
        if (dialogs == null)
        {
            return;
        }

        string? initial = Project?.Manifest != null ? System.IO.Path.GetDirectoryName(Project.Manifest.ManifestPath) : _settings.LastProjectLocation;
        string? path = dialogs.PickProjectFile(initial);
        if (path == null)
        {
            return;
        }

        await OpenProjectFileAsync(path);
    }

    /// <summary>File &gt; Open Recent and the start-screen list: opens the entry; a missing one offers to remove it from the list.</summary>
    [RelayCommand(CanExecute = nameof(CanOpenFolder))]
    private async Task OpenRecentAsync(RecentProjectItem? item)
    {
        if (item == null)
        {
            return;
        }

        if (!item.Exists)
        {
            OfferRemoveMissing(item);
            return;
        }

        bool named = item.Kind == RecentProjectKind.Named;
        bool opened = named ? await OpenCoreAsync(item.Path, () => OpenNamed(item.Path)) : await OpenCoreAsync(item.Path, () => (_projects.Open(item.Path), null));
        if (!opened && !(named ? File.Exists(item.Path) : Directory.Exists(item.Path)))
        {
            OfferRemoveMissing(item);
        }
    }

    private (Project Project, ProjectOpenResult? Named) OpenNamed(string manifestPath)
    {
        ProjectOpenResult result = _manager.OpenFile(manifestPath);
        return (result.Project, result);
    }

    private void OfferRemoveMissing(RecentProjectItem item)
    {
        if (Dialogs != null && Dialogs.Confirm(Strings.RecentMissingTitle, string.Format(Strings.RecentMissingFormat, item.Path), Strings.RecentRemove))
        {
            Recent.Remove(item.Path);
        }
    }

    [RelayCommand]
    private void RemoveRecent(RecentProjectItem? item)
    {
        if (item != null)
        {
            Recent.Remove(item.Path);
        }
    }

    [RelayCommand(CanExecute = nameof(HasRecentProjects))]
    private void ClearRecent()
    {
        Recent.Clear();
    }

    private bool CanSaveProject() => Project != null && !IsBusy;

    /// <summary>File &gt; Save Project (Ctrl+S): writes the held edits of the open image and, for a named project, touches its project file.</summary>
    [RelayCommand(CanExecute = nameof(CanSaveProject))]
    private void SaveProject()
    {
        Project? project = Project;
        if (project == null)
        {
            return;
        }

        if (Canvas.IsDirty && !Canvas.SaveNow())
        {
            return;
        }

        if (project.Manifest == null)
        {
            StatusMessage = string.Format(Strings.SbProjectSavedFormat, project.RootDir);
            return;
        }

        try
        {
            ProjectManifest manifest = _manager.SaveManifest(project);
            StatusMessage = string.Format(Strings.SbProjectSavedFormat, manifest.Name);
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            _store.Log("Save project file failed: " + ex);
            ShowNotice(Strings.ProjSaveErrorTitle, ex.Message, InfoBarSeverity.Warning);
        }
    }

    /// <summary>
    /// File &gt; Save Project As (Ctrl+Shift+S): duplicates the project (also a legacy image folder, which converts) under a new name, with
    /// progress and Cancel, then opens the copy. The source is never changed.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSaveProject))]
    private async Task SaveProjectAsAsync()
    {
        Project? project = Project;
        IDialogService? dialogs = Dialogs;
        if (project == null || dialogs == null || !ConfirmLeaveImage(true))
        {
            return;
        }

        ProjectOpenResult? copy = null;
        IsBusy = true;
        Canvas.InputBlocked = true;
        try
        {
            // The copy reads the files on disk, so every queued edit must be written first.
            await Canvas.FlushAsync();

            string location = DefaultProjectLocation();
            string stem = project.Manifest?.Name ?? System.IO.Path.GetFileName(project.RootDir.TrimEnd('\\', '/'));
            string suggested = ProjectDefaults.FreeName(stem, location);
            copy = dialogs.RunSaveProjectAs(
                suggested,
                location,
                (name, target, copyImages, progress, token) => _manager.SaveAs(project, name, target, copyImages, progress, token));
        }
        catch (Exception ex)
        {
            _store.Log("Save project as failed: " + ex);
            ShowNotice(Strings.ProjectErrorTitle, ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            Canvas.InputBlocked = false;
            IsBusy = false;
        }

        if (copy == null)
        {
            return;
        }

        RememberProjectLocation(System.IO.Path.GetDirectoryName(copy.Manifest.ManifestPath));
        await OpenCoreAsync(copy.Manifest.ManifestPath, () => (copy.Project, copy));
    }

    private bool CanSelectNext() => !IsInspectTab && SelectedImage != null && Panels.Files.Items.IndexOf(SelectedImage) < Panels.Files.Items.Count - 1;

    private bool CanSelectPrevious() => !IsInspectTab && SelectedImage != null && Panels.Files.Items.IndexOf(SelectedImage) > 0;

    [RelayCommand(CanExecute = nameof(CanSelectNext))]
    private void NextImage()
    {
        // Steps through the list as it is shown (order toggle, search filter), not through the project order.
        System.Collections.ObjectModel.ObservableCollection<ImageItemViewModel> shown = Panels.Files.Items;
        int index = SelectedImage == null ? -1 : shown.IndexOf(SelectedImage);
        if (index >= 0 && index < shown.Count - 1)
        {
            SelectedImage = shown[index + 1];
        }
    }

    [RelayCommand(CanExecute = nameof(CanSelectPrevious))]
    private void PreviousImage()
    {
        System.Collections.ObjectModel.ObservableCollection<ImageItemViewModel> shown = Panels.Files.Items;
        int index = SelectedImage == null ? -1 : shown.IndexOf(SelectedImage);
        if (index > 0)
        {
            SelectedImage = shown[index - 1];
        }
    }

    /// <summary>Any edit of the open document (the flags panel can write the ok/ng keys): the open image's class follows the document.</summary>
    private void OnCanvasDocumentEdited(object? sender, EventArgs e)
    {
        ImageItemViewModel? current = SelectedImage;
        EasyLabel.Core.Model.LabelMeDocument? document = Canvas.Document;
        if (current == null || document == null)
        {
            return;
        }

        byte value = EasyLabel.Core.Model.ImageClassFlags.Read(document.Flags);
        if (current.ImageClass == value)
        {
            return;
        }

        current.ImageClass = value;
        Label.NotifyClassesChanged();
        Train.NotifyClassesChanged();
        Panels.Files.NotifyClassesChanged();
    }

    /// <summary>
    /// G/B: sets the OK/NG class of the open image (one undo unit), then opens the next image of the list. Returns false when the
    /// class was not changed (Inspect tab, no editable image, or the class already had that value).
    /// </summary>
    public bool TagOpenImageClass(byte value)
    {
        if (IsInspectTab || !Canvas.SetImageClass(value))
        {
            return false;
        }

        ImageItemViewModel? current = SelectedImage;
        if (current != null)
        {
            current.ImageClass = value;
        }

        // Next untagged image of the list; when none is left after this one, the plain next image (as before).
        int index = current == null ? -1 : Images.IndexOf(current);
        int next = ClassifyRules.NextUntaggedIndex(Images.Select(i => i.ImageClass).ToList(), index);
        if (next >= 0)
        {
            SelectedImage = Images[next];
        }
        else
        {
            NextImage();
        }

        Label.NotifyClassesChanged();
        Train.NotifyClassesChanged();
        Panels.Files.NotifyClassesChanged();
        return true;
    }

    // ---------- save model, settings, help ----------

    /// <summary>Set by the window; asks the questions and opens the Settings and Guide windows.</summary>
    public IDialogService? Dialogs { get; set; }

    /// <summary>Class menu "Edit classes...": the class table editor of the open project; a saved table reloads the canvas classes.</summary>
    [RelayCommand]
    private void EditClasses()
    {
        SegAiSession? session = _segSession;
        if (session == null || Dialogs == null)
        {
            return;
        }

        if (Dialogs.EditClassTable(session))
        {
            Canvas.RefreshClasses();
        }
    }

    /// <summary>
    /// Right-click "New label...": appends one defect class to the project class table, with or without any box drawn; the canvas classes,
    /// the label list and the prompt chips reload from the saved table.
    /// </summary>
    [RelayCommand]
    private void NewLabel()
    {
        SegAiSession? session = _segSession;
        if (session == null || Dialogs == null)
        {
            ShowDataNotice(Strings.NewLabelTitle, Strings.NewLabelNeedSession, InfoBarSeverity.Informational);
            return;
        }

        string? name = Dialogs.AskNewLabel();
        if (name == null)
        {
            return;
        }

        List<SegAiClassEntry> draft = session.ClassTable.Classes.ToList();
        draft.Add(new SegAiClassEntry(name, SegAiClassKind.Defect, true));
        SegAiModelProblem? problem = ClassTableEditRules.Validate(draft);
        if (problem != null)
        {
            ShowDataNotice(Strings.NewLabelTitle, string.Format(Strings.NewLabelFailedFormat, problem), InfoBarSeverity.Warning);
            return;
        }

        try
        {
            session.SaveClassTable(new SegAiClassTable(draft));
        }
        catch (Exception ex) when (ex is ArgumentException || ex is IOException || ex is UnauthorizedAccessException || ex is InvalidDataException)
        {
            ShowDataNotice(Strings.NewLabelTitle, string.Format(Strings.NewLabelFailedFormat, ex.Message), InfoBarSeverity.Warning);
            return;
        }

        Canvas.RefreshClasses();
    }

    /// <summary>The window applies a theme change (it owns the backdrop watcher).</summary>
    public event EventHandler<string>? ThemeChanged;

    /// <summary>Stored application settings (Settings window rows read and write through the properties above).</summary>
    internal AppSettings Settings => _settings;

    /// <summary>Detected AI model files (SAM2, YOLO-World). No inference is integrated, so every AI control stays disabled.</summary>
    public IReadOnlyList<AiModelStatus> AiModels { get; private set; }

    /// <summary>Folder that holds one sub-folder per AI model (sam2, yolo-world, custom-detect); empty when the settings folder is unknown.</summary>
    internal string ModelsRoot => _store.Directory.Length == 0 ? string.Empty : System.IO.Path.Combine(_store.Directory, "models");

    /// <summary>Looks for the AI model files again (after "Use this model" installed one).</summary>
    internal void RefreshAiModels()
    {
        AiModels = AiModelCatalog.Detect(ModelsRoot);
        OnPropertyChanged(nameof(AiModels));
        OnPropertyChanged(nameof(AiPointsStatusText));
        OnPropertyChanged(nameof(AiTextStatusText));
    }

    public string AiPointsStatusText => SettingsCatalog.AiStatusText(Strings.ByName, AiModels.First(m => m.Id == AiModelCatalog.Sam2Id));

    public string AiTextStatusText => SettingsCatalog.AiStatusText(Strings.ByName, AiModels.First(m => m.Id == AiModelCatalog.YoloWorldId));

    partial void OnAutoSaveChanged(bool value)
    {
        _settings.AutoSave = value;
        PersistSettings();
        if (value)
        {
            // Switching Auto-save ON writes whatever was held.
            Canvas.SaveNow();
        }
    }

    partial void OnEmbedImageDataChanged(bool value)
    {
        _settings.EmbedImageData = value;
        PersistSettings();
    }

    partial void OnOutputDirectoryChanged(string? value)
    {
        _settings.OutputDirectory = value;
        PersistSettings();
    }

    partial void OnThemeModeChanged(string value)
    {
        _settings.Theme = value;
        PersistSettings();
        ThemeChanged?.Invoke(this, value);
    }

    partial void OnUiCultureNameChanged(string value)
    {
        _settings.UiCulture = value;
        PersistSettings();
    }

    /// <summary>The agent program chosen in Settings (<see cref="AppSettings.AgentBackend"/>); anything but "codex" is Claude Code.</summary>
    internal EasyAI.Desktop.Services.Agent.AgentTool AgentBackend
        => string.Equals(_settings.AgentBackend, "codex", StringComparison.OrdinalIgnoreCase)
            ? EasyAI.Desktop.Services.Agent.AgentTool.Codex
            : EasyAI.Desktop.Services.Agent.AgentTool.Claude;

    /// <summary>Raised after Settings changed <see cref="AgentBackend"/>; the Label step looks for the new program again.</summary>
    internal event EventHandler? AgentBackendChanged;

    internal void SetAgentBackend(EasyAI.Desktop.Services.Agent.AgentTool tool)
    {
        string value = tool == EasyAI.Desktop.Services.Agent.AgentTool.Codex ? "codex" : "claude";
        if (string.Equals(_settings.AgentBackend, value, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _settings.AgentBackend = value;
        PersistSettings();
        AgentBackendChanged?.Invoke(this, EventArgs.Empty);
    }

    internal void PersistSettings()
    {
        _store.SaveSettings(_settings);
    }

    /// <summary>The colour theme palette changed (a Settings colour row or the reset button): the window applies it live.</summary>
    public event EventHandler? PaletteChanged;

    /// <summary>The stored colour theme (invalid stored colours fall back to the logo colours).</summary>
    internal ThemePalette Palette => SettingsAccessor.PaletteOf(_settings);

    /// <summary>A palette colour was stored in <see cref="Settings"/>: write the file and apply the colours.</summary>
    public void NotifyPaletteChanged()
    {
        PersistSettings();
        PaletteChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>"Reset to logo": the four palette colours go back to the logo defaults.</summary>
    public void ResetPalette()
    {
        SettingsAccessor.ResetPalette(_settings);
        NotifyPaletteChanged();
    }

    /// <summary>The cache folder of the open project, or null when no project is open.</summary>
    internal string? CacheDir => Project?.Paths.CacheDir;

    private int _cacheTrimRunning;

    /// <summary>
    /// Trims the project cache to the stored limit on a low-priority background thread: never on the UI thread, never while a training
    /// run is active, one run at a time. Does not wait and does not throw, so opening a project is never delayed.
    /// </summary>
    internal void StartCacheTrim()
    {
        try
        {
            string? dir = CacheDir;
            if (dir == null || (_segSession != null && _segSession.IsTrainingActive))
            {
                return;
            }

            if (System.Threading.Interlocked.CompareExchange(ref _cacheTrimRunning, 1, 0) != 0)
            {
                return;
            }

            long maxBytes = (long)_settings.CacheMaxMegabytes * 1024L * 1024L;
            var thread = new System.Threading.Thread(() =>
            {
                try
                {
                    EasyLabel.Core.CacheGovernor.Trim(dir, maxBytes);
                }
                catch (Exception)
                {
                    // Trim never throws; this only guards the thread.
                }
                finally
                {
                    System.Threading.Interlocked.Exchange(ref _cacheTrimRunning, 0);
                }
            })
            {
                IsBackground = true,
                Priority = System.Threading.ThreadPriority.BelowNormal,
                Name = "CacheTrim",
            };
            thread.Start();
        }
        catch (Exception ex)
        {
            System.Threading.Interlocked.Exchange(ref _cacheTrimRunning, 0);
            _store.Log("Cache trim failed to start: " + ex.Message);
        }
    }

    /// <summary>The cache size limit was stored: write the file and trim to the new limit in the background.</summary>
    public void NotifyCacheLimitChanged()
    {
        PersistSettings();
        StartCacheTrim();
    }

    /// <summary>A Settings dialog row stored a canvas / label option: write the file and let the live canvas pick it up.</summary>
    public void NotifySettingChanged()
    {
        PersistSettings();
        Canvas.ApplySettings();
    }

    /// <summary>The one camera connection of the app (Settings page, live tab and capture buttons share it). Closed at shutdown.</summary>
    public EasyLabel.Camera.CameraService Camera { get; }

    /// <summary>True while the image source setting is the MVS camera (the live tab and capture buttons depend on it).</summary>
    public bool IsCameraSourceActive => string.Equals(_settings.ImageSource, AppSettings.ImageSourceCameraMvs, StringComparison.Ordinal);

    /// <summary>The image source row stored a value: write the file, tell listeners, and close the camera when the source is Files.</summary>
    public void NotifyImageSourceChanged()
    {
        PersistSettings();
        // The Live tab goes first: leave it before its strip item disappears, so the strip never points at a hidden tab.
        CurrentStep = LiveCameraRules.StepAfterSourceChange(CurrentStep, IsCameraSourceActive);
        OnPropertyChanged(nameof(IsCameraSourceActive));
        OnPropertyChanged(nameof(StepTabIndex));
        Capture.NotifyStateChanged();
        if (!IsCameraSourceActive)
        {
            Camera.Disconnect();
        }
    }

    /// <summary>
    /// True when it is fine to leave the current image: nothing is held, or the user chose Save (queued) or Discard. Cancel keeps
    /// the image. <paramref name="allowDiscard"/> false offers only Save and Cancel (training reads the saved files).
    /// </summary>
    internal bool ConfirmLeaveImage(bool allowDiscard)
    {
        if (!SavePolicy.NeedsPrompt(AutoSave, Canvas.IsDirty))
        {
            return true;
        }

        UnsavedChoice choice = Dialogs == null ? UnsavedChoice.Save : Dialogs.AskUnsaved(Canvas.CurrentRel ?? string.Empty, allowDiscard);
        switch (choice)
        {
            case UnsavedChoice.Save:
                return Canvas.SaveNow();
            case UnsavedChoice.Discard:
                Canvas.DiscardChanges();
                return true;
            default:
                return false;
        }
    }

    /// <summary>Window close: prompts for held edits; false cancels the close.</summary>
    public bool ConfirmExit()
    {
        return ConfirmLeaveImage(true);
    }

    private bool CanSave() => SavePolicy.CanSave(AutoSave, Canvas.IsDirty);

    [RelayCommand(CanExecute = nameof(CanSave))]
    private void Save()
    {
        Canvas.SaveNow();
    }

    private bool CanSaveAs() => SelectedImage != null && Canvas.Session != null && !IsBusy && !IsInspectTab;

    [RelayCommand(CanExecute = nameof(CanSaveAs))]
    private async Task SaveAsAsync()
    {
        ImageItemViewModel? item = SelectedImage;
        IDialogService? dialogs = Dialogs;
        if (item == null || dialogs == null)
        {
            return;
        }

        string? initial = OutputDirectory ?? Project?.RootDir;
        string? path = dialogs.PickSavePath(SavePolicy.SuggestedFileName(item.Rel), initial);
        if (path == null)
        {
            return;
        }

        if (await Canvas.SaveCopyToAsync(path, item.FullPath))
        {
            StatusMessage = string.Format(Strings.SbSavedAsFormat, path);
        }
    }

    [RelayCommand]
    private void ChangeOutputDirectory()
    {
        string? folder = Dialogs?.PickFolder(Strings.DlgOutputDirTitle, OutputDirectory ?? Project?.RootDir);
        if (folder == null)
        {
            return;
        }

        OutputDirectory = folder;
        StatusMessage = string.Format(Strings.SbOutputDirFormat, folder);
    }

    public void ClearOutputDirectory()
    {
        OutputDirectory = null;
        StatusMessage = Strings.SbOutputDirCleared;
    }

    private bool CanCloseImage() => SelectedImage != null && !IsInspectTab;

    /// <summary>Closes the current image (the unsaved prompt runs in the selection change).</summary>
    [RelayCommand(CanExecute = nameof(CanCloseImage))]
    private void CloseImage()
    {
        SelectedImage = null;
    }

    private bool CanDeleteAnnotationFiles() => SelectedImage != null && _segSession != null && !IsBusy && !IsInspectTab;

    /// <summary>Deletes the annotation files of the current image (never the image) after a confirmation that names them.</summary>
    [RelayCommand(CanExecute = nameof(CanDeleteAnnotationFiles))]
    private async Task DeleteAnnotationFilesAsync()
    {
        ImageItemViewModel? item = SelectedImage;
        SegAiSession? session = _segSession;
        IDialogService? dialogs = Dialogs;
        if (item == null || session == null || dialogs == null)
        {
            return;
        }

        string rel = item.Rel;
        await Canvas.FlushAsync();
        IReadOnlyList<string> files;
        try
        {
            files = session.AnnotationFiles(rel);
        }
        catch (Exception ex)
        {
            _store.Log("List annotation files failed: " + ex);
            ShowNotice(Strings.SaveErrorTitle, ex.Message, InfoBarSeverity.Error);
            return;
        }

        if (files.Count == 0)
        {
            StatusMessage = Strings.SbNoAnnotationFiles;
            return;
        }

        if (!dialogs.ConfirmDeleteAnnotations(item.DisplayName, files))
        {
            return;
        }

        IReadOnlyList<string>? deleted = await RunStep1Async(s => s.DeleteAnnotationFiles(rel), false);
        if (deleted == null)
        {
            return;
        }

        StatusMessage = string.Format(Strings.SbDeletedFormat, deleted.Count, item.DisplayName);
        if (ReferenceEquals(item, SelectedImage))
        {
            // Reload the (now empty) document; this also drops edits that were held for the deleted annotation.
            await LoadPreviewAsync(item);
        }

        await Data.RefreshItemAsync(item);
    }

    private bool CanDeleteImage() => SelectedImage != null && _segSession != null && !IsBusy && !IsInspectTab;

    /// <summary>The rows a list command applies to: the Ctrl / Shift selection, or the current image alone.</summary>
    private IReadOnlyList<ImageItemViewModel> SelectedRows()
    {
        IReadOnlyList<ImageItemViewModel> multi = Panels.Files.MultiSelection;
        if (multi.Count > 1 || (multi.Count == 1 && SelectedImage == null))
        {
            return multi.ToList();
        }

        return SelectedImage == null ? Array.Empty<ImageItemViewModel>() : new[] { SelectedImage };
    }

    /// <summary>Delete key on the image list: the selected image(s) and all their label files go to the Recycle Bin after a small Yes / No question.</summary>
    [RelayCommand(CanExecute = nameof(CanDeleteImage))]
    private Task DeleteImageAsync()
    {
        return DeleteImagesAsync(SelectedRows());
    }

    /// <summary>Moves the given images and their label files to the Recycle Bin (after a Yes / No question), removes them from the list and selects a neighbour.</summary>
    internal async Task DeleteImagesAsync(IReadOnlyList<ImageItemViewModel> rows)
    {
        SegAiSession? session = _segSession;
        IDialogService? dialogs = Dialogs;
        if (rows.Count == 0 || session == null || dialogs == null || IsBusy)
        {
            return;
        }

        List<ImageItemViewModel> items = rows.Distinct().ToList();
        await Canvas.FlushAsync();
        string question = items.Count == 1
            ? string.Format(Strings.DlgDeleteImageQuestionFormat, items[0].DisplayName)
            : string.Format(Strings.DlgDeleteImagesQuestionFormat, items.Count);
        if (!dialogs.ConfirmCompact(question))
        {
            return;
        }

        List<string> paths = new List<string>();
        try
        {
            foreach (ImageItemViewModel item in items)
            {
                paths.AddRange(session.AnnotationFiles(item.Rel));
                paths.Add(item.FullPath);
            }
        }
        catch (Exception ex)
        {
            _store.Log("List annotation files failed: " + ex);
            ShowNotice(Strings.SaveErrorTitle, ex.Message, InfoBarSeverity.Error);
            return;
        }

        ObservableCollection<ImageItemViewModel> shown = Panels.Files.Items;
        int index = items.Select(i => shown.IndexOf(i)).Where(i => i >= 0).DefaultIfEmpty(0).Min();
        SelectedImage = null;
        if (SelectedImage != null)
        {
            return;
        }

        IReadOnlyList<string>? deleted = await RunStep1Async(s =>
        {
            foreach (string path in paths)
            {
                RecycleFile(path);
            }

            return (IReadOnlyList<string>)paths;
        }, false);
        if (deleted == null)
        {
            return;
        }

        foreach (ImageItemViewModel item in items)
        {
            Images.Remove(item);
        }

        Data.Rebuild();
        StatusMessage = items.Count == 1
            ? string.Format(Strings.SbDeletedImageFormat, items[0].DisplayName, Math.Max(0, paths.Count - 1))
            : string.Format(Strings.SbDeletedImagesFormat, items.Count);
        shown = Panels.Files.Items;
        if (shown.Count > 0)
        {
            SelectedImage = shown[Math.Min(Math.Max(index, 0), shown.Count - 1)];
        }
    }

    /// <summary>Deletes the label files of the given images (never the images) after a small Yes / No question.</summary>
    internal async Task RemoveLabelsAsync(IReadOnlyList<ImageItemViewModel> rows)
    {
        SegAiSession? session = _segSession;
        IDialogService? dialogs = Dialogs;
        if (rows.Count == 0 || session == null || dialogs == null || IsBusy)
        {
            return;
        }

        List<ImageItemViewModel> items = rows.Distinct().ToList();
        await Canvas.FlushAsync();
        string question = string.Format(Strings.DlgRemoveLabelsQuestionFormat, items.Count == 1 ? items[0].DisplayName : items.Count.ToString(System.Globalization.CultureInfo.CurrentCulture));
        if (!dialogs.ConfirmCompact(question))
        {
            return;
        }

        List<string> rels = items.Select(i => i.Rel).ToList();
        IReadOnlyList<string>? deleted = await RunStep1Async(s =>
        {
            List<string> all = new List<string>();
            foreach (string rel in rels)
            {
                all.AddRange(s.DeleteAnnotationFiles(rel));
            }

            return (IReadOnlyList<string>)all;
        }, false);
        if (deleted == null)
        {
            return;
        }

        StatusMessage = string.Format(Strings.SbRemovedLabelsFormat, items.Count);
        if (SelectedImage != null && items.Contains(SelectedImage))
        {
            await LoadPreviewAsync(SelectedImage);
        }

        foreach (ImageItemViewModel item in items)
        {
            await Data.RefreshItemAsync(item);
        }
    }

    private static void RecycleFile(string path)
    {
        if (!System.IO.File.Exists(path))
        {
            return;
        }

        Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(
            path,
            Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
            Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
    }

    private bool CanExport() => _segSession != null && !IsBusy && !_segSession.IsTrainingActive;

    /// <summary>Why File &gt; Export is unavailable (menu tooltip); null while it is available.</summary>
    public string? ExportDisabledReason
    {
        get
        {
            SegAiSession? session = _segSession;
            if (session == null)
            {
                return Strings.ExpNoProject;
            }

            return IsBusy || session.IsTrainingActive ? Strings.ExpBusy : null;
        }
    }

    /// <summary>Opens the Export dialog. Edits held by Auto-save OFF are written first, because the export reads the files on disk.</summary>
    [RelayCommand(CanExecute = nameof(CanExport))]
    private Task ExportAsync()
    {
        return ExportCoreAsync(null);
    }

    /// <summary>File &gt; Export LabelMe JSON: the same Export dialog with the LabelMe JSON format preselected.</summary>
    [RelayCommand(CanExecute = nameof(CanExport))]
    private Task ExportLabelMeAsync()
    {
        return ExportCoreAsync(EasyLabel.SegAI.Export.ExportFormat.LabelMeJson);
    }

    private async Task ExportCoreAsync(EasyLabel.SegAI.Export.ExportFormat? preset)
    {
        SegAiSession? session = _segSession;
        IDialogService? dialogs = Dialogs;
        if (session == null || dialogs == null || !ConfirmLeaveImage(false))
        {
            return;
        }

        await Canvas.FlushAsync();
        dialogs.ShowExport(session.ProjectRoot, preset, session.Paths);
    }

    /// <summary>
    /// File &gt; Import LabelMe JSON: held edits are written first, then the dialog scans a folder, previews it and imports. The project is
    /// busy meanwhile (no training, no other Step 1 operation); the image list and summary are reloaded afterwards.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanImportLabelMe))]
    private async Task ImportLabelMeAsync()
    {
        IDialogService? dialogs = Dialogs;
        if (dialogs == null || IsBusy)
        {
            return;
        }

        if (_segSession == null && !await OpenImportTargetProjectAsync())
        {
            return;
        }

        SegAiSession? session = _segSession;
        if (session == null || !ConfirmLeaveImage(false))
        {
            return;
        }

        await Canvas.FlushAsync();
        IsBusy = true;
        Canvas.InputBlocked = true;
        try
        {
            dialogs.ShowImportLabelMe(session.ProjectRoot, session.Paths);
            if (ReferenceEquals(session, _segSession))
            {
                await ReloadImagesAsync(session);
            }
        }
        catch (Exception ex)
        {
            _store.Log("Import LabelMe failed: " + ex);
            ShowNotice(Strings.DataErrorTitle, ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            Canvas.InputBlocked = false;
            IsBusy = false;
        }
    }

    /// <summary>Import works without an open project (the toolbar button asks for the project folder first); not while training or busy.</summary>
    private bool CanImportLabelMe() => !IsBusy && (_segSession == null || !_segSession.IsTrainingActive);

    /// <summary>No project is open: asks for the project folder the JSON files are imported into and opens it. False when cancelled or it failed to open.</summary>
    private async Task<bool> OpenImportTargetProjectAsync()
    {
        Microsoft.Win32.OpenFolderDialog dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = Strings.ImportTargetFolderDialogTitle,
            Multiselect = false,
        };

        if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.FolderName))
        {
            return false;
        }

        await OpenProjectAsync(dialog.FolderName);
        return _segSession != null;
    }

    /// <summary>Tools &gt; Convert LabelMe folder to YOLO: works without a project (an open one only pre-fills the class list).</summary>
    [RelayCommand]
    private void ShowConvertYolo()
    {
        Dialogs?.ShowConvertYolo(_segSession?.ProjectRoot, _segSession?.Paths);
    }

    [RelayCommand]
    private void ShowSettings()
    {
        Dialogs?.ShowSettings();
    }

    [RelayCommand]
    private void ShowGuide()
    {
        Dialogs?.ShowGuide();
    }

    private bool NeverAvailable() => false;

    /// <summary>
    /// Command of the menu and toolbar items whose feature arrives in a later release: it can never execute, so the item shows
    /// disabled. It has no behaviour to pretend.
    /// </summary>
    [RelayCommand(CanExecute = nameof(NeverAvailable))]
    private void NotYet()
    {
    }

    [RelayCommand]
    private void ResetLayout()
    {
        IsImageListVisible = true;
        IsLabelsVisible = true;
        LayoutResetRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void Exit()
    {
        ExitRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void ShowAbout()
    {
        AboutRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private async Task ChooseNativeFolderAsync()
    {
        Microsoft.Win32.OpenFolderDialog dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = Strings.ChooseFolderDialogTitle,
            Multiselect = false,
        };

        if (!string.IsNullOrWhiteSpace(_settings.NativeSegAiFolder) && Directory.Exists(_settings.NativeSegAiFolder))
        {
            dialog.InitialDirectory = _settings.NativeSegAiFolder;
        }

        if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.FolderName))
        {
            return;
        }

        _settings.NativeSegAiFolder = dialog.FolderName;
        _store.SaveSettings(_settings);

        if (NativeSegAiLibrary.IsLoaded)
        {
            SegAi.Update(EngineState.RequiresRestart, SegAi.Version, SegAi.Path, dialog.FolderName, string.Empty);
            ShowNotice(Strings.RestartTitle, dialog.FolderName, InfoBarSeverity.Informational);
            return;
        }

        SegAi.Update(EngineState.Loading, string.Empty, string.Empty, string.Empty, string.Empty);
        try
        {
            ApplySegAi(await _engines.LoadSegAiAsync(dialog.FolderName));
        }
        catch (Exception ex)
        {
            _store.Log("SegAI load failed: " + ex);
            SegAi.Update(EngineState.Unavailable, string.Empty, string.Empty, ex.Message, string.Empty);
        }
    }

    /// <summary>Opens a legacy image-folder project.</summary>
    private async Task OpenProjectAsync(string folder)
    {
        await OpenCoreAsync(folder, () => (_projects.Open(folder), null));
    }

    /// <summary>Opens a named project from its <c>.easyai</c> file.</summary>
    private async Task OpenProjectFileAsync(string manifestPath)
    {
        await OpenCoreAsync(manifestPath, () =>
        {
            ProjectOpenResult result = _manager.OpenFile(manifestPath);
            return (result.Project, result);
        });
    }

    /// <summary>
    /// The one open path of every way to get a project: Open Folder, Open Project, Open Recent, New Project, Save Project As and the
    /// command line. <paramref name="open"/> runs on a worker thread and returns the opened project (and the named-project result, or null
    /// for a legacy folder). True when the project is open afterwards.
    /// </summary>
    private async Task<bool> OpenCoreAsync(string folder, Func<(Project Project, ProjectOpenResult? Named)> open)
    {
        if (!ConfirmLeaveImage(true))
        {
            return false;
        }

        IsBusy = true;
        Canvas.InputBlocked = true;
        try
        {
            // Edits of the current project must reach disk before another project (or the same one again) is opened.
            await Canvas.FlushAsync();

            (Project project, ProjectOpenResult? namedResult, List<ImageItemViewModel> items, SegAiSession? session, string? sessionError, Dictionary<string, SegAiSampleInfo> samples) = await Task.Run(() =>
            {
                (Project opened, ProjectOpenResult? openedNamed) = open();
                List<ImageItemViewModel> list = new List<ImageItemViewModel>(opened.Images.Count);
                foreach (ImageItem image in opened.Images)
                {
                    // Off the UI thread, before the item is bound: the OK/NG class is read from its LabelMe JSON.
                    ImageItemViewModel item = new ImageItemViewModel(image, opened.RootDir);
                    item.ImageClass = EasyAI.Desktop.Infrastructure.ImageClassLoader.ReadClass(image.JsonPath);
                    list.Add(item);
                }

                SegAiSession? created = null;
                string? error = null;
                try
                {
                    created = SegAiSession.Create(opened.Paths);
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                }

                return (opened, openedNamed, list, created, error, ReadSamples(created));
            });

            ApplySamples(items, samples);

            // Anything queued while the project was opening must reach the old session before it is replaced and disposed.
            await Canvas.FlushAsync();

            SegAiSession? previous = _segSession;
            _segSession = session;
            Canvas.Kind = project.Kind;
            Canvas.SetSession(session);
            Label.Rebind(session);
            Train.Rebind(session);
            Inspect.Rebind(session);
            previous?.Dispose();
            Project = project;
            ProjectPath = project.RootDir;
            ImageCount = items.Count;
            ClassCount = project.Classes.Count;
            _baseTitle = ProjectTitleRules.BaseTitle(Strings.AppName, project.Manifest?.Name, project.RootDir);
            RefreshTitle();
            IsNoticeOpen = false;
            if (project.Migration is { Needed: true } migration)
            {
                // The old .easylabel/.easyai layout was moved into the single .easyai folder; the old data is kept as a backup.
                _store.Log("Project migration: " + folder + " " + migration.Summary + " " + string.Join(" | ", migration.Errors));
                ShowNotice(
                    Strings.MigrationTitle,
                    (migration.Completed
                        ? migration.Summary
                        : migration.Summary + Environment.NewLine + string.Join(Environment.NewLine, migration.Errors.Take(5)))
                        + Environment.NewLine + Strings.MigrationOneWay,
                    migration.Completed ? InfoBarSeverity.Informational : InfoBarSeverity.Warning);
            }

            if (namedResult != null)
            {
                Recent.Record(namedResult);
                ShowProjectOpenNotice(namedResult);
            }
            else
            {
                Recent.RecordFolder(project.RootDir);
            }

            if (sessionError != null)
            {
                _store.Log("Open SegAI session failed: " + folder + " " + sessionError);
                ShowNotice(Strings.CanvasLoadErrorTitle, sessionError, InfoBarSeverity.Error);
            }

            Images = new ObservableCollection<ImageItemViewModel>(items);
            SelectedImage = items.Count > 0 ? items[0] : null;
            Label.NotifyClassesChanged();
            Train.NotifyClassesChanged();
            Panels.Files.NotifyClassesChanged();
            UpdatePosition();
            Data.Rebuild();
            if (await SynchronizeAnnotationClassesAsync(session))
            {
                ApplySamples(items, await Task.Run(() => ReadSamples(session)));
                Data.Rebuild();
            }
            Recent.Refresh();
            StartCacheTrim();
            return true;
        }
        catch (ProjectException ex)
        {
            _store.Log("Open project failed: " + folder + " " + ex);
            ShowNotice(Strings.ProjectErrorTitle, FormatProjectMessage(ProjectMessages.For(ex)), InfoBarSeverity.Error);
            return false;
        }
        catch (Exception ex)
        {
            _store.Log("Open project failed: " + folder + " " + ex);
            ShowNotice(Strings.ProjectErrorTitle, ex.Message, InfoBarSeverity.Error);
            return false;
        }
        finally
        {
            Canvas.InputBlocked = false;
            IsBusy = false;
        }
    }

    private static string FormatProjectMessage(ProjectMessage message)
    {
        return string.Format(Strings.ByName(message.Key), message.Argument ?? string.Empty);
    }

    /// <summary>
    /// A project that opened but needs a word: its data folder was missing (created empty) or its file could not be updated. The core lists
    /// the "data folder was missing" line first in <c>Warnings</c> (ProjectManager.OpenFile), so it is skipped here because it has its own text.
    /// </summary>
    private void ShowProjectOpenNotice(ProjectOpenResult result)
    {
        List<string> lines = new List<string>();
        if (result.DataFolderRecreated)
        {
            lines.Add(string.Format(Strings.ProjDataRecreatedFormat, result.Paths.MetaDir));
        }

        lines.AddRange(result.Warnings.Skip(result.DataFolderRecreated ? 1 : 0));
        if (lines.Count > 0)
        {
            ShowDataNotice(Strings.ProjNoticeTitle, string.Join(Environment.NewLine, lines), InfoBarSeverity.Warning);
        }
    }

    private async Task LoadPreviewAsync(ImageItemViewModel? item)
    {
        if (CurrentStep == WorkflowStep.Inspect)
        {
            // The Inspect tab owns the canvas; the selected training image is brought back when the Label tab is shown again.
            return;
        }

        _canvasShowsInspect = false;
        try
        {
            if (item == null)
            {
                PreviewImage = null;
                ImageSizeText = string.Empty;
                await Canvas.ShowAsync(null, null, 0, 0);
                return;
            }

            ImagePreviewResult? result = await _loader.LoadAsync(item.FullPath);
            if (result == null || !ReferenceEquals(item, SelectedImage))
            {
                return;
            }

            if (result.Image == null)
            {
                PreviewImage = null;
                ImageSizeText = string.Empty;
                await Canvas.ShowAsync(null, null, 0, 0);
                ShowNotice(Strings.PreviewErrorTitle, item.FileName + ": " + (result.Error ?? string.Empty), InfoBarSeverity.Error, NoticeSource.Preview);
                return;
            }

            if (IsNoticeOpen && _noticeSource == NoticeSource.Preview)
            {
                IsNoticeOpen = false;
            }

            PreviewImage = result.Image;
            ImageSizeText = string.Format(Strings.ImageSizeFormat, result.PixelWidth, result.PixelHeight);
            await Canvas.ShowAsync(item.DisplayName.Replace('\\', '/'), result.Image, result.PixelWidth, result.PixelHeight);
            PrefetchNeighbours(item);
        }
        catch (Exception ex)
        {
            _store.Log("Preview failed: " + ex);
            ShowNotice(Strings.PreviewErrorTitle, ex.Message, InfoBarSeverity.Error, NoticeSource.Preview);
        }
    }

    /// <summary>Shows the selected image again through the normal selection path (new pixels, fresh edit document, no undo history).</summary>
    internal async Task ReloadSelectedImageAsync()
    {
        await SynchronizeAnnotationClassesAsync(_segSession);
        await LoadPreviewAsync(SelectedImage);
    }

    /// <summary>Drops the cached previews of one file whose pixels were replaced.</summary>
    internal void InvalidatePreview(string fullPath)
    {
        _loader.Invalidate(fullPath);
    }

    /// <summary>Warms the preview cache with the next and previous images (same order as NextImage/PreviousImage).</summary>
    private void PrefetchNeighbours(ImageItemViewModel current)
    {
        try
        {
            if (IsBusy || (_segSession != null && _segSession.IsTrainingActive) || !ReferenceEquals(current, SelectedImage) || IsInspectTab)
            {
                return;
            }

            // The list as it is shown: the Up / Down / next / previous commands step through it, whatever the sort toggles say.
            System.Collections.ObjectModel.ObservableCollection<ImageItemViewModel> shown = Panels.Files.Items;
            int index = shown.IndexOf(current);
            if (index < 0)
            {
                return;
            }

            // Nearest first, forward before backward: the worker decodes in this order.
            List<string> paths = new List<string>(ImagePreviewLoader.MaxPrefetchPaths);
            for (int step = 1; step <= ImagePreviewLoader.MaxPrefetchPaths / 2; step++)
            {
                if (index + step < shown.Count)
                {
                    paths.Add(shown[index + step].FullPath);
                }

                if (index - step >= 0)
                {
                    paths.Add(shown[index - step].FullPath);
                }
            }

            PrefetchPreviews(paths);
        }
        catch (Exception ex)
        {
            _store.Log("Preview prefetch skipped: " + ex.Message);
        }
    }

    /// <summary>Decodes the given previews into the cache in the background (Label and Inspect tabs).</summary>
    internal void PrefetchPreviews(IReadOnlyList<string> paths)
    {
        if (paths.Count > 0 && !IsBusy && !(_segSession != null && _segSession.IsTrainingActive))
        {
            _loader.Prefetch(paths);
        }
    }

    /// <summary>Window close: waits (bounded) for queued annotation writes, then releases the project session.</summary>
    public void Shutdown()
    {
        try
        {
            Live.Dispose();
            Capture.Dispose();
            Camera.Dispose();
        }
        catch (Exception ex)
        {
            _store.Log("Camera shutdown failed: " + ex.Message);
        }

        try
        {
            if (!Canvas.Flush(TimeSpan.FromSeconds(15)))
            {
                _store.Log("Annotation save queue did not finish within 15 s.");
                System.Windows.MessageBox.Show(Strings.SaveErrorTitle, Strings.AppName);
            }

            SegAiSession? session = _segSession;
            _segSession = null;
            Canvas.SetSession(null);
            Canvas.Kind = EasyLabel.Core.Projects.ProjectKind.Detect;
            Label.Rebind(null);
            Train.Rebind(null);
            Inspect.Rebind(null);
            session?.Dispose();
        }
        catch (Exception ex)
        {
            _store.Log("Shutdown failed: " + ex);
        }
    }

    private void ApplySegAi(NativeSegAiLoadResult result)
    {
        if (result.IsLoaded)
        {
            SegAi.Update(
                EngineState.Loaded,
                string.Format(Strings.EngineAbiFormat, result.AbiVersion),
                result.LoadedPath ?? string.Empty,
                result.Message,
                result.BuildInfo ?? string.Empty);
            return;
        }

        string probed = string.Join("; ", result.ProbedFolders);
        SegAi.Update(EngineState.Unavailable, string.Empty, string.Empty, result.Message, probed);
        ShowNotice(Strings.SegAi + ": " + Strings.StatusUnavailable, result.Message, InfoBarSeverity.Warning);
    }

    private void ApplyMvs(MvsSdkLoadResult result)
    {
        if (result.IsLoaded)
        {
            Mvs.Update(EngineState.Loaded, result.Version, result.LoadedPath ?? string.Empty, result.Message, result.RawVersionHex);
            return;
        }

        Mvs.Update(EngineState.Unavailable, string.Empty, string.Empty, result.Message, result.RuntimeFolder);
    }

    private void ShowNotice(string title, string message, InfoBarSeverity severity, NoticeSource source = NoticeSource.Other)
    {
        _noticeSource = source;
        NoticeTitle = title;
        NoticeMessage = message;
        NoticeSeverity = severity;
        IsNoticeOpen = true;
    }

    private void UpdatePosition()
    {
        int index = SelectedImage == null ? -1 : Images.IndexOf(SelectedImage);
        ImagePositionText = string.Format(Strings.ImagePositionFormat, index + 1, Images.Count);
        NextImageCommand.NotifyCanExecuteChanged();
        PreviousImageCommand.NotifyCanExecuteChanged();
    }
}
