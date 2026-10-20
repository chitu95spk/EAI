using EasyLabel.Core.Migration;

namespace EasyLabel.Core.Model;

public sealed class Project
{
    public Project(
        string rootDir,
        IReadOnlyList<ImageItem> images,
        List<string> classes,
        ProjectSettings settings,
        ProjectMigrationReport? migration = null,
        ProjectPaths? paths = null)
    {
        RootDir = rootDir ?? throw new ArgumentNullException(nameof(rootDir));
        Paths = paths ?? ProjectPaths.Legacy(rootDir);
        Images = images ?? throw new ArgumentNullException(nameof(images));
        Classes = classes ?? throw new ArgumentNullException(nameof(classes));
        Settings = settings ?? throw new ArgumentNullException(nameof(settings));
        Migration = migration;
        Kind = EasyLabel.Core.Projects.ProjectKindStore.Read(Paths);
    }

    /// <summary>What the project labels (<c>projectKind</c> in project.json); <c>Detect</c> when the key is missing. Fixed at creation.</summary>
    public EasyLabel.Core.Projects.ProjectKind Kind { get; }

    /// <summary>The image root (images, LabelMe JSON and classes.txt live here); the metadata folder is <see cref="Paths"/>.MetaDir.</summary>
    public string RootDir { get; }

    /// <summary>Every path of the project: image root and metadata folder as separate values.</summary>
    public ProjectPaths Paths { get; }

    public IReadOnlyList<ImageItem> Images { get; }

    public List<string> Classes { get; }

    public ProjectSettings Settings { get; }

    /// <summary>Report of the legacy-layout migration run by <c>ProjectService.Open</c>; null or <c>Needed == false</c> when the project was already current.</summary>
    public ProjectMigrationReport? Migration { get; }

    /// <summary>The <c>&lt;Name&gt;.easyai</c> manifest of a named project; null for a legacy image-folder project. Set by <c>ProjectManager</c>.</summary>
    public EasyLabel.Core.Projects.ProjectManifest? Manifest { get; internal set; }
}
