using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using AvalonDock;
using AvalonDock.Layout;
using AvalonDock.Serializer.Xml;
using AvalonDock.Themes;
using EasyAI.Desktop.Infrastructure;

namespace EasyAI.Desktop.Views;

/// <summary>
/// Owns the AvalonDock layout of the main window: the central document (canvas or training charts), the two dockable panels
/// (merged Label / Shape list, Files), their visibility, the default arrangement, the stored layout XML, the light / dark chrome and the compact mode
/// (panels auto-hide into the side strip). Every AvalonDock call is wrapped: a failure falls back to the default arrangement.
/// </summary>
internal sealed class DockLayoutController
{
    private readonly DockingManager _dock;
    private readonly Func<string, FrameworkElement> _contentOf;
    private readonly FrameworkElement _documentContent;
    private readonly Func<string, string> _titleOf;
    private readonly string _documentTitle;
    private readonly Action<string> _log;
    private readonly Dictionary<string, LayoutAnchorable> _anchorables = new Dictionary<string, LayoutAnchorable>(StringComparer.Ordinal);
    private readonly List<LayoutAnchorable> _autoHidden = new List<LayoutAnchorable>();
    private LayoutDocument? _document;
    private string? _wideSnapshot;
    private bool _compact;
    private bool _syncing;

    public DockLayoutController(
        DockingManager dock,
        Func<string, FrameworkElement> contentOf,
        Func<string, string> titleOf,
        FrameworkElement documentContent,
        string documentTitle,
        Action<string> log)
    {
        _dock = dock;
        _contentOf = contentOf;
        _titleOf = titleOf;
        _documentContent = documentContent;
        _documentTitle = documentTitle;
        _log = log;
    }

    /// <summary>A panel became visible or hidden through its own title bar (close button, drag, restore). Not raised for <see cref="SetVisible"/>.</summary>
    public event Action<string, bool>? VisibilityChanged;

    public bool IsCompact => _compact;

    // ---------- build / restore ----------

    /// <summary>Builds the default arrangement: canvas in the document area, the two panels stacked in the right column.</summary>
    public void BuildDefault(double rightWidth)
    {
        DetachContents();
        _anchorables.Clear();
        _autoHidden.Clear();

        _document = new LayoutDocument
        {
            ContentId = DockPanelIds.Canvas,
            Title = _documentTitle,
            Content = Adopt(_documentContent),
            CanClose = false,
            CanFloat = false,
        };

        var column = new LayoutAnchorablePaneGroup
        {
            Orientation = Orientation.Vertical,
            DockWidth = new GridLength(rightWidth),
        };
        foreach (string id in DockPanelIds.DefaultOrder)
        {
            LayoutAnchorable anchorable = CreateAnchorable(id);
            column.Children.Add(new LayoutAnchorablePane(anchorable)
            {
                DockHeight = new GridLength(DockPanelIds.DefaultHeightWeight(id), GridUnitType.Star),
                DockMinHeight = DockPanelIds.DefaultMinHeight(id),
            });
        }

        var root = new LayoutPanel { Orientation = Orientation.Horizontal };
        root.Children.Add(new LayoutDocumentPane(_document) { ShowHeader = false });
        root.Children.Add(column);
        _dock.Layout = new LayoutRoot { RootPanel = root };
    }

    /// <summary>Applies a stored layout. False (and nothing changed) when the XML is unusable; the caller then builds the default.</summary>
    public bool TryRestore(string? layoutXml)
    {
        if (!DockLayoutRules.LooksUsable(layoutXml))
        {
            return false;
        }

        try
        {
            DetachContents();
            _anchorables.Clear();
            _autoHidden.Clear();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var serializer = new XmlLayoutSerializer(_dock);
            serializer.LayoutSerializationCallback += (sender, args) =>
            {
                string? id = args.Model.ContentId;
                if (id == DockPanelIds.Canvas && args.Model is LayoutDocument document)
                {
                    document.Title = _documentTitle;
                    document.CanClose = false;
                    document.CanFloat = false;
                    args.Content = Adopt(_documentContent);
                    _document = document;
                    seen.Add(id);
                }
                else if (DockPanelIds.IsPanel(id) && args.Model is LayoutAnchorable anchorable)
                {
                    anchorable.Title = _titleOf(id!);
                    args.Content = Adopt(_contentOf(id!));
                    Register(id!, anchorable);
                    seen.Add(id!);
                }
                else
                {
                    args.Cancel = true;
                }
            };

            using (var reader = new StringReader(layoutXml!))
            {
                serializer.Deserialize(reader);
            }

            if (seen.Count == DockPanelIds.DefaultOrder.Count + 1)
            {
                // The canvas needs no tab header, also in a restored layout.
                if (_document?.Parent is LayoutDocumentPane documentPane)
                {
                    documentPane.ShowHeader = false;
                }

                return true;
            }

            _log("Stored dock layout lacks a panel; the default arrangement is used.");
            return false;
        }
        catch (Exception ex)
        {
            _log("Restore dock layout failed: " + ex);
            return false;
        }
    }

    /// <summary>Current layout as XML for layout.json (the wide layout while compact mode has panels auto-hidden). Null on failure.</summary>
    public string? Capture()
    {
        if (_compact && _wideSnapshot != null)
        {
            return _wideSnapshot;
        }

        return SerializeNow();
    }

    private string? SerializeNow()
    {
        try
        {
            var serializer = new XmlLayoutSerializer(_dock);
            using (var writer = new StringWriter())
            {
                serializer.Serialize(writer);
                return writer.ToString();
            }
        }
        catch (Exception ex)
        {
            _log("Serialize dock layout failed: " + ex);
            return null;
        }
    }

    /// <summary>Hides panels that an old layout file (before docking) had hidden.</summary>
    public void ApplyLegacyHidden(IEnumerable<string> ids)
    {
        foreach (string id in ids)
        {
            SetVisible(id, false);
        }
    }

    // ---------- visibility ----------

    public bool IsVisible(string id)
    {
        return _anchorables.TryGetValue(id, out LayoutAnchorable? anchorable) && anchorable.IsVisible;
    }

    /// <summary>Shows or hides one panel (View menu). Does not raise <see cref="VisibilityChanged"/>.</summary>
    public void SetVisible(string id, bool visible)
    {
        if (!_anchorables.TryGetValue(id, out LayoutAnchorable? anchorable) || anchorable.IsVisible == visible)
        {
            return;
        }

        _syncing = true;
        try
        {
            if (visible)
            {
                anchorable.Show();
            }
            else
            {
                anchorable.Hide();
            }
        }
        catch (Exception ex)
        {
            _log("Dock panel " + id + " visibility failed: " + ex);
        }
        finally
        {
            _syncing = false;
        }
    }

    /// <summary>Changes the title bar text of a panel (the right panel is renamed with the Label / Train tab).</summary>
    public void SetTitle(string id, string title)
    {
        if (_anchorables.TryGetValue(id, out LayoutAnchorable? anchorable))
        {
            anchorable.Title = title;
        }
    }

    // ---------- theme ----------

    /// <summary>Light or dark docking chrome (title bars, tabs, splitters) to match the WPF-UI theme.</summary>
    public void ApplyTheme(bool dark)
    {
        try
        {
            _dock.Theme = dark ? new Vs2013DarkTheme() : (Theme)new Vs2013LightTheme();
        }
        catch (Exception ex)
        {
            _log("Dock theme failed: " + ex);
        }
    }

    /// <summary>
    /// Panel title bars in the accent colour (active) and a softer accent (inactive). Call after <see cref="ApplyTheme"/>:
    /// the entries sit in the docking manager's own resources, so they win over the theme dictionary.
    /// </summary>
    public void ApplyPalette(ThemeColors colors)
    {
        try
        {
            uint inactive = ThemePalette.Mix(colors.Accent, 0x808080, 0.45);
            _dock.Resources[AvalonDock.Themes.VS2013.Themes.ResourceKeys.ToolWindowCaptionActiveBackground] = Solid(colors.Accent);
            _dock.Resources[AvalonDock.Themes.VS2013.Themes.ResourceKeys.ToolWindowCaptionActiveText] =
                Solid(ThemePalette.TextOn(colors.Accent, colors.Accent));
            _dock.Resources[AvalonDock.Themes.VS2013.Themes.ResourceKeys.ToolWindowCaptionInactiveBackground] = Solid(inactive);
            _dock.Resources[AvalonDock.Themes.VS2013.Themes.ResourceKeys.ToolWindowCaptionInactiveText] =
                Solid(ThemePalette.TextOn(inactive, inactive));
        }
        catch (Exception ex)
        {
            _log("Dock palette failed: " + ex);
        }
    }

    private static System.Windows.Media.SolidColorBrush Solid(uint rgb)
    {
        var brush = new System.Windows.Media.SolidColorBrush(ThemeApplier.ToColor(rgb));
        brush.Freeze();
        return brush;
    }

    // ---------- compact mode ----------

    /// <summary>Narrow window: docked panels collapse into the side strip (auto-hide); floating panels stay as they are.</summary>
    public void EnterCompact()
    {
        if (_compact)
        {
            return;
        }

        _compact = true;
        _wideSnapshot = SerializeNow();
        _autoHidden.Clear();
        foreach (LayoutAnchorable anchorable in _anchorables.Values.ToList())
        {
            try
            {
                if (anchorable.IsVisible && !anchorable.IsFloating && !anchorable.IsAutoHidden)
                {
                    anchorable.ToggleAutoHide();
                    _autoHidden.Add(anchorable);
                }
            }
            catch (Exception ex)
            {
                _log("Dock auto-hide failed: " + ex);
            }
        }
    }

    /// <summary>Wide window again: the panels that compact mode auto-hid return to their docked place.</summary>
    public void ExitCompact(double rightWidth)
    {
        if (!_compact)
        {
            return;
        }

        string? snapshot = _wideSnapshot;
        _compact = false;
        _wideSnapshot = null;
        bool failed = false;
        foreach (LayoutAnchorable anchorable in _autoHidden)
        {
            try
            {
                if (anchorable.IsAutoHidden)
                {
                    anchorable.ToggleAutoHide();
                }
            }
            catch (Exception ex)
            {
                failed = true;
                _log("Dock restore from auto-hide failed: " + ex);
            }
        }

        _autoHidden.Clear();
        if (failed && !TryRestore(snapshot))
        {
            BuildDefault(rightWidth);
        }
    }

    /// <summary>The layout object is replaced (reset): compact mode starts over, the caller enters it again if still narrow.</summary>
    public void ForgetCompact()
    {
        _compact = false;
        _wideSnapshot = null;
        _autoHidden.Clear();
    }

    // ---------- helpers ----------

    private LayoutAnchorable CreateAnchorable(string id)
    {
        var anchorable = new LayoutAnchorable
        {
            ContentId = id,
            Title = _titleOf(id),
            Content = Adopt(_contentOf(id)),
            CanClose = true,
            CanFloat = true,
            CanAutoHide = true,
        };
        Register(id, anchorable);
        return anchorable;
    }

    private void Register(string id, LayoutAnchorable anchorable)
    {
        _anchorables[id] = anchorable;
        anchorable.PropertyChanged -= OnAnchorablePropertyChanged;
        anchorable.PropertyChanged += OnAnchorablePropertyChanged;
    }

    private void OnAnchorablePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_syncing || e.PropertyName != nameof(LayoutAnchorable.IsVisible) || sender is not LayoutAnchorable anchorable)
        {
            return;
        }

        string? id = anchorable.ContentId;
        if (id != null && _anchorables.TryGetValue(id, out LayoutAnchorable? known) && ReferenceEquals(known, anchorable))
        {
            VisibilityChanged?.Invoke(id, anchorable.IsVisible);
        }
    }

    /// <summary>
    /// Takes an element out of whatever currently holds it (the hidden store the panels are declared in, or an old
    /// anchorable) so it can become the content of a layout item: a control can only have one logical parent.
    /// </summary>
    private static FrameworkElement Adopt(FrameworkElement element)
    {
        switch (LogicalTreeHelper.GetParent(element))
        {
            case System.Windows.Controls.Panel panel: panel.Children.Remove(element); break;
            case System.Windows.Controls.ContentControl content: content.Content = null; break;
            case System.Windows.Controls.Decorator decorator: decorator.Child = null; break;
        }

        return element;
    }

    /// <summary>A control can only have one parent: release the panels from the old layout before a new one is built or restored.</summary>
    private void DetachContents()
    {
        foreach (LayoutAnchorable anchorable in _anchorables.Values)
        {
            anchorable.PropertyChanged -= OnAnchorablePropertyChanged;
            anchorable.Content = null;
        }

        if (_document != null)
        {
            _document.Content = null;
        }
    }
}
