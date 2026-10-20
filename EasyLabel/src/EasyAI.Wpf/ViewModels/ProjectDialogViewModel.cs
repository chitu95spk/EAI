using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EasyAI.Desktop.Infrastructure;
using EasyAI.Desktop.Resources;
using EasyLabel.Core.Projects;

namespace EasyAI.Desktop.ViewModels;

public enum ProjectDialogMode
{
    /// <summary>New Project: name, location and an optional image folder; the project is created by the caller.</summary>
    New,

    /// <summary>Save Project As: name, location and the "copy images and labels" option; the dialog runs the copy with progress and Cancel.</summary>
    SaveAs,
}

/// <summary>What the New Project dialog returns. <see cref="ImageFolder"/> is null when the project creates its own &lt;Name&gt;.images folder.</summary>
public sealed record NewProjectInput(string Name, string Location, string? ImageFolder, ProjectKind Kind = ProjectKind.Detect);

/// <summary>
/// View model of the New Project and Save Project As dialog. Live validation (<see cref="NewProjectInputRules"/>) keeps OK disabled while the
/// input is unusable. In Save As mode OK runs <c>work</c> on a worker thread with progress; Cancel (or closing the window) then cancels the copy.
/// </summary>
public sealed partial class ProjectDialogViewModel : ObservableObject
{
    public delegate ProjectOpenResult SaveAsWork(string name, string location, bool copyImages, IProgress<ProjectCopyProgress> progress, CancellationToken cancellationToken);

    private readonly Func<string, string?, string?> _pickFolder;
    private readonly SaveAsWork? _work;
    private CancellationTokenSource? _cts;
    private bool _nameTouched;
    private string _validationText = string.Empty;
    private string _runText = string.Empty;
    private bool _isValid;

    [ObservableProperty]
    private string _name;

    [ObservableProperty]
    private string _location;

    [ObservableProperty]
    private string _imageFolder = string.Empty;

    [ObservableProperty]
    private bool _copyImages;

    /// <summary>New mode: true when the project is created as Position; false (default) = Detect.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDetectKind))]
    private bool _isPositionKind;

    [ObservableProperty]
    private string _errorText = string.Empty;

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private bool _isProgressIndeterminate = true;

    [ObservableProperty]
    private double _progressValue;

    [ObservableProperty]
    private string _progressText = string.Empty;

    [ObservableProperty]
    private string _previewText = string.Empty;

    /// <param name="work">Save As only: the copy, run on a worker thread when OK is pressed. Null in New mode.</param>
    public ProjectDialogViewModel(ProjectDialogMode mode, string name, string location, Func<string, string?, string?> pickFolder, SaveAsWork? work)
    {
        Mode = mode;
        _name = name;
        _location = location;
        _pickFolder = pickFolder;
        _work = work;
        Revalidate();
    }

    public ProjectDialogMode Mode { get; }

    public bool IsNewMode => Mode == ProjectDialogMode.New;

    /// <summary>Mirror of <see cref="IsPositionKind"/> for the second radio button of the kind selector.</summary>
    public bool IsDetectKind
    {
        get => !IsPositionKind;
        set
        {
            if (value)
            {
                IsPositionKind = false;
            }
        }
    }

    public bool IsSaveAsMode => Mode == ProjectDialogMode.SaveAs;

    /// <summary>Inputs can be edited (nothing is being copied).</summary>
    public bool IsIdle => !IsRunning;

    public bool HasError => ErrorText.Length > 0;

    /// <summary>Set when the dialog closed with OK in New mode.</summary>
    public NewProjectInput? Input { get; private set; }

    /// <summary>Set when the dialog closed with OK in Save As mode: the new, already opened-able project.</summary>
    public ProjectOpenResult? Result { get; private set; }

    /// <summary>Raised when the window must close; the argument is the dialog result.</summary>
    public event EventHandler<bool>? CloseRequested;

    partial void OnNameChanged(string value)
    {
        _nameTouched = true;
        Revalidate();
    }

    partial void OnLocationChanged(string value)
    {
        Revalidate();
    }

    partial void OnImageFolderChanged(string value)
    {
        Revalidate();
    }

    partial void OnCopyImagesChanged(bool value)
    {
        Revalidate();
    }

    partial void OnIsRunningChanged(bool value)
    {
        OnPropertyChanged(nameof(IsIdle));
        OkCommand.NotifyCanExecuteChanged();
        BrowseLocationCommand.NotifyCanExecuteChanged();
        BrowseImageFolderCommand.NotifyCanExecuteChanged();
        ClearImageFolderCommand.NotifyCanExecuteChanged();
    }

    partial void OnErrorTextChanged(string value)
    {
        OnPropertyChanged(nameof(HasError));
    }

    private void Revalidate()
    {
        _runText = string.Empty;
        bool imagesFree = IsNewMode ? string.IsNullOrWhiteSpace(ImageFolder) : CopyImages;
        ProjectInputCheck check = NewProjectInputRules.Check(Name, Location, IsNewMode ? ImageFolder : null, imagesFree);
        _isValid = check.Error == ProjectInputError.None;

        ProjectMessage? message = ProjectMessages.For(check);
        // An empty name is not reported until the user has typed in the field (the dialog opens with a suggested name anyway).
        _validationText = message == null || (check.Error == ProjectInputError.NameEmpty && !_nameTouched) ? string.Empty : Format(message.Value);

        PreviewText = check.Error == ProjectInputError.None || check.Error == ProjectInputError.TargetExists
            ? SafeManifestPath()
            : string.Empty;
        UpdateErrorText();
        OkCommand.NotifyCanExecuteChanged();
    }

    private string SafeManifestPath()
    {
        try
        {
            return ProjectManager.ManifestPathFor(Location.Trim(), Name);
        }
        catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
        {
            return string.Empty;
        }
    }

    private void UpdateErrorText()
    {
        ErrorText = _runText.Length > 0 ? _runText : _validationText;
    }

    private static string Format(ProjectMessage message)
    {
        return string.Format(Strings.ByName(message.Key), message.Argument ?? string.Empty);
    }

    private bool CanOk() => _isValid && !IsRunning;

    [RelayCommand(CanExecute = nameof(CanOk))]
    private async Task OkAsync()
    {
        string name = ProjectNameRules.Normalize(Name);
        string location = Location.Trim();
        if (_work == null)
        {
            string? images = string.IsNullOrWhiteSpace(ImageFolder) ? null : ImageFolder.Trim();
            Input = new NewProjectInput(name, location, images, IsPositionKind ? ProjectKind.Position : ProjectKind.Detect);
            CloseRequested?.Invoke(this, true);
            return;
        }

        bool copy = CopyImages;
        _runText = string.Empty;
        UpdateErrorText();
        IsProgressIndeterminate = true;
        ProgressValue = 0;
        ProgressText = Strings.ProjCopyScanning;
        IsRunning = true;
        CancellationTokenSource cts = new CancellationTokenSource();
        _cts = cts;
        ProjectOpenResult? result = null;
        try
        {
            // Created on the UI thread, so the callbacks run on it as well.
            Progress<ProjectCopyProgress> progress = new Progress<ProjectCopyProgress>(OnProgress);
            CancellationToken token = cts.Token;
            SaveAsWork work = _work;
            result = await Task.Run(() => work(name, location, copy, progress, token));
        }
        catch (OperationCanceledException)
        {
            ProgressText = string.Empty;
        }
        catch (ProjectException ex)
        {
            _runText = Format(ProjectMessages.For(ex));
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            _runText = Format(new ProjectMessage(ProjectMessages.Generic, ex.Message));
        }
        finally
        {
            _cts = null;
            cts.Dispose();
            IsRunning = false;
        }

        if (result != null)
        {
            Result = result;
            CloseRequested?.Invoke(this, true);
            return;
        }

        UpdateErrorText();
    }

    private void OnProgress(ProjectCopyProgress progress)
    {
        double? fraction = ProjectCopyProgressRules.Fraction(progress);
        IsProgressIndeterminate = fraction == null;
        ProgressValue = fraction ?? 0d;
        string phase = Strings.ByName(ProjectCopyProgressRules.PhaseKey(progress.Phase));
        ProgressText = string.IsNullOrEmpty(progress.CurrentFile) ? phase : phase + " " + progress.CurrentFile;
    }

    /// <summary>Cancel button, Esc and window close: cancels a running copy, otherwise closes the dialog.</summary>
    public void RequestCancel()
    {
        CancellationTokenSource? cts = _cts;
        if (IsRunning)
        {
            try
            {
                cts?.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The copy finished at the same moment.
            }

            return;
        }

        CloseRequested?.Invoke(this, false);
    }

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private void BrowseLocation()
    {
        string? initial = Directory.Exists(Location) ? Location : null;
        string? picked = _pickFolder(Strings.ProjPickLocationTitle, initial);
        if (picked != null)
        {
            Location = picked;
        }
    }

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private void BrowseImageFolder()
    {
        string? initial = Directory.Exists(ImageFolder) ? ImageFolder : null;
        string? picked = _pickFolder(Strings.ProjPickImagesTitle, initial);
        if (picked != null)
        {
            ImageFolder = picked;
        }
    }

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private void ClearImageFolder()
    {
        ImageFolder = string.Empty;
    }
}
