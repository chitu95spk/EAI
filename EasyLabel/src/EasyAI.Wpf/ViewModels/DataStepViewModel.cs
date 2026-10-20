using System.Windows;
using System.Windows.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EasyAI.Desktop.Infrastructure;
using EasyAI.Desktop.Resources;
using EasyLabel.Core.Editing;
using EasyLabel.SegAI;
using EasyLabel.SegAI.Data;
using InfoBarSeverity = Wpf.Ui.Controls.InfoBarSeverity;

namespace EasyAI.Desktop.ViewModels;

/// <summary>Data operations: import, add images, the Train/Val/Ignored/Stale/Missing JSON summary (shown only in the Train header) and the per-image Train/Val switch.</summary>
public sealed partial class DataStepViewModel : ObservableObject
{
    private const int MaxListedLines = 200;
    private readonly MainViewModel _host;

    [ObservableProperty]
    private SegAiDataSummary _summary = SegAiDataSummary.Empty;

    public DataStepViewModel(MainViewModel host)
    {
        _host = host;
    }

    private sealed class AddImagesResult
    {
        public int Added { get; set; }

        public List<string> Errors { get; } = new List<string>();
    }

    /// <summary>Recomputes the summary from the image list (call after any change of an item's state).</summary>
    internal void Rebuild()
    {
        Summary = SegAiDataSummary.From(_host.Images.Select(i => i.Info));
        ModeAvailabilityChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Busy flag, session or selection changed: re-evaluates every command.</summary>
    internal void NotifyStateChanged()
    {
        ImportDatasetCommand.NotifyCanExecuteChanged();
        AddImagesCommand.NotifyCanExecuteChanged();
        ModeAvailabilityChanged?.Invoke(this, EventArgs.Empty);
    }

    private bool CanRun() => _host.HasSession && !_host.IsBusy;

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task ImportDatasetAsync()
    {
        Microsoft.Win32.OpenFileDialog dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = Strings.ImportDatasetDialogTitle,
            Filter = Strings.ImportDatasetFilter,
            CheckFileExists = true,
            Multiselect = false,
        };

        if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.FileName))
        {
            return;
        }

        // A dataset folder is chosen by its dataset.json; a zip is passed as is.
        string path = dialog.FileName;
        if (string.Equals(System.IO.Path.GetFileName(path), "dataset.json", StringComparison.OrdinalIgnoreCase))
        {
            path = System.IO.Path.GetDirectoryName(path) ?? path;
        }

        SegAiImportReport? report = await _host.RunStep1Async(session => session.ImportEasyVisionDataset(path), true);
        if (report != null)
        {
            await ShowImportResultAsync(report);
        }
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
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

        string[] files = dialog.FileNames;
        AddImagesResult? result = await _host.RunStep1Async(session =>
        {
            AddImagesResult added = new AddImagesResult();
            foreach (string file in files)
            {
                try
                {
                    session.AddImage(file);
                    added.Added++;
                }
                catch (Exception ex)
                {
                    added.Errors.Add(System.IO.Path.GetFileName(file) + ": " + ex.Message);
                }
            }

            return added;
        }, true);

        if (result != null && result.Errors.Count > 0)
        {
            _host.ShowDataNotice(
                Strings.AddImagesErrorTitle,
                string.Join(Environment.NewLine, result.Errors.Take(5)),
                InfoBarSeverity.Warning);
        }
    }

    /// <summary>Raised when the enable state of the Train / Val buttons may have changed (busy flag, selection, summary).</summary>
    public event EventHandler? ModeAvailabilityChanged;

    /// <summary>Train / Val of ANY image (file row button or the Label step switch): enabled only for an image with regions.</summary>
    internal bool CanSetMode(ImageItemViewModel? item)
    {
        if (item == null)
        {
            return false;
        }

        CanvasViewModel canvas = _host.Canvas;

        // Evaluated live (not cached): a drag or an unfinished polygon owns the editor.
        CanvasEditSession? session = canvas.Session;
        bool dragOrDraft = session != null && (session.IsDragging || session.HasDraft);
        return FileModeRules.CanSet(_host.HasSession, _host.IsBusy, canvas.InputBlocked, dragOrDraft, item.HasInfo, item.HasRegions);
    }

    /// <summary>Puts one image in the Train or Val set (the session writes it, the item is read back). Not an undo unit.</summary>
    internal async Task SetModeAsync(ImageItemViewModel item, SegTrainingMode target)
    {
        if (item.Info == null || !CanSetMode(item) || SegAiDataSummary.DisplayMode(item.Info.Mode) == target)
        {
            return;
        }

        string rel = item.Rel;
        string fullPath = item.FullPath;
        SegAiSampleInfo? info = await _host.RunStep1Async(session =>
        {
            session.SetMode(rel, target);

            // AddImage on a file already inside the project only reads its state back.
            return session.AddImage(fullPath);
        }, false);

        if (info != null)
        {
            item.Info = info;
            Rebuild();
        }
    }

    /// <summary>Puts every given image in the Train or Val set; images without regions are skipped (they cannot be in a set).</summary>
    internal async Task SetModeManyAsync(IReadOnlyList<ImageItemViewModel> rows, SegTrainingMode target)
    {
        foreach (ImageItemViewModel row in rows.ToList())
        {
            await SetModeAsync(row, target);
        }
    }

    /// <summary>Re-reads the state of one image (after its edits were written) so badges and summary follow the canvas.</summary>
    internal async Task RefreshItemAsync(ImageItemViewModel item)
    {
        SegAiSession? session = _host.Session;
        if (session == null || _host.IsBusy)
        {
            return;
        }

        try
        {
            await _host.Canvas.FlushAsync();
            string fullPath = item.FullPath;
            SegAiSampleInfo info = await Task.Run(() => session.AddImage(fullPath));
            if (ReferenceEquals(session, _host.Session))
            {
                item.Info = info;
                Rebuild();
            }
        }
        catch (Exception ex)
        {
            _host.LogData("Refresh image state failed: " + item.FullPath + " " + ex.Message);
        }
    }

    private static async Task ShowImportResultAsync(SegAiImportReport report)
    {
        List<string> lines = new List<string>
        {
            string.Format(Strings.ImportResultFormat, report.Imported, report.Skipped),
        };

        if (report.SkipReasons.Count > 0)
        {
            lines.Add(string.Empty);
            lines.AddRange(report.SkipReasons.Take(MaxListedLines));
            if (report.SkipReasons.Count > MaxListedLines)
            {
                lines.Add(string.Format(Strings.ImportMoreFormat, report.SkipReasons.Count - MaxListedLines));
            }
        }

        Wpf.Ui.Controls.TextBlock text = new Wpf.Ui.Controls.TextBlock
        {
            Text = string.Join(Environment.NewLine, lines),
            TextWrapping = TextWrapping.Wrap,
        };
        ScrollViewer scroll = new ScrollViewer
        {
            MaxHeight = 320,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = text,
        };

        Wpf.Ui.Controls.MessageBox box = new Wpf.Ui.Controls.MessageBox
        {
            Title = Strings.ImportResultTitle,
            Content = scroll,
            CloseButtonText = Strings.Close,
            Owner = Application.Current?.MainWindow,
        };
        await box.ShowDialogAsync();
    }
}
