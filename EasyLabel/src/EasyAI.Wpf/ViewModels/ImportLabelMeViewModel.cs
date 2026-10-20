using System.Collections.ObjectModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EasyAI.Desktop.Infrastructure;
using EasyAI.Desktop.Resources;
using EasyAI.Desktop.Services;
using EasyLabel.Core.Import;

namespace EasyAI.Desktop.ViewModels;

/// <summary>One row of the preview table of the Import LabelMe dialog.</summary>
public sealed record ImportPreviewRow(string Json, string Image, string Shapes, string Status);

/// <summary>
/// Import LabelMe JSON dialog: Scan reads the folder (nothing is written), the preview table and counts are shown, Import copies the
/// images and JSON into the project with the chosen conflict mode (default Skip). Both steps run on a background task with cancel.
/// </summary>
public sealed partial class ImportLabelMeViewModel : ObservableObject
{
    private const int MaxListedReasons = 200;

    private readonly IDialogService _dialogs;
    private readonly SettingsStore _store;
    private LabelMeImportPreview? _preview;
    private CancellationTokenSource? _cts;

    private readonly EasyLabel.Core.ProjectPaths _paths;

    public ImportLabelMeViewModel(string projectRoot, IDialogService dialogs, SettingsStore store, EasyLabel.Core.ProjectPaths? paths = null)
    {
        _paths = paths ?? EasyLabel.Core.ProjectPaths.Legacy(projectRoot);
        _dialogs = dialogs;
        _store = store;
    }

    public ObservableCollection<ImportPreviewRow> Rows { get; } = new ObservableCollection<ImportPreviewRow>();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ScanCommand))]
    private string _folderPath = string.Empty;

    [ObservableProperty]
    private bool _isSkip = true;

    [ObservableProperty]
    private bool _isReplace;

    [ObservableProperty]
    private bool _isRename;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ScanCommand), nameof(RunCommand), nameof(CancelCommand), nameof(BrowseCommand))]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    private bool _isRunning;

    [ObservableProperty]
    private string _summaryText = string.Empty;

    [ObservableProperty]
    private string _detailText = string.Empty;

    [ObservableProperty]
    private double _progressValue;

    [ObservableProperty]
    private double _progressMaximum = 1;

    [ObservableProperty]
    private string _progressText = string.Empty;

    [ObservableProperty]
    private string _resultText = string.Empty;

    public bool IsIdle => !IsRunning;

    private bool CanBrowse() => !IsRunning;

    private bool CanScan() => !IsRunning && !string.IsNullOrWhiteSpace(FolderPath);

    private bool CanRun() => !IsRunning && _preview != null && _preview.Items.Any(i => i.CanImport);

    private bool CanCancel() => IsRunning;

    private LabelMeImportConflictMode Mode => IsReplace ? LabelMeImportConflictMode.Replace : IsRename ? LabelMeImportConflictMode.Rename : LabelMeImportConflictMode.Skip;

    [RelayCommand(CanExecute = nameof(CanBrowse))]
    private void Browse()
    {
        string? initial = Directory.Exists(FolderPath) ? FolderPath : null;
        string? folder = _dialogs.PickFolder(Strings.ImpBrowseTitle, initial);
        if (folder != null)
        {
            FolderPath = folder;
        }
    }

    [RelayCommand(CanExecute = nameof(CanScan))]
    private async Task ScanAsync()
    {
        string folder = FolderPath.Trim();
        CancellationTokenSource cts = new CancellationTokenSource();
        _cts = cts;
        IsRunning = true;
        _preview = null;
        Rows.Clear();
        SummaryText = string.Empty;
        DetailText = string.Empty;
        ResultText = string.Empty;
        try
        {
            LabelMeImportPreview preview = await Task.Run(() => new LabelMeFolderImporter(_paths).Preview(folder, cts.Token));
            _preview = preview;
            ShowPreview(preview);
        }
        catch (OperationCanceledException)
        {
            ResultText = Strings.ExpCancelled;
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException)
        {
            ResultText = string.Format(Strings.ImpFailedFormat, ex.Message);
        }
        catch (Exception ex)
        {
            _store.Log("Import LabelMe scan failed: " + ex);
            ResultText = string.Format(Strings.ImpFailedFormat, ex.Message);
        }
        finally
        {
            _cts = null;
            cts.Dispose();
            IsRunning = false;
            RunCommand.NotifyCanExecuteChanged();
        }
    }

    private void ShowPreview(LabelMeImportPreview preview)
    {
        foreach (LabelMeImportItem item in preview.Items)
        {
            string status = item.Error != null
                ? Strings.ImpStatusInvalid
                : item.ImageMissing ? Strings.ImpStatusMissing : item.Conflict ? Strings.ImpStatusConflict : Strings.ImpStatusNew;
            string shapes = item.Error != null
                ? string.Empty
                : item.ShapeCount.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + (item.ShapesByKind.Count > 0 ? " (" + string.Join(", ", item.ShapesByKind.Select(p => p.Key + " " + p.Value)) + ")" : string.Empty);
            Rows.Add(new ImportPreviewRow(item.SourceRel, item.TargetRel, shapes, status));
        }

        SummaryText = string.Format(
            Strings.ImpSummaryFormat,
            preview.JsonFiles,
            preview.ImagesFound,
            preview.ImagesMissing,
            preview.InvalidJson,
            preview.Conflicts,
            preview.TotalShapes);
        string kinds = preview.ShapesByKind.Count == 0 ? "-" : string.Join(", ", preview.ShapesByKind.Select(p => p.Key + " " + p.Value));
        string newLabels = preview.NewLabels.Count == 0 ? "-" : string.Join(", ", preview.NewLabels);
        string knownLabels = preview.ExistingLabels.Count == 0 ? "-" : string.Join(", ", preview.ExistingLabels);
        DetailText = string.Format(Strings.ImpKindsFormat, kinds) + Environment.NewLine + string.Format(Strings.ImpLabelsFormat, newLabels, knownLabels);
        if (!preview.Items.Any(i => i.CanImport))
        {
            ResultText = Strings.ImpNothing;
        }
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RunAsync()
    {
        LabelMeImportPreview? preview = _preview;
        if (preview == null)
        {
            return;
        }

        LabelMeImportConflictMode mode = Mode;
        if (mode == LabelMeImportConflictMode.Replace && preview.Conflicts > 0)
        {
            // Replace overwrites the project image and its annotation: ask first and name the number of files.
            Wpf.Ui.Controls.MessageBox confirm = new Wpf.Ui.Controls.MessageBox
            {
                Title = Strings.ImpReplaceTitle,
                Content = string.Format(Strings.ImpReplacePrompt, preview.Conflicts),
                PrimaryButtonText = Strings.ImpReplaceConfirm,
                CloseButtonText = Strings.Cancel,
                Owner = System.Windows.Application.Current?.MainWindow,
            };
            Wpf.Ui.Controls.MessageBoxResult answer = await confirm.ShowDialogAsync();
            if (answer != Wpf.Ui.Controls.MessageBoxResult.Primary)
            {
                return;
            }
        }

        CancellationTokenSource cts = new CancellationTokenSource();
        _cts = cts;
        IsRunning = true;
        ResultText = string.Empty;
        ProgressValue = 0;
        ProgressMaximum = 1;
        // Created on the UI thread, so the reports arrive on it.
        Progress<LabelMeImportProgress> progress = new Progress<LabelMeImportProgress>(p =>
        {
            ProgressMaximum = Math.Max(1, p.Total);
            ProgressValue = p.Done;
            ProgressText = string.Format(Strings.ExpProgressFormat, p.Done, p.Total, p.Current);
        });

        try
        {
            LabelMeImportResult result = await Task.Run(() => new LabelMeFolderImporter(_paths).Apply(preview, mode, progress, cts.Token));
            ResultText = BuildSummary(result);
            // The preview describes the state before the import; scan again to import more.
            _preview = null;
        }
        catch (Exception ex)
        {
            _store.Log("Import LabelMe failed: " + ex);
            ResultText = string.Format(Strings.ImpFailedFormat, ex.Message);
        }
        finally
        {
            _cts = null;
            cts.Dispose();
            ProgressText = string.Empty;
            IsRunning = false;
            RunCommand.NotifyCanExecuteChanged();
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

    /// <summary>Called by the window when it is asked to close: true when nothing runs; otherwise the run is cancelled and the close refused.</summary>
    public bool TryClose()
    {
        if (!IsRunning)
        {
            return true;
        }

        Cancel();
        return false;
    }

    private static string BuildSummary(LabelMeImportResult result)
    {
        StringBuilder text = new StringBuilder();
        text.AppendLine(string.Format(Strings.ImpResultFormat, result.Imported, result.Replaced, result.Renamed, result.Skipped, result.Failed));
        if (result.Cancelled)
        {
            text.AppendLine(Strings.ImpCancelled);
        }

        if (result.ClassesAdded.Count > 0)
        {
            text.AppendLine(string.Format(Strings.ImpClassesAddedFormat, string.Join(", ", result.ClassesAdded)));
        }

        int listed = 0;
        foreach (string reason in result.Reasons)
        {
            if (listed >= MaxListedReasons)
            {
                text.AppendLine(string.Format(Strings.ExpMoreFormat, result.Reasons.Count - listed));
                break;
            }

            text.AppendLine("- " + reason);
            listed++;
        }

        return text.ToString().TrimEnd();
    }
}
