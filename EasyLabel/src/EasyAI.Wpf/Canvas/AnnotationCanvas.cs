using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using EasyAI.Desktop.Infrastructure;
using EasyAI.Desktop.Resources;
using EasyAI.Desktop.ViewModels;
using EasyLabel.Core.Editing;
using EasyLabel.Core.Geometry;
using EasyLabel.Core.Model;
using EasyLabel.SegAI.AutoLabel;
using PointF = System.Drawing.PointF;

namespace EasyAI.Desktop.Annotation;

/// <summary>
/// Drawing surface of the annotation canvas. The image is drawn under a matrix transform built from the
/// <see cref="ViewTransform"/>; shapes, handles and drafts are drawn afterwards in screen space (DIPs) so line widths and handle
/// sizes do not scale with the zoom. Pointer input is forwarded to the <see cref="CanvasEditSession"/> of the
/// <see cref="CanvasViewModel"/> in IMAGE coordinates; this class holds no document state.
/// </summary>
public sealed class AnnotationCanvas : FrameworkElement
{
    public static readonly DependencyProperty ModelProperty = DependencyProperty.Register(
        nameof(Model),
        typeof(CanvasViewModel),
        typeof(AnnotationCanvas),
        new PropertyMetadata(null, OnModelChanged));

    private const double FitMargin = 8.0;
    private const double CloseTargetRadius = 6.0;
    private const double DraftDotRadius = 3.5;
    private const double PointMarkerRadius = 4.5;

    private ViewTransform _view = new ViewTransform(1.0, 0.0, 0.0);
    private bool _autoFit = true;
    private bool _panning;
    private MouseButton _panButton;
    private bool _hadImage;
    private int _previousImageWidth;
    private int _previousImageHeight;
    private bool _altClickUsed;
    private bool _aiBoxDragging;
    private PointF _aiBoxStart;
    private PointF _aiBoxNow;
    private Point _panLast;
    private BitmapScalingMode _scalingMode = BitmapScalingMode.HighQuality;

    // Accept / Reject icons next to each pending suggestion (drawn on this surface, hit-tested in the mouse handlers).
    private SuggestionButtons[] _buttons = Array.Empty<SuggestionButtons>();
    private IReadOnlyList<SegAiSuggestion>? _buttonsSource;
    private double _buttonsScale;
    private double _buttonsOffsetX;
    private double _buttonsOffsetY;
    private double _buttonsWidth;
    private double _buttonsHeight;
    private int _buttonsImageWidth;
    private int _buttonsImageHeight;
    private SegAiSuggestion? _hoverSuggestion;
    private SuggestionButtonKind _hoverKind;
    private ToolTip? _iconTip;
    private long _iconClickTick;
    private Point _iconClickAt;

    private static readonly Brush AcceptIconFill = FrozenBrush(46, 160, 67);
    private static readonly Brush AcceptIconHover = FrozenBrush(86, 200, 108);
    private static readonly Brush RejectIconFill = FrozenBrush(214, 48, 49);
    private static readonly Brush RejectIconHover = FrozenBrush(240, 104, 104);
    private static readonly Pen IconRing = FrozenPen(Brushes.White, 1.0);
    private static readonly Pen IconGlyphPen = FrozenPen(Brushes.White, 2.4);
    private static readonly Geometry CheckGlyph = BuildGlyph(new Point(-5.0, 0.5), new Point(-1.5, 4.0), new Point(5.0, -3.5));
    private static readonly Geometry CrossGlyph = BuildCrossGlyph(4.0);

    // Agent review: a remove suggestion draws its target dashed red with a cross; a replace suggestion highlights its target.
    private static readonly Pen RemoveDashedPen = FrozenDashedPen(RejectIconFill, 2.0);
    private static readonly Pen RemoveCrossPen = FrozenPen(RejectIconFill, 2.5);
    private static readonly Pen ReplaceTargetPen = FrozenPen(FrozenBrush(245, 165, 36), 3.0);

    public AnnotationCanvas()
    {
        Focusable = true;
        FocusVisualStyle = null;
        ClipToBounds = true;
        RenderOptions.SetBitmapScalingMode(this, _scalingMode);
        Unloaded -= OnCanvasUnloaded;
        Unloaded += OnCanvasUnloaded;
    }

    private void OnCanvasUnloaded(object sender, RoutedEventArgs e) => ClearSuggestionHover();

    public CanvasViewModel? Model
    {
        get => (CanvasViewModel?)GetValue(ModelProperty);
        set => SetValue(ModelProperty, value);
    }

    private double DpiScale => VisualTreeHelper.GetDpi(this).DpiScaleX;

    private static void OnModelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        AnnotationCanvas canvas = (AnnotationCanvas)d;
        if (e.OldValue is CanvasViewModel old)
        {
            old.RenderRequested -= canvas.OnRenderRequested;
            old.ImageReplaced -= canvas.OnImageReplaced;
            old.FitRequested -= canvas.OnFitRequested;
            old.ActualSizeRequested -= canvas.OnActualSizeRequested;
            old.ZoomStepRequested -= canvas.OnZoomStepRequested;
            old.FitWidthRequested -= canvas.OnFitWidthRequested;
            old.ZoomPercentRequested -= canvas.OnZoomPercentRequested;
        }

        if (e.NewValue is CanvasViewModel model)
        {
            model.ZoomStepRequested -= canvas.OnZoomStepRequested;
            model.ZoomStepRequested += canvas.OnZoomStepRequested;
            model.FitWidthRequested -= canvas.OnFitWidthRequested;
            model.FitWidthRequested += canvas.OnFitWidthRequested;
            model.ZoomPercentRequested -= canvas.OnZoomPercentRequested;
            model.ZoomPercentRequested += canvas.OnZoomPercentRequested;
            model.RenderRequested -= canvas.OnRenderRequested;
            model.RenderRequested += canvas.OnRenderRequested;
            model.ImageReplaced -= canvas.OnImageReplaced;
            model.ImageReplaced += canvas.OnImageReplaced;
            model.FitRequested -= canvas.OnFitRequested;
            model.FitRequested += canvas.OnFitRequested;
            model.ActualSizeRequested -= canvas.OnActualSizeRequested;
            model.ActualSizeRequested += canvas.OnActualSizeRequested;
        }

        canvas._autoFit = true;
        canvas._hadImage = false;
        canvas._previousImageWidth = 0;
        canvas._previousImageHeight = 0;
        canvas.ApplyFit();
        canvas.InvalidateVisual();
    }

    private void OnRenderRequested(object? sender, EventArgs e)
    {
        if (_hoverKind != SuggestionButtonKind.None)
        {
            // The hovered suggestion was resolved or the tool changed: its icon is gone, so the tooltip and highlight go too.
            CanvasViewModel? model = Model;
            if (model == null || !SuggestionButtonsActive(model) || !model.Suggestions.Contains(_hoverSuggestion!, ReferenceEqualityComparer.Instance))
            {
                ClearSuggestionHover();
            }
        }

        if (!_panning)
        {
            if (Model?.Tool == CanvasTool.Hand)
            {
                Cursor = Cursors.Hand;
            }
            else if (Cursor == Cursors.Hand && _hoverKind == SuggestionButtonKind.None)
            {
                Cursor = null;
            }
        }

        InvalidateVisual();
    }

    private void OnImageReplaced(object? sender, EventArgs e)
    {
        CanvasViewModel? model = Model;
        if (model != null && model.KeepPreviousZoom && _hadImage && _previousImageWidth > 0 && _previousImageHeight > 0 && model.Image != null && model.ImageWidth > 0 && model.ImageHeight > 0
            && ActualWidth > 0 && ActualHeight > 0)
        {
            // Keep the fractional image position at the viewport centre as well as the zoom.
            _autoFit = false;
            _view = ViewTransform.KeepCentre(_view, _previousImageWidth, _previousImageHeight,
                model.ImageWidth, model.ImageHeight, ActualWidth, ActualHeight);
            _previousImageWidth = model.ImageWidth;
            _previousImageHeight = model.ImageHeight;
            UpdateScalingMode();
            ReportZoom();
            InvalidateVisual();
            return;
        }

        if (model?.Image == null)
        {
            _hadImage = false;
            _previousImageWidth = 0;
            _previousImageHeight = 0;
        }

        _autoFit = true;
        ApplyFit();
        InvalidateVisual();
    }

    /// <summary>Tells the view model the current zoom in percent (100 = one image pixel per physical pixel).</summary>
    private void ReportZoom()
    {
        Model?.ReportZoom(_view.Scale * DpiScale * 100.0);
    }

    private void OnZoomStepRequested(object? sender, int direction)
    {
        CanvasViewModel? model = Model;
        if (model == null || model.Image == null || ActualWidth <= 0 || ActualHeight <= 0)
        {
            return;
        }

        _view = _view.ZoomAt(ActualWidth / 2.0, ActualHeight / 2.0, direction);
        _autoFit = false;
        UpdateScalingMode();
        ReportZoom();
        InvalidateVisual();
    }

    private void OnFitWidthRequested(object? sender, EventArgs e)
    {
        CanvasViewModel? model = Model;
        if (model == null || model.Image == null || model.ImageWidth <= 0 || model.ImageHeight <= 0 || ActualWidth <= 0 || ActualHeight <= 0)
        {
            return;
        }

        double scale = Math.Min(ViewTransform.MaxScale, Math.Max(ViewTransform.MinScale, (ActualWidth - 2.0 * FitMargin) / model.ImageWidth));
        double height = model.ImageHeight * scale;
        // The whole width is visible; a tall image starts at the top, a short one is centred vertically.
        double offsetY = height <= ActualHeight ? (ActualHeight - height) / 2.0 : FitMargin;
        _view = new ViewTransform(scale, (ActualWidth - model.ImageWidth * scale) / 2.0, offsetY);
        _autoFit = false;
        UpdateScalingMode();
        ReportZoom();
        InvalidateVisual();
    }

    private void OnZoomPercentRequested(object? sender, double percent)
    {
        CanvasViewModel? model = Model;
        if (model == null || model.Image == null || ActualWidth <= 0 || ActualHeight <= 0 || percent <= 0.0)
        {
            return;
        }

        double scale = Math.Min(ViewTransform.MaxScale, Math.Max(ViewTransform.MinScale, percent / 100.0 / DpiScale));
        PointF center = _view.ToImage((float)(ActualWidth / 2.0), (float)(ActualHeight / 2.0));
        _view = new ViewTransform(scale, ActualWidth / 2.0 - center.X * scale, ActualHeight / 2.0 - center.Y * scale);
        _autoFit = false;
        UpdateScalingMode();
        ReportZoom();
        InvalidateVisual();
    }

    private void OnFitRequested(object? sender, EventArgs e)
    {
        _autoFit = true;
        ApplyFit();
        InvalidateVisual();
    }

    private void OnActualSizeRequested(object? sender, EventArgs e)
    {
        CanvasViewModel? model = Model;
        if (model == null || model.Image == null || ActualWidth <= 0 || ActualHeight <= 0)
        {
            return;
        }

        // 100% = one image pixel per physical pixel: DIPs per image pixel is 1 / DPI scale.
        double scale = Math.Min(ViewTransform.MaxScale, Math.Max(ViewTransform.MinScale, 1.0 / DpiScale));
        PointF center = _autoFit
            ? new PointF(model.ImageWidth / 2f, model.ImageHeight / 2f)
            : _view.ToImage((float)(ActualWidth / 2.0), (float)(ActualHeight / 2.0));
        _view = new ViewTransform(scale, ActualWidth / 2.0 - center.X * scale, ActualHeight / 2.0 - center.Y * scale);
        _autoFit = false;
        _hadImage = true;
        UpdateScalingMode();
        ReportZoom();
        InvalidateVisual();
    }

    private void ApplyFit()
    {
        CanvasViewModel? model = Model;
        if (model == null || model.Image == null || model.ImageWidth <= 0 || model.ImageHeight <= 0 || ActualWidth <= 0 || ActualHeight <= 0)
        {
            return;
        }

        double availW = Math.Max(1.0, ActualWidth - 2.0 * FitMargin);
        double availH = Math.Max(1.0, ActualHeight - 2.0 * FitMargin);
        double scale = Math.Min(availW / model.ImageWidth, availH / model.ImageHeight);
        scale = Math.Min(ViewTransform.MaxScale, Math.Max(ViewTransform.MinScale, scale));
        _view = new ViewTransform(
            scale,
            (ActualWidth - model.ImageWidth * scale) / 2.0,
            (ActualHeight - model.ImageHeight * scale) / 2.0);
        _hadImage = true;
        _previousImageWidth = model.ImageWidth;
        _previousImageHeight = model.ImageHeight;
        UpdateScalingMode();
        ReportZoom();
    }

    private void UpdateScalingMode()
    {
        BitmapScalingMode wanted = _view.Scale >= 2.0 ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.HighQuality;
        if (wanted != _scalingMode)
        {
            _scalingMode = wanted;
            RenderOptions.SetBitmapScalingMode(this, wanted);
        }
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        if (_autoFit)
        {
            ApplyFit();
        }
    }

    // ---------- pointer ----------

    private static EditModifiers Mods()
    {
        ModifierKeys keys = Keyboard.Modifiers;
        EditModifiers result = EditModifiers.None;
        if ((keys & ModifierKeys.Shift) != 0) result |= EditModifiers.Shift;
        if ((keys & ModifierKeys.Control) != 0) result |= EditModifiers.Ctrl;
        if ((keys & ModifierKeys.Alt) != 0) result |= EditModifiers.Alt;
        return result;
    }

    private PointF ToImage(MouseEventArgs e)
    {
        Point p = e.GetPosition(this);
        return _view.ToImage((float)p.X, (float)p.Y);
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        if (e.ChangedButton == MouseButton.Middle)
        {
            BeginPan(e);
            e.Handled = true;
            return;
        }

        if (e.ChangedButton == MouseButton.Left && Model is CanvasViewModel panModel && panModel.Tool == CanvasTool.Hand)
        {
            if (!panModel.InputBlocked)
            {
                BeginPan(e);
            }

            e.Handled = true;
            return;
        }

        if (e.ChangedButton == MouseButton.Right)
        {
            // Right click never starts or commits anything. With the select tool it only selects the shape under the pointer
            // (not an edit, no undo unit) and remembers the position for the "Add point to edge" entry of the context menu.
            CanvasViewModel? contextModel = Model;
            CanvasEditSession? contextSession = contextModel?.Session;
            if (contextModel != null && contextSession != null && !contextModel.InputBlocked && contextSession.StampActive)
            {
                // Stamp mode (CounterAI): right click deletes the stamped shape under the pointer.
                PointF sp = ToImage(e);
                contextSession.PointerDown(sp.X, sp.Y, (float)_view.Scale, Mods(), EditPointerButton.Right);
                contextModel.NotifyEdited();
                InvalidateVisual();
            }
            else if (contextModel != null && contextSession != null && !contextModel.InputBlocked
                && contextSession.Tool == CanvasTool.Select && !contextSession.HasDraft && !contextSession.IsDragging)
            {
                PointF c = ToImage(e);
                contextSession.SelectForContext(c.X, c.Y, (float)_view.Scale);
                contextModel.SetContextPoint(c.X, c.Y, (float)_view.Scale);
                contextModel.NotifyEdited();
            }

            e.Handled = true;
            return;
        }

        CanvasViewModel? model = Model;
        CanvasEditSession? session = model?.Session;
        if (e.ChangedButton != MouseButton.Left || model == null || session == null || _panning)
        {
            return;
        }

        if (model.InputBlocked)
        {
            // A project is opening: no edit may be queued against the session that is about to be replaced.
            e.Handled = true;
            return;
        }

        PointF p = ToImage(e);
        float scale = (float)_view.Scale;
        if (session.Tool == CanvasTool.AiPoints || session.Tool == CanvasTool.AiBox)
        {
            // AI prompt tools: the canvas view model owns the prompt; nothing reaches the edit session or the document.
            if (session.Tool == CanvasTool.AiBox)
            {
                _aiBoxDragging = true;
                _aiBoxStart = p;
                _aiBoxNow = p;
                CaptureMouse();
            }
            else if (e.ClickCount == 1)
            {
                model.AiAddPoint(p.X, p.Y, (Mods() & EditModifiers.Shift) != 0);
            }

            InvalidateVisual();
            e.Handled = true;
            return;
        }

        if (TryClickSuggestionButton(model, e))
        {
            // The check / X icon of a pending suggestion: resolves that suggestion only and never starts a selection or a drag.
            e.Handled = true;
            return;
        }

        if ((Keyboard.Modifiers & ModifierKeys.Alt) != 0 && session.Tool == CanvasTool.Select)
        {
            // Alt+click on a vertex of a selected polygon removes it (one undo unit; a polygon keeps at least 3 vertices).
            _altClickUsed = true;
            session.DeleteVertexAt(p.X, p.Y, scale);
            model.NotifyEdited();
            e.Handled = true;
            return;
        }

        if (session.Tool == CanvasTool.Select && e.ClickCount == 1 && model.Suggestions.Count > 0
            && model.TrySelectSuggestionAt(p.X, p.Y, scale, (Mods() & EditModifiers.Ctrl) != 0))
        {
            // A pending suggestion was picked (no real shape under the pointer); it is not part of the document.
            e.Handled = true;
            return;
        }

        if (e.ClickCount == 2 && (session.Tool == CanvasTool.Select || session.Tool == CanvasTool.Polygon || session.Tool == CanvasTool.LineStrip))
        {
            // The second click of a double click replaces PointerDown (session contract).
            session.DoubleClick(p.X, p.Y, scale, Mods());
        }
        else
        {
            session.PointerDown(p.X, p.Y, scale, Mods());
            if (session.IsDragging)
            {
                CaptureMouse();
            }
        }

        model.NotifyEdited();
        e.Handled = true;
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (_altClickUsed && e.Key == Key.System && (e.SystemKey == Key.LeftAlt || e.SystemKey == Key.RightAlt))
        {
            // Alt was only the vertex-delete modifier: do not let its release move focus to the window system menu.
            _altClickUsed = false;
            e.Handled = true;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_panning)
        {
            if ((_panButton == MouseButton.Left ? e.LeftButton : e.MiddleButton) != MouseButtonState.Pressed)
            {
                EndPan();
                return;
            }

            Point p = e.GetPosition(this);
            _view = new ViewTransform(_view.Scale, _view.OffsetX + (p.X - _panLast.X), _view.OffsetY + (p.Y - _panLast.Y));
            _panLast = p;
            _autoFit = false;
            InvalidateVisual();
            return;
        }

        CanvasViewModel? model = Model;
        if (model != null)
        {
            PointF pointer = ToImage(e);
            model.SetPointer(pointer.X, pointer.Y);
        }

        if (model?.Tool == CanvasTool.Hand)
        {
            Cursor = Cursors.Hand;
            ClearSuggestionHover();
            InvalidateVisual();
            return;
        }

        CanvasEditSession? session = model?.Session;
        if (model == null || session == null)
        {
            return;
        }

        if (_aiBoxDragging)
        {
            if (e.LeftButton != MouseButtonState.Pressed)
            {
                // The button was released without us seeing it: drop the box drag.
                _aiBoxDragging = false;
                ReleaseMouseCapture();
                InvalidateVisual();
                return;
            }

            _aiBoxNow = ToImage(e);
        }

        if (session.IsDragging && e.LeftButton != MouseButtonState.Pressed)
        {
            // The button was released without us seeing it: never leave a drag stuck.
            session.Cancel();
            ReleaseMouseCapture();
            model.NotifyEdited();
            return;
        }

        PointF q = ToImage(e);
        session.PointerMove(q.X, q.Y, (float)_view.Scale, Mods());
        Cursor = session.Tool == CanvasTool.Select ? null : Cursors.Cross;
        UpdateSuggestionHover(model, e.GetPosition(this));
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        ClearSuggestionHover();
        Model?.ClearPointer();
    }

    // ---------- suggestion icons (Accept = check, Reject = X) ----------

    /// <summary>The icons are live only where suggestions can be picked: Select tool, nothing being drawn / dragged, input not blocked.</summary>
    private bool SuggestionButtonsActive(CanvasViewModel model)
    {
        CanvasEditSession? session = model.Session;
        return session != null && model.Image != null && model.Suggestions.Count > 0 && !model.InputBlocked && !_panning && !_aiBoxDragging
            && session.Tool == CanvasTool.Select && !session.HasDraft && !session.IsDragging;
    }

    /// <summary>Icon rectangles of every suggestion for the current view (cached until the suggestion list, zoom, pan or size changes).</summary>
    private SuggestionButtons[] ButtonsFor(CanvasViewModel model)
    {
        IReadOnlyList<SegAiSuggestion> list = model.Suggestions;
        if (ReferenceEquals(list, _buttonsSource) && _buttonsScale == _view.Scale && _buttonsOffsetX == _view.OffsetX
            && _buttonsOffsetY == _view.OffsetY && _buttonsWidth == ActualWidth && _buttonsHeight == ActualHeight
            && _buttonsImageWidth == model.ImageWidth && _buttonsImageHeight == model.ImageHeight)
        {
            return _buttons;
        }

        ButtonRect[] boxes = new ButtonRect[list.Count];
        SuggestionAnchor[] anchors = new SuggestionAnchor[list.Count];
        for (int i = 0; i < boxes.Length; i++)
        {
            // A remove suggestion has no outline of its own: its icons sit at the target shape it would delete.
            LabelShape? target = list[i].Outer.Length == 0 ? TargetShapeOf(model, list[i]) : null;
            PointF[] outline = target != null ? OuterOf(target) : list[i].Outer;
            boxes[i] = SuggestionButtonLayout.BoundsOf(outline);
            // A rotated box carries its icons on its own top-right corner, turned with the box.
            anchors[i] = SuggestionButtonLayout.AnchorOf(target != null ? target.ShapeType : list[i].ShapeType, outline);
        }

        // Icons stay inside the part of the image that is on screen, so they remain reachable for boxes touching the border.
        ButtonRect image = new ButtonRect(_view.OffsetX, _view.OffsetY, model.ImageWidth * _view.Scale, model.ImageHeight * _view.Scale);
        ButtonRect visible = image.Intersect(new ButtonRect(0.0, 0.0, ActualWidth, ActualHeight));
        _buttons = SuggestionButtonLayout.Layout(boxes, anchors, _view.Scale, _view.OffsetX, _view.OffsetY, visible);
        _buttonsSource = list;
        _buttonsScale = _view.Scale;
        _buttonsOffsetX = _view.OffsetX;
        _buttonsOffsetY = _view.OffsetY;
        _buttonsWidth = ActualWidth;
        _buttonsHeight = ActualHeight;
        _buttonsImageWidth = model.ImageWidth;
        _buttonsImageHeight = model.ImageHeight;
        return _buttons;
    }

    /// <summary>Left press on an icon: accept / reject exactly that suggestion. Returns true when the press was consumed.</summary>
    private bool TryClickSuggestionButton(CanvasViewModel model, MouseButtonEventArgs e)
    {
        if (!SuggestionButtonsActive(model))
        {
            return false;
        }

        Point at = e.GetPosition(this);
        if (e.ClickCount > 1 && Environment.TickCount64 - _iconClickTick <= 700
            && Math.Abs(at.X - _iconClickAt.X) <= 4.0 && Math.Abs(at.Y - _iconClickAt.Y) <= 4.0)
        {
            // The repeat of a double click on an icon: the first press already resolved a suggestion, so a suggestion that slid under the
            // pointer must not be resolved too and nothing beneath may be edited.
            return true;
        }

        SuggestionButtonHit hit = SuggestionButtonLayout.HitTest(ButtonsFor(model), at.X, at.Y);
        if (hit.Kind == SuggestionButtonKind.None)
        {
            return false;
        }

        SegAiSuggestion suggestion = model.Suggestions[hit.Index];
        _iconClickTick = Environment.TickCount64;
        _iconClickAt = at;
        ClearSuggestionHover();
        if (hit.Kind == SuggestionButtonKind.Accept)
        {
            model.AcceptSuggestion(suggestion);
        }
        else
        {
            model.RejectSuggestion(suggestion);
        }

        Cursor = null;
        InvalidateVisual();
        return true;
    }

    /// <summary>Hover on an icon: Hand cursor, lighter fill and a one-word tooltip; anything else clears them.</summary>
    private void UpdateSuggestionHover(CanvasViewModel model, Point at)
    {
        SuggestionButtonHit hit = SuggestionButtonsActive(model)
            ? SuggestionButtonLayout.HitTest(ButtonsFor(model), at.X, at.Y)
            : SuggestionButtonHit.None;
        if (hit.Kind == SuggestionButtonKind.None)
        {
            ClearSuggestionHover();
            return;
        }

        Cursor = Cursors.Hand;
        SegAiSuggestion suggestion = model.Suggestions[hit.Index];
        if (ReferenceEquals(suggestion, _hoverSuggestion) && hit.Kind == _hoverKind)
        {
            return;
        }

        _hoverSuggestion = suggestion;
        _hoverKind = hit.Kind;
        SuggestionButtons buttons = _buttons[hit.Index];
        ShowIconTip(hit.Kind, buttons.ScreenBounds(hit.Kind == SuggestionButtonKind.Accept ? buttons.Accept : buttons.Reject));
    }

    private void ClearSuggestionHover()
    {
        _hoverSuggestion = null;
        _hoverKind = SuggestionButtonKind.None;
        if (_iconTip != null)
        {
            _iconTip.IsOpen = false;
        }
    }

    private void ShowIconTip(SuggestionButtonKind kind, ButtonRect button)
    {
        if (_iconTip == null)
        {
            _iconTip = new ToolTip { Placement = PlacementMode.Relative, PlacementTarget = this, IsHitTestVisible = false };
        }

        _iconTip.IsOpen = false;
        _iconTip.Content = kind == SuggestionButtonKind.Accept ? Strings.AcceptSuggestionIcon : Strings.RejectSuggestionIcon;
        _iconTip.HorizontalOffset = button.X;
        _iconTip.VerticalOffset = button.Bottom + 4.0;
        _iconTip.IsOpen = true;
    }

    private void DrawSuggestionButtons(DrawingContext dc, CanvasViewModel model)
    {
        if (!SuggestionButtonsActive(model))
        {
            return;
        }

        IReadOnlyList<SegAiSuggestion> list = model.Suggestions;
        SuggestionButtons[] buttons = ButtonsFor(model);
        for (int i = 0; i < buttons.Length && i < list.Count; i++)
        {
            if (!buttons[i].Visible)
            {
                continue;
            }

            bool hovered = ReferenceEquals(list[i], _hoverSuggestion);
            bool rotated = buttons[i].AngleDegrees != 0.0;
            if (rotated)
            {
                dc.PushTransform(new RotateTransform(buttons[i].AngleDegrees, buttons[i].PivotX, buttons[i].PivotY));
            }

            DrawIcon(dc, buttons[i].Accept, hovered && _hoverKind == SuggestionButtonKind.Accept ? AcceptIconHover : AcceptIconFill, CheckGlyph);
            DrawIcon(dc, buttons[i].Reject, hovered && _hoverKind == SuggestionButtonKind.Reject ? RejectIconHover : RejectIconFill, CrossGlyph);
            if (rotated)
            {
                dc.Pop();
            }
        }
    }

    /// <summary>One round icon: filled disc with a thin light ring and a vector glyph, all in DIPs so it keeps its size at every zoom.</summary>
    private static void DrawIcon(DrawingContext dc, ButtonRect button, Brush fill, Geometry glyph)
    {
        Point centre = new Point(button.X + button.Width / 2.0, button.Y + button.Height / 2.0);
        double radius = button.Width / 2.0 - 0.5;
        dc.DrawEllipse(fill, IconRing, centre, radius, radius);
        dc.PushTransform(new TranslateTransform(centre.X, centre.Y));
        dc.DrawGeometry(null, IconGlyphPen, glyph);
        dc.Pop();
    }

    private static Brush FrozenBrush(byte r, byte g, byte b)
    {
        SolidColorBrush brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    private static Pen FrozenPen(Brush brush, double thickness)
    {
        Pen pen = new Pen(brush, thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        pen.Freeze();
        return pen;
    }

    private static Pen FrozenDashedPen(Brush brush, double thickness)
    {
        Pen pen = new Pen(brush, thickness) { DashStyle = DashStyles.Dash, LineJoin = PenLineJoin.Round };
        pen.Freeze();
        return pen;
    }

    /// <summary>
    /// The existing shape a replace / remove suggestion refers to (by <see cref="SegAiSuggestion.TargetIndex"/>), or null for a plain
    /// suggestion or an index that is not in the document any more.
    /// </summary>
    private static LabelShape? TargetShapeOf(CanvasViewModel model, SegAiSuggestion suggestion)
    {
        LabelMeDocument? document = model.Document;
        int? index = suggestion.TargetIndex;
        if (suggestion.Action == null || document == null || !index.HasValue || index.Value < 0 || index.Value >= document.Shapes.Count)
        {
            return null;
        }

        return document.Shapes[index.Value];
    }

    /// <summary>An open poly-line glyph centred on the origin.</summary>
    private static Geometry BuildGlyph(Point first, params Point[] rest)
    {
        StreamGeometry geometry = new StreamGeometry();
        using (StreamGeometryContext ctx = geometry.Open())
        {
            ctx.BeginFigure(first, false, false);
            ctx.PolyLineTo(rest, true, true);
        }

        geometry.Freeze();
        return geometry;
    }

    /// <summary>The X glyph: two diagonals of half-length <paramref name="arm"/> centred on the origin.</summary>
    private static Geometry BuildCrossGlyph(double arm)
    {
        StreamGeometry geometry = new StreamGeometry();
        using (StreamGeometryContext ctx = geometry.Open())
        {
            ctx.BeginFigure(new Point(-arm, -arm), false, false);
            ctx.LineTo(new Point(arm, arm), true, true);
            ctx.BeginFigure(new Point(-arm, arm), false, false);
            ctx.LineTo(new Point(arm, -arm), true, true);
        }

        geometry.Freeze();
        return geometry;
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        if (_panning && e.ChangedButton == _panButton)
        {
            EndPan();
            e.Handled = true;
            return;
        }

        if (e.ChangedButton == MouseButton.Middle || (e.ChangedButton == MouseButton.Left && Model?.Tool == CanvasTool.Hand))
        {
            if (!_panning)
            {
                _aiBoxDragging = false;
                ReleaseMouseCapture();
                InvalidateVisual();
            }

            e.Handled = true;
            return;
        }

        if (e.ChangedButton == MouseButton.Right)
        {
            // Left unhandled so the context menu can open; OnContextMenuOpening vetoes it while drawing.
            return;
        }

        if (e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        CanvasViewModel? model = Model;
        CanvasEditSession? session = model?.Session;
        if (_aiBoxDragging)
        {
            // AI-Box: the dragged rectangle is the prompt (a drag shorter than a few pixels is only a click and is ignored).
            PointF end = ToImage(e);
            _aiBoxDragging = false;
            if (IsMouseCaptured && !_panning)
            {
                ReleaseMouseCapture();
            }

            model?.AiSubmitBox(_aiBoxStart.X, _aiBoxStart.Y, end.X, end.Y);
            InvalidateVisual();
            e.Handled = true;
            return;
        }

        if (model != null && session != null && session.IsDragging)
        {
            PointF p = ToImage(e);
            session.PointerUp(p.X, p.Y, (float)_view.Scale, Mods());
            model.NotifyEdited();
        }

        if (IsMouseCaptured && !_panning)
        {
            ReleaseMouseCapture();
        }

        e.Handled = true;
    }

    protected override void OnContextMenuOpening(ContextMenuEventArgs e)
    {
        CanvasViewModel? model = Model;
        CanvasEditSession? session = model?.Session;
        if (model == null || model.Image == null || model.InputBlocked || _panning
            || (session != null && (session.HasDraft || session.IsDragging || session.StampActive)))
        {
            // Right click while drawing (or without an image) opens nothing and records nothing.
            e.Handled = true;
            return;
        }

        base.OnContextMenuOpening(e);
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (_panning)
        {
            _panning = false;
            Cursor = Model?.Tool == CanvasTool.Hand ? Cursors.Hand : null;
        }

        if (_aiBoxDragging)
        {
            _aiBoxDragging = false;
            InvalidateVisual();
        }

        CanvasViewModel? model = Model;
        CanvasEditSession? session = model?.Session;
        if (model != null && session != null && session.IsDragging)
        {
            // Capture was taken away mid drag: revert, record nothing.
            session.Cancel();
            model.NotifyEdited();
        }
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        CanvasViewModel? model = Model;
        if (model == null || model.Image == null)
        {
            return;
        }

        Point p = e.GetPosition(this);
        double factor = Math.Pow(ViewTransform.ZoomStep, e.Delta / 120.0);
        _view = _view.ZoomAtFactor(p.X, p.Y, factor);
        _autoFit = false;
        UpdateScalingMode();
        ReportZoom();
        InvalidateVisual();
        e.Handled = true;
    }

    private void BeginPan(MouseButtonEventArgs e)
    {
        if (_panning)
        {
            return;
        }

        _panning = true;
        _panButton = e.ChangedButton;
        _panLast = e.GetPosition(this);
        Cursor = Cursors.SizeAll;
        CaptureMouse();
    }

    private void EndPan()
    {
        bool wasPanning = _panning;
        _panning = false;
        Cursor = Model?.Tool == CanvasTool.Hand ? Cursors.Hand : null;
        CanvasEditSession? session = Model?.Session;
        if (wasPanning && IsMouseCaptured && (session == null || !session.IsDragging))
        {
            ReleaseMouseCapture();
        }
    }

    // ---------- rendering ----------

    private Point S(PointF imagePoint)
    {
        PointF s = _view.ToScreen(imagePoint.X, imagePoint.Y);
        return new Point(s.X, s.Y);
    }

    private Brush ThemeBrush(string key, Brush fallback) => TryFindResource(key) as Brush ?? fallback;

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        Rect bounds = new Rect(RenderSize);
        // A filled background also makes empty areas hit-testable for pan, wheel and tool clicks.
        // The surround follows the colour theme (ThemeApplier); the card colour stays the fallback before a palette exists.
        dc.DrawRectangle(ThemeBrush(ThemeApplier.SurroundBrushKey, ThemeBrush("CardBackgroundFillColorDefaultBrush", Brushes.Transparent)), null, bounds);

        CanvasViewModel? model = Model;
        if (model == null || model.Image == null || model.ImageWidth <= 0 || model.ImageHeight <= 0)
        {
            return;
        }

        dc.PushTransform(new MatrixTransform(_view.Scale, 0.0, 0.0, _view.Scale, _view.OffsetX, _view.OffsetY));
        dc.DrawImage(model.DisplayImage ?? model.Image, new Rect(0.0, 0.0, model.ImageWidth, model.ImageHeight));
        dc.Pop();

        // Pending suggestions sit above the image and under the real shapes.
        DrawSuggestions(dc, model);

        CanvasEditSession? session = model.Session;
        LabelMeDocument? document = model.Document;
        if (session == null || document == null)
        {
            return;
        }

        float scale = (float)_view.Scale;
        float handle = session.HandleScreenSize;
        Brush handleFill = ThemeBrush("TextFillColorPrimaryBrush", Brushes.White);
        var shapes = document.Shapes;

        for (int i = 0; i < shapes.Count; i++)
        {
            bool dragged = session.DraftShapes.TryGetValue(i, out LabelShape? draft);
            LabelShape shape = dragged && draft != null ? draft : shapes[i];
            if (!CanvasEditSession.IsEditable(shape) || !model.IsShapeVisible(i))
            {
                continue;
            }

            AnnotationPalette.Swatch swatch = AnnotationPalette.For(model.ColorOf(shape.Label));
            bool selected = session.SelectedIndices.Contains(i);
            PointF[][]? holeSet = null;
            if (!dragged && model.Holes != null && model.Holes.TryGet(shapes[i], out PointF[][]? found))
            {
                holeSet = found;
            }

            DrawShape(dc, shape, holeSet, swatch, selected, false);
        }

        if (model.ShowLabelOnCanvas)
        {
            DrawShapeLabels(dc, model, session, shapes);
        }
        else if (model.IsCountTool || model.IsStampKind)
        {
            DrawCountNumbers(dc, model, session, shapes);
        }

        foreach (int index in session.SelectedIndices)
        {
            if (index < 0 || index >= shapes.Count || !model.IsShapeVisible(index))
            {
                continue;
            }

            LabelShape shape = session.DraftShapes.TryGetValue(index, out LabelShape? draft) && draft != null ? draft : shapes[index];
            if (session.Tool != CanvasTool.Hand && CanvasEditSession.IsEditable(shape))
            {
                int vertex = session.SelectedVertexShape == index ? session.SelectedVertexIndex : -1;
                DrawHandles(dc, shape, AnnotationPalette.For(model.ColorOf(shape.Label)), scale, handle, handleFill, vertex);
            }
        }

        DrawCreation(dc, model, session, handleFill);
        DrawMarquee(dc, model, session);
        DrawAiPrompt(dc, model, session);

        // Last, so the Accept / Reject icons sit above the boxes, labels and real shapes.
        DrawSuggestionButtons(dc, model);
    }

    /// <summary>The sweep rectangle of the Select tool (dashed, class-colour fill) while the pointer is dragged from empty space.</summary>
    private void DrawMarquee(DrawingContext dc, CanvasViewModel model, CanvasEditSession session)
    {
        System.Drawing.RectangleF? area = session.MarqueeRect;
        if (!area.HasValue)
        {
            return;
        }

        AnnotationPalette.Swatch swatch = AnnotationPalette.For(model.ColorOf(session.ActiveLabel));
        System.Drawing.RectangleF r = area.Value;
        dc.DrawRectangle(swatch.Fill, swatch.Dashed, new Rect(S(new PointF(r.Left, r.Top)), S(new PointF(r.Right, r.Bottom))));
    }

    /// <summary>
    /// The AI prompt in the creation style: the box (dragged or last submitted) as a dashed class-colour rectangle, positive clicks as
    /// filled dots, negative clicks as an open dot with a cross.
    /// </summary>
    private void DrawAiPrompt(DrawingContext dc, CanvasViewModel model, CanvasEditSession session)
    {
        var draft = model.AiDraft;
        if (!_aiBoxDragging && draft.IsEmpty)
        {
            return;
        }

        AnnotationPalette.Swatch swatch = AnnotationPalette.For(model.ColorOf(session.ActiveLabel));
        if (_aiBoxDragging)
        {
            dc.DrawRectangle(swatch.Fill, swatch.Dashed, new Rect(S(_aiBoxStart), S(_aiBoxNow)));
        }
        else if (draft.Box.HasValue)
        {
            var box = draft.Box.Value;
            dc.DrawRectangle(swatch.Fill, swatch.Dashed, new Rect(S(new PointF(box.X1, box.Y1)), S(new PointF(box.X2, box.Y2))));
        }

        Brush plate = ThemeBrush("CardBackgroundFillColorDefaultBrush", Brushes.Black);
        Pen white = new Pen(Brushes.White, 1.0);
        foreach (var point in draft.Points)
        {
            Point at = S(new PointF(point.X, point.Y));
            if (point.Positive)
            {
                dc.DrawEllipse(swatch.Solid, white, at, PointMarkerRadius, PointMarkerRadius);
                continue;
            }

            dc.DrawEllipse(plate, swatch.Outline, at, PointMarkerRadius, PointMarkerRadius);
            double arm = PointMarkerRadius * 0.6;
            dc.DrawLine(swatch.Outline, new Point(at.X - arm, at.Y - arm), new Point(at.X + arm, at.Y + arm));
            dc.DrawLine(swatch.Outline, new Point(at.X - arm, at.Y + arm), new Point(at.X + arm, at.Y - arm));
        }
    }

    /// <summary>One shape in its own kind: closed kinds as geometry (holes included), circle as an ellipse, point as a marker, line / linestrip as strokes.</summary>
    private void DrawShape(DrawingContext dc, LabelShape shape, PointF[][]? holes, AnnotationPalette.Swatch swatch, bool selected, bool dashed)
    {
        Pen pen = dashed
            ? (selected ? swatch.DashedSelected : swatch.Dashed)
            : (selected ? swatch.Selected : swatch.Outline);
        switch (shape.ShapeType)
        {
            case "circle":
                if (shape.Points.Count >= 2)
                {
                    Point centre = S(new PointF((float)shape.Points[0][0], (float)shape.Points[0][1]));
                    Point edge = S(new PointF((float)shape.Points[1][0], (float)shape.Points[1][1]));
                    double radius = Math.Sqrt((edge.X - centre.X) * (edge.X - centre.X) + (edge.Y - centre.Y) * (edge.Y - centre.Y));
                    dc.DrawEllipse(swatch.Fill, pen, centre, radius, radius);
                }

                break;
            case "point":
                if (shape.Points.Count >= 1)
                {
                    Point at = S(new PointF((float)shape.Points[0][0], (float)shape.Points[0][1]));
                    double r = selected ? PointMarkerRadius + 1.5 : PointMarkerRadius;
                    dc.DrawEllipse(swatch.Solid, new Pen(Brushes.White, 1.0), at, r, r);
                }

                break;
            case "line":
                if (shape.Points.Count >= 2)
                {
                    dc.DrawLine(pen, S(new PointF((float)shape.Points[0][0], (float)shape.Points[0][1])),
                        S(new PointF((float)shape.Points[1][0], (float)shape.Points[1][1])));
                }

                break;
            case "linestrip":
                if (shape.Points.Count >= 2)
                {
                    dc.DrawGeometry(null, pen, BuildStrip(OuterOf(shape)));
                }

                break;
            default:
                dc.DrawGeometry(swatch.Fill, pen, BuildGeometry(OuterOf(shape), holes));
                break;
        }
    }

    private Geometry BuildStrip(PointF[] points)
    {
        var geometry = new StreamGeometry();
        using (StreamGeometryContext ctx = geometry.Open())
        {
            ctx.BeginFigure(S(points[0]), false, false);
            var rest = new List<Point>(points.Length - 1);
            for (int i = 1; i < points.Length; i++)
            {
                rest.Add(S(points[i]));
            }

            ctx.PolyLineTo(rest, true, true);
        }

        geometry.Freeze();
        return geometry;
    }

    /// <summary>"Show the label of shapes on the canvas" (BETA): label text above the top-left corner of each visible shape.</summary>
    /// <summary>Count mode: no class caption, only the running number 1..N at each stamp's centre.</summary>
    private void DrawCountNumbers(DrawingContext dc, CanvasViewModel model, CanvasEditSession session, List<LabelShape> shapes)
    {
        double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        Typeface face = new Typeface(SystemFonts.MessageFontFamily, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        int n = 0;
        for (int i = 0; i < shapes.Count; i++)
        {
            LabelShape shape = session.DraftShapes.TryGetValue(i, out LabelShape? draft) && draft != null ? draft : shapes[i];
            if (!CanvasEditSession.IsEditable(shape) || !model.IsShapeVisible(i) || shape.Points.Count == 0)
            {
                continue;
            }

            n++;
            PointF[] outer = OuterOf(shape);
            if (outer.Length == 0)
            {
                continue;
            }

            double cx = 0, cy = 0;
            foreach (PointF p in outer) { Point s = S(p); cx += s.X; cy += s.Y; }
            cx /= outer.Length; cy /= outer.Length;
            var text = new FormattedText(n.ToString(System.Globalization.CultureInfo.InvariantCulture),
                System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, 11, Brushes.Yellow, pixelsPerDip);
            Point at = new Point(cx - text.Width / 2, cy - text.Height / 2);
            dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(150, 0, 0, 0)), null, new Rect(at.X - 1, at.Y, text.Width + 2, text.Height));
            dc.DrawText(text, at);
        }
    }

    private void DrawShapeLabels(DrawingContext dc, CanvasViewModel model, CanvasEditSession session, List<LabelShape> shapes)
    {
        double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        Typeface face = new Typeface(SystemFonts.MessageFontFamily, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        Brush plate = ThemeBrush("CardBackgroundFillColorDefaultBrush", Brushes.Black);
        for (int i = 0; i < shapes.Count; i++)
        {
            LabelShape shape = session.DraftShapes.TryGetValue(i, out LabelShape? draft) && draft != null ? draft : shapes[i];
            if (!CanvasEditSession.IsEditable(shape) || !model.IsShapeVisible(i) || string.IsNullOrEmpty(shape.Label) || shape.Points.Count == 0)
            {
                continue;
            }

            // The caption follows the box: an oriented rectangle turns it along its first edge (kept readable, never upside down);
            // every other shape keeps it horizontal. Text frame: dir = reading direction, up = above the text, both in screen space.
            double angle = 0.0;
            PointF[] outer = OuterOf(shape);
            if (string.Equals(shape.ShapeType, "oriented_rectangle", StringComparison.Ordinal) && outer.Length >= 2)
            {
                angle = Math.Atan2(outer[1].Y - outer[0].Y, outer[1].X - outer[0].X) * 180.0 / Math.PI;
                if (angle > 90.0)
                {
                    angle -= 180.0;
                }
                else if (angle <= -90.0)
                {
                    angle += 180.0;
                }
            }

            double rad = angle * Math.PI / 180.0;
            double dirX = Math.Cos(rad), dirY = Math.Sin(rad);
            double upX = Math.Sin(rad), upY = -Math.Cos(rad);
            double minAlong = double.MaxValue, maxUp = double.MinValue;
            foreach (PointF p in outer)
            {
                Point s = S(p);
                minAlong = Math.Min(minAlong, s.X * dirX + s.Y * dirY);
                maxUp = Math.Max(maxUp, s.X * upX + s.Y * upY);
            }

            Point anchor = new Point(dirX * minAlong + upX * maxUp, dirY * minAlong + upY * maxUp);

            FormattedText text = new FormattedText(
                shape.Label!,
                System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                face,
                12.0,
                AnnotationPalette.For(model.ColorOf(shape.Label)).Solid,
                pixelsPerDip);
            dc.PushTransform(new RotateTransform(angle, anchor.X, anchor.Y));
            double y = anchor.Y - text.Height - 2.0;
            dc.DrawRectangle(plate, null, new Rect(anchor.X - 2.0, y, text.Width + 4.0, text.Height));
            dc.DrawText(text, new Point(anchor.X, y));
            dc.Pop();
        }
    }

    /// <summary>Dashed class-colour outline with even-odd holes and the score; the selected ones get a thicker dash.</summary>
    private void DrawSuggestions(DrawingContext dc, CanvasViewModel model)
    {
        IReadOnlyList<SegAiSuggestion> list = model.Suggestions;
        if (list.Count == 0)
        {
            return;
        }

        IReadOnlyList<SegAiSuggestion> selected = model.SelectedSuggestions;
        Brush plate = ThemeBrush("CardBackgroundFillColorDefaultBrush", Brushes.Black);
        double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        Typeface face = new Typeface(SystemFonts.MessageFontFamily, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

        foreach (SegAiSuggestion suggestion in list)
        {
            int cls = suggestion.ClassIndex;
            bool isSelected = selected.Contains(suggestion);
            PointF[][]? holes = suggestion.Holes.Length > 0 ? suggestion.Holes : null;
            LabelShape? target = TargetShapeOf(model, suggestion);
            if (suggestion.Outer.Length == 0)
            {
                // Remove suggestion: no outline of its own, so the target is drawn dashed red with a cross over its bounds.
                if (target != null)
                {
                    DrawRemoveTarget(dc, target, model, plate, pixelsPerDip, face);
                }

                continue;
            }

            if (target != null)
            {
                // Replace suggestion: the shape that would be replaced is highlighted under the new outline.
                dc.DrawGeometry(null, ReplaceTargetPen, BuildGeometry(OuterOf(target), null));
            }

            Geometry geometry = BuildGeometry(suggestion.Outer, holes);
            dc.DrawGeometry(
                AnnotationPalette.Fill(cls),
                isSelected ? AnnotationPalette.DashedSelectedOutline(cls) : AnnotationPalette.DashedOutline(cls),
                geometry);

            // The class name (what accepting the suggestion will paste as its label) first, then the score when there is one.
            string labelName = !string.IsNullOrEmpty(suggestion.Label) ? suggestion.Label! : model.ClassNameAt(cls);
            // Agent suggestions carry no meaningful score (provenance starts with "Agent | "), so only their label is shown.
            bool agentSuggestion = suggestion.Model != null && suggestion.Model.StartsWith("Agent | ", StringComparison.Ordinal);
            string caption = suggestion.Score.HasValue && !agentSuggestion
                ?labelName + " " + suggestion.Score.Value.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)
                : labelName;
            if (string.IsNullOrEmpty(caption))
            {
                continue;
            }

            FormattedText text = new FormattedText(
                caption,
                System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                face,
                12.0,
                AnnotationPalette.Solid(cls),
                pixelsPerDip);
            Rect bounds = geometry.Bounds;
            double x = Math.Max(0.0, Math.Min(bounds.Left, Math.Max(0.0, RenderSize.Width - text.Width - 4.0)));
            double y = bounds.Top - text.Height - 2.0;
            if (y < 0.0)
            {
                y = bounds.Top + 2.0;
            }

            dc.DrawRectangle(plate, null, new Rect(x - 2.0, y, text.Width + 4.0, text.Height));
            dc.DrawText(text, new Point(x, y));
        }
    }

    private void DrawRemoveTarget(DrawingContext dc, LabelShape target, CanvasViewModel model, Brush plate, double pixelsPerDip, Typeface face)
    {
        PointF[] outer = OuterOf(target);
        if (outer.Length < 2)
        {
            return;
        }

        Geometry geometry = BuildGeometry(outer, null);
        dc.DrawGeometry(null, RemoveDashedPen, geometry);
        Rect bounds = geometry.Bounds;
        dc.DrawLine(RemoveCrossPen, new Point(bounds.Left, bounds.Top), new Point(bounds.Right, bounds.Bottom));
        dc.DrawLine(RemoveCrossPen, new Point(bounds.Left, bounds.Bottom), new Point(bounds.Right, bounds.Top));

        string caption = target.Label ?? string.Empty;
        if (caption.Length == 0)
        {
            return;
        }

        FormattedText text = new FormattedText(caption, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight, face, 12.0, RejectIconFill, pixelsPerDip);
        double x = Math.Max(0.0, Math.Min(bounds.Left, Math.Max(0.0, RenderSize.Width - text.Width - 4.0)));
        double y = bounds.Top - text.Height - 2.0;
        if (y < 0.0)
        {
            y = bounds.Top + 2.0;
        }

        dc.DrawRectangle(plate, null, new Rect(x - 2.0, y, text.Width + 4.0, text.Height));
        dc.DrawText(text, new Point(x, y));
    }

    private static PointF[] OuterOf(LabelShape shape)
    {
        var pts = new List<PointF>(shape.Points.Count);
        foreach (double[] p in shape.Points)
        {
            if (p != null && p.Length >= 2)
            {
                pts.Add(new PointF((float)p[0], (float)p[1]));
            }
        }

        if (string.Equals(shape.ShapeType, "rectangle", StringComparison.Ordinal) && pts.Count >= 2)
        {
            float l = Math.Min(pts[0].X, pts[1].X), r = Math.Max(pts[0].X, pts[1].X);
            float t = Math.Min(pts[0].Y, pts[1].Y), b = Math.Max(pts[0].Y, pts[1].Y);
            return new[] { new PointF(l, t), new PointF(r, t), new PointF(r, b), new PointF(l, b) };
        }

        return pts.ToArray();
    }

    /// <summary>Outer ring plus hole rings in one even-odd geometry, in screen space.</summary>
    private Geometry BuildGeometry(PointF[] outer, PointF[][]? holes)
    {
        var geometry = new StreamGeometry { FillRule = FillRule.EvenOdd };
        using (StreamGeometryContext ctx = geometry.Open())
        {
            AddRing(ctx, outer);
            if (holes != null)
            {
                foreach (PointF[] hole in holes)
                {
                    AddRing(ctx, hole);
                }
            }
        }

        geometry.Freeze();
        return geometry;
    }

    private void AddRing(StreamGeometryContext ctx, PointF[] ring)
    {
        if (ring == null || ring.Length < 2)
        {
            return;
        }

        ctx.BeginFigure(S(ring[0]), true, true);
        var rest = new List<Point>(ring.Length - 1);
        for (int i = 1; i < ring.Length; i++)
        {
            rest.Add(S(ring[i]));
        }

        ctx.PolyLineTo(rest, true, true);
    }

    private void DrawHandles(DrawingContext dc, LabelShape shape, AnnotationPalette.Swatch swatch, float scale, float handle, Brush fill, int selectedVertex)
    {
        PointF[] outer = OuterOf(shape);
        if (shape.ShapeType == "circle" || shape.ShapeType == "point" || shape.ShapeType == "line" || shape.ShapeType == "linestrip")
        {
            // Open and small kinds: one square handle per defining point (circle: centre and edge point).
            for (int i = 0; i < outer.Length; i++)
            {
                Point at = S(outer[i]);
                Brush body = i == selectedVertex ? swatch.Solid : fill;
                dc.DrawRectangle(body, swatch.Outline, new Rect(at.X - handle / 2.0, at.Y - handle / 2.0, handle, handle));
            }

            return;
        }

        if (outer.Length < 3)
        {
            return;
        }

        float minX = outer.Min(p => p.X), maxX = outer.Max(p => p.X);
        float minY = outer.Min(p => p.Y), maxY = outer.Max(p => p.Y);
        bool isBox = !string.Equals(shape.ShapeType, "polygon", StringComparison.Ordinal);
        float shortest = Math.Min(maxX - minX, maxY - minY);
        if (isBox && string.Equals(shape.ShapeType, "oriented_rectangle", StringComparison.Ordinal)
            && RotatedBoxGeometry.TryCreate(outer, out RotatedBoxGeometry box))
        {
            shortest = Math.Min(box.Width, box.Height);
        }

        double size = EditHandleMetrics.ClampToShapeScreen(handle, shortest, scale);
        Pen pen = swatch.Outline;

        if (string.Equals(shape.ShapeType, "oriented_rectangle", StringComparison.Ordinal)
            && RotatedBoxGeometry.TryCreate(outer, out RotatedBoxGeometry rot))
        {
            PointF[] c = rot.ToPoints();
            PointF mid = new PointF((c[0].X + c[1].X) * 0.5f, (c[0].Y + c[1].Y) * 0.5f);
            PointF handlePos = ShapeHitTester.RotationHandlePosition(rot, scale, handle);
            Point a = S(mid);
            Point b = S(handlePos);
            dc.DrawLine(pen, a, b);
            dc.DrawEllipse(fill, pen, b, size * 0.6, size * 0.6);
        }

        for (int i = 0; i < outer.Length; i++)
        {
            Point c = S(outer[i]);
            bool isSelected = i == selectedVertex && string.Equals(shape.ShapeType, "polygon", StringComparison.Ordinal);
            dc.DrawRectangle(isSelected ? swatch.Solid : fill, pen, new Rect(c.X - size / 2.0, c.Y - size / 2.0, size, size));
        }
    }

    private void DrawCreation(DrawingContext dc, CanvasViewModel model, CanvasEditSession session, Brush handleFill)
    {
        AnnotationPalette.Swatch swatch = AnnotationPalette.For(model.ColorOf(session.ActiveLabel));
        Pen dashed = swatch.Dashed;

        LabelShape? preview = session.CreationPreview;
        if (preview != null)
        {
            DrawShape(dc, preview, null, swatch, false, true);
        }

        IReadOnlyList<PointF> draft = session.DraftPoints;
        if (draft.Count == 0)
        {
            return;
        }

        PointF? hover = session.HoverPoint;
        if (session.Tool == CanvasTool.Polygon && model.FillWhileDrawing && draft.Count + (hover.HasValue ? 1 : 0) >= 3)
        {
            // "Fill polygon while drawing": the open outline (plus the rubber-band point) is filled under the dashed edges.
            var fillPoints = new List<PointF>(draft);
            if (hover.HasValue)
            {
                fillPoints.Add(hover.Value);
            }

            dc.DrawGeometry(swatch.Fill, null, BuildGeometry(fillPoints.ToArray(), null));
        }

        for (int i = 1; i < draft.Count; i++)
        {
            dc.DrawLine(dashed, S(draft[i - 1]), S(draft[i]));
        }

        if (hover.HasValue && (session.Tool == CanvasTool.Polygon || session.Tool == CanvasTool.LineStrip))
        {
            dc.DrawLine(dashed, S(draft[draft.Count - 1]), S(hover.Value));
        }
        else if (hover.HasValue && session.Tool == CanvasTool.Rot && draft.Count == 1)
        {
            dc.DrawLine(dashed, S(draft[0]), S(hover.Value));
        }

        Pen outline = swatch.Outline;
        for (int i = 0; i < draft.Count; i++)
        {
            double radius = i == 0 && session.Tool == CanvasTool.Polygon && draft.Count >= 3 ? CloseTargetRadius : DraftDotRadius;
            dc.DrawEllipse(handleFill, outline, S(draft[i]), radius, radius);
        }
    }
}
