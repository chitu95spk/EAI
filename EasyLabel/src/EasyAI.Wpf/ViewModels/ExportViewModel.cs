using EasyLabel.SegAI.Native;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EasyAI.Desktop.Infrastructure;
using EasyAI.Desktop.Resources;
using EasyAI.Desktop.Services;
using EasyLabel.Core.Trace;
using EasyLabel.SegAI;
using EasyLabel.SegAI.Export;
using EasyLabel.SegAI.Model;

namespace EasyAI.Desktop.ViewModels;

/// <summary>One entry of the format list of the Export dialog.</summary>
public sealed record ExportFormatOption(ExportFormat Format, string Caption)
{
    public override string ToString() => Caption;
}

/// <summary>One model of the project offered by "Send to EasyVision".</summary>
public sealed record SendModelOption(string File, string Caption)
{
    public override string ToString() => Caption;
}

/// <summary>
/// Export dialog: collects the request, runs <see cref="ExportService"/> on a background task with cancel and progress, and shows the
/// result summary, including every shape or image that was left out. The project files are only read.
/// </summary>
public sealed partial class ExportViewModel : ObservableObject
{
    private const int MaxListedSkips = 200;

    private readonly string _projectRoot;
    private readonly EasyLabel.Core.ProjectPaths? _paths;
    private readonly IDialogService _dialogs;
    private readonly SettingsStore _store;
    private CancellationTokenSource? _cts;

    public ExportViewModel(string projectRoot, IDialogService dialogs, SettingsStore store, ExportFormat? presetFormat = null, EasyLabel.Core.ProjectPaths? paths = null)
    {
        _projectRoot = projectRoot;
        _paths = paths;
        _dialogs = dialogs;
        _store = store;
        Formats = new[]
        {
            new ExportFormatOption(ExportFormat.LabelMeJson, Strings.ExpFmtLabelMe),
            new ExportFormatOption(ExportFormat.YoloDetect, Strings.ExpFmtYoloDetect),
            new ExportFormatOption(ExportFormat.YoloSegment, Strings.ExpFmtYoloSegment),
            new ExportFormatOption(ExportFormat.CocoInstances, Strings.ExpFmtCoco),
            new ExportFormatOption(ExportFormat.PascalVoc, Strings.ExpFmtVoc),
            new ExportFormatOption(ExportFormat.MaskPng, Strings.ExpFmtMask),
            new ExportFormatOption(ExportFormat.EasyVisionDataset, Strings.ExpFmtEasyVision),
            new ExportFormatOption(ExportFormat.Csv, Strings.ExpFmtCsv),
        };
        _selectedFormat = Formats.FirstOrDefault(f => presetFormat.HasValue && f.Format == presetFormat.Value) ?? Formats[0];
        string parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(projectRoot)) ?? projectRoot;
        string name = Path.GetFileName(Path.TrimEndingDirectorySeparator(projectRoot));
        _outputDirectory = Path.Combine(parent, (string.IsNullOrEmpty(name) ? "project" : name) + "_export");
        _projectName = string.IsNullOrEmpty(name) ? "project" : name;
        _modelsDir = (paths ?? EasyLabel.Core.ProjectPaths.Legacy(projectRoot)).ModelsDir;
        _classifyDir = (paths ?? EasyLabel.Core.ProjectPaths.Legacy(projectRoot)).ClassifyDir;
        LoadModels();
        // The OK/NG models (latest version of each name) follow the segmentation models; they cannot be sent to EasyVision.
        SendModels = SendModels.Concat(OkNgOptions()).ToList();
    }

    // ---------- Send to EasyVision (T4) ----------

    private readonly string _projectName;
    private readonly string _modelsDir;
    private readonly string _classifyDir;
    private EasyLabel.SegAI.Model.SegAiRegistry? _registry;

    /// <summary>Raised on the UI thread after a model was sent to EasyVision successfully (Easy step 5).</summary>
    public event EventHandler? EasyVisionSent;

    /// <summary>Models of the project that can be sent, newest first (empty when the project has none).</summary>
    public IReadOnlyList<SendModelOption> SendModels { get; private set; } = Array.Empty<SendModelOption>();

    public bool HasSendModels => SendModels.Count > 0;

    public string SendHeaderText => Text("ExpSendHeader", "Send to EasyVision");

    public string SendButtonText => Text("ExpSendButton", "Send to EasyVision");

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendToEasyVisionCommand), nameof(ExportOkNgModelCommand))]
    [NotifyPropertyChangedFor(nameof(IsOkNgSelected))]
    [NotifyPropertyChangedFor(nameof(SendBlockText))]
    private SendModelOption? _selectedSendModel;

    private static string Text(string key, string english) => SettingsCatalog.Localized(Strings.ByName, key, english);

    /// <summary>The selected entry is an OK/NG model: it can be copied with its record, not sent.</summary>
    public bool IsOkNgSelected => OkNgModelRules.IsModelFile(SelectedSendModel?.File);

    /// <summary>Why "Send to EasyVision" is unavailable for the selected entry; empty when it is available.</summary>
    public string SendBlockText
    {
        get
        {
            string? key = OkNgModelRules.SendBlockKey(SelectedSendModel?.File);
            return key == null ? string.Empty : Text(key, "EasyVision cannot read OK/NG models yet.");
        }
    }

    public string OkNgButtonText => Text("ExportOkNgModel", "OK/NG model");

    public string OperatorLabel => Text("TraceOperator", "Operator");

    public string NoteLabel => Text("TraceNote", "Note");

    /// <summary>The latest version of each OK/NG model of the project, as send-list entries (empty when the project has none).</summary>
    private IReadOnlyList<SendModelOption> OkNgOptions()
    {
        try
        {
            if (!Directory.Exists(_classifyDir))
            {
                return Array.Empty<SendModelOption>();
            }

            string label = Text("KindClassify", "OK/NG");
            return OkNgModelRules.LatestPerName(Directory.EnumerateFiles(_classifyDir).Select(f => Path.GetFileName(f)).ToList())
                .Select(f => new SendModelOption(f, f + " · " + label))
                .ToList();
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            return Array.Empty<SendModelOption>();
        }
    }

    [ObservableProperty]
    private string _traceOperator = string.Empty;

    [ObservableProperty]
    private string _traceNote = string.Empty;

    private void LoadModels()
    {
        try
        {
            var registry = new EasyLabel.SegAI.Model.SegAiRegistry(_modelsDir);
            registry.Load();
            _registry = registry;
            string? active = registry.Active;
            var options = registry.Entries
                .Where(e => !string.Equals(e.State, "FAILED", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(e => e.CreatedUtc)
                .Select(e => new SendModelOption(e.File, e.File + (string.Equals(e.File, active, StringComparison.OrdinalIgnoreCase) ? " *" : string.Empty)))
                .ToList();
            SendModels = options;
            _selectedSendModel = options.FirstOrDefault(o => string.Equals(o.File, active, StringComparison.OrdinalIgnoreCase)) ?? options.FirstOrDefault();
        }
        catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is UnauthorizedAccessException || ex is ArgumentException)
        {
            _registry = null;
            SendModels = Array.Empty<SendModelOption>();
        }
    }

    private bool CanSend() => !IsRunning && SelectedSendModel != null && OkNgModelRules.SendBlockKey(SelectedSendModel.File) == null;

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendToEasyVisionAsync()
    {
        SendModelOption? choice = SelectedSendModel;
        EasyLabel.SegAI.Model.SegAiRegistry? registry = _registry;
        if (choice == null || registry == null)
        {
            return;
        }

        EasyLabel.SegAI.Model.SegAiRegistryEntry? entry = registry.Entries.FirstOrDefault(e => string.Equals(e.File, choice.File, StringComparison.OrdinalIgnoreCase));
        string? folder = _store.LoadSettings().EasyVisionModelFolder;
        string modelPath = registry.PathOf(choice.File);
        var metadata = new PublishMetadata(
            _projectName,
            choice.File,
            entry?.ReportSummary ?? string.Empty,
            entry?.DatasetFingerprint ?? string.Empty,
            typeof(ExportViewModel).Assembly.GetName().Version?.ToString() ?? string.Empty);

        IsRunning = true;
        ResultText = string.Empty;
        try
        {
            PublishResult result = await Task.Run(() => new EasyVisionPublisher().Publish(modelPath, folder, _projectName, metadata, () => DateTime.UtcNow));
            ResultText = DescribePublish(result);
            if (result.Error == PublishError.None)
            {
                EasyVisionSent?.Invoke(this, EventArgs.Empty);
            }
        }
        catch (Exception ex)
        {
            _store.Log("Send to EasyVision failed: " + ex);
            ResultText = string.Format(Strings.ExpFailedFormat, ex.Message);
        }
        finally
        {
            IsRunning = false;
        }
    }

    /// <summary>The message the Export dialog shows for a publish result (English fallback while a resource key is missing).</summary>
    internal static string DescribePublish(PublishResult result)
    {
        switch (result.Error)
        {
            case PublishError.None:
                return string.Format(Text("ExpSendOkFormat", "Sent to EasyVision as {0} (version {1})."), result.ModelPath, result.Version);
            case PublishError.FolderNotSet:
                return Text("ExpSendFolderNotSet", "The EasyVision model folder is not set. Choose it in Settings > Files.");
            case PublishError.FolderMissing:
                return string.Format(Text("ExpSendFolderMissingFormat", "The EasyVision model folder does not exist. {0}"), result.Detail);
            case PublishError.FolderNotWritable:
                return string.Format(Text("ExpSendFolderNotWritableFormat", "The EasyVision model folder cannot be written. {0}"), result.Detail);
            case PublishError.ModelMissing:
                return string.Format(Text("ExpSendModelMissingFormat", "The model file is missing. {0}"), result.Detail);
            case PublishError.UnsupportedVersion:
                return string.Format(Text("ExpSendUnsupportedFormat", "EasyVision cannot read this model file format. {0}"), result.Detail);
            case PublishError.ModelInvalid:
                return string.Format(Text("ExpSendModelInvalidFormat", "The model file is damaged or not a SegAI model. {0}"), result.Detail);
            default:
                return string.Format(Text("ExpSendFailedFormat", "Sending the model failed. {0}"), result.Detail);
        }
    }

    public IReadOnlyList<ExportFormatOption> Formats { get; }

    [ObservableProperty]
    private ExportFormatOption _selectedFormat;

    // The three set radio buttons share a GroupName; the group clears the old one and each binding writes its own flag.
    [ObservableProperty]
    private bool _isSetAll = true;

    [ObservableProperty]
    private bool _isSetTrain;

    [ObservableProperty]
    private bool _isSetVal;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunCommand), nameof(ExportOkNgModelCommand))]
    private string _outputDirectory;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCopyImages))]
    private bool _isWriteZip;

    [ObservableProperty]
    private bool _onlyAnnotated = true;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunCommand), nameof(CancelCommand), nameof(BrowseCommand), nameof(SendToEasyVisionCommand), nameof(ExportOkNgModelCommand))]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    private bool _isRunning;

    [ObservableProperty]
    private double _progressValue;

    [ObservableProperty]
    private double _progressMaximum = 1;

    [ObservableProperty]
    private string _progressText = string.Empty;

    [ObservableProperty]
    private string _resultText = string.Empty;

    public bool IsCopyImages
    {
        get => !IsWriteZip;
        set => IsWriteZip = !value;
    }

    public bool IsIdle => !IsRunning;

    private ExportSet CurrentSet => IsSetTrain ? ExportSet.Train : IsSetVal ? ExportSet.Val : ExportSet.All;

    private bool CanRun() => !IsRunning && !string.IsNullOrWhiteSpace(OutputDirectory);

    private bool CanBrowse() => !IsRunning;

    private bool CanCancel() => IsRunning;

    [RelayCommand(CanExecute = nameof(CanBrowse))]
    private void Browse()
    {
        string? initial = Directory.Exists(OutputDirectory) ? OutputDirectory : Path.GetDirectoryName(OutputDirectory);
        string? folder = _dialogs.PickFolder(Strings.ExpBrowseTitle, initial);
        if (folder != null)
        {
            OutputDirectory = folder;
        }
    }

    private bool CanExportOkNg() => !IsRunning && IsOkNgSelected && !string.IsNullOrWhiteSpace(OutputDirectory);

    /// <summary>Copies the selected OK/NG model and its sidecar to the output folder and writes the trace record next to them.</summary>
    [RelayCommand(CanExecute = nameof(CanExportOkNg))]
    private async Task ExportOkNgModelAsync()
    {
        SendModelOption? choice = SelectedSendModel;
        if (choice == null || !IsOkNgSelected)
        {
            return;
        }

        string modelPath = Path.Combine(_classifyDir, choice.File);
        string outputDirectory = OutputDirectory.Trim();
        string? modelsRoot = string.IsNullOrEmpty(_store.Directory) ? null : Path.Combine(_store.Directory, "models");
        string? nativeFolder = NativeSegAiLibrary.LoadedFolder;
        EasyLabel.Core.ProjectPaths paths = _paths ?? EasyLabel.Core.ProjectPaths.Legacy(_projectRoot);
        string? operatorText = TraceOperator;
        string? noteText = TraceNote;

        IsRunning = true;
        ResultText = string.Empty;
        try
        {
            TraceWriteResult record = await Task.Run(() => OkNgModelExporter.Export(modelPath, outputDirectory, paths, modelsRoot, nativeFolder, operatorText, noteText));
            ResultText = string.Format(Text("TraceSaved", "Record saved: {0}"), record.TraceJsonPath);
        }
        catch (Exception ex)
        {
            _store.Log("Export OK/NG model failed: " + ex);
            ResultText = string.Format(Strings.ExpFailedFormat, ex.Message);
        }
        finally
        {
            IsRunning = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RunAsync()
    {
        var request = new ExportRequest
        {
            ProjectRoot = _projectRoot,
            Paths = _paths,
            Format = SelectedFormat.Format,
            Set = CurrentSet,
            OutputDirectory = OutputDirectory.Trim(),
            WriteZip = IsWriteZip,
            OnlyAnnotated = OnlyAnnotated,
        };

        var cts = new CancellationTokenSource();
        _cts = cts;
        IsRunning = true;
        ResultText = string.Empty;
        ProgressValue = 0;
        ProgressMaximum = 1;
        ProgressText = string.Empty;
        // Created on the UI thread, so the reports arrive on it.
        var progress = new Progress<ExportProgress>(p =>
        {
            ProgressMaximum = Math.Max(1, p.Total);
            ProgressValue = p.Done;
            ProgressText = string.Format(Strings.ExpProgressFormat, p.Done, p.Total, p.Current);
        });

        try
        {
            ExportResult result = await Task.Run(() => new ExportService().Run(request, progress, cts.Token));
            ResultText = BuildSummary(result);
        }
        catch (OperationCanceledException)
        {
            ResultText = Strings.ExpCancelled;
        }
        catch (ExportException ex)
        {
            ResultText = string.Format(Strings.ExpRefusedFormat, ex.Message);
        }
        catch (Exception ex)
        {
            _store.Log("Export failed: " + ex);
            ResultText = string.Format(Strings.ExpFailedFormat, ex.Message);
        }
        finally
        {
            _cts = null;
            cts.Dispose();
            ProgressText = string.Empty;
            IsRunning = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel()
    {
        try
        {
            _cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <summary>Called by the window when it is asked to close: true when no export is running; otherwise the run is cancelled and the close refused.</summary>
    public bool TryClose()
    {
        if (!IsRunning)
        {
            return true;
        }

        Cancel();
        return false;
    }

    private static string BuildSummary(ExportResult result)
    {
        var text = new StringBuilder();
        text.AppendLine(string.Format(
            Strings.ExpSummaryFormat,
            result.ImagesExported,
            result.ImagesSkipped,
            result.ShapesExported,
            result.ShapesSkipped,
            result.FilesWritten.Count));
        text.AppendLine(result.OutputPath);
        int listed = 0;
        foreach (ExportSkip skip in result.Skips)
        {
            if (listed >= MaxListedSkips)
            {
                text.AppendLine(string.Format(Strings.ExpMoreFormat, result.Skips.Count - listed));
                break;
            }

            text.AppendLine((skip.IsWarning ? Strings.ExpWarningPrefix : "- ") + skip);
            listed++;
        }

        return text.ToString().TrimEnd();
    }
}
