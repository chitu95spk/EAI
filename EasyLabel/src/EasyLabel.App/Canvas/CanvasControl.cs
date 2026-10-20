using EasyLabel.App.Canvas.Tools;
using EasyLabel.Core.Commands;
using EasyLabel.Core.Model;

namespace EasyLabel.App.Canvas;

public sealed class CanvasControl : Control
{
    private const double MinimumScale = 0.01;
    private const double MaximumScale = 64.0;
    private const float MinimumShapeSize = 1f;
    private Bitmap? _image;
    private LabelMeDocument? _document;
    private EasyLabelSidecar _sidecar = new EasyLabelSidecar();
    private readonly ShapeRenderer _shapeRenderer = new ShapeRenderer();
    private readonly EditController _editController = new EditController();
    private readonly ContextMenuStrip _shapeMenu = new ContextMenuStrip();
    private ViewTransform _transform = new ViewTransform(1.0, 0.0, 0.0);
    private CanvasTool _tool = CanvasTool.Select;
    private bool _fitPending, _spacePressed, _isPanning, _isDrawing, _isEditing;
    private Point _lastPanPoint;
    private PointF _drawStartImage, _lastEditImage, _dragStartImage, _polygonCursorImage, _orientedFirst, _orientedSecond;
    private LabelShape _previewShape = new LabelShape();
    private LabelShape? _dragStartShape;
    private EditHitTestResult _editHit = EditHitTestResult.None;
    private int _selectedVertexIndex = -1, _orientedStage;
    private readonly List<PointF> _polygonPoints = new List<PointF>();
    private LabelShape _copiedShape = new LabelShape();
    private ShapeProvenance _copiedProvenance = new ShapeProvenance();
    private bool _hasCopiedShape;

    public CanvasControl()
    {
        DoubleBuffered = true; TabStop = true; BackColor = Color.FromArgb(32, 34, 38);
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BuildContextMenu();
    }

    public ViewTransform Transform { get { return _transform; } }
    public string? ImagePath { get; private set; }
    public string ActiveLabel { get; set; } = string.Empty;
    public int SelectedShapeIndex { get; private set; } = -1;
    public DocumentEditor Editor { get; private set; } = new DocumentEditor(new LabelMeDocument(), new EasyLabelSidecar());
    public Func<string?, string?>? RequestLabel { get; set; }
    public Func<string?, string?>? RequestEditedLabel { get; set; }
    public event EventHandler? DocumentChanged;
    public event EventHandler? SelectionChanged;

    public CanvasTool Tool
    {
        get { return _tool; }
        set { CancelGesture(); _tool = value; Cursor = IsEditTool(value) ? Cursors.Default : Cursors.Cross; }
    }

    public void SetImage(Bitmap? image, string? imagePath)
    {
        Bitmap? oldImage = _image; _image = image; ImagePath = imagePath; _fitPending = image != null;
        if (oldImage != null) oldImage.Dispose(); Invalidate();
    }

    public void SetDocument(LabelMeDocument? document) { SetDocument(document ?? new LabelMeDocument(), new EasyLabelSidecar()); }

    public void SetDocument(LabelMeDocument document, EasyLabelSidecar sidecar)
    {
        if (document == null) throw new ArgumentNullException(nameof(document));
        if (sidecar == null) throw new ArgumentNullException(nameof(sidecar));
        CancelGesture(); _document = document; _sidecar = sidecar; Editor = new DocumentEditor(document, sidecar); SetSelectedShapeIndex(-1); Invalidate();
    }

    public void SetSelectedShapeIndex(int index)
    {
        int normalized = _document == null || index < 0 || index >= _document.Shapes.Count ? -1 : index;
        if (SelectedShapeIndex == normalized) return;
        SelectedShapeIndex = normalized; _selectedVertexIndex = -1; SelectionChanged?.Invoke(this, EventArgs.Empty); Invalidate();
    }

    public void CancelGesture()
    {
        _isDrawing = false; _isEditing = false; _isPanning = false; _spacePressed = false; _editHit = EditHitTestResult.None; _selectedVertexIndex = -1;
        _polygonPoints.Clear(); _orientedStage = 0; _dragStartShape = null; Capture = false; Cursor = IsEditTool(_tool) ? Cursors.Default : Cursors.Cross; Invalidate();
    }

    public void Undo() { CancelGesture(); if (Editor.History.Undo()) HistoryChanged(); }
    public void Redo() { CancelGesture(); if (Editor.History.Redo()) HistoryChanged(); }

    public void ChangeSelectedClass(string label)
    {
        if (string.IsNullOrWhiteSpace(label) || _document == null || SelectedShapeIndex < 0) return;
        CancelGesture(); Editor.ChangeClass(SelectedShapeIndex, label.Trim()); OnDocumentChanged(); Invalidate();
    }

    public void EditSelectedLabel()
    {
        if (_document == null || SelectedShapeIndex < 0 || SelectedShapeIndex >= _document.Shapes.Count) return;
        string? label = RequestEditedLabel == null ? null : RequestEditedLabel(_document.Shapes[SelectedShapeIndex].Label);
        if (!string.IsNullOrWhiteSpace(label)) ChangeSelectedClass(label);
    }

    public void CopySelected()
    {
        if (_document == null || SelectedShapeIndex < 0 || SelectedShapeIndex >= _document.Shapes.Count || IsDraftActive()) return;
        _copiedShape = DocumentEditor.Clone(_document.Shapes[SelectedShapeIndex]);
        _copiedProvenance = SelectedShapeIndex < _sidecar.Shapes.Count ? DocumentEditor.Clone(_sidecar.Shapes[SelectedShapeIndex]) : new ShapeProvenance(); _hasCopiedShape = true;
    }

    public void Paste()
    {
        if (_document == null || !_hasCopiedShape) return;
        CancelGesture(); Editor.Add(new[] { _copiedShape }, new[] { _copiedProvenance }, "Paste shape"); SetSelectedShapeIndex(_document.Shapes.Count - 1); OnDocumentChanged();
    }

    public void DuplicateSelected()
    {
        if (_document == null || SelectedShapeIndex < 0 || SelectedShapeIndex >= _document.Shapes.Count) return;
        CancelGesture(); Editor.Duplicate(SelectedShapeIndex); SetSelectedShapeIndex(_document.Shapes.Count - 1); OnDocumentChanged();
    }

    public void AddVertexToSelectedPolygon()
    {
        if (!TryGetSelectedPolygon(out LabelShape shape)) return;
        int after = _selectedVertexIndex >= 0 ? _selectedVertexIndex : 0, next = (after + 1) % shape.Points.Count;
        double[] a = shape.Points[after], b = shape.Points[next];
        Editor.InsertVertex(SelectedShapeIndex, after, new[] { (a[0] + b[0]) / 2.0, (a[1] + b[1]) / 2.0 }); _selectedVertexIndex = after + 1; OnDocumentChanged(); Invalidate();
    }

    public void RemoveSelectedPolygonVertex()
    {
        if (!TryGetSelectedPolygon(out LabelShape shape) || shape.Points.Count <= 3 || _selectedVertexIndex < 0 || _selectedVertexIndex >= shape.Points.Count) return;
        Editor.RemoveVertex(SelectedShapeIndex, _selectedVertexIndex); _selectedVertexIndex = -1; OnDocumentChanged(); Invalidate();
    }

    public bool DeleteSelectedShape()
    {
        if (_document == null || SelectedShapeIndex < 0 || SelectedShapeIndex >= _document.Shapes.Count) return false;
        CancelGesture(); Editor.Delete(SelectedShapeIndex); SetSelectedShapeIndex(-1); OnDocumentChanged(); return true;
    }

    public void ApplyDetections(IEnumerable<LabelShape> shapes, IEnumerable<ShapeProvenance> provenance) { CancelGesture(); Editor.Add(shapes, provenance, "Apply detections"); HistoryChanged(); }

    public void FitToWindow()
    {
        if (_image == null || ClientSize.Width <= 0 || ClientSize.Height <= 0) _transform = new ViewTransform(1.0, 0.0, 0.0);
        else
        {
            double scale = Math.Clamp(Math.Min(ClientSize.Width / (double)_image.Width, ClientSize.Height / (double)_image.Height), MinimumScale, MaximumScale);
            _transform = new ViewTransform(scale, (ClientSize.Width - _image.Width * scale) / 2.0, (ClientSize.Height - _image.Height * scale) / 2.0);
        }
        _fitPending = false; Invalidate();
    }
    public void ZoomInAtCenter() { ZoomAtCenter(1.2); }
    public void ZoomOutAtCenter() { ZoomAtCenter(1.0 / 1.2); }

    protected override void Dispose(bool disposing) { if (disposing) { _image?.Dispose(); _shapeMenu.Dispose(); } base.Dispose(disposing); }
    protected override void OnResize(EventArgs e) { base.OnResize(e); if (_fitPending) FitToWindow(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e); e.Graphics.Clear(BackColor); e.Graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor; e.Graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
        if (_image == null) { DrawEmptyState(e.Graphics); return; }
        if (_fitPending) FitToWindow();
        RectangleF target = new RectangleF((float)_transform.OffsetX, (float)_transform.OffsetY, (float)(_image.Width * _transform.Scale), (float)(_image.Height * _transform.Scale)); e.Graphics.DrawImage(_image, target);
        LabelMeDocument renderDocument = _document ?? new LabelMeDocument();
        if (_isDrawing || _isEditing || _polygonPoints.Count > 0 || _orientedStage > 0)
            renderDocument = new LabelMeDocument { Shapes = _isEditing ? renderDocument.Shapes.Select((shape, index) => index == SelectedShapeIndex ? _previewShape : shape).ToList() : renderDocument.Shapes.Concat(new[] { BuildDraftShapeForRender() }).ToList() };
        _shapeRenderer.Render(e.Graphics, renderDocument, _transform, SelectedShapeIndex); DrawImageBorder(e.Graphics, target);
    }

    protected override void OnMouseWheel(MouseEventArgs e) { base.OnMouseWheel(e); if (_image != null && e.Delta != 0) ZoomAt(e.Location, e.Delta > 0 ? 1.2 : 1.0 / 1.2); }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e); Focus(); if (_image == null || _document == null) return;
        if (e.Button == MouseButtons.Middle || (e.Button == MouseButtons.Left && _spacePressed)) { _isPanning = true; Capture = true; _lastPanPoint = e.Location; Cursor = Cursors.SizeAll; return; }
        PointF imagePoint = _transform.ToImage(e.X, e.Y);
        if (e.Button == MouseButtons.Right)
        {
            if (IsDraftActive()) { CancelGesture(); return; }
            SelectHit(e.Location); if (SelectedShapeIndex >= 0) _shapeMenu.Show(this, e.Location); return;
        }
        if (e.Button != MouseButtons.Left) return;
        if (_tool == CanvasTool.Rectangle) { _isDrawing = true; _drawStartImage = imagePoint; _previewShape = CreateRectanglePreview(imagePoint, imagePoint); Capture = true; Invalidate(); return; }
        if (_tool == CanvasTool.Polygon) { AddPolygonPoint(imagePoint); return; }
        if (_tool == CanvasTool.Rotation) { AddOrientedRectanglePoint(imagePoint); return; }
        SelectHit(e.Location);
        if (_editHit.Kind != EditHitKind.None && SelectedShapeIndex >= 0)
        {
            _isEditing = true; _lastEditImage = imagePoint; _dragStartImage = imagePoint; _dragStartShape = DocumentEditor.Clone(_document.Shapes[SelectedShapeIndex]); _previewShape = DocumentEditor.Clone(_dragStartShape); Capture = true;
        }
        Invalidate();
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        if (e.Button == MouseButtons.Left && _tool == CanvasTool.Polygon && _polygonPoints.Count > 0) FinishPolygon();
        else if (e.Button == MouseButtons.Left && !IsDraftActive() && _image != null) FitToWindow();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_isPanning) { _transform = new ViewTransform(_transform.Scale, _transform.OffsetX + e.X - _lastPanPoint.X, _transform.OffsetY + e.Y - _lastPanPoint.Y); _lastPanPoint = e.Location; Invalidate(); return; }
        if (_image == null || _document == null) return; PointF imagePoint = _transform.ToImage(e.X, e.Y);
        if (_isDrawing) { _previewShape = CreateRectanglePreview(_drawStartImage, imagePoint); Invalidate(); return; }
        if (_polygonPoints.Count > 0 || _orientedStage > 0) { _polygonCursorImage = imagePoint; Invalidate(); return; }
        if (_isEditing && SelectedShapeIndex >= 0 && _dragStartShape != null) { ApplyEdit(_previewShape, imagePoint); Invalidate(); }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (_isPanning && (e.Button == MouseButtons.Middle || e.Button == MouseButtons.Left)) { _isPanning = false; Capture = false; Cursor = IsEditTool(_tool) ? Cursors.Default : Cursors.Cross; return; }
        if (e.Button != MouseButtons.Left) return;
        if (_isDrawing) { _isDrawing = false; Capture = false; CommitNewShape(_previewShape, "Draw rectangle"); return; }
        if (_isEditing)
        {
            _isEditing = false; Capture = false; _editHit = EditHitTestResult.None;
            if (SelectedShapeIndex >= 0 && IsFiniteNondegenerate(_previewShape)) { Editor.Replace(SelectedShapeIndex, _previewShape, "Edit shape"); OnDocumentChanged(); }
            _dragStartShape = null; Invalidate();
        }
    }

    protected override void OnKeyDown(KeyEventArgs e) { base.OnKeyDown(e); if (e.KeyCode == Keys.Space) { _spacePressed = true; Cursor = Cursors.SizeAll; } }
    protected override void OnKeyUp(KeyEventArgs e) { base.OnKeyUp(e); if (e.KeyCode == Keys.Space) { _spacePressed = false; if (!_isPanning) Cursor = IsEditTool(_tool) ? Cursors.Default : Cursors.Cross; } }
    protected override void OnMouseCaptureChanged(EventArgs e) { base.OnMouseCaptureChanged(e); if (!Capture && (_isDrawing || _isEditing || _isPanning)) CancelGesture(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); if (_isDrawing || _isEditing) CancelGesture(); }

    private void BuildContextMenu()
    {
        AddMenuItem("Edit label", delegate { EditSelectedLabel(); }); AddMenuItem("Duplicate", delegate { DuplicateSelected(); }); AddMenuItem("Copy", delegate { CopySelected(); }); AddMenuItem("Paste", delegate { Paste(); });
        _shapeMenu.Items.Add(new ToolStripSeparator()); AddMenuItem("Add vertex", delegate { AddVertexToSelectedPolygon(); }); AddMenuItem("Remove vertex", delegate { RemoveSelectedPolygonVertex(); });
        _shapeMenu.Items.Add(new ToolStripSeparator()); AddMenuItem("Delete", delegate { DeleteSelectedShape(); });
    }
    private void AddMenuItem(string text, Action action) { ToolStripMenuItem item = new ToolStripMenuItem(text); item.Click += delegate { action(); }; _shapeMenu.Items.Add(item); }

    private void AddPolygonPoint(PointF point)
    {
        if (_polygonPoints.Count > 0 && Distance(_polygonPoints[_polygonPoints.Count - 1], point) < MinimumShapeSize) return;
        _polygonPoints.Add(point); _polygonCursorImage = point; _previewShape = BuildDraftShapeForRender(); Invalidate();
    }

    private void FinishPolygon()
    {
        if (_polygonPoints.Count < 3 || !HasThreeDistinctPoints(_polygonPoints)) { CancelGesture(); return; }
        LabelShape shape = new LabelShape { ShapeType = "polygon", Points = _polygonPoints.Select(point => new[] { (double)point.X, (double)point.Y }).ToList() };
        _polygonPoints.Clear(); CommitNewShape(shape, "Draw polygon");
    }

    private void AddOrientedRectanglePoint(PointF point)
    {
        if (_orientedStage == 0) { _orientedFirst = point; _polygonCursorImage = point; _orientedStage = 1; }
        else if (_orientedStage == 1) { if (Distance(_orientedFirst, point) < MinimumShapeSize) return; _orientedSecond = point; _polygonCursorImage = point; _orientedStage = 2; }
        else { LabelShape shape = CreateOrientedRectangle(_orientedFirst, _orientedSecond, point); _orientedStage = 0; if (IsFiniteNondegenerate(shape)) CommitNewShape(shape, "Draw oriented rectangle"); else CancelGesture(); }
        Invalidate();
    }

    private LabelShape BuildDraftShapeForRender()
    {
        if (_polygonPoints.Count > 0) return new LabelShape { Label = ActiveLabel, ShapeType = "polygon", Points = _polygonPoints.Concat(new[] { _polygonCursorImage }).Select(point => new[] { (double)point.X, (double)point.Y }).ToList() };
        if (_orientedStage == 1) return CreateRectanglePreview(_orientedFirst, _polygonCursorImage);
        if (_orientedStage == 2) return CreateOrientedRectangle(_orientedFirst, _orientedSecond, _polygonCursorImage);
        return _previewShape;
    }

    private void CommitNewShape(LabelShape shape, string name)
    {
        if (_document == null || !IsFiniteNondegenerate(shape)) { Invalidate(); return; }
        string? label = ResolveNewShapeLabel(); if (string.IsNullOrWhiteSpace(label)) { Invalidate(); return; }
        shape.Label = label.Trim(); Editor.Add(new[] { shape }, new[] { new ShapeProvenance() }, name); SetSelectedShapeIndex(_document.Shapes.Count - 1); OnDocumentChanged(); Invalidate();
    }
    private string? ResolveNewShapeLabel() { return !string.IsNullOrWhiteSpace(ActiveLabel) ? ActiveLabel : RequestLabel == null ? null : RequestLabel(null); }

    private void SelectHit(Point screenPoint)
    {
        if (_document == null) return;
        _editHit = _editController.HitTest(_document, _transform, screenPoint, SelectedShapeIndex);
        if (_editHit.Kind == EditHitKind.None) _editHit = _editController.HitTest(_document, _transform, screenPoint, -1);
        SetSelectedShapeIndex(_editHit.ShapeIndex); _selectedVertexIndex = _editHit.Kind == EditHitKind.Vertex ? _editHit.VertexIndex : -1;
    }

    private void ApplyEdit(LabelShape shape, PointF current)
    {
        if (_dragStartShape == null) return;
        if (_editHit.Kind == EditHitKind.Rotate && EditController.IsOrientedRectangle(_dragStartShape)) { _previewShape = DocumentEditor.Clone(_dragStartShape); RotateFromDragStart(_previewShape, current); }
        else if (_editHit.Kind == EditHitKind.Vertex && _editHit.VertexIndex >= 0)
        {
            _previewShape = DocumentEditor.Clone(_dragStartShape);
            if (EditController.IsOrientedRectangle(_previewShape) && _previewShape.Points.Count >= 4) ResizeOrientedRectangleVertex(_previewShape, _editHit.VertexIndex, current);
            else if (_previewShape.ShapeType == "rectangle" && _previewShape.Points.Count >= 2) ResizeRectangleVertex(_previewShape, _editHit.VertexIndex, current);
            else if (_editHit.VertexIndex < _previewShape.Points.Count) _previewShape.Points[_editHit.VertexIndex] = new[] { (double)current.X, (double)current.Y };
        }
        else if (_editHit.Kind == EditHitKind.Move)
        {
            float dx = current.X - _lastEditImage.X, dy = current.Y - _lastEditImage.Y;
            foreach (double[] point in shape.Points) { point[0] += dx; point[1] += dy; }
        }
        _lastEditImage = current;
    }

    private void RotateFromDragStart(LabelShape shape, PointF current)
    {
        PointF center = GetCenter(shape); double start = Math.Atan2(_dragStartImage.Y - center.Y, _dragStartImage.X - center.X), currentAngle = Math.Atan2(current.Y - center.Y, current.X - center.X), delta = currentAngle - start, sin = Math.Sin(delta), cos = Math.Cos(delta);
        foreach (double[] point in shape.Points) { double x = point[0] - center.X, y = point[1] - center.Y; point[0] = center.X + x * cos - y * sin; point[1] = center.Y + x * sin + y * cos; }
        shape.Direction = (_dragStartShape!.Direction ?? GetOrientedAngle(_dragStartShape)) + delta;
    }

    private static void ResizeRectangleVertex(LabelShape shape, int index, PointF current)
    {
        PointF[] corners = EditController.GetRectangleImageCorners(shape); if (corners.Length != 4) return; PointF opposite = corners[(index + 2) % 4];
        shape.Points = new List<double[]> { new[] { (double)Math.Min(opposite.X, current.X), (double)Math.Min(opposite.Y, current.Y) }, new[] { (double)Math.Max(opposite.X, current.X), (double)Math.Max(opposite.Y, current.Y) } };
    }

    private static void ResizeOrientedRectangleVertex(LabelShape shape, int index, PointF current)
    {
        PointF[] corners = shape.Points.Take(4).Select(ToPoint).ToArray(); if (corners.Length != 4) return;
        int moving = index % 4, next = (moving + 1) % 4, previous = (moving + 3) % 4, opposite = (moving + 2) % 4; PointF fixedOpposite = corners[opposite];
        PointF axisA = Normalize(Subtract(corners[next], corners[moving])), axisB = Normalize(Subtract(corners[previous], corners[moving])), across = Subtract(fixedOpposite, current);
        PointF edgeA = Multiply(axisA, Dot(across, axisA)), edgeB = Multiply(axisB, Dot(across, axisB)); if (Length(edgeA) < MinimumShapeSize || Length(edgeB) < MinimumShapeSize) return;
        PointF[] projected = new PointF[4]; projected[moving] = current; projected[next] = Add(current, edgeA); projected[previous] = Add(current, edgeB); projected[opposite] = fixedOpposite;
        shape.Points = projected.Select(point => new[] { (double)point.X, (double)point.Y }).ToList(); shape.Direction = GetOrientedAngle(shape);
    }

    private static LabelShape CreateRectanglePreview(PointF first, PointF second) { return new LabelShape { ShapeType = "rectangle", Points = new List<double[]> { new[] { (double)first.X, (double)first.Y }, new[] { (double)second.X, (double)second.Y } } }; }
    private static LabelShape CreateOrientedRectangle(PointF first, PointF second, PointF third)
    {
        PointF edge = Subtract(second, first); float length = Length(edge); if (length < MinimumShapeSize) return new LabelShape { ShapeType = "oriented_rectangle" };
        PointF unit = Multiply(edge, 1f / length), normal = new PointF(-unit.Y, unit.X); float height = Dot(Subtract(third, first), normal); PointF fourth = Add(first, Multiply(normal, height)), thirdCorner = Add(second, Multiply(normal, height));
        return new LabelShape { ShapeType = "oriented_rectangle", Direction = Math.Atan2(unit.Y, unit.X), Points = new List<double[]> { new[] { (double)first.X, (double)first.Y }, new[] { (double)second.X, (double)second.Y }, new[] { (double)thirdCorner.X, (double)thirdCorner.Y }, new[] { (double)fourth.X, (double)fourth.Y } } };
    }

    private bool TryGetSelectedPolygon(out LabelShape shape)
    {
        shape = new LabelShape(); if (_document == null || SelectedShapeIndex < 0 || SelectedShapeIndex >= _document.Shapes.Count) return false;
        shape = _document.Shapes[SelectedShapeIndex]; return string.Equals(shape.ShapeType, "polygon", StringComparison.Ordinal) && shape.Points.Count >= 3;
    }
    private bool IsDraftActive() { return _isDrawing || _isEditing || _polygonPoints.Count > 0 || _orientedStage > 0; }
    private static bool IsEditTool(CanvasTool tool) { return tool == CanvasTool.Select || tool == CanvasTool.Edit; }
    private static bool IsFiniteNondegenerate(LabelShape shape)
    {
        if (shape.Points.Count == 0 || shape.Points.Any(point => point.Length < 2 || double.IsNaN(point[0]) || double.IsNaN(point[1]) || double.IsInfinity(point[0]) || double.IsInfinity(point[1]))) return false;
        if (shape.ShapeType == "rectangle") return shape.Points.Count >= 2 && Math.Abs(shape.Points[0][0] - shape.Points[1][0]) >= MinimumShapeSize && Math.Abs(shape.Points[0][1] - shape.Points[1][1]) >= MinimumShapeSize;
        if (EditController.IsOrientedRectangle(shape)) return shape.Points.Count >= 4 && Math.Abs(PolygonArea(shape.Points.Take(4))) >= MinimumShapeSize;
        return shape.ShapeType != "polygon" || (shape.Points.Count >= 3 && Math.Abs(PolygonArea(shape.Points)) >= MinimumShapeSize);
    }
    private static bool HasThreeDistinctPoints(IEnumerable<PointF> points) { List<PointF> distinct = new List<PointF>(); foreach (PointF point in points) if (!distinct.Any(existing => Distance(existing, point) < MinimumShapeSize)) distinct.Add(point); return distinct.Count >= 3; }
    private void HistoryChanged() { SetSelectedShapeIndex(-1); OnDocumentChanged(); Invalidate(); }
    private void ZoomAt(Point screen, double factor) { PointF image = _transform.ToImage(screen.X, screen.Y); double scale = Math.Clamp(_transform.Scale * factor, MinimumScale, MaximumScale); _transform = new ViewTransform(scale, screen.X - image.X * scale, screen.Y - image.Y * scale); _fitPending = false; Invalidate(); }
    private void ZoomAtCenter(double factor) { if (_image != null && ClientSize.Width > 0 && ClientSize.Height > 0) ZoomAt(new Point(ClientSize.Width / 2, ClientSize.Height / 2), factor); }
    private void OnDocumentChanged() { DocumentChanged?.Invoke(this, EventArgs.Empty); }
    private void DrawEmptyState(Graphics graphics) { using (StringFormat format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center }) using (Brush brush = new SolidBrush(Color.FromArgb(180, 186, 196))) { graphics.DrawString("Select an image to preview.", Font, brush, ClientRectangle, format); } }
    private static void DrawImageBorder(Graphics graphics, RectangleF target) { using (Pen pen = new Pen(Color.FromArgb(95, 105, 120))) { graphics.DrawRectangle(pen, target.X, target.Y, target.Width, target.Height); } }
    private static double PolygonArea(IEnumerable<double[]> points) { double[][] values = points.ToArray(); double area = 0.0; for (int i = 0; i < values.Length; i++) { double[] a = values[i], b = values[(i + 1) % values.Length]; area += a[0] * b[1] - b[0] * a[1]; } return area / 2.0; }
    private static PointF GetCenter(LabelShape shape) { return new PointF((float)shape.Points.Average(point => point[0]), (float)shape.Points.Average(point => point[1])); }
    private static double GetOrientedAngle(LabelShape shape) { return Math.Atan2(shape.Points[1][1] - shape.Points[0][1], shape.Points[1][0] - shape.Points[0][0]); }
    private static PointF ToPoint(double[] point) { return new PointF((float)point[0], (float)point[1]); }
    private static PointF Add(PointF a, PointF b) { return new PointF(a.X + b.X, a.Y + b.Y); }
    private static PointF Subtract(PointF a, PointF b) { return new PointF(a.X - b.X, a.Y - b.Y); }
    private static PointF Multiply(PointF point, float factor) { return new PointF(point.X * factor, point.Y * factor); }
    private static float Dot(PointF a, PointF b) { return a.X * b.X + a.Y * b.Y; }
    private static float Length(PointF point) { return MathF.Sqrt(Dot(point, point)); }
    private static PointF Normalize(PointF point) { float length = Length(point); return length < 0.0001f ? PointF.Empty : Multiply(point, 1f / length); }
    private static float Distance(PointF a, PointF b) { return Length(Subtract(a, b)); }
}
