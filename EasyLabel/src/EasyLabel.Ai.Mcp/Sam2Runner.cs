using System.Drawing;
using System.Globalization;
using EasyLabel.Ai.Onnx;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace EasyLabel.Ai.Mcp;

/// <summary>Lazy SAM2 backend for the process plus the results waiting for propose_shapes.</summary>
internal sealed class Sam2Runner : IDisposable
{
    private readonly string? _modelsFolder;
    private readonly ImageSource _image;
    private readonly Dictionary<string, PendingShape> _pending = new Dictionary<string, PendingShape>(StringComparer.Ordinal);
    private Sam2Backend? _backend;
    private int _nextId;

    public Sam2Runner(string? modelsFolder, ImageSource image)
    {
        _modelsFolder = modelsFolder;
        _image = image;
    }

    public PendingShape? FindPending(string id) => _pending.TryGetValue(id, out PendingShape? shape) ? shape : null;

    public string Segment(JObject args, CancellationToken ct)
    {
        (int w, int h) = _image.ImageSize;
        AiBox? box = ParseBox(args["box"], w, h);
        List<AiPoint> points = ParsePoints(args["points"], w, h);
        if (box == null && points.Count == 0) throw new ArgumentException("Give a box [x1,y1,x2,y2] or at least one point {x,y,positive}.");
        string? label = args["label"]?.Type == JTokenType.String ? args["label"]!.Value<string>() : null;

        Sam2Backend backend = GetBackend();
        IReadOnlyList<AiProposal> results = backend.RunAsync(_image.ToAiImage(), new AiPrompt(points, box), ct).GetAwaiter().GetResult();
        if (results.Count == 0) throw new InvalidOperationException("SAM2 found no object for this prompt. Try a tighter box or add a positive point inside the object.");

        AiProposal best = results[0];
        List<double[]> outer = best.Outer.Select(ToArray).ToList();
        List<List<double[]>> holes = best.Holes.Select(hole => hole.Select(ToArray).ToList()).ToList();
        string id = "sam2-" + (++_nextId).ToString(CultureInfo.InvariantCulture);
        _pending[id] = new PendingShape(id, string.IsNullOrWhiteSpace(label) ? null : label, outer, holes, best.Score);

        return new JObject
        {
            ["id"] = id,
            ["provider"] = backend.Provider,
            ["fallbackReason"] = backend.FallbackReason,
            ["score"] = Math.Round(best.Score, 3),
            ["holeCount"] = holes.Count,
            ["pointCount"] = outer.Count,
            ["points"] = Json.Points(outer, 1),
            ["next"] = "Not committed yet. Call propose_shapes with {fromSam2: \"" + id + "\", label: <class>} to keep it.",
        }.ToString(Formatting.None);
    }

    /// <summary>
    /// One SAM2 run per object of <paramref name="objects"/> (each {x, y} click on the object, optional box [x1,y1,x2,y2]) in a single tool call.
    /// The first object computes and caches the image embedding; the others run at the same time (decoder, mask upscale and outline are CPU work
    /// outside the decoder gate). Returns the pending ids in input order (null for an object SAM2 could not segment) and the reason of each miss.
    /// </summary>
    public IReadOnlyList<(string? Id, string? Error)> SegmentMany(JArray objects, string? label, CancellationToken ct)
    {
        (int w, int h) = _image.ImageSize;
        Sam2Backend backend = GetBackend();
        AiImage image = _image.ToAiImage();
        string? cleanLabel = string.IsNullOrWhiteSpace(label) ? null : label;

        var found = new AiProposal?[objects.Count];
        var errors = new string?[objects.Count];

        void RunOne(int i)
        {
            try
            {
                ct.ThrowIfCancellationRequested();
                if (objects[i] is not JObject o) throw new ArgumentException("each object must be {x, y, box?}.");
                var points = new List<AiPoint>();
                if (o["x"] != null && o["y"] != null) points = ParsePoints(new JArray(new JObject { ["x"] = o["x"], ["y"] = o["y"], ["positive"] = true }), w, h);
                AiBox? box = ParseBox(o["box"], w, h);
                if (box == null && points.Count == 0) throw new ArgumentException("Give a click {x, y} or a box.");
                IReadOnlyList<AiProposal> results = backend.RunAsync(image, new AiPrompt(points, box), ct).GetAwaiter().GetResult();
                if (results.Count == 0) throw new InvalidOperationException("SAM2 found no object for this prompt.");
                found[i] = results[0];
            }
            catch (Exception ex) when (ex is ArgumentException || ex is InvalidOperationException || ex is AiModelException)
            {
                errors[i] = ex.Message;
            }
        }

        if (objects.Count > 0) RunOne(0);
        if (objects.Count > 1)
        {
            var options = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount), CancellationToken = ct };
            Parallel.For(1, objects.Count, options, RunOne);
        }

        bool[] tooBig = OversizedObjects(found.Select(f => f == null ? 0.0 : (double)f.Box.Width * f.Box.Height).ToList(), (double)w * h);
        var results2 = new List<(string? Id, string? Error)>(objects.Count);
        for (int i = 0; i < objects.Count; i++)
        {
            AiProposal? best = found[i];
            if (best != null && tooBig[i])
            {
                results2.Add((null, "the outline covers too much of the image (the point is probably on the background); give a point on the object"));
                continue;
            }

            if (best == null)
            {
                results2.Add((null, errors[i] ?? "SAM2 found no object."));
                continue;
            }

            List<double[]> outer = best.Outer.Select(ToArray).ToList();
            List<List<double[]>> holes = best.Holes.Select(hole => hole.Select(ToArray).ToList()).ToList();
            string id = "sam2-" + (++_nextId).ToString(CultureInfo.InvariantCulture);
            _pending[id] = new PendingShape(id, cleanLabel, outer, holes, best.Score);
            results2.Add((id, null));
        }

        return results2;
    }

    /// <summary>Registers an outline made elsewhere (a union of candidates) so propose_shapes can take it by id.</summary>
    public string AddPending(string? label, List<double[]> outer, List<List<double[]>> holes, double score)
    {
        string id = "sam2-" + (++_nextId).ToString(CultureInfo.InvariantCulture);
        _pending[id] = new PendingShape(id, string.IsNullOrWhiteSpace(label) ? null : label, outer, holes, score);
        return id;
    }

    /// <summary>Largest share of the image one object may cover.</summary>
    public const double MaxImageFraction = 0.25;

    /// <summary>With at least <see cref="MinForMedian"/> objects, one whose area is more than this many times the median is an outlier.</summary>
    public const double MaxTimesMedian = 5.0;

    public const int MinForMedian = 4;

    /// <summary>True for every object (area in square pixels, 0 = none found) that is too big: over a quarter of the image, or far above the median of the others.</summary>
    internal static bool[] OversizedObjects(IReadOnlyList<double> areas, double imageArea)
    {
        var result = new bool[areas.Count];
        List<double> found = areas.Where(a => a > 0).OrderBy(a => a).ToList();
        double median = found.Count >= MinForMedian ? found[found.Count / 2] : 0;
        for (int i = 0; i < areas.Count; i++)
        {
            if (areas[i] <= 0) continue;
            result[i] = areas[i] > MaxImageFraction * imageArea || (median > 0 && areas[i] > MaxTimesMedian * median);
        }

        return result;
    }

    private readonly object _backendGate = new object();

    /// <summary>Loads SAM2 and runs one centre-click prompt so the image embedding is cached; failures are ignored (the first real call reports them).</summary>
    public void StartWarmUp()
    {
        if (string.IsNullOrWhiteSpace(_modelsFolder)) return;
        _ = Task.Run(() =>
        {
            try
            {
                Sam2Backend backend = GetBackend();
                (int w, int h) = _image.ImageSize;
                backend.RunAsync(_image.ToAiImage(), new AiPrompt(new[] { new AiPoint(w / 2f, h / 2f, true) }), CancellationToken.None).GetAwaiter().GetResult();
            }
            catch (Exception)
            {
            }
        });
    }

    private Sam2Backend GetBackend()
    {
        lock (_backendGate)
        {
            if (_backend != null) return _backend;
            if (string.IsNullOrWhiteSpace(_modelsFolder)) throw new AiModelException("No models folder: start the server with --models <dir> or set modelsFolder in request.json.");
            AiModelResolution files = AiModelFiles.Resolve(AiModelFiles.Sam2Id, Path.Combine(_modelsFolder, AiModelFiles.Sam2Id));
            _backend = Sam2Backend.Load(files);
            return _backend;
        }
    }

    private static double[] ToArray(PointF p) => new[] { (double)p.X, (double)p.Y };

    private static AiBox? ParseBox(JToken? token, int w, int h)
    {
        if (token == null || token.Type == JTokenType.Null) return null;
        if (token is not JArray a || a.Count != 4) throw new ArgumentException("box must be [x1,y1,x2,y2].");
        var v = new double[4];
        for (int i = 0; i < 4; i++)
        {
            if (!Json.TryNumber(a[i], out v[i])) throw new ArgumentException("box values must be finite numbers.");
        }

        float x1 = ClampF(Math.Min(v[0], v[2]), w), x2 = ClampF(Math.Max(v[0], v[2]), w);
        float y1 = ClampF(Math.Min(v[1], v[3]), h), y2 = ClampF(Math.Max(v[1], v[3]), h);
        if (x2 - x1 < 1 || y2 - y1 < 1) throw new ArgumentException("box is empty after clamping to the image.");
        return new AiBox(x1, y1, x2, y2);
    }

    private static List<AiPoint> ParsePoints(JToken? token, int w, int h)
    {
        var list = new List<AiPoint>();
        if (token == null || token.Type == JTokenType.Null) return list;
        if (token is not JArray a) throw new ArgumentException("points must be an array of {x,y,positive}.");
        foreach (JToken item in a)
        {
            if (item is not JObject o || !Json.TryNumber(o["x"], out double x) || !Json.TryNumber(o["y"], out double y))
            {
                throw new ArgumentException("each point needs finite numeric x and y.");
            }

            bool positive = o["positive"]?.Type != JTokenType.Boolean || o["positive"]!.Value<bool>();
            list.Add(new AiPoint(ClampF(x, w), ClampF(y, h), positive));
        }

        return list;
    }

    private static float ClampF(double value, int max) => (float)Math.Min(max, Math.Max(0, value));

    public void Dispose()
    {
        _backend?.Dispose();
        _backend = null;
    }
}
