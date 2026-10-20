using System.Drawing;
using System.Text;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EasyAI.Desktop.Annotation;
using EasyAI.Desktop.Infrastructure;
using EasyAI.Desktop.Resources;
using EasyAI.Desktop.Services;
using EasyLabel.Core.Commands;
using EasyLabel.Core.Io;
using EasyLabel.Core.Editing;
using EasyLabel.Core.Geometry;
using EasyLabel.Core.Model;
using EasyLabel.Core.Projects;
using EasyLabel.SegAI;
using EasyLabel.SegAI.AutoLabel;

namespace EasyAI.Desktop.ViewModels;

/// <summary>Menu parameter of the NG (0) and OK (1) entries; the active class itself is a class index (<see cref="CanvasViewModel.ActiveClassIndex"/>).</summary>
public enum AnnotationClass
{
    Ng = 0,
    Ok = 1,
}

/// <summary>
/// State of the annotation canvas: tool/class modes, the edit session of the current image (one <see cref="DocumentEditor"/>
/// and therefore one undo stack per image) and the background save queue. All members run on the UI thread except the queue jobs.
/// </summary>
public sealed partial class CanvasViewModel : ObservableObject
{
    private readonly Action<string, string> _reportError;
    private readonly Dispatcher _dispatcher;
    private readonly AnnotationSaveQueue _queue = new AnnotationSaveQueue();
    private readonly ShapeClipboard _clipboard = new ShapeClipboard();
    private SegAiSession? _segAi;
    private SegAiEditDocument? _edit;
    private DocumentEditor? _editor;
    private readonly DirtyTracker _dirty = new DirtyTracker();
    private int _generation;
    private bool _hasPointer;
    private float _pointerX;
    private float _pointerY;
    private IReadOnlyList<SegAiSuggestion> _suggestions = Array.Empty<SegAiSuggestion>();
    private readonly List<SegAiSuggestion> _selectedSuggestions = new List<SegAiSuggestion>();
    private string? _shownRel;
    private readonly ImageAdjuster _adjuster = new ImageAdjuster();
    private BitmapSource? _displayImage;
    private readonly HashSet<int> _hiddenShapes = new HashSet<int>();
    private int _lastShapeCount;
    private readonly List<string> _usedLabels = new List<string>();
    private List<LabelShape> _previousShapes = new List<LabelShape>();
    private bool _echoingZoom;
    private double _zoomPercent = 100.0;
    private float _contextX;
    private float _contextY;
    private float _contextScale;
    private bool _contextValid;

    /// <summary>Opens the "Shape label" dialog; returns the edited values or null when cancelled. Set by the window.</summary>
    public Func<LabelDialogRequest, ShapeLabelInfo?>? LabelPrompt { get; set; }

    /// <summary>Opens the brightness / contrast dialog. Set by the window.</summary>
    public Action? BrightnessPrompt { get; set; }

    /// <summary>Confirmation (title, warning text) shown before a lossy shape conversion; true = proceed.</summary>
    public Func<string, string, bool>? ConvertConfirmPrompt { get; set; }

    /// <summary>Writes the settings file after a menu toggle changed a stored option. Set by the main view model.</summary>
    public Action? PersistSettings { get; set; }

    /// <summary>True while a project is being opened: the canvas ignores edits so no save job targets a session about to be disposed.</summary>
    public bool InputBlocked { get; set; }

    [ObservableProperty]
    private CanvasTool _tool = CanvasTool.Select;

    /// <summary>Kind of the open project (set by the main view model before the session is bound). Position offers only Select, Rot, Circle and AiBox.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AllowsFreeShapeTools))]
    private ProjectKind _kind = ProjectKind.Detect;

    /// <summary>True for Detect: the polygon, rectangle, point, line, line-strip and AI-Points tools are offered. Bound by the palettes.</summary>
    public bool AllowsFreeShapeTools => Kind != ProjectKind.Position;

    /// <summary>Class index (0-based position in <see cref="ClassNames"/>) used for newly drawn shapes.</summary>
    [ObservableProperty]
    private int _activeClassIndex;

    [ObservableProperty]
    private string _ngName = "NG";

    [ObservableProperty]
    private string _okName = "OK";

    /// <summary>Project class names in table order (class id = position + 1). Defaults to NG, OK.</summary>
    private IReadOnlyList<string> _classNames = new[] { "NG", "OK" };

    /// <summary>Enabled flag per class (same order as <see cref="_classNames"/>); a disabled class cannot be selected.</summary>
    private IReadOnlyList<bool> _classEnabled = new[] { true, true };

    /// <summary>Right side of the status bar: "mode=EDIT | x=.., y=.." (image pixels under the pointer).</summary>
    [ObservableProperty]
    private string _pointerText = StatusTextFormatter.Pointer(StatusTextFormatter.EditMode, false, 0, 0);

    /// <summary>The current image has edits that are not written (only possible with Auto-save OFF, or after a failed write).</summary>
    [ObservableProperty]
    private bool _isDirty;

    /// <summary>AI-Points tool enabled (follows the Auto-label source; set by the Label tab view model).</summary>
    [ObservableProperty]
    private bool _aiPointsEnabled;

    /// <summary>AI-Box tool enabled (follows the Auto-label source; set by the Label tab view model).</summary>
    [ObservableProperty]
    private bool _aiBoxEnabled;

    /// <summary>Tooltip of the AI-Points tool: caption and shortcut when enabled, the "Unavailable: ..." reason otherwise.</summary>
    [ObservableProperty]
    private string _aiPointsToolTip = string.Empty;

    /// <summary>Tooltip of the AI-Box tool: caption and shortcut when enabled, the "Unavailable: ..." reason otherwise.</summary>
    [ObservableProperty]
    private string _aiBoxToolTip = string.Empty;

    /// <summary>Save settings (Auto-save, embed image data, output directory); null behaves like the defaults.</summary>
    public AppSettings? Options { get; set; }

    private bool AutoSave => Options == null || Options.AutoSave;

    public CanvasViewModel(Dispatcher dispatcher, Action<string, string> reportError)
    {
        _dispatcher = dispatcher;
        _reportError = reportError;
        _queue.Failed -= OnSaveFailed;
        _queue.Failed += OnSaveFailed;
        _queue.Saved -= OnSaved;
        _queue.Saved += OnSaved;
    }

    /// <summary>The edits of an image reached disk (raised on the UI thread); badges and summary can re-read its state.</summary>
    public event Action<string>? ImageSaved;

    /// <summary>Something visible changed; the canvas must redraw.</summary>
    public event EventHandler? RenderRequested;

    /// <summary>A different image was shown; the canvas refits its view.</summary>
    public event EventHandler? ImageReplaced;

    public event EventHandler? FitRequested;

    public event EventHandler? ActualSizeRequested;

    /// <summary>The suggestion list or its selection changed.</summary>
    public event EventHandler? SuggestionsChanged;

    /// <summary>Suggestions left the layer because they were accepted into the document or rejected.</summary>
    public event Action<IReadOnlyList<SegAiSuggestion>>? SuggestionsResolved;

    /// <summary>One event per accepted or rejected suggestion (all paths: selected, all, per-box check / X), raised just before <see cref="SuggestionsResolved"/>.</summary>
    public event Action<SuggestionDecision>? SuggestionDecided;

    /// <summary>An accept skipped this many replace / remove suggestions because their target shape changed or is gone.</summary>
    public event Action<int>? ReviewSkipped;

    /// <summary>The editable document of a newly shown image finished loading.</summary>
    public event EventHandler? DocumentReady;

    /// <summary>Raised after every edit of the open document, including the OK/NG flags written by the flags panel.</summary>
    public event EventHandler? DocumentEdited;

    /// <summary>Preview bitmap (may be decoded smaller than the real image; it is stretched to ImageWidth x ImageHeight).</summary>
    public BitmapSource? Image { get; private set; }

    public int ImageWidth { get; private set; }

    public int ImageHeight { get; private set; }

    /// <summary>Null while no editable document is loaded (no image, no project session, or load failed).</summary>
    public CanvasEditSession? Session { get; private set; }

    public LabelMeDocument? Document => _edit?.Document;

    public ShapeHoleStore<PointF[][]>? Holes => _edit?.Holes;

    public bool HasSelection => Session != null && Session.SelectedIndices.Count > 0;

    /// <summary>Project class names in table order; the class id of a name is its position + 1.</summary>
    public IReadOnlyList<string> ClassNames => _classNames;

    /// <summary>Enabled flag per class, same order as <see cref="ClassNames"/>.</summary>
    public IReadOnlyList<bool> ClassEnabled => _classEnabled;

    public int ClassCount => _classNames.Count;

    /// <summary>0-based class index of a label, or <see cref="ClassTableRules.OtherIndex"/> when the label is not a class.</summary>
    public int ClassIndexOf(string? label) => ClassTableRules.IndexOf(label, _classNames);

    /// <summary>Name of a class index; NG for an index outside the table (the two-class default).</summary>
    public string ClassNameAt(int index) => index >= 0 && index < _classNames.Count ? _classNames[index] : NgName;

    /// <summary>Makes a class the one new shapes are drawn with. A disabled or unknown class is ignored.</summary>
    public void SetActiveClass(int index)
    {
        if (ClassTableRules.IsSelectable(_classEnabled, index))
        {
            ActiveClassIndex = index;
        }
    }

    partial void OnKindChanged(ProjectKind value)
    {
        if (!ProjectKindToolRules.IsToolAllowed(value, Tool))
        {
            Tool = CanvasTool.Select;
        }

        ConvertShapeCommand.NotifyCanExecuteChanged();
    }

    partial void OnToolChanged(CanvasTool value)
    {
        if (!ProjectKindToolRules.IsToolAllowed(Kind, value))
        {
            // Single gate for the palettes, menus and hotkeys: a refused tool falls back to Select (re-enters this method).
            Tool = CanvasTool.Select;
            return;
        }

        if (Session != null)
        {
            Session.Tool = value;
        }

        if (!AiDraft.IsEmpty)
        {
            // Another tool: the clicks / box of the AI prompt are over. Results already shown stay as normal suggestions.
            bool hadPoints = AiDraft.HasPoints;
            AiDraft.Clear();
            if (hadPoints)
            {
                AiPromptCommitted?.Invoke(this, EventArgs.Empty);
            }
        }

        RefreshPointerText();
        RaiseRender();
    }

    partial void OnAiPointsEnabledChanged(bool value)
    {
        SelectAiPointsCommand.NotifyCanExecuteChanged();
        if (!value && Tool == CanvasTool.AiPoints)
        {
            Tool = CanvasTool.Select;
        }
    }

    partial void OnAiBoxEnabledChanged(bool value)
    {
        SelectAiBoxCommand.NotifyCanExecuteChanged();
        if (!value && Tool == CanvasTool.AiBox)
        {
            Tool = CanvasTool.Select;
        }
    }

    // ---------- AI prompt (AI-Points / AI-Box tools) ----------

    /// <summary>Clicks or box of the AI prompt being built on the current image (drawn by the canvas).</summary>
    public AiPromptState AiDraft { get; } = new AiPromptState();

    /// <summary>An AI run is in flight (set by the Label tab view model); Esc then cancels it even without a prompt.</summary>
    [ObservableProperty]
    private bool _aiBusy;

    /// <summary>A click or a dragged box produced a prompt to run with the active AI source.</summary>
    public event EventHandler<AiPromptEventArgs>? AiPromptRequested;

    /// <summary>Enter (or leaving the tool) ended the clicks: the results shown so far are final.</summary>
    public event EventHandler? AiPromptCommitted;

    /// <summary>Esc: the prompt is dropped; a run in flight and the results of this prompt must be dropped too.</summary>
    public event EventHandler? AiPromptCleared;

    private bool CanPrompt(CanvasTool tool, bool enabled)
    {
        return Tool == tool && enabled && !InputBlocked && Session != null && ImageWidth > 0 && ImageHeight > 0;
    }

    /// <summary>AI-Points click in image pixels (negative = Shift+click). Re-runs the source with all clicks so far.</summary>
    public bool AiAddPoint(float x, float y, bool negative)
    {
        if (!CanPrompt(CanvasTool.AiPoints, AiPointsEnabled) || !AiDraft.AddPoint(x, y, !negative, ImageWidth, ImageHeight))
        {
            return false;
        }

        RaiseAiPrompt(false);
        RaiseRender();
        return true;
    }

    /// <summary>AI-Box drag finished (image pixels, corners in any order). A box that is too small is only a click and is ignored.</summary>
    public bool AiSubmitBox(float x1, float y1, float x2, float y2)
    {
        if (!CanPrompt(CanvasTool.AiBox, AiBoxEnabled) || !AiDraft.SetBox(x1, y1, x2, y2, ImageWidth, ImageHeight))
        {
            return false;
        }

        RaiseAiPrompt(true);
        RaiseRender();
        return true;
    }

    /// <summary>Enter with the AI-Points tool: the clicks are final, the next click starts a new object.</summary>
    public bool AiCommitPrompt()
    {
        if (Tool != CanvasTool.AiPoints || !AiDraft.Commit())
        {
            return false;
        }

        AiPromptCommitted?.Invoke(this, EventArgs.Empty);
        RaiseRender();
        return true;
    }

    /// <summary>Esc: drops the prompt and cancels a run in flight. False when there was neither.</summary>
    public bool AiCancelPrompt()
    {
        bool had = AiDraft.Clear();
        if (!had && !AiBusy)
        {
            return false;
        }

        AiPromptCleared?.Invoke(this, EventArgs.Empty);
        RaiseRender();
        return true;
    }

    private void RaiseAiPrompt(bool isBox)
    {
        var prompt = AiDraft.ToPrompt();
        if (prompt != null)
        {
            AiPromptRequested?.Invoke(this, new AiPromptEventArgs(prompt, isBox));
        }
    }

    // ---------- view state (never saved, never undone) ----------

    /// <summary>Display brightness, -100..100 (0 = unchanged). Applied to the displayed bitmap only.</summary>
    [ObservableProperty]
    private int _brightness;

    /// <summary>Display contrast, -100..100 (0 = unchanged). Applied to the displayed bitmap only.</summary>
    [ObservableProperty]
    private int _contrast;

    partial void OnBrightnessChanged(int value)
    {
        RefreshDisplayImage();
    }

    partial void OnContrastChanged(int value)
    {
        RefreshDisplayImage();
    }

    /// <summary>The bitmap to draw: the image with brightness / contrast applied (or the image itself).</summary>
    public BitmapSource? DisplayImage => _displayImage ?? Image;

    private void RefreshDisplayImage()
    {
        _displayImage = _adjuster.Apply(Brightness, Contrast);
        RaiseRender();
    }

    /// <summary>Zoom of the canvas in percent (100 = one image pixel per screen pixel); two-way with the toolbar box.</summary>
    public double ZoomPercent
    {
        get => _zoomPercent;
        set
        {
            double v = Math.Min(4000.0, Math.Max(5.0, value));
            if (Math.Abs(v - _zoomPercent) < 0.01)
            {
                return;
            }

            _zoomPercent = v;
            OnPropertyChanged();
            if (!_echoingZoom)
            {
                ZoomPercentRequested?.Invoke(this, v);
            }
        }
    }

    /// <summary>The canvas changed its zoom (wheel, fit, ...); updates the box without asking the canvas to zoom again.</summary>
    public void ReportZoom(double percent)
    {
        _echoingZoom = true;
        try
        {
            ZoomPercent = Math.Round(percent, 1);
        }
        finally
        {
            _echoingZoom = false;
        }
    }

    public event EventHandler<int>? ZoomStepRequested;

    public event EventHandler? FitWidthRequested;

    public event EventHandler<double>? ZoomPercentRequested;

    // ---------- settings-backed options ----------

    public bool KeepPreviousAnnotation
    {
        get => Options != null && Options.KeepPreviousAnnotation;
        set => SetOption(Options != null && Options.KeepPreviousAnnotation, value, v => Options!.KeepPreviousAnnotation = v);
    }

    public bool KeepPreviousZoom
    {
        get => Options != null && Options.KeepPreviousZoom;
        set => SetOption(Options != null && Options.KeepPreviousZoom, value, v => Options!.KeepPreviousZoom = v);
    }

    public bool KeepPreviousBrightness
    {
        get => Options != null && Options.KeepPreviousBrightness;
        set => SetOption(Options != null && Options.KeepPreviousBrightness, value, v => Options!.KeepPreviousBrightness = v);
    }

    public bool FillWhileDrawing
    {
        get => Options == null || Options.FillWhileDrawing;
        set => SetOption(Options == null || Options.FillWhileDrawing, value, v => Options!.FillWhileDrawing = v);
    }

    public bool ShowLabelOnCanvas => Options != null && Options.ShowLabelOnCanvas;

    private void SetOption(bool current, bool value, Action<bool> write, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (Options == null || current == value)
        {
            return;
        }

        write(value);
        OnPropertyChanged(name);
        PersistSettings?.Invoke();
        RaiseRender();
    }

    /// <summary>The Settings dialog changed a stored option: push it into the live session and redraw.</summary>
    public void ApplySettings()
    {
        if (Session != null)
        {
            Session.AllowOutOfBounds = Options != null && Options.AllowOutOfBounds;
        }

        OnPropertyChanged(nameof(KeepPreviousAnnotation));
        OnPropertyChanged(nameof(KeepPreviousZoom));
        OnPropertyChanged(nameof(KeepPreviousBrightness));
        OnPropertyChanged(nameof(FillWhileDrawing));
        OnPropertyChanged(nameof(ShowLabelOnCanvas));
        RaiseRender();
    }

    // ---------- shape colours and labels ----------

    /// <summary>Colour (0xAARRGGBB) of a shape label under the current colour mode.</summary>
    public uint ColorOf(string? label)
    {
        AppSettings o = Options ?? new AppSettings();
        if (!ShapeColorRules.TryParseColor(o.UniformColor, out uint uniform))
        {
            uniform = 0xFF00FF00;
        }

        if (!ShapeColorRules.TryParseColor(o.FallbackColor, out uint fallback))
        {
            fallback = 0xFF00FF00;
        }

        string mode = ShapeColorRules.NormalizeMode(o.ShapeColorMode);
        if (mode != ShapeColorRules.ModeUniform && _classNames.Count > 2)
        {
            // Project classes beyond NG / OK keep their class colour (the same colour as the suggestion layer).
            int index = ClassIndexOf(label);
            if (index >= 2 && index < _classNames.Count)
            {
                return ClassTableRules.ColorOf(index);
            }
        }

        // The auto palette counts only labels that are not classes, so a class name does not shift the slot of a free label.
        IReadOnlyList<string> used = _classNames.Count > 2
            ? _usedLabels.Where(l => ClassIndexOf(l) == ClassTableRules.OtherIndex).ToList()
            : _usedLabels;
        return ShapeColorRules.Resolve(o.ShapeColorMode, label, NgName, OkName, used, o.ShiftAutoColor, uniform, fallback);
    }

    private void RememberLabel(string? label)
    {
        if (!string.IsNullOrWhiteSpace(label) && !_usedLabels.Contains(label, StringComparer.Ordinal))
        {
            _usedLabels.Add(label);
        }
    }

    private void RememberDocumentLabels()
    {
        if (Document == null)
        {
            return;
        }

        foreach (LabelShape shape in Document.Shapes)
        {
            RememberLabel(shape.Label);
        }
    }

    // ---------- per-shape visibility (view state; resets with the image) ----------

    public bool IsShapeVisible(int index) => !_hiddenShapes.Contains(index);

    public void SetShapeVisible(int index, bool visible)
    {
        bool changed = visible ? _hiddenShapes.Remove(index) : _hiddenShapes.Add(index);
        if (!changed)
        {
            return;
        }

        if (!visible && Session != null)
        {
            Session.ClearSelection();
        }

        NotifyCommands();
        RaiseRender();
    }

    /// <summary>Shows or hides several shapes at once (the label checkbox of the merged list): one command refresh and one render.</summary>
    public void SetShapesVisible(IReadOnlyList<int> indices, bool visible)
    {
        bool changed = false;
        foreach (int index in indices)
        {
            changed |= visible ? _hiddenShapes.Remove(index) : _hiddenShapes.Add(index);
        }

        if (!changed)
        {
            return;
        }

        if (!visible && Session != null)
        {
            Session.ClearSelection();
        }

        NotifyCommands();
        RaiseRender();
    }

    public int HiddenShapeCount => _hiddenShapes.Count;

    /// <summary>A panel changed the shape selection of the session directly: commands and the canvas follow it.</summary>
    public void NotifySelectionChanged()
    {
        NotifyCommands();
        RaiseRender();
    }

    // ---------- image flags (Flags panel) ----------

    /// <summary>
    /// Sets one image flag of the document as ONE undo unit; it is saved with the LabelMe JSON (held while Auto-save is OFF).
    /// Returns false when there is no editable document, input is blocked or the flag already has that value.
    /// </summary>
    public bool SetImageFlag(string name, bool value)
    {
        LabelMeDocument? document = _edit?.Document;
        if (_editor == null || document == null || InputBlocked || string.IsNullOrWhiteSpace(name)
            || !FlagRules.WouldChange(document.Flags, name, value))
        {
            return false;
        }

        _editor.History.Execute(new ImageFlagCommand(document, name, value));
        NotifyEdited();
        return true;
    }

    /// <summary>
    /// Sets the OK/NG class of the document as ONE undo unit (<see cref="EasyLabel.Core.Model.ImageClassFlags"/> values; 0 clears it).
    /// Returns false when there is no editable document, input is blocked or the class already has that value.
    /// </summary>
    public bool SetImageClass(byte value)
    {
        LabelMeDocument? document = _edit?.Document;
        if (_editor == null || document == null || InputBlocked
            || EasyLabel.Core.Model.ImageClassFlags.Read(document.Flags) == value)
        {
            return false;
        }

        _editor.History.Execute(new ImageClassCommand(document, value));
        NotifyEdited();
        return true;
    }

    private void SyncAfterEdit()
    {
        int count = Document?.Shapes.Count ?? 0;
        if (count < _lastShapeCount)
        {
            // Shapes were removed or undone: the indices moved, so the per-shape flags start over.
            _hiddenShapes.Clear();
        }

        _lastShapeCount = count;
        RememberDocumentLabels();
    }

    // ---------- pointer / status ----------

    /// <summary>Records the image pixel under the pointer for the status bar.</summary>
    public void SetPointer(float x, float y)
    {
        if (Image == null)
        {
            ClearPointer();
            return;
        }

        _hasPointer = true;
        _pointerX = x;
        _pointerY = y;
        RefreshPointerText();
    }

    public void ClearPointer()
    {
        _hasPointer = false;
        RefreshPointerText();
    }

    private void RefreshPointerText()
    {
        string tool = Tool == CanvasTool.Rect ? "rectangle"
            : Tool == CanvasTool.Rot ? "oriented_rectangle"
            : Tool == CanvasTool.AiPoints ? "ai-points"
            : Tool == CanvasTool.AiBox ? "ai-box"
            : Tool.ToString().ToLowerInvariant();
        string mode = Tool == CanvasTool.Hand ? "PAN" : StatusTextFormatter.ModeText(Tool == CanvasTool.Select, tool);
        PointerText = StatusTextFormatter.Pointer(mode, _hasPointer, _pointerX, _pointerY);
    }

    partial void OnActiveClassIndexChanged(int value)
    {
        ApplyActiveLabel();
    }

    // ---------- lifetime ----------

    /// <summary>Binds the project session (null = none). Drops the current document; the caller flushed the queue first.</summary>
    public void SetSession(SegAiSession? session)
    {
        _generation++;
        DropDocument();
        _previousShapes = new List<LabelShape>();
        _usedLabels.Clear();
        _segAi = session;
        if (session != null)
        {
            LoadClasses(session);
        }

        NotifyCommands();
        RaiseRender();
    }

    /// <summary>Reloads the class names and enabled flags of the bound session (after the class table editor saved a table).</summary>
    public void RefreshClasses()
    {
        if (_segAi == null)
        {
            return;
        }

        LoadClasses(_segAi);
        RaiseRender();
    }

    private void LoadClasses(SegAiSession session)
    {
        try
        {
            IReadOnlyList<string> names = session.ClassNames;
            if (names.Count >= 2)
            {
                NgName = names[0];
                OkName = names[1];
            }

            if (names.Count >= 1)
            {
                _classNames = names.ToArray();
                _classEnabled = EnabledFlags(session, names.Count);
                OnPropertyChanged(nameof(ClassNames));
                OnPropertyChanged(nameof(ClassEnabled));
                if (!ClassTableRules.IsSelectable(_classEnabled, ActiveClassIndex))
                {
                    ActiveClassIndex = ClassTableRules.FirstEnabled(_classEnabled);
                }
            }
        }
        catch (Exception ex)
        {
            _reportError(Strings.CanvasLoadErrorTitle, ex.Message);
        }
    }

    /// <summary>Enabled flag of each class from the project table; all enabled when the table does not match the names.</summary>
    private static IReadOnlyList<bool> EnabledFlags(SegAiSession session, int count)
    {
        IReadOnlyList<EasyLabel.SegAI.Model.SegAiClassEntry> entries = session.ClassTable.Classes;
        var enabled = new bool[count];
        for (int i = 0; i < count; i++)
        {
            enabled[i] = entries.Count != count || entries[i].Enabled;
        }

        return enabled;
    }

    /// <summary>Shows an image (or nothing) and, when a project session exists, loads its shapes for editing.</summary>
    public async Task ShowAsync(string? rel, BitmapSource? image, int width, int height)
    {
        int generation = ++_generation;
        DropDocument();
        _shownRel = rel;
        Image = image;
        _adjuster.SetSource(image);
        if (!KeepPreviousBrightness)
        {
            Brightness = 0;
            Contrast = 0;
        }

        RefreshDisplayImage();
        _hiddenShapes.Clear();
        _lastShapeCount = 0;
        _hasPointer = false;
        RefreshPointerText();
        ImageWidth = image == null ? 0 : width;
        ImageHeight = image == null ? 0 : height;
        ImageReplaced?.Invoke(this, EventArgs.Empty);
        NotifyCommands();
        RaiseRender();

        SegAiSession? segAi = _segAi;
        if (rel == null || image == null || segAi == null || width <= 0 || height <= 0)
        {
            return;
        }

        try
        {
            // A queued save of the same image must reach disk before it is read back.
            await _queue.FlushAsync();
            if (generation != _generation)
            {
                return;
            }

            SegAiEditDocument edit = await Task.Run(() => segAi.LoadForEdit(rel));
            if (generation != _generation)
            {
                return;
            }

            _edit = edit;
            _editor = new DocumentEditor(edit.Document, edit.Sidecar);
            Session = new CanvasEditSession(_editor, edit.Document, width, height, _clipboard)
            {
                Holes = edit.Holes,
                Tool = Tool,
                AllowOutOfBounds = Options != null && Options.AllowOutOfBounds,
                VisibilityFilter = IsShapeVisible,
            };
            ApplyActiveLabel();
            _dirty.MarkSaved(Signature(edit.Document));
            IsDirty = false;
            RememberDocumentLabels();
            ApplyKeptAnnotations(width, height);
            _lastShapeCount = edit.Document.Shapes.Count;
        }
        catch (Exception ex)
        {
            if (generation == _generation)
            {
                DropDocument();
                _reportError(Strings.CanvasLoadErrorTitle, rel + ": " + ex.Message);
            }
        }

        NotifyCommands();
        RaiseRender();
        if (Session != null && generation == _generation)
        {
            DocumentReady?.Invoke(this, EventArgs.Empty);
        }
    }

    public Task FlushAsync() => _queue.FlushAsync();

    /// <summary>Blocking flush for window close.</summary>
    public bool Flush(TimeSpan timeout) => _queue.Flush(timeout);

    /// <summary>
    /// "Keep previous annotations": an image without shapes receives copies of the previous image's shapes as new annotations
    /// (clamped into the image unless out-of-bounds points are allowed). One undo unit; saved like any edit.
    /// </summary>
    private void ApplyKeptAnnotations(int width, int height)
    {
        if (!KeepPreviousAnnotation || _editor == null || _edit == null || _edit.Document.Shapes.Count > 0 || _previousShapes.Count == 0)
        {
            return;
        }

        bool outside = Options != null && Options.AllowOutOfBounds;
        List<LabelShape> copies = new List<LabelShape>(_previousShapes.Count);
        foreach (LabelShape source in _previousShapes)
        {
            LabelShape copy = DocumentEditor.Clone(source);
            if (!outside)
            {
                foreach (double[] point in copy.Points)
                {
                    point[0] = Math.Min(Math.Max(point[0], 0.0), width);
                    point[1] = Math.Min(Math.Max(point[1], 0.0), height);
                }
            }

            copies.Add(copy);
        }

        _editor.Add(copies, copies.Select(_ => new ShapeProvenance()).ToList(), "Keep previous annotations");
        QueueSaveIfChanged();
    }

    private void DropDocument()
    {
        if (_edit != null)
        {
            // The shapes of the image being left are what "keep previous annotations" copies onto the next empty image.
            _previousShapes = _edit.Document.Shapes.Select(DocumentEditor.Clone).ToList();
        }

        Session = null;
        _edit = null;
        _editor = null;
        _dirty.Reset();
        IsDirty = false;
        _shownRel = null;
        AiDraft.Clear();
        if (_suggestions.Count > 0 || _selectedSuggestions.Count > 0)
        {
            // Suggestions belong to the document they were computed for.
            _suggestions = Array.Empty<SegAiSuggestion>();
            _selectedSuggestions.Clear();
            SuggestionsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void ApplyActiveLabel()
    {
        if (Session != null)
        {
            Session.ActiveLabel = ClassNameAt(ActiveClassIndex);
        }
    }

    // ---------- edit entry points used by the canvas ----------

    /// <summary>Call after any pointer/keyboard action on the session: queues a save when the geometry changed and redraws.</summary>
    public void NotifyEdited()
    {
        SyncAfterEdit();
        DocumentEdited?.Invoke(this, EventArgs.Empty);
        int created = Session == null ? -1 : Session.TakeCreatedIndex();
        bool prompt = created >= 0 && LabelPrompt != null && !InputBlocked && Options != null && Options.ShowLabelDialog;
        if (prompt)
        {
            // The label dialog decides the final label (or discards the shape); the save follows its result.
            CanvasEditSession? expected = Session;
            _dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, new Action(() =>
            {
                if (ReferenceEquals(Session, expected))
                {
                    PromptLabel(true);
                }
            }));
        }
        else
        {
            QueueSaveIfChanged();
        }

        NotifyCommands();
        RaiseRender();
    }

    /// <summary>Right click: remembers where the pointer is so "Add point to edge" knows the edge.</summary>
    public void SetContextPoint(float x, float y, float scale)
    {
        _contextX = x;
        _contextY = y;
        _contextScale = scale;
        _contextValid = true;
        NotifyCommands();
    }

    private void PromptLabel(bool afterCreate)
    {
        CanvasEditSession? session = Session;
        Func<LabelDialogRequest, ShapeLabelInfo?>? prompt = LabelPrompt;
        if (session == null || prompt == null || session.SelectedIndices.Count == 0)
        {
            QueueSaveIfChanged();
            return;
        }

        ShapeLabelInfo initial = session.DescribeSelection();
        AppSettings o = Options ?? new AppSettings();
        LabelDialogRequest request = new LabelDialogRequest(
            initial,
            LabelListRules.Build(_classNames, _usedLabels, o.PredefinedLabels, o.SortLabels),
            o.ShowLabelTextField,
            LabelListRules.NormalizeAutoComplete(o.AutoComplete));
        ShapeLabelInfo? result = prompt(request);
        if (result == null)
        {
            // Cancel after drawing discards the new shape (Labelme); cancel on Edit label changes nothing.
            if (afterCreate && _editor != null && _editor.History.Undo())
            {
                session.NotifyDocumentChanged();
            }
        }
        else if (session.ApplyLabelInfo(initial, result))
        {
            RememberLabel(result.Label);
        }

        NotifyEdited();
    }

    public void Cancel()
    {
        if (Session != null && Session.Cancel())
        {
            NotifyEdited();
        }
    }

    public void Finish()
    {
        if (Session != null)
        {
            Session.Finish();
            NotifyEdited();
        }
    }

    public void RemoveDraftVertex()
    {
        if (Session != null)
        {
            Session.RemoveDraftVertex();
            NotifyEdited();
        }
    }

    public void CycleSelection(bool backwards)
    {
        if (Session != null)
        {
            ClearSuggestionSelection();
            Session.CycleSelection(backwards);
            NotifyEdited();
        }
    }

    public bool Nudge(int dx, int dy, bool shiftTen)
    {
        if (Session == null || Session.SelectedIndices.Count == 0)
        {
            return false;
        }

        Session.Nudge(dx, dy, shiftTen);
        NotifyEdited();
        return true;
    }

    // ---------- suggestion layer (read-only overlay: not in the document, not in the undo stack, never saved) ----------

    /// <summary>Suggestions shown for the current image (already filtered by the score threshold).</summary>
    public IReadOnlyList<SegAiSuggestion> Suggestions => _suggestions;

    public IReadOnlyList<SegAiSuggestion> SelectedSuggestions => _selectedSuggestions;

    public bool HasSelectedSuggestions => _selectedSuggestions.Count > 0;

    /// <summary>Model file name written to the provenance of accepted shapes.</summary>
    public string? SuggestionModel { get; set; }

    /// <summary>Project-relative path of the image whose document is loaded, or null.</summary>
    public string? CurrentRel => Session != null ? _shownRel : null;

    /// <summary>Replaces the suggestion layer; a selected suggestion that is still present stays selected.</summary>
    public void SetSuggestions(IReadOnlyList<SegAiSuggestion> visible)
    {
        _suggestions = visible ?? Array.Empty<SegAiSuggestion>();
        HashSet<SegAiSuggestion> present = new HashSet<SegAiSuggestion>(_suggestions, ReferenceEqualityComparer.Instance);
        _selectedSuggestions.RemoveAll(s => !present.Contains(s));
        RaiseSuggestionsChanged();
    }

    public void ClearSuggestionSelection()
    {
        if (_selectedSuggestions.Count > 0)
        {
            _selectedSuggestions.Clear();
            RaiseSuggestionsChanged();
        }
    }

    /// <summary>
    /// Select tool click: selects the suggestion under the point unless a real shape (or one of its handles) is hit first.
    /// Returns true when a suggestion consumed the click. Ctrl toggles instead of replacing the selection.
    /// </summary>
    public bool TrySelectSuggestionAt(float x, float y, float scale, bool add)
    {
        CanvasEditSession? session = Session;
        LabelMeDocument? document = Document;
        if (_suggestions.Count == 0 || session == null || document == null)
        {
            return false;
        }

        PointF point = new PointF(x, y);
        List<LabelShape> shapes = document.Shapes;
        for (int i = shapes.Count - 1; i >= 0; i--)
        {
            if (CanvasEditSession.IsEditable(shapes[i])
                && ShapeHitTester.HitTest(shapes[i], point, scale, session.HandleScreenSize).Kind != ShapeHitKind.None)
            {
                ClearSuggestionSelection();
                return false;
            }
        }

        int hit = LabelStepRules.HitTopmost(_suggestions, x, y);
        if (hit < 0)
        {
            if (!add)
            {
                ClearSuggestionSelection();
            }

            return false;
        }

        SegAiSuggestion picked = _suggestions[hit];
        if (add)
        {
            if (!_selectedSuggestions.Remove(picked))
            {
                _selectedSuggestions.Add(picked);
            }
        }
        else
        {
            _selectedSuggestions.Clear();
            _selectedSuggestions.Add(picked);
        }

        session.ClearSelection();
        RaiseSuggestionsChanged();
        return true;
    }

    /// <summary>
    /// Promotes the selected (or all visible) suggestions into the document as polygons: ONE undo unit for the whole batch, holes
    /// go into the hole store like drawn shapes, and the usual save path persists them. Returns the number accepted.
    /// </summary>
    public int AcceptSuggestions(bool onlySelected)
    {
        return AcceptChosen(onlySelected
            ? _suggestions.Where(s => _selectedSuggestions.Contains(s)).ToList()
            : _suggestions.ToList());
    }

    /// <summary>
    /// Accept (same path as <see cref="AcceptSuggestions(bool)"/>) for the visible suggestions that pass <paramref name="filter"/>: the chat
    /// review of a batch uses it for the agent proposals of the open image only. Returns the number accepted.
    /// </summary>
    public int AcceptSuggestionsWhere(Func<SegAiSuggestion, bool> filter)
    {
        return AcceptChosen(_suggestions.Where(filter).ToList());
    }

    /// <summary>
    /// The check icon next to one suggestion: promotes exactly that suggestion (one undo unit). Returns 1 when it was accepted, 0 when it
    /// is not in the layer any more or the input is blocked.
    /// </summary>
    public int AcceptSuggestion(SegAiSuggestion suggestion)
    {
        if (suggestion == null || !_suggestions.Contains(suggestion, ReferenceEqualityComparer.Instance))
        {
            return 0;
        }

        return AcceptChosen(new List<SegAiSuggestion> { suggestion });
    }

    private int AcceptChosen(List<SegAiSuggestion> chosen)
    {
        if (InputBlocked || _editor == null || _edit == null || Session == null)
        {
            return 0;
        }

        // Position projects take only oriented boxes and circles; a mask polygon is refused here (producers already convert or refuse it).
        // Review removals stay: deleting a shape does not add a polygon.
        if (Kind == ProjectKind.Position)
        {
            chosen = chosen.Where(s => s.Action == SegAiSuggestion.ActionRemove || ProjectKindToolRules.IsShapeTypeAllowed(Kind, s.ShapeType)).ToList();
        }

        if (chosen.Count == 0)
        {
            return 0;
        }

        LabelMeDocument? document = Document;
        if (document == null)
        {
            return 0;
        }

        // Replace / remove targets are rechecked against the document as it is now; stale ones are skipped and reported.
        ReviewPlan plan = ReviewApplyRules.Plan(chosen, document.Shapes);
        List<SegAiSuggestion> applied = new List<SegAiSuggestion>(plan.AppliedCount);
        List<string> labels = new List<string>(plan.AppliedCount);

        List<LabelShape> shapes = new List<LabelShape>(plan.Adds.Count);
        List<ShapeProvenance> provenance = new List<ShapeProvenance>(plan.Adds.Count);
        foreach (SegAiSuggestion suggestion in plan.Adds)
        {
            LabelShape shape = LabelStepRules.ToShape(suggestion, ClassNameAt(suggestion.ClassIndex));
            if (suggestion.Holes.Length > 0)
            {
                _edit.Holes.Set(shape, LabelStepRules.CopyHoles(suggestion));
            }

            shapes.Add(shape);
            provenance.Add(LabelStepRules.ToProvenance(suggestion, SuggestionModel));
            applied.Add(suggestion);
            labels.Add(shape.Label ?? string.Empty);
        }

        Dictionary<int, LabelShape> replacements = new Dictionary<int, LabelShape>();
        Dictionary<int, ShapeProvenance> replacementProvenance = new Dictionary<int, ShapeProvenance>();
        foreach (ReviewTarget target in plan.Replacements)
        {
            LabelShape shape = LabelStepRules.ToShape(target.Suggestion, ClassNameAt(target.Suggestion.ClassIndex));
            if (target.Suggestion.Holes.Length > 0)
            {
                _edit.Holes.Set(shape, LabelStepRules.CopyHoles(target.Suggestion));
            }

            replacements[target.Index] = shape;
            replacementProvenance[target.Index] = LabelStepRules.ToProvenance(target.Suggestion, SuggestionModel);
            applied.Add(target.Suggestion);
            labels.Add(shape.Label ?? string.Empty);
        }

        List<int> deletions = new List<int>(plan.Removals.Count);
        foreach (ReviewTarget target in plan.Removals)
        {
            deletions.Add(target.Index);
            applied.Add(target.Suggestion);
            labels.Add(document.Shapes[target.Index].Label ?? string.Empty);
        }

        if (applied.Count > 0)
        {
            if (replacements.Count == 0 && deletions.Count == 0)
            {
                _editor.Add(shapes, provenance, "Accept suggestions");
            }
            else
            {
                // One undo unit for the whole review: undo restores replaced and deleted shapes at their old indexes.
                _editor.ApplyReview(shapes, provenance, replacements, replacementProvenance, deletions, "Accept review");
                Session.ClearSelection();
            }

            Session.NotifyDocumentChanged();
            RaiseDecisions(applied, labels, true);
        }

        // Skipped suggestions leave the layer without a recorded decision: their target can never match again.
        RemoveSuggestions(chosen);
        if (plan.Skipped.Count > 0)
        {
            ReviewSkipped?.Invoke(plan.Skipped.Count);
        }

        if (applied.Count > 0)
        {
            NotifyEdited();
        }

        return applied.Count;
    }

    /// <summary>Drops the selected (or all visible) suggestions from the layer without touching the document.</summary>
    public int RejectSuggestions(bool onlySelected)
    {
        return RejectChosen(onlySelected
            ? _suggestions.Where(s => _selectedSuggestions.Contains(s)).ToList()
            : _suggestions.ToList());
    }

    /// <summary>Reject (same path as <see cref="RejectSuggestions(bool)"/>) for the visible suggestions that pass <paramref name="filter"/>. Returns the number removed.</summary>
    public int RejectSuggestionsWhere(Func<SegAiSuggestion, bool> filter)
    {
        return RejectChosen(_suggestions.Where(filter).ToList());
    }

    /// <summary>The X icon next to one suggestion: drops exactly that suggestion from the layer. Returns 1 when removed, 0 when it is not in the layer.</summary>
    public int RejectSuggestion(SegAiSuggestion suggestion)
    {
        if (suggestion == null || !_suggestions.Contains(suggestion, ReferenceEqualityComparer.Instance))
        {
            return 0;
        }

        return RejectChosen(new List<SegAiSuggestion> { suggestion });
    }

    private int RejectChosen(List<SegAiSuggestion> chosen)
    {
        if (chosen.Count == 0)
        {
            return 0;
        }

        RaiseDecisions(chosen, chosen.Select(s => s.Label ?? ClassNameAt(s.ClassIndex)).ToList(), false);
        RemoveSuggestions(chosen);
        return chosen.Count;
    }

    // One SuggestionDecided per resolved suggestion; a failing subscriber must not break accept / reject.
    private void RaiseDecisions(List<SegAiSuggestion> chosen, List<string> labels, bool accepted)
    {
        Action<SuggestionDecision>? handler = SuggestionDecided;
        if (handler == null)
        {
            return;
        }

        for (int i = 0; i < chosen.Count; i++)
        {
            try
            {
                handler(AgentOutcomeRecording.CreateDecision(chosen[i], labels[i], accepted, ImageWidth, ImageHeight));
            }
            catch (Exception)
            {
            }
        }
    }

    private void RemoveSuggestions(List<SegAiSuggestion> removed)
    {
        HashSet<SegAiSuggestion> gone = new HashSet<SegAiSuggestion>(removed, ReferenceEqualityComparer.Instance);
        _suggestions = _suggestions.Where(s => !gone.Contains(s)).ToList();
        _selectedSuggestions.RemoveAll(s => gone.Contains(s));
        SuggestionsResolved?.Invoke(removed);
        RaiseSuggestionsChanged();
    }

    private void RaiseSuggestionsChanged()
    {
        SuggestionsChanged?.Invoke(this, EventArgs.Empty);
        RaiseRender();
    }

    // ---------- commands ----------

    private bool CanUndo() => _editor != null && _editor.History.CanUndo;

    private bool CanRedo() => _editor != null && _editor.History.CanRedo;

    private bool CanOnSelection() => HasSelection;

    private bool CanPaste() => Session != null && _clipboard.HasContent;

    private bool HasSession() => Session != null;

    private bool HasImage() => Image != null;

    private bool CanUndoLastPoint() => Session != null && Session.HasDraft;

    /// <summary>Removes the last point of the shape being drawn (Labelme "Undo last point").</summary>
    [RelayCommand(CanExecute = nameof(CanUndoLastPoint))]
    private void UndoLastPoint()
    {
        RemoveDraftVertex();
    }

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo()
    {
        if (_editor != null && Session != null && _editor.History.Undo())
        {
            Session.NotifyDocumentChanged();
            NotifyEdited();
        }
    }

    [RelayCommand(CanExecute = nameof(CanRedo))]
    private void Redo()
    {
        if (_editor != null && Session != null && _editor.History.Redo())
        {
            Session.NotifyDocumentChanged();
            NotifyEdited();
        }
    }

    [RelayCommand(CanExecute = nameof(CanOnSelection))]
    private void Delete()
    {
        if (Session != null)
        {
            Session.Delete();
            NotifyEdited();
        }
    }

    [RelayCommand(CanExecute = nameof(CanOnSelection))]
    private void Copy()
    {
        if (Session != null)
        {
            Session.Copy();
            NotifyEdited();
        }
    }

    [RelayCommand(CanExecute = nameof(CanPaste))]
    private void Paste()
    {
        if (Session != null)
        {
            Session.Paste();
            NotifyEdited();
        }
    }

    [RelayCommand(CanExecute = nameof(CanOnSelection))]
    private void Duplicate()
    {
        if (Session != null)
        {
            Session.Duplicate();
            NotifyEdited();
        }
    }

    [RelayCommand(CanExecute = nameof(HasSession))]
    private void SelectAll()
    {
        if (Session != null)
        {
            ClearSuggestionSelection();
            Session.SelectAll();
            NotifyEdited();
        }
    }

    [RelayCommand(CanExecute = nameof(HasImage))]
    private void Fit()
    {
        FitRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand(CanExecute = nameof(HasImage))]
    private void ActualSize()
    {
        ActualSizeRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand(CanExecute = nameof(HasImage))]
    private void ZoomIn()
    {
        ZoomStepRequested?.Invoke(this, 1);
    }

    [RelayCommand(CanExecute = nameof(HasImage))]
    private void ZoomOut()
    {
        ZoomStepRequested?.Invoke(this, -1);
    }

    [RelayCommand(CanExecute = nameof(HasImage))]
    private void FitWidth()
    {
        FitWidthRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand(CanExecute = nameof(HasImage))]
    private void AdjustBrightness()
    {
        BrightnessPrompt?.Invoke();
    }

    /// <summary>Resets brightness and contrast of the displayed image.</summary>
    [RelayCommand]
    private void ResetBrightness()
    {
        Brightness = 0;
        Contrast = 0;
    }

    private bool CanMergeShapes() => Session != null && Session.CanMerge();

    /// <summary>Merges the selected same-label closed shapes into one polygon (disabled when their union is not a single polygon).</summary>
    [RelayCommand(CanExecute = nameof(CanMergeShapes))]
    private void MergeShapes()
    {
        if (Session != null && Session.Merge())
        {
            NotifyEdited();
        }
    }

    private bool CanConvertShape(string? target) => Session != null && !string.IsNullOrEmpty(target)
        && (Kind != ProjectKind.Position || target == "oriented_rectangle" || target == "circle")
        && Session.CanConvertSelection(target);

    /// <summary>Converts the selected shapes to <paramref name="target"/> ("polygon", "rectangle", "oriented_rectangle", "circle", "point") as one undo step.</summary>
    [RelayCommand(CanExecute = nameof(CanConvertShape))]
    private void ConvertShape(string? target)
    {
        CanvasEditSession? session = Session;
        if (session == null || string.IsNullOrEmpty(target))
        {
            return;
        }

        ShapeConversionPlan plan = session.PlanConversion(target);
        if (plan.Replacements.Count == 0)
        {
            return;
        }

        if (plan.WarningKeys.Count > 0)
        {
            Func<string, string, bool>? confirm = ConvertConfirmPrompt;
            string warnings = string.Join(Environment.NewLine, plan.WarningKeys.Select(WarningText));
            if (confirm == null || !confirm(Strings.ConvertShapeTitle, warnings))
            {
                return;
            }
        }

        if (session.ApplyConversion(plan))
        {
            NotifyEdited();
            if (plan.Skipped > 0)
            {
                _reportError(Strings.ConvertShapeTitle, string.Format(Strings.ConvertShapeSkippedFormat, plan.Skipped));
            }
        }
    }

    private static string WarningText(string key)
    {
        switch (key)
        {
            case ShapeConverter.LossyHolesKey: return Strings.ShapeConvertLossyHoles;
            case ShapeConverter.LossyLineKey: return Strings.ShapeConvertLossyLine;
            case ShapeConverter.LossyPointKey: return Strings.ShapeConvertLossyPoint;
            case ShapeConverter.LossyCircleKey: return Strings.ShapeConvertLossyCircle;
            case ShapeConverter.LossyVerticesKey: return Strings.ShapeConvertLossyVertices;
            default: return key;
        }
    }

    private bool CanAddPointOnEdge()
    {
        return Session != null && _contextValid && Session.CanAddPointOnEdge(_contextX, _contextY, _contextScale);
    }

    /// <summary>Context menu: inserts a vertex on the edge under the right-click position.</summary>
    [RelayCommand(CanExecute = nameof(CanAddPointOnEdge))]
    private void AddPointOnEdge()
    {
        if (Session != null && Session.AddPointOnEdge(_contextX, _contextY, _contextScale))
        {
            NotifyEdited();
        }
    }

    private bool CanDeleteVertex() => Session != null && Session.CanDeleteSelectedVertex;

    [RelayCommand(CanExecute = nameof(CanDeleteVertex))]
    private void DeleteVertex()
    {
        if (Session != null && Session.DeleteSelectedVertex())
        {
            NotifyEdited();
        }
    }

    [RelayCommand(CanExecute = nameof(CanOnSelection))]
    private void EditLabel()
    {
        PromptLabel(false);
    }

    /// <summary>Ctrl+arrows: selects the nearest shape above, below, left or right of the selected one.</summary>
    [RelayCommand(CanExecute = nameof(HasSession))]
    private void SelectNeighbour(SelectDirection direction)
    {
        if (Session != null)
        {
            ClearSuggestionSelection();
            Session.SelectInDirection(direction);
            NotifyEdited();
        }
    }

    [RelayCommand(CanExecute = nameof(HasSession))]
    private void HideShapes()
    {
        SetAllShapesVisible(false);
    }

    [RelayCommand(CanExecute = nameof(HasSession))]
    private void ShowShapes()
    {
        SetAllShapesVisible(true);
    }

    /// <summary>"Toggle shapes" (T): hides every shape when any is visible, otherwise shows all.</summary>
    [RelayCommand(CanExecute = nameof(HasSession))]
    private void ToggleShapes()
    {
        int count = Document?.Shapes.Count ?? 0;
        SetAllShapesVisible(_hiddenShapes.Count >= count);
    }

    private void SetAllShapesVisible(bool visible)
    {
        int count = Document?.Shapes.Count ?? 0;
        _hiddenShapes.Clear();
        if (!visible)
        {
            for (int i = 0; i < count; i++)
            {
                _hiddenShapes.Add(i);
            }

            Session?.ClearSelection();
        }

        NotifyCommands();
        RaiseRender();
    }

    private bool CanSelectAiPoints() => AiPointsEnabled;

    private bool CanSelectAiBox() => AiBoxEnabled;

    /// <summary>Menu / hotkey entry of the AI-Points tool; enabled exactly when the Auto-label source allows point prompts.</summary>
    [RelayCommand(CanExecute = nameof(CanSelectAiPoints))]
    private void SelectAiPoints()
    {
        Tool = CanvasTool.AiPoints;
    }

    /// <summary>Menu / hotkey entry of the AI-Box tool; enabled exactly when the Auto-label source allows box prompts.</summary>
    [RelayCommand(CanExecute = nameof(CanSelectAiBox))]
    private void SelectAiBox()
    {
        Tool = CanvasTool.AiBox;
    }

    /// <summary>Menu entry for the V/R/O/P hotkeys: switches the drawing tool.</summary>
    [RelayCommand]
    private void SelectTool(CanvasTool tool)
    {
        Tool = tool;
    }

    /// <summary>Menu entry of the NG / OK class: switches the class the next shape is drawn with.</summary>
    [RelayCommand]
    private void SetClass(AnnotationClass annotationClass)
    {
        SetActiveClass((int)annotationClass);
    }

    /// <summary>Class menu entry of any class of the table (parameter = class index); a disabled class is refused by <see cref="SetActiveClass"/>.</summary>
    [RelayCommand]
    private void SetClassIndex(int index)
    {
        SetActiveClass(index);
    }

    // ---------- saving ----------

    /// <summary>
    /// When the document differs from the last queued state, snapshots it (document, sidecar, holes) on the UI thread and queues
    /// a write. The worker only touches the snapshot, so edits keep running while it writes; the session lock orders the write
    /// against every other SegAiSession call.
    /// </summary>
    private void QueueSaveIfChanged()
    {
        SegAiSession? segAi = _segAi;
        SegAiEditDocument? edit = _edit;
        if (segAi == null || edit == null)
        {
            return;
        }

        string signature = Signature(edit.Document);
        if (!SavePolicy.WritesImmediately(AutoSave))
        {
            // Auto-save OFF: the edit stays in memory; the dirty flag follows the document (undo back to the saved state is clean).
            _dirty.Update(signature);
            IsDirty = _dirty.IsDirty;
            return;
        }

        _dirty.Update(signature);
        if (!_dirty.IsDirty)
        {
            return;
        }

        EnqueueSave(segAi, edit, signature);
    }

    /// <summary>
    /// Writes the held edits of the current image now (Save command, Auto-save switched ON, or "Save" in the unsaved prompt).
    /// Returns false when there is no editable document. The write itself still runs on the save queue.
    /// </summary>
    public bool SaveNow()
    {
        SegAiSession? segAi = _segAi;
        SegAiEditDocument? edit = _edit;
        if (segAi == null || edit == null)
        {
            return false;
        }

        string signature = Signature(edit.Document);
        _dirty.Update(signature);
        if (!_dirty.IsDirty)
        {
            IsDirty = false;
            return true;
        }

        EnqueueSave(segAi, edit, signature);
        return true;
    }

    /// <summary>The user chose "Don't save": the held edits count as handled (the document is dropped or reloaded by the caller).</summary>
    public void DiscardChanges()
    {
        if (_edit != null)
        {
            _dirty.MarkSaved(Signature(_edit.Document));
        }

        IsDirty = false;
    }

    /// <summary>
    /// "Save as": writes the LabelMe JSON of the current document to <paramref name="path"/> (embedding the image when the
    /// setting is on). The project's own files and the dirty state are untouched. Returns false on failure (error reported).
    /// </summary>
    public async Task<bool> SaveCopyToAsync(string path, string imagePath)
    {
        SegAiEditDocument? edit = _edit;
        if (edit == null)
        {
            return false;
        }

        LabelMeDocument copy = DocumentEditor.Clone(edit.Document);
        bool embed = Options == null || Options.EmbedImageData;
        try
        {
            await Task.Run(() => new LabelMeIO().Write(copy, path, embed ? imagePath : null));
            return true;
        }
        catch (Exception ex)
        {
            _reportError(Strings.SaveErrorTitle, path + ": " + ex.Message);
            return false;
        }
    }

    private void EnqueueSave(SegAiSession segAi, SegAiEditDocument edit, string signature)
    {
        _dirty.MarkSaved(signature);
        IsDirty = false;
        bool embed = Options == null || Options.EmbedImageData;
        string? mirror = SavePolicy.MirrorDirectory(Options?.OutputDirectory);
        string rel = edit.Rel;
        LabelMeDocument document = DocumentEditor.Clone(edit.Document);
        EasyLabelSidecar sidecar = DocumentEditor.Clone(edit.Sidecar);
        var holes = new Dictionary<string, PointF[][]>(StringComparer.Ordinal);
        foreach (LabelShape shape in document.Shapes)
        {
            if (edit.Holes.TryGet(shape, out PointF[][]? found) && found != null)
            {
                holes[ShapeHoleStore<PointF[][]>.KeyOf(shape)] = found;
            }
        }

        _queue.Enqueue(new AnnotationSaveJob(rel, () =>
            segAi.SaveEditedShapes(
                rel,
                document,
                sidecar,
                shape => holes.TryGetValue(ShapeHoleStore<PointF[][]>.KeyOf(shape), out PointF[][]? value) ? value : null,
                embed,
                mirror)));
    }

    private static string Signature(LabelMeDocument document)
    {
        var sb = new StringBuilder();
        if (document.Flags != null && document.Flags.Count > 0)
        {
            // Image flags are part of the saved state, so toggling one makes the image dirty (and undoing it clean again).
            foreach (KeyValuePair<string, bool> flag in document.Flags.OrderBy(f => f.Key, StringComparer.Ordinal))
            {
                sb.Append(flag.Key).Append('=').Append(flag.Value ? '1' : '0').Append(',');
            }

            sb.Append('#');
        }

        foreach (LabelShape shape in document.Shapes)
        {
            sb.Append(shape.ShapeType).Append('|').Append(shape.Label).Append('|').Append(ShapeHoleStore<PointF[][]>.KeyOf(shape))
                .Append('|').Append(shape.GroupId).Append('|').Append(shape.Description).Append(';');
        }

        return sb.ToString();
    }

    private void OnSaved(string rel)
    {
        _dispatcher.BeginInvoke(new Action(() => ImageSaved?.Invoke(rel)));
    }

    private void OnSaveFailed(string rel, Exception exception)
    {
        _dispatcher.BeginInvoke(new Action(() =>
        {
            // The edit never reached disk: forget the saved state of the loaded image so the next edit or image switch retries.
            // A sentinel (not string.Empty) so that a failed "all shapes deleted" save, whose signature is empty, retries too.
            if (_edit != null && string.Equals(_edit.Rel, rel, StringComparison.Ordinal))
            {
                _dirty.MarkUnsaved();
                IsDirty = true;
            }

            _reportError(Strings.SaveErrorTitle, rel + ": " + exception.Message);
        }));
    }

    private void NotifyCommands()
    {
        UndoCommand.NotifyCanExecuteChanged();
        UndoLastPointCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
        DeleteCommand.NotifyCanExecuteChanged();
        CopyCommand.NotifyCanExecuteChanged();
        PasteCommand.NotifyCanExecuteChanged();
        DuplicateCommand.NotifyCanExecuteChanged();
        SelectAllCommand.NotifyCanExecuteChanged();
        FitCommand.NotifyCanExecuteChanged();
        ActualSizeCommand.NotifyCanExecuteChanged();
        ZoomInCommand.NotifyCanExecuteChanged();
        ZoomOutCommand.NotifyCanExecuteChanged();
        FitWidthCommand.NotifyCanExecuteChanged();
        AdjustBrightnessCommand.NotifyCanExecuteChanged();
        MergeShapesCommand.NotifyCanExecuteChanged();
        ConvertShapeCommand.NotifyCanExecuteChanged();
        AddPointOnEdgeCommand.NotifyCanExecuteChanged();
        DeleteVertexCommand.NotifyCanExecuteChanged();
        EditLabelCommand.NotifyCanExecuteChanged();
        SelectNeighbourCommand.NotifyCanExecuteChanged();
        HideShapesCommand.NotifyCanExecuteChanged();
        ShowShapesCommand.NotifyCanExecuteChanged();
        ToggleShapesCommand.NotifyCanExecuteChanged();
    }

    private void RaiseRender()
    {
        RenderRequested?.Invoke(this, EventArgs.Empty);
    }
}
