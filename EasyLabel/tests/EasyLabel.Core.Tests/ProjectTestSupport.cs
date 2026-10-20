using System.Security.Cryptography;
using EasyLabel.Core.Io;
using EasyLabel.Core.Model;

namespace EasyLabel.Core.Tests;

/// <summary>Scratch folders, fixtures and folder snapshots shared by the named-project tests.</summary>
internal static class ProjectTestSupport
{
    public static string NewFolder(string name)
    {
        string folder = Path.Combine(AppContext.BaseDirectory, "scratch", "proj-" + name + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return Path.GetFullPath(folder);
    }

    public static string Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    /// <summary>A fake image (the Core never decodes it) and, optionally, a LabelMe JSON with one NG rectangle beside it.</summary>
    public static string WriteImage(string folder, string relative, bool withJson, string label = "NG")
    {
        string image = Path.Combine(folder, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(image)!);
        File.WriteAllText(image, "fake-image-" + relative);
        if (withJson)
        {
            LabelMeDocument document = new LabelMeDocument { ImagePath = Path.GetFileName(image), ImageWidth = 100, ImageHeight = 80 };
            document.Shapes.Add(new LabelShape { Label = label, ShapeType = "rectangle", Points = { new[] { 10.0, 20.0 }, new[] { 30.0, 50.0 } } });
            new LabelMeIO().Write(document, Path.ChangeExtension(image, ".json"));
        }

        return image;
    }

    /// <summary>Relative path (lower case, '/') to SHA-256 of every file below <paramref name="folder"/>, plus every folder as "dir/".</summary>
    public static SortedDictionary<string, string> Snapshot(string folder)
    {
        SortedDictionary<string, string> result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (string entry in Directory.EnumerateFileSystemEntries(folder, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(folder, entry).Replace('\\', '/').ToLowerInvariant();
            if (Directory.Exists(entry))
            {
                result[rel + "/"] = string.Empty;
            }
            else
            {
                using FileStream stream = File.OpenRead(entry);
                result[rel] = System.Convert.ToHexString(SHA256.HashData(stream));
            }
        }

        return result;
    }

    public static string[] Names(string folder)
    {
        return Directory.GetFileSystemEntries(folder).Select(p => Path.GetFileName(p)!).OrderBy(n => n, StringComparer.Ordinal).ToArray();
    }
}

/// <summary>Reports on the calling thread at once (Progress&lt;T&gt; posts, which makes assertions racy).</summary>
internal sealed class SyncProgress<T> : IProgress<T>
{
    private readonly Action<T>? _onReport;

    public SyncProgress(Action<T>? onReport = null)
    {
        _onReport = onReport;
    }

    public List<T> Items { get; } = new List<T>();

    public void Report(T value)
    {
        Items.Add(value);
        _onReport?.Invoke(value);
    }
}
