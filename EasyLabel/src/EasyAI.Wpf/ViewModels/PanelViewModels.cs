using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows.Input;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EasyAI.Desktop.Annotation;
using EasyAI.Desktop.Infrastructure;
using EasyAI.Desktop.Resources;
using EasyLabel.Core.Editing;
using EasyLabel.Core.Model;
using EasyLabel.SegAI.Data;

namespace EasyAI.Desktop.ViewModels;

/// <summary>The view models of the dockable panels. They only read and command the canvas / main view model; no state of their own is saved.</summary>
public sealed class PanelsViewModel
{
    public PanelsViewModel(MainViewModel host)
    {
        Labels = new LabelsPanelViewModel(host.Canvas) { NewLabelCommand = host.NewLabelCommand };
        Files = new FilesPanelViewModel(host);
    }

    /// <summary>The merged label / shape list.</summary>
    public LabelsPanelViewModel Labels { get; }

    public FilesPanelViewModel Files { get; }
}

// ---------- Merged label / shape list ----------

/// <summary>One shape of the current image under its label: visibility checkbox (view only), kind, ignored marker and selection.</summary>
public sealed class ShapeRowViewModel : ObservableObject
{
    private readonly LabelsPanelViewModel _owner;
    private bool _isVisible;
    private bool _isSelected;
    private Brush _swatch;

    public ShapeRowViewModel(LabelsPanelViewModel owner, int index, string label, string kind, Brush swatch, bool ignored, bool visible)
    {
        _owner = owner;
        Index = index;
        Label = label;
        Kind = kind;
        _swatch = swatch;
        IsIgnored = ignored;
        _isVisible = visible;
    }

    public int Index { get; }

    public string Label { get; }

    public string Kind { get; }

    /// <summary>Labels other than NG / OK are stored and drawn but never used by Segment AI.</summary>
    public bool IsIgnored { get; }

    public Brush Swatch
    {
        get => _swatch;
        private set => SetProperty(ref _swatch, value);
    }

    /// <summary>Checkbox: shows or hides the shape on the canvas (view state, never saved).</summary>
    public bool IsVisible
    {
        get => _isVisible;
        set
        {
            if (SetProperty(ref _isVisible, value))
            {
                _owner.OnShapeVisibleChanged(this);
            }
        }
    }

    /// <summary>Highlight only: the selection itself lives in the edit session.</summary>
    public bool IsSelected => _isSelected;

    internal void SyncVisible(bool value) => SetProperty(ref _isVisible, value, nameof(IsVisible));

    internal void SyncSelected(bool value) => SetProperty(ref _isSelected, value, nameof(IsSelected));

    internal void SyncSwatch(Brush value)
    {
        if (!ReferenceEquals(_swatch, value))
        {
            Swatch = value;
        }
    }
}

/// <summary>A label row: colour dot, name, shape count on the current image, active-class mark, show/hide checkbox, collapsible shapes.</summary>
public sealed class LabelGroupViewModel : ObservableObject
{
    private readonly LabelsPanelViewModel _owner;
    private Brush _swatch;
    private int _count;
    private bool _isActive;
    private bool _isShown = true;
    private bool _isExpanded;

    public LabelGroupViewModel(LabelsPanelViewModel owner, string name, bool ignored, Brush swatch, string automationKey, bool expanded)
    {
        _owner = owner;
        Name = name;
        IsIgnored = ignored;
        _swatch = swatch;
        AutomationKey = automationKey;
        _isExpanded = expanded;
    }

    public string Name { get; }

    public string DisplayName => Name.Length == 0 ? Strings.PnlNoLabel : Name;

    public bool IsIgnored { get; }

    /// <summary>"ClassNg" / "ClassOk" for the NG and OK rows (the smoke harness clicks them), empty for the others.</summary>
    public string AutomationKey { get; }

    public ObservableCollection<ShapeRowViewModel> Shapes { get; } = new ObservableCollection<ShapeRowViewModel>();

    public bool HasShapes => Shapes.Count > 0;

    public Brush Swatch
    {
        get => _swatch;
        private set => SetProperty(ref _swatch, value);
    }

    public int Count
    {
        get => _count;
        private set
        {
            if (SetProperty(ref _count, value))
            {
                OnPropertyChanged(nameof(HasShapes));
            }
        }
    }

    /// <summary>This label is the class the next shape is drawn with (a project class only).</summary>
    public bool IsActive
    {
        get => _isActive;
        private set => SetProperty(ref _isActive, value);
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (SetProperty(ref _isExpanded, value))
            {
                _owner.OnGroupExpandedChanged(this);
            }
        }
    }

    /// <summary>Checkbox: every shape of this label is shown. Setting it shows or hides them all (view only).</summary>
    public bool IsShown
    {
        get => _isShown;
        set
        {
            if (SetProperty(ref _isShown, value))
            {
                _owner.OnGroupShownChanged(this);
            }
        }
    }

    internal IReadOnlyList<int> ShapeIndices
    {
        get
        {
            var indices = new List<int>(Shapes.Count);
            foreach (ShapeRowViewModel row in Shapes)
            {
                indices.Add(row.Index);
            }

            return indices;
        }
    }

    internal void Sync(int count, bool active, bool shown, Brush swatch)
    {
        Count = count;
        IsActive = active;
        SetProperty(ref _isShown, shown, nameof(IsShown));
        if (!ReferenceEquals(_swatch, swatch))
        {
            Swatch = swatch;
        }
    }
}

/// <summary>
/// Two-level list: label rows (NG and OK always present) with their shapes beneath. Clicking NG or OK makes it the active drawing
/// class, clicking a shape selects it in the edit session (two-way, no feedback loop), the checkboxes drive the canvas view-only
/// hide flags.
/// </summary>
public sealed class LabelsPanelViewModel : ObservableObject
{
    private readonly CanvasViewModel _canvas;
    private readonly HashSet<string> _collapsed = new HashSet<string>(StringComparer.Ordinal);
    private object? _document;
    private int _hash = int.MinValue;
    private string[] _names = Array.Empty<string>();
    private bool[] _enabled = Array.Empty<bool>();
    private bool _syncing;

    public LabelsPanelViewModel(CanvasViewModel canvas)
    {
        _canvas = canvas;
        _canvas.RenderRequested -= OnRender;
        _canvas.RenderRequested += OnRender;
        _canvas.PropertyChanged -= OnCanvasPropertyChanged;
        _canvas.PropertyChanged += OnCanvasPropertyChanged;
        Refresh();
    }

    public ObservableCollection<LabelGroupViewModel> Groups { get; } = new ObservableCollection<LabelGroupViewModel>();

    /// <summary>Right-click "New label..." (the main view model's command).</summary>
    public IRelayCommand? NewLabelCommand { get; init; }

    /// <summary>A label row was clicked: an enabled project class becomes the active class, other labels are not a drawing class.</summary>
    public void ActivateGroup(LabelGroupViewModel group)
    {
        _canvas.SetActiveClass(ClassTableRules.IndexOf(group.Name, _canvas.ClassNames));
        Refresh();
    }

    /// <summary>A shape row was clicked: selects it (Ctrl adds or removes it). A hidden shape cannot be selected.</summary>
    public void SelectShape(ShapeRowViewModel row, bool additive)
    {
        CanvasEditSession? session = _canvas.Session;
        if (session == null)
        {
            return;
        }

        List<int> wanted = ShapeSelectionRules.Next(session.SelectedIndices, row.Index, additive, _canvas.IsShapeVisible(row.Index));
        if (!ShapeListRules.SameSet(session.SelectedIndices, wanted))
        {
            session.ClearSelection();
            foreach (int index in wanted)
            {
                session.Select(index, true);
            }

            _canvas.NotifySelectionChanged();
        }

        Refresh();
    }

    /// <summary>Delete key in the list: the same command as the toolbar and the canvas.</summary>
    public void DeleteSelected()
    {
        if (_canvas.DeleteCommand.CanExecute(null))
        {
            _canvas.DeleteCommand.Execute(null);
        }
    }

    internal void OnGroupExpandedChanged(LabelGroupViewModel group)
    {
        if (group.IsExpanded)
        {
            _collapsed.Remove(group.Name);
        }
        else
        {
            _collapsed.Add(group.Name);
        }
    }

    internal void OnGroupShownChanged(LabelGroupViewModel group)
    {
        if (_syncing)
        {
            return;
        }

        _canvas.SetShapesVisible(group.ShapeIndices, group.IsShown);
        Refresh();
    }

    internal void OnShapeVisibleChanged(ShapeRowViewModel row)
    {
        if (_syncing)
        {
            return;
        }

        _canvas.SetShapeVisible(row.Index, row.IsVisible);
        Refresh();
    }

    private void OnRender(object? sender, EventArgs e)
    {
        Refresh();
    }

    private void OnCanvasPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CanvasViewModel.ActiveClassIndex) || e.PropertyName == nameof(CanvasViewModel.NgName)
            || e.PropertyName == nameof(CanvasViewModel.OkName) || e.PropertyName == nameof(CanvasViewModel.ClassNames))
        {
            Refresh();
        }
    }

    private void Refresh()
    {
        if (_syncing)
        {
            return;
        }

        IReadOnlyList<LabelShape>? shapes = _canvas.Document?.Shapes;
        int hash = ShapeListRules.ShapeHash(shapes);
        bool rebuild = !ReferenceEquals(_document, _canvas.Document)
            || hash != _hash
            || !_names.SequenceEqual(_canvas.ClassNames)
            || !_enabled.SequenceEqual(_canvas.ClassEnabled);
        _syncing = true;
        try
        {
            if (rebuild)
            {
                _document = _canvas.Document;
                _hash = hash;
                _names = _canvas.ClassNames.ToArray();
                _enabled = _canvas.ClassEnabled.ToArray();
                Rebuild(shapes);
            }

            IReadOnlyList<int> selected = _canvas.Session?.SelectedIndices ?? Array.Empty<int>();
            string active = _canvas.ClassNameAt(_canvas.ActiveClassIndex);
            foreach (LabelGroupViewModel group in Groups)
            {
                foreach (ShapeRowViewModel row in group.Shapes)
                {
                    row.SyncVisible(_canvas.IsShapeVisible(row.Index));
                    row.SyncSelected(ContainsIndex(selected, row.Index));
                    row.SyncSwatch(ColorOf(row.Label));
                }

                bool shown = LabelGroupRules.AllVisible(group.ShapeIndices, _canvas.IsShapeVisible);
                group.Sync(group.Shapes.Count, string.Equals(group.Name, active, StringComparison.Ordinal), shown, ColorOf(group.Name));
            }
        }
        finally
        {
            _syncing = false;
        }
    }

    private void Rebuild(IReadOnlyList<LabelShape>? shapes)
    {
        var names = LabelListRules.Build(_names, ShapeListRules.UsedLabels(shapes), null, false).ToList();
        Groups.Clear();
        foreach (LabelGroupRow row in LabelGroupRules.Build(shapes, names))
        {
            int classIndex = ClassTableRules.IndexOf(row.Name, _names);
            string key = classIndex == 0 ? "ClassNg"
                : classIndex == 1 ? "ClassOk"
                : string.Empty;
            var group = new LabelGroupViewModel(
                this,
                row.Name,
                ShapeListRules.IsIgnoredBySegAi(row.Name, _names, _enabled),
                ColorOf(row.Name),
                key,
                !_collapsed.Contains(row.Name));
            foreach (int index in row.ShapeIndices)
            {
                LabelShape shape = shapes![index];
                group.Shapes.Add(new ShapeRowViewModel(
                    this,
                    index,
                    shape.Label ?? string.Empty,
                    ShapeListRules.KindText(shape.ShapeType),
                    ColorOf(shape.Label),
                    ShapeListRules.IsIgnoredBySegAi(shape.Label, _names, _enabled),
                    _canvas.IsShapeVisible(index)));
            }

            Groups.Add(group);
        }
    }

    private static bool ContainsIndex(IReadOnlyList<int> list, int index)
    {
        for (int i = 0; i < list.Count; i++)
        {
            if (list[i] == index)
            {
                return true;
            }
        }

        return false;
    }

    private Brush ColorOf(string? label)
    {
        return AnnotationPalette.For(_canvas.ColorOf(label)).Solid;
    }
}

// ---------- Files ("Danh sach Tep") ----------

/// <summary>The image list with the search box. Selection is two-way with <see cref="MainViewModel.SelectedImage"/>.</summary>
public sealed partial class FilesPanelViewModel : ObservableObject
{
    private readonly MainViewModel _host;
    private ObservableCollection<ImageItemViewModel>? _watched;
    private ImageItemViewModel? _unsavedItem;

    [ObservableProperty]
    private string _searchText = string.Empty;

    /// <summary>Order the rows by the local uncertainty of their cached result, most doubtful first (rows without a result last). Off = list order.</summary>
    [ObservableProperty]
    private bool _mostDoubtfulFirst;

    /// <summary>List order of the Label tab: on = images without a label first; off = by name. Either way each group is by name.</summary>
    [ObservableProperty]
    private bool _unlabelledFirst;

    partial void OnUnlabelledFirstChanged(bool value)
    {
        Rebuild();
    }

    /// <summary>Train images before the others (after the unlabelled ones when that toggle is on).</summary>
    [ObservableProperty]
    private bool _trainFirst;

    partial void OnTrainFirstChanged(bool value)
    {
        Rebuild();
    }

    /// <summary>Val images before the others (after the Train ones when that box is on too).</summary>
    [ObservableProperty]
    private bool _valFirst;

    partial void OnValFirstChanged(bool value)
    {
        Rebuild();
    }

    public string TrainFirstText => Strings.ByName("FileSortTrainFirst");

    public string ValFirstText => Strings.ByName("FileSortValFirst");

    public string RemoveLabelsText => Strings.ByName("FileRemoveLabels");

    private IReadOnlyList<ImageItemViewModel> _multiSelection = Array.Empty<ImageItemViewModel>();

    /// <summary>The rows selected with Ctrl / Shift in the list (set by the list view); empty or one row = just the current image.</summary>
    public IReadOnlyList<ImageItemViewModel> MultiSelection
    {
        get { return _multiSelection; }
        set { _multiSelection = value ?? Array.Empty<ImageItemViewModel>(); }
    }

    /// <summary>Context menu Train / Val on every selected row.</summary>
    public Task SetModeForSelectionAsync(IReadOnlyList<ImageItemViewModel> rows, SegTrainingMode target) => _host.Data.SetModeManyAsync(rows, target);

    /// <summary>Context menu Remove label on every selected row.</summary>
    public Task RemoveLabelsForSelectionAsync(IReadOnlyList<ImageItemViewModel> rows) => _host.RemoveLabelsAsync(rows);

    /// <summary>Context menu Delete on every selected row.</summary>
    public Task DeleteSelectionAsync(IReadOnlyList<ImageItemViewModel> rows) => _host.DeleteImagesAsync(rows);

    /// <summary>Caption of the "Unlabelled first" toggle; English text until the resource has the key.</summary>
    public string UnlabelledFirstText
    {
        get
        {
            string text = Strings.ByName("FileSortUnlabelledFirst");
            return string.Equals(text, "FileSortUnlabelledFirst", StringComparison.Ordinal) ? "Unlabelled first" : text;
        }
    }

    [ObservableProperty]
    private ObservableCollection<ImageItemViewModel> _items = new ObservableCollection<ImageItemViewModel>();

    public FilesPanelViewModel(MainViewModel host)
    {
        _host = host;
        _host.PropertyChanged -= OnHostPropertyChanged;
        _host.PropertyChanged += OnHostPropertyChanged;
        _host.Canvas.PropertyChanged -= OnCanvasPropertyChanged;
        _host.Canvas.PropertyChanged += OnCanvasPropertyChanged;
        _host.Canvas.DocumentReady -= OnDocumentReady;
        _host.Canvas.DocumentReady += OnDocumentReady;
        _host.Data.ModeAvailabilityChanged -= OnModeAvailabilityChanged;
        _host.Data.ModeAvailabilityChanged += OnModeAvailabilityChanged;
        SetTrainCommand = new AsyncRelayCommand<ImageItemViewModel>(item => SetModeAsync(item, SegTrainingMode.Train), CanSetMode);
        SetValCommand = new AsyncRelayCommand<ImageItemViewModel>(item => SetModeAsync(item, SegTrainingMode.Val), CanSetMode);
        Watch();
        Rebuild();
    }

    /// <summary>Train button of a file row: puts THAT file in the Train set (CommandParameter = the row's image).</summary>
    public IAsyncRelayCommand<ImageItemViewModel> SetTrainCommand { get; }

    /// <summary>Val button of a file row.</summary>
    public IAsyncRelayCommand<ImageItemViewModel> SetValCommand { get; }

    private bool CanSetMode(ImageItemViewModel? item) => _host.Data.CanSetMode(item);

    private Task SetModeAsync(ImageItemViewModel? item, SegTrainingMode target)
    {
        return item == null ? Task.CompletedTask : _host.Data.SetModeAsync(item, target);
    }

    private void OnModeAvailabilityChanged(object? sender, EventArgs e)
    {
        SetTrainCommand.NotifyCanExecuteChanged();
        SetValCommand.NotifyCanExecuteChanged();
    }

    /// <summary>The selected image. A null write (the list lost the row while it was filtered or rebuilt) is ignored: closing is a command.</summary>
    public ImageItemViewModel? SelectedItem
    {
        get => _host.SelectedImage;
        set
        {
            if (value != null && !ReferenceEquals(value, _host.SelectedImage))
            {
                _host.SelectedImage = value;
            }
        }
    }

    /// <summary>"OK n · NG n · Not marked n" of the project images (the Label step counts), shown under the file list.</summary>
    public string ClassCountsText => _host.Label.ClassCountsText;

    /// <summary>Raised after an image class changed or the project list was replaced.</summary>
    internal void NotifyClassesChanged()
    {
        OnPropertyChanged(nameof(ClassCountsText));
    }

    public string CountText => Items.Count == _host.Images.Count
        ? _host.Images.Count.ToString(System.Globalization.CultureInfo.CurrentCulture)
        : Items.Count.ToString(System.Globalization.CultureInfo.CurrentCulture) + "/" + _host.Images.Count.ToString(System.Globalization.CultureInfo.CurrentCulture);

    partial void OnSearchTextChanged(string value)
    {
        Rebuild();
    }

    private void OnHostPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.Images))
        {
            Watch();
            Rebuild();
        }
        else if (e.PropertyName == nameof(MainViewModel.SelectedImage))
        {
            OnPropertyChanged(nameof(SelectedItem));
            UpdateUnsaved();
        }
    }

    private void OnCanvasPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CanvasViewModel.IsDirty))
        {
            UpdateUnsaved();
        }
    }

    private void OnDocumentReady(object? sender, EventArgs e)
    {
        UpdateUnsaved();
    }

    private void Watch()
    {
        if (_watched != null)
        {
            _watched.CollectionChanged -= OnImagesCollectionChanged;
        }

        _watched = _host.Images;
        _watched.CollectionChanged -= OnImagesCollectionChanged;
        _watched.CollectionChanged += OnImagesCollectionChanged;
    }

    private void OnImagesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Remove && e.OldItems != null)
        {
            // A deleted image only leaves the shown list: the others keep their place, so the list scrolls up by one row instead of being rebuilt.
            foreach (ImageItemViewModel removed in e.OldItems.OfType<ImageItemViewModel>())
            {
                Items.Remove(removed);
            }

            OnPropertyChanged(nameof(CountText));
            _host.NextImageCommand.NotifyCanExecuteChanged();
            _host.PreviousImageCommand.NotifyCanExecuteChanged();
            return;
        }

        Rebuild();
    }

    /// <summary>Caption of the "Most doubtful first" toggle; the key is new, so the English text shows until the resource has it.</summary>
    public string MostDoubtfulText
    {
        get
        {
            string text = Strings.ByName("FileSortMostDoubtful");
            return string.Equals(text, "FileSortMostDoubtful", StringComparison.Ordinal) ? "Most doubtful first" : text;
        }
    }

    partial void OnMostDoubtfulFirstChanged(bool value)
    {
        Rebuild();
        if (value)
        {
            _ = WarmDoubtfulOrderAsync();
        }
        else
        {
            _host.Label?.CancelClassifierUncertainty();
        }
    }

    /// <summary>
    /// Reads the disk-cached local results of the loaded model into memory (no inference), then runs the OK/NG pass (background, cancellable).
    /// Re-sorts the list after each step that changed the cache.
    /// </summary>
    private async Task WarmDoubtfulOrderAsync()
    {
        LabelStepViewModel? label = _host.Label;
        if (label == null)
        {
            return;
        }

        if (await label.WarmUncertaintyFromDiskAsync())
        {
            RefreshOrder();
        }

        if (await label.RefreshClassifierUncertaintyAsync())
        {
            RefreshOrder();
        }
    }

    /// <summary>Puts the list in order again, e.g. after the cached local results changed.</summary>
    public void RefreshOrder()
    {
        if (MostDoubtfulFirst)
        {
            Rebuild();
        }
    }

    private void Rebuild()
    {
        ObservableCollection<ImageItemViewModel> all = _host.Images;
        IEnumerable<ImageItemViewModel> shown;
        if (string.IsNullOrWhiteSpace(SearchText))
        {
            shown = all;
        }
        else
        {
            var names = new List<string>(all.Count);
            foreach (ImageItemViewModel image in all)
            {
                names.Add(image.DisplayName);
            }

            shown = FileListRules.FilterIndices(names, SearchText).Select(i => all[i]);
        }

        Items = new ObservableCollection<ImageItemViewModel>(
            ImageListSortRules.OrderRows(shown, UnlabelledFirst, TrainFirst, ValFirst, image => image.RegionCount <= 0 && !image.HasClass, image => image.IsTrainMode, image => image.IsValMode, image => image.DisplayName));

        OnPropertyChanged(nameof(CountText));
        _host.NextImageCommand.NotifyCanExecuteChanged();
        _host.PreviousImageCommand.NotifyCanExecuteChanged();
        // The list cleared its selection while the items were swapped; re-publish the real one.
        OnPropertyChanged(nameof(SelectedItem));
    }

    /// <summary>Marks the image with held edits (Auto-save OFF) with a leading "*".</summary>
    private void UpdateUnsaved()
    {
        string? rel = _host.Canvas.IsDirty ? _host.Canvas.CurrentRel : null;
        if (_unsavedItem != null && (rel == null || !string.Equals(_unsavedItem.Rel, rel, StringComparison.OrdinalIgnoreCase)))
        {
            _unsavedItem.IsUnsaved = false;
            _unsavedItem = null;
        }

        if (rel != null && _unsavedItem == null)
        {
            ImageItemViewModel? item = _host.Images.FirstOrDefault(i => string.Equals(i.Rel, rel, StringComparison.OrdinalIgnoreCase));
            if (item != null)
            {
                item.IsUnsaved = true;
                _unsavedItem = item;
            }
        }
    }
}
