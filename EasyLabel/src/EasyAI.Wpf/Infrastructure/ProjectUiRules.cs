using EasyLabel.Core.Projects;

namespace EasyAI.Desktop.Infrastructure;

/// <summary>Why the New Project / Save Project As input cannot be used yet. Order is the order the checks run in.</summary>
public enum ProjectInputError
{
    None = 0,
    NameEmpty,
    NameTooLong,
    NameInvalidCharacter,
    NameTrailingDot,
    NameReserved,
    NameReservedSuffix,
    LocationEmpty,
    LocationInvalid,
    ImageFolderMissing,
    TargetExists,
}

/// <summary>The first problem found plus the path it is about (the existing item, the missing folder), or null.</summary>
public readonly record struct ProjectInputCheck(ProjectInputError Error, string? Path);

/// <summary>A resource key plus the single value its text takes as <c>{0}</c> (null when the text has no placeholder).</summary>
public readonly record struct ProjectMessage(string Key, string? Argument);

/// <summary>UI-free validation of the project dialogs (name, location, image folder, nothing overwritten). Existence tests can be replaced for unit tests.</summary>
public static class NewProjectInputRules
{
    /// <param name="imageFolder">The existing image folder of a new project, or blank for "create &lt;Name&gt;.images".</param>
    /// <param name="imagesFolderMustBeFree">True when <c>&lt;Name&gt;.images</c> is going to be created (new project without an image folder, Save As with copied images).</param>
    public static ProjectInputCheck Check(
        string? name,
        string? location,
        string? imageFolder,
        bool imagesFolderMustBeFree,
        Func<string, bool>? fileExists = null,
        Func<string, bool>? directoryExists = null)
    {
        Func<string, bool> isFile = fileExists ?? File.Exists;
        Func<string, bool> isDirectory = directoryExists ?? Directory.Exists;

        ProjectInputError nameError = FromNameError(ProjectNameRules.Validate(name));
        if (nameError != ProjectInputError.None)
        {
            return new ProjectInputCheck(nameError, null);
        }

        string trimmedLocation = (location ?? string.Empty).Trim();
        if (trimmedLocation.Length == 0)
        {
            return new ProjectInputCheck(ProjectInputError.LocationEmpty, null);
        }

        string fullLocation;
        try
        {
            if (!Path.IsPathRooted(trimmedLocation))
            {
                return new ProjectInputCheck(ProjectInputError.LocationInvalid, trimmedLocation);
            }

            fullLocation = Path.GetFullPath(trimmedLocation);
        }
        catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
        {
            return new ProjectInputCheck(ProjectInputError.LocationInvalid, trimmedLocation);
        }

        if (isFile(fullLocation))
        {
            return new ProjectInputCheck(ProjectInputError.LocationInvalid, fullLocation);
        }

        string images = (imageFolder ?? string.Empty).Trim();
        if (images.Length > 0 && !isDirectory(images))
        {
            return new ProjectInputCheck(ProjectInputError.ImageFolderMissing, images);
        }

        string manifest = ProjectManager.ManifestPathFor(fullLocation, name!);
        string data = ProjectManager.DataFolderPathFor(fullLocation, name!);
        foreach (string target in new[] { manifest, data })
        {
            if (isFile(target) || isDirectory(target))
            {
                return new ProjectInputCheck(ProjectInputError.TargetExists, target);
            }
        }

        if (imagesFolderMustBeFree)
        {
            string imagesDir = ProjectManager.ImagesFolderPathFor(fullLocation, name!);
            if (isFile(imagesDir) || isDirectory(imagesDir))
            {
                return new ProjectInputCheck(ProjectInputError.TargetExists, imagesDir);
            }
        }

        return new ProjectInputCheck(ProjectInputError.None, null);
    }

    public static ProjectInputError FromNameError(ProjectNameError error)
    {
        switch (error)
        {
            case ProjectNameError.None: return ProjectInputError.None;
            case ProjectNameError.Empty: return ProjectInputError.NameEmpty;
            case ProjectNameError.TooLong: return ProjectInputError.NameTooLong;
            case ProjectNameError.InvalidCharacter: return ProjectInputError.NameInvalidCharacter;
            case ProjectNameError.TrailingDotOrSpace: return ProjectInputError.NameTrailingDot;
            case ProjectNameError.ReservedName: return ProjectInputError.NameReserved;
            case ProjectNameError.ReservedSuffix: return ProjectInputError.NameReservedSuffix;
            default: return ProjectInputError.NameInvalidCharacter;
        }
    }
}

/// <summary>Maps project errors (dialog input and <see cref="ProjectException"/> codes) to Strings.resx keys and their one argument. No text here: the caller formats it.</summary>
public static class ProjectMessages
{
    public const string NameEmpty = "ProjErrNameEmpty";
    public const string NameTooLong = "ProjErrNameTooLong";
    public const string NameChars = "ProjErrNameChars";
    public const string NameDot = "ProjErrNameDot";
    public const string NameReserved = "ProjErrNameReserved";
    public const string NameSuffix = "ProjErrNameSuffix";
    public const string LocationEmpty = "ProjErrLocationEmpty";
    public const string LocationInvalid = "ProjErrLocationInvalid";
    public const string ImageMissing = "ProjErrImageMissing";
    public const string TargetExists = "ProjErrTargetExists";
    public const string ManifestMissing = "ProjErrManifestMissing";
    public const string ManifestInvalid = "ProjErrManifestInvalid";
    public const string Layout = "ProjErrLayout";
    public const string NotNamed = "ProjErrNotNamed";
    public const string Generic = "ProjErrGeneric";

    /// <summary>The message of a dialog input problem; null for <see cref="ProjectInputError.None"/>.</summary>
    public static ProjectMessage? For(ProjectInputCheck check)
    {
        switch (check.Error)
        {
            case ProjectInputError.None: return null;
            case ProjectInputError.NameEmpty: return new ProjectMessage(NameEmpty, null);
            case ProjectInputError.NameTooLong: return new ProjectMessage(NameTooLong, ProjectNameRules.MaxLength.ToString(System.Globalization.CultureInfo.InvariantCulture));
            case ProjectInputError.NameInvalidCharacter: return new ProjectMessage(NameChars, null);
            case ProjectInputError.NameTrailingDot: return new ProjectMessage(NameDot, null);
            case ProjectInputError.NameReserved: return new ProjectMessage(NameReserved, null);
            case ProjectInputError.NameReservedSuffix: return new ProjectMessage(NameSuffix, null);
            case ProjectInputError.LocationEmpty: return new ProjectMessage(LocationEmpty, null);
            case ProjectInputError.LocationInvalid: return new ProjectMessage(LocationInvalid, check.Path ?? string.Empty);
            case ProjectInputError.ImageFolderMissing: return new ProjectMessage(ImageMissing, check.Path ?? string.Empty);
            case ProjectInputError.TargetExists: return new ProjectMessage(TargetExists, check.Path ?? string.Empty);
            default: return new ProjectMessage(Generic, check.Path ?? string.Empty);
        }
    }

    /// <summary>The message of a refused named-project operation. The English <see cref="Exception.Message"/> is only the argument of the generic fallback.</summary>
    public static ProjectMessage For(ProjectException exception)
    {
        string path = exception.FilePath ?? string.Empty;
        switch (exception.Code)
        {
            case ProjectErrorCode.InvalidName:
                ProjectMessage? name = For(new ProjectInputCheck(NewProjectInputRules.FromNameError(exception.NameError), null));
                return name ?? new ProjectMessage(NameChars, null);
            case ProjectErrorCode.InvalidLocation: return new ProjectMessage(LocationInvalid, path);
            case ProjectErrorCode.TargetExists: return new ProjectMessage(TargetExists, path);
            case ProjectErrorCode.ManifestMissing: return new ProjectMessage(ManifestMissing, path);
            case ProjectErrorCode.ManifestInvalid: return new ProjectMessage(ManifestInvalid, path.Length > 0 ? path : exception.Message);
            case ProjectErrorCode.ImageRootMissing: return new ProjectMessage(ImageMissing, path);
            case ProjectErrorCode.InvalidLayout: return new ProjectMessage(Layout, null);
            case ProjectErrorCode.NotANamedProject: return new ProjectMessage(NotNamed, null);
            default: return new ProjectMessage(Generic, exception.Message);
        }
    }
}

/// <summary>Window title: the application name plus the project name (named project) or the image folder (legacy project).</summary>
public static class ProjectTitleRules
{
    public const string Separator = " · ";

    public static string BaseTitle(string appName, string? namedProjectName, string? imageRoot)
    {
        if (!string.IsNullOrWhiteSpace(namedProjectName))
        {
            return appName + Separator + namedProjectName!.Trim();
        }

        return string.IsNullOrWhiteSpace(imageRoot) ? appName : appName + Separator + imageRoot;
    }
}

/// <summary>Text of one recent-projects row.</summary>
public static class RecentProjectDisplay
{
    /// <summary>Second line of a row: the folder that holds the project file (named) or the image folder (legacy).</summary>
    public static string SecondaryText(RecentProject entry)
    {
        if (entry.Kind == RecentProjectKind.Named)
        {
            string? folder = Path.GetDirectoryName(entry.Path);
            return string.IsNullOrEmpty(folder) ? entry.Path : folder;
        }

        return entry.Path;
    }
}

/// <summary>Progress of Save Project As as the dialog shows it.</summary>
public static class ProjectCopyProgressRules
{
    public static string PhaseKey(ProjectCopyPhase phase)
    {
        switch (phase)
        {
            case ProjectCopyPhase.Scanning: return "ProjCopyScanning";
            case ProjectCopyPhase.CopyingData: return "ProjCopyData";
            case ProjectCopyPhase.CopyingImages: return "ProjCopyImages";
            case ProjectCopyPhase.Finishing: return "ProjCopyFinishing";
            default: return "ProjCopyDone";
        }
    }

    /// <summary>Done part, 0 to 1 (bytes when known, else files); null while the total is not known yet (scanning).</summary>
    public static double? Fraction(ProjectCopyProgress progress)
    {
        if (progress.Phase == ProjectCopyPhase.Done)
        {
            return 1d;
        }

        double value;
        if (progress.BytesTotal > 0)
        {
            value = (double)progress.BytesDone / progress.BytesTotal;
        }
        else if (progress.FilesTotal > 0)
        {
            value = (double)progress.FilesDone / progress.FilesTotal;
        }
        else
        {
            return progress.Phase == ProjectCopyPhase.Scanning ? (double?)null : 0d;
        }

        return Math.Max(0d, Math.Min(1d, value));
    }
}

/// <summary>Default location and a free default name for the project dialogs.</summary>
public static class ProjectDefaults
{
    public const string DefaultFolderName = "EasyAI Projects";

    /// <summary>The last used location when it is a usable absolute path, else <c>&lt;Documents&gt;\EasyAI Projects</c>.</summary>
    public static string Location(string? lastLocation, string documentsFolder)
    {
        string last = (lastLocation ?? string.Empty).Trim();
        if (last.Length > 0 && Path.IsPathRooted(last))
        {
            return last;
        }

        return Path.Combine(documentsFolder, DefaultFolderName);
    }

    /// <summary>
    /// <paramref name="stem"/>, or <paramref name="stem"/> + " 2", " 3", ... for the first name whose project file, data folder and images
    /// folder are all free in <paramref name="location"/>. Falls back to <paramref name="stem"/> when the location is not a valid path.
    /// </summary>
    public static string FreeName(string stem, string location, Func<string, bool>? exists = null)
    {
        Func<string, bool> taken = exists ?? (path => File.Exists(path) || Directory.Exists(path));
        string baseName = ProjectNameRules.Validate(stem) == ProjectNameError.None ? ProjectNameRules.Normalize(stem) : "Project";
        try
        {
            for (int i = 1; i < 1000; i++)
            {
                string candidate = i == 1 ? baseName : baseName + " " + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (ProjectNameRules.Validate(candidate) != ProjectNameError.None)
                {
                    break;
                }

                if (!taken(ProjectManager.ManifestPathFor(location, candidate))
                    && !taken(ProjectManager.DataFolderPathFor(location, candidate))
                    && !taken(ProjectManager.ImagesFolderPathFor(location, candidate)))
                {
                    return candidate;
                }
            }
        }
        catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
        {
            // An unusable location is reported by the dialog's own validation.
        }

        return baseName;
    }
}
