namespace EasyLabel.Core;

/// <summary>
/// Constants of the project layout (schema 2) and the original (legacy) placement of its paths. Annotations stay in the LabelMe JSON beside
/// the image; everything else lives under one metadata folder: <c>project.json</c>, <c>meta/&lt;rel&gt;.json</c> (EasyLabel sidecar),
/// <c>recovery/&lt;rel&gt;.json</c>, <c>cache/&lt;rel&gt;.seg.json</c> and <c>.labels.png</c> (derived, deletable), <c>models/</c> and <c>runs/</c>.
/// The methods taking a project root are thin wrappers over <see cref="ProjectPaths.Legacy"/> (metadata in <c>&lt;root&gt;\.easyai</c>); code
/// that has a project object must use its <see cref="ProjectPaths"/> instead, because the metadata folder may live elsewhere.
/// </summary>
public static class ProjectLayout
{
    public const string MetaDirName = ".easyai";
    public const string LegacyMetaDirName = ".easylabel";
    public const string LegacySegDirName = "seg";
    public const string LegacySidecarSuffix = ".easylabel.json";
    public const string SegSidecarSuffix = ".seg.json";
    public const string LabelsSuffix = ".labels.png";

    /// <summary>Name suffix of a metadata folder placed next to or inside the images (<c>&lt;Name&gt;.easyai.data</c>); scanners skip it.</summary>
    public const string MetaDataFolderSuffix = ".easyai.data";

    public const int CurrentSchema = 2;

    public static string MetaDir(string projectRoot) => ProjectPaths.Legacy(projectRoot).MetaDir;

    public static string ProjectFile(string projectRoot) => ProjectPaths.Legacy(projectRoot).ProjectFile;

    public static string MetaFolder(string projectRoot) => ProjectPaths.Legacy(projectRoot).MetaSubdir;

    public static string RecoveryDir(string projectRoot) => ProjectPaths.Legacy(projectRoot).RecoveryDir;

    public static string CacheDir(string projectRoot) => ProjectPaths.Legacy(projectRoot).CacheDir;

    public static string ModelsDir(string projectRoot) => ProjectPaths.Legacy(projectRoot).ModelsDir;

    public static string RunsDir(string projectRoot) => ProjectPaths.Legacy(projectRoot).RunsDir;

    /// <summary>Memory of the labelling agent (<c>.easyai/agent-memory.md</c>), see <c>AgentMemoryFile</c>.</summary>
    public static string AgentMemoryFile(string projectRoot) => ProjectPaths.Legacy(projectRoot).AgentMemoryFile;

    /// <summary>Review outcomes of the labelling agent (<c>.easyai/agent-outcomes.jsonl</c>), see <c>AgentOutcomeLog</c>.</summary>
    public static string AgentOutcomesFile(string projectRoot) => ProjectPaths.Legacy(projectRoot).AgentOutcomesFile;

    /// <summary>Image list of the Inspect tab (<c>.easyai/inspect.json</c>); independent of the training image list.</summary>
    public static string InspectFile(string projectRoot) => ProjectPaths.Legacy(projectRoot).InspectFile;

    /// <summary>Former <c>.easylabel</c> folder (read only by the migration).</summary>
    public static string LegacyMetaDir(string projectRoot) => ProjectPaths.Legacy(projectRoot).LegacyMetaDir;

    /// <summary>Former <c>.easyai/seg</c> folder (read only by the migration).</summary>
    public static string LegacySegDir(string projectRoot) => ProjectPaths.Legacy(projectRoot).LegacySegDir;

    /// <summary>
    /// Normalizes an image path (project relative, or absolute inside the project) to a '/' separated relative path.
    /// Rejects empty, drive-qualified, '.' and '..' paths and paths outside the project.
    /// </summary>
    public static string NormalizeRel(string projectRoot, string imagePathOrRel) => ProjectPaths.Legacy(projectRoot).NormalizeRel(imagePathOrRel);

    /// <summary>EasyLabel sidecar of an image (<c>.easyai/meta/&lt;rel without extension&gt;.json</c>).</summary>
    public static string MetaSidecarPath(string projectRoot, string imagePathOrRel) => ProjectPaths.Legacy(projectRoot).MetaSidecarPath(imagePathOrRel);

    /// <summary>Same as <see cref="MetaSidecarPath"/> for an already extension-less relative path (e.g. migration of <c>sub/a.easylabel.json</c>).</summary>
    public static string MetaSidecarPathForStem(string projectRoot, string stemRel) => ProjectPaths.Legacy(projectRoot).MetaSidecarPathForStem(stemRel);

    /// <summary>Crash-recovery file of an image (<c>.easyai/recovery/&lt;rel&gt;.json</c>, the extension of the image is kept).</summary>
    public static string RecoveryPath(string projectRoot, string imagePathOrRel) => ProjectPaths.Legacy(projectRoot).RecoveryPath(imagePathOrRel);

    /// <summary>Derived Segment AI data of an image: the region file and the label raster, both under <c>.easyai/cache</c>.</summary>
    public static (string Sidecar, string Labels) CachePaths(string projectRoot, string imagePathOrRel) => ProjectPaths.Legacy(projectRoot).CachePaths(imagePathOrRel);

    /// <summary>Folder-relative form of a path inside <paramref name="projectRoot"/>, '/' separated; empty for the root itself.</summary>
    public static string RelativeOf(string projectRoot, string fullPath)
    {
        if (string.IsNullOrWhiteSpace(projectRoot)) throw new ArgumentException("Project folder is required.", nameof(projectRoot));
        string rel = Path.GetRelativePath(Path.GetFullPath(projectRoot), Path.GetFullPath(fullPath));
        return rel == "." ? string.Empty : rel.Replace('\\', '/');
    }

    /// <summary>
    /// True when a project-relative path lies in a metadata folder that scanners must skip: <c>.easyai</c>, the legacy
    /// <c>.easylabel*</c> folders (backups now live inside .easyai/backup) and any <c>*.easyai.data</c> folder. Only the first segment is
    /// tested for the first two; the <c>.easyai.data</c> suffix is tested on every segment.
    /// </summary>
    public static bool IsMetadataPath(string relativePath)
    {
        if (string.IsNullOrEmpty(relativePath)) return false;
        string[] segments = relativePath.Replace('\\', '/').TrimStart('/').Split('/');
        string first = segments[0];
        if (first.Equals(MetaDirName, StringComparison.OrdinalIgnoreCase)
            || first.StartsWith(LegacyMetaDirName, StringComparison.OrdinalIgnoreCase)) return true;
        foreach (string segment in segments)
            if (segment.EndsWith(MetaDataFolderSuffix, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>Convenience overload for an absolute path inside <paramref name="projectRoot"/> (legacy placement).</summary>
    public static bool IsMetadataPath(string projectRoot, string fullPath) => ProjectPaths.Legacy(projectRoot).IsMetadataPath(fullPath);

    /// <summary>
    /// True for a folder name that scanners never descend into: any dot-folder (<c>.easyai</c>, <c>.easylabel</c> ...) and any
    /// <c>*.easyai.data</c> folder.
    /// </summary>
    public static bool IsSkippedFolderName(string folderName)
    {
        return folderName.StartsWith('.') || folderName.EndsWith(MetaDataFolderSuffix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Creates <c>.easyai/project.json</c> (schema 2) when it is missing. An existing file is never touched.</summary>
    public static void EnsureProjectFile(string projectRoot) => ProjectPaths.Legacy(projectRoot).EnsureProjectFile();
}
