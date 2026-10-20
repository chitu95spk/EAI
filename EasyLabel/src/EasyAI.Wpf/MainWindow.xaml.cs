using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using EasyLabel.Core.Editing;
using EasyAI.Desktop.Infrastructure;
using EasyAI.Desktop.Resources;
using EasyAI.Desktop.ViewModels;
using EasyAI.Desktop.Views;
using EasyAI.Desktop.Services;
using ApplicationTheme = Wpf.Ui.Appearance.ApplicationTheme;
using ApplicationThemeManager = Wpf.Ui.Appearance.ApplicationThemeManager;

namespace EasyAI.Desktop;

public partial class MainWindow
{
    private readonly MainViewModel _viewModel;
    private readonly SettingsStore _store;
    private readonly DispatcherTimer _saveTimer;
    private readonly DockLayoutController _dock;
    private LayoutSettings _layout;
    private bool _syncingPanels;
    private bool _closeConfirmed;
    private bool _closeFlowActive;
    private readonly List<Wpf.Ui.Controls.MenuItem> _classMenuItems = new();
    private readonly List<Wpf.Ui.Controls.MenuItem> _recentMenuItems = new();
    private readonly List<RecentProjectItem> _recentWatched = new();
    private AgentChatWindow? _chatWindow;
    private bool _chatWanted;

    public MainWindow(MainViewModel viewModel, SettingsStore store)
    {
        _viewModel = viewModel;
        _store = store;
        DataContext = viewModel;

        InitializeComponent();

        // Explicit data contexts: the panels are re-hosted inside the docking manager (and floating windows), which do not inherit
        // the window's data context.
        CenterHost.DataContext = viewModel;
        CanvasHost.DataContext = viewModel;
        CanvasPane.DataContext = viewModel.Canvas;
        ToolStrip.DataContext = viewModel.Canvas;
        LabelsPanelHost.DataContext = viewModel;
        LabelsPaneControl.DataContext = viewModel.Panels.Labels;
        FilesPanelHost.DataContext = viewModel;
        ImagesPane.DataContext = viewModel.Panels.Files;
        InspectListControl.DataContext = viewModel.Inspect;

        _layout = store.LoadLayout().Normalized(
            SystemParameters.VirtualScreenLeft,
            SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth,
            SystemParameters.VirtualScreenHeight);
        ApplyWindowBounds(_layout);

        _dock = new DockLayoutController(Dock, PanelContent, PanelTitle, CenterHost, Strings.CanvasName, store.Log);
        ApplyChrome();
        // A layout saved by an older default arrangement (schema below current) is dropped once so the new default applies.
        if (_layout.SchemaVersion < LayoutSettings.CurrentSchema || !_dock.TryRestore(_layout.DockLayout))
        {
            _dock.BuildDefault(_layout.RightWidth);
            _dock.ApplyLegacyHidden(DockLayoutRules.HiddenByLegacyFlags(_layout.ImageListVisible));
        }

        SyncViewModelFromDock();

        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _saveTimer.Tick -= OnSaveTimerTick;
        _saveTimer.Tick += OnSaveTimerTick;

        _dock.VisibilityChanged -= OnDockVisibilityChanged;
        _dock.VisibilityChanged += OnDockVisibilityChanged;
        ApplicationThemeManager.Changed -= OnAppThemeChanged;
        ApplicationThemeManager.Changed += OnAppThemeChanged;
        _viewModel.LayoutResetRequested -= OnLayoutResetRequested;
        _viewModel.LayoutResetRequested += OnLayoutResetRequested;
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        _viewModel.Canvas.PropertyChanged -= OnCanvasClassesChanged;
        _viewModel.Canvas.PropertyChanged += OnCanvasClassesChanged;
        _viewModel.Label.AgentChatOpenRequested -= OnAgentChatOpenRequested;
        _viewModel.Label.AgentChatOpenRequested += OnAgentChatOpenRequested;
        RebuildClassMenu();
        _viewModel.Recent.Items.CollectionChanged -= OnRecentItemsChanged;
        _viewModel.Recent.Items.CollectionChanged += OnRecentItemsChanged;
        RebuildRecentMenu();
        _viewModel.ExitRequested -= OnExitRequested;
        _viewModel.ExitRequested += OnExitRequested;
        _viewModel.AboutRequested -= OnAboutRequested;
        _viewModel.AboutRequested += OnAboutRequested;
        _viewModel.ThemeChanged -= OnThemeChanged;
        _viewModel.ThemeChanged += OnThemeChanged;
        _viewModel.PaletteChanged -= OnPaletteChanged;
        _viewModel.PaletteChanged += OnPaletteChanged;
        _viewModel.Dialogs = new WpfDialogService(this, viewModel, store);
        _viewModel.Canvas.LabelPrompt = request => _viewModel.Dialogs?.AskLabel(request);
        _viewModel.Canvas.BrightnessPrompt = () => _viewModel.Dialogs?.ShowBrightness();
        _viewModel.Canvas.ConvertConfirmPrompt = (title, warnings) => _viewModel.Dialogs?.ConfirmConvertShapes(title, warnings) ?? false;

        RemoveHandler(ButtonBase.ClickEvent, new RoutedEventHandler(OnButtonClick));
        AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler(OnButtonClick));
    }

    // ---------- dockable panels ----------

    private string PanelTitle(string id)
    {
        switch (id)
        {
            // The same panel hosts the label / shape list on the Label tab, the training controls on the Train tab and the model / run controls on the Inspect tab.
            case DockPanelIds.Labels:
                return _viewModel.CurrentStep == WorkflowStep.Train ? Strings.StepTrain
                    : _viewModel.CurrentStep == WorkflowStep.Inspect ? Strings.StepInspect
                    : _viewModel.CurrentStep == WorkflowStep.Live ? Strings.LiveParamsTitle
                    : Strings.MiShowLabelsShapes;
            // The Files panel shows the independent Inspect image list on the Inspect tab.
            case DockPanelIds.Files: return _viewModel.CurrentStep == WorkflowStep.Inspect ? Strings.InspectListHeader : Strings.MiShowFiles;
            default: return id;
        }
    }

    /// <summary>The control of a panel, released from its previous parent (the hidden store, or an old layout) first.</summary>
    private FrameworkElement PanelContent(string id)
    {
        FrameworkElement control;
        switch (id)
        {
            case DockPanelIds.Labels: control = LabelsPanelHost; break;
            default: control = FilesPanelHost; break;
        }

        ReleaseFromParent(control);
        return control;
    }

    private static void ReleaseFromParent(FrameworkElement control)
    {
        DependencyObject? parent = control.Parent;
        if (parent is Panel panel)
        {
            panel.Children.Remove(control);
        }
        else if (parent is ContentPresenter presenter)
        {
            presenter.Content = null;
        }
        else if (parent is ContentControl contentControl)
        {
            contentControl.Content = null;
        }
        else if (parent is Decorator decorator)
        {
            decorator.Child = null;
        }
    }

    private static bool IsDarkTheme()
    {
        return ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Dark;
    }

    private void OnAppThemeChanged(ApplicationTheme currentApplicationTheme, System.Windows.Media.Color systemAccent)
    {
        // The Windows theme may change while the app follows the system: the palette follows the light / dark variant.
        Dispatcher.BeginInvoke(new Action(() =>
        {
            ThemeService.ApplyPalette();
            ApplyChrome();
        }));
    }

    /// <summary>Docking chrome (light / dark) and its panel title bars in the palette's accent colour.</summary>
    private void ApplyChrome()
    {
        bool dark = IsDarkTheme();
        _dock.ApplyTheme(dark);
        _dock.ApplyPalette(ThemeService.Palette.Resolve(dark));
        CanvasPane.Redraw();
    }

    /// <summary>A Settings colour (or the reset button) changed the palette: apply it live.</summary>
    private void OnPaletteChanged(object? sender, EventArgs e)
    {
        ThemeService.Palette = _viewModel.Palette;
        ThemeService.ApplyPalette();
        ApplyChrome();
    }

    private static string? PanelIdOf(string? propertyName)
    {
        switch (propertyName)
        {
            case nameof(MainViewModel.IsImageListVisible): return DockPanelIds.Files;
            case nameof(MainViewModel.IsLabelsVisible): return DockPanelIds.Labels;
            default: return null;
        }
    }

    private void SetViewModelVisible(string id, bool visible)
    {
        switch (id)
        {
            case DockPanelIds.Files: _viewModel.IsImageListVisible = visible; break;
            case DockPanelIds.Labels: _viewModel.IsLabelsVisible = visible; break;
        }
    }

    /// <summary>Copies the real panel visibility (after a layout was built or restored) to the View menu check marks.</summary>
    private void SyncViewModelFromDock()
    {
        _syncingPanels = true;
        try
        {
            foreach (string id in DockPanelIds.DefaultOrder)
            {
                SetViewModelVisible(id, _dock.IsVisible(id));
            }
        }
        finally
        {
            _syncingPanels = false;
        }
    }

    /// <summary>A panel was closed, restored or floated through its own title bar: the menu follows.</summary>
    private void OnDockVisibilityChanged(string id, bool visible)
    {
        // The Train tab hides the Files panel by itself. Its hide event can arrive after SyncFilesPanelForStep returned, so it is
        // recognised by the step: it is not the user closing the panel and must not change the View menu choice.
        if (id == DockPanelIds.Files && !visible && (_stepChangingFiles || _viewModel.CurrentStep == WorkflowStep.Train))
        {
            return;
        }

        _syncingPanels = true;
        try
        {
            SetViewModelVisible(id, visible);
        }
        finally
        {
            _syncingPanels = false;
        }

        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private bool _stepChangingFiles;

    /// <summary>The Train tab does not use the image list: the Files panel is hidden there and comes back on the other tabs when the View menu has it on (that choice is never changed by the tab).</summary>
    private void SyncFilesPanelForStep()
    {
        _stepChangingFiles = true;
        try
        {
            if (_viewModel.CurrentStep == WorkflowStep.Train)
            {
                if (_dock.IsVisible(DockPanelIds.Files))
                {
                    _dock.SetVisible(DockPanelIds.Files, false);
                }
            }
            else if (_viewModel.IsImageListVisible && !_dock.IsVisible(DockPanelIds.Files))
            {
                _dock.SetVisible(DockPanelIds.Files, true);
            }
        }
        finally
        {
            _stepChangingFiles = false;
        }
    }

    private void OnCanvasClassesChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CanvasViewModel.ClassNames) || e.PropertyName == nameof(CanvasViewModel.ClassEnabled))
        {
            RebuildClassMenu();
        }
    }

    /// <summary>
    /// Class menu: one entry per class of the project table (AutomationId from <see cref="ClassTableRules.MenuAutomationId"/>). A
    /// disabled class is greyed out. The entries are kept above the separator <c>ClassListEnd</c>.
    /// </summary>
    private void RebuildClassMenu()
    {
        foreach (Wpf.Ui.Controls.MenuItem old in _classMenuItems)
        {
            ClassGroupMenu.Items.Remove(old);
        }

        _classMenuItems.Clear();
        CanvasViewModel canvas = _viewModel.Canvas;
        int insertAt = ClassGroupMenu.Items.IndexOf(ClassListEnd);
        for (int i = 0; i < canvas.ClassCount; i++)
        {
            var item = new Wpf.Ui.Controls.MenuItem
            {
                Header = canvas.ClassNames[i],
                Command = canvas.SetClassIndexCommand,
                CommandParameter = i,
                IsEnabled = canvas.ClassEnabled[i],
            };
            System.Windows.Automation.AutomationProperties.SetAutomationId(item, ClassTableRules.MenuAutomationId(i));
            ClassGroupMenu.Items.Insert(insertAt++, item);
            _classMenuItems.Add(item);
        }
    }

    private void OnRecentItemsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        RebuildRecentMenu();
    }

    private void OnRecentItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(RecentProjectItem.Exists))
        {
            RebuildRecentMenu();
        }
    }

    /// <summary>
    /// File &gt; Open Recent: one entry per recent project (name over its folder), a missing one greyed; right-click removes it from the list.
    /// The entries are kept above the separator <c>RecentListEnd</c>, the "Clear list" entry stays below it.
    /// </summary>
    private void RebuildRecentMenu()
    {
        foreach (Wpf.Ui.Controls.MenuItem old in _recentMenuItems)
        {
            OpenRecentMenu.Items.Remove(old);
        }

        _recentMenuItems.Clear();
        foreach (RecentProjectItem watched in _recentWatched)
        {
            watched.PropertyChanged -= OnRecentItemChanged;
        }

        _recentWatched.Clear();
        int insertAt = OpenRecentMenu.Items.IndexOf(RecentListEnd);
        int index = 0;
        foreach (RecentProjectItem recent in _viewModel.Recent.Items)
        {
            recent.PropertyChanged -= OnRecentItemChanged;
            recent.PropertyChanged += OnRecentItemChanged;
            _recentWatched.Add(recent);

            StackPanel header = new StackPanel();
            header.Children.Add(new TextBlock { Text = recent.Name, MaxWidth = 420, TextTrimming = TextTrimming.CharacterEllipsis });
            header.Children.Add(new TextBlock { Text = recent.SecondaryText, MaxWidth = 420, FontSize = 11, Opacity = 0.7, TextTrimming = TextTrimming.CharacterEllipsis });

            var item = new Wpf.Ui.Controls.MenuItem
            {
                Header = header,
                Command = _viewModel.OpenRecentCommand,
                CommandParameter = recent,
                Opacity = recent.Exists ? 1d : 0.5d,
            };
            System.Windows.Automation.AutomationProperties.SetAutomationId(item, "MenuRecent_" + index);
            System.Windows.Automation.AutomationProperties.SetName(item, recent.DisplayText);

            var remove = new Wpf.Ui.Controls.MenuItem
            {
                Header = Strings.MiRecentRemove,
                Command = _viewModel.RemoveRecentCommand,
                CommandParameter = recent,
            };
            System.Windows.Automation.AutomationProperties.SetAutomationId(remove, "MenuRecentRemove_" + index);
            item.ContextMenu = new ContextMenu();
            item.ContextMenu.Items.Add(remove);

            OpenRecentMenu.Items.Insert(insertAt++, item);
            _recentMenuItems.Add(item);
            index++;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.CurrentStep))
        {
            // The Inspect / Label / Train tab changed: the shared panels carry the matching titles.
            _dock.SetTitle(DockPanelIds.Labels, PanelTitle(DockPanelIds.Labels));
            _dock.SetTitle(DockPanelIds.Files, PanelTitle(DockPanelIds.Files));
            SyncFilesPanelForStep();
            SyncAgentChat();
            return;
        }

        if (e.PropertyName == nameof(MainViewModel.HasProject))
        {
            // No project: the chat window goes away and is not brought back by itself.
            if (!_viewModel.HasProject)
            {
                _chatWanted = false;
            }

            SyncAgentChat();
            return;
        }

        string? id = PanelIdOf(e.PropertyName);
        if (id == null || _syncingPanels)
        {
            return;
        }

        // The View menu check mark changed: show or hide the panel.
        bool visible = id == DockPanelIds.Files ? _viewModel.IsImageListVisible : _viewModel.IsLabelsVisible;
        _dock.SetVisible(id, visible);
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    // ---------- agent chat window ----------

    private void OnAgentChatOpenRequested(object? sender, EventArgs e)
    {
        if (_chatWindow == null)
        {
            _chatWindow = new AgentChatWindow(_viewModel);
            _chatWindow.ClosedByUser -= OnAgentChatClosedByUser;
            _chatWindow.ClosedByUser += OnAgentChatClosedByUser;
        }

        _chatWanted = true;
        SyncAgentChat(true);
    }

    private void OnAgentChatClosedByUser(object? sender, EventArgs e)
    {
        _chatWanted = false;
    }

    /// <summary>Shows the chat window only on the Label step with a project and while the user wants it; hides it otherwise (it keeps its state).</summary>
    private void SyncAgentChat(bool reanchor = false)
    {
        if (_chatWindow == null)
        {
            return;
        }

        if (_chatWanted && _viewModel.CurrentStep == WorkflowStep.Label && _viewModel.HasProject)
        {
            // Only the chat button re-anchors a window that is already open; a step or project change must not move it.
            _chatWindow.ShowChat(this, ToolStrip.AgentChatAnchor, reanchor);
        }
        else
        {
            _chatWindow.HideChat();
        }
    }

    private void OnButtonClick(object sender, RoutedEventArgs e)
    {
        // A clicked canvas-toolbar button keeps keyboard focus and would swallow Enter and Delete; hand focus back
        // to the canvas once the command has run.
        if (e.OriginalSource is not ButtonBase button)
        {
            return;
        }

        // Label tab auto-label buttons also hand focus back so Enter and Delete keep working.
        bool isLabelPanelButton = _viewModel.CurrentStep == WorkflowStep.Label && IsDescendantOf(button, LabelStepControl);
        if (isLabelPanelButton || IsDescendantOf(button, CanvasPane) || IsDescendantOf(button, ToolStrip)
            || IsDescendantOf(button, ToolbarBar))
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(CanvasPane.FocusSurface));
        }
    }

    private static bool IsDescendantOf(DependencyObject? node, DependencyObject ancestor)
    {
        while (node != null)
        {
            if (ReferenceEquals(node, ancestor))
            {
                return true;
            }

            node = System.Windows.Media.VisualTreeHelper.GetParent(node) ?? LogicalTreeHelper.GetParent(node);
        }

        return false;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ThemeService.Apply(_viewModel.ThemeMode, this);
        ApplyChrome();
        UpdateCompactState();
        UpdateHeaderHeightCap();
    }

    private void OnThemeChanged(object? sender, string mode)
    {
        ThemeService.Apply(mode, this);
        ApplyChrome();
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateCompactState();
        UpdateHeaderHeightCap();
    }

    /// <summary>Lowest height the header keeps when the window is short; the work area (canvas, file list, Train pane) gets the rest.</summary>
    private const double MinWorkAreaHeight = 240;
    private const double MinHeaderHeight = 80;

    /// <summary>
    /// Caps the scrolling header (row 2) so the work area row keeps <see cref="MinWorkAreaHeight"/>. When the window is tall
    /// enough the cap is above the header's natural height, so the layout is unchanged.
    /// </summary>
    private void UpdateHeaderHeightCap()
    {
        Grid root = Content as Grid;
        if (root == null || HeaderScroll == null || root.RowDefinitions.Count < 6 || root.ActualHeight <= 0)
        {
            return;
        }

        // Rows 0 (title and menu), 1 (menu), 3 (notice) and 5 (status bar) keep their measured size.
        double fixedHeight = root.RowDefinitions[0].ActualHeight + root.RowDefinitions[1].ActualHeight
            + root.RowDefinitions[3].ActualHeight + root.RowDefinitions[5].ActualHeight;
        double cap = root.ActualHeight - fixedHeight - MinWorkAreaHeight;
        HeaderScroll.MaxHeight = System.Math.Max(MinHeaderHeight, cap);
    }

    private void OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (TryDeleteSelectedImage(e))
        {
            e.Handled = true;
            return;
        }

        if (TryApplicationShortcut(e))
        {
            e.Handled = true;
            return;
        }

        if (CanvasPane.HandleKey(e))
        {
            e.Handled = true;
        }
    }

    /// <summary>Plain Delete while the image list has the focus: deletes the selected image and its label files (after a confirmation).</summary>
    private bool TryDeleteSelectedImage(System.Windows.Input.KeyEventArgs e)
    {
        if (e.IsRepeat || e.Key != System.Windows.Input.Key.Delete || System.Windows.Input.Keyboard.Modifiers != System.Windows.Input.ModifierKeys.None)
        {
            return false;
        }

        DependencyObject? node = System.Windows.Input.Keyboard.FocusedElement as DependencyObject;
        while (node != null)
        {
            if (node is System.Windows.Controls.ListView list && System.Windows.Automation.AutomationProperties.GetAutomationId(list) == "ImageList")
            {
                if (_viewModel.DeleteImageCommand.CanExecute(null))
                {
                    _viewModel.DeleteImageCommand.Execute(null);
                }

                return true;
            }

            node = node is System.Windows.Media.Visual || node is System.Windows.Media.Media3D.Visual3D
                ? System.Windows.Media.VisualTreeHelper.GetParent(node)
                : System.Windows.LogicalTreeHelper.GetParent(node);
        }

        return false;
    }

    /// <summary>
    /// Window-level shortcuts of <see cref="ShortcutTable"/> (file, image stepping, edit mode and the EasyAI commands).
    /// Never while a text box, combo box or slider has the focus.
    /// </summary>
    private bool TryApplicationShortcut(System.Windows.Input.KeyEventArgs e)
    {
        if (e.IsRepeat || ShortcutInput.BlocksShortcuts(System.Windows.Input.Keyboard.FocusedElement))
        {
            return false;
        }

        ShortcutEntry? entry = ShortcutInput.Match(e);
        if (entry == null)
        {
            return false;
        }

        if (entry.Id == ShortcutId.TagOk || entry.Id == ShortcutId.TagNg)
        {
            byte value = entry.Id == ShortcutId.TagOk ? EasyLabel.Core.Model.ImageClassFlags.Ok : EasyLabel.Core.Model.ImageClassFlags.Ng;
            _viewModel.TagOpenImageClass(value);
            return true;
        }

        System.Windows.Input.ICommand? command = null;
        object? parameter = null;
        switch (entry.Id)
        {
            case ShortcutId.OpenImages: command = _viewModel.Data.AddImagesCommand; break;
            case ShortcutId.OpenFolder: command = _viewModel.OpenFolderCommand; break;
            case ShortcutId.Exit: command = _viewModel.ExitCommand; break;
            case ShortcutId.Save: command = _viewModel.SaveProjectCommand; break;
            case ShortcutId.SaveProjectAs: command = _viewModel.SaveProjectAsCommand; break;
            case ShortcutId.NewProject: command = _viewModel.NewProjectCommand; break;
            case ShortcutId.OpenProject: command = _viewModel.OpenProjectFileDialogCommand; break;
            case ShortcutId.CloseImage: command = _viewModel.CloseImageCommand; break;
            case ShortcutId.DeleteFile: command = _viewModel.DeleteAnnotationFilesCommand; break;
            case ShortcutId.Settings: command = _viewModel.ShowSettingsCommand; break;
            case ShortcutId.NextImage: command = _viewModel.NextImageCommand; break;
            case ShortcutId.PreviousImage: command = _viewModel.PreviousImageCommand; break;
            case ShortcutId.NextImageDown:
            case ShortcutId.PreviousImageUp:
                // Plain arrows keep their own job wherever the image stepping is unavailable (the Inspect list, menus, open dropdowns).
                command = entry.Id == ShortcutId.NextImageDown ? _viewModel.NextImageCommand : _viewModel.PreviousImageCommand;
                if (!command.CanExecute(null))
                {
                    return false;
                }

                break;
            case ShortcutId.EditMode:
                command = _viewModel.Canvas.SelectToolCommand;
                parameter = CanvasTool.Select;
                break;
            case ShortcutId.ToolAiPoints: command = _viewModel.Canvas.SelectAiPointsCommand; break;
            case ShortcutId.ToolAiBox: command = _viewModel.Canvas.SelectAiBoxCommand; break;
            case ShortcutId.ImportDataset: command = _viewModel.Data.ImportDatasetCommand; break;
            case ShortcutId.AutoLabel: command = _viewModel.ShowLabelTabCommand; break;
            case ShortcutId.Train: command = _viewModel.ShowTrainTabCommand; break;
        }

        if (command == null)
        {
            return false;
        }

        // A recognised shortcut is consumed even when the command is currently disabled (Save with nothing to save).
        if (command.CanExecute(parameter))
        {
            command.Execute(parameter);
        }

        return true;
    }

    /// <summary>The File menu is about to show: re-check which recent projects still exist (off the UI thread).</summary>
    private void OnFileMenuSubmenuOpened(object sender, RoutedEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, sender))
        {
            _viewModel.Recent.Refresh();
        }
    }

    private void OnExitRequested(object? sender, EventArgs e)
    {
        Close();
    }

    private void OnAboutRequested(object? sender, EventArgs e)
    {
        AboutWindow about = new AboutWindow { Owner = this, DataContext = _viewModel };
        about.ShowDialog();
    }

    private async Task ConfirmCloseDuringTrainingAsync()
    {
        try
        {
            // Asks, cancels the run and waits (bounded) without blocking the UI thread; the close is retried once nothing runs.
            if (await _viewModel.Train.ConfirmStopForCloseAsync())
            {
                _closeConfirmed = true;
                Close();
            }
        }
        catch (Exception ex)
        {
            _store.Log("Close during training failed: " + ex);
        }
        finally
        {
            _closeFlowActive = false;
        }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!_closeConfirmed && !_viewModel.ConfirmExit())
        {
            // Cancel in the unsaved prompt: keep the window (and the held edits) open.
            e.Cancel = true;
            return;
        }

        if (!_closeConfirmed && _viewModel.Train.IsRunning)
        {
            e.Cancel = true;
            if (!_closeFlowActive)
            {
                _closeFlowActive = true;
                _ = ConfirmCloseDuringTrainingAsync();
            }

            return;
        }

        _saveTimer.Stop();
        CaptureWindowState();
        _store.SaveLayout(_layout);
        _dock.VisibilityChanged -= OnDockVisibilityChanged;
        ApplicationThemeManager.Changed -= OnAppThemeChanged;
        _viewModel.LayoutResetRequested -= OnLayoutResetRequested;
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel.Canvas.PropertyChanged -= OnCanvasClassesChanged;
        _viewModel.Label.AgentChatOpenRequested -= OnAgentChatOpenRequested;
        if (_chatWindow != null)
        {
            _chatWindow.ClosedByUser -= OnAgentChatClosedByUser;
            _chatWindow.CloseForReal();
            _chatWindow = null;
        }

        _viewModel.Recent.Items.CollectionChanged -= OnRecentItemsChanged;
        foreach (RecentProjectItem watched in _recentWatched)
        {
            watched.PropertyChanged -= OnRecentItemChanged;
        }

        _recentWatched.Clear();
        _viewModel.ExitRequested -= OnExitRequested;
        _viewModel.AboutRequested -= OnAboutRequested;
        _viewModel.ThemeChanged -= OnThemeChanged;
        _viewModel.PaletteChanged -= OnPaletteChanged;
        _viewModel.Shutdown();
    }

    private void OnSaveTimerTick(object? sender, EventArgs e)
    {
        _saveTimer.Stop();
        CaptureWindowState();
        _store.SaveLayout(_layout);
    }

    private void OnLayoutResetRequested(object? sender, EventArgs e)
    {
        _saveTimer.Stop();
        _layout = LayoutSettings.CreateDefault();

        WindowState = WindowState.Normal;
        Width = LayoutSettings.DefaultWindowWidth;
        Height = LayoutSettings.DefaultWindowHeight;
        Rect work = SystemParameters.WorkArea;
        Left = Math.Max(work.Left, work.Left + (work.Width - Width) / 2);
        Top = Math.Max(work.Top, work.Top + (work.Height - Height) / 2);

        // Floating panels dock again and every panel is shown in the default stack.
        _dock.ForgetCompact();
        _dock.BuildDefault(_layout.RightWidth);
        SyncViewModelFromDock();
        UpdateCompactState();
        _layout.DockLayout = _dock.Capture();
        _store.SaveLayout(_layout);
    }

    private void ApplyWindowBounds(LayoutSettings layout)
    {
        Width = layout.WindowWidth;
        Height = layout.WindowHeight;
        if (layout.WindowLeft.HasValue && layout.WindowTop.HasValue)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = layout.WindowLeft.Value;
            Top = layout.WindowTop.Value;
        }

        if (layout.IsMaximized)
        {
            WindowState = WindowState.Maximized;
        }
    }

    private void CaptureWindowState()
    {
        bool maximized = WindowState == WindowState.Maximized;
        Rect bounds = WindowState == WindowState.Normal
            ? new Rect(Left, Top, ActualWidth, ActualHeight)
            : RestoreBounds;
        if (IsFinite(bounds.Left) && IsFinite(bounds.Top) && bounds.Width > 0 && bounds.Height > 0
            && IsFinite(bounds.Width) && IsFinite(bounds.Height))
        {
            _layout.WindowLeft = bounds.Left;
            _layout.WindowTop = bounds.Top;
            _layout.WindowWidth = bounds.Width;
            _layout.WindowHeight = bounds.Height;
        }

        _layout.IsMaximized = maximized;
        _layout.SchemaVersion = LayoutSettings.CurrentSchema;

        // The docking arrangement (panel order, sizes, floating windows, hidden panels) is one XML text; the two legacy flags
        // stay current so an older build still reads sensible values.
        string? xml = _dock.Capture();
        if (xml != null)
        {
            _layout.DockLayout = xml;
        }

        _layout.ImageListVisible = _dock.IsVisible(DockPanelIds.Files) || (_viewModel.CurrentStep == WorkflowStep.Train && _viewModel.IsImageListVisible);
    }

    private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

    /// <summary>Narrow window: the panels collapse into the docking side strip (auto-hide); wide again: they dock back.</summary>
    private void UpdateCompactState()
    {
        double width = ActualWidth > 0 ? ActualWidth : Width;
        bool compact = width < LayoutSettings.CompactThreshold;
        if (compact == _dock.IsCompact)
        {
            return;
        }

        if (compact)
        {
            _dock.EnterCompact();
        }
        else
        {
            _dock.ExitCompact(_layout.RightWidth);
        }
    }
}
