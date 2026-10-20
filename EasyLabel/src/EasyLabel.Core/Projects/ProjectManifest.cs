using Newtonsoft.Json.Linq;

namespace EasyLabel.Core.Projects;

/// <summary>
/// The <c>&lt;Name&gt;.easyai</c> file of a named project (UTF-8 JSON, schema 3): the name, two timestamps, where the images are and the
/// name of the data folder that sits next to the manifest. Fields this version does not know are kept and written back unchanged.
/// Read and written by <see cref="ProjectManifestStore"/>.
/// </summary>
public sealed class ProjectManifest
{
    public const int CurrentSchema = 3;

    /// <summary>Extension of the manifest file (<c>.easyai</c>).</summary>
    public const string FileExtension = ".easyai";

    /// <summary>Suffix of the data folder name (<c>&lt;Name&gt;.easyai.data</c>).</summary>
    public const string DataFolderSuffix = ProjectLayout.MetaDataFolderSuffix;

    /// <summary>Suffix of the images folder a project creates for itself (<c>&lt;Name&gt;.images</c>).</summary>
    public const string ImagesFolderSuffix = ".images";

    public int Schema { get; set; } = CurrentSchema;

    public string Name { get; set; } = string.Empty;

    public DateTime CreatedUtc { get; set; }

    public DateTime LastOpenedUtc { get; set; }

    /// <summary>The image folder as stored: relative to the manifest folder when possible, otherwise an absolute path. Use <see cref="ImageRootFullPath"/>.</summary>
    public string ImageRoot { get; set; } = string.Empty;

    /// <summary>Name (not path) of the data folder next to the manifest.</summary>
    public string DataDir { get; set; } = string.Empty;

    /// <summary>The user's consent to send this project's images and prompts to an agent provider; null = not decided (the key is then not written).</summary>
    public ProjectAgentConsent? AgentConsent { get; set; }

    /// <summary>Full path of the manifest file this value was read from or will be written to.</summary>
    public string ManifestPath { get; set; } = string.Empty;

    /// <summary>The folder that holds the manifest and the data folder.</summary>
    public string ManifestFolder => Path.GetDirectoryName(Path.GetFullPath(ManifestPath)) ?? string.Empty;

    /// <summary>Full path of the image folder (a relative <see cref="ImageRoot"/> is resolved against the manifest folder).</summary>
    public string ImageRootFullPath => ResolveImageRoot(ManifestFolder, ImageRoot);

    /// <summary>Full path of the data folder.</summary>
    public string DataDirFullPath => Path.GetFullPath(Path.Combine(ManifestFolder, DataDir));

    /// <summary>The paths of the project this manifest describes (image folder + data folder).</summary>
    public ProjectPaths ToPaths()
    {
        return new ProjectPaths(ImageRootFullPath, DataDirFullPath);
    }

    /// <summary>The whole JSON document as it was read, so fields of newer versions survive a rewrite. Null for a manifest that was never read.</summary>
    internal JObject? Raw { get; set; }

    /// <summary>The default data folder name for a project name.</summary>
    public static string DefaultDataDirName(string name)
    {
        return name + DataFolderSuffix;
    }

    /// <summary>
    /// The text to store for an image folder: a path relative to the manifest folder (with '/') when both are on the same drive or share,
    /// otherwise the absolute path.
    /// </summary>
    public static string FormatImageRoot(string manifestFolder, string imageRootFullPath)
    {
        string folder = Path.GetFullPath(manifestFolder);
        string image = Path.GetFullPath(imageRootFullPath);
        string folderRoot = Path.GetPathRoot(folder) ?? string.Empty;
        string imageRoot = Path.GetPathRoot(image) ?? string.Empty;
        if (!string.Equals(folderRoot, imageRoot, StringComparison.OrdinalIgnoreCase)) return TrimEnd(image);

        string relative = Path.GetRelativePath(folder, image);
        if (Path.IsPathRooted(relative)) return TrimEnd(image);
        return relative.Replace('\\', '/');
    }

    /// <summary>Resolves a stored image folder (relative to <paramref name="manifestFolder"/>, or absolute) to a full path without a trailing separator.</summary>
    public static string ResolveImageRoot(string manifestFolder, string stored)
    {
        string text = (stored ?? string.Empty).Trim();
        string full = Path.IsPathRooted(text) ? Path.GetFullPath(text) : Path.GetFullPath(Path.Combine(manifestFolder, text.Replace('/', Path.DirectorySeparatorChar)));
        return TrimEnd(full);
    }

    private static string TrimEnd(string fullPath)
    {
        string root = Path.GetPathRoot(fullPath) ?? string.Empty;
        return fullPath.Length > root.Length ? fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) : fullPath;
    }
}
