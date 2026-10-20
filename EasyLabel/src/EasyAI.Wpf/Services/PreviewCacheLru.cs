using System.Collections.Generic;

namespace EasyAI.Desktop.Services;

/// <summary>UI-free, thread-safe LRU keyed by string; evicts by total bytes and by entry count.</summary>
public sealed class PreviewCacheLru<T> where T : class
{
    private sealed class Entry
    {
        public string Key = string.Empty;
        public T Value = null!;
        public long Bytes;
    }

    private readonly object _gate = new object();
    private readonly Dictionary<string, LinkedListNode<Entry>> _map = new Dictionary<string, LinkedListNode<Entry>>(System.StringComparer.Ordinal);
    private readonly LinkedList<Entry> _order = new LinkedList<Entry>(); // First = most recently used.
    private readonly long _maxBytes;
    private readonly int _maxCount;
    private long _totalBytes;

    public PreviewCacheLru(long maxBytes = 256L * 1024 * 1024, int maxCount = 64)
    {
        _maxBytes = maxBytes < 0 ? 0 : maxBytes;
        _maxCount = maxCount < 0 ? 0 : maxCount;
    }

    public int Count
    {
        get { lock (_gate) { return _map.Count; } }
    }

    public long TotalBytes
    {
        get { lock (_gate) { return _totalBytes; } }
    }

    public bool TryGet(string key, out T value)
    {
        lock (_gate)
        {
            if (_map.TryGetValue(key, out LinkedListNode<Entry>? node))
            {
                _order.Remove(node);
                _order.AddFirst(node);
                value = node.Value.Value;
                return true;
            }
        }

        value = null!;
        return false;
    }

    public void Put(string key, T value, long bytes)
    {
        if (bytes < 0)
        {
            bytes = 0;
        }

        lock (_gate)
        {
            if (_map.TryGetValue(key, out LinkedListNode<Entry>? existing))
            {
                RemoveNode(existing);
            }

            if (bytes > _maxBytes || _maxCount <= 0)
            {
                return;
            }

            LinkedListNode<Entry> node = _order.AddFirst(new Entry { Key = key, Value = value, Bytes = bytes });
            _map[key] = node;
            _totalBytes += bytes;

            while ((_totalBytes > _maxBytes || _map.Count > _maxCount) && _order.Last != null)
            {
                RemoveNode(_order.Last);
            }
        }
    }

    /// <summary>Removes every entry whose key starts with <paramref name="prefix"/>; returns the number removed.</summary>
    public int RemoveByPrefix(string prefix)
    {
        int removed = 0;
        lock (_gate)
        {
            LinkedListNode<Entry>? node = _order.First;
            while (node != null)
            {
                LinkedListNode<Entry>? next = node.Next;
                if (node.Value.Key.StartsWith(prefix, System.StringComparison.Ordinal))
                {
                    RemoveNode(node);
                    removed++;
                }

                node = next;
            }
        }

        return removed;
    }

    public void Clear()
    {
        lock (_gate)
        {
            _map.Clear();
            _order.Clear();
            _totalBytes = 0;
        }
    }

    private void RemoveNode(LinkedListNode<Entry> node)
    {
        _order.Remove(node);
        _map.Remove(node.Value.Key);
        _totalBytes -= node.Value.Bytes;
    }
}
