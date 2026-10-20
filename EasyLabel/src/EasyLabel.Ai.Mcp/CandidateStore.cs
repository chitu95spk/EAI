using EasyLabel.Ai.Onnx;
using Newtonsoft.Json.Linq;
using OpenCvSharp;

namespace EasyLabel.Ai.Mcp;

/// <summary>The SAM2 outlines the app wrote into the run folder (<c>candidates.json</c>) before the agent started; the agent picks them by number.</summary>
internal sealed class CandidateStore
{
    public const string FileName = "candidates.json";

    private readonly string _path;
    private Dictionary<int, (List<double[]> Points, double Score)>? _items;

    public CandidateStore(string runDirectory)
    {
        _path = Path.Combine(runDirectory, FileName);
    }

    public bool Exists => File.Exists(_path);

    private Dictionary<int, (List<double[]> Points, double Score)> Items()
    {
        if (_items != null) return _items;
        var items = new Dictionary<int, (List<double[]> Points, double Score)>();
        if (File.Exists(_path))
        {
            JObject root = JObject.Parse(File.ReadAllText(_path));
            foreach (JToken c in root["candidates"] as JArray ?? new JArray())
            {
                int id = c["id"]?.Value<int>() ?? 0;
                var pts = new List<double[]>();
                foreach (JToken p in c["points"] as JArray ?? new JArray()) pts.Add(new[] { p[0]!.Value<double>(), p[1]!.Value<double>() });
                if (id > 0 && pts.Count >= 3) items[id] = (pts, c["score"]?.Value<double>() ?? 0);
            }
        }

        _items = items;
        return items;
    }

    /// <summary>The union of the listed outlines as one polygon (largest connected part), or null with the reason.</summary>
    public (List<double[]>? Outer, List<List<double[]>> Holes, double Score, string? Error) Union(IReadOnlyList<int> ids, int width, int height)
    {
        var holes = new List<List<double[]>>();
        if (ids.Count == 0) return (null, holes, 0, "ids is empty.");
        Dictionary<int, (List<double[]> Points, double Score)> items = Items();
        using var mask = new Mat(height, width, MatType.CV_8UC1, Scalar.All(0));
        double score = 1;
        foreach (int id in ids)
        {
            if (!items.TryGetValue(id, out var item)) return (null, holes, 0, "no candidate " + id + ".");
            Cv2.FillPoly(mask, new[] { item.Points.Select(p => new Point((int)Math.Round(p[0]), (int)Math.Round(p[1]))).ToArray() }, Scalar.All(255));
            score = Math.Min(score, item.Score);
        }

        var bytes = new byte[width * height];
        System.Runtime.InteropServices.Marshal.Copy(mask.Data, bytes, 0, bytes.Length);
        MaskPolygon? polygon = MaskPolygonizer.Polygonize(bytes, width, height);
        if (polygon == null) return (null, holes, 0, "the outlines are empty.");
        foreach (System.Drawing.PointF[] hole in polygon.Holes) holes.Add(hole.Select(p => new[] { (double)p.X, (double)p.Y }).ToList());
        return (polygon.Outer.Select(p => new[] { (double)p.X, (double)p.Y }).ToList(), holes, score, null);
    }
}
