using System.Globalization;
using System.Text;
using EasyLabel.SegAI.AutoLabel;
using EasyLabel.SegAI.Data;
using EasyLabel.SegAI.Model;
using EasyLabel.SegAI.Project;

namespace EasyLabel.SegAI.Inspection;

/// <summary>Outcome of inspecting one image. <see cref="NotRun"/> is only used by lists for rows without a result.</summary>
public enum InspectVerdict
{
    NotRun = 0,
    OK = 1,
    NG = 2,
    Error = 3,
}

/// <summary>
/// Result of inspecting one image with the loaded model. Nothing here is a label: the suggestions are a read-only overlay.
/// </summary>
/// <param name="Path">Full path of the image.</param>
/// <param name="Regions">Number of NG regions that passed the filters (0 for OK and for an error).</param>
/// <param name="Score">Highest region score, or the native image score when the model gave no region scores.</param>
/// <param name="ElapsedMs">Inference time of the run that produced the result (a cached result keeps it).</param>
/// <param name="Model">File name of the model that produced the result.</param>
/// <param name="Suggestions">NG regions (class 0) followed by OK regions (class 1) as polygons in image pixels, for the overlay; for a named-class project the class position in <see cref="SegAiSuggestion.ClassIndex"/>.</param>
/// <param name="Width">Image width the suggestions are scaled to.</param>
/// <param name="Height">Image height the suggestions are scaled to.</param>
/// <param name="Classes">Named-class (N-class) result only: one entry per enabled class in table order; null for the NG/OK result.</param>
/// <param name="Uncertainty">How doubtful the model's output is; null = unknown (an older cached result, an error, or no usable score).</param>
public sealed record InspectResult(
    string Path,
    InspectVerdict Verdict,
    int Regions,
    float Score,
    double ElapsedMs,
    string Model,
    DateTime TimestampUtc,
    string? Error,
    bool FromCache,
    IReadOnlyList<SegAiSuggestion> Suggestions,
    int Width,
    int Height,
    IReadOnlyList<InspectClassCount>? Classes = null,
    ImageUncertainty? Uncertainty = null);

/// <summary>One enabled class of a named-class inspection.</summary>
/// <param name="ClassIndex">Position in the project class table (0-based), the <see cref="SegAiSuggestion.ClassIndex"/> of its regions.</param>
/// <param name="Name">Project class name.</param>
/// <param name="Kind">DEFECT or GOOD class.</param>
/// <param name="Regions">Regions of this class that passed the filters.</param>
public sealed record InspectClassCount(int ClassIndex, string Name, SegAiClassKind Kind, int Regions);

/// <summary>One finished image of <see cref="InspectService.RunAll"/>; <paramref name="Result"/> lets a list update row by row.</summary>
public readonly record struct InspectProgress(int Completed, int Total, string Path, InspectResult Result);

/// <summary>Counts and mean time of a set of results (rows without a result are not counted).</summary>
/// <param name="Run">Results with a verdict (OK, NG and errors).</param>
/// <param name="MeanMs">Mean inference time of the OK and NG results; 0 when there is none.</param>
public sealed record InspectSummary(int Run, int Ok, int Ng, int Errors, double MeanMs);

/// <summary>
/// Runs the loaded model over images anywhere on disk and keeps the results in memory. Never writes labels. Inference goes through the
/// session lock; the cache has its own lock that is never held during inference. A cached result is valid for the same image path,
/// the same model file, the same image file (length and write time) and the same threshold and minimum area, at most
/// <see cref="MaxCachedImages"/> images (least recently used dropped).
/// </summary>
public sealed class InspectService
{
    /// <summary>LRU capacity, in images.</summary>
    public const int MaxCachedImages = 500;

    private sealed class CacheEntry
    {
        public string Key = "";
        public InspectResult Result = null!;
        public LinkedListNode<string> Node = null!;
    }

    private readonly Func<bool> _isTrainingActive;
    private readonly Func<string, (string Model, string Stamp)> _probe;
    private readonly Func<string, (SegAiResult Result, int Width, int Height)> _infer;
    private readonly Func<(float Threshold, int MinArea)> _settings;
    private readonly Func<DateTime> _clock;
    private ResultDiskCache? _disk;
    private Func<string>? _modelStamp;
    private readonly object _gate = new();
    private readonly Dictionary<string, CacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<string> _lru = new(); // first = most recently used

    public InspectService(SegAiSession session)
        : this(
            () => (session ?? throw new ArgumentNullException(nameof(session))).IsTrainingActive,
            session.ProbeForInspect,
            session.PredictForInspect,
            () => (session.Inference.DefectThreshold, session.Inference.MinDefectArea),
            () => DateTime.UtcNow)
    {
        ConfigureDiskCache(new ResultDiskCache(session.Paths.CacheDir), session.ModelFileStampForCache);
        _instanceMode = () => session.UsesInstanceSuggestions;
        _inferInstances = session.PredictInstancesForInspect;
    }

    // Instance head (oriented box / circle) path: when the loaded model has an instance head, Inspect shows boxes instead of masks.
    private Func<bool>? _instanceMode;
    private Func<string, (SegAiInstanceResult[] Instances, int Width, int Height, string OutputShape)>? _inferInstances;

    /// <summary>Test seam: replaces the session guard, probe, inference, settings and clock.</summary>
    internal InspectService(
        Func<bool> isTrainingActive,
        Func<string, (string Model, string Stamp)> probe,
        Func<string, (SegAiResult Result, int Width, int Height)> infer,
        Func<(float Threshold, int MinArea)> settings,
        Func<DateTime> clock)
    {
        _isTrainingActive = isTrainingActive ?? throw new ArgumentNullException(nameof(isTrainingActive));
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
        _infer = infer ?? throw new ArgumentNullException(nameof(infer));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    /// <summary>
    /// Turns the on-disk result cache on (both arguments set) or off (either null). <paramref name="modelStamp"/> returns the model file
    /// stamp (length:ticks); an empty stamp skips the disk for that call. Call before the first <see cref="Run(string)"/>.
    /// </summary>
    internal void ConfigureDiskCache(ResultDiskCache? disk, Func<string>? modelStamp)
    {
        _disk = disk;
        _modelStamp = modelStamp;
    }

    /// <summary>Waits for queued disk writes (tests, shutdown).</summary>
    internal void FlushDiskWrites()
    {
        _disk?.Flush();
    }

    // Full key of the on-disk entry, or null when the disk cache is off or the model file cannot be stamped.
    private string? DiskKey(string full, string key)
    {
        if (_disk == null || _modelStamp == null) return null;
        string modelFile;
        try
        {
            modelFile = _modelStamp();
        }
        catch (Exception)
        {
            return null;
        }

        if (string.IsNullOrEmpty(modelFile)) return null;
        return string.Join("|", "inspect", full, key, modelFile);
    }

    /// <summary>Number of images currently cached.</summary>
    public int CachedImages
    {
        get { lock (_gate) { return _cache.Count; } }
    }

    /// <summary>Drops every cached result.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _cache.Clear();
            _lru.Clear();
        }
    }

    /// <summary>Drops the in-memory result of one image (its pixels changed). Disk results are keyed by the image stamp and need no action.</summary>
    public void Invalidate(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        string full;
        try { full = System.IO.Path.GetFullPath(path); }
        catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException) { full = path; }
        lock (_gate)
        {
            if (!_cache.TryGetValue(full, out CacheEntry? entry)) return;
            if (entry.Node.List != null) _lru.Remove(entry.Node);
            _cache.Remove(full);
        }
    }

    /// <summary>
    /// Verdict of a native result: an error is <see cref="InspectVerdict.Error"/>; otherwise NG when the result is not OK, which is the
    /// ToolSegmentAI rule (a model without an OK class: at least one NG region passed the filters; otherwise the image score is at or above the threshold).
    /// </summary>
    public static InspectVerdict VerdictOf(SegAiResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Error != null) return InspectVerdict.Error;
        return result.IsOK ? InspectVerdict.OK : InspectVerdict.NG;
    }

    /// <summary>Highest region score; the native image score when the model gave no (or an empty set of) region scores.</summary>
    public static float ScoreOf(SegAiResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        float[]? scores = result.RegionScores;
        if (scores == null || scores.Length == 0) return result.Score;
        float best = scores[0];
        for (int i = 1; i < scores.Length; i++)
        {
            if (scores[i] > best) best = scores[i];
        }

        return best;
    }

    /// <summary>The part of the cache key besides the image path: model file, image stamp (length:ticks), threshold and minimum area.</summary>
    public static string CacheKey(string model, string stamp, float threshold, int minArea)
    {
        return string.Join("|", model, stamp, threshold.ToString("R", CultureInfo.InvariantCulture), minArea.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Inspects one image (cached when path, model, file and settings are unchanged). Per-image problems (no model, training active,
    /// missing or undecodable image, inference error) are returned as <see cref="InspectVerdict.Error"/>; nothing is cached for them.
    /// </summary>
    public InspectResult Run(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        string full;
        try
        {
            full = System.IO.Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
        {
            return Failed(path, "", ex.Message);
        }

        // Native training and inference must not share the process at the same time; the answer is a per-image error, not an exception.
        if (_isTrainingActive()) return Failed(full, "", SegAiSession.InspectTrainingActiveMessage);
        try
        {
            (string model, string stamp) = _probe(full);
            (float threshold, int minArea) = _settings();
            bool instanceMode = _instanceMode != null && _inferInstances != null && _instanceMode();
            string key = CacheKey(model, stamp, threshold, minArea) + (instanceMode ? "|inst" : string.Empty);
            lock (_gate)
            {
                if (_cache.TryGetValue(full, out CacheEntry? hit) && hit.Key == key)
                {
                    Touch(hit);
                    return hit.Result with { FromCache = true };
                }
            }

            string? diskKey = DiskKey(full, key);
            if (diskKey != null && _disk!.TryGet(ResultDiskCache.InspectKind, diskKey, out InspectDiskPayload? stored) && stored != null)
            {
                InspectResult? restored = stored.TryRestore(full);
                if (restored != null)
                {
                    Store(full, key, restored);
                    return restored with { FromCache = true };
                }
            }

            if (instanceMode)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var live = _inferInstances!(full);
                sw.Stop();
                IReadOnlyList<SegAiSuggestion> boxes = SegAiSuggestionBuilder.BuildInstances(full, live.Instances, live.OutputShape);
                float best = 0f;
                foreach (SegAiInstanceResult inst in live.Instances)
                {
                    if (inst.Score > best) best = inst.Score;
                }

                var boxResult = new InspectResult(
                    full,
                    live.Instances.Length > 0 ? InspectVerdict.NG : InspectVerdict.OK,
                    live.Instances.Length,
                    best,
                    (float)sw.Elapsed.TotalMilliseconds,
                    model,
                    _clock(),
                    null,
                    false,
                    boxes,
                    live.Width,
                    live.Height,
                    null,
                    null);
                Store(full, key, boxResult);
                if (diskKey != null) _disk!.PutAsync(ResultDiskCache.InspectKind, diskKey, InspectDiskPayload.From(boxResult));
                return boxResult;
            }

            (SegAiResult native, int width, int height) = _infer(full);
            if (native.Error != null) return Failed(full, model, native.Error);
            IReadOnlyList<SegAiSuggestion> suggestions = native.PerClass != null
                ? SegAiSuggestionBuilder.BuildClasses(full, native, width, height, minArea)
                : SegAiSuggestionBuilder.Build(full, native, width, height, minArea);
            var result = new InspectResult(
                full,
                VerdictOf(native),
                native.Regions.Count,
                ScoreOf(native),
                native.ElapsedMs,
                model,
                _clock(),
                null,
                false,
                suggestions,
                width,
                height,
                native.PerClass?.Select(p => new InspectClassCount(p.ClassId - 1, p.Name, p.Kind, p.Regions.Count)).ToList(),
                ImageUncertaintyCalculator.FromResult(native));
            Store(full, key, result);
            if (diskKey != null) _disk!.PutAsync(ResultDiskCache.InspectKind, diskKey, InspectDiskPayload.From(result));
            return result;
        }
        catch (Exception ex) when (ex is InvalidOperationException || ex is IOException || ex is InvalidDataException || ex is ArgumentException)
        {
            return Failed(full, "", ex.Message);
        }
    }

    /// <summary>Background version of <see cref="Run(string)"/>.</summary>
    public Task<InspectResult> RunAsync(string path, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            return Run(path);
        }, ct);
    }

    /// <summary>
    /// Inspects a list of images in order, one at a time. Cancelling stops before the next image (finished images stay cached and were
    /// reported) and throws <see cref="OperationCanceledException"/>. One entry per processed image.
    /// </summary>
    public IReadOnlyList<InspectResult> RunAll(IReadOnlyList<string> paths, IProgress<InspectProgress>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(paths);
        string[] snapshot = paths.ToArray();
        var results = new List<InspectResult>(snapshot.Length);
        for (int i = 0; i < snapshot.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            InspectResult result = Run(snapshot[i]);
            results.Add(result);
            progress?.Report(new InspectProgress(i + 1, snapshot.Length, snapshot[i], result));
        }

        return results;
    }

    /// <summary>Background version of <see cref="RunAll"/>.</summary>
    public Task<IReadOnlyList<InspectResult>> RunAllAsync(IReadOnlyList<string> paths, IProgress<InspectProgress>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return Task.Run(() => RunAll(paths, progress, ct), ct);
    }

    /// <summary>Counts and mean time of the given results; <see cref="InspectVerdict.NotRun"/> entries are ignored.</summary>
    public static InspectSummary Summarize(IEnumerable<InspectResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        int ok = 0, ng = 0, errors = 0;
        double total = 0;
        foreach (InspectResult r in results)
        {
            switch (r.Verdict)
            {
                case InspectVerdict.OK: ok++; total += r.ElapsedMs; break;
                case InspectVerdict.NG: ng++; total += r.ElapsedMs; break;
                case InspectVerdict.Error: errors++; break;
            }
        }

        int timed = ok + ng;
        return new InspectSummary(ok + ng + errors, ok, ng, errors, timed == 0 ? 0 : total / timed);
    }

    private InspectResult Failed(string full, string model, string message)
    {
        return new InspectResult(full, InspectVerdict.Error, 0, 0f, 0, model, _clock(), message, false, Array.Empty<SegAiSuggestion>(), 0, 0);
    }

    private void Store(string full, string key, InspectResult result)
    {
        lock (_gate)
        {
            if (_cache.TryGetValue(full, out CacheEntry? existing))
            {
                existing.Key = key;
                existing.Result = result;
                Touch(existing);
                return;
            }

            var entry = new CacheEntry { Key = key, Result = result };
            entry.Node = _lru.AddFirst(full);
            _cache[full] = entry;
            while (_cache.Count > MaxCachedImages)
            {
                string oldest = _lru.Last!.Value;
                _lru.RemoveLast();
                _cache.Remove(oldest);
            }
        }
    }

    // Caller holds _gate.
    private void Touch(CacheEntry entry)
    {
        if (entry.Node.List == null || entry.Node == _lru.First) return;
        _lru.Remove(entry.Node);
        _lru.AddFirst(entry.Node);
    }
}

/// <summary>Writes inspection results as CSV: UTF-8 without BOM, CRLF lines, RFC 4180 quoting, invariant numbers.</summary>
public static class InspectCsv
{
    public const string Header = "file,verdict,regions,score,ms,model,timestamp";

    /// <summary>The CSV text of the results that have a verdict (rows without a result are left out).</summary>
    public static string Format(IEnumerable<InspectResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        var text = new StringBuilder();
        text.Append(Header).Append("\r\n");
        foreach (InspectResult r in results)
        {
            if (r.Verdict == InspectVerdict.NotRun) continue;
            text.Append(Quote(r.Path)).Append(',')
                .Append(r.Verdict == InspectVerdict.Error ? "ERROR" : r.Verdict.ToString()).Append(',')
                .Append(r.Regions.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(r.Score.ToString("0.0000", CultureInfo.InvariantCulture)).Append(',')
                .Append(r.ElapsedMs.ToString("0.0", CultureInfo.InvariantCulture)).Append(',')
                .Append(Quote(r.Model)).Append(',')
                .Append(r.TimestampUtc.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture))
                .Append("\r\n");
        }

        return text.ToString();
    }

    /// <summary>Writes <see cref="Format"/> to <paramref name="path"/> (UTF-8, no BOM), replacing an existing file.</summary>
    public static void Write(string path, IEnumerable<InspectResult> results)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("CSV path is required.", nameof(path));
        File.WriteAllText(path, Format(results), new UTF8Encoding(false));
    }

    /// <summary>A field with a comma, quote, CR/LF or edge space is wrapped in quotes with inner quotes doubled.</summary>
    public static string Quote(string? value)
    {
        string v = value ?? "";
        bool needs = v.Length > 0
            && (v.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0 || v[0] == ' ' || v[v.Length - 1] == ' ');
        return needs ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
    }
}
