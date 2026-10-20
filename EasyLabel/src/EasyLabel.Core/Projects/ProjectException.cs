namespace EasyLabel.Core.Projects;

/// <summary>Why a named-project operation was refused. Localisable: callers map the code to their own text; <see cref="ProjectException.Message"/> is English.</summary>
public enum ProjectErrorCode
{
    /// <summary>The project name breaks <see cref="ProjectNameRules"/> (see <see cref="ProjectException.NameError"/>).</summary>
    InvalidName,

    /// <summary>The target location cannot be used (empty, a file, or inside a folder the operation copies from).</summary>
    InvalidLocation,

    /// <summary>The manifest, data folder or image folder to create already exists. Nothing is overwritten.</summary>
    TargetExists,

    /// <summary>The <c>.easyai</c> manifest file does not exist.</summary>
    ManifestMissing,

    /// <summary>The manifest is not a valid schema 3 project file.</summary>
    ManifestInvalid,

    /// <summary>The image folder the manifest points to does not exist (moved, renamed or on a disconnected drive).</summary>
    ImageRootMissing,

    /// <summary>The image folder and the data folder overlap in a way a project cannot have.</summary>
    InvalidLayout,

    /// <summary>The operation needs a named project, but the project is a legacy image folder (no manifest).</summary>
    NotANamedProject,
}

/// <summary>A named-project operation was refused before or while it ran. Nothing is left half-written.</summary>
public sealed class ProjectException : IOException
{
    public ProjectException(ProjectErrorCode code, string message, string? path = null, ProjectNameError nameError = ProjectNameError.None)
        : base(message)
    {
        Code = code;
        FilePath = path;
        NameError = nameError;
    }

    public ProjectException(ProjectErrorCode code, string message, string? path, Exception inner)
        : base(message, inner)
    {
        Code = code;
        FilePath = path;
    }

    public ProjectErrorCode Code { get; }

    /// <summary>The file or folder the problem is about, or null.</summary>
    public string? FilePath { get; }

    /// <summary>The broken name rule when <see cref="Code"/> is <see cref="ProjectErrorCode.InvalidName"/>; otherwise <see cref="ProjectNameError.None"/>.</summary>
    public ProjectNameError NameError { get; }
}
