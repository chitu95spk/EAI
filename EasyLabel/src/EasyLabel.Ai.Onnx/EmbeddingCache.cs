using Microsoft.ML.OnnxRuntime.Tensors;

namespace EasyLabel.Ai.Onnx;

/// <summary>The three SAM2 encoder outputs of one image (private copies, safe to reuse after the encoder run is disposed).</summary>
public sealed class Sam2Embedding
{
    public Sam2Embedding(DenseTensor<float> highRes0, DenseTensor<float> highRes1, DenseTensor<float> imageEmbed)
    {
        HighRes0 = highRes0;
        HighRes1 = highRes1;
        ImageEmbed = imageEmbed;
    }

    public DenseTensor<float> HighRes0 { get; }

    public DenseTensor<float> HighRes1 { get; }

    public DenseTensor<float> ImageEmbed { get; }
}

/// <summary>Thread-safe least-recently-used cache of SAM2 embeddings (default 4 entries).</summary>
public sealed class EmbeddingCache
{
    public const int DefaultCapacity = 4;

    private readonly object _lock = new object();
    private readonly List<KeyValuePair<string, Sam2Embedding>> _items = new List<KeyValuePair<string, Sam2Embedding>>();
    private readonly int _capacity;

    public EmbeddingCache(int capacity = DefaultCapacity)
    {
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
    }

    /// <summary>Key = image cache key + encoder model file name + encoder file size.</summary>
    public static string MakeKey(string imageCacheKey, string modelFileName, long modelFileSize)
        => imageCacheKey + "|" + modelFileName + "|" + modelFileSize.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public int Count
    {
        get { lock (_lock) return _items.Count; }
    }

    /// <summary>Finds an entry and marks it most recently used.</summary>
    public bool TryGet(string key, out Sam2Embedding? embedding)
    {
        lock (_lock)
        {
            int index = _items.FindIndex(i => i.Key == key);
            if (index < 0)
            {
                embedding = null;
                return false;
            }

            KeyValuePair<string, Sam2Embedding> item = _items[index];
            _items.RemoveAt(index);
            _items.Add(item);
            embedding = item.Value;
            return true;
        }
    }

    /// <summary>Adds or replaces an entry as most recently used and evicts the least recently used one beyond the capacity.</summary>
    public void Add(string key, Sam2Embedding embedding)
    {
        lock (_lock)
        {
            _items.RemoveAll(i => i.Key == key);
            _items.Add(new KeyValuePair<string, Sam2Embedding>(key, embedding));
            while (_items.Count > _capacity) _items.RemoveAt(0);
        }
    }

    public void Clear()
    {
        lock (_lock) _items.Clear();
    }
}
