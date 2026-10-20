namespace EasyLabel.Core;

/// <summary>
/// Size governance of the project cache folder (<see cref="ProjectPaths.CacheDir"/>). It owns exactly two things inside it:
/// <c>image-meta.json</c> and everything under <c>results\</c>. Any other file of the cache folder is never measured, trimmed or
/// deleted, and nothing outside the cache folder is ever touched. Reparse points (links) are never followed. Every method swallows
/// IO errors (locked or missing files are skipped) and never throws.
/// </summary>
public static class CacheGovernor
{
    public const string CacheFolderName = "cache";
    public const string MetaFileName = "image-meta.json";
    public const string ResultsFolderName = "results";

    private sealed class OwnedFile
    {
        public OwnedFile(string path, long length, DateTime writeUtc)
        {
            Path = path;
            Length = length;
            WriteUtc = writeUtc;
        }

        public string Path { get; }

        public long Length { get; }

        public DateTime WriteUtc { get; }
    }

    /// <summary>Total bytes of the owned files; 0 when the folder fails the safety guard.</summary>
    public static long Measure(string? cacheDir)
    {
        try
        {
            if (!TryGuard(cacheDir, out string root)) return 0;
            long total = 0;
            foreach (OwnedFile file in Collect(root, includeMeta: true)) total += file.Length;
            return total;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    /// <summary>Deletes every owned file (<c>results\**</c> and <c>image-meta.json</c>); returns the bytes actually freed.</summary>
    public static long Clear(string? cacheDir)
    {
        try
        {
            if (!TryGuard(cacheDir, out string root)) return 0;
            long freed = 0;
            foreach (OwnedFile file in Collect(root, includeMeta: true))
            {
                if (TryDelete(file.Path)) freed += file.Length;
            }

            return freed;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    /// <summary>
    /// Deletes the oldest files (by last write time, UTC) of <c>results\**</c> until the total of all owned files is at most
    /// <paramref name="maxBytes"/>. <c>image-meta.json</c> counts towards the total but is never deleted. Returns the bytes freed.
    /// </summary>
    public static long Trim(string? cacheDir, long maxBytes)
    {
        try
        {
            if (!TryGuard(cacheDir, out string root)) return 0;
            if (maxBytes < 0) maxBytes = 0;
            List<OwnedFile> all = Collect(root, includeMeta: true);
            long total = 0;
            foreach (OwnedFile file in all) total += file.Length;
            if (total <= maxBytes) return 0;

            string metaPath = Path.Combine(root, MetaFileName);
            List<OwnedFile> candidates = new List<OwnedFile>();
            foreach (OwnedFile file in all)
            {
                if (!string.Equals(file.Path, metaPath, StringComparison.OrdinalIgnoreCase)) candidates.Add(file);
            }

            candidates.Sort((a, b) =>
            {
                int byTime = a.WriteUtc.CompareTo(b.WriteUtc);
                return byTime != 0 ? byTime : string.CompareOrdinal(a.Path, b.Path);
            });

            long freed = 0;
            foreach (OwnedFile file in candidates)
            {
                if (total <= maxBytes) break;
                if (TryDelete(file.Path))
                {
                    total -= file.Length;
                    freed += file.Length;
                }
            }

            return freed;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    // The folder must exist and be named "cache"; the returned root is the full path without a trailing separator.
    private static bool TryGuard(string? cacheDir, out string root)
    {
        root = string.Empty;
        if (string.IsNullOrWhiteSpace(cacheDir)) return false;
        string full = Path.GetFullPath(cacheDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (full.Length == 0 || !Directory.Exists(full)) return false;
        if (!string.Equals(Path.GetFileName(full), CacheFolderName, StringComparison.OrdinalIgnoreCase)) return false;
        FileAttributes attributes = File.GetAttributes(full);
        if ((attributes & FileAttributes.ReparsePoint) != 0) return false;
        root = full;
        return true;
    }

    private static List<OwnedFile> Collect(string root, bool includeMeta)
    {
        List<OwnedFile> result = new List<OwnedFile>();
        string prefix = root + Path.DirectorySeparatorChar;
        if (includeMeta)
        {
            AddFile(result, Path.Combine(root, MetaFileName), prefix);
        }

        string results = Path.Combine(root, ResultsFolderName);
        if (Directory.Exists(results) && !IsLink(results))
        {
            Walk(results, prefix, result);
        }

        return result;
    }

    private static void Walk(string directory, string prefix, List<OwnedFile> result)
    {
        try
        {
            foreach (string file in Directory.EnumerateFiles(directory))
            {
                AddFile(result, file, prefix);
            }

            foreach (string sub in Directory.EnumerateDirectories(directory))
            {
                if (!IsLink(sub)) Walk(sub, prefix, result);
            }
        }
        catch (Exception)
        {
            // An unreadable folder is skipped.
        }
    }

    private static void AddFile(List<OwnedFile> result, string path, string prefix)
    {
        try
        {
            string full = Path.GetFullPath(path);
            if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return;
            FileInfo info = new FileInfo(full);
            if (!info.Exists || (info.Attributes & FileAttributes.ReparsePoint) != 0) return;
            result.Add(new OwnedFile(full, info.Length, info.LastWriteTimeUtc));
        }
        catch (Exception)
        {
            // A file that cannot be inspected is not counted.
        }
    }

    private static bool IsLink(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception)
        {
            return true;
        }
    }

    private static bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return !File.Exists(path);
        }
        catch (Exception)
        {
            return false;
        }
    }
}
