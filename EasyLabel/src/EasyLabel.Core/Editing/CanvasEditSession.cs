using System.Drawing;
using EasyLabel.Core.Commands;
using EasyLabel.Core.Geometry;
using EasyLabel.Core.Model;

namespace EasyLabel.Core.Editing;

/// <summary>
/// UI-free pointer state machine for the annotation canvas. All coordinates are IMAGE pixels; <c>scale</c> is
/// screen units per image pixel (used only for handle/hit tolerances). Geometry is edited on drafts and written
/// to the <see cref="DocumentEditor"/> exactly once per pointer-up that changed something, so one drag is one
/// undo unit. Esc (<see cref="Cancel"/>) during a drag, right/middle button and no-op edits record nothing.
/// Only rectangle / rotation / polygon shapes are editable; any other shape is left untouched.
/// Double-click contract for the UI: send <see cref="DoubleClick"/> INSTEAD of <see cref="PointerDown"/>
/// for the second click of a double-click.
/// </summary>
public sealed class CanvasEditSession
{
    public const float MinBoxSize = 2f;
    private const float CloseScreenTolerance = 8f;
    private const float EdgeScreenTolerance = 6f;

    private enum DragKind { None, Move, Resize, Rotate, Vertex, DrawRect, Marquee }

    private readonly DocumentEditor _editor;
    private readonly LabelMeDocument _document;
    private readonly List<int> _selected = new List<int>();
    private readonly List<PointF> _draftPoints = new List<PointF>();
    private Dictionary<int, LabelShape> _orig = new Dictionary<int, LabelShape>();
    private Dictionary<int, LabelShape> _draft = new Dictionary<int, LabelShape>();
    private DragKind _drag;
    private PointF _down;
    private PointF _rectNow;
    private int _handleShape = -1;
    private int _handleIndex = -1;
    private CanvasTool _tool;
    private PointF? _hover;
    private int _selectedVertexShape = -1;
    private int _selectedVertex = -1;
    private int _createdIndex = -1;

    public CanvasEditSession(DocumentEditor editor, LabelMeDocument document, int imageWidth, int imageHeight,
        ShapeClipboard? clipboard = null)
    {
        _editor = editor ?? throw new ArgumentNullException(nameof(editor));
        _document = document ?? throw new ArgumentNullException(nameof(document));
        if (imageWidth <= 0 || imageHeight <= 0) throw new ArgumentOutOfRangeException(nameof(imageWidth));
        ImageWidth = imageWidth;
        ImageHeight = imageHeight;
        Clipboard = clipboard ?? new ShapeClipboard();
    }

    public int ImageWidth { get; }
    public int ImageHeight { get; }
    public ShapeClipboard Clipboard { get; }
    /// <summary>Optional hole store notified of every geometry change/copy (null = holes not tracked).</summary>
    public IHoleCarrier? Holes { get; set; }
    /// <summary>Label given to newly drawn shapes.</summary>
    public string ActiveLabel { get; set; } = "NG";
    public float HandleScreenSize { get; set; } = EditHandleMetrics.DefaultHandleScreen;
    /// <summary>When true vertices and moved shapes may leave the image (the SegAI save still rejects out-of-bounds regions).</summary>
    public bool AllowOutOfBounds { get; set; }
    /// <summary>View-state filter: a shape for which this returns false is neither hit-tested nor selectable. Null = all visible.</summary>
    public Func<int, bool>? VisibilityFilter { get; set; }
    /// <summary>Shape and vertex index of the vertex last pressed with the select tool (-1 when none).</summary>
    public int SelectedVertexShape => _selectedVertexShape;
    public int SelectedVertexIndex => _selectedVertex;

    /// <summary>Index of the shape created since the last call (then reset), or -1. Lets the UI open the label dialog.</summary>
    public int TakeCreatedIndex()
    {
        int index = _createdIndex;
        _createdIndex = -1;
        return index;
    }

    private bool Visible(int index) => VisibilityFilter == null || VisibilityFilter(index);

    public CanvasTool Tool
    {
        get => _tool;
        set
        {
            if (_tool == value) return;
            ResetDrag();
            _draftPoints.Clear();
            _tool = value;
        }
    }

    public IReadOnlyList<int> SelectedIndices => _selected;
    public bool IsDragging => _drag != DragKind.None;
    public bool HasDraft => _draftPoints.Count > 0;
    /// <summary>Selection rectangle (image coordinates) while a sweep from empty space is in progress, else null.</summary>
    public RectangleF? MarqueeRect => _drag == DragKind.Marquee ? RectOf(_down, _rectNow) : (RectangleF?)null;
    /// <summary>Polygon vertices / rotation-box clicks collected so far (image coordinates).</summary>
    public IReadOnlyList<PointF> DraftPoints => _draftPoints;
    /// <summary>Shapes being dragged: index to replacement geometry (render these instead of the document shapes).</summary>
    public IReadOnlyDictionary<int, LabelShape> DraftShapes => _draft;
    /// <summary>Last pointer position over the image (rubber band for polygon / rotation creation).</summary>
    public PointF? HoverPoint => _hover;

    /// <summary>Shape being created (rect drag, or rotation box once two clicks exist), else null.</summary>
    public LabelShape? CreationPreview
    {
        get
        {
            if (_drag == DragKind.DrawRect)
            {
                RectangleF r = RectOf(_down, _rectNow);
                return r.Width > 0f || r.Height > 0f ? NewRect(r) : null;
            }
            if (Tool == CanvasTool.Rot && _draftPoints.Count == 2 && _hover.HasValue
                && TryBuildRotBox(_draftPoints[0], _draftPoints[1], _hover.Value, out PointF[] pts))
                return NewShape("oriented_rectangle", pts);
            if ((Tool == CanvasTool.Circle || Tool == CanvasTool.Line) && _draftPoints.Count == 1 && _hover.HasValue
                && Dist(_draftPoints[0], Clamp(_hover.Value)) > 0f)
                return NewShape(Tool == CanvasTool.Circle ? "circle" : "line", new[] { _draftPoints[0], Clamp(_hover.Value) });
            return null;
        }
    }

    private List<LabelShape> Shapes => _document.Shapes;

    // ---------- pointer ----------

    public bool PointerDown(float x, float y, float scale, EditModifiers modifiers, EditPointerButton button = EditPointerButton.Left)
    {
        if (button != EditPointerButton.Left || !ValidScale(scale)) return false;
        ResetDrag();
        PointF p = new PointF(x, y);
        switch (Tool)
        {
            case CanvasTool.Rect:
                _down = Clamp(p);
                _rectNow = _down;
                _drag = DragKind.DrawRect;
                return true;
            case CanvasTool.Rot:
                return AddRotPoint(Clamp(p));
            case CanvasTool.Polygon:
                return AddPolygonPoint(Clamp(p), scale);
            case CanvasTool.Circle:
                return AddTwoClickShape(Clamp(p), "circle", "Draw circle");
            case CanvasTool.Line:
                return AddTwoClickShape(Clamp(p), "line", "Draw line");
            case CanvasTool.Point:
                return AddNew(NewShape("point", new[] { Clamp(p) }), "Draw point");
            case CanvasTool.LineStrip:
                return AddLineStripPoint(Clamp(p), scale);
            case CanvasTool.AiPoints:
            case CanvasTool.AiBox:
                // AI prompts are collected by the UI layer; nothing is selected, drawn or recorded here.
                return false;
            default:
                return SelectDown(p, scale, modifiers);
        }
    }

    public bool PointerMove(float x, float y, float scale, EditModifiers modifiers)
    {
        PointF p = new PointF(x, y);
        _hover = p;
        if (_drag == DragKind.None || !ValidScale(scale)) return HasDraft;
        switch (_drag)
        {
            case DragKind.DrawRect: _rectNow = Clamp(p); break;
            case DragKind.Marquee: _rectNow = Clamp(p); break;
            case DragKind.Move: UpdateMove(p); break;
            case DragKind.Resize: UpdateResize(p); break;
            case DragKind.Rotate: UpdateRotate(p, (modifiers & EditModifiers.Shift) != 0); break;
            case DragKind.Vertex: UpdateVertex(p); break;
        }
        return true;
    }

    /// <summary>Returns true when the document gained an undo unit.</summary>
    public bool PointerUp(float x, float y, float scale, EditModifiers modifiers, EditPointerButton button = EditPointerButton.Left)
    {
        if (button != EditPointerButton.Left || _drag == DragKind.None) return false;
        PointerMove(x, y, scale, modifiers);
        if (_drag == DragKind.Marquee)
        {
            RectangleF area = RectOf(_down, _rectNow);
            ResetDrag();
            SelectInRect(area, scale);
            return false;
        }
        if (_drag == DragKind.DrawRect)
        {
            RectangleF r = RectOf(_down, _rectNow);
            ResetDrag();
            if (r.Width < MinBoxSize || r.Height < MinBoxSize) return false;
            return AddNew(NewRect(r), "Draw rectangle");
        }
        return CommitDrag();
    }

    /// <summary>Select tool: insert a polygon vertex on the nearest edge of a selected polygon. Polygon tool: finish.</summary>
    public bool DoubleClick(float x, float y, float scale, EditModifiers modifiers)
    {
        if (!ValidScale(scale)) return false;
        ResetDrag();
        if (Tool == CanvasTool.Polygon || Tool == CanvasTool.LineStrip) return Finish();
        if (Tool != CanvasTool.Select) return false;
        return AddPointOnEdge(x, y, scale, EdgeScreenTolerance);
    }

    private bool TryFindEdge(float x, float y, float scale, float screenTolerance, out int shapeIndex, out int edgeIndex, out PointF point)
    {
        shapeIndex = -1;
        edgeIndex = -1;
        point = PointF.Empty;
        if (!ValidScale(scale)) return false;
        PointF p = new PointF(x, y);
        double tol = screenTolerance / scale;
        foreach (int idx in _selected)
        {
            LabelShape s = Shapes[idx];
            bool polygon = s.ShapeType == "polygon" && s.Points.Count >= 3;
            bool strip = s.ShapeType == "linestrip" && s.Points.Count >= 2;
            if (!polygon && !strip) continue;
            int edges = polygon ? s.Points.Count : s.Points.Count - 1;
            for (int i = 0; i < edges; i++)
            {
                double[] a = s.Points[i], b = s.Points[(i + 1) % s.Points.Count];
                PointF q = ClosestOnSegment(a, b, p, out double dist);
                if (dist > tol) continue;
                shapeIndex = idx;
                edgeIndex = i;
                point = q;
                return true;
            }
        }
        return false;
    }

    /// <summary>True when a selected polygon / linestrip has an edge within <paramref name="screenTolerance"/> of the point.</summary>
    public bool CanAddPointOnEdge(float x, float y, float scale, float screenTolerance = 12f)
        => TryFindEdge(x, y, scale, screenTolerance, out _, out _, out _);

    /// <summary>"Add point to edge": inserts a vertex on the nearest edge of a selected polygon / linestrip (one undo unit).</summary>
    public bool AddPointOnEdge(float x, float y, float scale, float screenTolerance = 12f)
    {
        if (!TryFindEdge(x, y, scale, screenTolerance, out int idx, out int edge, out PointF q)) return false;
        LabelShape before = DocumentEditor.Clone(Shapes[idx]);
        if (before.ShapeType == "polygon")
        {
            _editor.InsertVertex(idx, edge, new double[] { q.X, q.Y });
        }
        else
        {
            LabelShape changed = DocumentEditor.Clone(before);
            changed.Points.Insert(edge + 1, new double[] { q.X, q.Y });
            _editor.ReplaceMany(new Dictionary<int, LabelShape> { [idx] = changed }, "Add vertex");
        }
        Holes?.Carry(before, Shapes[idx], null);
        return true;
    }

    /// <summary>Esc. Reverts an active drag, drops a creation draft, else clears the selection. Never records history.</summary>
    public bool Cancel()
    {
        if (_drag != DragKind.None) { ResetDrag(); return true; }
        if (_draftPoints.Count > 0) { _draftPoints.Clear(); return true; }
        if (_selected.Count > 0) { _selected.Clear(); ClearVertex(); return true; }
        return false;
    }

    /// <summary>Enter. Commits a polygon draft (3+ vertices) or a linestrip draft (2+ vertices) as ONE undo unit.</summary>
    public bool Finish()
    {
        if (Tool == CanvasTool.LineStrip && _draftPoints.Count >= 2)
        {
            PointF[] strip = _draftPoints.ToArray();
            _draftPoints.Clear();
            return AddNew(NewShape("linestrip", strip), "Draw linestrip");
        }
        if (Tool != CanvasTool.Polygon || _draftPoints.Count < 3) return false;
        PointF[] pts = _draftPoints.ToArray();
        _draftPoints.Clear();
        return AddNew(NewShape("polygon", pts), "Draw polygon");
    }

    private void ClearVertex()
    {
        _selectedVertexShape = -1;
        _selectedVertex = -1;
    }

    /// <summary>Backspace while drawing: removes the last draft vertex / rotation click.</summary>
    public bool RemoveDraftVertex()
    {
        if (_draftPoints.Count == 0) return false;
        _draftPoints.RemoveAt(_draftPoints.Count - 1);
        return true;
    }

    // ---------- selection / commands ----------

    public void SelectAll()
    {
        _selected.Clear();
        ClearVertex();
        for (int i = 0; i < Shapes.Count; i++)
            if (IsEditable(Shapes[i]) && Visible(i)) _selected.Add(i);
    }

    public void ClearSelection()
    {
        _selected.Clear();
        ClearVertex();
    }

    public void Select(int index, bool add)
    {
        if (index < 0 || index >= Shapes.Count || !IsEditable(Shapes[index])) return;
        if (!add) _selected.Clear();
        ClearVertex();
        if (!_selected.Contains(index)) { _selected.Add(index); _selected.Sort(); }
    }

    /// <summary>Right click on the canvas: selects the topmost shape under the point unless it is already selected. Never records history.</summary>
    public bool SelectForContext(float x, float y, float scale)
    {
        if (!ValidScale(scale) || IsDragging || HasDraft) return false;
        PointF p = new PointF(x, y);
        for (int i = Shapes.Count - 1; i >= 0; i--)
        {
            if (!IsEditable(Shapes[i]) || !Visible(i)) continue;
            if (ShapeHitTester.HitTest(Shapes[i], p, scale, HandleScreenSize).Kind == ShapeHitKind.None) continue;
            if (_selected.Contains(i)) return false;
            _selected.Clear();
            _selected.Add(i);
            ClearVertex();
            return true;
        }
        return false;
    }

    /// <summary>Ctrl+arrows: selects the nearest visible shape (by centre) above / below / left / right of the last selected one.</summary>
    public bool SelectInDirection(SelectDirection direction)
    {
        if (_selected.Count == 0)
        {
            int first = Enumerable.Range(0, Shapes.Count).FirstOrDefault(i => IsEditable(Shapes[i]) && Visible(i), -1);
            if (first < 0) return false;
            _selected.Add(first);
            ClearVertex();
            return true;
        }
        int target = ShapeGeometryOps.NearestInDirection(Shapes, _selected[_selected.Count - 1], direction,
            i => IsEditable(Shapes[i]) && Visible(i));
        if (target < 0) return false;
        _selected.Clear();
        _selected.Add(target);
        ClearVertex();
        return true;
    }

    /// <summary>Tab / Shift+Tab: select the next editable shape.</summary>
    public void CycleSelection(bool backwards)
    {
        List<int> all = Enumerable.Range(0, Shapes.Count).Where(i => IsEditable(Shapes[i]) && Visible(i)).ToList();
        if (all.Count == 0) return;
        int current = _selected.Count == 0 ? -1 : _selected[_selected.Count - 1];
        int pos = all.IndexOf(current);
        pos = pos < 0 ? (backwards ? all.Count - 1 : 0) : (pos + (backwards ? all.Count - 1 : 1)) % all.Count;
        _selected.Clear();
        ClearVertex();
        _selected.Add(all[pos]);
    }

    /// <summary>Call after Undo/Redo or any external document change: drops drags and stale selection.</summary>
    public void NotifyDocumentChanged()
    {
        ResetDrag();
        _draftPoints.Clear();
        ClearVertex();
        _selected.RemoveAll(i => i < 0 || i >= Shapes.Count || !IsEditable(Shapes[i]));
    }

    public bool Delete()
    {
        if (_selected.Count == 0) return false;
        List<int> indices = _selected.ToList();
        _selected.Clear();
        ClearVertex();
        _editor.DeleteMany(indices);
        return true;
    }

    public bool Nudge(float dx, float dy, bool shiftTen)
    {
        if (_selected.Count == 0) return false;
        float k = shiftTen ? 10f : 1f;
        var before = new Dictionary<int, LabelShape>();
        foreach (int i in _selected) before[i] = DocumentEditor.Clone(Shapes[i]);
        Dictionary<int, LabelShape> moved = MoveBy(before, dx * k, dy * k);
        return CommitReplace(before, moved, "Nudge", MoveMap);
    }

    public bool Copy()
    {
        if (_selected.Count == 0) return false;
        Clipboard.Copy(_selected.Select(i => Shapes[i]));
        return true;
    }

    public bool Paste()
    {
        if (!Clipboard.HasContent) return false;
        List<LabelShape> added = Clipboard.NextPaste(ImageWidth, ImageHeight, out PointF offset);
        return AddCopies(Clipboard.Items.ToList(), added, offset, "Paste");
    }

    public bool Duplicate()
    {
        if (_selected.Count == 0) return false;
        List<LabelShape> sources = _selected.Select(i => Shapes[i]).ToList();
        List<LabelShape> added = ShapeClipboard.Translate(sources, ShapeClipboard.DefaultOffset, ShapeClipboard.DefaultOffset,
            ImageWidth, ImageHeight, out PointF offset);
        return AddCopies(sources, added, offset, "Duplicate");
    }

    /// <summary>Removes one polygon vertex; a polygon keeps at least three. Returns false when refused.</summary>
    public bool DeleteVertex(int shapeIndex, int vertexIndex)
    {
        if (shapeIndex < 0 || shapeIndex >= Shapes.Count) return false;
        LabelShape s = Shapes[shapeIndex];
        if (!CanDeleteVertex(s, vertexIndex)) return false;
        LabelShape before = DocumentEditor.Clone(s);
        if (s.ShapeType == "polygon")
        {
            _editor.RemoveVertex(shapeIndex, vertexIndex);
        }
        else
        {
            LabelShape changed = DocumentEditor.Clone(s);
            changed.Points.RemoveAt(vertexIndex);
            _editor.ReplaceMany(new Dictionary<int, LabelShape> { [shapeIndex] = changed }, "Remove vertex");
        }
        Holes?.Carry(before, Shapes[shapeIndex], null);
        if (_selectedVertexShape == shapeIndex) ClearVertex();
        return true;
    }

    private static bool CanDeleteVertex(LabelShape s, int vertexIndex)
    {
        if (vertexIndex < 0 || vertexIndex >= s.Points.Count) return false;
        return (s.ShapeType == "polygon" && s.Points.Count > 3) || (s.ShapeType == "linestrip" && s.Points.Count > 2);
    }

    /// <summary>True when the vertex pressed last belongs to a polygon (3+) or linestrip (2+) that can lose it.</summary>
    public bool CanDeleteSelectedVertex =>
        _selectedVertexShape >= 0 && _selectedVertexShape < Shapes.Count && _selected.Contains(_selectedVertexShape)
        && CanDeleteVertex(Shapes[_selectedVertexShape], _selectedVertex);

    /// <summary>"Delete selected vertex" (Meta+H): removes the vertex pressed last, one undo unit.</summary>
    public bool DeleteSelectedVertex()
    {
        return CanDeleteSelectedVertex && DeleteVertex(_selectedVertexShape, _selectedVertex);
    }

    // ---------- merge / label ----------

    /// <summary>The shapes (with their union) that "Merge shapes" would combine, or false when the selection cannot be merged.</summary>
    private bool TryBuildMerge(out List<double[]> ring)
    {
        ring = new List<double[]>();
        if (_selected.Count < 2) return false;
        string? label = Shapes[_selected[0]].Label;
        foreach (int i in _selected)
        {
            if (!ShapeGeometryOps.IsClosedKind(Shapes[i]) || !string.Equals(Shapes[i].Label, label, StringComparison.Ordinal)) return false;
            // Segment AI holes are keyed by geometry: a union polygon has a new key, so merging would drop them silently.
            if (Holes != null && Holes.HasHoles(Shapes[i])) return false;
        }
        List<double[]> acc = ShapeGeometryOps.RingOf(Shapes[_selected[0]]);
        for (int k = 1; k < _selected.Count; k++)
        {
            if (!ShapeGeometryOps.TryUnion(acc, ShapeGeometryOps.RingOf(Shapes[_selected[k]]), out List<double[]> merged)) return false;
            acc = merged;
        }
        ring = acc;
        return true;
    }

    /// <summary>Two or more same-label closed shapes whose union is a single hole-free polygon.</summary>
    public bool CanMerge() => TryBuildMerge(out _);

    /// <summary>"Merge shapes": replaces the selected shapes with their union polygon as ONE undo unit; the first shape keeps its metadata.</summary>
    public bool Merge()
    {
        if (!TryBuildMerge(out List<double[]> ring)) return false;
        int first = _selected[0];
        LabelShape merged = DocumentEditor.Clone(Shapes[first]);
        merged.ShapeType = "polygon";
        merged.Direction = null;
        merged.Points = ring;
        _editor.ReplaceAndDelete(new Dictionary<int, LabelShape> { [first] = merged }, _selected.Skip(1).ToList(), "Merge shapes");
        _selected.Clear();
        _selected.Add(first);
        ClearVertex();
        return true;
    }

    /// <summary>True when at least one selected shape can be converted to <paramref name="targetType"/> (type-level check).</summary>
    public bool CanConvertSelection(string targetType)
    {
        foreach (int i in _selected)
            if (ShapeConverter.CanConvert(Shapes[i].ShapeType, targetType)) return true;
        return false;
    }

    /// <summary>Converts every selected shape to <paramref name="targetType"/> without touching the document; shapes that cannot be converted are counted as skipped.</summary>
    public ShapeConversionPlan PlanConversion(string targetType)
    {
        var plan = new ShapeConversionPlan(targetType);
        foreach (int i in _selected)
        {
            LabelShape source = Shapes[i];
            ShapeConversionResult result = ShapeConverter.Convert(source, targetType);
            if (result.Shape == null) { plan.Skipped++; continue; }
            plan.Replacements[i] = result.Shape;
            if (result.Lossy && result.WarningKey != null && !plan.WarningKeys.Contains(result.WarningKey))
                plan.WarningKeys.Add(result.WarningKey);
            // Holes live in the hole store, keyed by geometry; only a polygon outline can keep them.
            if (!string.Equals(result.Shape.ShapeType, "polygon", StringComparison.Ordinal)
                && Holes != null && Holes.HasHoles(source) && !plan.WarningKeys.Contains(ShapeConverter.LossyHolesKey))
                plan.WarningKeys.Add(ShapeConverter.LossyHolesKey);
        }
        return plan;
    }

    /// <summary>Writes a plan from <see cref="PlanConversion"/> as ONE undo unit (single ReplaceMany); the selection stays on the converted shapes.</summary>
    public bool ApplyConversion(ShapeConversionPlan plan)
    {
        if (plan == null) throw new ArgumentNullException(nameof(plan));
        if (plan.Replacements.Count == 0) return false;
        var originals = plan.Replacements.Keys.ToDictionary(i => i, i => Shapes[i]);
        _editor.ReplaceMany(plan.Replacements, "Convert shape");
        if (Holes != null)
            foreach (KeyValuePair<int, LabelShape> kv in plan.Replacements)
                if (string.Equals(kv.Value.ShapeType, "polygon", StringComparison.Ordinal))
                    Holes.Carry(originals[kv.Key], kv.Value, null);
        ClearVertex();
        return true;
    }

    /// <summary>Common label / group id / description of the selection (a field that differs between shapes is null / empty).</summary>
    public ShapeLabelInfo DescribeSelection()
    {
        var info = new ShapeLabelInfo();
        if (_selected.Count == 0) return info;
        LabelShape first = Shapes[_selected[0]];
        info.Label = first.Label;
        info.GroupId = first.GroupId;
        info.Description = first.Description ?? string.Empty;
        foreach (int i in _selected.Skip(1))
        {
            LabelShape s = Shapes[i];
            if (!string.Equals(s.Label, info.Label, StringComparison.Ordinal)) info.Label = null;
            if (s.GroupId != info.GroupId) info.GroupId = null;
            if (!string.Equals(s.Description ?? string.Empty, info.Description, StringComparison.Ordinal)) info.Description = string.Empty;
        }
        return info;
    }

    /// <summary>
    /// "Edit label": writes the dialog result to the selected shapes as ONE undo unit. The label is applied when non-empty; group id and
    /// description only when they differ from <paramref name="initial"/> (so a multi-selection keeps fields the user did not touch).
    /// </summary>
    public bool ApplyLabelInfo(ShapeLabelInfo initial, ShapeLabelInfo result)
    {
        if (_selected.Count == 0 || initial == null || result == null) return false;
        string label = (result.Label ?? string.Empty).Trim();
        bool groupTouched = result.GroupId != initial.GroupId;
        bool descTouched = !string.Equals(result.Description ?? string.Empty, initial.Description ?? string.Empty, StringComparison.Ordinal);
        var changed = new Dictionary<int, LabelShape>();
        foreach (int i in _selected)
        {
            LabelShape c = DocumentEditor.Clone(Shapes[i]);
            if (label.Length > 0) c.Label = label;
            if (groupTouched) c.GroupId = result.GroupId;
            if (descTouched) c.Description = string.IsNullOrEmpty(result.Description) ? null : result.Description;
            if (string.Equals(c.Label, Shapes[i].Label, StringComparison.Ordinal) && c.GroupId == Shapes[i].GroupId
                && string.Equals(c.Description, Shapes[i].Description, StringComparison.Ordinal)) continue;
            changed[i] = c;
        }
        if (changed.Count == 0) return false;
        _editor.ReplaceMany(changed, "Edit label");
        return true;
    }

    /// <summary>Removes the vertex of a selected polygon under the pointer (UI: e.g. Alt+click).</summary>
    public bool DeleteVertexAt(float x, float y, float scale)
    {
        if (!ValidScale(scale)) return false;
        foreach (int idx in _selected)
        {
            ShapeHit hit = ShapeHitTester.HitTest(Shapes[idx], new PointF(x, y), scale, HandleScreenSize);
            if (hit.Kind == ShapeHitKind.Vertex) return DeleteVertex(idx, hit.Index);
        }
        return false;
    }

    // ---------- select tool ----------

    private bool SelectDown(PointF p, float scale, EditModifiers mods)
    {
        bool ctrl = (mods & EditModifiers.Ctrl) != 0;
        if (!ctrl)
        {
            for (int k = _selected.Count - 1; k >= 0; k--)
            {
                int idx = _selected[k];
                ShapeHit hit = ShapeHitTester.HitTest(Shapes[idx], p, scale, HandleScreenSize);
                DragKind kind = hit.Kind == ShapeHitKind.Corner ? DragKind.Resize
                    : hit.Kind == ShapeHitKind.Rotate ? DragKind.Rotate
                    : hit.Kind == ShapeHitKind.Vertex ? DragKind.Vertex : DragKind.None;
                if (kind == DragKind.None) continue;
                BeginDrag(kind, p, new[] { idx });
                _handleShape = idx;
                _handleIndex = hit.Index;
                if (kind == DragKind.Vertex)
                {
                    _selectedVertexShape = idx;
                    _selectedVertex = hit.Index;
                }
                return true;
            }
        }

        int top = -1;
        for (int i = Shapes.Count - 1; i >= 0; i--)
        {
            if (!IsEditable(Shapes[i]) || !Visible(i)) continue;
            if (ShapeHitTester.HitTest(Shapes[i], p, scale, HandleScreenSize).Kind != ShapeHitKind.None) { top = i; break; }
        }
        if (top < 0)
        {
            // Empty space: a click deselects, a sweep selects every shape the rectangle touches (Ctrl adds to the selection).
            if (!ctrl) { _selected.Clear(); ClearVertex(); }
            _down = Clamp(p);
            _rectNow = _down;
            _drag = DragKind.Marquee;
            return true;
        }
        ClearVertex();
        if (ctrl)
        {
            if (!_selected.Remove(top)) { _selected.Add(top); _selected.Sort(); }
            return true;
        }
        if (!_selected.Contains(top)) { _selected.Clear(); _selected.Add(top); }
        BeginDrag(DragKind.Move, p, _selected.ToArray());
        return true;
    }

    /// <summary>Selects the editable visible shapes whose bounding box touches <paramref name="area"/>; a rectangle under 3 screen pixels is a plain click and changes nothing.</summary>
    private void SelectInRect(RectangleF area, float scale)
    {
        float min = 3f / scale;
        if (area.Width < min && area.Height < min) return;
        for (int i = 0; i < Shapes.Count; i++)
        {
            if (!IsEditable(Shapes[i]) || !Visible(i) || Shapes[i].Points.Count == 0) continue;
            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            foreach (double[] pt in Shapes[i].Points)
            {
                minX = Math.Min(minX, pt[0]); maxX = Math.Max(maxX, pt[0]);
                minY = Math.Min(minY, pt[1]); maxY = Math.Max(maxY, pt[1]);
            }
            if (Shapes[i].ShapeType == "circle" && Shapes[i].Points.Count >= 2)
            {
                double[] c = Shapes[i].Points[0], e = Shapes[i].Points[1];
                double r = Math.Sqrt((e[0] - c[0]) * (e[0] - c[0]) + (e[1] - c[1]) * (e[1] - c[1]));
                minX = c[0] - r; maxX = c[0] + r; minY = c[1] - r; maxY = c[1] + r;
            }
            if (maxX < area.Left || minX > area.Right || maxY < area.Top || minY > area.Bottom) continue;
            if (!_selected.Contains(i)) _selected.Add(i);
        }
        _selected.Sort();
    }

    private void BeginDrag(DragKind kind, PointF down, IEnumerable<int> indices)
    {
        _drag = kind;
        _down = down;
        _orig = indices.ToDictionary(i => i, i => DocumentEditor.Clone(Shapes[i]));
        _draft = new Dictionary<int, LabelShape>();
    }

    private void UpdateMove(PointF p) => _draft = MoveBy(_orig, p.X - _down.X, p.Y - _down.Y);

    /// <summary>Translates clones of the shapes by (dx, dy), pushed back so the group bounding box stays inside the image.</summary>
    private Dictionary<int, LabelShape> MoveBy(Dictionary<int, LabelShape> source, double dx, double dy)
    {
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (LabelShape s in source.Values)
            foreach (double[] pt in s.Points)
            {
                minX = Math.Min(minX, pt[0]); maxX = Math.Max(maxX, pt[0]);
                minY = Math.Min(minY, pt[1]); maxY = Math.Max(maxY, pt[1]);
            }
        var result = new Dictionary<int, LabelShape>();
        if (minX > maxX) return result;
        if (!AllowOutOfBounds)
        {
            dx = Math.Min(Math.Max(dx, -minX), ImageWidth - maxX);
            dy = Math.Min(Math.Max(dy, -minY), ImageHeight - maxY);
        }
        foreach (KeyValuePair<int, LabelShape> kv in source)
        {
            LabelShape c = DocumentEditor.Clone(kv.Value);
            foreach (double[] pt in c.Points) { pt[0] += dx; pt[1] += dy; }
            result[kv.Key] = c;
        }
        return result;
    }

    private void UpdateResize(PointF p)
    {
        LabelShape orig = _orig[_handleShape];
        if (!TryBoxOf(orig, out RotatedBoxGeometry box)) return;
        if (!box.TryResize(_handleIndex, _down, p, MinBoxSize, out RotatedBoxGeometry c) || !c.IsInside(new SizeF(ImageWidth, ImageHeight))) return;
        _draft[_handleShape] = FromBox(orig, c);
    }

    private void UpdateRotate(PointF p, bool snap)
    {
        LabelShape orig = _orig[_handleShape];
        if (orig.ShapeType != "oriented_rectangle" || !TryBoxOf(orig, out RotatedBoxGeometry box)) return;
        if (!box.TryRotate(_down, p, snap, out RotatedBoxGeometry c) || !c.IsInside(new SizeF(ImageWidth, ImageHeight))) return;
        _draft[_handleShape] = FromBox(orig, c);
    }

    private void UpdateVertex(PointF p)
    {
        LabelShape orig = _orig[_handleShape];
        if (_handleIndex < 0 || _handleIndex >= orig.Points.Count) return;
        LabelShape c = DocumentEditor.Clone(orig);
        PointF q = Clamp(p);
        if (orig.ShapeType == "circle" && _handleIndex == 0 && orig.Points.Count >= 2)
        {
            // The centre handle moves the whole circle (the edge point keeps its offset).
            double dx = q.X - orig.Points[0][0], dy = q.Y - orig.Points[0][1];
            foreach (double[] pt in c.Points) { pt[0] += dx; pt[1] += dy; }
        }
        else
        {
            c.Points[_handleIndex] = new double[] { q.X, q.Y };
            if ((orig.ShapeType == "circle" || orig.ShapeType == "line") && c.Points.Count >= 2
                && Dist(new PointF((float)c.Points[0][0], (float)c.Points[0][1]), new PointF((float)c.Points[1][0], (float)c.Points[1][1])) < MinBoxSize)
            {
                return; // a circle or line cannot be dragged down to zero size
            }
        }
        _draft[_handleShape] = c;
    }

    private bool CommitDrag()
    {
        DragKind kind = _drag;
        Dictionary<int, LabelShape> orig = _orig;
        Dictionary<int, LabelShape> draft = _draft;
        ResetDrag();
        string name = kind == DragKind.Move ? "Move" : kind == DragKind.Resize ? "Resize"
            : kind == DragKind.Rotate ? "Rotate" : "Edit vertex";
        Func<LabelShape, LabelShape, Func<PointF, PointF>?>? map = kind == DragKind.Move ? MoveMap
            : kind == DragKind.Rotate ? RotateMap : (Func<LabelShape, LabelShape, Func<PointF, PointF>?>?)null;
        return CommitReplace(orig, draft, name, map);
    }

    private bool CommitReplace(Dictionary<int, LabelShape> orig, Dictionary<int, LabelShape> draft, string name,
        Func<LabelShape, LabelShape, Func<PointF, PointF>?>? map)
    {
        var changed = new Dictionary<int, LabelShape>();
        foreach (KeyValuePair<int, LabelShape> kv in draft)
            if (orig.TryGetValue(kv.Key, out LabelShape? o) && !SamePoints(o, kv.Value)) changed[kv.Key] = kv.Value;
        if (changed.Count == 0) return false;
        _editor.ReplaceMany(changed, name);
        if (Holes != null)
            foreach (KeyValuePair<int, LabelShape> kv in changed)
                Holes.Carry(orig[kv.Key], kv.Value, map?.Invoke(orig[kv.Key], kv.Value));
        return true;
    }

    private static Func<PointF, PointF>? MoveMap(LabelShape from, LabelShape to)
    {
        float dx = (float)(to.Points[0][0] - from.Points[0][0]);
        float dy = (float)(to.Points[0][1] - from.Points[0][1]);
        return pt => new PointF(pt.X + dx, pt.Y + dy);
    }

    private static Func<PointF, PointF>? RotateMap(LabelShape from, LabelShape to)
    {
        if (!TryBoxOf(from, out RotatedBoxGeometry a) || !TryBoxOf(to, out RotatedBoxGeometry b)) return null;
        PointF center = a.Center;
        float delta = b.RotationDegrees - a.RotationDegrees;
        PointF shift = new PointF(b.Center.X - center.X, b.Center.Y - center.Y);
        return pt =>
        {
            PointF r = RotatedBoxGeometry.RotateVector(new PointF(pt.X - center.X, pt.Y - center.Y), delta);
            return new PointF(center.X + r.X + shift.X, center.Y + r.Y + shift.Y);
        };
    }

    // ---------- creation ----------

    private bool AddRotPoint(PointF p)
    {
        if (_draftPoints.Count == 1 && Dist(_draftPoints[0], p) < MinBoxSize) return false;
        if (_draftPoints.Count < 2) { _draftPoints.Add(p); return true; }
        if (!TryBuildRotBox(_draftPoints[0], _draftPoints[1], p, out PointF[] pts)) return false;
        _draftPoints.Clear();
        return AddNew(NewShape("oriented_rectangle", pts), "Draw rotated box");
    }

    private bool AddPolygonPoint(PointF p, float scale)
    {
        if (_draftPoints.Count >= 3 && Dist(_draftPoints[0], p) * scale <= CloseScreenTolerance) return Finish();
        if (_draftPoints.Count > 0 && Dist(_draftPoints[_draftPoints.Count - 1], p) * scale < 1f) return false;
        _draftPoints.Add(p);
        return true;
    }

    /// <summary>Circle (centre, edge point) and line (two end points): first click starts the draft, second click commits.</summary>
    private bool AddTwoClickShape(PointF p, string type, string name)
    {
        if (_draftPoints.Count == 0) { _draftPoints.Add(p); return true; }
        if (Dist(_draftPoints[0], p) < MinBoxSize) return false;
        PointF first = _draftPoints[0];
        _draftPoints.Clear();
        return AddNew(NewShape(type, new[] { first, p }), name);
    }

    private bool AddLineStripPoint(PointF p, float scale)
    {
        if (_draftPoints.Count > 0 && Dist(_draftPoints[_draftPoints.Count - 1], p) * scale < 1f) return false;
        _draftPoints.Add(p);
        return true;
    }

    /// <summary>
    /// Edge p0-p1 plus a side point p2 gives the box width. The result is normalised to TL,TR,BR,BL with a
    /// positive cross product (flipping the edge when p2 is on the other side), so TryCreate accepts both sides.
    /// </summary>
    private bool TryBuildRotBox(PointF p0, PointF p1, PointF p2, out PointF[] points)
    {
        points = Array.Empty<PointF>();
        float ex = p1.X - p0.X, ey = p1.Y - p0.Y;
        float len = (float)Math.Sqrt(ex * ex + ey * ey);
        if (len < MinBoxSize) return false;
        float ux = ex / len, uy = ey / len;
        float vx = -uy, vy = ux; // cross(u, v) = 1 > 0
        float h = (p2.X - p0.X) * vx + (p2.Y - p0.Y) * vy;
        if (Math.Abs(h) < MinBoxSize) return false;
        PointF tl, tr;
        float height = Math.Abs(h);
        if (h > 0f) { tl = p0; tr = p1; }
        else { tl = p1; tr = p0; ux = -ux; uy = -uy; vx = -vx; vy = -vy; }
        PointF bl = new PointF(tl.X + vx * height, tl.Y + vy * height);
        PointF br = new PointF(tr.X + vx * height, tr.Y + vy * height);
        if (!RotatedBoxGeometry.TryCreate(new[] { tl, tr, br, bl }, out RotatedBoxGeometry box)) return false;
        if (!box.IsInside(new SizeF(ImageWidth, ImageHeight))) return false;
        points = box.ToPoints();
        return true;
    }

    private bool AddNew(LabelShape shape, string name)
    {
        int index = Shapes.Count;
        _editor.Add(new[] { shape }, new[] { new ShapeProvenance() }, name);
        _selected.Clear();
        ClearVertex();
        _selected.Add(index);
        _createdIndex = index;
        return true;
    }

    private bool AddCopies(List<LabelShape> sources, List<LabelShape> added, PointF offset, string name)
    {
        if (added.Count == 0) return false;
        int first = Shapes.Count;
        _editor.Add(added, added.Select(_ => new ShapeProvenance()).ToList(), name);
        if (Holes != null)
            for (int i = 0; i < added.Count && i < sources.Count; i++)
                Holes.Carry(sources[i], added[i], pt => new PointF(pt.X + offset.X, pt.Y + offset.Y));
        _selected.Clear();
        for (int i = 0; i < added.Count; i++) _selected.Add(first + i);
        return true;
    }

    private LabelShape NewRect(RectangleF r)
    {
        return NewShape("rectangle", new[] { new PointF(r.Left, r.Top), new PointF(r.Right, r.Bottom) });
    }

    private LabelShape NewShape(string type, IEnumerable<PointF> pts)
    {
        return new LabelShape
        {
            Label = ActiveLabel,
            ShapeType = type,
            Points = pts.Select(p => new double[] { p.X, p.Y }).ToList()
        };
    }

    // ---------- helpers ----------

    private void ResetDrag()
    {
        _drag = DragKind.None;
        _orig = new Dictionary<int, LabelShape>();
        _draft = new Dictionary<int, LabelShape>();
        _handleShape = -1;
        _handleIndex = -1;
    }

    private static bool ValidScale(float scale) => scale > 0f && !float.IsNaN(scale) && !float.IsInfinity(scale);

    private PointF Clamp(PointF p) => AllowOutOfBounds
        ? p
        : new PointF(Math.Min(Math.Max(p.X, 0f), ImageWidth), Math.Min(Math.Max(p.Y, 0f), ImageHeight));

    private static float Dist(PointF a, PointF b) =>
        (float)Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    private static RectangleF RectOf(PointF a, PointF b) =>
        new RectangleF(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));

    public static bool IsEditable(LabelShape s)
    {
        if (s == null || s.Points == null) return false;
        switch (s.ShapeType)
        {
            case "rectangle": return s.Points.Count >= 2;
            case "oriented_rectangle": return s.Points.Count == 4;
            case "polygon": return s.Points.Count >= 3;
            case "circle": return s.Points.Count == 2;
            case "point": return s.Points.Count == 1;
            case "line": return s.Points.Count == 2;
            case "linestrip": return s.Points.Count >= 2;
            default: return false;
        }
    }

    private static bool TryBoxOf(LabelShape s, out RotatedBoxGeometry box)
    {
        box = default;
        if (s.ShapeType == "rectangle" && s.Points.Count >= 2)
        {
            float l = (float)Math.Min(s.Points[0][0], s.Points[1][0]), r = (float)Math.Max(s.Points[0][0], s.Points[1][0]);
            float t = (float)Math.Min(s.Points[0][1], s.Points[1][1]), b = (float)Math.Max(s.Points[0][1], s.Points[1][1]);
            return RotatedBoxGeometry.TryCreate(new[] { new PointF(l, t), new PointF(r, t), new PointF(r, b), new PointF(l, b) }, out box);
        }
        if (s.ShapeType == "oriented_rectangle" && s.Points.Count == 4)
            return RotatedBoxGeometry.TryCreate(s.Points.Select(p => new PointF((float)p[0], (float)p[1])).ToArray(), out box);
        return false;
    }

    private LabelShape FromBox(LabelShape template, RotatedBoxGeometry box)
    {
        LabelShape c = DocumentEditor.Clone(template);
        PointF[] pts = box.ToPoints().Select(Clamp).ToArray();
        if (template.ShapeType == "rectangle")
        {
            c.Points = new List<double[]>
            {
                new double[] { pts.Min(p => p.X), pts.Min(p => p.Y) },
                new double[] { pts.Max(p => p.X), pts.Max(p => p.Y) }
            };
        }
        else c.Points = pts.Select(p => new double[] { p.X, p.Y }).ToList();
        return c;
    }

    private static bool SamePoints(LabelShape a, LabelShape b)
    {
        if (a.Points.Count != b.Points.Count) return false;
        for (int i = 0; i < a.Points.Count; i++)
            if (a.Points[i][0] != b.Points[i][0] || a.Points[i][1] != b.Points[i][1]) return false;
        return true;
    }

    private static PointF ClosestOnSegment(double[] a, double[] b, PointF p, out double distance)
    {
        double abx = b[0] - a[0], aby = b[1] - a[1];
        double len2 = abx * abx + aby * aby;
        double t = len2 <= 0 ? 0 : ((p.X - a[0]) * abx + (p.Y - a[1]) * aby) / len2;
        t = Math.Min(Math.Max(t, 0), 1);
        double qx = a[0] + abx * t, qy = a[1] + aby * t;
        distance = Math.Sqrt((p.X - qx) * (p.X - qx) + (p.Y - qy) * (p.Y - qy));
        return new PointF((float)qx, (float)qy);
    }
}
