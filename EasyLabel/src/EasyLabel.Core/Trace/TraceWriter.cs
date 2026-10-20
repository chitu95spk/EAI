using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using EasyLabel.Core.Hardware;
using Newtonsoft.Json;

namespace EasyLabel.Core.Trace;

/// <summary>One image in a trace. <see cref="Tag"/> is e.g. ok/ng; <see cref="Split"/> is train/val or empty.</summary>
public sealed record TraceImageInput(string FullPath, string Tag, string Split);

/// <summary>One CSV row. <see cref="RelativePath"/> uses forward slashes and is relative to the image root.</summary>
public sealed record TraceImageRow(string RelativePath, string Sha256, string Tag, string Split);

public sealed class TraceWriteRequest
{
    /// <summary>One of <c>segment</c>, <c>ultra</c> or <c>classify</c>.</summary>
    public string ModelKind { get; init; } = string.Empty;

    /// <summary>Safe file-name stem; the outputs are <c>{ModelName}_v{n}.trace.json</c> and <c>{ModelName}_v{n}.manifest.csv</c>.</summary>
    public string ModelName { get; init; } = string.Empty;

    /// <summary>The model file that is hashed into the record (must exist).</summary>
    public string ModelFilePath { get; init; } = string.Empty;

    /// <summary>Folder that the image paths are made relative to; every image must lie under it.</summary>
    public string ImageRoot { get; init; } = string.Empty;

    public IReadOnlyList<TraceImageInput> Images { get; init; } = Array.Empty<TraceImageInput>();

    /// <summary>Metric values; NaN and infinite values are dropped.</summary>
    public IReadOnlyDictionary<string, double>? Metrics { get; init; }

    public IReadOnlyDictionary<string, string>? Settings { get; init; }

    /// <summary>Hardware snapshot (e.g. from <c>HardwareProbe</c>); null writes zeros.</summary>
    public HardwareInfo? Hardware { get; init; }

    /// <summary>Defaults to this assembly's version.</summary>
    public string? AppVersion { get; init; }

    /// <summary>Optional free text; blank is omitted.</summary>
    public string? Operator { get; init; }

    /// <summary>Optional free text; blank is omitted.</summary>
    public string? Note { get; init; }

    /// <summary>Defaults to the current UTC time.</summary>
    public DateTime? CreatedUtc { get; init; }
}

public sealed class TraceWriteResult
{
    public string TraceJsonPath { get; init; } = string.Empty;

    public string ManifestCsvPath { get; init; } = string.Empty;

    public int Version { get; init; }

    /// <summary>Lowercase hex SHA-256 of the manifest CSV bytes (UTF-8, LF line endings).</summary>
    public string DatasetHash { get; init; } = string.Empty;

    public int ImageCount { get; init; }
}

/// <summary>
/// Caches image SHA-256 values keyed by full path, valid while length and last-write time are unchanged.
/// Thread-safe. Keep one instance per session to avoid re-hashing a dataset on every export.
/// </summary>
public sealed class TraceHashCache
{
    private readonly object _gate = new object();
    private readonly Dictionary<string, CacheEntry> _entries = new Dictionary<string, CacheEntry>(StringComparer.OrdinalIgnoreCase);
    private int _computations;

    /// <summary>How many files were actually hashed (cache misses), for tests and diagnostics.</summary>
    public int ComputationCount
    {
        get
        {
            lock (_gate)
            {
                return _computations;
            }
        }
    }

    public string GetSha256(string path)
    {
        string full = Path.GetFullPath(path);
        FileInfo info = new FileInfo(full);
        if (!info.Exists)
        {
            throw new FileNotFoundException("File not found: " + full, full);
        }

        long length = info.Length;
        long writeTicks = info.LastWriteTimeUtc.Ticks;
        lock (_gate)
        {
            if (_entries.TryGetValue(full, out CacheEntry? hit) && hit.Length == length && hit.WriteTicksUtc == writeTicks)
            {
                return hit.Sha256;
            }
        }

        string hash;
        using (FileStream stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 65536))
        {
            hash = System.Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        }

        lock (_gate)
        {
            _entries[full] = new CacheEntry(length, writeTicks, hash);
            _computations++;
        }

        return hash;
    }

    private sealed record CacheEntry(long Length, long WriteTicksUtc, string Sha256);
}

/// <summary>
/// Writes the trace record and per-image manifest of one model version. Files are never overwritten: the version
/// <c>n</c> is one above the highest existing <c>{ModelName}_v*.trace.json</c>, and each file is moved into place only
/// if its name is free. The CSV is deterministic (sorted rows, ordinal paths, LF endings), so the same inputs give the
/// same <see cref="TraceWriteResult.DatasetHash"/>.
/// </summary>
public static class TraceWriter
{
    private const string CsvHeader = "path,sha256,tag,split";

    private static readonly Regex NamePattern = new Regex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,63}\z", RegexOptions.CultureInvariant);

    private static readonly string[] Kinds = { "segment", "ultra", "classify" };

    private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);

    public static TraceWriteResult Write(TraceWriteRequest request, string outputDirectory, TraceHashCache? cache = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(outputDirectory))
        {
            throw new ArgumentException("Output folder is required.", nameof(outputDirectory));
        }

        string kind = (request.ModelKind ?? string.Empty).Trim().ToLowerInvariant();
        if (Array.IndexOf(Kinds, kind) < 0)
        {
            throw new ArgumentException("Unknown model kind: " + request.ModelKind, nameof(request));
        }

        if (!NamePattern.IsMatch(request.ModelName ?? string.Empty))
        {
            throw new ArgumentException("Model name must be a safe file-name stem.", nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.ImageRoot) || request.Images == null)
        {
            throw new ArgumentException("Image root and image list are required.", nameof(request));
        }

        TraceHashCache hashes = cache ?? new TraceHashCache();
        string imageRoot = Path.GetFullPath(request.ImageRoot);
        List<TraceImageRow> rows = request.Images
            .Select(img => new TraceImageRow(
                RelativePath(imageRoot, img.FullPath),
                hashes.GetSha256(img.FullPath),
                Clean(img.Tag),
                Clean(img.Split)))
            .OrderBy(r => r.RelativePath, StringComparer.Ordinal)
            .ToList();

        string csv = BuildCsv(rows);
        string datasetHash = Sha256Text(csv);
        string modelHash = hashes.GetSha256(request.ModelFilePath);

        string dir = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(dir);
        int version = NextVersion(dir, request.ModelName);
        string stem = request.ModelName + "_v" + version.ToString(CultureInfo.InvariantCulture);
        string csvName = stem + ".manifest.csv";
        string jsonName = stem + ".trace.json";

        TraceRecord record = new TraceRecord
        {
            CreatedUtc = (request.CreatedUtc ?? DateTime.UtcNow).ToUniversalTime().ToString("o", CultureInfo.InvariantCulture),
            AppVersion = string.IsNullOrWhiteSpace(request.AppVersion) ? DefaultAppVersion() : request.AppVersion!,
            Model = new TraceModel { Kind = kind, Name = request.ModelName, Version = version, Sha256 = modelHash },
            ImageCount = rows.Count,
            DatasetHash = datasetHash,
            ManifestFile = csvName,
            Hardware = TraceHardware.From(request.Hardware),
            Operator = Optional(request.Operator),
            Note = Optional(request.Note),
        };
        CopyMetrics(request.Metrics, record.Metrics);
        CopySettings(request.Settings, record.Settings);

        string csvPath = Path.Combine(dir, csvName);
        string jsonPath = Path.Combine(dir, jsonName);
        WriteNew(csvPath, csv);
        try
        {
            WriteNew(jsonPath, JsonConvert.SerializeObject(record, Formatting.Indented));
        }
        catch
        {
            TryDelete(csvPath);
            throw;
        }

        return new TraceWriteResult
        {
            TraceJsonPath = jsonPath,
            ManifestCsvPath = csvPath,
            Version = version,
            DatasetHash = datasetHash,
            ImageCount = rows.Count,
        };
    }

    /// <summary>The manifest CSV text for these rows (header, then one line per row, LF endings).</summary>
    public static string BuildCsv(IReadOnlyList<TraceImageRow> rows)
    {
        StringBuilder sb = new StringBuilder();
        sb.Append(CsvHeader).Append('\n');
        foreach (TraceImageRow row in rows)
        {
            sb.Append(CsvField(row.RelativePath)).Append(',')
                .Append(row.Sha256).Append(',')
                .Append(CsvField(row.Tag)).Append(',')
                .Append(CsvField(row.Split)).Append('\n');
        }

        return sb.ToString();
    }

    private static string RelativePath(string root, string fullPath)
    {
        string full = Path.GetFullPath(fullPath);
        string rel = Path.GetRelativePath(root, full);
        if (Path.IsPathRooted(rel) || rel == ".." || rel.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new ArgumentException("Image is outside the image root: " + fullPath);
        }

        return rel.Replace('\\', '/');
    }

    private static string CsvField(string value)
    {
        if (value.IndexOfAny(new[] { ',', '"', '\r', '\n' }) < 0)
        {
            return value;
        }

        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    private static string Clean(string? value)
    {
        return (value ?? string.Empty).Trim();
    }

    private static string? Optional(string? value)
    {
        string text = Clean(value);
        return text.Length == 0 ? null : text;
    }

    private static void CopyMetrics(IReadOnlyDictionary<string, double>? source, SortedDictionary<string, double> target)
    {
        if (source == null)
        {
            return;
        }

        foreach (KeyValuePair<string, double> pair in source)
        {
            if (!string.IsNullOrWhiteSpace(pair.Key) && double.IsFinite(pair.Value))
            {
                target[pair.Key.Trim()] = pair.Value;
            }
        }
    }

    private static void CopySettings(IReadOnlyDictionary<string, string>? source, SortedDictionary<string, string> target)
    {
        if (source == null)
        {
            return;
        }

        foreach (KeyValuePair<string, string> pair in source)
        {
            if (!string.IsNullOrWhiteSpace(pair.Key))
            {
                target[pair.Key.Trim()] = Clean(pair.Value);
            }
        }
    }

    private static int NextVersion(string dir, string modelName)
    {
        Regex pattern = new Regex("^" + Regex.Escape(modelName) + @"_v(\d+)\.trace\.json\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        int max = 0;
        foreach (string path in Directory.EnumerateFiles(dir))
        {
            Match match = pattern.Match(Path.GetFileName(path));
            if (match.Success
                && int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int value)
                && value > max)
            {
                max = value;
            }
        }

        return max + 1;
    }

    /// <summary>Writes through a temp file and moves it to a free name; never replaces an existing file.</summary>
    private static void WriteNew(string path, string text)
    {
        string temp = Path.Combine(Path.GetDirectoryName(path)!, "." + Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.WriteAllText(temp, text, Utf8NoBom);
            File.Move(temp, path);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    private static string Sha256Text(string text)
    {
        return System.Convert.ToHexString(SHA256.HashData(Utf8NoBom.GetBytes(text))).ToLowerInvariant();
    }

    private static string DefaultAppVersion()
    {
        return typeof(TraceWriter).Assembly.GetName().Version?.ToString() ?? string.Empty;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
