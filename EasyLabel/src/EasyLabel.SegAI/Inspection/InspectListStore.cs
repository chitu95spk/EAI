using EasyLabel.Core;
using EasyLabel.Core.Io;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace EasyLabel.SegAI.Inspection;

/// <summary>
/// The image list of the Inspect tab, kept per project in <c>.easyai/inspect.json</c> (paths only; results are never stored). The list is
/// independent of the training images: paths may point anywhere. A path inside the project folder is stored relative ('/' separated),
/// any other path as an absolute path. Files that no longer exist stay in the list; the caller shows them as missing.
/// </summary>
public sealed class InspectListStore
{
    public const int Schema = 1;

    private static readonly string[] ImageExtensions = { ".jpg", ".jpeg", ".png", ".bmp" };

    private readonly string _projectRoot;
    private readonly ProjectPaths _paths;

    /// <summary>Store with the original placement (<c>&lt;projectRoot&gt;\.easyai\inspect.json</c>).</summary>
    public InspectListStore(string projectRoot)
        : this(ProjectPaths.Legacy(projectRoot))
    {
    }

    public InspectListStore(ProjectPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _paths = paths;
        _projectRoot = paths.ImageRoot;
    }

    /// <summary>Full path of inspect.json.</summary>
    public string FilePath => _paths.InspectFile;

    /// <summary>True for the image types the Inspect list accepts (jpg, jpeg, png, bmp).</summary>
    public static bool IsImage(string path)
    {
        return !string.IsNullOrEmpty(path) && ImageExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Images under a folder, recursive, folders starting with a dot (.easyai ...) skipped, sorted by path. An unreadable sub folder is
    /// skipped; a missing root gives an empty list.
    /// </summary>
    public static IReadOnlyList<string> ScanFolder(string folder)
    {
        var found = new List<string>();
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return found;
        var pending = new Stack<string>();
        pending.Push(Path.GetFullPath(folder));
        while (pending.Count > 0)
        {
            string directory = pending.Pop();
            List<string> subdirectories;
            List<string> files;
            try
            {
                subdirectories = Directory.EnumerateDirectories(directory).ToList();
                files = Directory.EnumerateFiles(directory).ToList();
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                continue;
            }

            foreach (string subdirectory in subdirectories)
            {
                if (!ProjectLayout.IsSkippedFolderName(Path.GetFileName(subdirectory))) pending.Push(subdirectory);
            }

            foreach (string file in files)
            {
                if (IsImage(file)) found.Add(file);
            }
        }

        found.Sort(StringComparer.OrdinalIgnoreCase);
        return found;
    }

    /// <summary>
    /// <paramref name="existing"/> followed by the new image paths of <paramref name="added"/>: full paths, no duplicate (case
    /// insensitive), non-image files and invalid paths left out, input order kept.
    /// </summary>
    public static IReadOnlyList<string> Merge(IEnumerable<string> existing, IEnumerable<string> added)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(added);
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in existing.Concat(added))
        {
            string? full = TryFull(path);
            if (full == null || !IsImage(full) || !seen.Add(full)) continue;
            result.Add(full);
        }

        return result;
    }

    /// <summary>
    /// The stored list as full paths. A missing file is an empty list.
    /// </summary>
    /// <exception cref="InvalidDataException">The file exists but is not a valid inspect list.</exception>
    public IReadOnlyList<string> Load()
    {
        if (!File.Exists(FilePath)) return new List<string>();
        try
        {
            JObject root = JObject.Parse(File.ReadAllText(FilePath));
            if (root["items"] is not JArray items) throw new InvalidDataException("inspect.json has no item list.");
            var paths = new List<string>();
            foreach (JToken item in items)
            {
                string? stored = null;
                if (item is JObject obj)
                {
                    if (obj["path"] is JValue { Type: JTokenType.String } pathToken) stored = (string?)pathToken;
                    else if (obj["path"] != null && obj["path"]!.Type != JTokenType.Null) throw new InvalidDataException("inspect.json has an item whose path is not text.");
                }

                if (string.IsNullOrWhiteSpace(stored)) continue;
                string combined = Path.IsPathRooted(stored)
                    ? stored
                    : Path.Combine(_projectRoot, stored.Replace('/', Path.DirectorySeparatorChar));
                paths.Add(combined);
            }

            return Merge(paths, Array.Empty<string>());
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("inspect.json is not valid: " + ex.Message, ex);
        }
    }

    /// <summary>Writes the list atomically (full paths are stored relative when inside the project).</summary>
    public void Save(IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var items = new JArray();
        foreach (string path in Merge(paths, Array.Empty<string>()))
        {
            items.Add(new JObject { ["path"] = ToStored(path) });
        }

        var root = new JObject { ["schema"] = Schema, ["items"] = items };
        Directory.CreateDirectory(_paths.MetaDir);
        AtomicFile.WriteAllText(FilePath, root.ToString(Formatting.Indented));
    }

    private string ToStored(string fullPath)
    {
        string prefix = _projectRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return fullPath;
        return Path.GetRelativePath(_projectRoot, fullPath).Replace('\\', '/');
    }

    private static string? TryFull(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
        {
            return null;
        }
    }
}
