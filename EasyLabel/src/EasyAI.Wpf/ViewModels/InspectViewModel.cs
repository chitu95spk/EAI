using EasyLabel.SegAI.Native;
using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EasyAI.Desktop.Infrastructure;
using EasyAI.Desktop.Resources;
using EasyLabel.Ai.Onnx.Classify;
using EasyLabel.Core.Classify;
using EasyLabel.Core.Hardware;
using EasyLabel.SegAI;
using EasyLabel.SegAI.AutoLabel;
using EasyLabel.SegAI.Inspection;
using EasyLabel.SegAI.Model;
using InfoBarSeverity = Wpf.Ui.Controls.InfoBarSeverity;
using SymbolRegular = Wpf.Ui.Controls.SymbolRegular;

namespace EasyAI.Desktop.ViewModels;

/// <summary>One entry of the model selector (registry file name plus the text shown).</summary>
public sealed record InspectModelOption(string File, string Text);

/// <summary>One entry of the result filter.</summary>
public sealed record InspectFilterOption(InspectFilter Value, string Text);

/// <summary>One image of the Inspect list with its last result. Nothing here is stored: the list file holds paths only.</summary>
public sealed partial class InspectItemViewModel : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Verdict))]
    [NotifyPropertyChangedFor(nameof(VerdictText))]
    [NotifyPropertyChangedFor(nameof(VerdictSymbol))]
    [NotifyPropertyChangedFor(nameof(RegionsText))]
    [NotifyPropertyChangedFor(nameof(ScoreText))]
    [NotifyPropertyChangedFor(nameof(MsText))]
    [NotifyPropertyChangedFor(nameof(ToolTipText))]
    [NotifyPropertyChangedFor(nameof(ConfidenceText))]
    private InspectResult? _result;

    public InspectItemViewModel(string fullPath)
    {
        FullPath = fullPath;
        FileName = System.IO.Path.GetFileName(fullPath);
    }

    public string FullPath { get; }

    public string FileName { get; }

    public InspectVerdict Verdict => Result?.Verdict ?? InspectVerdict.NotRun;

    /// <summary>Text of the result column; the icon next to it carries the same information, so colour is not the only cue.</summary>
    public string VerdictText
    {
        get
        {
            switch (Verdict)
            {
                case InspectVerdict.OK: return Strings.InspectFilterOk;
                case InspectVerdict.NG: return Strings.InspectFilterNg;
                case InspectVerdict.Error: return Strings.InspectVerdictError;
                default: return Strings.InspectFilterNotRun;
            }
        }
    }

    public SymbolRegular VerdictSymbol
    {
        get
        {
            switch (Verdict)
            {
                case InspectVerdict.OK: return SymbolRegular.CheckmarkCircle24;
                case InspectVerdict.NG: return SymbolRegular.DismissCircle24;
                case InspectVerdict.Error: return SymbolRegular.QuestionCircle24;
                default: return SymbolRegular.Circle24;
            }
        }
    }

    public string RegionsText => Verdict == InspectVerdict.OK || Verdict == InspectVerdict.NG
        ? Result!.Regions.ToString(Culture())
        : "-";

    public string ScoreText => InspectRules.ScoreText(Result, Culture());

    /// <summary>Confidence of an OK/NG model verdict; a dash for segmentation rows.</summary>
    public string ConfidenceText => InspectRules.ConfidenceText(Result, Culture());

    public string MsText => InspectRules.MsText(Result, Culture());

    /// <summary>The error message of a failed image, otherwise the full path.</summary>
    public string ToolTipText => Result?.Error ?? FullPath;

    private static CultureInfo Culture() => Strings.Culture ?? CultureInfo.CurrentUICulture;
}

/// <summary>
/// Inspect tab: an image list of its own (independent of the training files), a model selector, Run current / Run all with progress
/// and cancel, the summary strip, CSV export and the read-only result overlay on the canvas. Results never write labels.
/// The session holds ONE loaded model that Auto-label and Inspect share: choosing a model here loads it without activating it
/// (<see cref="SegAiSession.LoadCandidate"/>), the panel always says which model is loaded, and Auto-label reloads the active model
/// before it runs when the loaded one differs.
/// </summary>
public sealed partial class InspectViewModel : ObservableObject
{
    private const int SummaryRefreshEvery = 25;

    private readonly MainViewModel _host;
    private readonly List<InspectItemViewModel> _all = new List<InspectItemViewModel>();
    private readonly Dictionary<string, InspectItemViewModel> _byPath = new Dictionary<string, InspectItemViewModel>(StringComparer.OrdinalIgnoreCase);
    private InspectService? _service;
    private InspectListStore? _store;
    private CancellationTokenSource? _cts;
    private IReadOnlyList<InspectModelChoice> _choices = new List<InspectModelChoice>();
    private string? _loadedModel;
    private string? _activeModel;
    private string? _newModel;
    private IReadOnlyList<string> _classifyModels = Array.Empty<string>();
    private string? _resultsModel;
    private string? _shownPath;
    private bool _explicitModel;
    private bool _syncingModel;
    private bool _rebuilding;
    private int _showVersion;
    private int _modelsVersion;
    private int _listVersion;
    private int _sinceSummary;
    private int _runTotal;
    private InspectSortKey _sortKey = InspectSortKey.File;
    private bool _sortDescending;

    [ObservableProperty]
    private ObservableCollection<InspectItemViewModel> _items = new ObservableCollection<InspectItemViewModel>();

    [ObservableProperty]
    private ObservableCollection<InspectModelOption> _models = new ObservableCollection<InspectModelOption>();

    [ObservableProperty]
    private InspectItemViewModel? _selectedItem;

    [ObservableProperty]
    private string? _selectedModel;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private InspectFilter _filter = InspectFilter.All;

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private bool _isProgressIndeterminate;

    [ObservableProperty]
    private double _progressValue;

    [ObservableProperty]
    private string _progressText = string.Empty;

    [ObservableProperty]
    private bool _hasSummary;

    [ObservableProperty]
    private string _summaryText = string.Empty;

    [ObservableProperty]
    private string _loadedModelText = Strings.InspectLoadedNone;

    [ObservableProperty]
    private bool _hasNewModel;

    [ObservableProperty]
    private string _newModelText = string.Empty;

    [ObservableProperty]
    private string _settingsText = string.Empty;

    [ObservableProperty]
    private string _runToolTip = Strings.InspectRunAllTip;

    [ObservableProperty]
    private string _countText = string.Empty;

    [ObservableProperty]
    private bool _isListEmpty = true;

    [ObservableProperty]
    private bool _hasResults;

    /// <summary>An OK/NG model was refused (its base model changed): the tab shows the fix button.</summary>
    [ObservableProperty]
    private bool _hasClassifyFix;

    /// <summary>OK rows of the whole list (not the filtered view); refreshed with the summary. Zero until a result exists.</summary>
    public int OkCount { get; private set; }

    /// <summary>NG rows of the whole list (not the filtered view); refreshed with the summary. Zero until a result exists.</summary>
    public int NgCount { get; private set; }

    public InspectViewModel(MainViewModel host)
    {
        _host = host;
        FilterOptions = new List<InspectFilterOption>
        {
            new InspectFilterOption(InspectFilter.All, Strings.InspectFilterAll),
            new InspectFilterOption(InspectFilter.Ok, Strings.InspectFilterOk),
            new InspectFilterOption(InspectFilter.Ng, Strings.InspectFilterNg),
            new InspectFilterOption(InspectFilter.NotRun, Strings.InspectFilterNotRun),
            new InspectFilterOption(InspectFilter.Doubtful, Text("InspectFilterDoubtful", "Review doubtful")),
        };
    }

    // The key is new: until the resource has it, the English text shows (ByName answers the key itself when it is missing).
    private static string Text(string key, string english)
    {
        string text = Strings.ByName(key);
        return string.Equals(text, key, StringComparison.Ordinal) ? english : text;
    }

    public IReadOnlyList<InspectFilterOption> FilterOptions { get; }

    /// <summary>Header of the confidence column (the key is new: English shows until the resource has it).</summary>
    public string ConfidenceHeaderText => Text("InspectColConfidence", "Confidence");

    /// <summary>Caption of the fix button of a refused OK/NG model: the Settings page with "Install model pack...".</summary>
    public string ClassifyFixText => Text("FixInstallModelPack", "Install model pack...");

    /// <summary>Opens the Settings page that installs the model pack (the OK/NG base model comes from it).</summary>
    [RelayCommand]
    private void FixClassify()
    {
        _host.Dialogs?.ShowSettingsPage(SettingsCatalog.PageAiModels);
    }

    /// <summary>The model selector can be used (a project is open and nothing else runs).</summary>
    public bool CanChooseModel => _host.HasSession && !_host.IsBusy && Models.Count > 0;

    // ---------- session / state ----------

    /// <summary>Binds the project session (null = none): new service and cache, the stored list, the model list.</summary>
    internal void Rebind(SegAiSession? session)
    {
        _cts?.Cancel();
        _showVersion++;
        _modelsVersion++;
        _listVersion++;
        _service?.Clear();
        _service = session == null ? null : new InspectService(session);
        _store = session == null ? null : new InspectListStore(session.Paths);
        _all.Clear();
        _byPath.Clear();
        _resultsModel = null;
        _loadedModel = null;
        _activeModel = null;
        _newModel = null;
        _classifyModels = Array.Empty<string>();
        _shownPath = null;
        _explicitModel = false;
        SelectedItem = null;
        SetChoices(new List<InspectModelChoice>(), null);
        ApplyView();
        RefreshSummary();
        RefreshSettingsText(session);
        RefreshState();
        if (session != null)
        {
            _ = LoadListAsync(session, _store!);
            _ = RefreshModelsAsync(session, null);
        }

        // The canvas may still hold an image of the previous project's list.
        _ = ShowSelectedAsync();
    }

    partial void OnIsListEmptyChanged(bool value)
    {
        _host.NotifyInspectStartChanged();
    }

    /// <summary>Busy flag, session or selection changed: re-evaluates the commands and the block reason.</summary>
    internal void NotifyStateChanged()
    {
        RefreshState();
    }

    private void RefreshState()
    {
        InspectBlock block = CurrentBlock();
        string? key = InspectRules.BlockKey(block);
        RunToolTip = key == null || IsRunning ? Strings.InspectRunAllTip : Strings.ByName(key);
        OnPropertyChanged(nameof(CanChooseModel));
        RunCurrentCommand.NotifyCanExecuteChanged();
        RunAllCommand.NotifyCanExecuteChanged();
        CancelRunCommand.NotifyCanExecuteChanged();
        AddFolderCommand.NotifyCanExecuteChanged();
        AddImagesCommand.NotifyCanExecuteChanged();
        RemoveSelectedCommand.NotifyCanExecuteChanged();
        ExportCsvCommand.NotifyCanExecuteChanged();
        UseNewModelCommand.NotifyCanExecuteChanged();
    }

    private InspectBlock CurrentBlock()
    {
        SegAiSession? session = _host.Session;
        return InspectRules.BlockOf(
            session != null,
            _host.IsBusy && !IsRunning,
            session?.IsTrainingActive ?? false,
            SelectedModel != null,
            _all.Count);
    }

    /// <summary>The current step changed. Entering the tab shows the selected image and re-reads the models; leaving it stops showing.</summary>
    internal void OnStepChanged(WorkflowStep oldStep, WorkflowStep newStep)
    {
        if (newStep == WorkflowStep.Inspect)
        {
            SegAiSession? session = _host.Session;
            RefreshSettingsText(session);
            if (session != null)
            {
                _ = RefreshModelsAsync(session, null);
            }

            _ = ShowSelectedAsync();
            RefreshState();
        }
        else if (oldStep == WorkflowStep.Inspect)
        {
            _showVersion++;
            _shownPath = null;
        }
    }

    private void RefreshSettingsText(SegAiSession? session)
    {
        if (session == null)
        {
            SettingsText = string.Empty;
            return;
        }

        CultureInfo culture = Strings.Culture ?? CultureInfo.CurrentUICulture;
        SettingsText = string.Format(
            Strings.InspectSettingsFormat,
            session.Inference.DefectThreshold.ToString("0.00", culture),
            session.Inference.MinDefectArea);
    }

    // ---------- list ----------

    private async Task LoadListAsync(SegAiSession session, InspectListStore store)
    {
        int version = _listVersion;
        IReadOnlyList<string> paths;
        try
        {
            paths = await Task.Run(() => store.Load());
        }
        catch (Exception ex) when (ex is InvalidDataException || ex is IOException || ex is UnauthorizedAccessException)
        {
            _host.LogData("Read inspect list failed: " + ex.Message);
            return;
        }

        if (version != _listVersion || !ReferenceEquals(session, _host.Session))
        {
            return;
        }

        AddFullPaths(paths, false);
    }

    /// <summary>Adds image paths to the list (UI thread). Duplicates are ignored. Returns the number added.</summary>
    private int AddFullPaths(IReadOnlyList<string> paths, bool persist)
    {
        int before = _all.Count;
        IReadOnlyList<string> merged = InspectListStore.Merge(_all.Select(i => i.FullPath), paths);
        foreach (string path in merged)
        {
            if (_byPath.ContainsKey(path))
            {
                continue;
            }

            InspectItemViewModel item = new InspectItemViewModel(path);
            _all.Add(item);
            _byPath[path] = item;
        }

        int added = _all.Count - before;
        if (added > 0)
        {
            ApplyView();
            RefreshSummary();
            RefreshState();
            if (persist)
            {
                Persist();
            }
        }

        return added;
    }

    private void Persist()
    {
        InspectListStore? store = _store;
        if (store == null)
        {
            return;
        }

        try
        {
            store.Save(_all.Select(i => i.FullPath));
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            _host.LogData("Write inspect list failed: " + ex.Message);
            _host.ShowDataNotice(Strings.InspectErrorTitle, ex.Message, InfoBarSeverity.Warning);
        }
    }

    private bool CanEditList() => _host.HasSession && !_host.IsBusy;

    [RelayCommand(CanExecute = nameof(CanEditList))]
    private async Task AddFolderAsync()
    {
        Microsoft.Win32.OpenFolderDialog dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = Strings.InspectAddFolderDialogTitle,
            Multiselect = false,
        };

        if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.FolderName))
        {
            return;
        }

        await AddPathsAsync(new[] { dialog.FolderName });
    }

    [RelayCommand(CanExecute = nameof(CanEditList))]
    private async Task AddImagesAsync()
    {
        Microsoft.Win32.OpenFileDialog dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = Strings.AddImagesDialogTitle,
            Filter = Strings.AddImagesFilter,
            CheckFileExists = true,
            Multiselect = true,
        };

        if (dialog.ShowDialog() != true || dialog.FileNames.Length == 0)
        {
            return;
        }

        await AddPathsAsync(dialog.FileNames);
    }

    /// <summary>Adds dropped or chosen files and folders (folders are scanned, sub folders included).</summary>
    internal async Task AddPathsAsync(IReadOnlyList<string> paths)
    {
        if (!CanEditList() || paths.Count == 0)
        {
            return;
        }

        int version = _listVersion;
        List<string> expanded = await Task.Run(() =>
        {
            List<string> found = new List<string>();
            foreach (string path in paths)
            {
                if (Directory.Exists(path))
                {
                    found.AddRange(InspectListStore.ScanFolder(path));
                }
                else if (File.Exists(path))
                {
                    found.Add(path);
                }
            }

            return found;
        });

        if (version != _listVersion)
        {
            return;
        }

        int added = AddFullPaths(expanded, true);
        _host.StatusMessage = string.Format(Strings.InspectAddedFormat, added);
    }

    /// <summary>The Capture buttons (shared with the Label tab).</summary>
    public CaptureViewModel Capture => _host.Capture;

    /// <summary>A captured frame was saved into the project: puts it on the list (persisted) and selects it. Filter and search are cleared when they hide it.</summary>
    internal void AddCaptured(string fullPath)
    {
        string full = System.IO.Path.GetFullPath(fullPath);
        AddFullPaths(new[] { full }, true);
        if (!_byPath.TryGetValue(full, out InspectItemViewModel? item))
        {
            return;
        }

        if (!Items.Contains(item))
        {
            SearchText = string.Empty;
            Filter = InspectFilter.All;
        }

        SelectedItem = item;
        _host.StatusMessage = string.Format(Strings.InspectAddedFormat, 1);
    }

    /// <summary>The pixels of an image were replaced (Capture to current): its cached result and the row result are stale.</summary>
    internal void InvalidateImage(string fullPath)
    {
        string full = System.IO.Path.GetFullPath(fullPath);
        _service?.Invalidate(full);
        if (_byPath.TryGetValue(full, out InspectItemViewModel? item) && item.Result != null)
        {
            item.Result = null;
            ApplyView();
            RefreshSummary();
        }
    }

    private bool CanRemove() => CanEditList() && SelectedItem != null && !IsRunning;

    [RelayCommand(CanExecute = nameof(CanRemove))]
    private void RemoveSelected()
    {
        InspectItemViewModel? item = SelectedItem;
        if (item == null)
        {
            return;
        }

        int index = Items.IndexOf(item);
        _all.Remove(item);
        _byPath.Remove(item.FullPath);
        SelectedItem = null;
        ApplyView();
        if (Items.Count > 0)
        {
            SelectedItem = Items[Math.Min(Math.Max(index, 0), Items.Count - 1)];
        }

        RefreshSummary();
        RefreshState();
        Persist();
    }

    // ---------- filter, search, sort ----------

    partial void OnSearchTextChanged(string value)
    {
        ApplyView();
    }

    partial void OnFilterChanged(InspectFilter value)
    {
        ApplyView();
    }

    /// <summary>Sorts by a column; the same column again flips the direction.</summary>
    internal void SortBy(InspectSortKey key)
    {
        if (_sortKey == key)
        {
            _sortDescending = !_sortDescending;
        }
        else
        {
            _sortKey = key;
            _sortDescending = false;
        }

        ApplyView();
    }

    /// <summary>Rebuilds the visible list (filter, search, sort) and keeps the selection while the row is still visible.</summary>
    private void ApplyView()
    {
        InspectItemViewModel? keep = SelectedItem;
        List<InspectItemViewModel> view = _all
            .Where(i => InspectRules.MatchesReview(Filter, i.Verdict, i.Result?.Uncertainty) && InspectRules.MatchesSearch(i.FullPath, SearchText))
            .ToList();
        InspectSortKey key = _sortKey;
        bool descending = _sortDescending;
        view.Sort((a, b) => InspectRules.Compare(key, descending, a.FullPath, a.Result, b.FullPath, b.Result));
        bool contains = keep != null && view.Contains(keep);

        _rebuilding = true;
        try
        {
            Items = new ObservableCollection<InspectItemViewModel>(view);
            // The list control clears its selection when the source is replaced; put the same row back without reloading the image.
            SelectedItem = contains ? keep : null;
        }
        finally
        {
            _rebuilding = false;
        }

        IsListEmpty = _all.Count == 0;
        CountText = view.Count == _all.Count
            ? _all.Count.ToString(CultureInfo.CurrentCulture)
            : view.Count.ToString(CultureInfo.CurrentCulture) + " / " + _all.Count.ToString(CultureInfo.CurrentCulture);
        if (keep != null && !contains)
        {
            _ = ShowSelectedAsync();
        }

        RefreshState();
    }

    partial void OnSelectedItemChanged(InspectItemViewModel? value)
    {
        if (_rebuilding)
        {
            return;
        }

        RefreshState();
        _ = ShowSelectedAsync();
    }

    // ---------- canvas ----------

    /// <summary>Shows the selected image on the canvas (no document, nothing editable) and its overlay.</summary>
    internal async Task ShowSelectedAsync()
    {
        if (_host.CurrentStep != WorkflowStep.Inspect)
        {
            return;
        }

        int version = ++_showVersion;
        InspectItemViewModel? item = SelectedItem;
        _shownPath = null;
        bool shown = await _host.ShowInspectImageAsync(item?.FullPath, () => version == _showVersion);
        if (shown && item != null && version == _showVersion)
        {
            _shownPath = item.FullPath;
            ApplyOverlay();
            PrefetchNeighbours(item);
        }
    }

    /// <summary>Warms the preview cache with the images around the shown one (list order), so stepping through the list does not wait for a decode.</summary>
    private void PrefetchNeighbours(InspectItemViewModel current)
    {
        int index = Items.IndexOf(current);
        if (index < 0)
        {
            return;
        }

        List<string> paths = new List<string>(Services.ImagePreviewLoader.MaxPrefetchPaths);
        for (int step = 1; step <= Services.ImagePreviewLoader.MaxPrefetchPaths / 2; step++)
        {
            if (index + step < Items.Count)
            {
                paths.Add(Items[index + step].FullPath);
            }

            if (index - step >= 0)
            {
                paths.Add(Items[index - step].FullPath);
            }
        }

        _host.PrefetchPreviews(paths);
    }

    private void ApplyOverlay()
    {
        InspectItemViewModel? item = SelectedItem;
        if (_host.CurrentStep != WorkflowStep.Inspect || item == null || !string.Equals(_shownPath, item.FullPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _host.Canvas.SetSuggestions(item.Result?.Suggestions ?? Array.Empty<SegAiSuggestion>());
    }

    // ---------- model ----------

    private static string? LoadedFileOf(SegAiSession session)
    {
        SegAiModelInfo? loaded = session.LoadedModel;
        return loaded == null ? null : System.IO.Path.GetFileName(loaded.Path);
    }

    private async Task RefreshModelsAsync(SegAiSession session, string? newCandidate)
    {
        int version = ++_modelsVersion;
        IReadOnlyList<SegAiCandidateInfo> candidates;
        string? loaded;
        IReadOnlyList<string> classify;
        try
        {
            (candidates, loaded, classify) = await Task.Run(() => (session.ReadCandidates(), LoadedFileOf(session), ReadClassifyModels(session)));
        }
        catch (Exception ex)
        {
            _host.LogData("Read inspect models failed: " + ex.Message);
            return;
        }

        if (version != _modelsVersion || !ReferenceEquals(session, _host.Session))
        {
            return;
        }

        _classifyModels = classify;
        _loadedModel = loaded;
        IReadOnlyList<InspectModelChoice> choices = InspectRules.BuildChoices(candidates);
        _activeModel = choices.FirstOrDefault(c => c.IsActive)?.File;
        SetChoices(choices, newCandidate);
        RefreshState();
    }

    /// <summary>Step 3 trained or imported a candidate, or a model became active: re-read the models; a new candidate is offered, never selected.</summary>
    internal async Task OnModelsChangedAsync(string? newCandidate)
    {
        SegAiSession? session = _host.Session;
        if (session != null)
        {
            await RefreshModelsAsync(session, newCandidate);
        }
    }

    /// <summary>The latest OK/NG model files of the project's classify folder (read on a worker; empty when there are none).</summary>
    private static IReadOnlyList<string> ReadClassifyModels(SegAiSession session)
    {
        string dir = session.Paths.ClassifyDir;
        if (!Directory.Exists(dir))
        {
            return Array.Empty<string>();
        }

        return OkNgModelRules.LatestPerName(Directory.EnumerateFiles(dir).Select(f => Path.GetFileName(f)).ToList());
    }

    private bool IsClassifySelection(string? file)
    {
        return file != null && _classifyModels.Any(f => string.Equals(f, file, StringComparison.OrdinalIgnoreCase));
    }

    private void SetChoices(IReadOnlyList<InspectModelChoice> choices, string? newCandidate)
    {
        _choices = choices;
        CultureInfo culture = Strings.Culture ?? CultureInfo.CurrentUICulture;
        ObservableCollection<InspectModelOption> options = new ObservableCollection<InspectModelOption>(
            choices.Select(c => new InspectModelOption(c.File, InspectRules.ChoiceText(c, Strings.InspectActiveSuffix, culture))));
        // OK/NG models come after the segmentation models; they are never loaded into the session.
        string classifyLabel = Text("KindClassify", "OK/NG");
        foreach (string file in _classifyModels)
        {
            options.Add(new InspectModelOption(file, file + " · " + classifyLabel));
        }

        _syncingModel = true;
        try
        {
            Models = options;
            // Until the user picks a model the selection follows the active model; a kept OK/NG selection stays.
            string? keep = _explicitModel ? SelectedModel : null;
            SelectedModel = IsClassifySelection(keep) ? keep : InspectRules.ResolveSelection(choices, keep);
        }
        finally
        {
            _syncingModel = false;
        }

        _newModel = InspectRules.OfferNewModel(choices, newCandidate ?? _newModel, SelectedModel);
        UpdateModelTexts();
    }

    private void UpdateModelTexts()
    {
        HasNewModel = _newModel != null;
        NewModelText = _newModel == null ? string.Empty : string.Format(Strings.InspectNewModelFormat, _newModel);
        if (_loadedModel == null)
        {
            LoadedModelText = Strings.InspectLoadedNone;
        }
        else if (_activeModel == null)
        {
            LoadedModelText = string.Format(Strings.InspectLoadedNoActiveFormat, _loadedModel);
        }
        else if (string.Equals(_loadedModel, _activeModel, StringComparison.OrdinalIgnoreCase))
        {
            LoadedModelText = string.Format(Strings.InspectLoadedActiveFormat, _loadedModel);
        }
        else
        {
            LoadedModelText = string.Format(Strings.InspectLoadedOtherFormat, _loadedModel, _activeModel);
        }

        OnPropertyChanged(nameof(CanChooseModel));
        UseNewModelCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedModelChanged(string? value)
    {
        if (_syncingModel)
        {
            return;
        }

        _explicitModel = true;
        _newModel = InspectRules.OfferNewModel(_choices, _newModel, value);
        UpdateModelTexts();
        RefreshState();
        if (value != null)
        {
            _ = LoadSelectedAsync(value);
        }
    }

    /// <summary>Loads the chosen model into the session without activating it; on failure the selection returns to the loaded model.</summary>
    private async Task LoadSelectedAsync(string file)
    {
        // An OK/NG model is read from its own file at run time; the session keeps its segmentation model.
        if (IsClassifySelection(file))
        {
            _newModel = InspectRules.OfferNewModel(_choices, _newModel, file);
            UpdateModelTexts();
            RefreshState();
            return;
        }

        if (string.Equals(file, _loadedModel, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        bool ok = await _host.RunLabelAsync(session => Task.Run(() => session.LoadCandidate(file)), Strings.ModelErrorTitle);
        if (ok)
        {
            _loadedModel = file;
            ClearStaleResults(file);
        }
        else
        {
            _syncingModel = true;
            try
            {
                SelectedModel = _loadedModel != null && _choices.Any(c => string.Equals(c.File, _loadedModel, StringComparison.OrdinalIgnoreCase))
                    ? _loadedModel
                    : InspectRules.ResolveSelection(_choices, null);
            }
            finally
            {
                _syncingModel = false;
            }
        }

        _newModel = InspectRules.OfferNewModel(_choices, _newModel, SelectedModel);
        UpdateModelTexts();
        RefreshState();
    }

    private bool CanUseNewModel() => _newModel != null && CanChooseModel;

    /// <summary>"Inspect with the new model": selects (and so loads) the model that was just trained. The active model is not touched.</summary>
    [RelayCommand(CanExecute = nameof(CanUseNewModel))]
    private void UseNewModel()
    {
        string? file = _newModel;
        if (file != null)
        {
            SelectedModel = file;
        }
    }

    /// <summary>Results belong to the model that produced them: rows of another model go back to "not run".</summary>
    private void ClearStaleResults(string? model)
    {
        bool changed = false;
        foreach (InspectItemViewModel item in _all)
        {
            if (item.Result != null && !string.Equals(item.Result.Model, model, StringComparison.OrdinalIgnoreCase))
            {
                item.Result = null;
                changed = true;
            }
        }

        if (changed)
        {
            ApplyView();
            ApplyOverlay();
            RefreshSummary();
        }
    }

    // ---------- run ----------

    private bool CanRunCurrent() => !IsRunning && CurrentBlock() == InspectBlock.None && SelectedItem != null;

    private bool CanRunAll() => !IsRunning && CurrentBlock() == InspectBlock.None;

    [RelayCommand(CanExecute = nameof(CanRunCurrent))]
    private async Task RunCurrentAsync()
    {
        InspectItemViewModel? item = SelectedItem;
        if (item != null)
        {
            await RunAsync(new List<InspectItemViewModel> { item }, true);
        }
    }

    [RelayCommand(CanExecute = nameof(CanRunAll))]
    private async Task RunAllAsync()
    {
        await RunAsync(_all.ToList(), false);
    }

    // A single-image run is indeterminate and cannot be interrupted once the native call started, so only list runs offer Cancel.
    private bool CanCancel() => IsRunning && !IsProgressIndeterminate;

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void CancelRun()
    {
        _cts?.Cancel();
    }

    private async Task RunAsync(List<InspectItemViewModel> items, bool single)
    {
        InspectService? service = _service;
        string? model = SelectedModel;
        if (service == null || model == null || items.Count == 0)
        {
            return;
        }

        HasClassifyFix = false;
        if (IsClassifySelection(model))
        {
            await RunClassifyAsync(items, model);
            return;
        }

        string[] paths = items.Select(i => i.FullPath).ToArray();
        CancellationTokenSource cts = BeginRun(single, paths.Length);
        IReadOnlyList<InspectResult>? results = null;
        string? loadedNow = null;
        try
        {
            CancellationToken token = cts.Token;
            Progress<InspectProgress> progress = new Progress<InspectProgress>(OnProgress);
            await _host.RunLabelAsync(
                async session =>
                {
                    // The session holds one model; Auto-label or another choice may have replaced it since the last run.
                    await Task.Run(
                        () =>
                        {
                            if (!string.Equals(LoadedFileOf(session), model, StringComparison.OrdinalIgnoreCase))
                            {
                                session.LoadCandidate(model);
                            }

                            loadedNow = model;
                        },
                        token);
                    results = await service.RunAllAsync(paths, progress, token);
                },
                Strings.InspectErrorTitle);
        }
        finally
        {
            EndRun(cts);
        }

        if (loadedNow != null)
        {
            _loadedModel = loadedNow;
        }

        // Cancelled or finished: whatever was computed is already on its row (progress); the full list closes any gap.
        if (results != null)
        {
            foreach (InspectResult result in results)
            {
                if (_byPath.TryGetValue(result.Path, out InspectItemViewModel? row))
                {
                    row.Result = result;
                }
            }
        }

        // A failed run (for example the model could not be loaded) must not wipe the results of the previous model.
        if (results != null)
        {
            _resultsModel = model;
            ClearStaleResults(model);
        }

        ApplyView();
        ApplyOverlay();
        RefreshSummary();
        UpdateModelTexts();
        RefreshState();

        if (results != null)
        {
            List<InspectResult> failed = results.Where(r => r.Verdict == InspectVerdict.Error).ToList();
            if (failed.Count > 0)
            {
                _host.ShowDataNotice(
                    Strings.InspectErrorTitle,
                    string.Format(Strings.InspectFailedFormat, failed.Count, failed[0].Error),
                    InfoBarSeverity.Warning);
            }
        }
    }

    /// <summary>
    /// Runs an OK/NG model over the rows: one predict call on a worker, progress per image, Cancel as for a list run. Rows get the verdict
    /// and the classifier confidence; no image or label is written. A refused model (base model changed) shows the fix button.
    /// </summary>
    private async Task RunClassifyAsync(List<InspectItemViewModel> items, string model)
    {
        SegAiSession? session = _host.Session;
        if (session == null || items.Count == 0)
        {
            return;
        }

        string[] paths = items.Select(i => i.FullPath).ToArray();
        CancellationTokenSource cts = BeginRun(false, paths.Length);
        IReadOnlyList<InspectResult>? results = null;
        string? refusal = null;
        bool baseChanged = false;
        try
        {
            CancellationToken token = cts.Token;
            Progress<ClassifyProgress> progress = new Progress<ClassifyProgress>(OnClassifyProgress);
            await _host.RunLabelAsync(
                async s =>
                {
                    try
                    {
                        var watch = System.Diagnostics.Stopwatch.StartNew();
                        ClassifyPredictResult predicted = await PredictClassifyAsync(s, paths, model, progress, token);
                        watch.Stop();
                        if (predicted.Status != ClassifyModelStatus.Ok)
                        {
                            baseChanged = predicted.Status == ClassifyModelStatus.BackboneMismatch;
                            refusal = predicted.Message;
                            return;
                        }

                        double each = predicted.Predictions.Count == 0 ? 0 : watch.Elapsed.TotalMilliseconds / predicted.Predictions.Count;
                        DateTime now = DateTime.UtcNow;
                        results = predicted.Predictions.Select(p => OkNgModelRules.ResultOf(p.RelativePath, p.PNg, model, each, now)).ToList();
                    }
                    catch (OperationCanceledException)
                    {
                        // Cancelled: no row is changed.
                    }
                },
                Strings.InspectErrorTitle);
        }
        finally
        {
            EndRun(cts);
        }

        if (refusal != null)
        {
            HasClassifyFix = baseChanged;
            string message = baseChanged
                ? Text("ClassifyBackboneMismatch", "The OK/NG base model changed since this model was trained. Install the model pack or retrain.")
                : refusal;
            _host.ShowDataNotice(Strings.InspectErrorTitle, message, InfoBarSeverity.Warning);
        }

        if (results != null)
        {
            foreach (InspectResult result in results)
            {
                if (_byPath.TryGetValue(result.Path, out InspectItemViewModel? row))
                {
                    row.Result = result;
                }
            }

            _resultsModel = model;
            ClearStaleResults(model);
        }

        ApplyView();
        ApplyOverlay();
        RefreshSummary();
        UpdateModelTexts();
        RefreshState();
    }

    private async Task<ClassifyPredictResult> PredictClassifyAsync(SegAiSession session, string[] paths, string model, IProgress<ClassifyProgress> progress, CancellationToken token)
    {
        string classifyDir = session.Paths.ClassifyDir;
        // Threads and priority come from the hardware recommendation for the classifier (the probe may take a moment: a worker).
        int threads = await Task.Run(() => RecommendedSettings.For(new HardwareProbe().Probe(), HardwareTask.ClassifyTrain).Threads, token);
        var request = new ClassifyPredictRequest(
            Path.Combine(classifyDir, model),
            paths.Select(p => new ClassifyImageRef(p, p)).ToList(),
            _host.ModelsRoot,
            NativeSegAiLibrary.LoadedFolder,
            Math.Max(1, threads),
            false,
            Path.Combine(classifyDir, "cache"));
        return await ClassifyService.PredictAsync(request, progress, token);
    }

    private void OnClassifyProgress(ClassifyProgress progress)
    {
        if (!IsRunning)
        {
            return;
        }

        ProgressValue = progress.Total <= 0 ? 0 : 100.0 * progress.Done / progress.Total;
        ProgressText = string.Format(Strings.ProgressFormat, progress.Done, progress.Total);
    }

    private CancellationTokenSource BeginRun(bool single, int total)
    {
        CancellationTokenSource cts = new CancellationTokenSource();
        _cts = cts;
        _runTotal = total;
        _sinceSummary = 0;
        IsProgressIndeterminate = single;
        ProgressValue = 0;
        ProgressText = single ? string.Empty : string.Format(Strings.ProgressFormat, 0, total);
        IsRunning = true;
        RefreshState();
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
        RefreshState();
    }

    private void OnProgress(InspectProgress progress)
    {
        if (!IsRunning)
        {
            return;
        }

        if (_byPath.TryGetValue(progress.Path, out InspectItemViewModel? row))
        {
            row.Result = progress.Result;
            if (ReferenceEquals(row, SelectedItem))
            {
                ApplyOverlay();
            }
        }

        ProgressValue = _runTotal <= 0 ? 0 : 100.0 * progress.Completed / _runTotal;
        ProgressText = string.Format(Strings.ProgressFormat, progress.Completed, _runTotal);
        if (++_sinceSummary >= SummaryRefreshEvery)
        {
            _sinceSummary = 0;
            _resultsModel = progress.Result.Model.Length == 0 ? _resultsModel : progress.Result.Model;
            RefreshSummary();
        }
    }

    // ---------- summary, export ----------

    private void RefreshSummary()
    {
        List<InspectResult> results = _all.Where(i => i.Result != null).Select(i => i.Result!).ToList();
        InspectSummary summary = InspectService.Summarize(results);
        CultureInfo culture = Strings.Culture ?? CultureInfo.CurrentUICulture;
        if (OkCount != summary.Ok)
        {
            OkCount = summary.Ok;
            OnPropertyChanged(nameof(OkCount));
        }

        if (NgCount != summary.Ng)
        {
            NgCount = summary.Ng;
            OnPropertyChanged(nameof(NgCount));
        }

        HasSummary = summary.Run > 0;
        HasResults = HasSummary;
        SummaryText = HasSummary
            ? string.Format(Strings.InspectSummaryFormat, _resultsModel ?? "-", summary.Run, summary.Ok, summary.Ng, summary.Errors, InspectRules.MeanMsText(summary, culture))
            : string.Empty;
        ExportCsvCommand.NotifyCanExecuteChanged();
    }

    private bool CanExport() => HasResults && !IsRunning;

    [RelayCommand(CanExecute = nameof(CanExport))]
    private void ExportCsv()
    {
        List<InspectResult> rows = _all.Where(i => i.Result != null && i.Result.Verdict != InspectVerdict.NotRun).Select(i => i.Result!).ToList();
        if (rows.Count == 0)
        {
            return;
        }

        Microsoft.Win32.SaveFileDialog dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = Strings.InspectCsvDialogTitle,
            Filter = Strings.InspectCsvFilter,
            FileName = "inspect-results.csv",
            DefaultExt = ".csv",
            AddExtension = true,
            OverwritePrompt = true,
        };

        if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.FileName))
        {
            return;
        }

        try
        {
            InspectCsv.Write(dialog.FileName, rows);
            _host.StatusMessage = string.Format(Strings.InspectCsvDoneFormat, rows.Count, dialog.FileName);
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            _host.LogData("Export inspect CSV failed: " + ex.Message);
            _host.ShowDataNotice(Strings.InspectErrorTitle, ex.Message, InfoBarSeverity.Error);
        }
    }
}
