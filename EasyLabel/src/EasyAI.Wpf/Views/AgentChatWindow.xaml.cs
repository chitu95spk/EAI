using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using EasyAI.Desktop.Infrastructure;
using EasyAI.Desktop.Services.Agent;
using EasyAI.Desktop.ViewModels;

namespace EasyAI.Desktop.Views;

/// <summary>
/// The agent chat window: owned, non-modal, never closed by Esc. Closing it only hides it (the history and the prompt stay); its size
/// is saved in the settings and every time it is shown it is anchored next to the round chat button inside the monitor's work area. DataContext: the Label step view model.
/// </summary>
public partial class AgentChatWindow
{
    // Title bar, plus the least room the history keeps above the controls.
    private const double ChromeHeight = 40;
    private const double MinHistoryHeight = 90;
    private const double MinBottomHeight = 140;

    private readonly MainViewModel _host;
    private bool _closeForReal;

    public AgentChatWindow(MainViewModel host)
    {
        _host = host;
        DataContext = host.Label;
        InitializeComponent();
        MinWidth = AgentChatWindowBounds.MinWidth;
        MinHeight = AgentChatWindowBounds.MinHeight;

        host.Label.AgentChatReloaded -= OnChatReloaded;
        host.Label.AgentChatReloaded += OnChatReloaded;
        SizeChanged -= OnWindowSizeChanged;
        SizeChanged += OnWindowSizeChanged;
        Closing -= OnWindowClosing;
        Closing += OnWindowClosing;
        PromptBox.PreviewKeyDown -= OnPromptKeyDown;
        PromptBox.PreviewKeyDown += OnPromptKeyDown;
        _host.PropertyChanged -= OnHostPropertyChanged;
        _host.PropertyChanged += OnHostPropertyChanged;
        ApplyUiMode();
        RefreshStartImageText();
    }

    /// <summary>The user closed the window with its close button (it is hidden, not destroyed); the main window stops re-showing it.</summary>
    public event EventHandler? ClosedByUser;

    /// <summary>
    /// Shows the window (or raises it) next to the chat button and puts the cursor in the prompt box. A hidden window is anchored every time it
    /// is shown; a visible one only when <paramref name="reanchor"/> is true (the chat button was clicked again).
    /// </summary>
    public void ShowChat(Window owner, FrameworkElement? anchor, bool reanchor)
    {
        if (!ReferenceEquals(Owner, owner))
        {
            Owner = owner;
        }

        _host.Label.RefreshChatCaps();
        if (!IsVisible || reanchor)
        {
            PlaceNextToButton(owner, anchor);
        }

        if (!IsVisible)
        {
            Show();
        }

        Activate();
        ScrollToNewest();
        // The click that opened the window hands keyboard focus back to the canvas at input priority; take it after that.
        Dispatcher.BeginInvoke(
            DispatcherPriority.Background,
            new Action(() =>
            {
                PromptBox.Focus();
                Keyboard.Focus(PromptBox);
            }));
    }

    /// <summary>Hides the window (step change, project close); nothing is lost.</summary>
    public void HideChat()
    {
        if (IsVisible)
        {
            CaptureBounds();
            Hide();
        }
    }

    /// <summary>The main window is closing: save the place and really close.</summary>
    public void CloseForReal()
    {
        _closeForReal = true;
        Unsubscribe();
        if (IsVisible)
        {
            CaptureBounds();
        }

        Close();
    }

    private void Unsubscribe()
    {
        _host.Label.AgentChatReloaded -= OnChatReloaded;
        SizeChanged -= OnWindowSizeChanged;
        PromptBox.PreviewKeyDown -= OnPromptKeyDown;
        _host.PropertyChanged -= OnHostPropertyChanged;
    }

    // Easy mode hides the precision row of the prompt; the host's IsAdvancedMode is the only source.
    private void OnHostPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName == nameof(MainViewModel.IsAdvancedMode))
        {
            ApplyUiMode();
        }

        if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName == nameof(MainViewModel.SelectedImage))
        {
            RefreshStartImageText();
        }
    }

    private void ApplyUiMode()
    {
        Visibility precision = _host.IsAdvancedMode ? Visibility.Visible : Visibility.Collapsed;
        PrecisionCaption.Visibility = precision;
        PromptPrecisionRow.Visibility = precision;
    }

    private static ChatRect VirtualScreen()
    {
        return new ChatRect(
            SystemParameters.VirtualScreenLeft,
            SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth,
            SystemParameters.VirtualScreenHeight);
    }

    // Anchors the window to the round chat button: left edge 8 DIP right of the button, bottom edge on the button's bottom edge, inside the
    // work area of the monitor that holds the button. The button is measured in screen pixels and converted with its own DPI scale.
    private void PlaceNextToButton(Window owner, FrameworkElement? anchor)
    {
        ChatRect button;
        ChatRect work = VirtualScreen();
        if (anchor != null && anchor.IsLoaded && anchor.ActualWidth > 0 && anchor.ActualHeight > 0 && PresentationSource.FromVisual(anchor) != null)
        {
            DpiScale dpi = VisualTreeHelper.GetDpi(anchor);
            double sx = 1.0 / dpi.DpiScaleX;
            double sy = 1.0 / dpi.DpiScaleY;
            Point topLeft = anchor.PointToScreen(new Point(0, 0));
            Point bottomRight = anchor.PointToScreen(new Point(anchor.ActualWidth, anchor.ActualHeight));
            button = new ChatRect(topLeft.X * sx, topLeft.Y * sy, (bottomRight.X - topLeft.X) * sx, (bottomRight.Y - topLeft.Y) * sy);
            ChatRect? monitorWork = MonitorWorkArea((topLeft.X + bottomRight.X) / 2, (topLeft.Y + bottomRight.Y) / 2, sx, sy);
            if (monitorWork.HasValue)
            {
                work = monitorWork.Value;
            }
        }
        else
        {
            // No button to measure: open at the bottom-left corner of the main window.
            button = new ChatRect(owner.Left, owner.Top + Math.Max(0, owner.ActualHeight), 0, 0);
        }

        // The size stays as the user left it (a visible window keeps its current size); the position is never restored.
        double width = IsVisible && ActualWidth > 0 ? ActualWidth : _host.Settings.AgentChatWidth;
        double height = IsVisible && ActualHeight > 0 ? ActualHeight : _host.Settings.AgentChatHeight;
        ChatRect bounds = AgentChatWindowBounds.Anchor(button, width, height, work);
        WindowStartupLocation = WindowStartupLocation.Manual;
        MaxHeight = Math.Max(AgentChatWindowBounds.MinHeight, work.Height);
        Left = bounds.Left;
        Top = bounds.Top;
        Width = bounds.Width;
        Height = bounds.Height;
    }

    // The work area (screen minus taskbar) of the monitor nearest to a screen-pixel point, in DIPs; null when the system cannot tell.
    private static ChatRect? MonitorWorkArea(double pixelX, double pixelY, double scaleX, double scaleY)
    {
        try
        {
            IntPtr monitor = MonitorFromPoint(new NativePoint { X = (int)Math.Round(pixelX), Y = (int)Math.Round(pixelY) }, MonitorDefaultToNearest);
            if (monitor == IntPtr.Zero)
            {
                return null;
            }

            MonitorInfo info = new MonitorInfo { Size = Marshal.SizeOf(typeof(MonitorInfo)) };
            if (!GetMonitorInfo(monitor, ref info))
            {
                return null;
            }

            return new ChatRect(
                info.Work.Left * scaleX,
                info.Work.Top * scaleY,
                (info.Work.Right - info.Work.Left) * scaleX,
                (info.Work.Bottom - info.Work.Top) * scaleY);
        }
        catch (Exception ex) when (ex is DllNotFoundException || ex is EntryPointNotFoundException || ex is MarshalDirectiveException)
        {
            return null;
        }
    }

    private const uint MonitorDefaultToNearest = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(NativePoint point, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    // Stores the size in the settings object and writes the settings file (only called when the window hides or the app closes).
    private void CaptureBounds()
    {
        if (WindowState != WindowState.Normal || ActualWidth <= 0 || ActualHeight <= 0)
        {
            return;
        }

        _host.Settings.AgentChatWidth = ActualWidth;
        _host.Settings.AgentChatHeight = ActualHeight;
        _host.PersistSettings();
    }

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_closeForReal)
        {
            return;
        }

        // The close button hides the window; the history, the prompt and the options stay as they are.
        e.Cancel = true;
        HideChat();
        ClosedByUser?.Invoke(this, EventArgs.Empty);
    }

    private void OnWindowSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // The controls below the history scroll instead of being clipped when the window is short.
        BottomScroll.MaxHeight = Math.Max(MinBottomHeight, ActualHeight - ChromeHeight - MinHistoryHeight);
    }

    private void OnChatReloaded(object? sender, EventArgs e)
    {
        ScrollToNewest();
    }

    private void ScrollToNewest()
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => ChatScroll.ScrollToEnd()));
    }

    // The start-image picker lists every image of the project; picking one makes it the open image, where Images All / Remaining begin.
    private bool _startImageSync;

    private void RefreshStartImageText()
    {
        StartImageText.Text = _host.SelectedImage?.DisplayName ?? string.Empty;
    }

    private void OnTuningClick(object sender, RoutedEventArgs e)
    {
        TuningPopup.IsOpen = true;
    }

    private void OnStartImageClick(object sender, RoutedEventArgs e)
    {
        _startImageSync = true;
        try
        {
            StartImageList.ItemsSource = _host.Images;
            StartImageList.SelectedItem = _host.SelectedImage;
        }
        finally
        {
            _startImageSync = false;
        }

        StartImagePopup.IsOpen = true;
        if (StartImageList.SelectedItem != null)
        {
            StartImageList.ScrollIntoView(StartImageList.SelectedItem);
        }
    }

    private void OnStartImageSelected(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_startImageSync || StartImageList.SelectedItem is not ViewModels.ImageItemViewModel item)
        {
            return;
        }

        StartImagePopup.IsOpen = false;
        _host.SelectedImage = item;
        RefreshStartImageText();
    }

    // Files dropped on the composer are attached as hints (pictures and notes; other kinds are skipped by AddAttachments).
    private void OnComposerDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnComposerDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
        {
            _host.Label.AddAttachments(files);
        }

        e.Handled = true;
    }

    // The pill opens one menu: the models (check on the one in use), a separator, the efforts that model supports (check on the one in use).
    // The rows are built at the click because the model list loads after the window exists.
    private void OnModelPillClick(object sender, RoutedEventArgs e)
    {
        LabelStepViewModel label = _host.Label;
        System.Windows.Controls.ContextMenu menu = new System.Windows.Controls.ContextMenu
        {
            PlacementTarget = ModelPill,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Top,
            MinWidth = ModelPill.ActualWidth,
        };
        foreach (AgentPillItem row in label.AgentPillItems)
        {
            // The effort rows moved to the Effort slider next to this button; the menu keeps the models only.
            if (row.IsSeparator || row.Kind == AgentPillItemKind.Effort)
            {
                continue;
            }

            Wpf.Ui.Controls.MenuItem item = new Wpf.Ui.Controls.MenuItem
            {
                Header = row.Header,
                Command = label.SelectPillItemCommand,
                CommandParameter = row,
            };
            if (row.IsChecked)
            {
                item.Icon = new Wpf.Ui.Controls.SymbolIcon { Symbol = Wpf.Ui.Controls.SymbolRegular.Checkmark24 };
            }

            System.Windows.Automation.AutomationProperties.SetAutomationId(item, row.AutomationId);
            menu.Items.Add(item);
        }

        menu.IsOpen = true;
    }

    private void OnPromptKeyDown(object sender, KeyEventArgs e)
    {
        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        bool alt = (Keyboard.Modifiers & ModifierKeys.Alt) != 0;
        if (!AgentChatRules.IsSubmitKey(e.Key == Key.Enter, shift, ctrl, alt))
        {
            return;
        }

        e.Handled = true;
        if (_host.Label.TryHandleSlashCommand())
        {
            return;
        }

        System.Windows.Input.ICommand run = _host.Label.RunChatCommand;
        if (run.CanExecute(null))
        {
            run.Execute(null);
        }
    }
}
