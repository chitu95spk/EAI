using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace EasyLabel.Ai.Onnx.Classify;

/// <summary>
/// Disk cache of backbone feature vectors, one little-endian float32 file per key (<c>&lt;SHA-256 of key&gt;.f32</c>). The key holds
/// the image path, its length and last-write time, the backbone SHA-256 and the long side, so an edited image or another backbone
/// is a miss. A missing, unreadable or wrong-sized entry is a miss; the cache never fails a run. Null folder = no disk cache.
/// Entries are not evicted; delete the folder to reclaim space.
/// </summary>
public sealed class FeatureCache
{
    private const string Extension = ".f32";

    private readonly string? _folder;

    public FeatureCache(string? folder)
    {
        _folder = string.IsNullOrWhiteSpace(folder) ? null : folder;
    }

    /// <summary>True when there is a folder to read and write.</summary>
    public bool IsEnabled => _folder != null;

    /// <summary>The key of one image under one backbone and long side.</summary>
    public static string MakeKey(string fullPath, long length, DateTime lastWriteUtc, string backboneSha256, int longSide)
    {
        ArgumentNullException.ThrowIfNull(fullPath);
        ArgumentNullException.ThrowIfNull(backboneSha256);
        return string.Format(CultureInfo.InvariantCulture, "{0}|{1}|{2}|{3}|{4}", fullPath, length, lastWriteUtc.Ticks, backboneSha256, longSide);
    }

    /// <summary>Reads the features of <paramref name="key"/>; false (and null) on a miss.</summary>
    public bool TryGet(string key, out float[]? features)
    {
        features = null;
        string? file = FileOf(key);
        if (file == null || !File.Exists(file)) return false;
        try
        {
            byte[] bytes = File.ReadAllBytes(file);
            if (bytes.Length != ClassifyBackbone.FeatureCount * sizeof(float)) return false;
            var values = new float[ClassifyBackbone.FeatureCount];
            Buffer.BlockCopy(bytes, 0, values, 0, bytes.Length);
            features = values;
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Stores the features of <paramref name="key"/> (replaces an older entry). Write failures are ignored: the cache is optional.</summary>
    public void Put(string key, float[] features)
    {
        ArgumentNullException.ThrowIfNull(features);
        if (features.Length != ClassifyBackbone.FeatureCount) throw new ArgumentException("Feature length does not match the backbone.", nameof(features));
        string? file = FileOf(key);
        if (file == null) return;

        string temp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            var bytes = new byte[features.Length * sizeof(float)];
            Buffer.BlockCopy(features, 0, bytes, 0, bytes.Length);
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, file, overwrite: true);
        }
        catch (IOException)
        {
            TryDelete(temp);
        }
        catch (UnauthorizedAccessException)
        {
            TryDelete(temp);
        }
    }

    private string? FileOf(string key)
    {
        if (_folder == null) return null;
        string name = System.Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();
        return Path.Combine(_folder, name + Extension);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
