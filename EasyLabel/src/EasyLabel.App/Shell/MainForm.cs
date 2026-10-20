using System.Globalization;
using EasyLabel.Ai.Detect;
using EasyLabel.Ai.Runtime;
using EasyLabel.App.Canvas;
using EasyLabel.App.Canvas.Tools;
using EasyLabel.App.Dialogs;
using EasyLabel.Core;
using EasyLabel.Core.Batch;
using EasyLabel.Core.Commands;
using EasyLabel.Core.Convert;
using EasyLabel.Core.Io;
using EasyLabel.Core.Model;
using EasyLabel.Core.Package;
using EasyLabel.Vision;
using EasyLabel.Vision.Augment;

namespace EasyLabel.App.Shell;

public sealed class MainForm : Form
{
    private readonly ProjectService _projectService = new();
    private readonly MenuStrip _menuStrip = new();
    private readonly ToolStripMenuItem _fileMenuItem = new("&File");
    private readonly ToolStripMenuItem _openFolderMenuItem = new("&Open Folder...");
    private readonly ToolStripMenuItem _exitMenuItem = new("E&xit");
    private readonly ToolStripMenuItem _toolsMenuItem = new("&Tools");
    private readonly ToolStripMenuItem _standaloneConvertMenuItem = new("&Convert Labels...");
    private readonly ToolStripMenuItem _classRemapMenuItem = new("&Class Remap...");
    private readonly ToolStripMenuItem _bulkLabelEditMenuItem = new("&Bulk Label Edit...");
    private readonly ToolStripMenuItem _augmentMenuItem = new("&Clone && Augment...");
    private readonly ToolStripMenuItem _ultralyticsExportMenuItem = new("&Export Ultralytics...");
    private readonly ToolStripMenuItem _datasetPackageMenuItem = new("&Package Dataset...");
    private readonly ToolStrip _toolStrip = new();
    private readonly ToolStrip _aiToolStrip = new ToolStrip();
    private readonly ToolbarIconFactory _toolbarIcons = new ToolbarIconFactory();
    private readonly ToolStripButton _openFolderButton = new ToolStripButton();
    private readonly ToolStripButton _previousImageButton = new ToolStripButton();
    private readonly ToolStripButton _nextImageButton = new ToolStripButton();
    private readonly ToolStripButton _undoButton = new ToolStripButton();
    private readonly ToolStripButton _redoButton = new ToolStripButton();
    private readonly ToolStripButton _selectButton = new("Select");
    private readonly ToolStripButton _rectangleButton = new("Rectangle");
    private readonly ToolStripButton _rotationButton = new("Rotation");
    private readonly ToolStripButton _polygonButton = new ToolStripButton("Polygon");
    private readonly ToolStripButton _editShapesButton = new ToolStripButton("Edit Shapes");
    private readonly ToolStripButton _duplicateButton = new ToolStripButton("Duplicate");
    private readonly ToolStripButton _editLabelButton = new ToolStripButton("Edit Label");
    private readonly ToolStripButton _addVertexButton = new ToolStripButton("Add Vertex");
    private readonly ToolStripButton _removeVertexButton = new ToolStripButton("Remove Vertex");
    private readonly ToolStripButton _deleteButton = new ToolStripButton();
    private readonly ToolStripButton _zoomOutButton = new ToolStripButton();
    private readonly ToolStripButton _fitButton = new("Fit");
    private readonly ToolStripButton _zoomInButton = new ToolStripButton();
    private readonly ToolStripButton _saveButton = new("Save");
    private readonly ToolStripButton _importModelButton = new("Import Model");
    private readonly ToolStripButton _detectCurrentButton = new("Detect Current");
    private readonly ToolStripButton _detectFolderButton = new("Detect Folder");
    private readonly ToolStripButton _detectUnlabeledButton = new("Detect Unlabeled");
    private readonly ToolStripButton _cancelDetectButton = new("Cancel");
    private readonly ToolStripLabel _confLabel = new("Conf");
    private readonly ToolStripTextBox _confTextBox = new() { Text = "0.25", Width = 48 };
    private readonly ToolStripLabel _iouLabel = new("IoU");
    private readonly ToolStripTextBox _iouTextBox = new() { Text = "0.70", Width = 48 };
    private readonly SplitContainer _mainSplitContainer = new();
    private readonly ListView _imageListView = new();
    private readonly TabControl _workspaceTabs = new TabControl();
    private readonly ListBox _labelsListBox = new ListBox();
    private readonly ListView _shapesListView = new ListView();
    private readonly Panel _aiWorkspacePanel = new Panel();
    private readonly CanvasControl _canvasControl = new();
    private readonly StatusStrip _statusStrip = new();
    private readonly ToolStripStatusLabel _statusLabel = new();
    private readonly ToolStripProgressBar _detectProgressBar = new() { Minimum = 0, Maximum = 1, Value = 0, Visible = false };
    private readonly ImageLoader _imageLoader = new();
    private readonly LabelMeIO _labelMeIO = new();
    private readonly SidecarIO _sidecarIO = new();
    private readonly DetectService _detectService = new();
    private readonly ClassRemapService _classRemapService = new();
    private readonly BulkLabelEditService _bulkLabelEditService = new();
    private readonly AugmentService _augmentService = new AugmentService(new OpenCvAugmentImageWriter());
    private readonly StandaloneConvertService _standaloneConvertService = new StandaloneConvertService(new OpenCvImageInfoReader());
    private readonly UltralyticsExportService _ultralyticsExportService = new UltralyticsExportService();
    private readonly DatasetPackageService _datasetPackageService = new DatasetPackageService();
    private readonly string? _startupProjectPath;

    private Project? _project;
    private ImageItem? _currentImage;
    private LabelMeDocument? _currentDocument;
    private EasyLabelSidecar? _currentSidecar;
    private bool _documentDirty;
    private readonly HotkeyMap _hotkeys = new HotkeyMap();
    private readonly AutoSaveService _autoSave = new AutoSaveService();
    private readonly System.Windows.Forms.Timer _autoSaveTimer = new System.Windows.Forms.Timer { Interval = 30000 };
    private bool _switchingImage;
    private bool _savingRecovery;
    private bool _closeApproved;
    private bool _closingPrompt;
    private bool _suppressImageSelectionChanged;
    private bool _suppressShapeSelectionChanged;
    private string? _modelPath;
    private CancellationTokenSource? _detectCancellation;

    private enum UnsavedDocumentDecision
    {
        Save,
        Discard,
        Cancel
    }

    private enum DetectScope
    {
        Current,
        Folder,
        OnlyUnlabeled
    }

    public MainForm(string? startupProjectPath = null)
    {
        _startupProjectPath = string.IsNullOrWhiteSpace(startupProjectPath) ? null : startupProjectPath;

        Text = "EasyLabel";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(960, 640);
        ClientSize = new Size(1280, 800);

        InitializeLayout();
        InitializeEditMenu();
        WireEvents();
        _autoSaveTimer.Tick -= AutoSaveTimer_Tick;
        _autoSaveTimer.Tick += AutoSaveTimer_Tick;
        _autoSaveTimer.Start();
    }

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        ConfigureWorkspaceSplitter();

        if (_startupProjectPath is not null)
        {
            await LoadProjectAsync(_startupProjectPath);
        }
    }

    protected override async void OnFormClosing(FormClosingEventArgs e)
    {
        if (_closeApproved)
        {
            base.OnFormClosing(e);
            return;
        }
        e.Cancel = true;
        if (_detectCancellation != null)
        {
            _detectCancellation.Cancel();
            _statusLabel.Text = "Canceling detect; close again when it finishes.";
            return;
        }
        if (_closingPrompt || _switchingImage) return;
        _closingPrompt = true;
        UpdatePrimaryToolbarEnabled();
        _canvasControl.CancelGesture();
        try
        {
            if (!await ConfirmSaveOrDiscardCurrentDocument()) return;
            _closeApproved = true;
            BeginInvoke(new Action(Close));
        }
        finally
        {
            _closingPrompt = false;
            UpdatePrimaryToolbarEnabled();
        }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _autoSaveTimer.Stop();
        _autoSaveTimer.Tick -= AutoSaveTimer_Tick;
        _autoSaveTimer.Dispose();
        base.OnFormClosed(e);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        ConfigureWorkspaceSplitter();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            ClearToolbarImageReferences();
            _toolbarIcons.Dispose();
        }

        base.Dispose(disposing);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        // Text editors retain their native clipboard, deletion and digit behavior.
        if (_confTextBox.Focused || _iouTextBox.Focused || ActiveControl is TextBoxBase)
            return base.ProcessCmdKey(ref msg, keyData);
        EditorAction action;
        int classIndex = _hotkeys.GetClassIndex(keyData);
        if (!_hotkeys.TryGetAction(keyData, out action) && classIndex < 0)
            return base.ProcessCmdKey(ref msg, keyData);
        if (_switchingImage || _closingPrompt || _detectCancellation != null || UseWaitCursor) return true;
        if (classIndex >= 0)
        {
            if (_project != null && classIndex < _project.Classes.Count)
            {
                string label = _project.Classes[classIndex];
                _canvasControl.ActiveLabel = label;
                SelectActiveLabel(label);
                _statusLabel.Text = "Class: " + label;
            }
            return true;
        }
        ExecuteEditorAction(action);
        return true;
    }

    private void ExecuteEditorAction(EditorAction action)
    {
        switch (action)
        {
            case EditorAction.Undo: _canvasControl.Undo(); break;
            case EditorAction.Redo: _canvasControl.Redo(); break;
            case EditorAction.Delete: _canvasControl.DeleteSelectedShape(); break;
            case EditorAction.Copy: _canvasControl.CopySelected(); break;
            case EditorAction.Paste: _canvasControl.Paste(); break;
            case EditorAction.Duplicate: _canvasControl.DuplicateSelected(); break;
            case EditorAction.EditLabel: _canvasControl.EditSelectedLabel(); break;
            case EditorAction.AddVertex: _canvasControl.AddVertexToSelectedPolygon(); break;
            case EditorAction.RemoveVertex: _canvasControl.RemoveSelectedPolygonVertex(); break;
            case EditorAction.Save: SaveButton_Click(this, EventArgs.Empty); break;
            case EditorAction.PreviousImage: NavigateImage(-1); break;
            case EditorAction.NextImage: NavigateImage(1); break;
            case EditorAction.SelectTool: SetTool(CanvasTool.Select); break;
            case EditorAction.EditTool: SetTool(CanvasTool.Edit); break;
            case EditorAction.RectangleTool: SetTool(CanvasTool.Rectangle); break;
            case EditorAction.RotationTool: SetTool(CanvasTool.Rotation); break;
            case EditorAction.PolygonTool: SetTool(CanvasTool.Polygon); break;
            case EditorAction.Fit: _canvasControl.FitToWindow(); break;
            case EditorAction.Cancel: _canvasControl.CancelGesture(); break;
        }
    }

    private void InitializeEditMenu()
    {
        ToolStripMenuItem editMenu = new ToolStripMenuItem("&Edit");
        foreach (EditorAction action in new[] { EditorAction.Undo, EditorAction.Redo,
            EditorAction.Copy, EditorAction.Paste, EditorAction.Duplicate, EditorAction.EditLabel,
            EditorAction.AddVertex, EditorAction.RemoveVertex, EditorAction.Delete })
        {
            ToolStripMenuItem item = new ToolStripMenuItem(action.ToString())
            {
                Tag = action,
                ShortcutKeyDisplayString = new KeysConverter().ConvertToString(_hotkeys.GetKeys(action))
            };
            item.Click -= EditMenuItem_Click;
            item.Click += EditMenuItem_Click;
            editMenu.DropDownItems.Add(item);
        }
        editMenu.DropDownOpening -= EditMenu_DropDownOpening;
        editMenu.DropDownOpening += EditMenu_DropDownOpening;
        _menuStrip.Items.Insert(1, editMenu);
    }

    private void EditMenu_DropDownOpening([System.Diagnostics.CodeAnalysis.AllowNull] object sender, EventArgs e)
    {
        if (!(sender is ToolStripMenuItem menu)) return;
        foreach (ToolStripMenuItem item in menu.DropDownItems)
        {
            if (!(item.Tag is EditorAction action)) continue;
            item.Enabled = _currentDocument != null && !_switchingImage && _detectCancellation == null
                && (action != EditorAction.Undo || _canvasControl.Editor.History.CanUndo)
                && (action != EditorAction.Redo || _canvasControl.Editor.History.CanRedo);
        }
    }

    private void EditMenuItem_Click([System.Diagnostics.CodeAnalysis.AllowNull] object sender, EventArgs e)
    {
        if (!(sender is ToolStripMenuItem item) || !(item.Tag is EditorAction action)) return;
        Message message = new Message();
        ProcessCmdKey(ref message, _hotkeys.GetKeys(action));
    }

    private void NavigateImage(int delta)
    {
        if (_currentImage == null) return;
        var item = FindImageListViewItem(_currentImage);
        if (item == null) return;
        int index = item.Index + delta;
        if (index < 0 || index >= _imageListView.Items.Count) return;
        _imageListView.Items[index].Selected = true;
        _imageListView.Items[index].EnsureVisible();
    }

    private async void AutoSaveTimer_Tick([System.Diagnostics.CodeAnalysis.AllowNull] object sender, EventArgs e)
    {
        if (_savingRecovery || _switchingImage || _closingPrompt || _detectCancellation != null) return;
        _savingRecovery = true;
        try { await SaveRecoveryAsync(); }
        finally { _savingRecovery = false; }
    }

    private async Task<bool> SaveRecoveryAsync()
    {
        if (_project == null || _currentImage == null
            || _currentDocument == null || _currentSidecar == null) return true;
        try
        {
            if (_documentDirty)
                await _autoSave.SaveAsync(_project.Paths, _currentImage.ImagePath, _currentDocument, _currentSidecar);
            else
                await _autoSave.ClearAsync(_project.Paths, _currentImage.ImagePath);
            return true;
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException)
        {
            _statusLabel.Text = "Recovery save failed: " + ex.Message;
            return false;
        }
    }

    private void InitializeLayout()
    {
        _fileMenuItem.DropDownItems.AddRange(new ToolStripItem[]
        {
            _openFolderMenuItem,
            new ToolStripSeparator(),
            _exitMenuItem
        });
        _classRemapMenuItem.Enabled = false;
        _bulkLabelEditMenuItem.Enabled = false;
        _augmentMenuItem.Enabled = false;
        _ultralyticsExportMenuItem.Enabled = false;
        _datasetPackageMenuItem.Enabled = false;
        _toolsMenuItem.DropDownItems.AddRange(new ToolStripItem[]
        {
            _standaloneConvertMenuItem,
            new ToolStripSeparator(),
            _classRemapMenuItem,
            _bulkLabelEditMenuItem,
            new ToolStripSeparator(),
            _augmentMenuItem,
            _ultralyticsExportMenuItem,
            _datasetPackageMenuItem
        });
        _menuStrip.Items.Add(_fileMenuItem);
        _menuStrip.Items.Add(_toolsMenuItem);
        MainMenuStrip = _menuStrip;

        _toolStrip.GripStyle = ToolStripGripStyle.Hidden;
        _toolStrip.CanOverflow = true;
        _toolStrip.ImageScalingSize = new Size(20, 20);
        _toolStrip.AutoSize = true;
        _toolStrip.LayoutStyle = ToolStripLayoutStyle.VerticalStackWithOverflow;
        _toolStrip.Padding = new Padding(2);
        _toolStrip.BackColor = Color.FromArgb(242, 244, 248);

        ConfigureIconButton(_openFolderButton, ToolbarIcon.OpenFolder, "Open Folder");
        ConfigureIconButton(_previousImageButton, ToolbarIcon.PreviousImage, "Previous Image (PageUp)");
        ConfigureIconButton(_nextImageButton, ToolbarIcon.NextImage, "Next Image (PageDown)");
        ConfigureIconButton(_saveButton, ToolbarIcon.Save, "Save (Ctrl+S)");
        ConfigureIconButton(_undoButton, ToolbarIcon.Undo, "Undo (Ctrl+Z)");
        ConfigureIconButton(_redoButton, ToolbarIcon.Redo, "Redo (Ctrl+Y)");
        ConfigureIconButton(_selectButton, ToolbarIcon.Select, "Select (V)");
        ConfigureIconButton(_rectangleButton, ToolbarIcon.Rectangle, "Rectangle (R)");
        ConfigureIconButton(_rotationButton, ToolbarIcon.OrientedRectangle, "Oriented Rectangle (O)");
        ConfigureTextButton(_polygonButton, "Polygon (P)");
        ConfigureTextButton(_editShapesButton, "Edit Shapes (E)");
        ConfigureTextButton(_duplicateButton, "Duplicate selected shape (Ctrl+D)");
        ConfigureTextButton(_editLabelButton, "Edit selected label");
        ConfigureTextButton(_addVertexButton, "Add polygon vertex");
        ConfigureTextButton(_removeVertexButton, "Remove polygon vertex");
        ConfigureIconButton(_deleteButton, ToolbarIcon.Delete, "Delete (Delete)");
        ConfigureIconButton(_zoomOutButton, ToolbarIcon.ZoomOut, "Zoom Out");
        ConfigureIconButton(_fitButton, ToolbarIcon.Fit, "Fit to Window (F)");
        ConfigureIconButton(_zoomInButton, ToolbarIcon.ZoomIn, "Zoom In");

        _selectButton.CheckOnClick = true;
        _rectangleButton.CheckOnClick = true;
        _rotationButton.CheckOnClick = true;
        _polygonButton.CheckOnClick = true;
        _editShapesButton.CheckOnClick = true;

        _toolStrip.Items.AddRange(new ToolStripItem[]
        {
            _openFolderButton,
            new ToolStripSeparator(),
            _previousImageButton,
            _nextImageButton,
            new ToolStripSeparator(),
            _saveButton,
            _undoButton,
            _redoButton,
            new ToolStripSeparator(),
            _selectButton,
            _editShapesButton,
            _rectangleButton,
            _rotationButton,
            _polygonButton,
            new ToolStripSeparator(),
            _editLabelButton,
            _duplicateButton,
            _addVertexButton,
            _removeVertexButton,
            _deleteButton,
            new ToolStripSeparator(),
            _zoomOutButton,
            _fitButton,
            _zoomInButton
        });

        _aiToolStrip.GripStyle = ToolStripGripStyle.Hidden;
        _aiToolStrip.Padding = new Padding(4, 1, 4, 1);
        _aiToolStrip.BackColor = Color.FromArgb(250, 250, 250);
        _importModelButton.DisplayStyle = ToolStripItemDisplayStyle.Text;
        _detectCurrentButton.DisplayStyle = ToolStripItemDisplayStyle.Text;
        _detectFolderButton.DisplayStyle = ToolStripItemDisplayStyle.Text;
        _detectUnlabeledButton.DisplayStyle = ToolStripItemDisplayStyle.Text;
        _cancelDetectButton.DisplayStyle = ToolStripItemDisplayStyle.Text;
        _cancelDetectButton.Enabled = false;
        _aiToolStrip.Items.AddRange(new ToolStripItem[]
        {
            new ToolStripLabel("AI"),
            new ToolStripSeparator(),
            _importModelButton,
            _detectCurrentButton,
            _detectFolderButton,
            _detectUnlabeledButton,
            _cancelDetectButton,
            new ToolStripSeparator(),
            _confLabel,
            _confTextBox,
            _iouLabel,
            _iouTextBox
        });
        SetTool(CanvasTool.Select);
        SetDetectButtonsEnabled(true);

        _imageListView.Dock = DockStyle.Fill;
        _imageListView.FullRowSelect = true;
        _imageListView.HideSelection = false;
        _imageListView.MultiSelect = false;
        _imageListView.View = View.Details;
        _imageListView.Columns.Add("Image", 260);
        _imageListView.Columns.Add("Status", 110);
        _imageListView.Columns.Add("Labels", 80);

        _canvasControl.Dock = DockStyle.Fill;
        _shapesListView.Dock = DockStyle.Fill;
        _shapesListView.View = View.Details;
        _shapesListView.FullRowSelect = true;
        _shapesListView.HideSelection = false;
        _shapesListView.MultiSelect = false;
        _shapesListView.Columns.Add("Shape", 82);
        _shapesListView.Columns.Add("Label", 150);
        _labelsListBox.Dock = DockStyle.Fill;
        _aiToolStrip.Dock = DockStyle.Top;
        _aiWorkspacePanel.Dock = DockStyle.Fill;
        _aiWorkspacePanel.Controls.Add(_aiToolStrip);

        TabPage labelsTab = new TabPage("Labels");
        labelsTab.Controls.Add(_labelsListBox);
        TabPage shapesTab = new TabPage("Shapes");
        shapesTab.Controls.Add(_shapesListView);
        TabPage filesTab = new TabPage("Files");
        filesTab.Controls.Add(_imageListView);
        TabPage aiTab = new TabPage("AI");
        aiTab.Controls.Add(_aiWorkspacePanel);
        _workspaceTabs.Dock = DockStyle.Fill;
        _workspaceTabs.TabPages.AddRange(new[] { labelsTab, shapesTab, filesTab, aiTab });

        _mainSplitContainer.Dock = DockStyle.Fill;
        _mainSplitContainer.FixedPanel = FixedPanel.Panel2;
        _mainSplitContainer.Panel1.Controls.Add(_canvasControl);
        _mainSplitContainer.Panel2.Controls.Add(_workspaceTabs);

        _statusLabel.Text = "No project open";
        _statusStrip.Items.Add(_statusLabel);
        _statusStrip.Items.Add(_detectProgressBar);

        Controls.Add(_mainSplitContainer);
        Controls.Add(_statusStrip);
        Controls.Add(_toolStrip);
        Controls.Add(_menuStrip);

        _toolStrip.Dock = DockStyle.Left;
        _menuStrip.Dock = DockStyle.Top;
    }

    private void ConfigureWorkspaceSplitter()
    {
        int availableWidth = _mainSplitContainer.ClientSize.Width;
        int splitterWidth = _mainSplitContainer.SplitterWidth;
        if (availableWidth <= splitterWidth)
        {
            return;
        }

        int desiredRightWidth = Math.Min(340, Math.Max(240, availableWidth / 3));
        int panel1MinSize = Math.Min(300, Math.Max(0, availableWidth - desiredRightWidth - splitterWidth));
        int panel2MinSize = Math.Min(260, Math.Max(0, availableWidth - panel1MinSize - splitterWidth));
        int maximumPanel1Width = Math.Max(panel1MinSize, availableWidth - panel2MinSize - splitterWidth);
        int desiredPanel1Width = Math.Max(panel1MinSize, availableWidth - desiredRightWidth - splitterWidth);

        _mainSplitContainer.Panel1MinSize = panel1MinSize;
        _mainSplitContainer.Panel2MinSize = panel2MinSize;
        _mainSplitContainer.SplitterDistance = Math.Min(maximumPanel1Width, desiredPanel1Width);
    }

    private void WireEvents()
    {
        _openFolderMenuItem.Click -= OpenFolderMenuItem_Click;
        _openFolderMenuItem.Click += OpenFolderMenuItem_Click;

        _openFolderButton.Click -= OpenFolderMenuItem_Click;
        _openFolderButton.Click += OpenFolderMenuItem_Click;

        _exitMenuItem.Click -= ExitMenuItem_Click;
        _exitMenuItem.Click += ExitMenuItem_Click;

        _standaloneConvertMenuItem.Click -= StandaloneConvertMenuItem_Click;
        _standaloneConvertMenuItem.Click += StandaloneConvertMenuItem_Click;

        _classRemapMenuItem.Click -= ClassRemapMenuItem_Click;
        _classRemapMenuItem.Click += ClassRemapMenuItem_Click;

        _bulkLabelEditMenuItem.Click -= BulkLabelEditMenuItem_Click;
        _bulkLabelEditMenuItem.Click += BulkLabelEditMenuItem_Click;

        _augmentMenuItem.Click -= AugmentMenuItem_Click;
        _augmentMenuItem.Click += AugmentMenuItem_Click;

        _ultralyticsExportMenuItem.Click -= UltralyticsExportMenuItem_Click;
        _ultralyticsExportMenuItem.Click += UltralyticsExportMenuItem_Click;

        _datasetPackageMenuItem.Click -= DatasetPackageMenuItem_Click;
        _datasetPackageMenuItem.Click += DatasetPackageMenuItem_Click;

        _fitButton.Click -= FitButton_Click;
        _fitButton.Click += FitButton_Click;

        BindEditorAction(_previousImageButton, EditorAction.PreviousImage);
        BindEditorAction(_nextImageButton, EditorAction.NextImage);
        BindEditorAction(_undoButton, EditorAction.Undo);
        BindEditorAction(_redoButton, EditorAction.Redo);
        BindEditorAction(_deleteButton, EditorAction.Delete);

        _zoomOutButton.Click -= ZoomOutButton_Click;
        _zoomOutButton.Click += ZoomOutButton_Click;

        _zoomInButton.Click -= ZoomInButton_Click;
        _zoomInButton.Click += ZoomInButton_Click;

        _selectButton.Click -= SelectButton_Click;
        _selectButton.Click += SelectButton_Click;

        _rectangleButton.Click -= RectangleButton_Click;
        _rectangleButton.Click += RectangleButton_Click;

        _rotationButton.Click -= RotationButton_Click;
        _rotationButton.Click += RotationButton_Click;

        _polygonButton.Click -= PolygonButton_Click;
        _polygonButton.Click += PolygonButton_Click;

        _editShapesButton.Click -= EditShapesButton_Click;
        _editShapesButton.Click += EditShapesButton_Click;

        BindEditorAction(_duplicateButton, EditorAction.Duplicate);
        BindEditorAction(_editLabelButton, EditorAction.EditLabel);
        BindEditorAction(_addVertexButton, EditorAction.AddVertex);
        BindEditorAction(_removeVertexButton, EditorAction.RemoveVertex);

        _saveButton.Click -= SaveButton_Click;
        _saveButton.Click += SaveButton_Click;

        _importModelButton.Click -= ImportModelButton_Click;
        _importModelButton.Click += ImportModelButton_Click;

        _detectCurrentButton.Click -= DetectCurrentButton_Click;
        _detectCurrentButton.Click += DetectCurrentButton_Click;

        _detectFolderButton.Click -= DetectFolderButton_Click;
        _detectFolderButton.Click += DetectFolderButton_Click;

        _detectUnlabeledButton.Click -= DetectUnlabeledButton_Click;
        _detectUnlabeledButton.Click += DetectUnlabeledButton_Click;

        _cancelDetectButton.Click -= CancelDetectButton_Click;
        _cancelDetectButton.Click += CancelDetectButton_Click;

        _imageListView.SelectedIndexChanged -= ImageListView_SelectedIndexChanged;
        _imageListView.SelectedIndexChanged += ImageListView_SelectedIndexChanged;

        _canvasControl.DocumentChanged -= CanvasControl_DocumentChanged;
        _canvasControl.DocumentChanged += CanvasControl_DocumentChanged;

        _canvasControl.SelectionChanged -= CanvasControl_SelectionChanged;
        _canvasControl.SelectionChanged += CanvasControl_SelectionChanged;
        _canvasControl.RequestLabel = PromptForLabel;
        _canvasControl.RequestEditedLabel = PromptForLabel;
        _labelsListBox.SelectedIndexChanged -= LabelsListBox_SelectedIndexChanged;
        _labelsListBox.SelectedIndexChanged += LabelsListBox_SelectedIndexChanged;
        _shapesListView.ItemSelectionChanged -= ShapesListView_ItemSelectionChanged;
        _shapesListView.ItemSelectionChanged += ShapesListView_ItemSelectionChanged;
    }

    private void ConfigureIconButton(ToolStripButton button, ToolbarIcon icon, string toolTipText)
    {
        button.AutoSize = false;
        button.Size = new Size(28, 28);
        button.Margin = Padding.Empty;
        button.Padding = Padding.Empty;
        button.DisplayStyle = ToolStripItemDisplayStyle.Image;
        button.Image = _toolbarIcons.Get(icon);
        button.ImageScaling = ToolStripItemImageScaling.SizeToFit;
        button.ToolTipText = toolTipText;
    }

    private static void ConfigureTextButton(ToolStripButton button, string toolTipText)
    {
        button.DisplayStyle = ToolStripItemDisplayStyle.Text;
        button.AutoSize = true;
        button.ToolTipText = toolTipText;
    }

    private void BindEditorAction(ToolStripButton button, EditorAction action)
    {
        button.Tag = action;
        button.Click -= EditorActionButton_Click;
        button.Click += EditorActionButton_Click;
    }

    private void EditorActionButton_Click(object sender, EventArgs e)
    {
        ToolStripButton button = sender as ToolStripButton;
        if (button == null || !(button.Tag is EditorAction))
        {
            return;
        }

        EditorAction action = (EditorAction)button.Tag;
        ExecuteEditorAction(action);
    }

    private void ZoomOutButton_Click(object sender, EventArgs e)
    {
        _canvasControl.ZoomOutAtCenter();
    }

    private void ZoomInButton_Click(object sender, EventArgs e)
    {
        _canvasControl.ZoomInAtCenter();
    }

    private void ClearToolbarImageReferences()
    {
        ToolStripButton[] buttons = new ToolStripButton[]
        {
            _openFolderButton,
            _previousImageButton,
            _nextImageButton,
            _saveButton,
            _undoButton,
            _redoButton,
            _selectButton,
            _rectangleButton,
            _rotationButton,
            _polygonButton,
            _editShapesButton,
            _deleteButton,
            _zoomOutButton,
            _fitButton,
            _zoomInButton
        };

        foreach (ToolStripButton button in buttons)
        {
            button.Image = null;
        }
    }

    private async void OpenFolderMenuItem_Click(object? sender, EventArgs e)
    {
        if (!await SaveRecoveryAsync())
        {
            return;
        }

        using FolderBrowserDialog dialog = new()
        {
            Description = "Open EasyLabel project folder",
            UseDescriptionForTitle = true
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        await LoadProjectAsync(dialog.SelectedPath);
    }

    private void ExitMenuItem_Click(object? sender, EventArgs e)
    {
        Close();
    }

    private void StandaloneConvertMenuItem_Click(object? sender, EventArgs e)
    {
        using (StandaloneConvertForm dialog = new StandaloneConvertForm(_standaloneConvertService))
        {
            dialog.ShowDialog(this);
        }
    }

    private async void ClassRemapMenuItem_Click(object? sender, EventArgs e)
    {
        if (_project is null)
        {
            MessageBox.Show(this, "Open a project folder before remapping classes.", "Class Remap", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (!await ConfirmSaveOrDiscardCurrentDocument())
        {
            return;
        }

        string rootDir = _project.RootDir;
        ProjectPaths projectPaths = _project.Paths;
        using (ClassRemapForm dialog = new ClassRemapForm(_project, _classRemapService))
        {
            if (dialog.ShowDialog(this) != DialogResult.OK)
            {
                return;
            }
        }

        await LoadProjectAsync(rootDir, projectPaths);
        _statusLabel.Text = $"Class remap applied - {rootDir}";
    }

    private async void BulkLabelEditMenuItem_Click(object? sender, EventArgs e)
    {
        if (_project is null)
        {
            MessageBox.Show(this, "Open a project folder before editing labels in bulk.", "Bulk Label Edit", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (!await ConfirmSaveOrDiscardCurrentDocument())
        {
            return;
        }

        string rootDir = _project.RootDir;
        ProjectPaths projectPaths = _project.Paths;
        using (BulkLabelEditForm dialog = new BulkLabelEditForm(_project, _bulkLabelEditService))
        {
            if (dialog.ShowDialog(this) != DialogResult.OK)
            {
                return;
            }
        }

        await LoadProjectAsync(rootDir, projectPaths);
        _statusLabel.Text = $"Bulk label edit applied - {rootDir}";
    }

    private async void AugmentMenuItem_Click(object? sender, EventArgs e)
    {
        if (_project is null || _currentImage is null)
        {
            MessageBox.Show(this, "Load an annotated image before cloning or augmenting.", "Clone & Augment", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (!await ConfirmSaveOrDiscardCurrentDocument())
        {
            return;
        }

        string rootDir = _project.RootDir;
        ProjectPaths projectPaths = _project.Paths;
        using (AugmentForm dialog = new AugmentForm(_project, _currentImage, _augmentService))
        {
            if (dialog.ShowDialog(this) != DialogResult.OK)
            {
                return;
            }
        }

        await LoadProjectAsync(rootDir, projectPaths);
        _statusLabel.Text = $"Clone and augment applied - {rootDir}";
    }

    private void UltralyticsExportMenuItem_Click(object? sender, EventArgs e)
    {
        if (_project is null)
        {
            MessageBox.Show(this, "Open a project folder before exporting.", "Export Ultralytics", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        using (UltralyticsExportForm dialog = new UltralyticsExportForm(_project, _ultralyticsExportService))
        {
            dialog.ShowDialog(this);
        }
    }

    private void DatasetPackageMenuItem_Click(object? sender, EventArgs e)
    {
        if (_project is null)
        {
            MessageBox.Show(this, "Open a project folder before packaging.", "Package Dataset", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        using (DatasetPackageForm dialog = new DatasetPackageForm(_project, _datasetPackageService))
        {
            dialog.ShowDialog(this);
        }
    }

    private void FitButton_Click(object? sender, EventArgs e)
    {
        _canvasControl.FitToWindow();
    }

    private void SelectButton_Click(object? sender, EventArgs e)
    {
        SetTool(CanvasTool.Select);
    }

    private void RectangleButton_Click(object? sender, EventArgs e)
    {
        SetTool(CanvasTool.Rectangle);
    }

    private void RotationButton_Click(object? sender, EventArgs e)
    {
        SetTool(CanvasTool.Rotation);
    }

    private void PolygonButton_Click(object? sender, EventArgs e)
    {
        SetTool(CanvasTool.Polygon);
    }

    private void EditShapesButton_Click(object? sender, EventArgs e)
    {
        SetTool(CanvasTool.Edit);
    }

    private async void SaveButton_Click(object? sender, EventArgs e)
    {
        await SaveCurrentDocument();
    }

    private void ImportModelButton_Click(object? sender, EventArgs e)
    {
        using OpenFileDialog dialog = new()
        {
            Title = "Import detection model",
            Filter = "YOLO/OpenVINO model (*.pt;*.onnx;*.xml)|*.pt;*.onnx;*.xml|All files (*.*)|*.*"
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        _modelPath = dialog.FileName;
        _statusLabel.Text = $"Model imported: {Path.GetFileName(_modelPath)}";
        SetDetectButtonsEnabled(_detectCancellation is null);
    }

    private async void DetectCurrentButton_Click(object? sender, EventArgs e)
    {
        await StartDetectAsync(DetectScope.Current);
    }

    private async void DetectFolderButton_Click(object? sender, EventArgs e)
    {
        await StartDetectAsync(DetectScope.Folder);
    }

    private async void DetectUnlabeledButton_Click(object? sender, EventArgs e)
    {
        await StartDetectAsync(DetectScope.OnlyUnlabeled);
    }

    private void CancelDetectButton_Click(object? sender, EventArgs e)
    {
        _detectCancellation?.Cancel();
        _statusLabel.Text = "Canceling detect...";
    }

    private void CanvasControl_DocumentChanged(object? sender, EventArgs e)
    {
        _documentDirty = _canvasControl.Editor.History.IsDirty;
        RefreshShapeList();
        UpdateTitle();
    }

    private void CanvasControl_SelectionChanged(object? sender, EventArgs e)
    {
        if (_suppressShapeSelectionChanged) return;
        _suppressShapeSelectionChanged = true;
        try
        {
            foreach (ListViewItem item in _shapesListView.Items) item.Selected = false;
            int index = _canvasControl.SelectedShapeIndex;
            if (index >= 0 && index < _shapesListView.Items.Count)
            {
                _shapesListView.Items[index].Selected = true;
                _shapesListView.Items[index].Focused = true;
                _shapesListView.Items[index].EnsureVisible();
            }
        }
        finally { _suppressShapeSelectionChanged = false; }
    }

    private void ShapesListView_ItemSelectionChanged(object? sender, ListViewItemSelectionChangedEventArgs e)
    {
        if (_suppressShapeSelectionChanged || !e.IsSelected) return;
        _canvasControl.SetSelectedShapeIndex(e.ItemIndex);
    }

    private void LabelsListBox_SelectedIndexChanged(object? sender, EventArgs e)
    {
        if (_labelsListBox.SelectedItem is string label)
        {
            _canvasControl.ActiveLabel = label;
            _statusLabel.Text = "Active label: " + label;
        }
    }

    private async void ImageListView_SelectedIndexChanged(object? sender, EventArgs e)
    {
        if (_suppressImageSelectionChanged)
        {
            return;
        }
        if (_switchingImage || _detectCancellation != null)
        {
            RestoreCurrentImageSelection();
            return;
        }

        if (_imageListView.SelectedItems.Count == 0)
        {
            return;
        }

        if (_imageListView.SelectedItems[0].Tag is ImageItem image)
        {
            if (ReferenceEquals(image, _currentImage))
            {
                return;
            }

            _switchingImage = true;
            UpdatePrimaryToolbarEnabled();
            _canvasControl.CancelGesture();
            _canvasControl.Enabled = false;
            _aiToolStrip.Enabled = false;
            _menuStrip.Enabled = false;
            _imageListView.Enabled = false;
            try
            {
                if (!await SaveRecoveryAsync() || !await LoadImage(image)) RestoreCurrentImageSelection();
            }
            finally
            {
                _switchingImage = false;
                UpdatePrimaryToolbarEnabled();
                _canvasControl.Enabled = true;
                _aiToolStrip.Enabled = true;
                _menuStrip.Enabled = true;
                _imageListView.Enabled = true;
            }
        }
    }

    private Task LoadProjectAsync(string rootDir)
    {
        return LoadProjectAsync(rootDir, null);
    }

    // paths: reopen an already open project with its own paths (a named project keeps its metadata folder); null opens the folder with the original placement.
    private async Task LoadProjectAsync(string rootDir, ProjectPaths? paths)
    {
        SetBusy(true);

        try
        {
            Project project = await Task.Run(() => paths is null ? _projectService.Open(rootDir) : _projectService.Open(paths));
            _project = project;
            _classRemapMenuItem.Enabled = true;
            _bulkLabelEditMenuItem.Enabled = true;
            _augmentMenuItem.Enabled = false;
            _ultralyticsExportMenuItem.Enabled = true;
            _datasetPackageMenuItem.Enabled = true;
            PopulateImageList(project);
            PopulateLabels(project);
            _statusLabel.Text = $"{project.Images.Count} images, {project.Classes.Count} classes - {project.RootDir}";

            if (project.Images.Count > 0)
            {
                _imageListView.Items[0].Selected = true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            MessageBox.Show(this, ex.Message, "Open Folder", MessageBoxButtons.OK, MessageBoxIcon.Error);
            _statusLabel.Text = "Open folder failed";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void PopulateImageList(Project project)
    {
        _suppressImageSelectionChanged = true;
        _imageListView.BeginUpdate();

        try
        {
            _imageListView.Items.Clear();

            foreach (ImageItem image in project.Images)
            {
                ListViewItem item = new(Path.GetRelativePath(project.RootDir, image.ImagePath))
                {
                    Tag = image
                };
                item.SubItems.Add(image.ReviewStatus);
                item.SubItems.Add(image.HasLabels ? "yes" : "no");
                _imageListView.Items.Add(item);
            }
        }
        finally
        {
            _imageListView.EndUpdate();
            _suppressImageSelectionChanged = false;
        }

        _currentImage = null;
        _currentDocument = null;
        _currentSidecar = null;
        _documentDirty = false;
        _canvasControl.SetImage(null, null);
        _canvasControl.SetDocument(null);
        RefreshShapeList();
        UpdateTitle();
    }

    private void PopulateLabels(Project project)
    {
        string active = _canvasControl.ActiveLabel;
        _labelsListBox.BeginUpdate();
        try
        {
            _labelsListBox.Items.Clear();
            foreach (string label in project.Classes) _labelsListBox.Items.Add(label);
        }
        finally { _labelsListBox.EndUpdate(); }
        if (_labelsListBox.Items.Count > 0)
        {
            SelectActiveLabel(string.IsNullOrWhiteSpace(active) ? project.Classes[0] : active);
        }
    }

    private void SelectActiveLabel(string label)
    {
        _canvasControl.ActiveLabel = label;
        for (int index = 0; index < _labelsListBox.Items.Count; index++)
        {
            if (string.Equals(_labelsListBox.Items[index] as string, label, StringComparison.Ordinal))
            {
                _labelsListBox.SelectedIndex = index;
                return;
            }
        }
    }

    private void RefreshShapeList()
    {
        _suppressShapeSelectionChanged = true;
        _shapesListView.BeginUpdate();
        try
        {
            _shapesListView.Items.Clear();
            if (_currentDocument == null) return;
            for (int index = 0; index < _currentDocument.Shapes.Count; index++)
            {
                LabelShape shape = _currentDocument.Shapes[index];
                ListViewItem item = new ListViewItem((shape.ShapeType ?? "shape").Replace('_', ' '));
                item.SubItems.Add(shape.Label ?? string.Empty);
                item.Tag = index;
                _shapesListView.Items.Add(item);
            }
        }
        finally
        {
            _shapesListView.EndUpdate();
            _suppressShapeSelectionChanged = false;
        }
        CanvasControl_SelectionChanged(_canvasControl, EventArgs.Empty);
    }

    private string? PromptForLabel(string? currentLabel)
    {
        using Form dialog = new Form
        {
            Text = string.IsNullOrWhiteSpace(currentLabel) ? "Assign label" : "Edit label",
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            ClientSize = new Size(360, 112),
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false
        };
        Label prompt = new Label { Text = "Label", AutoSize = true, Location = new Point(14, 17) };
        TextBox input = new TextBox { Location = new Point(14, 38), Width = 330, Text = currentLabel ?? string.Empty };
        Button accept = new Button { Text = "OK", DialogResult = DialogResult.OK, Width = 82, Location = new Point(174, 74) };
        Button cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = 82, Location = new Point(262, 74) };
        dialog.Controls.AddRange(new Control[] { prompt, input, accept, cancel });
        dialog.AcceptButton = accept;
        dialog.CancelButton = cancel;
        if (dialog.ShowDialog(this) != DialogResult.OK || string.IsNullOrWhiteSpace(input.Text)) return null;
        string label = input.Text.Trim();
        if (_project != null && !_project.Classes.Contains(label))
        {
            _labelsListBox.Items.Add(label);
        }
        SelectActiveLabel(label);
        return label;
    }

    private void SetBusy(bool busy)
    {
        UseWaitCursor = busy;
        UpdatePrimaryToolbarEnabled();
        _openFolderMenuItem.Enabled = !busy;
        _standaloneConvertMenuItem.Enabled = !busy;
        _classRemapMenuItem.Enabled = !busy && _project is not null;
        _bulkLabelEditMenuItem.Enabled = !busy && _project is not null;
        _augmentMenuItem.Enabled = !busy && _project is not null && _currentImage is not null;
        _ultralyticsExportMenuItem.Enabled = !busy && _project is not null;
        _datasetPackageMenuItem.Enabled = !busy && _project is not null;
        _importModelButton.Enabled = !busy;
        SetDetectButtonsEnabled(!busy && _detectCancellation is null);
        _statusLabel.Text = busy ? "Opening project..." : _statusLabel.Text;
    }

    private void UpdatePrimaryToolbarEnabled()
    {
        _toolStrip.Enabled = !UseWaitCursor
            && !_switchingImage
            && !_closingPrompt
            && !_closeApproved
            && _detectCancellation is null;
    }

    private async Task<bool> LoadImage(ImageItem image)
    {
        try
        {
            Bitmap bitmap = _imageLoader.LoadBitmap(image.ImagePath);
            LabelMeDocument document = LoadOrCreateDocument(image, bitmap);
            EasyLabelSidecar sidecar = _sidecarIO.Read(image.SidecarPath);
            bool recovered = false;
            try
            {
                var recovery = _project == null ? null
                    : _autoSave.ReadNewer(_project.Paths, image.ImagePath, image.JsonPath);
                if (recovery != null)
                {
                    DialogResult decision = MessageBox.Show(this, "Restore newer unsaved edits for "
                        + Path.GetFileName(image.ImagePath) + "?", "Recover edits",
                        MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
                    if (decision == DialogResult.Cancel) { bitmap.Dispose(); return false; }
                    if (decision == DialogResult.Yes)
                    {
                        document = recovery.Document;
                        sidecar = recovery.Sidecar;
                        recovered = true;
                    }
                    else if (_project != null) await _autoSave.ClearAsync(_project.Paths, image.ImagePath);
                }
            }
            catch
            {
                bitmap.Dispose();
                throw;
            }
            _currentImage = image;
            _currentDocument = document;
            _currentSidecar = sidecar;
            _documentDirty = recovered;
            _canvasControl.SetImage(bitmap, image.ImagePath);
            _canvasControl.SetDocument(document, sidecar);
            if (recovered) _canvasControl.Editor.History.MarkDirty();
            _canvasControl.ActiveLabel = GetActiveLabel();
            RefreshShapeList();
            _statusLabel.Text = $"{Path.GetFileName(image.ImagePath)} - {bitmap.Width} x {bitmap.Height}";
            UpdateTitle();
            _augmentMenuItem.Enabled = _project is not null;
            SetDetectButtonsEnabled(_detectCancellation is null);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            MessageBox.Show(this, ex.Message, "Load Image", MessageBoxButtons.OK, MessageBoxIcon.Error);
            _statusLabel.Text = "Load image failed";
            return false;
        }
    }

    private LabelMeDocument LoadOrCreateDocument(ImageItem image, Bitmap bitmap)
    {
        if (File.Exists(image.JsonPath))
        {
            return _labelMeIO.Read(image.JsonPath);
        }

        return new LabelMeDocument
        {
            ImagePath = Path.GetFileName(image.ImagePath),
            ImageData = null,
            ImageHeight = bitmap.Height,
            ImageWidth = bitmap.Width
        };
    }

    private async Task<bool> SaveCurrentDocument()
    {
        if (_currentImage is null || _currentDocument is null)
        {
            return true;
        }

        try
        {
            _labelMeIO.Write(_currentDocument, _currentImage.JsonPath);
            EasyLabelSidecar sidecar = _currentSidecar ?? new EasyLabelSidecar();

            _sidecarIO.Write(sidecar, _currentImage.SidecarPath);
            _currentSidecar = sidecar;
            _documentDirty = false;
            _canvasControl.Editor.History.MarkSaved();
            RefreshImageListItem(_currentImage, sidecar, _currentDocument);
            _statusLabel.Text = $"Saved {Path.GetFileName(_currentImage.JsonPath)}";
            UpdateTitle();
            if (_project != null) await _autoSave.ClearAsync(_project.Paths, _currentImage.ImagePath);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            MessageBox.Show(this, ex.Message, "Save Image", MessageBoxButtons.OK, MessageBoxIcon.Error);
            _statusLabel.Text = "Save failed";
            return false;
        }
    }

    private async Task<bool> ConfirmSaveOrDiscardCurrentDocument()
    {
        if (!_documentDirty)
        {
            return await SaveRecoveryAsync();
        }

        UnsavedDocumentDecision decision = ShowUnsavedDocumentPrompt();
        if (decision == UnsavedDocumentDecision.Save)
        {
            return await SaveCurrentDocument();
        }

        if (decision != UnsavedDocumentDecision.Discard) return false;
        try
        {
            if (_project != null && _currentImage != null)
                await _autoSave.ClearAsync(_project.Paths, _currentImage.ImagePath);
            _documentDirty = false;
            return _currentImage == null || await LoadImage(_currentImage);
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            MessageBox.Show(this, ex.Message, "Discard edits", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
    }

    private UnsavedDocumentDecision ShowUnsavedDocumentPrompt()
    {
        string imageName = _currentImage is null
            ? "this image"
            : Path.GetFileName(_currentImage.ImagePath) ?? _currentImage.ImagePath;
        using Form dialog = new()
        {
            Text = "Unsaved changes",
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            ClientSize = new Size(420, 140),
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false
        };

        Label messageLabel = new()
        {
            Text = $"Save changes to {imageName} before leaving it?",
            AutoSize = false,
            Bounds = new Rectangle(16, 16, 388, 48)
        };
        Button saveButton = new()
        {
            Text = "Save",
            DialogResult = DialogResult.Yes,
            Width = 92
        };
        Button discardButton = new()
        {
            Text = "Discard",
            DialogResult = DialogResult.No,
            Width = 92
        };
        Button cancelButton = new()
        {
            Text = "Cancel",
            DialogResult = DialogResult.Cancel,
            Width = 92
        };
        FlowLayoutPanel buttonPanel = new()
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(12),
            Height = 56
        };

        buttonPanel.Controls.Add(cancelButton);
        buttonPanel.Controls.Add(discardButton);
        buttonPanel.Controls.Add(saveButton);
        dialog.Controls.Add(messageLabel);
        dialog.Controls.Add(buttonPanel);
        dialog.AcceptButton = saveButton;
        dialog.CancelButton = cancelButton;

        DialogResult result = dialog.ShowDialog(this);
        return result switch
        {
            DialogResult.Yes => UnsavedDocumentDecision.Save,
            DialogResult.No => UnsavedDocumentDecision.Discard,
            _ => UnsavedDocumentDecision.Cancel
        };
    }

    private void RestoreCurrentImageSelection()
    {
        _suppressImageSelectionChanged = true;

        try
        {
            for (int i = 0; i < _imageListView.Items.Count; i++)
            {
                _imageListView.Items[i].Selected = false;
            }

            if (_currentImage is null)
            {
                return;
            }

            ListViewItem? currentItem = FindImageListViewItem(_currentImage);
            if (currentItem is null)
            {
                return;
            }

            currentItem.Selected = true;
            currentItem.Focused = true;
            currentItem.EnsureVisible();
        }
        finally
        {
            _suppressImageSelectionChanged = false;
        }
    }

    private async Task StartDetectAsync(DetectScope scope)
    {
        if (_project is null)
        {
            MessageBox.Show(this, "Open a project folder before running detect.", "Detect", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (string.IsNullOrWhiteSpace(_modelPath))
        {
            MessageBox.Show(this, "Import a detection model before running detect.", "Detect", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (!TryReadThreshold(_confTextBox.Text, "confidence", out double confidence)
            || !TryReadThreshold(_iouTextBox.Text, "IoU", out double iou))
        {
            return;
        }

        List<ImageItem> targets = GetDetectTargets(scope);
        if (targets.Count == 0)
        {
            _statusLabel.Text = "No images match the detect scope.";
            return;
        }

        using CancellationTokenSource cancellation = new();
        _canvasControl.CancelGesture();
        _detectCancellation = cancellation;
        SetDetectBusy(true, targets.Count);

        try
        {
            int completed = 0;
            IProgress<DetectApplySummary> progress = new Progress<DetectApplySummary>(summary =>
            {
                completed++;
                if (ReferenceEquals(summary.Image, _currentImage))
                    _canvasControl.ApplyDetections(summary.Document.Shapes, summary.Sidecar.Shapes);
                else
                    RefreshImageListItem(summary.Image, summary.Sidecar, summary.Document);
                _detectProgressBar.Value = Math.Min(completed, _detectProgressBar.Maximum);
                _statusLabel.Text = $"Detecting {completed}/{targets.Count}...";
            });

            List<DetectApplySummary> summaries = await Task.Run(
                () => DetectAndSaveTargets(targets, _modelPath, confidence, iou, cancellation.Token, progress, _currentImage),
                cancellation.Token);

            int detectionCount = summaries.Sum(summary => summary.DetectionCount);
            _statusLabel.Text = $"Detect complete: {detectionCount} shapes on {summaries.Count} images";
        }
        catch (OperationCanceledException)
        {
            _statusLabel.Text = "Detect canceled";
        }
        catch (Exception ex) when (ex is EnvironmentSetupException or IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            MessageBox.Show(this, ex.Message, "Detect", MessageBoxButtons.OK, MessageBoxIcon.Error);
            _statusLabel.Text = "Detect failed";
        }
        finally
        {
            _detectCancellation = null;
            SetDetectBusy(false, 0);
        }
    }

    private List<ImageItem> GetDetectTargets(DetectScope scope)
    {
        if (_project is null)
        {
            return new List<ImageItem>();
        }

        if (scope == DetectScope.Current)
        {
            return _currentImage is null ? new List<ImageItem>() : new List<ImageItem> { _currentImage };
        }

        IEnumerable<ImageItem> images = _project.Images;
        if (scope == DetectScope.OnlyUnlabeled)
        {
            images = images.Where(image => !image.HasLabels);
        }

        return images.ToList();
    }

    private List<DetectApplySummary> DetectAndSaveTargets(
        IReadOnlyList<ImageItem> targets,
        string modelPath,
        double confidence,
        double iou,
        CancellationToken cancellationToken,
        IProgress<DetectApplySummary> progress,
        [System.Diagnostics.CodeAnalysis.AllowNull] ImageItem openImage)
    {
        List<DetectApplySummary> summaries = new();

        for (int i = 0; i < targets.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ImageItem image = targets[i];
            DetectRequest request = new()
            {
                Model = modelPath,
                Images = { image.ImagePath },
                Conf = confidence,
                Iou = iou
            };

            DetectImageResult result = _detectService.Detect(request).First();
            DetectApplySummary summary = SaveDetectResult(image, result, modelPath, ReferenceEquals(image, openImage));
            summaries.Add(summary);
            progress.Report(summary);
        }

        return summaries;
    }

    private DetectApplySummary SaveDetectResult(ImageItem image, DetectImageResult result, string modelPath, bool applyToOpenDocument)
    {
        LabelMeDocument document = !applyToOpenDocument && File.Exists(image.JsonPath)
            ? _labelMeIO.Read(image.JsonPath)
            : new LabelMeDocument
            {
                ImagePath = Path.GetFileName(image.ImagePath),
                ImageData = null,
                ImageHeight = result.Height,
                ImageWidth = result.Width
            };

        if (result.Width > 0)
        {
            document.ImageWidth = result.Width;
        }

        if (result.Height > 0)
        {
            document.ImageHeight = result.Height;
        }

        EasyLabelSidecar sidecar = applyToOpenDocument ? new EasyLabelSidecar()
            : BuildSidecarForSave(document, _sidecarIO.Read(image.SidecarPath));
        DocumentEditor editor = new DocumentEditor(document, sidecar);
        editor.Add(result.Detections.Select(CreateShape),
            result.Detections.Select(detection => new ShapeProvenance
            {
                Source = "model",
                Confidence = detection.Confidence,
                Model = Path.GetFileName(modelPath)
            }), "Apply detections");

        sidecar.ReviewStatus = document.Shapes.Count == 0 ? "unlabeled" : "in_progress";
        if (!applyToOpenDocument)
        {
            _labelMeIO.Write(document, image.JsonPath);
            _sidecarIO.Write(sidecar, image.SidecarPath);
        }
        return new DetectApplySummary(image, document, sidecar, result.Detections.Count);
    }

    private static LabelShape CreateShape(DetectionResult detection)
    {
        LabelShape shape = new()
        {
            Label = detection.Label,
            ShapeType = detection.ShapeType,
            Points = detection.Points.Select(point => new[] { point[0], point[1] }).ToList()
        };

        if (shape.ShapeType == "oriented_rectangle" && shape.Points.Count >= 2)
        {
            double[] topLeft = shape.Points[0];
            double[] topRight = shape.Points[1];
            shape.Direction = Math.Atan2(topRight[1] - topLeft[1], topRight[0] - topLeft[0]);
        }

        return shape;
    }

    private void RefreshDetectedRows(IEnumerable<DetectApplySummary> summaries)
    {
        foreach (DetectApplySummary summary in summaries)
        {
            RefreshImageListItem(summary.Image, summary.Sidecar, summary.Document);
        }
    }

    private void RefreshImageListItem(ImageItem image, EasyLabelSidecar sidecar, LabelMeDocument document)
    {
        image.ReviewStatus = sidecar.ReviewStatus;
        image.HasLabels = document.Shapes.Count > 0;

        ListViewItem? item = FindImageListViewItem(image);
        if (item is null)
        {
            return;
        }

        item.SubItems[1].Text = image.ReviewStatus;
        item.SubItems[2].Text = image.HasLabels ? "yes" : "no";
    }

    private static EasyLabelSidecar BuildSidecarForSave(LabelMeDocument document, EasyLabelSidecar? existing)
    {
        return new EasyLabelSidecar
        {
            ReviewStatus = document.Shapes.Count == 0 ? "unlabeled" : "in_progress",
            Shapes = Enumerable.Range(0, document.Shapes.Count).Select(index =>
                existing != null && index < existing.Shapes.Count
                    ? CopyProvenance(existing.Shapes[index]) : new ShapeProvenance()).ToList()
        };
    }

    private static ShapeProvenance CopyProvenance(ShapeProvenance provenance)
    {
        return new ShapeProvenance
        {
            Source = string.IsNullOrWhiteSpace(provenance.Source) ? "manual" : provenance.Source,
            Confidence = provenance.Confidence,
            Model = provenance.Model
        };
    }

    private bool TryReadThreshold(string value, string name, out double threshold)
    {
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out threshold)
            && threshold >= 0.0
            && threshold <= 1.0)
        {
            return true;
        }

        MessageBox.Show(this, $"Enter a {name} threshold between 0 and 1.", "Detect", MessageBoxButtons.OK, MessageBoxIcon.Information);
        return false;
    }

    private void SetDetectBusy(bool busy, int maximum)
    {
        SetBusy(busy);
        _imageListView.Enabled = !busy;
        _canvasControl.Enabled = !busy;
        _saveButton.Enabled = !busy;
        _selectButton.Enabled = !busy;
        _rectangleButton.Enabled = !busy;
        _rotationButton.Enabled = !busy;
        _polygonButton.Enabled = !busy;
        _editShapesButton.Enabled = !busy;
        _duplicateButton.Enabled = !busy;
        _editLabelButton.Enabled = !busy;
        _addVertexButton.Enabled = !busy;
        _removeVertexButton.Enabled = !busy;
        SetDetectButtonsEnabled(!busy);
        _cancelDetectButton.Enabled = busy;
        _detectProgressBar.Visible = busy;
        _detectProgressBar.Value = 0;
        _detectProgressBar.Maximum = Math.Max(1, maximum);
        UseWaitCursor = busy;
    }

    private void SetDetectButtonsEnabled(bool enabled)
    {
        bool hasProject = _project is not null;
        bool hasModel = !string.IsNullOrWhiteSpace(_modelPath);
        _detectCurrentButton.Enabled = enabled && hasProject && hasModel && _currentImage is not null;
        _detectFolderButton.Enabled = enabled && hasProject && hasModel;
        _detectUnlabeledButton.Enabled = enabled && hasProject && hasModel;
    }

    private ListViewItem? FindImageListViewItem(ImageItem image)
    {
        foreach (ListViewItem item in _imageListView.Items)
        {
            if (ReferenceEquals(item.Tag, image))
            {
                return item;
            }
        }

        return null;
    }

    private string GetActiveLabel()
    {
        if (_labelsListBox.SelectedItem is string selected && !string.IsNullOrWhiteSpace(selected))
        {
            return selected;
        }
        if (_project is not null && _project.Classes.Count > 0)
        {
            return _project.Classes[0];
        }

        return string.Empty;
    }

    private void SetTool(CanvasTool tool)
    {
        _canvasControl.Tool = tool;
        _selectButton.Checked = tool == CanvasTool.Select;
        _editShapesButton.Checked = tool == CanvasTool.Edit;
        _rectangleButton.Checked = tool == CanvasTool.Rectangle;
        _rotationButton.Checked = tool == CanvasTool.Rotation;
        _polygonButton.Checked = tool == CanvasTool.Polygon;
    }

    private void UpdateTitle()
    {
        string suffix = _documentDirty ? "*" : string.Empty;
        Text = _currentImage is null
            ? "EasyLabel"
            : $"EasyLabel - {Path.GetFileName(_currentImage.ImagePath)}{suffix}";
    }

    private sealed record DetectApplySummary(
        ImageItem Image,
        LabelMeDocument Document,
        EasyLabelSidecar Sidecar,
        int DetectionCount);
}
