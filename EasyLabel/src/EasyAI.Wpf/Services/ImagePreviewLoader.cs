using System.Windows.Media.Imaging;

namespace EasyAI.Desktop.Services;

public sealed record ImagePreviewResult(BitmapSource? Image, int PixelWidth, int PixelHeight, string? Error);

/// <summary>Decodes previews off the UI thread. A new request cancels the previous one.</summary>
public sealed class ImagePreviewLoader : IDisposable
{
    public const int MaxDecodeWidth = 2048;

    private readonly object _gate = new object();
    public const int MaxPrefetchPaths = 6;
    private const int PrefetchParallelism = 2;

    // A 2048 px preview is about 11 MB, so 512 MB keeps the shown image and the prefetched neighbours of both directions.
    private readonly PreviewCacheLru<ImagePreviewResult> _cache = new PreviewCacheLru<ImagePreviewResult>(512L * 1024 * 1024, 96);
    private readonly CancellationTokenSource _lifetime = new CancellationTokenSource();
    private CancellationTokenSource? _current;
    private volatile bool _disposed;
    private int _prefetchRunning;

    /// <summary>Returns null when the request was superseded or cancelled. Never throws for bad files.</summary>
    public async Task<ImagePreviewResult?> LoadAsync(string path)
    {
        CancellationTokenSource cts = new CancellationTokenSource();
        lock (_gate)
        {
            _current?.Cancel();
            _current?.Dispose();
            _current = cts;
        }

        CancellationToken token = cts.Token;
        try
        {
            string? key = BuildKey(path);
            if (key != null && _cache.TryGet(key, out ImagePreviewResult? cached))
            {
                return token.IsCancellationRequested ? null : cached;
            }

            ImagePreviewResult result = await Task.Run(() => Decode(path, token), token).ConfigureAwait(true);
            if (token.IsCancellationRequested)
            {
                return null;
            }

            StoreInCache(key, result);
            return result;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            return token.IsCancellationRequested ? null : new ImagePreviewResult(null, 0, 0, ex.Message);
        }
    }

    /// <summary>
    /// Decodes up to <see cref="MaxPrefetchPaths"/> not-yet-cached paths into the cache on a below-normal background
    /// thread. Does not cancel or touch the current LoadAsync request; cancelled by Dispose; never throws.
    /// </summary>
    public void Prefetch(IEnumerable<string> paths)
    {
        try
        {
            if (_disposed || paths == null)
            {
                return;
            }

            List<KeyValuePair<string, string>> work = new List<KeyValuePair<string, string>>();
            foreach (string path in paths)
            {
                if (work.Count >= MaxPrefetchPaths)
                {
                    break;
                }

                string? key = string.IsNullOrEmpty(path) ? null : BuildKey(path);
                if (key != null && !_cache.TryGet(key, out ImagePreviewResult? _))
                {
                    work.Add(new KeyValuePair<string, string>(path, key));
                }
            }

            if (work.Count == 0)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref _prefetchRunning, 1, 0) != 0)
            {
                return; // one prefetch at a time; the next switch asks again
            }

            CancellationToken token = _lifetime.Token;
            Thread thread = new Thread(() => PrefetchWorker(work, token))
            {
                IsBackground = true,
                Priority = ThreadPriority.BelowNormal,
                Name = "PreviewPrefetch"
            };
            thread.Start();
        }
        catch
        {
            Volatile.Write(ref _prefetchRunning, 0);
        }
    }

    private void PrefetchWorker(List<KeyValuePair<string, string>> work, CancellationToken token)
    {
        try
        {
            // Two images at a time: a 20 MP PNG decodes on one core, so the neighbours of the shown image are ready sooner.
            ParallelOptions options = new ParallelOptions { MaxDegreeOfParallelism = PrefetchParallelism, CancellationToken = token };
            Parallel.ForEach(work, options, item =>
            {
                ImagePreviewResult result = Decode(item.Key, token);
                if (!token.IsCancellationRequested)
                {
                    StoreInCache(item.Value, result);
                }
            });
        }
        catch
        {
            // Cancelled or failed: nothing to report.
        }
        finally
        {
            Volatile.Write(ref _prefetchRunning, 0);
        }
    }

    /// <summary>Drops the cached previews of one file (its pixels were replaced). The key already carries length and write time, so this is belt and braces.</summary>
    public void Invalidate(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        _cache.RemoveByPrefix(path + "|");
    }

    public void Dispose()
    {
        _disposed = true;
        try
        {
            _lifetime.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        lock (_gate)
        {
            _current?.Cancel();
            _current?.Dispose();
            _current = null;
        }

        _cache.Clear();
    }

    /// <summary>Path + length + last write ticks; null when the file cannot be stat'ed (cache skipped).</summary>
    private static string? BuildKey(string path)
    {
        try
        {
            FileInfo info = new FileInfo(path);
            if (!info.Exists)
            {
                return null;
            }

            return path + "|" + info.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + "|" + info.LastWriteTimeUtc.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        catch
        {
            return null;
        }
    }

    private void StoreInCache(string? key, ImagePreviewResult result)
    {
        if (key == null || result.Image == null || _disposed)
        {
            return;
        }

        long bytes = (long)result.Image.PixelWidth * result.Image.PixelHeight * 4L;
        _cache.Put(key, result, bytes);
    }

    private static ImagePreviewResult Decode(string path, CancellationToken token)
    {
        try
        {
            byte[] bytes = File.ReadAllBytes(path);
            token.ThrowIfCancellationRequested();

            int originalWidth;
            int originalHeight;
            using (MemoryStream probe = new MemoryStream(bytes, false))
            {
                BitmapDecoder decoder = BitmapDecoder.Create(
                    probe,
                    BitmapCreateOptions.IgnoreColorProfile | BitmapCreateOptions.DelayCreation,
                    BitmapCacheOption.None);
                originalWidth = decoder.Frames[0].PixelWidth;
                originalHeight = decoder.Frames[0].PixelHeight;
            }

            token.ThrowIfCancellationRequested();

            using MemoryStream stream = new MemoryStream(bytes, false);
            BitmapImage bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            if (originalWidth > MaxDecodeWidth)
            {
                bitmap.DecodePixelWidth = MaxDecodeWidth;
            }

            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
            return new ImagePreviewResult(bitmap, originalWidth, originalHeight, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new ImagePreviewResult(null, 0, 0, ex.Message);
        }
    }
}
