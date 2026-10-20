using EasyLabel.Core.Model;

namespace EasyLabel.Core.Projects;

/// <summary>
/// Named projects: <c>&lt;Name&gt;.easyai</c> (manifest) + <c>&lt;Name&gt;.easyai.data</c> (metadata) next to it, the images wherever the
/// manifest says. Create, open, touch and Save As. Nothing is ever overwritten; a failed or cancelled operation removes what it created.
/// Synchronous: run <see cref="SaveAs"/> on a worker thread. Legacy image-folder projects keep opening through <see cref="ProjectService"/>.
/// </summary>
public sealed class ProjectManager
{
    private static readonly string[] ImageExtensions = { ".jpg", ".jpeg", ".png", ".bmp" };

    private readonly ProjectService _projects;

    public ProjectManager()
        : this(new ProjectService())
    {
    }

    public ProjectManager(ProjectService projects)
    {
        _projects = projects ?? throw new ArgumentNullException(nameof(projects));
    }

    // ---- names of the three items of a project ----

    /// <summary>Path of the manifest of project <paramref name="name"/> in <paramref name="location"/>: <c>&lt;location&gt;\&lt;Name&gt;.easyai</c>.</summary>
    public static string ManifestPathFor(string location, string name)
    {
        return Path.Combine(Path.GetFullPath(location), ProjectNameRules.Normalize(name) + ProjectManifest.FileExtension);
    }

    /// <summary>Path of the data folder: <c>&lt;location&gt;\&lt;Name&gt;.easyai.data</c>.</summary>
    public static string DataFolderPathFor(string location, string name)
    {
        return Path.Combine(Path.GetFullPath(location), ProjectManifest.DefaultDataDirName(ProjectNameRules.Normalize(name)));
    }

    /// <summary>Path of the images folder a project creates for itself: <c>&lt;location&gt;\&lt;Name&gt;.images</c>.</summary>
    public static string ImagesFolderPathFor(string location, string name)
    {
        return Path.Combine(Path.GetFullPath(location), ProjectNameRules.Normalize(name) + ProjectManifest.ImagesFolderSuffix);
    }

    // ---- create ----

    /// <summary>
    /// Creates a project: the manifest, the data folder (with <c>project.json</c> and an empty <c>classes.txt</c>) and, when
    /// <paramref name="imageRoot"/> is null, an empty <c>&lt;Name&gt;.images</c> folder in <paramref name="location"/>. An existing image
    /// folder is only referenced. The manifest is written last. Throws <see cref="ProjectException"/> when the name is invalid, the location
    /// cannot be used, the image folder is missing, or any of the three items already exists.
    /// </summary>
    public ProjectOpenResult Create(string name, string location, string? imageRoot, ProjectKind kind = ProjectKind.Detect)
    {
        string trimmed = RequireName(name);
        string folder = RequireLocation(location);
        string manifestPath = ManifestPathFor(folder, trimmed);
        string dataDir = DataFolderPathFor(folder, trimmed);
        bool ownImages = string.IsNullOrWhiteSpace(imageRoot);
        string images = ownImages ? ImagesFolderPathFor(folder, trimmed) : TrimEnd(Path.GetFullPath(imageRoot!));
        if (!ownImages && !Directory.Exists(images))
            throw new ProjectException(ProjectErrorCode.ImageRootMissing, "The image folder does not exist: " + images, images);
        RequireFree(manifestPath, dataDir, ownImages ? images : null);
        ProjectPaths paths = BuildPaths(images, dataDir);

        List<string> created = new List<string>();
        bool locationCreated = !Directory.Exists(folder);
        try
        {
            Directory.CreateDirectory(folder);
            Directory.CreateDirectory(dataDir);
            created.Add(dataDir);
            if (ownImages)
            {
                Directory.CreateDirectory(images);
                created.Add(images);
            }

            paths.EnsureProjectFile();
            ProjectKindStore.Write(paths, kind);
            Project project = _projects.Open(paths);
            ProjectManifest manifest = ProjectManifestStore.CreateNew(manifestPath, trimmed, images);
            ProjectManifestStore.Write(manifest);
            project.Manifest = manifest;
            return new ProjectOpenResult(project, manifest, false, Array.Empty<string>());
        }
        catch
        {
            Cleanup(created, locationCreated ? folder : null);
            throw;
        }
    }

    // ---- open ----

    /// <summary>
    /// Opens a project from its <c>.easyai</c> manifest. A missing data folder is created empty and reported in
    /// <see cref="ProjectOpenResult.DataFolderRecreated"/>; a missing image folder throws <see cref="ProjectException"/>
    /// (<see cref="ProjectErrorCode.ImageRootMissing"/>) with the path in the message. Sets <c>lastOpenedUtc</c>; when the manifest cannot
    /// be rewritten (read-only location) the project still opens and the problem is listed in <see cref="ProjectOpenResult.Warnings"/>.
    /// </summary>
    public ProjectOpenResult OpenFile(string manifestPath)
    {
        ProjectManifest manifest = ProjectManifestStore.Read(manifestPath);
        string images = manifest.ImageRootFullPath;
        if (!Directory.Exists(images))
            throw new ProjectException(
                ProjectErrorCode.ImageRootMissing,
                "The image folder of project '" + manifest.Name + "' was not found: " + images + ". Move it back, or edit \"imageRoot\" in " + manifest.ManifestPath + ".",
                images);

        string dataDir = manifest.DataDirFullPath;
        ProjectPaths paths = BuildPaths(images, dataDir);
        if (File.Exists(dataDir))
            throw new ProjectException(ProjectErrorCode.ManifestInvalid, "The data folder of the project is a file: " + dataDir, dataDir);

        List<string> warnings = new List<string>();
        bool recreated = !Directory.Exists(dataDir);
        Directory.CreateDirectory(dataDir);
        if (recreated) warnings.Add("The data folder was missing and has been created empty: " + dataDir);

        Project project = _projects.Open(paths);
        manifest.LastOpenedUtc = DateTime.UtcNow;
        try
        {
            ProjectManifestStore.Write(manifest);
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            warnings.Add("Could not update the project file " + manifest.ManifestPath + ": " + ex.Message);
        }

        project.Manifest = manifest;
        return new ProjectOpenResult(project, manifest, recreated, warnings);
    }

    /// <summary>
    /// Scans the same project again (after a batch edit changed files) and keeps its manifest. Does not touch the manifest file. A legacy
    /// project reloads exactly like <see cref="ProjectService.Open(ProjectPaths)"/>.
    /// </summary>
    public Project Reload(Project project)
    {
        ArgumentNullException.ThrowIfNull(project);
        Project reloaded = _projects.Open(project.Paths);
        reloaded.Manifest = project.Manifest;
        return reloaded;
    }

    // ---- save ----

    /// <summary>
    /// Touches <c>lastOpenedUtc</c> of the project's manifest and writes it back (atomically, unknown fields kept). The labels themselves are
    /// saved by the editor; this only keeps the manifest current. Throws <see cref="ProjectException"/>
    /// (<see cref="ProjectErrorCode.NotANamedProject"/>) for a legacy image-folder project, which has no manifest: use <see cref="SaveAs"/>.
    /// </summary>
    public ProjectManifest SaveManifest(Project project)
    {
        ArgumentNullException.ThrowIfNull(project);
        ProjectManifest manifest = project.Manifest
            ?? throw new ProjectException(ProjectErrorCode.NotANamedProject, "This project is an image folder without a project file; use Save As to create one.", project.Paths.ImageRoot);
        manifest.LastOpenedUtc = DateTime.UtcNow;
        ProjectManifestStore.Write(manifest);
        return manifest;
    }

    /// <summary>
    /// Duplicates <paramref name="project"/> as a new named project in <paramref name="newLocation"/> and opens the copy. Works from a named
    /// or a legacy project; the source is never modified. The data folder is copied whole (only <c>*.tmp</c> files are left out; the
    /// <c>classes.txt</c> of a legacy project is copied into it). With <paramref name="copyImages"/> false the new manifest references the
    /// same image folder; with true the images and their LabelMe JSON files (nothing else) are copied, folders kept, into
    /// <c>&lt;newLocation&gt;\&lt;newName&gt;.images</c>. Never overwrites: throws <see cref="ProjectException"/>
    /// (<see cref="ProjectErrorCode.TargetExists"/>) if the manifest, data folder or images folder exists. The manifest is written last; on
    /// cancellation (<see cref="OperationCanceledException"/>) or any failure the folders created by this call are removed and no manifest exists.
    /// </summary>
    public ProjectOpenResult SaveAs(Project project, string newName, string newLocation, bool copyImages, IProgress<ProjectCopyProgress>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);
        ProjectPaths source = project.Paths;
        string trimmed = RequireName(newName);
        string folder = RequireLocation(newLocation);
        string manifestPath = ManifestPathFor(folder, trimmed);
        string dataDir = DataFolderPathFor(folder, trimmed);
        string imagesDir = ImagesFolderPathFor(folder, trimmed);
        RequireFree(manifestPath, dataDir, copyImages ? imagesDir : null);

        if (IsInside(source.MetaDir, dataDir))
            throw new ProjectException(ProjectErrorCode.InvalidLocation, "The new project cannot be placed inside the data folder of the project it is copied from: " + folder, folder);
        if (copyImages && IsInside(source.ImageRoot, imagesDir))
            throw new ProjectException(ProjectErrorCode.InvalidLocation, "The copied images cannot be placed inside the image folder they are copied from: " + folder, folder);

        string newImageRoot = copyImages ? imagesDir : source.ImageRoot;
        ProjectPaths target = BuildPaths(newImageRoot, dataDir);

        // Nothing is written before the plan is complete, so a failure while listing leaves nothing behind.
        progress?.Report(new ProjectCopyProgress(ProjectCopyPhase.Scanning, 0, 0, 0, 0, string.Empty));
        List<CopyItem> plan = BuildPlan(source, target, copyImages, cancellationToken);
        int total = plan.Count;
        long totalBytes = plan.Sum(item => item.Length);

        List<string> created = new List<string>();
        bool locationCreated = !Directory.Exists(folder);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(folder);
            Directory.CreateDirectory(dataDir);
            created.Add(dataDir);
            if (copyImages)
            {
                Directory.CreateDirectory(imagesDir);
                created.Add(imagesDir);
            }

            int done = 0;
            long bytes = 0;
            foreach (CopyItem item in plan)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(new ProjectCopyProgress(item.Phase, done, total, bytes, totalBytes, item.Display));
                Directory.CreateDirectory(Path.GetDirectoryName(item.Target)!);
                try
                {
                    File.Copy(item.Source, item.Target, false);
                }
                catch (Exception ex) when (ex is FileNotFoundException || ex is DirectoryNotFoundException)
                {
                    // The file vanished after it was listed (a cache file rewritten by a running job): there is nothing to copy.
                }

                done++;
                bytes += item.Length;
            }

            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new ProjectCopyProgress(ProjectCopyPhase.Finishing, done, total, bytes, totalBytes, string.Empty));
            target.EnsureProjectFile();
            if (project.Kind != ProjectKind.Detect) ProjectKindStore.Write(target, project.Kind); // the data folder copy already carries the key; this guards a missing file
            ProjectManifest manifest = ProjectManifestStore.CreateNew(manifestPath, trimmed, newImageRoot);
            ProjectManifestStore.Write(manifest);
        }
        catch
        {
            Cleanup(created, locationCreated ? folder : null);
            throw;
        }

        ProjectOpenResult result = OpenFile(manifestPath);
        progress?.Report(new ProjectCopyProgress(ProjectCopyPhase.Done, total, total, totalBytes, totalBytes, string.Empty));
        return result;
    }

    // ---- copy plan ----

    private sealed record CopyItem(string Source, string Target, long Length, ProjectCopyPhase Phase, string Display);

    private static List<CopyItem> BuildPlan(ProjectPaths source, ProjectPaths target, bool copyImages, CancellationToken ct)
    {
        List<CopyItem> plan = new List<CopyItem>();
        HashSet<string> targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (Directory.Exists(source.MetaDir))
        {
            foreach (string file in Directory.EnumerateFiles(source.MetaDir, "*", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                ct.ThrowIfCancellationRequested();
                if (file.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) continue;
                string rel = Path.GetRelativePath(source.MetaDir, file);
                AddItem(plan, targets, file, Path.Combine(target.MetaDir, rel), ProjectCopyPhase.CopyingData, rel);
            }
        }

        // A legacy project keeps classes.txt beside the images; the named layout keeps it in the data folder.
        string classes = source.ClassesFile;
        if (File.Exists(classes) && !IsInside(source.MetaDir, classes))
            AddItem(plan, targets, classes, target.ClassesFile, ProjectCopyPhase.CopyingData, "classes.txt");

        if (copyImages)
        {
            foreach (string image in EnumerateImages(source, ct))
            {
                string rel = Path.GetRelativePath(source.ImageRoot, image);
                AddItem(plan, targets, image, Path.Combine(target.ImageRoot, rel), ProjectCopyPhase.CopyingImages, rel);
                string json = Path.ChangeExtension(image, ".json");
                if (File.Exists(json))
                    AddItem(plan, targets, json, Path.Combine(target.ImageRoot, Path.GetRelativePath(source.ImageRoot, json)), ProjectCopyPhase.CopyingImages, Path.GetRelativePath(source.ImageRoot, json));
            }
        }

        return plan;
    }

    private static void AddItem(List<CopyItem> plan, HashSet<string> targets, string source, string target, ProjectCopyPhase phase, string display)
    {
        if (!targets.Add(target)) return; // a.jpg and a.png share a.json: copy it once
        long length = 0;
        try
        {
            length = new FileInfo(source).Length;
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            // Listed but unreadable now: the copy step reports the real error.
        }

        plan.Add(new CopyItem(source, target, length, phase, display));
    }

    // Images of the project in a stable order; metadata folders and dot-folders are never entered.
    private static List<string> EnumerateImages(ProjectPaths paths, CancellationToken ct)
    {
        List<string> found = new List<string>();
        Stack<string> pending = new Stack<string>();
        pending.Push(paths.ImageRoot);
        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            string directory = pending.Pop();
            foreach (string file in Directory.EnumerateFiles(directory))
            {
                if (ImageExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase)) found.Add(file);
            }

            foreach (string sub in Directory.EnumerateDirectories(directory))
            {
                if (!paths.IsSkippedDirectory(sub)) pending.Push(sub);
            }
        }

        found.Sort(StringComparer.OrdinalIgnoreCase);
        return found;
    }

    // ---- checks ----

    private static string RequireName(string name)
    {
        ProjectNameError error = ProjectNameRules.Validate(name);
        if (error != ProjectNameError.None)
            throw new ProjectException(ProjectErrorCode.InvalidName, "The project name is not valid (" + error + "): '" + name + "'.", null, error);
        return ProjectNameRules.Normalize(name);
    }

    private static string RequireLocation(string location)
    {
        if (string.IsNullOrWhiteSpace(location))
            throw new ProjectException(ProjectErrorCode.InvalidLocation, "The project location is required.");
        string full;
        try
        {
            full = TrimEnd(Path.GetFullPath(location));
        }
        catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
        {
            throw new ProjectException(ProjectErrorCode.InvalidLocation, "The project location is not a valid path: " + location, location, ex);
        }

        if (File.Exists(full))
            throw new ProjectException(ProjectErrorCode.InvalidLocation, "The project location is a file, not a folder: " + full, full);
        return full;
    }

    private static void RequireFree(string manifestPath, string dataDir, string? imagesDir)
    {
        foreach (string path in new[] { manifestPath, dataDir, imagesDir ?? string.Empty })
        {
            if (path.Length > 0 && (File.Exists(path) || Directory.Exists(path)))
                throw new ProjectException(ProjectErrorCode.TargetExists, "Already exists, nothing was changed: " + path, path);
        }
    }

    private static ProjectPaths BuildPaths(string imageRoot, string dataDir)
    {
        try
        {
            return new ProjectPaths(imageRoot, dataDir);
        }
        catch (ArgumentException ex)
        {
            throw new ProjectException(ProjectErrorCode.InvalidLayout, "The image folder and the data folder overlap: " + ex.Message, dataDir, ex);
        }
    }

    // Removes only what the failed call created: whole folders it made itself, and the location when it made that too and it is empty.
    private static void Cleanup(List<string> createdFolders, string? createdLocation)
    {
        foreach (string folder in createdFolders)
        {
            try
            {
                if (Directory.Exists(folder)) Directory.Delete(folder, true);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
            }
        }

        if (createdLocation == null) return;
        try
        {
            if (Directory.Exists(createdLocation) && !Directory.EnumerateFileSystemEntries(createdLocation).Any()) Directory.Delete(createdLocation);
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
        }
    }

    private static bool IsInside(string folder, string fullPath)
    {
        string prefix = TrimEnd(folder) + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            || string.Equals(TrimEnd(fullPath), TrimEnd(folder), StringComparison.OrdinalIgnoreCase);
    }

    private static string TrimEnd(string fullPath)
    {
        string root = Path.GetPathRoot(fullPath) ?? string.Empty;
        return fullPath.Length > root.Length ? fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) : fullPath;
    }
}
