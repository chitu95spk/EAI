using EasyLabel.Core.Model;
using EasyLabel.SegAI.Data;
using EasyLabel.SegAI.Inspection;
using EasyLabel.SegAI.Project;
using PointF = System.Drawing.PointF;

namespace EasyLabel.SegAI.AutoLabel;

/// <summary>Result of auto-labelling one image: all suggestions found at the floor threshold, unfiltered.</summary>
/// <param name="Suggestions">Every suggestion at <see cref="AutoLabelService.FloorThreshold"/>; use <see cref="AutoLabelService.Filter"/> to apply a score threshold.</param>
/// <param name="HasScores">The model produced region scores; false means a score threshold has no meaning.</param>
/// <param name="Error">Set when this image could not be processed (no suggestions then).</param>
/// <param name="FromCache">The suggestions came from the cache, no inference ran.</param>
/// <param name="Uncertainty">How doubtful the model's output for this image is; null = unknown (an older cached result, or the model gave no usable score).</param>
public sealed record AutoLabelImageResult(string Rel, IReadOnlyList<SegAiSuggestion> Suggestions, bool HasScores, string? Error, bool FromCache, ImageUncertainty? Uncertainty = null);

/// <summary>Progress of a multi-image run: <paramref name="Completed"/> of <paramref name="Total"/> images done, the last one being <paramref name="Rel"/>.</summary>
public readonly record struct AutoLabelProgress(int Completed, int Total, string Rel);

/// <summary>
/// Runs the loaded model over project images and keeps the suggestions in memory. Inference always runs at
/// <see cref="FloorThreshold"/>; a score threshold is applied afterwards with <see cref="Filter"/> without new inference. Cache entries
/// are valid for the same image path, model file name and image file (length and write time), at most <see cref="MaxCachedImages"/>
/// images (least recently used dropped). Inference goes through the session lock; the cache has its own lock and is never held
/// during inference.
/// </summary>
public sealed class AutoLabelService
{
    /// <summary>Defect threshold used for every inference so a score slider can move in both directions.</summary>
    public const float FloorThreshold = 0.05f;

    /// <summary>LRU capacity, in images.</summary>
    public const int MaxCachedImages = 200;

    private sealed class CacheEntry
    {
        public string Model = "";
        public string Stamp = "";
        public bool HasScores;
        public ImageUncertainty? Uncertainty;
        public IReadOnlyList<SegAiSuggestion> Suggestions = Array.Empty<SegAiSuggestion>();
        public LinkedListNode<string> Node = null!;
    }

    private readonly SegAiSession _session;
    private readonly Func<string, (string Model, string Stamp)> _probe;
    private readonly Func<string, float, (SegAiResult Result, int Width, int Height)> _infer;
    private ResultDiskCache? _disk;
    private Func<string>? _modelStamp;
    private readonly object _cacheGate = new();
    private readonly Dictionary<string, CacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<string> _lru = new(); // first = most recently used

    public AutoLabelService(SegAiSession session)
        : this(session, session.ProbeForAutoLabel, session.PredictForAutoLabel)
    {
        ConfigureDiskCache(new ResultDiskCache(session.Paths.CacheDir), session.ModelFileStampForCache);
    }

    /// <summary>Test seam: replaces the session probe/inference (the session is still used for classes, edits and MinDefectArea).</summary>
    internal AutoLabelService(SegAiSession session, Func<string, (string Model, string Stamp)> probe, Func<string, float, (SegAiResult Result, int Width, int Height)> infer)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
        _infer = infer ?? throw new ArgumentNullException(nameof(infer));
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
    private string? DiskKey(string rel, string model, string stamp)
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
        return string.Join(
            "|",
            "autolabel",
            rel,
            model,
            stamp,
            modelFile,
            FloorThreshold.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            _session.Inference.MinDefectArea.ToString(System.Globalization.CultureInfo.InvariantCulture),
            _session.InstanceScoreAnchorForCache().ToString("R", System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>Number of images currently cached.</summary>
    public int CachedImages
    {
        get { lock (_cacheGate) { return _cache.Count; } }
    }

    /// <summary>Drops every cached result.</summary>
    public void Clear()
    {
        lock (_cacheGate)
        {
            _cache.Clear();
            _lru.Clear();
        }
    }

    /// <summary>Drops the in-memory result of one image (its pixels changed). Disk results are keyed by the image stamp and need no action.</summary>
    public void Invalidate(string rel)
    {
        ArgumentNullException.ThrowIfNull(rel);
        lock (_cacheGate)
        {
            if (!_cache.TryGetValue(rel, out CacheEntry? entry)) return;
            if (entry.Node.List != null) _lru.Remove(entry.Node);
            _cache.Remove(rel);
        }
    }

    /// <summary>
    /// Suggestions for one image (cached when model and image are unchanged). Per-image problems (no model, missing or undecodable
    /// image, inference error) are returned in <see cref="AutoLabelImageResult.Error"/>; nothing is cached for them.
    /// </summary>
    public AutoLabelImageResult Run(string rel)
    {
        ArgumentNullException.ThrowIfNull(rel);
        // Native training and inference must not share the process at the same time; the answer is a per-image error, not an exception.
        if (_session.IsTrainingActive) return new AutoLabelImageResult(rel, Array.Empty<SegAiSuggestion>(), false, SegAiSession.TrainingActiveMessage, false);
        try
        {
            (string model, string stamp) = _probe(rel);
            // Position project with a model that has no instance head: no mask suggestions and nothing cached.
            if (_session.NeedsInstanceHead) return new AutoLabelImageResult(rel, Array.Empty<SegAiSuggestion>(), false, SegAiSession.InstanceHeadMissingMessage, false);
            lock (_cacheGate)
            {
                if (_cache.TryGetValue(rel, out CacheEntry? hit) && hit.Model == model && hit.Stamp == stamp)
                {
                    Touch(hit);
                    return new AutoLabelImageResult(rel, hit.Suggestions, hit.HasScores, null, true, hit.Uncertainty);
                }
            }

            string? diskKey = DiskKey(rel, model, stamp);
            if (diskKey != null && _disk!.TryGet(ResultDiskCache.AutoLabelKind, diskKey, out AutoLabelDiskPayload? stored) && stored != null)
            {
                IReadOnlyList<SegAiSuggestion>? restored = stored.TryRestore();
                if (restored != null)
                {
                    ImageUncertainty? storedUncertainty = stored.RestoreUncertainty();
                    Store(rel, model, stamp, stored.HasScores, restored, storedUncertainty);
                    return new AutoLabelImageResult(rel, restored, stored.HasScores, null, true, storedUncertainty);
                }
            }

            IReadOnlyList<SegAiSuggestion> suggestions;
            bool hasScores;
            ImageUncertainty? uncertainty;
            if (_session.UsesInstanceSuggestions)
            {
                // Instance head (Position, or Detect box mode): one box or circle per object; no masks, so no uncertainty map.
                var live = _session.PredictInstancesForAutoLabel(rel, FloorThreshold);
                suggestions = SegAiSuggestionBuilder.BuildInstances(rel, live.Instances, live.OutputShape);
                hasScores = true;
                uncertainty = null;
            }
            else
            {
                (SegAiResult result, int width, int height) = _infer(rel, FloorThreshold);
                if (result.Error != null) return new AutoLabelImageResult(rel, Array.Empty<SegAiSuggestion>(), false, result.Error, false);
                // Named-class result (P2h): ClassIndex = project class position; the NG/OK result keeps its two-class builder.
                suggestions = result.PerClass != null
                    ? SegAiSuggestionBuilder.BuildClasses(rel, result, width, height, _session.Inference.MinDefectArea)
                    : SegAiSuggestionBuilder.Build(rel, result, width, height, _session.Inference.MinDefectArea);
                hasScores = result.PerClass != null || result.RegionScores != null;
                // From the maps and scores this very inference returned: no extra inference.
                uncertainty = ImageUncertaintyCalculator.FromResult(result);
            }

            Store(rel, model, stamp, hasScores, suggestions, uncertainty);
            if (diskKey != null) _disk!.PutAsync(ResultDiskCache.AutoLabelKind, diskKey, AutoLabelDiskPayload.From(hasScores, suggestions, uncertainty));
            return new AutoLabelImageResult(rel, suggestions, hasScores, null, false, uncertainty);
        }
        catch (Exception ex) when (ex is InvalidOperationException || ex is IOException || ex is InvalidDataException || ex is ArgumentException)
        {
            return new AutoLabelImageResult(rel, Array.Empty<SegAiSuggestion>(), false, ex.Message, false);
        }
    }

    /// <summary>
    /// True for a rectangle suggestion that comes from the instance path (an instance head is loaded): it is accepted as a two-point
    /// rectangle. Every other rectangle suggestion keeps the polygon path it always had (no instance head, so no box-mode suggestion).
    /// </summary>
    public static bool IsBoxRectangle(bool instanceHeadLoaded, string? shapeType, int outerLength)
    {
        return instanceHeadLoaded && string.Equals(shapeType, SegAiSuggestionBuilder.RectangleShape, StringComparison.Ordinal) && outerLength == 4;
    }

    /// <summary>Position project, model loaded, model without an instance head: the one case where no mask suggestion is offered.</summary>
    public static bool MissingInstanceHead(bool isPosition, bool modelLoaded, bool instanceHead)
    {
        return isPosition && modelLoaded && !instanceHead;
    }

    /// <summary>Background version of <see cref="Run(string)"/>.</summary>
    public Task<AutoLabelImageResult> RunAsync(string rel, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            return Run(rel);
        }, ct);
    }

    /// <summary>
    /// Runs a list of images in order on a background task. Cancelling stops before the next image (completed images stay cached) and
    /// the task ends cancelled. The result list has one entry per image that was processed.
    /// </summary>
    public Task<IReadOnlyList<AutoLabelImageResult>> RunAsync(IReadOnlyList<string> rels, IProgress<AutoLabelProgress>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(rels);
        string[] snapshot = rels.ToArray();
        return Task.Run<IReadOnlyList<AutoLabelImageResult>>(() =>
        {
            var results = new List<AutoLabelImageResult>(snapshot.Length);
            for (int i = 0; i < snapshot.Length; i++)
            {
                ct.ThrowIfCancellationRequested();
                results.Add(Run(snapshot[i]));
                progress?.Report(new AutoLabelProgress(i + 1, snapshot.Length, snapshot[i]));
            }

            return results;
        }, ct);
    }

    /// <summary>
    /// The cached suggestions of an image with score at least <paramref name="threshold"/>; no inference. A suggestion without a score
    /// is always kept. Empty when the image has no cached result.
    /// </summary>
    public IReadOnlyList<SegAiSuggestion> Filter(string rel, float threshold)
    {
        ArgumentNullException.ThrowIfNull(rel);
        lock (_cacheGate)
        {
            if (!_cache.TryGetValue(rel, out CacheEntry? entry)) return Array.Empty<SegAiSuggestion>();
            Touch(entry);
            return Filter(entry.Suggestions, threshold);
        }
    }

    /// <summary>
    /// Uncertainty of the in-memory cached result of an image; no inference and no disk read (cheap enough for list rebuilds on the UI thread).
    /// Null when the image has no cached result or the result has no uncertainty.
    /// </summary>
    public ImageUncertainty? CachedUncertainty(string rel)
    {
        ArgumentNullException.ThrowIfNull(rel);
        lock (_cacheGate)
        {
            return _cache.TryGetValue(rel, out CacheEntry? entry) ? entry.Uncertainty : null;
        }
    }

    /// <summary>
    /// Fills the in-memory cache from the disk cache for the loaded model, so <see cref="CachedUncertainty"/> knows the images of an earlier
    /// session. No inference runs. Images already in memory, images without a usable disk entry and any read problem are skipped (they stay
    /// unknown). Stops at <see cref="MaxCachedImages"/> so no result is evicted. Blocking (disk and session lock): call on a background thread.
    /// Returns the number of images restored.
    /// </summary>
    public int WarmFromDisk(IEnumerable<string> rels)
    {
        ArgumentNullException.ThrowIfNull(rels);
        ResultDiskCache? disk = _disk;
        if (disk == null || _session.IsTrainingActive) return 0;
        int restored = 0;
        foreach (string rel in rels)
        {
            lock (_cacheGate)
            {
                if (_cache.Count >= MaxCachedImages) break;
            }

            try
            {
                (string model, string stamp) = _probe(rel);
                lock (_cacheGate)
                {
                    if (_cache.TryGetValue(rel, out CacheEntry? hit) && hit.Model == model && hit.Stamp == stamp) continue;
                }

                string? diskKey = DiskKey(rel, model, stamp);
                if (diskKey == null || !disk.TryGet(ResultDiskCache.AutoLabelKind, diskKey, out AutoLabelDiskPayload? stored) || stored == null) continue;
                IReadOnlyList<SegAiSuggestion>? suggestions = stored.TryRestore();
                if (suggestions == null) continue;
                Store(rel, model, stamp, stored.HasScores, suggestions, stored.RestoreUncertainty());
                restored++;
            }
            catch (Exception)
            {
                // Best effort: a missing, busy or unreadable entry only means the uncertainty stays unknown.
            }
        }

        return restored;
    }

    /// <summary>Score filter on any suggestion list (score at least <paramref name="threshold"/>, unscored kept).</summary>
    public static IReadOnlyList<SegAiSuggestion> Filter(IReadOnlyList<SegAiSuggestion> suggestions, float threshold)
    {
        ArgumentNullException.ThrowIfNull(suggestions);
        return suggestions.Where(s => s.Score == null || s.Score.Value >= threshold).ToList();
    }

    /// <summary>
    /// Writes the suggestions into the image through <see cref="SegAiSession.SaveEditedShapes"/>: polygons (holes kept), or for the instance
    /// head an oriented box or a circle. Position projects accept only instance shapes (a mask polygon is skipped there). Shapes already in
    /// the image are kept. Works for any image, not only the one open in the editor. Returns the number added.
    /// </summary>
    public int Accept(string rel, IReadOnlyList<SegAiSuggestion> suggestions)
    {
        ArgumentNullException.ThrowIfNull(rel);
        ArgumentNullException.ThrowIfNull(suggestions);
        if (suggestions.Count == 0) return 0;
        SegAiEditDocument edit = _session.LoadForEdit(rel);
        IReadOnlyList<string> names = _session.ClassNames;
        edit.Document.Shapes ??= new List<LabelShape>();
        int added = 0;
        foreach (SegAiSuggestion suggestion in suggestions)
        {
            if (suggestion.ClassIndex < 0 || suggestion.ClassIndex >= names.Count)
                throw new ArgumentOutOfRangeException(nameof(suggestions), "Unknown class index " + suggestion.ClassIndex + ".");
            bool instance = SegAiSuggestionBuilder.IsInstanceShape(suggestion.ShapeType);
            if (_session.IsInstanceHead && !instance) continue;
            if (IsBoxRectangle(_session.UsesInstanceSuggestions, suggestion.ShapeType, suggestion.Outer.Length))
            {
                // Detect box mode: the Rect tool's two corners (top-left, bottom-right) of the suggested box; holes do not apply.
                var rectangle = new LabelShape { Label = names[suggestion.ClassIndex], ShapeType = SegAiSuggestionBuilder.RectangleShape };
                rectangle.Points.Add(new double[] { suggestion.Outer[0].X, suggestion.Outer[0].Y });
                rectangle.Points.Add(new double[] { suggestion.Outer[2].X, suggestion.Outer[2].Y });
                edit.Document.Shapes.Add(rectangle);
                added++;
                continue;
            }

            var shape = new LabelShape { Label = names[suggestion.ClassIndex], ShapeType = instance ? suggestion.ShapeType : "polygon" };
            if (instance)
            {
                shape.Points.AddRange(SegAiSuggestionBuilder.InstancePoints(suggestion.ShapeType, suggestion.Outer));
                if (suggestion.ShapeType == SegAiSuggestionBuilder.OrientedRectangleShape) shape.Direction = SegAiSuggestionBuilder.DirectionOf(suggestion.Outer);
                edit.Document.Shapes.Add(shape);
            }
            else
            {
                foreach (PointF p in suggestion.Outer) shape.Points.Add(new double[] { p.X, p.Y });
                edit.Document.Shapes.Add(shape);
                if (suggestion.Holes.Length > 0) edit.Holes.Set(shape, suggestion.Holes.Select(h => (PointF[])h.Clone()).ToArray());
            }

            added++;
        }

        _session.SaveEditedShapes(rel, edit.Document, edit.Sidecar, shape => edit.Holes.TryGet(shape, out PointF[][] holes) ? holes : null);
        return added;
    }

    private void Store(string rel, string model, string stamp, bool hasScores, IReadOnlyList<SegAiSuggestion> suggestions, ImageUncertainty? uncertainty)
    {
        lock (_cacheGate)
        {
            if (_cache.TryGetValue(rel, out CacheEntry? existing))
            {
                existing.Model = model;
                existing.Stamp = stamp;
                existing.HasScores = hasScores;
                existing.Uncertainty = uncertainty;
                existing.Suggestions = suggestions;
                Touch(existing);
                return;
            }

            var entry = new CacheEntry { Model = model, Stamp = stamp, HasScores = hasScores, Uncertainty = uncertainty, Suggestions = suggestions };
            entry.Node = _lru.AddFirst(rel);
            _cache[rel] = entry;
            while (_cache.Count > MaxCachedImages)
            {
                string oldest = _lru.Last!.Value;
                _lru.RemoveLast();
                _cache.Remove(oldest);
            }
        }
    }

    // Caller holds _cacheGate.
    private void Touch(CacheEntry entry)
    {
        if (entry.Node.List == null || entry.Node == _lru.First) return;
        _lru.Remove(entry.Node);
        _lru.AddFirst(entry.Node);
    }
}
