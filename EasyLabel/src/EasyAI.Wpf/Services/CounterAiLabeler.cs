namespace EasyAI.Desktop.Services;

/// <summary>
/// Point labels of one image for CounterAI (port of Labeler in label_points.py): add a point, remove the nearest one within <see cref="Reach"/>,
/// undo, reset to the automatic marks, and read/write <c>&lt;image&gt;_points.csv</c> (columns id,x,y; original image pixels).
/// </summary>
public sealed class CounterAiLabeler
{
    private readonly List<List<(int X, int Y)>> _history = new List<List<(int X, int Y)>>();

    public CounterAiLabeler(double reach)
    {
        Reach = reach;
    }

    public double Reach { get; set; }

    public List<(int X, int Y)> Points { get; private set; } = new List<(int X, int Y)>();

    public bool CanUndo => _history.Count > 0;

    private void Push() => _history.Add(new List<(int X, int Y)>(Points));

    public void Load(IEnumerable<(int X, int Y)> points)
    {
        Points = new List<(int X, int Y)>(points);
        _history.Clear();
    }

    public void Add(double x, double y)
    {
        Push();
        Points.Add(((int)x, (int)y));
    }

    public bool RemoveNear(double x, double y)
    {
        if (Points.Count == 0) return false;
        int best = -1;
        double bestD = double.MaxValue;
        for (int i = 0; i < Points.Count; i++)
        {
            double dx = Points[i].X - x, dy = Points[i].Y - y;
            double d = dx * dx + dy * dy;
            if (d < bestD) { bestD = d; best = i; }
        }

        if (bestD > Reach * Reach) return false;
        Push();
        Points.RemoveAt(best);
        return true;
    }

    public void Undo()
    {
        if (_history.Count == 0) return;
        Points = _history[^1];
        _history.RemoveAt(_history.Count - 1);
    }

    public void Reset(IEnumerable<(int X, int Y)> auto)
    {
        Push();
        Points = new List<(int X, int Y)>(auto);
    }

    public static List<(int X, int Y)> ReadCsv(string path) => CounterAiPaths.ReadPointsCsv(path);

    public void WriteCsv(string path) => CounterAiPaths.WritePointsCsv(path, Points);
}
