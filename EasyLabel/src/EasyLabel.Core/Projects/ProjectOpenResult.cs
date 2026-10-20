using EasyLabel.Core.Model;

namespace EasyLabel.Core.Projects;

/// <summary>A named project that was created, opened or saved as. <c>Project.Paths</c> holds the image folder and the data folder.</summary>
public sealed class ProjectOpenResult
{
    public ProjectOpenResult(Project project, ProjectManifest manifest, bool dataFolderRecreated, IReadOnlyList<string> warnings)
    {
        Project = project;
        Manifest = manifest;
        DataFolderRecreated = dataFolderRecreated;
        Warnings = warnings;
    }

    public Project Project { get; }

    public ProjectManifest Manifest { get; }

    public ProjectPaths Paths => Project.Paths;

    /// <summary>The data folder next to the manifest was missing and was created empty (caches, models and runs of the project are gone).</summary>
    public bool DataFolderRecreated { get; }

    /// <summary>Non-fatal problems in English (e.g. the manifest could not be updated); the project is open anyway.</summary>
    public IReadOnlyList<string> Warnings { get; }
}
