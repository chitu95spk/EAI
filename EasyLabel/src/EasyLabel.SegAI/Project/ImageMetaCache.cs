using Newtonsoft.Json;

namespace EasyLabel.SegAI.Project;

/// <summary>
/// Disk cache of decoded image sizes (<c>image-meta.json</c> in the project cache folder). An entry is valid only while
/// the image file length and last-write ticks are unchanged; any problem with the file means "no cache", never an error.
/// Thread-safe. Writes are debounced and atomic (temp file in the same folder, then replace).
/// </summary>
public sealed class ImageMetaCache : IDisposable
{
    public const int CurrentVersion = 1;
    public const string FileName = "image-meta.json";

    private readonly object _gate = new();
    private readonly string _filePath;
    private readonly int _debounceMs;
    private Dictionary<string, Entry>? _entries;
    private Timer? _timer;
    private bool _dirty;
    private bool _disposed;

    private sealed class Entry
    {
        public long Length { get; set; }
        public long WriteTicks { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
    }

    private sealed class FileModel
    {
        public int Version { get; set; }
        public Dictionary<string, Entry>? Entries { get; set; }
    }

    /// <param name="cacheDir">The project cache folder (ProjectPaths.CacheDir); the file is always <c>cacheDir\image-meta.json</c>.</param>
    /// <param name="debounceMs">Delay before a learned size is written; later sizes inside the window share one write.</param>
    public ImageMetaCache(string cacheDir, int debounceMs = 1500)
    {
        _filePath = Path.Combine(cacheDir, FileName);
        _debounceMs = debounceMs < 0 ? 0 : debounceMs;
    }

    /// <summary>The full path of the cache file.</summary>
    public string FilePath => _filePath;

    /// <summary>Cached size when the stored length and write ticks equal the given ones.</summary>
    public bool TryGet(string normalizedRel, long length, long writeTicks, out int width, out int height)
    {
        width = 0;
        height = 0;
        lock (_gate)
        {
            EnsureLoaded();
            if (!_entries!.TryGetValue(normalizedRel, out Entry? entry)) return false;
            if (entry.Length != length || entry.WriteTicks != writeTicks) return false;
            width = entry.Width;
            height = entry.Height;
            return true;
        }
    }

    /// <summary>Stores (or replaces) a size and schedules the debounced write. Invalid sizes are ignored.</summary>
    public void Put(string normalizedRel, long length, long writeTicks, int width, int height)
    {
        if (width <= 0 || height <= 0) return;
        lock (_gate)
        {
            if (_disposed) return;
            EnsureLoaded();
            _entries![normalizedRel] = new Entry { Length = length, WriteTicks = writeTicks, Width = width, Height = height };
            _dirty = true;
            if (_timer == null) _timer = new Timer(OnTimer, null, _debounceMs, Timeout.Infinite);
            else _timer.Change(_debounceMs, Timeout.Infinite);
        }
    }

    /// <summary>Drops the entry of one image (its pixels were replaced) and schedules the debounced write. Returns true when an entry existed.</summary>
    public bool Remove(string normalizedRel)
    {
        lock (_gate)
        {
            if (_disposed) return false;
            EnsureLoaded();
            if (!_entries!.Remove(normalizedRel)) return false;
            _dirty = true;
            if (_timer == null) _timer = new Timer(OnTimer, null, _debounceMs, Timeout.Infinite);
            else _timer.Change(_debounceMs, Timeout.Infinite);
            return true;
        }
    }

    /// <summary>
    /// Cache hit returns the stored size without calling <paramref name="decode"/>; otherwise decodes, stores the result
    /// and returns it. A null decode result (undecodable image) is returned as false and nothing is stored.
    /// </summary>
    public bool GetOrDecode(string normalizedRel, long length, long writeTicks, Func<(int Width, int Height)?> decode, out int width, out int height)
    {
        if (TryGet(normalizedRel, length, writeTicks, out width, out height)) return true;
        (int Width, int Height)? decoded = decode();
        if (decoded == null || decoded.Value.Width <= 0 || decoded.Value.Height <= 0)
        {
            width = 0;
            height = 0;
            return false;
        }

        width = decoded.Value.Width;
        height = decoded.Value.Height;
        Put(normalizedRel, length, writeTicks, width, height);
        return true;
    }

    /// <summary>Writes pending changes now. A failed write is swallowed (the cache is disposable) and retried on the next change.</summary>
    public void Flush()
    {
        lock (_gate)
        {
            _timer?.Change(Timeout.Infinite, Timeout.Infinite);
            if (!_dirty || _entries == null) return;
            if (TryWrite()) _dirty = false;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            if (_dirty && _entries != null && TryWrite()) _dirty = false;
            _disposed = true;
            _timer?.Dispose();
            _timer = null;
        }
    }

    private void OnTimer(object? state)
    {
        lock (_gate)
        {
            if (_disposed || !_dirty || _entries == null) return;
            if (TryWrite()) _dirty = false;
        }
    }

    // Called under _gate.
    private void EnsureLoaded()
    {
        if (_entries != null) return;
        _entries = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!File.Exists(_filePath)) return;
            FileModel? model = JsonConvert.DeserializeObject<FileModel>(File.ReadAllText(_filePath));
            if (model == null || model.Version != CurrentVersion || model.Entries == null) return;
            foreach (KeyValuePair<string, Entry> pair in model.Entries)
            {
                Entry? e = pair.Value;
                if (string.IsNullOrEmpty(pair.Key) || e == null || e.Width <= 0 || e.Height <= 0) continue;
                _entries[pair.Key] = e;
            }
        }
        catch (Exception)
        {
            // corrupt or unreadable: start empty
            _entries.Clear();
        }
    }

    // Called under _gate.
    private bool TryWrite()
    {
        string? temp = null;
        try
        {
            string? dir = Path.GetDirectoryName(_filePath);
            if (string.IsNullOrEmpty(dir)) return false;
            Directory.CreateDirectory(dir);
            var model = new FileModel
            {
                Version = CurrentVersion,
                Entries = new Dictionary<string, Entry>(_entries!, StringComparer.OrdinalIgnoreCase),
            };
            temp = Path.Combine(dir, FileName + "." + Guid.NewGuid().ToString("N") + ".tmp");
            File.WriteAllText(temp, JsonConvert.SerializeObject(model));
            File.Move(temp, _filePath, overwrite: true);
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
}
