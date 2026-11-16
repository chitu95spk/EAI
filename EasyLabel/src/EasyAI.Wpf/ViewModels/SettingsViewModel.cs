using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EasyAI.Desktop.Infrastructure;
using EasyAI.Desktop.Resources;
using EasyAI.Desktop.Services;
using InfoBarSeverity = Wpf.Ui.Controls.InfoBarSeverity;

namespace EasyAI.Desktop.ViewModels;

/// <summary>One row of the Settings window. Enabled rows write through to <see cref="MainViewModel"/>; every other row is read-only.</summary>
public sealed class SettingRowViewModel : ObservableObject
{
    private readonly SettingRowModel _model;
    private readonly MainViewModel _main;
    private bool _showHeader;
    private string _headerText = string.Empty;
    private bool _showPageHeader;
    private string _pageHeaderText = string.Empty;

    public SettingRowViewModel(SettingRowModel model, MainViewModel main)
    {
        _model = model;
        _main = main;
        ChooseColorCommand = new RelayCommand(ChooseColor);
        ResetPaletteCommand = new RelayCommand(_main.ResetPalette);
        CameraActionCommand = new RelayCommand(RunCameraAction, CanRunCameraAction);
    }

    private IReadOnlyList<SettingOption>? _dynamicOptions;

    /// <summary>The button of a Camera page row: Refresh, Connect or Disconnect (by row id).</summary>
    public RelayCommand CameraActionCommand { get; }

    public bool IsCameraButtonRow => _model.Kind == SettingKind.Button && _model.IsEnabled && SettingsAccessor.IsCameraButtonRow(_model.Id);

    /// <summary>False only for the Error row of the Camera page while it has no text (the row collapses).</summary>
    public bool IsRowVisible => _model.Id != SettingsAccessor.RowCameraError || !string.IsNullOrEmpty(_model.ValueText);

    public string CameraButtonText
    {
        get
        {
            switch (_model.Id)
            {
                case SettingsAccessor.RowCameraRefresh: return Strings.SetCameraRefreshButton;
                case SettingsAccessor.RowCameraConnect: return Strings.SetCameraConnectButton;
                case SettingsAccessor.RowEasyVisionFolderBrowse: return SettingsCatalog.Localized(Strings.ByName, "SetEasyVisionFolderBrowseButton", "Browse...");
                default: return Strings.SetCameraDisconnectButton;
            }
        }
    }

    /// <summary>Re-evaluates whether the Camera page button can run (the state, the source or the chosen device changed).</summary>
    public void RaiseCameraCanExecuteChanged()
    {
        CameraActionCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Replaces the choices of the device row (the enumerated cameras) and re-reads the selection.</summary>
    public void SetOptions(IReadOnlyList<SettingOption> options)
    {
        _dynamicOptions = options;
        OnPropertyChanged(nameof(Options));
        OnPropertyChanged(nameof(SelectedOption));
    }

    /// <summary>Raised after a button of this row changed a setting that another row shows (the EasyVision folder Browse button).</summary>
    public event EventHandler? SettingChangedByButton;

    private void BrowseEasyVisionFolder()
    {
        string? current = _main.Settings.EasyVisionModelFolder;
        string? initial = !string.IsNullOrWhiteSpace(current) && System.IO.Directory.Exists(current) ? current : null;
        string? folder = _main.Dialogs?.PickFolder(SettingsCatalog.Localized(Strings.ByName, "SetEasyVisionFolderPickTitle", "Choose the EasyVision model folder"), initial);
        if (string.IsNullOrWhiteSpace(folder))
        {
            return;
        }

        _main.Settings.EasyVisionModelFolder = folder;
        _main.NotifySettingChanged();
        SettingChangedByButton?.Invoke(this, EventArgs.Empty);
    }

    private bool CanRunCameraAction()
    {
        EasyLabel.Camera.CameraService camera = _main.Camera;
        switch (_model.Id)
        {
            case SettingsAccessor.RowEasyVisionFolderBrowse:
                return true;
            case SettingsAccessor.RowCameraRefresh:
                return true;
            case SettingsAccessor.RowCameraConnect:
                string? id = _main.Settings.LastCameraId;
                return _main.IsCameraSourceActive
                    && !string.IsNullOrEmpty(id)
                    && camera.Devices.Any(d => string.Equals(d.Id, id, StringComparison.Ordinal))
                    && camera.State != EasyLabel.Camera.CameraServiceState.Connecting
                    && !(camera.State == EasyLabel.Camera.CameraServiceState.Connected && string.Equals(camera.DeviceId, id, StringComparison.Ordinal));
            case SettingsAccessor.RowCameraDisconnect:
                return camera.State != EasyLabel.Camera.CameraServiceState.Disconnected;
            default:
                return false;
        }
    }

    private void RunCameraAction()
    {
        switch (_model.Id)
        {
            case SettingsAccessor.RowEasyVisionFolderBrowse:
                BrowseEasyVisionFolder();
                break;
            case SettingsAccessor.RowCameraRefresh:
                _ = _main.Camera.EnumerateAsync();
                break;
            case SettingsAccessor.RowCameraConnect:
                string? id = _main.Settings.LastCameraId;
                if (!string.IsNullOrEmpty(id))
                {
                    _ = ConnectAsync(id);
                }

                break;
            case SettingsAccessor.RowCameraDisconnect:
                _main.Camera.Disconnect();
                break;
        }
    }

    private async Task ConnectAsync(string id)
    {
        try
        {
            await _main.Camera.ConnectAsync(id);
        }
        catch (Exception)
        {
            // The service reports a failed connect through its state; only a disposed service throws here.
        }
    }

    /// <summary>Opens the colour dialog for a colour row; a chosen colour is stored and applied at once.</summary>
    public System.Windows.Input.ICommand ChooseColorCommand { get; }

    /// <summary>The "Reset to logo" button: the four palette colours return to the logo defaults.</summary>
    public System.Windows.Input.ICommand ResetPaletteCommand { get; }

    /// <summary>Text of the reset button (the row caption explains it).</summary>
    public string ResetButtonText => Strings.SetThemeReset;

    public bool IsColorRow => _model.Kind == SettingKind.Color && _model.IsEnabled;

    public bool IsButtonRow => _model.Kind == SettingKind.Button && _model.IsEnabled && _model.Id != SettingsAccessor.RowCacheClear
        && _model.Id != SettingsAccessor.RowInstallModelPack && !SettingsAccessor.IsCameraButtonRow(_model.Id);

    /// <summary>The "Clear cache" button row of the Cache page.</summary>
    public bool IsCacheClearRow => _model.Kind == SettingKind.Button && _model.IsEnabled && _model.Id == SettingsAccessor.RowCacheClear;

    /// <summary>The "Install model pack..." button row of the AI models page (its command is on the Settings view model).</summary>
    public bool IsInstallModelPackRow => _model.Kind == SettingKind.Button && _model.IsEnabled && _model.Id == SettingsAccessor.RowInstallModelPack;

    /// <summary>Text of the install button.</summary>
    public string InstallModelPackButtonText => SettingsCatalog.Localized(Strings.ByName, "FixInstallModelPack", "Install model pack...");

    /// <summary>Text of the clear-cache button (the row caption names the action).</summary>
    public string CacheClearButtonText => Strings.SetCacheClearButton;

    /// <summary>Automation id of the row's value control: the Cache rows have fixed ids, every other row keeps Setting_{Id}.</summary>
    public string RowAutomationId
    {
        get
        {
            switch (_model.Id)
            {
                case SettingsAccessor.RowCacheSize: return "SettingsCacheSize";
                case SettingsAccessor.RowCacheMax: return "SettingsCacheMax";
                case SettingsAccessor.RowCacheClear: return "SettingsCacheClear";
                case SettingsAccessor.RowInstallModelPack: return "SettingsInstallModelPack";
                case SettingsAccessor.RowAgentMaxImages: return "SettingsAgentMaxImages";
                case SettingsAccessor.RowAgentBatchWorkers: return "SettingsAgentBatchWorkers";
                case SettingsAccessor.RowAgentMaxCost: return "SettingsAgentMaxCost";
                case SettingsAccessor.RowAgentMaxUsdPerDay: return "SettingsAgentMaxUsdPerDay";
                case SettingsAccessor.RowAgentRouteLocalFirst: return "SettingsAgentRouteLocalFirst";
                case SettingsAccessor.RowAgentBatchRetryNormal: return "SettingsAgentBatchRetryNormal";
                case SettingsAccessor.RowAgentBatchSam2Refine: return "SettingsAgentBatchSam2Refine";
                case SettingsAccessor.RowCameraSource: return "SettingsCameraSource";
                case SettingsAccessor.RowCameraDevice: return "SettingsCameraDevice";
                case SettingsAccessor.RowCameraRefresh: return "SettingsCameraRefresh";
                case SettingsAccessor.RowCameraConnect: return "SettingsCameraConnect";
                case SettingsAccessor.RowCameraDisconnect: return "SettingsCameraDisconnect";
                case SettingsAccessor.RowCameraStatus: return "SettingsCameraStatus";
                case SettingsAccessor.RowCameraError: return "SettingsCameraError";
                case SettingsAccessor.RowEasyVisionFolder: return "SettingsEasyVisionFolder";
                case SettingsAccessor.RowEasyVisionFolderBrowse: return "SettingsEasyVisionFolderBrowse";
                default: return "Setting_" + _model.Id;
            }
        }
    }

    /// <summary>Replaces the status text of an Info row (the current cache size).</summary>
    public void SetValueText(string text)
    {
        _model.ValueText = text;
        OnPropertyChanged(nameof(ValueText));
        OnPropertyChanged(nameof(IsRowVisible));
    }

    /// <summary>Colour of the swatch next to the hex text of a colour row (transparent while the text is not a valid colour).</summary>
    public System.Windows.Media.Brush SwatchBrush
    {
        get
        {
            if (!ThemePalette.TryParseHex(TextValue, out uint rgb))
            {
                return System.Windows.Media.Brushes.Transparent;
            }

            var brush = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
            brush.Freeze();
            return brush;
        }
    }

    private void ChooseColor()
    {
        uint current = ThemePalette.TryParseHex(TextValue, out uint rgb) ? rgb : 0u;
        if (NativeColorDialog.TryChoose(current, out uint chosen))
        {
            TextValue = ThemePalette.ToHex(chosen);
        }
    }

    public string Id => _model.Id;

    public string PageId => _model.PageId;

    public string Section => _model.Section;

    public string Caption => _model.Caption;

    public string? Description => string.IsNullOrEmpty(_model.Description) ? null : _model.Description;

    public bool IsEnabled => _model.IsEnabled;

    /// <summary>Why the row is not available (tooltip); null for a working row.</summary>
    public string? ToolTipText => _model.IsEnabled ? null : _model.DisabledReason ?? _model.ValueText;

    public string ValueText => _model.ValueText ?? string.Empty;

    public IReadOnlyList<SettingOption> Options => _dynamicOptions ?? _model.Options;

    public bool IsToggleRow => _model.Kind == SettingKind.Toggle;

    public bool IsChoiceRow => _model.Kind == SettingKind.Choice && _model.IsEnabled;

    public bool IsPathRow => _model.Kind == SettingKind.Path;

    public bool IsInfoRow => _model.Kind == SettingKind.Info;

    /// <summary>Disabled text and choice rows show their default in a read-only box.</summary>
    public bool IsTextRow => (_model.Kind == SettingKind.Text || _model.Kind == SettingKind.Choice) && !_model.IsEnabled;

    /// <summary>Enabled text rows (colours, predefined labels / flags) are edited in place.</summary>
    public bool IsEditableTextRow => _model.Kind == SettingKind.Text && _model.IsEnabled;

    public string TextValue
    {
        get => SettingsAccessor.TryGetString(_main.Settings, _model.Id, out string value) ? value : string.Empty;
        set
        {
            if (_model.IsEnabled && SettingsAccessor.TrySetString(_main.Settings, _model.Id, value))
            {
                if (SettingsAccessor.IsThemeColorRow(_model.Id))
                {
                    _main.NotifyPaletteChanged();
                }
                else if (_model.Id == SettingsAccessor.RowCacheMax)
                {
                    _main.NotifyCacheLimitChanged();
                }
                else
                {
                    _main.NotifySettingChanged();
                }
            }

            // An invalid value (a colour that is not #RRGGBB) snaps back to the stored one.
            OnPropertyChanged();
            OnPropertyChanged(nameof(SwatchBrush));
        }
    }

    public bool ShowHeader
    {
        get => _showHeader;
        set => SetProperty(ref _showHeader, value);
    }

    public string HeaderText
    {
        get => _headerText;
        set => SetProperty(ref _headerText, value);
    }

    /// <summary>First row of a page in the single scrolling view: the page title is shown above it.</summary>
    public bool ShowPageHeader
    {
        get => _showPageHeader;
        set => SetProperty(ref _showPageHeader, value);
    }

    public string PageHeaderText
    {
        get => _pageHeaderText;
        set => SetProperty(ref _pageHeaderText, value);
    }

    public bool BoolValue
    {
        get
        {
            switch (_model.Id)
            {
                case SettingsCatalog.RowAutoSave: return _main.AutoSave;
                case SettingsCatalog.RowEmbedImage: return _main.EmbedImageData;
                default:
                    if (_model.IsEnabled && SettingsAccessor.TryGetBool(_main.Settings, _model.Id, out bool stored))
                    {
                        return stored;
                    }

                    return string.Equals(_model.ValueText, Strings.SetOn, StringComparison.Ordinal);
            }
        }

        set
        {
            if (!_model.IsEnabled)
            {
                return;
            }

            if (_model.Id == SettingsCatalog.RowAutoSave)
            {
                _main.AutoSave = value;
            }
            else if (_model.Id == SettingsCatalog.RowEmbedImage)
            {
                _main.EmbedImageData = value;
            }
            else if (SettingsAccessor.TrySetBool(_main.Settings, _model.Id, value))
            {
                if (_model.Id == SettingsAccessor.RowAgentOfflineOnly)
                {
                    _main.Label?.RefreshAgentOfflineState();
                }

                _main.NotifySettingChanged();
            }

            OnPropertyChanged();
        }
    }

    public SettingOption? SelectedOption
    {
        get
        {
            string current;
            if (_model.Id == SettingsCatalog.RowTheme)
            {
                current = _main.ThemeMode;
            }
            else if (_model.Id == SettingsCatalog.RowLanguage)
            {
                current = _main.UiCultureName;
            }
            else if (!SettingsAccessor.TryGetString(_main.Settings, _model.Id, out current))
            {
                current = string.Empty;
            }

            return Options.FirstOrDefault(o => string.Equals(o.Value, current, StringComparison.Ordinal));
        }

        set
        {
            if (value == null || !_model.IsEnabled)
            {
                return;
            }

            if (_model.Id == SettingsCatalog.RowTheme)
            {
                _main.ThemeMode = value.Value;
            }
            else if (_model.Id == SettingsCatalog.RowLanguage)
            {
                _main.UiCultureName = value.Value;
            }
            else if (SettingsAccessor.TrySetString(_main.Settings, _model.Id, value.Value))
            {
                if (_model.Id == SettingsAccessor.RowCameraSource)
                {
                    _main.NotifyImageSourceChanged();
                }
                else if (_model.Id == SettingsAccessor.RowSegAiDevice)
                {
                    _main.NotifySegAiDeviceChanged();
                }
                else
                {
                    _main.NotifySettingChanged();
                }
            }

            OnPropertyChanged();
        }
    }

    public string PathText => string.IsNullOrWhiteSpace(_main.OutputDirectory) ? Strings.SetNoOutputDirectory : _main.OutputDirectory!;

    /// <summary>The setting behind the row changed elsewhere (File menu); re-read it.</summary>
    public void Refresh()
    {
        OnPropertyChanged(nameof(BoolValue));
        OnPropertyChanged(nameof(SelectedOption));
        OnPropertyChanged(nameof(PathText));
        OnPropertyChanged(nameof(TextValue));
        OnPropertyChanged(nameof(SwatchBrush));
        CameraActionCommand.NotifyCanExecuteChanged();
    }
}

/// <summary>State of the Settings window: pages, search box and the rows that match.</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private readonly SettingsStore _store;
    private readonly List<SettingPageModel> _pages;
    private readonly Dictionary<SettingRowModel, SettingRowViewModel> _rows = new Dictionary<SettingRowModel, SettingRowViewModel>();

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private SettingPageModel? _selectedPage;

    public SettingsViewModel(MainViewModel main, SettingsStore store)
    {
        _main = main;
        _store = store;
        _pages = SettingsCatalog.Build(Strings.ByName, main.AiModels).ToList();
        foreach (SettingPageModel page in _pages)
        {
            foreach (SettingRowModel row in page.Rows)
            {
                _rows[row] = new SettingRowViewModel(row, main);
            }
        }

        Agents = new AgentsSettingsViewModel(main);
        Agents.PropertyChanged -= OnAgentSettingsChanged;
        Agents.PropertyChanged += OnAgentSettingsChanged;
        SettingRowViewModel? offlineRow = RowById(SettingsAccessor.RowAgentOfflineOnly);
        if (offlineRow != null)
        {
            offlineRow.PropertyChanged -= OnOfflineRowChanged;
            offlineRow.PropertyChanged += OnOfflineRowChanged;
        }
        Pages = _pages;
        _selectedPage = _pages[0];
        VisibleRows = new ObservableCollection<SettingRowViewModel>();
        Rebuild();
        _main.PropertyChanged -= OnMainPropertyChanged;
        _main.PropertyChanged += OnMainPropertyChanged;
        _main.Canvas.PropertyChanged -= OnCanvasPropertyChanged;
        _main.Canvas.PropertyChanged += OnCanvasPropertyChanged;
        _main.PaletteChanged -= OnPaletteChanged;
        _main.PaletteChanged += OnPaletteChanged;
        _dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
        SettingRowViewModel? deviceRow = RowById(SettingsAccessor.RowCameraDevice);
        if (deviceRow != null)
        {
            deviceRow.PropertyChanged -= OnCameraDeviceRowChanged;
            deviceRow.PropertyChanged += OnCameraDeviceRowChanged;
        }

        SettingRowViewModel? browseRow = RowById(SettingsAccessor.RowEasyVisionFolderBrowse);
        if (browseRow != null)
        {
            browseRow.SettingChangedByButton -= OnEasyVisionFolderPicked;
            browseRow.SettingChangedByButton += OnEasyVisionFolderPicked;
        }

        _main.Camera.StateChanged -= OnCameraStateChanged;
        _main.Camera.StateChanged += OnCameraStateChanged;
        SyncCameraRows();
        _ = RefreshCacheSizeAsync();
    }

    private readonly System.Windows.Threading.Dispatcher _dispatcher;
    private bool _cameraChecked;

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
    {
        RowById(SettingsAccessor.RowEasyVisionFolder)?.Refresh();
    }

    private SettingRowViewModel? RowById(string id)
    {
        return _rows.Values.FirstOrDefault(r => r.Id == id);
    }

    // The camera service raises StateChanged on worker threads: marshal to the UI thread.
    private void OnCameraStateChanged(object? sender, EventArgs e)
    {
        if (_dispatcher.CheckAccess())
        {
            SyncCameraRows();
        }
        else
        {
            _dispatcher.BeginInvoke(new Action(SyncCameraRows));
        }
    }

    private void OnCameraDeviceRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SettingRowViewModel.SelectedOption))
        {
            RaiseCameraCanExecuteChanged();
        }
    }

    private void RaiseCameraCanExecuteChanged()
    {
        foreach (SettingRowViewModel row in _rows.Values)
        {
            if (SettingsAccessor.IsCameraButtonRow(row.Id))
            {
                row.RaiseCameraCanExecuteChanged();
            }
        }
    }

    /// <summary>Copies the camera service (device list, status line, error) into the Camera page rows. UI thread only.</summary>
    private void SyncCameraRows()
    {
        EasyLabel.Camera.CameraService camera = _main.Camera;
        IReadOnlyList<EasyLabel.Camera.CameraInfo> devices = camera.Devices;

        // First enumeration with nothing chosen yet: pick the first camera so Connect is ready.
        if (string.IsNullOrEmpty(_main.Settings.LastCameraId) && devices.Count > 0)
        {
            _main.Settings.LastCameraId = devices[0].Id;
            _main.NotifySettingChanged();
        }

        SettingRowViewModel? deviceRow = RowById(SettingsAccessor.RowCameraDevice);
        if (deviceRow != null)
        {
            bool same = deviceRow.Options.Count == devices.Count;
            for (int i = 0; same && i < devices.Count; i++)
            {
                same = string.Equals(deviceRow.Options[i].Value, devices[i].Id, StringComparison.Ordinal)
                    && string.Equals(deviceRow.Options[i].Caption, devices[i].DisplayName, StringComparison.Ordinal);
            }

            if (!same)
            {
                deviceRow.SetOptions(devices.Select(d => new SettingOption(d.Id, d.DisplayName)).ToList());
            }
        }

        string status = string.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            Strings.ByName(camera.StatusKey),
            camera.DeviceName ?? string.Empty,
            camera.ReconnectAttempt,
            EasyLabel.Camera.CameraService.MaxReconnectAttempts);
        RowById(SettingsAccessor.RowCameraStatus)?.SetValueText(status);

        string error = camera.ErrorText;
        if (error.Length == 0 && camera.HasEnumerated && devices.Count == 0)
        {
            error = camera.EnumerationMessage.Length > 0 ? camera.EnumerationMessage : Strings.SetCameraNoDevices;
        }

        RowById(SettingsAccessor.RowCameraError)?.SetValueText(error);
        RaiseCameraCanExecuteChanged();
    }

    private bool _installingPack;

    private bool CanInstallModelPack() => !_installingPack && _main.ModelsRoot.Length > 0 && _main.Dialogs != null;

    /// <summary>
    /// Installs a model pack (zip): reads and checks its manifest, shows the licence and asks for confirmation, then installs on a worker.
    /// Nothing in the pack is run. Results are shown as a notice; the AI model list is read again after a successful install.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanInstallModelPack))]
    private async Task InstallModelPackAsync()
    {
        IDialogService? dialogs = _main.Dialogs;
        string modelsRoot = _main.ModelsRoot;
        if (dialogs == null || modelsRoot.Length == 0)
        {
            return;
        }

        string title = SettingsCatalog.Localized(Strings.ByName, "FixInstallModelPack", "Install model pack...");
        string? pack = dialogs.PickModelPack(title, null);
        if (string.IsNullOrWhiteSpace(pack))
        {
            return;
        }

        string packPath = pack;
        EasyLabel.Core.Models.ModelPackReadResult read = await Task.Run(() => EasyLabel.Core.Models.ModelPackInstaller.ReadManifest(packPath));
        if (!read.Success || read.Manifest == null)
        {
            _main.ShowDataNotice(title, read.Message, InfoBarSeverity.Error);
            return;
        }

        string licence = string.Join(Environment.NewLine, OkNgModelRules.Licences(read.Manifest));
        string question = string.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            SettingsCatalog.Localized(Strings.ByName, "ModelPackConfirmFormat", "Install {0} files into the models folder?"),
            read.Manifest.Files.Count);
        string targets = string.Join(", ", read.Manifest.Files.Select(f => f.Target).Distinct(StringComparer.OrdinalIgnoreCase));
        string folders = string.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            SettingsCatalog.Localized(Strings.ByName, "ModelPackTargetsFormat", "Folders: {0}"),
            targets);
        string message = string.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            SettingsCatalog.Localized(Strings.ByName, "ModelPackLicence", "Licence: {0}"),
            licence) + Environment.NewLine + Environment.NewLine + folders + Environment.NewLine + Environment.NewLine + question;
        if (!dialogs.Confirm(title, message, SettingsCatalog.Localized(Strings.ByName, "ModelPackInstallConfirm", "Install")))
        {
            return;
        }

        EasyLabel.Core.Models.ModelPackInstallResult result;
        _installingPack = true;
        InstallModelPackCommand.NotifyCanExecuteChanged();
        try
        {
            result = await Task.Run(() => EasyLabel.Core.Models.ModelPackInstaller.Install(packPath, modelsRoot));
        }
        finally
        {
            _installingPack = false;
            InstallModelPackCommand.NotifyCanExecuteChanged();
        }

        if (result.Success)
        {
            _main.RefreshAiModels();
            _main.ShowDataNotice(
                title,
                string.Format(System.Globalization.CultureInfo.CurrentCulture, SettingsCatalog.Localized(Strings.ByName, "ModelPackInstalled", "Installed {0} models"), result.InstalledFiles.Count),
                InfoBarSeverity.Success);
            return;
        }

        string? errorKey = OkNgModelRules.ModelPackErrorKey(result.Error);
        string text = errorKey == "ModelPackBadHash"
            ? string.Format(System.Globalization.CultureInfo.CurrentCulture, SettingsCatalog.Localized(Strings.ByName, errorKey, "File {0} is damaged (checksum differs)"), result.BadFile)
            : string.Format(System.Globalization.CultureInfo.CurrentCulture, SettingsCatalog.Localized(Strings.ByName, "ModelPackFailed", "Model pack install failed: {0}"), result.Message);
        _main.ShowDataNotice(title, text, InfoBarSeverity.Error);
    }

    private bool _clearingCache;

    private bool CanClearCache() => !_clearingCache && _main.CacheDir != null;

    /// <summary>Measures the cache on a worker and shows the size on the Cache page (0 when no project is open).</summary>
    private async Task RefreshCacheSizeAsync()
    {
        try
        {
            string? dir = _main.CacheDir;
            long bytes = dir == null ? 0L : await Task.Run(() => EasyLabel.Core.CacheGovernor.Measure(dir));
            SettingRowViewModel? row = _rows.Values.FirstOrDefault(r => r.Id == SettingsAccessor.RowCacheSize);
            row?.SetValueText(FormatBytes(bytes));
            ClearCacheCommand.NotifyCanExecuteChanged();
        }
        catch (Exception ex)
        {
            _store.Log("Cache size failed: " + ex.Message);
        }
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return bytes.ToString(System.Globalization.CultureInfo.CurrentCulture) + " B";
        double value = bytes;
        string[] units = { "KB", "MB", "GB", "TB" };
        int unit = -1;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return value.ToString("0.#", System.Globalization.CultureInfo.CurrentCulture) + " " + units[unit];
    }

    /// <summary>Deletes the cache files this app owns (after a confirmation), then shows the new size.</summary>
    [RelayCommand(CanExecute = nameof(CanClearCache))]
    private async Task ClearCacheAsync()
    {
        string? dir = _main.CacheDir;
        IDialogService? dialogs = _main.Dialogs;
        if (dir == null || dialogs == null || !dialogs.Confirm(Strings.CacheClearTitle, Strings.CacheClearMessage, Strings.SetCacheClearButton))
        {
            return;
        }

        _clearingCache = true;
        ClearCacheCommand.NotifyCanExecuteChanged();
        try
        {
            await Task.Run(() => EasyLabel.Core.CacheGovernor.Clear(dir));
        }
        catch (Exception ex)
        {
            _store.Log("Clear cache failed: " + ex.Message);
        }
        finally
        {
            _clearingCache = false;
            ClearCacheCommand.NotifyCanExecuteChanged();
        }

        await RefreshCacheSizeAsync();
    }

    // The reset button (or another window) changed the palette: the colour rows show the stored colours again.
    private void OnPaletteChanged(object? sender, EventArgs e)
    {
        foreach (SettingRowViewModel row in _rows.Values)
        {
            row.Refresh();
        }
    }

    // A menu toggle (keep zoom, fill while drawing, ...) changed a stored option while the dialog is open.
    private void OnCanvasPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        foreach (SettingRowViewModel row in _rows.Values)
        {
            row.Refresh();
        }
    }

    public IReadOnlyList<SettingPageModel> Pages { get; }

    /// <summary>The Claude Code &amp; Codex page (sign-in and skills); it replaces the row list while its category is selected.</summary>
    public AgentsSettingsViewModel Agents { get; }

    private bool _agentsChecked;

    /// <summary>True while the Claude Code &amp; Codex category is selected and no search is running.</summary>
    public bool IsAgentsPage => SelectedPage != null && SelectedPage.Id == SettingsCatalog.PageAgents && !IsSearching;

    public bool IsRowsPage => !IsAgentsPage;

    public ObservableCollection<SettingRowViewModel> VisibleRows { get; }

    /// <summary>True while the search box has text: only the rows that match are listed.</summary>
    public bool IsSearching => !string.IsNullOrWhiteSpace(SearchText);

    /// <summary>The category list asked to scroll to the first row of a page; the window brings it to the top.</summary>
    public event EventHandler<SettingRowViewModel>? ScrollToRowRequested;

    partial void OnSearchTextChanged(string value)
    {
        OnPropertyChanged(nameof(IsSearching));
        OnPropertyChanged(nameof(IsAgentsPage));
        OnPropertyChanged(nameof(IsRowsPage));
        Rebuild();
    }

    partial void OnSelectedPageChanged(SettingPageModel? value)
    {
        // All pages share one scrolling list (like Labelme); picking a category scrolls to its section.
        OnPropertyChanged(nameof(IsAgentsPage));
        OnPropertyChanged(nameof(IsRowsPage));
        if (value == null)
        {
            return;
        }

        if (value.Id == SettingsCatalog.PageCache)
        {
            _ = RefreshCacheSizeAsync();
        }

        if (value.Id == SettingsCatalog.PageCamera && !_cameraChecked)
        {
            _cameraChecked = true;
            _ = _main.Camera.EnumerateAsync();
        }

        if (value.Id == SettingsCatalog.PageAgents)
        {
            Agents.RefreshOfflineState();
            if (!_agentsChecked)
            {
                _agentsChecked = true;
                _ = Agents.RefreshAsync();
            }

            return;
        }

        SettingRowViewModel? first = VisibleRows.FirstOrDefault(r => string.Equals(r.PageId, value.Id, StringComparison.Ordinal));
        if (first != null)
        {
            ScrollToRowRequested?.Invoke(this, first);
        }
    }

    /// <summary>Unhooks the window from the main view model (call when the window closes).</summary>
    public void Detach()
    {
        Agents.PropertyChanged -= OnAgentSettingsChanged;
        SettingRowViewModel? offlineRow = RowById(SettingsAccessor.RowAgentOfflineOnly);
        if (offlineRow != null) offlineRow.PropertyChanged -= OnOfflineRowChanged;
        _main.PropertyChanged -= OnMainPropertyChanged;
        _main.Canvas.PropertyChanged -= OnCanvasPropertyChanged;
        _main.PaletteChanged -= OnPaletteChanged;
        _main.Camera.StateChanged -= OnCameraStateChanged;
    }

    private void OnMainPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.IsCameraSourceActive))
        {
            RaiseCameraCanExecuteChanged();
        }

        if (e.PropertyName == nameof(MainViewModel.AutoSave)
            || e.PropertyName == nameof(MainViewModel.EmbedImageData)
            || e.PropertyName == nameof(MainViewModel.OutputDirectory)
            || e.PropertyName == nameof(MainViewModel.ThemeMode)
            || e.PropertyName == nameof(MainViewModel.UiCultureName))
        {
            foreach (SettingRowViewModel row in _rows.Values)
            {
                row.Refresh();
            }
        }
    }

    private void Rebuild()
    {
        // One scrolling view with every page (an empty search returns every row of every page, in page order).
        IEnumerable<SettingRowModel> models = SettingsSearch.Filter(_pages, SearchText);
        VisibleRows.Clear();
        SettingRowViewModel? previous = null;
        foreach (SettingRowModel model in models)
        {
            SettingRowViewModel row = _rows[model];
            bool newPage = previous == null || !string.Equals(previous.PageId, row.PageId, StringComparison.Ordinal);
            bool newGroup = newPage || !string.Equals(previous!.Section, row.Section, StringComparison.Ordinal);
            row.ShowPageHeader = newPage;
            row.PageHeaderText = PageTitle(row.PageId);
            row.ShowHeader = newGroup && !string.IsNullOrEmpty(row.Section);
            row.HeaderText = row.Section;
            VisibleRows.Add(row);
            previous = row;
        }
    }

    private string PageTitle(string pageId)
    {
        return _pages.First(p => p.Id == pageId).Title;
    }

    [RelayCommand]
    private void ChooseOutputDirectory()
    {
        _main.ChangeOutputDirectoryCommand.Execute(null);
    }

    [RelayCommand]
    private void ClearOutputDirectory()
    {
        _main.ClearOutputDirectory();
    }

    private bool CanOpenConfig() => _store.Directory.Length > 0;

    /// <summary>Opens the real settings.json with the default editor (written first when it does not exist yet).</summary>
    [RelayCommand(CanExecute = nameof(CanOpenConfig))]
    private void OpenConfig()
    {
        string path = _store.SettingsPath;
        try
        {
            if (!File.Exists(path))
            {
                _store.SaveSettings(_main.Settings);
            }

            try
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (Win32Exception)
            {
                // No program is associated with .json: fall back to Notepad.
                Process.Start(new ProcessStartInfo("notepad.exe", "\"" + path + "\"") { UseShellExecute = true });
            }
        }
        catch (Exception ex)
        {
            _store.Log("Open settings file failed: " + ex.Message);
        }
    }
}
