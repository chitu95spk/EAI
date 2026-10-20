using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EasyAI.Desktop.Infrastructure;
using EasyAI.Desktop.Resources;
using EasyAI.Desktop.Services;
using EasyLabel.Core.Convert;

namespace EasyAI.Desktop.ViewModels;

/// <summary>One row of the "expected objects per class" grid; a blank count means "ignore".</summary>
public sealed partial class ExpectedCountRow : ObservableObject
{
    public ExpectedCountRow(string className, string countText)
    {
        ClassName = className;
        _countText = countText;
    }

    public string ClassName { get; }

    [ObservableProperty]
    private string _countText;
}

/// <summary>
/// Tools &gt; Convert LabelMe folder to YOLO: collects the request, runs <see cref="LabelMeFolderYoloConverter"/> on a background task with
/// cancel and progress and shows the script counters, the skipped objects and the warnings. It needs no open project.
/// </summary>
public sealed partial class ConvertYoloViewModel : ObservableObject
{
    private const int MaxListedLines = 200;

    private readonly IDialogService _dialogs;
    private readonly SettingsStore _store;
    private CancellationTokenSource? _cts;
    private bool _classesEdited;
    private bool _settingClasses;

    public ConvertYoloViewModel(string? projectRoot, IDialogService dialogs, SettingsStore store, EasyLabel.Core.ProjectPaths? paths = null)
    {
        _dialogs = dialogs;
        _store = store;
        if (!string.IsNullOrWhiteSpace(projectRoot) || paths != null)
        {
            string classesFile = paths != null ? paths.ClassesFile : EasyLabel.Core.ProjectPaths.Legacy(projectRoot!).ClassesFile;
            IReadOnlyList<string> names = LabelMeFolderYoloConverter.ReadClassesFile(classesFile);
            if (names.Count > 0)
            {
                SetClasses(string.Join(Environment.NewLine, names));
            }
        }
    }

    public ObservableCollection<ExpectedCountRow> ExpectedRows { get; } = new ObservableCollection<ExpectedCountRow>();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunCommand))]
    private string _inputDirectory = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunCommand))]
    private string _outputDirectory = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsObb))]
    private bool _isDetect = true;

    [ObservableProperty]
    private string _classesText = string.Empty;

    [ObservableProperty]
    private bool _writeClasses = true;

    [ObservableProperty]
    private bool _overwrite;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunCommand), nameof(CancelCommand), nameof(BrowseInputCommand), nameof(BrowseOutputCommand))]
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

    public bool IsObb
    {
        get => !IsDetect;
        set => IsDetect = !value;
    }

    public bool IsIdle => !IsRunning;

    partial void OnClassesTextChanged(string value)
    {
        if (!_settingClasses)
        {
            _classesEdited = true;
        }

        RebuildExpectedRows(ParseClasses(value));
    }

    private void SetClasses(string text)
    {
        _settingClasses = true;
        try
        {
            ClassesText = text;
        }
        finally
        {
            _settingClasses = false;
        }
    }

    internal static List<string> ParseClasses(string text)
    {
        List<string> names = new List<string>();
        foreach (string line in (text ?? string.Empty).Split('\n'))
        {
            string name = line.Trim();
            if (name.Length > 0)
            {
                names.Add(name);
            }
        }

        return names;
    }

    private void RebuildExpectedRows(List<string> names)
    {
        // Keep the grid as is while the class list did not change (typing in the box fires this on every key).
        if (ExpectedRows.Select(r => r.ClassName).SequenceEqual(names, StringComparer.Ordinal))
        {
            return;
        }

        Dictionary<string, string> kept = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (ExpectedCountRow row in ExpectedRows)
        {
            kept[row.ClassName] = row.CountText;
        }

        ExpectedRows.Clear();
        foreach (string name in names)
        {
            ExpectedRows.Add(new ExpectedCountRow(name, kept.TryGetValue(name, out string? text) ? text : string.Empty));
        }
    }

    private bool CanBrowse() => !IsRunning;

    private bool CanRun() => !IsRunning && !string.IsNullOrWhiteSpace(InputDirectory) && !string.IsNullOrWhiteSpace(OutputDirectory);

    private bool CanCancel() => IsRunning;

    [RelayCommand(CanExecute = nameof(CanBrowse))]
    private void BrowseInput()
    {
        string? initial = Directory.Exists(InputDirectory) ? InputDirectory : null;
        string? folder = _dialogs.PickFolder(Strings.CvInputTitle, initial);
        if (folder == null)
        {
            return;
        }

        InputDirectory = folder;
        if (string.IsNullOrWhiteSpace(OutputDirectory))
        {
            OutputDirectory = Path.TrimEndingDirectorySeparator(folder) + "_yolo";
        }

        // A classes.txt next to the JSON files wins over the project's list, unless the user already typed one.
        if (!_classesEdited)
        {
            IReadOnlyList<string> names = LabelMeFolderYoloConverter.ReadClassesFile(Path.Combine(folder, "classes.txt"));
            if (names.Count > 0)
            {
                SetClasses(string.Join(Environment.NewLine, names));
            }
        }
    }

    [RelayCommand(CanExecute = nameof(CanBrowse))]
    private void BrowseOutput()
    {
        string? initial = Directory.Exists(OutputDirectory) ? OutputDirectory : null;
        string? folder = _dialogs.PickFolder(Strings.CvOutputTitle, initial);
        if (folder != null)
        {
            OutputDirectory = folder;
        }
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RunAsync()
    {
        List<string> classes = ParseClasses(ClassesText);
        if (classes.Count == 0)
        {
            ResultText = Strings.CvNeedClasses;
            return;
        }

        Dictionary<string, int> expected = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (ExpectedCountRow row in ExpectedRows)
        {
            string text = (row.CountText ?? string.Empty).Trim();
            if (text.Length == 0)
            {
                continue;
            }

            if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int count))
            {
                ResultText = string.Format(Strings.CvBadExpectedFormat, row.ClassName);
                return;
            }

            expected[row.ClassName] = count;
        }

        LabelMeFolderYoloRequest request = new LabelMeFolderYoloRequest
        {
            InputDirectory = InputDirectory.Trim(),
            OutputDirectory = OutputDirectory.Trim(),
            Format = IsDetect ? LabelMeYoloFormat.Detect : LabelMeYoloFormat.Obb,
            ClassNames = classes,
            WriteClassesFile = WriteClasses,
            OverwriteExisting = Overwrite,
            ExpectedObjectsPerClass = expected.Count > 0 ? expected : null,
        };

        CancellationTokenSource cts = new CancellationTokenSource();
        _cts = cts;
        IsRunning = true;
        ResultText = string.Empty;
        ProgressValue = 0;
        ProgressMaximum = 1;
        ProgressText = string.Empty;
        // Created on the UI thread, so the reports arrive on it.
        Progress<LabelMeYoloProgress> progress = new Progress<LabelMeYoloProgress>(p =>
        {
            ProgressMaximum = Math.Max(1, p.Total);
            ProgressValue = p.Done;
            ProgressText = string.Format(Strings.ExpProgressFormat, p.Done, p.Total, p.Current);
        });

        try
        {
            LabelMeYoloResult result = await Task.Run(() => new LabelMeFolderYoloConverter().Run(request, progress, cts.Token));
            ResultText = BuildSummary(result);
        }
        catch (OperationCanceledException)
        {
            ResultText = Strings.ExpCancelled;
        }
        catch (LabelMeYoloException ex)
        {
            ResultText = string.Format(Strings.CvRefusedFormat, ex.Message);
        }
        catch (Exception ex)
        {
            _store.Log("Convert LabelMe to YOLO failed: " + ex);
            ResultText = string.Format(Strings.CvFailedFormat, ex.Message);
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

    private static string BuildSummary(LabelMeYoloResult result)
    {
        StringBuilder text = new StringBuilder();
        text.AppendLine(string.Format(
            Strings.CvSummaryFormat,
            result.FilesFound,
            result.FilesConverted,
            result.FilesFailed,
            result.ObjectsConverted,
            result.ObjectsSkipped));
        text.AppendLine(result.OutputDirectory);
        if (result.ClassesFileWritten)
        {
            text.AppendLine(Strings.CvClassesWritten);
        }

        text.AppendLine(Strings.CvPerClassHeader);
        foreach (KeyValuePair<string, int> pair in result.PerClassCounts)
        {
            text.AppendLine("  " + pair.Key + ": " + pair.Value.ToString(CultureInfo.InvariantCulture));
        }

        AppendList(text, Strings.CvErrorsHeader, result.FileErrors);
        AppendList(text, Strings.CvSkippedHeader, result.Skips.Select(s => s.ToString()).ToList());
        AppendList(text, Strings.CvWarningsHeader, result.Warnings);
        return text.ToString().TrimEnd();
    }

    private static void AppendList(StringBuilder text, string header, IReadOnlyList<string> lines)
    {
        if (lines.Count == 0)
        {
            return;
        }

        text.AppendLine(header);
        for (int i = 0; i < lines.Count; i++)
        {
            if (i >= MaxListedLines)
            {
                text.AppendLine(string.Format(Strings.ExpMoreFormat, lines.Count - i));
                break;
            }

            text.AppendLine("- " + lines[i]);
        }
    }
}
