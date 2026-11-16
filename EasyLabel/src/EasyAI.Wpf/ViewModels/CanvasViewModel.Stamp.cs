using CommunityToolkit.Mvvm.ComponentModel;
using EasyLabel.Core.Commands;
using EasyLabel.Core.Editing;
using EasyLabel.Core.Model;
using EasyLabel.Core.Projects;

namespace EasyAI.Desktop.ViewModels;

// CounterAI labelling inside the main canvas: fixed-size stamps (left click = paste, right click = delete) and bulk replace by machine points.
public sealed partial class CanvasViewModel
{
    /// <summary>Stamped circle diameter / rectangle width in image pixels.</summary>
    [ObservableProperty]
    private double _stampWidth = 62;

    /// <summary>Stamped rectangle height in image pixels.</summary>
    [ObservableProperty]
    private double _stampHeight = 62;

    /// <summary>Count tool shape: true = circle, false = square / rectangle.</summary>
    [ObservableProperty]
    private bool _stampCircle = true;

    /// <summary>True for a CounterAI project (points CSV is kept beside each image).</summary>
    public bool IsStampKind => Kind == ProjectKind.CounterAi;

    /// <summary>True while the Count tool is the active tool: the size / shape / auto-label panel shows.</summary>
    public bool IsCountTool => Tool == CanvasTool.Count;

    partial void OnStampWidthChanged(double value) => ApplyStamp();

    partial void OnStampHeightChanged(double value) => ApplyStamp();

    partial void OnStampCircleChanged(bool value) => ApplyStamp();

    private void ApplyStamp()
    {
        if (Session == null) return;
        Session.StampMode = true;
        Session.StampCircle = StampCircle;
        Session.StampWidth = (float)Math.Max(1.0, StampWidth);
        Session.StampHeight = (float)Math.Max(1.0, StampHeight);
        RaiseRender();
    }

    /// <summary>Full path of the image on show (project root + relative path), or null.</summary>
    public string? CurrentImagePath
    {
        get
        {
            string? rel = CurrentRel;
            string? root = _segAi?.ProjectRoot;
            return rel == null || string.IsNullOrEmpty(root) ? null : System.IO.Path.GetFullPath(System.IO.Path.Combine(root, rel));
        }
    }

    /// <summary>Centres of the circles and rectangles of the current image, in image pixels.</summary>
    public IReadOnlyList<(int X, int Y)> StampCentres()
    {
        List<(int X, int Y)> centres = new List<(int X, int Y)>();
        if (_edit == null) return centres;
        foreach (LabelShape s in _edit.Document.Shapes)
        {
            if (s.Points == null || s.Points.Count < 2) continue;
            if (s.ShapeType == "circle")
            {
                centres.Add(((int)Math.Round(s.Points[0][0]), (int)Math.Round(s.Points[0][1])));
            }
            else if (s.ShapeType == "rectangle")
            {
                centres.Add(((int)Math.Round((s.Points[0][0] + s.Points[1][0]) / 2.0), (int)Math.Round((s.Points[0][1] + s.Points[1][1]) / 2.0)));
            }
        }

        return centres;
    }

    /// <summary>
    /// Opening an image of a CounterAI project that has no stamps yet but a <c>*_points.csv</c> beside it shows the CSV points as stamps
    /// (the document stays "saved": the CSV is the source of truth, nothing is rewritten).
    /// </summary>
    private void LoadStampsFromCsv(string? root, string rel)
    {
        if (!IsStampKind || string.IsNullOrEmpty(root) || _edit == null || _editor == null || Session == null) return;
        foreach (LabelShape s in _edit.Document.Shapes)
        {
            if (s.ShapeType == "circle" || s.ShapeType == "rectangle") return;
        }

        try
        {
            string image = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, rel));
            string csv = EasyAI.Desktop.Services.CounterAiPaths.PointsCsvPath(image);
            if (!System.IO.File.Exists(csv)) return;
            ReplaceStamps(EasyAI.Desktop.Services.CounterAiLabeler.ReadCsv(csv), false);
        }
        catch (Exception)
        {
            // A broken CSV must never block opening the image.
        }
    }

    /// <summary>
    /// Machine labelling: replaces the circles / rectangles of the current image with stamps at <paramref name="points"/> (image pixels),
    /// using the current stamp shape and size. One undo unit for the delete and one for the add. Returns the number of stamps added.
    /// </summary>
    public int ReplaceStamps(IEnumerable<(int X, int Y)> points, bool notify = true)
    {
        if (_editor == null || _edit == null || Session == null) return 0;
        List<int> old = new List<int>();
        for (int i = 0; i < _edit.Document.Shapes.Count; i++)
        {
            string t = _edit.Document.Shapes[i].ShapeType;
            if (t == "circle" || t == "rectangle") old.Add(i);
        }

        if (old.Count > 0) _editor.DeleteMany(old);
        bool circle = StampCircle;
        string label = ClassNameAt(ActiveClassIndex);
        List<LabelShape> added = new List<LabelShape>();
        double r = Math.Max(1.0, StampWidth) / 2.0, hh = Math.Max(1.0, StampHeight) / 2.0;
        foreach ((int x, int y) in points)
        {
            added.Add(new LabelShape
            {
                Label = label,
                ShapeType = circle ? "circle" : "rectangle",
                Points = circle
                    ? new List<double[]> { new double[] { x, y }, new double[] { x + r, y } }
                    : new List<double[]> { new double[] { x - r, y - hh }, new double[] { x + r, y + hh } },
            });
        }

        if (added.Count > 0) _editor.Add(added, added.Select(_ => new ShapeProvenance()).ToList(), "Auto label");
        if (notify) NotifyEdited();
        return added.Count;
    }
}
