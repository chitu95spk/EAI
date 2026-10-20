using System.Security.Cryptography;
using System.Text;
using EasyLabel.SegAI.AutoLabel;
using EasyLabel.SegAI.Inspection;
using EasyLabel.SegAI.Model;
using Newtonsoft.Json;
using PointF = System.Drawing.PointF;

namespace EasyLabel.SegAI.Project;

/// <summary>
/// Disk store for auto-label and inspect results: <c>&lt;cacheDir&gt;\results\&lt;kind&gt;\&lt;hash&gt;.json</c> where hash is the first 32
/// hex characters of the SHA-256 of the full key string. Each file keeps the full key string, so a hash collision, a changed key,
/// a wrong version or a corrupt file is a miss, never an error. Writes are atomic (temp file in the same folder, then replace),
/// run one after another on the thread pool and swallow every IO error. Nothing is ever written outside <c>results</c>.
/// </summary>
public sealed class ResultDiskCache
{
    public const int CurrentVersion = 1;
    public const string FolderName = "results";
    public const string AutoLabelKind = "autolabel";
    public const string InspectKind = "inspect";

    private readonly string _root;
    private readonly object _writeGate = new();
    private Task _pending = Task.CompletedTask;

    private sealed class FileModel<T>
        where T : class
    {
        public int Version { get; set; }
        public string? KeyString { get; set; }
        public T? Payload { get; set; }
    }

    /// <param name="cacheDir">The project cache folder (ProjectPaths.CacheDir); files live under <c>cacheDir\results</c>.</param>
    public ResultDiskCache(string cacheDir)
    {
        ArgumentException.ThrowIfNullOrEmpty(cacheDir);
        _root = Path.Combine(cacheDir, FolderName);
    }

    /// <summary>The folder that holds every file of this store.</summary>
    public string RootDir => _root;

    /// <summary>First 32 hex characters (lower case) of the SHA-256 of the UTF-8 key string.</summary>
    public static string HashOf(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return System.Convert.ToHexString(hash).Substring(0, 32).ToLowerInvariant();
    }

    /// <summary>The file of a key, or null for an unknown kind.</summary>
    public string? FilePathFor(string kind, string key)
    {
        if (key == null || !IsKnownKind(kind)) return null;
        return Path.Combine(_root, kind, HashOf(key) + ".json");
    }

    /// <summary>The stored payload when a file for the key exists, has the current version and the same full key string.</summary>
    public bool TryGet<T>(string kind, string key, out T? payload)
        where T : class
    {
        payload = null;
        try
        {
            string? path = FilePathFor(kind, key);
            if (path == null || !File.Exists(path)) return false;
            FileModel<T>? model = JsonConvert.DeserializeObject<FileModel<T>>(File.ReadAllText(path));
            if (model == null || model.Version != CurrentVersion || model.KeyString != key || model.Payload == null) return false;
            payload = model.Payload;
            return true;
        }
        catch (Exception)
        {
            payload = null;
            return false;
        }
    }

    /// <summary>Writes the payload now (atomic). Returns false when anything failed; never throws.</summary>
    public bool Put<T>(string kind, string key, T payload)
        where T : class
    {
        string? temp = null;
        try
        {
            string? path = FilePathFor(kind, key);
            if (path == null || payload == null) return false;
            string? dir = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(dir)) return false;
            Directory.CreateDirectory(dir);
            var model = new FileModel<T> { Version = CurrentVersion, KeyString = key, Payload = payload };
            temp = Path.Combine(dir, Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N") + ".tmp");
            File.WriteAllText(temp, JsonConvert.SerializeObject(model));
            File.Move(temp, path, overwrite: true);
            temp = null;
            return true;
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            if (temp != null)
            {
                try { File.Delete(temp); } catch (Exception) { }
            }
        }
    }

    /// <summary>Queues <see cref="Put{T}"/> on the thread pool, after the writes queued before it. The caller is never blocked.</summary>
    public void PutAsync<T>(string kind, string key, T payload)
        where T : class
    {
        lock (_writeGate)
        {
            _pending = _pending.ContinueWith(_ => Put(kind, key, payload), TaskScheduler.Default);
        }
    }

    /// <summary>Waits until every write queued so far has finished (used by tests and at shutdown).</summary>
    public void Flush()
    {
        Task pending;
        lock (_writeGate) { pending = _pending; }
        try { pending.Wait(); } catch (Exception) { }
    }

    private static bool IsKnownKind(string kind)
    {
        return kind == AutoLabelKind || kind == InspectKind;
    }
}

/// <summary>Reads the optional uncertainty fields of a stored payload; a missing or invalid value is "unknown" (null), never an error.</summary>
internal static class UncertaintyStore
{
    public static ImageUncertainty? Restore(float? score, int signal)
    {
        if (score == null || float.IsNaN(score.Value) || float.IsInfinity(score.Value)) return null;
        UncertaintySignal kind = Enum.IsDefined(typeof(UncertaintySignal), signal) ? (UncertaintySignal)signal : UncertaintySignal.None;
        return ImageUncertainty.Of(score.Value, kind);
    }
}

/// <summary>Serialisable copy of a <see cref="SegAiSuggestion"/> (points flattened to x,y pairs).</summary>
internal sealed class SuggestionDto
{
    public string Rel { get; set; } = "";
    public int ClassIndex { get; set; }
    public float[]? Outer { get; set; }
    public List<float[]>? Holes { get; set; }
    public float? Score { get; set; }
    public string? Label { get; set; }
    public string? ShapeType { get; set; }
    public string? Model { get; set; }

    public static SuggestionDto From(SegAiSuggestion s)
    {
        return new SuggestionDto
        {
            Rel = s.Rel,
            ClassIndex = s.ClassIndex,
            Outer = Flatten(s.Outer),
            Holes = s.Holes.Select(Flatten).ToList(),
            Score = s.Score,
            Label = s.Label,
            ShapeType = s.ShapeType,
            Model = s.Model,
        };
    }

    /// <summary>Throws on a malformed record; callers treat that as a cache miss.</summary>
    public SegAiSuggestion ToSuggestion()
    {
        if (Outer == null || Holes == null) throw new InvalidDataException("Incomplete suggestion.");
        return new SegAiSuggestion(Rel, ClassIndex, Unflatten(Outer), Holes.Select(Unflatten).ToArray(), Score, Label, ShapeType ?? "polygon", Model);
    }

    public static List<SuggestionDto> FromAll(IReadOnlyList<SegAiSuggestion> suggestions)
    {
        return suggestions.Select(From).ToList();
    }

    public static IReadOnlyList<SegAiSuggestion>? ToAll(List<SuggestionDto>? dtos)
    {
        if (dtos == null) return null;
        var list = new List<SegAiSuggestion>(dtos.Count);
        foreach (SuggestionDto dto in dtos)
        {
            if (dto == null) return null;
            list.Add(dto.ToSuggestion());
        }

        return list;
    }

    private static float[] Flatten(PointF[] points)
    {
        var flat = new float[points.Length * 2];
        for (int i = 0; i < points.Length; i++)
        {
            flat[2 * i] = points[i].X;
            flat[2 * i + 1] = points[i].Y;
        }

        return flat;
    }

    private static PointF[] Unflatten(float[] flat)
    {
        if (flat == null || flat.Length % 2 != 0) throw new InvalidDataException("Odd point list.");
        var points = new PointF[flat.Length / 2];
        for (int i = 0; i < points.Length; i++) points[i] = new PointF(flat[2 * i], flat[2 * i + 1]);
        return points;
    }
}

/// <summary>Stored auto-label result of one image (the data of the in-memory entry).</summary>
internal sealed class AutoLabelDiskPayload
{
    public bool HasScores { get; set; }
    public List<SuggestionDto>? Suggestions { get; set; }

    /// <summary>Image uncertainty score (0..1); null in files written before the field existed (unknown).</summary>
    public float? Uncertainty { get; set; }

    /// <summary>The <see cref="UncertaintySignal"/> the score came from.</summary>
    public int UncertaintySignal { get; set; }

    public static AutoLabelDiskPayload From(bool hasScores, IReadOnlyList<SegAiSuggestion> suggestions, ImageUncertainty? uncertainty = null)
    {
        return new AutoLabelDiskPayload
        {
            HasScores = hasScores,
            Suggestions = SuggestionDto.FromAll(suggestions),
            Uncertainty = uncertainty?.Score,
            UncertaintySignal = uncertainty == null ? 0 : (int)uncertainty.Value.Signal,
        };
    }

    /// <summary>The stored uncertainty, or null when the file has none (an older file).</summary>
    public ImageUncertainty? RestoreUncertainty()
    {
        return UncertaintyStore.Restore(Uncertainty, UncertaintySignal);
    }

    /// <summary>The suggestions, or null when the payload is malformed.</summary>
    public IReadOnlyList<SegAiSuggestion>? TryRestore()
    {
        try { return SuggestionDto.ToAll(Suggestions); }
        catch (Exception) { return null; }
    }
}

/// <summary>Stored inspect result of one image (the data of the in-memory entry).</summary>
internal sealed class InspectDiskPayload
{
    public sealed class ClassDto
    {
        public int ClassIndex { get; set; }
        public string Name { get; set; } = "";
        public int Kind { get; set; }
        public int Regions { get; set; }
    }

    public string Path { get; set; } = "";
    public int Verdict { get; set; }
    public int Regions { get; set; }
    public float Score { get; set; }
    public double ElapsedMs { get; set; }
    public string Model { get; set; } = "";
    public long TimestampTicks { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public List<SuggestionDto>? Suggestions { get; set; }
    public List<ClassDto>? Classes { get; set; }

    /// <summary>Image uncertainty score (0..1); null in files written before the field existed (unknown).</summary>
    public float? Uncertainty { get; set; }

    /// <summary>The <see cref="UncertaintySignal"/> the score came from.</summary>
    public int UncertaintySignal { get; set; }

    public static InspectDiskPayload From(InspectResult r)
    {
        return new InspectDiskPayload
        {
            Uncertainty = r.Uncertainty?.Score,
            UncertaintySignal = r.Uncertainty == null ? 0 : (int)r.Uncertainty.Value.Signal,
            Path = r.Path,
            Verdict = (int)r.Verdict,
            Regions = r.Regions,
            Score = r.Score,
            ElapsedMs = r.ElapsedMs,
            Model = r.Model,
            TimestampTicks = r.TimestampUtc.ToUniversalTime().Ticks,
            Width = r.Width,
            Height = r.Height,
            Suggestions = SuggestionDto.FromAll(r.Suggestions),
            Classes = r.Classes?.Select(c => new ClassDto { ClassIndex = c.ClassIndex, Name = c.Name, Kind = (int)c.Kind, Regions = c.Regions }).ToList(),
        };
    }

    /// <summary>The result (FromCache false), or null when the payload is malformed or is not an OK/NG verdict.</summary>
    public InspectResult? TryRestore(string fullPath)
    {
        try
        {
            if (Verdict != (int)InspectVerdict.OK && Verdict != (int)InspectVerdict.NG) return null;
            IReadOnlyList<SegAiSuggestion>? suggestions = SuggestionDto.ToAll(Suggestions);
            if (suggestions == null) return null;
            List<InspectClassCount>? classes = Classes?.Select(c => new InspectClassCount(c.ClassIndex, c.Name, (SegAiClassKind)c.Kind, c.Regions)).ToList();
            return new InspectResult(
                fullPath,
                (InspectVerdict)Verdict,
                Regions,
                Score,
                ElapsedMs,
                Model,
                new DateTime(TimestampTicks, DateTimeKind.Utc),
                null,
                false,
                suggestions,
                Width,
                Height,
                classes,
                UncertaintyStore.Restore(Uncertainty, UncertaintySignal));
        }
        catch (Exception)
        {
            return null;
        }
    }
}
