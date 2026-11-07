using System.Windows;
using EasyAI.Desktop.Infrastructure;
using EasyAI.Desktop.Resources;
using EasyAI.Desktop.ViewModels;
using EasyAI.Desktop.Views;
using EasyLabel.Core.Editing;
using EasyLabel.SegAI;
using EasyLabel.SegAI.Export;

namespace EasyAI.Desktop.Services;

/// <summary>Input of the "Shape label" dialog: the current values, the label list and the label-behaviour settings.</summary>
public sealed class LabelDialogRequest
{
    public LabelDialogRequest(ShapeLabelInfo initial, IReadOnlyList<string> labels, bool showTextField, string autoComplete)
    {
        Initial = initial;
        Labels = labels;
        ShowTextField = showTextField;
        AutoComplete = autoComplete;
    }

    public ShapeLabelInfo Initial { get; }

    /// <summary>NG, OK, predefined and already used labels.</summary>
    public IReadOnlyList<string> Labels { get; }

    /// <summary>False hides the label text box: a label can only be picked from the list.</summary>
    public bool ShowTextField { get; }

    /// <summary>"startsWith" or "contains": how typing filters the list.</summary>
    public string AutoComplete { get; }
}

/// <summary>What the skill editor returns: the skill name (as typed when adding) and the SKILL.md text.</summary>
public sealed class SkillEditResult
{
    public SkillEditResult(string name, string text)
    {
        Name = name;
        Text = text;
    }

    public string Name { get; }

    public string Text { get; }
}

/// <summary>Modal questions and pickers the view models need (kept behind an interface so the view models stay free of windows).</summary>
public interface IDialogService
{
    /// <summary>Save / Discard / Cancel prompt for unsaved edits of <paramref name="imageName"/>; without <paramref name="allowDiscard"/> only Save and Cancel are offered.</summary>
    UnsavedChoice AskUnsaved(string imageName, bool allowDiscard);

    /// <summary>Confirmation that lists the annotation files that will be deleted.</summary>
    bool ConfirmDeleteAnnotations(string imageName, IReadOnlyList<string> files);

    /// <summary>Small Yes / No question with a delete icon, placed on the selected row of the image list.</summary>
    bool ConfirmCompact(string question);

    /// <summary>Confirms deleting an image together with its label files (all go to the Recycle Bin); the list names every file.</summary>
    bool ConfirmDeleteImage(string imageName, IReadOnlyList<string> files);

    /// <summary>Confirmation that lists the information a lossy shape conversion loses.</summary>
    bool ConfirmConvertShapes(string title, string warnings);

    /// <summary>Save file dialog for a LabelMe JSON; null when cancelled.</summary>
    string? PickSavePath(string suggestedFileName, string? initialDirectory);

    /// <summary>Folder picker; null when cancelled.</summary>
    string? PickFolder(string title, string? initialDirectory);

    void ShowSettings();

    /// <summary>Settings dialog opened on one page (for example the Camera page); an unknown id opens the first page.</summary>
    void ShowSettingsPage(string pageId);

    void ShowGuide();

    /// <summary>"Shape label" dialog; null when cancelled.</summary>
    ShapeLabelInfo? AskLabel(LabelDialogRequest request);

    /// <summary>Brightness / contrast dialog (edits the view state of the canvas live).</summary>
    void ShowBrightness();

    /// <summary>Class table editor of the project; true when the table was saved (the canvas must reload its classes).</summary>
    bool EditClassTable(SegAiSession session);

    /// <summary>"New label" prompt: the trimmed name, or null when cancelled.</summary>
    string? AskNewLabel();

    /// <summary>Skill editor of the Settings window: the SKILL.md text (and the name while adding); null when cancelled.</summary>
    SkillEditResult? EditSkill(string title, string name, string text, bool nameEditable);

    /// <summary>Confirmation before a skill folder is deleted from disk.</summary>
    bool ConfirmDeleteSkill(string name);

    /// <summary>Add / edit dialog of an external MCP server entry; the validated entry (never approved), or null when cancelled.</summary>
    EasyAI.Desktop.Services.Agent.McpServerEntry? EditMcpServer(string title, EasyAI.Desktop.Services.Agent.McpServerEntry? initial);

    /// <summary>Approval dialog showing all of an external MCP server entry; true only when the user clicked Approve.</summary>
    bool ApproveMcpServer(EasyAI.Desktop.Services.Agent.McpServerEntry entry);

    /// <summary>Two-button confirmation (the primary button text names the action); true when the primary button was pressed.</summary>
    bool Confirm(string title, string message, string primaryText);

    /// <summary>Export dialog (format, set, output folder) for the open project; returns when it is closed.</summary>
    void ShowExport(string projectRoot, ExportFormat? presetFormat = null, EasyLabel.Core.ProjectPaths? paths = null);

    /// <summary>Import LabelMe JSON dialog (scan, preview, conflict choice, import) for the open project; returns when it is closed.</summary>
    void ShowImportLabelMe(string projectRoot, EasyLabel.Core.ProjectPaths? paths = null);

    /// <summary>LabelMe folder to YOLO conversion dialog; <paramref name="projectRoot"/> (may be null) only pre-fills the class list.</summary>
    void ShowConvertYolo(string? projectRoot, EasyLabel.Core.ProjectPaths? paths = null);

    /// <summary>"Self-learn" window: monitor an auto-loop run (report.json / autoloop.log, Stop) and run OBB detection on a folder of test images. Non-modal.</summary>
    void ShowSelfLearn(string? projectRoot);

    /// <summary>New Project dialog (name, location, optional image folder); null when cancelled.</summary>
    NewProjectInput? AskNewProject(string suggestedName, string initialLocation);

    /// <summary>
    /// Save Project As dialog: collects name, location and the copy option, runs <paramref name="work"/> on a worker with progress and
    /// Cancel, and returns the new project; null when cancelled or the copy was cancelled.
    /// </summary>
    EasyLabel.Core.Projects.ProjectOpenResult? RunSaveProjectAs(string suggestedName, string initialLocation, ProjectDialogViewModel.SaveAsWork work);

    /// <summary>Open file dialog for a <c>.easyai</c> project file; null when cancelled.</summary>
    string? PickProjectFile(string? initialDirectory);

    /// <summary>Open file dialog for a model pack (zip); null when cancelled.</summary>
    string? PickModelPack(string title, string? initialDirectory);
}

public sealed class WpfDialogService : IDialogService
{
    private readonly Window _owner;
    private readonly MainViewModel _main;
    private readonly SettingsStore _store;

    public WpfDialogService(Window owner, MainViewModel main, SettingsStore store)
    {
        _owner = owner;
        _main = main;
        _store = store;
    }

    public UnsavedChoice AskUnsaved(string imageName, bool allowDiscard)
    {
        PromptWindow window = new PromptWindow(
            Strings.DlgUnsavedTitle,
            string.Format(Strings.DlgUnsavedMessageFormat, imageName),
            null,
            Strings.DlgSave,
            allowDiscard ? Strings.DlgDiscard : null,
            Strings.Cancel) { Owner = _owner };
        window.ShowDialog();
        switch (window.Result)
        {
            case PromptResult.Primary: return UnsavedChoice.Save;
            case PromptResult.Secondary: return UnsavedChoice.Discard;
            default: return UnsavedChoice.Cancel;
        }
    }

    public bool ConfirmDeleteAnnotations(string imageName, IReadOnlyList<string> files)
    {
        PromptWindow window = new PromptWindow(
            Strings.DlgDeleteTitle,
            string.Format(Strings.DlgDeleteMessageFormat, imageName),
            string.Join(Environment.NewLine, files.Select(f => System.IO.Path.GetFileName(f))),
            Strings.DlgDelete,
            null,
            Strings.Cancel) { Owner = _owner };
        window.ShowDialog();
        return window.Result == PromptResult.Primary;
    }

    public bool ConfirmCompact(string question)
    {
        PromptWindow window = new PromptWindow(question, Strings.DlgYes, Strings.DlgNo, RowRect(out System.Windows.Rect bounds), bounds) { Owner = _owner };
        window.ShowDialog();
        return window.Result == PromptResult.Primary;
    }

    public bool ConfirmDeleteImage(string imageName, IReadOnlyList<string> files)
    {
        // Small Yes / No question placed on the selected row of the image list (centred on the window when the row is not visible).
        PromptWindow window = new PromptWindow(
            string.Format(Strings.DlgDeleteImageQuestionFormat, imageName),
            Strings.DlgYes,
            Strings.DlgNo,
            RowRect(out System.Windows.Rect bounds),
            bounds) { Owner = _owner };
        window.ShowDialog();
        return window.Result == PromptResult.Primary;
    }

    /// <summary>Screen rectangle (device-independent units) of the selected row of the image list, and of the main window in <paramref name="bounds"/>.</summary>
    private System.Windows.Rect RowRect(out System.Windows.Rect bounds)
    {
        bounds = new System.Windows.Rect(_owner.Left, _owner.Top, _owner.ActualWidth, _owner.ActualHeight);
        System.Windows.Rect fallback = new System.Windows.Rect(bounds.Left + bounds.Width / 2 - 100, bounds.Top + bounds.Height / 2 - 20, 200, 24);
        try
        {
            System.Windows.Controls.ListView? list = FindImageList(_owner);
            if (list == null || list.SelectedItem == null)
            {
                return fallback;
            }

            System.Windows.FrameworkElement? row = list.ItemContainerGenerator.ContainerFromItem(list.SelectedItem) as System.Windows.FrameworkElement;
            System.Windows.PresentationSource? source = System.Windows.PresentationSource.FromVisual(_owner);
            if (row == null || source == null || source.CompositionTarget == null || !row.IsVisible)
            {
                return fallback;
            }

            System.Windows.Point topLeft = source.CompositionTarget.TransformFromDevice.Transform(row.PointToScreen(new System.Windows.Point(0, 0)));
            System.Windows.Point bottomRight = source.CompositionTarget.TransformFromDevice.Transform(row.PointToScreen(new System.Windows.Point(row.ActualWidth, row.ActualHeight)));
            return new System.Windows.Rect(topLeft, bottomRight);
        }
        catch (InvalidOperationException)
        {
            return fallback;
        }
    }

    private static System.Windows.Controls.ListView? FindImageList(System.Windows.DependencyObject node)
    {
        if (node is System.Windows.Controls.ListView list && System.Windows.Automation.AutomationProperties.GetAutomationId(list) == "ImageList")
        {
            return list;
        }

        int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(node);
        for (int i = 0; i < count; i++)
        {
            System.Windows.Controls.ListView? found = FindImageList(System.Windows.Media.VisualTreeHelper.GetChild(node, i));
            if (found != null)
            {
                return found;
            }
        }

        return null;
    }

    public bool ConfirmConvertShapes(string title, string warnings)
    {
        PromptWindow window = new PromptWindow(
            title,
            warnings,
            null,
            Strings.ConvertShapeConfirm,
            null,
            Strings.Cancel) { Owner = _owner };
        window.ShowDialog();
        return window.Result == PromptResult.Primary;
    }

    public string? PickSavePath(string suggestedFileName, string? initialDirectory)
    {
        Microsoft.Win32.SaveFileDialog dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = Strings.DlgSaveAsTitle,
            Filter = Strings.DlgSaveAsFilter,
            FileName = suggestedFileName,
            DefaultExt = ".json",
            AddExtension = true,
            OverwritePrompt = true,
        };
        if (!string.IsNullOrWhiteSpace(initialDirectory) && Directory.Exists(initialDirectory))
        {
            dialog.InitialDirectory = initialDirectory;
        }

        return dialog.ShowDialog(_owner) == true ? dialog.FileName : null;
    }

    public string? PickFolder(string title, string? initialDirectory)
    {
        Microsoft.Win32.OpenFolderDialog dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = title,
            Multiselect = false,
        };
        if (!string.IsNullOrWhiteSpace(initialDirectory) && Directory.Exists(initialDirectory))
        {
            dialog.InitialDirectory = initialDirectory;
        }

        return dialog.ShowDialog(_owner) == true && !string.IsNullOrWhiteSpace(dialog.FolderName) ? dialog.FolderName : null;
    }

    public void ShowSettings()
    {
        SettingsWindow window = new SettingsWindow(new SettingsViewModel(_main, _store)) { Owner = _owner };
        window.ShowDialog();
    }

    public void ShowSettingsPage(string pageId)
    {
        SettingsViewModel viewModel = new SettingsViewModel(_main, _store);
        SettingsWindow window = new SettingsWindow(viewModel) { Owner = _owner };
        SettingPageModel? page = viewModel.Pages.FirstOrDefault(p => string.Equals(p.Id, pageId, StringComparison.Ordinal));
        if (page != null)
        {
            // The window already listens for the scroll request, so the page is scrolled into view once it is laid out.
            viewModel.SelectedPage = page;
        }

        window.ShowDialog();
    }

    public void ShowGuide()
    {
        GuideWindow window = new GuideWindow { Owner = _owner };
        window.ShowDialog();
    }

    public ShapeLabelInfo? AskLabel(LabelDialogRequest request)
    {
        LabelDialog window = new LabelDialog(request) { Owner = _owner };
        window.ShowDialog();
        return window.Result;
    }

    public void ShowBrightness()
    {
        BrightnessWindow window = new BrightnessWindow { Owner = _owner, DataContext = _main.Canvas };
        window.ShowDialog();
    }

    public void ShowExport(string projectRoot, ExportFormat? presetFormat = null, EasyLabel.Core.ProjectPaths? paths = null)
    {
        var export = new ExportViewModel(projectRoot, this, _store, presetFormat, paths);
        export.EasyVisionSent -= OnExportEasyVisionSent;
        export.EasyVisionSent += OnExportEasyVisionSent;
        ExportWindow window = new ExportWindow(export) { Owner = _owner };
        window.ShowDialog();
    }

    // A successful "Send to EasyVision" from the Export dialog completes Easy step 5 (the dialog itself is modal, the strip is on the main window).
    private void OnExportEasyVisionSent(object? sender, EventArgs e)
    {
        _main.NoteEasySent();
    }

    public void ShowImportLabelMe(string projectRoot, EasyLabel.Core.ProjectPaths? paths = null)
    {
        ImportLabelMeWindow window = new ImportLabelMeWindow(new ImportLabelMeViewModel(projectRoot, this, _store, paths)) { Owner = _owner };
        window.ShowDialog();
    }

    public void ShowConvertYolo(string? projectRoot, EasyLabel.Core.ProjectPaths? paths = null)
    {
        ConvertYoloWindow window = new ConvertYoloWindow(new ConvertYoloViewModel(projectRoot, this, _store, paths)) { Owner = _owner };
        window.ShowDialog();
    }

    public void ShowSelfLearn(string? projectRoot)
    {
        // Not modal: the user keeps working (or the Agent keeps labelling) while the run is watched.
        SelfLearnWindow window = new SelfLearnWindow(new SelfLearnViewModel(projectRoot, this, _store)) { Owner = _owner };
        window.Show();
    }

    public NewProjectInput? AskNewProject(string suggestedName, string initialLocation)
    {
        ProjectDialogViewModel model = new ProjectDialogViewModel(ProjectDialogMode.New, suggestedName, initialLocation, PickFolder, null);
        ProjectDialog window = new ProjectDialog(model) { Owner = _owner };
        return window.ShowDialog() == true ? model.Input : null;
    }

    public EasyLabel.Core.Projects.ProjectOpenResult? RunSaveProjectAs(string suggestedName, string initialLocation, ProjectDialogViewModel.SaveAsWork work)
    {
        ProjectDialogViewModel model = new ProjectDialogViewModel(ProjectDialogMode.SaveAs, suggestedName, initialLocation, PickFolder, work);
        ProjectDialog window = new ProjectDialog(model) { Owner = _owner };
        return window.ShowDialog() == true ? model.Result : null;
    }

    public string? PickProjectFile(string? initialDirectory)
    {
        Microsoft.Win32.OpenFileDialog dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = Strings.ProjOpenDialogTitle,
            Filter = Strings.ProjOpenFilter,
            Multiselect = false,
            CheckFileExists = true,
        };
        if (!string.IsNullOrWhiteSpace(initialDirectory) && Directory.Exists(initialDirectory))
        {
            dialog.InitialDirectory = initialDirectory;
        }

        return dialog.ShowDialog(_owner) == true && !string.IsNullOrWhiteSpace(dialog.FileName) ? dialog.FileName : null;
    }

    public string? PickModelPack(string title, string? initialDirectory)
    {
        Microsoft.Win32.OpenFileDialog dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = title,
            Filter = "Model pack (*.zip)|*.zip",
            Multiselect = false,
            CheckFileExists = true,
        };
        if (!string.IsNullOrWhiteSpace(initialDirectory) && Directory.Exists(initialDirectory))
        {
            dialog.InitialDirectory = initialDirectory;
        }

        return dialog.ShowDialog(Application.Current?.Windows.OfType<SettingsWindow>().FirstOrDefault() ?? _owner) == true && !string.IsNullOrWhiteSpace(dialog.FileName) ? dialog.FileName : null;
    }

    public bool EditClassTable(SegAiSession session)
    {
        ClassTableDialog window = new ClassTableDialog(new ClassTableViewModel(session)) { Owner = _owner };
        return window.ShowDialog() == true;
    }

    public string? AskNewLabel()
    {
        NewLabelDialog window = new NewLabelDialog { Owner = _owner };
        return window.ShowDialog() == true ? window.Result : null;
    }

    public SkillEditResult? EditSkill(string title, string name, string text, bool nameEditable)
    {
        SkillEditDialog window = new SkillEditDialog(title, name, text, nameEditable) { Owner = Application.Current?.Windows.OfType<SettingsWindow>().FirstOrDefault() ?? _owner };
        return window.ShowDialog() == true ? window.Result : null;
    }

    public EasyAI.Desktop.Services.Agent.McpServerEntry? EditMcpServer(string title, EasyAI.Desktop.Services.Agent.McpServerEntry? initial)
    {
        McpServerEditDialog window = new McpServerEditDialog(title, initial) { Owner = Application.Current?.Windows.OfType<SettingsWindow>().FirstOrDefault() ?? _owner };
        return window.ShowDialog() == true ? window.Result : null;
    }

    public bool ApproveMcpServer(EasyAI.Desktop.Services.Agent.McpServerEntry entry)
    {
        McpApprovalDialog window = new McpApprovalDialog(entry) { Owner = Application.Current?.Windows.OfType<SettingsWindow>().FirstOrDefault() ?? _owner };
        return window.ShowDialog() == true;
    }

    public bool Confirm(string title, string message, string primaryText)
    {
        PromptWindow window = new PromptWindow(title, message, null, primaryText, null, Strings.Cancel)
        {
            Owner = Application.Current?.Windows.OfType<SettingsWindow>().FirstOrDefault() ?? _owner,
        };
        window.ShowDialog();
        return window.Result == PromptResult.Primary;
    }

    public bool ConfirmDeleteSkill(string name)
    {
        PromptWindow window = new PromptWindow(
            Strings.AgentsDelete,
            string.Format(Strings.AgentsDeleteMessageFormat, name),
            null,
            Strings.DlgDelete,
            null,
            Strings.Cancel) { Owner = Application.Current?.Windows.OfType<SettingsWindow>().FirstOrDefault() ?? _owner };
        window.ShowDialog();
        return window.Result == PromptResult.Primary;
    }
}
