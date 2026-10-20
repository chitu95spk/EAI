namespace EasyLabel.Core.Projects;

/// <summary>What a recent-projects entry points at.</summary>
public enum RecentProjectKind
{
    /// <summary>A <c>&lt;Name&gt;.easyai</c> manifest file.</summary>
    Named = 0,

    /// <summary>A legacy image folder (no manifest).</summary>
    Folder = 1,
}

/// <summary>One entry of the recent-projects list. <see cref="Path"/> is the manifest file (Named) or the image folder (Folder).</summary>
public sealed record RecentProject(string Path, string Name, DateTime LastOpenedUtc, RecentProjectKind Kind);

/// <summary>A recent-projects entry plus whether its path exists right now (missing ones are shown greyed, never deleted automatically).</summary>
public readonly record struct RecentProjectStatus(RecentProject Project, bool Exists);

/// <summary>
/// Pure, immutable operations on the recent-projects list: newest first, one entry per path (case-insensitive, trailing separators
/// ignored), at most <see cref="Max"/> entries. Every method returns a new list and never touches the disk.
/// </summary>
public static class RecentProjectList
{
    public const int Max = 10;

    /// <summary>Adds <paramref name="entry"/> at the top; an entry with the same path is replaced (so it moves up and its name and time are updated).</summary>
    public static IReadOnlyList<RecentProject> Add(IReadOnlyList<RecentProject>? list, RecentProject entry)
    {
        if (entry == null || string.IsNullOrWhiteSpace(entry.Path))
        {
            return Normalize(list);
        }

        string path = NormalizePath(entry.Path);
        RecentProject added = entry with { Path = path, Name = NameOrDerived(entry.Name, path, entry.Kind) };

        var result = new List<RecentProject>(Max) { added };
        foreach (RecentProject existing in Normalize(list))
        {
            if (!SamePath(existing.Path, path))
            {
                result.Add(existing);
            }
        }

        return Cap(result);
    }

    /// <summary>The list without the entry at <paramref name="path"/> (same list when there is none).</summary>
    public static IReadOnlyList<RecentProject> Remove(IReadOnlyList<RecentProject>? list, string? path)
    {
        IReadOnlyList<RecentProject> normalized = Normalize(list);
        if (string.IsNullOrWhiteSpace(path))
        {
            return normalized;
        }

        var result = new List<RecentProject>(normalized.Count);
        foreach (RecentProject existing in normalized)
        {
            if (!SamePath(existing.Path, path))
            {
                result.Add(existing);
            }
        }

        return result;
    }

    public static IReadOnlyList<RecentProject> Clear()
    {
        return Array.Empty<RecentProject>();
    }

    /// <summary>Pairs each entry with <c>exists(path)</c>. A throwing <paramref name="exists"/> counts as missing.</summary>
    public static IReadOnlyList<RecentProjectStatus> MarkMissing(IReadOnlyList<RecentProject>? list, Func<string, bool> exists)
    {
        if (exists == null) throw new ArgumentNullException(nameof(exists));

        var result = new List<RecentProjectStatus>();
        foreach (RecentProject entry in Normalize(list))
        {
            bool found;
            try
            {
                found = exists(entry.Path);
            }
            catch (Exception)
            {
                found = false;
            }

            result.Add(new RecentProjectStatus(entry, found));
        }

        return result;
    }

    /// <summary>
    /// Cleans a list read from settings: tolerates null, drops null entries and empty paths, keeps the first of several entries with the
    /// same path (the list is already newest first), fills a blank name from the path and cuts the list to <see cref="Max"/>.
    /// </summary>
    public static IReadOnlyList<RecentProject> Normalize(IReadOnlyList<RecentProject>? list)
    {
        var result = new List<RecentProject>();
        if (list == null)
        {
            return result;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (RecentProject? entry in list)
        {
            if (entry == null || string.IsNullOrWhiteSpace(entry.Path))
            {
                continue;
            }

            string path = NormalizePath(entry.Path);
            if (!seen.Add(path))
            {
                continue;
            }

            result.Add(entry with { Path = path, Name = NameOrDerived(entry.Name, path, entry.Kind) });
            if (result.Count >= Max)
            {
                break;
            }
        }

        return result;
    }

    /// <summary>The path as compared: full path when it can be resolved, without a trailing separator (a drive root keeps its separator).</summary>
    public static string NormalizePath(string path)
    {
        string text = (path ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            return text;
        }

        try
        {
            text = System.IO.Path.GetFullPath(text);
        }
        catch (Exception)
        {
            // Keep the text as typed; it is only used as a comparison key and a display value.
        }

        string root = System.IO.Path.GetPathRoot(text) ?? string.Empty;
        return text.Length > root.Length
            ? text.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar)
            : text;
    }

    public static bool SamePath(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
        {
            return false;
        }

        return string.Equals(NormalizePath(a), NormalizePath(b), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Name shown for an entry without a name: the file name without extension (Named) or the folder name.</summary>
    public static string DerivedName(string path, RecentProjectKind kind)
    {
        string normalized = NormalizePath(path);
        string name = kind == RecentProjectKind.Named
            ? System.IO.Path.GetFileNameWithoutExtension(normalized)
            : System.IO.Path.GetFileName(normalized);
        return string.IsNullOrEmpty(name) ? normalized : name;
    }

    private static string NameOrDerived(string? name, string path, RecentProjectKind kind)
    {
        return string.IsNullOrWhiteSpace(name) ? DerivedName(path, kind) : name.Trim();
    }

    private static IReadOnlyList<RecentProject> Cap(List<RecentProject> list)
    {
        if (list.Count > Max)
        {
            list.RemoveRange(Max, list.Count - Max);
        }

        return list;
    }
}
