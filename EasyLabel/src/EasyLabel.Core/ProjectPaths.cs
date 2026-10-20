using System.Globalization;
using EasyLabel.Core.Io;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace EasyLabel.Core;

/// <summary>
/// Every path of one project, with the image root and the metadata folder as two independent values. The image root holds the images,
/// the LabelMe JSON beside them and <c>classes.txt</c>; the metadata folder holds <c>project.json</c>, <c>meta/</c>, <c>recovery/</c>,
/// <c>cache/</c>, <c>models/</c>, <c>runs/</c>, <c>agent-memory.md</c>, <c>agent-outcomes.jsonl</c> and <c>inspect.json</c>. A relative image
/// path always stays relative to <see cref="ImageRoot"/>. <see cref="Legacy"/> gives the original layout (<c>&lt;imageRoot&gt;\.easyai</c>);
/// the static <see cref="ProjectLayout"/> members are thin wrappers over it. Immutable.
/// </summary>
public sealed class ProjectPaths
{
    /// <param name="imageRoot">Folder of the images (relative image paths and LabelMe JSON live here).</param>
    /// <param name="metaDir">Metadata folder; may be inside or outside <paramref name="imageRoot"/>.</param>
    public ProjectPaths(string imageRoot, string metaDir)
    {
        if (string.IsNullOrWhiteSpace(imageRoot)) throw new ArgumentException("Project folder is required.", nameof(imageRoot));
        if (string.IsNullOrWhiteSpace(metaDir)) throw new ArgumentException("Metadata folder is required.", nameof(metaDir));
        ImageRoot = Path.GetFullPath(imageRoot);
        MetaDir = Path.GetFullPath(metaDir);

        // The metadata folder may sit inside the image root (a sub-folder) but never be, or contain, the image root: scanners would
        // treat the whole project as metadata and writers would mix images with caches.
        string rootTrim = ImageRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string metaTrim = MetaDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.Equals(rootTrim, metaTrim, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The metadata folder must not be the image folder.", nameof(metaDir));
        if (IsInside(metaTrim, rootTrim))
            throw new ArgumentException("The image folder must not be inside the metadata folder.", nameof(imageRoot));
    }

    /// <summary>The original layout: metadata in <c>&lt;imageRoot&gt;\.easyai</c>.</summary>
    public static ProjectPaths Legacy(string imageRoot)
    {
        if (string.IsNullOrWhiteSpace(imageRoot)) throw new ArgumentException("Project folder is required.", nameof(imageRoot));
        string root = Path.GetFullPath(imageRoot);
        return new ProjectPaths(root, Path.Combine(root, ProjectLayout.MetaDirName));
    }

    public string ImageRoot { get; }

    public string MetaDir { get; }

    /// <summary>True for the original <c>&lt;imageRoot&gt;\.easyai</c> placement.</summary>
    public bool IsLegacyPlacement
    {
        get { return string.Equals(MetaDir, Path.Combine(ImageRoot, ProjectLayout.MetaDirName), StringComparison.OrdinalIgnoreCase); }
    }

    public string ProjectFile => Path.Combine(MetaDir, "project.json");

    /// <summary>The <c>meta</c> folder (EasyLabel sidecars).</summary>
    public string MetaSubdir => Path.Combine(MetaDir, "meta");

    public string RecoveryDir => Path.Combine(MetaDir, "recovery");

    public string CacheDir => Path.Combine(MetaDir, "cache");

    public string ModelsDir => Path.Combine(MetaDir, "models");

    public string RunsDir => Path.Combine(MetaDir, "runs");

    /// <summary>Folder of the Ultralytics training runs.</summary>
    public string UltraDir => Path.Combine(MetaDir, "ultra");

    /// <summary>Folder of the OK/NG classifier models (<c>.eaicls</c> files) and its feature cache (<c>.easyai/classify</c>).</summary>
    public string ClassifyDir => Path.Combine(MetaDir, "classify");

    /// <summary>Segment AI settings file (class table, ...).</summary>
    public string AdvancedFile => Path.Combine(MetaDir, "advanced.json");

    /// <summary>
    /// The class list of the project: <c>&lt;imageRoot&gt;\classes.txt</c> for the legacy placement (as always), <c>&lt;metaDir&gt;\classes.txt</c>
    /// for a named project (so a read-only image folder is never written to).
    /// </summary>
    public string ClassesFile
    {
        get { return IsLegacyPlacement ? Path.Combine(ImageRoot, "classes.txt") : Path.Combine(MetaDir, "classes.txt"); }
    }

    public string AgentMemoryFile => Path.Combine(MetaDir, "agent-memory.md");

    public string AgentOutcomesFile => Path.Combine(MetaDir, "agent-outcomes.jsonl");

    /// <summary>Words the user's prompts use for classes of this project (<c>.easyai/vocabulary.json</c>), see <c>ProjectVocabulary</c>.</summary>
    public string VocabularyFile => Path.Combine(MetaDir, "vocabulary.json");

    /// <summary>Chat history of the labelling agent (user view only, never read into a prompt), see <c>AgentChatLog</c>.</summary>
    public string AgentChatFile => Path.Combine(MetaDir, "agent-chat.jsonl");

    public string InspectFile => Path.Combine(MetaDir, "inspect.json");

    /// <summary>Former <c>.easylabel</c> folder (always beside the images; read only by the migration).</summary>
    public string LegacyMetaDir => Path.Combine(ImageRoot, ProjectLayout.LegacyMetaDirName);

    /// <summary>Former <c>seg</c> folder inside the metadata folder (read only by the migration).</summary>
    public string LegacySegDir => Path.Combine(MetaDir, ProjectLayout.LegacySegDirName);

    /// <summary>
    /// Normalizes an image path (relative to the image root, or absolute inside it) to a '/' separated relative path.
    /// Rejects empty, drive-qualified, '.' and '..' paths and paths outside the image root.
    /// </summary>
    public string NormalizeRel(string imagePathOrRel)
    {
        if (string.IsNullOrWhiteSpace(imagePathOrRel)) throw new ArgumentException("Image path is required.", nameof(imagePathOrRel));
        string rel = imagePathOrRel;
        if (Path.IsPathRooted(rel)) rel = Path.GetRelativePath(ImageRoot, Path.GetFullPath(rel));

        rel = rel.Replace('\\', '/');
        if (rel.StartsWith('/') || rel.Contains(':')) throw new ArgumentException("Image path must be relative to the project.", nameof(imagePathOrRel));
        string[] parts = rel.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts.Any(p => p == ".." || p == ".")) throw new ArgumentException("Image path must stay inside the project.", nameof(imagePathOrRel));
        return string.Join("/", parts);
    }

    /// <summary>EasyLabel sidecar of an image (<c>meta/&lt;rel without extension&gt;.json</c> in the metadata folder).</summary>
    public string MetaSidecarPath(string imagePathOrRel)
    {
        return Under(MetaSubdir, StripExtension(NormalizeRel(imagePathOrRel)), ".json");
    }

    /// <summary>Same as <see cref="MetaSidecarPath"/> for an already extension-less relative path (e.g. migration of <c>sub/a.easylabel.json</c>).</summary>
    public string MetaSidecarPathForStem(string stemRel)
    {
        return Under(MetaSubdir, NormalizeRel(stemRel), ".json");
    }

    /// <summary>Crash-recovery file of an image (<c>recovery/&lt;rel&gt;.json</c>, the extension of the image is kept).</summary>
    public string RecoveryPath(string imagePathOrRel)
    {
        return Under(RecoveryDir, NormalizeRel(imagePathOrRel), ".json");
    }

    /// <summary>Derived Segment AI data of an image: the region file and the label raster, both under <c>cache</c>.</summary>
    public (string Sidecar, string Labels) CachePaths(string imagePathOrRel)
    {
        string stem = StripExtension(NormalizeRel(imagePathOrRel));
        string cache = CacheDir;
        return (Under(cache, stem, ProjectLayout.SegSidecarSuffix), Under(cache, stem, ProjectLayout.LabelsSuffix));
    }

    /// <summary>Image-root-relative form of a path inside the project, '/' separated; empty for the root itself.</summary>
    public string RelativeOf(string fullPath)
    {
        return ProjectLayout.RelativeOf(ImageRoot, fullPath);
    }

    /// <summary>
    /// True when <paramref name="fullPath"/> lies in a metadata folder that scanners must skip: the metadata folder itself (wherever
    /// it is), <c>.easyai</c>, the legacy <c>.easylabel*</c> folders at the image root, or any <c>*.easyai.data</c> folder.
    /// </summary>
    public bool IsMetadataPath(string fullPath)
    {
        string full = Path.GetFullPath(fullPath);
        if (IsInside(MetaDir, full)) return true;
        return ProjectLayout.IsMetadataPath(Path.GetRelativePath(ImageRoot, full));
    }

    /// <summary>
    /// True when a scanner must not descend into <paramref name="directoryFullPath"/>: a dot-folder, a <c>*.easyai.data</c> folder
    /// or the metadata folder itself.
    /// </summary>
    public bool IsSkippedDirectory(string directoryFullPath)
    {
        string name = Path.GetFileName(directoryFullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (ProjectLayout.IsSkippedFolderName(name)) return true;
        string full = Path.GetFullPath(directoryFullPath).TrimEnd(Path.DirectorySeparatorChar);
        return string.Equals(full, MetaDir.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Creates <c>project.json</c> (schema 2) in the metadata folder when it is missing. An existing file is never touched.</summary>
    public void EnsureProjectFile()
    {
        string file = ProjectFile;
        if (File.Exists(file)) return;
        var doc = new JObject
        {
            ["schema"] = ProjectLayout.CurrentSchema,
            ["createdUtc"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)
        };
        AtomicFile.WriteAllText(file, doc.ToString(Formatting.Indented));
    }

    private static bool IsInside(string folder, string fullPath)
    {
        string prefix = folder.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            || string.Equals(fullPath.TrimEnd(Path.DirectorySeparatorChar), folder.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
    }

    private static string StripExtension(string rel)
    {
        int slash = rel.LastIndexOf('/');
        int dot = rel.LastIndexOf('.');
        return dot > slash + 1 ? rel.Substring(0, dot) : rel;
    }

    private static string Under(string folder, string rel, string suffix)
    {
        string full = Path.GetFullPath(Path.Combine(folder, rel.Replace('/', Path.DirectorySeparatorChar) + suffix));
        string prefix = folder.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Image path escapes the project.", nameof(rel));
        return full;
    }
}
