using System.Globalization;
using EasyLabel.Core.Io;
using EasyLabel.Core.Model;

namespace EasyLabel.Core.Import;

/// <summary>What to do when the image (or its JSON) of an imported file already exists in the project.</summary>
public enum LabelMeImportConflictMode
{
    /// <summary>Leave the project file alone and report the import of that file as skipped (default).</summary>
    Skip,

    /// <summary>Overwrite the project image and JSON with the imported ones.</summary>
    Replace,

    /// <summary>Import under a free name (<c>name_1</c>, <c>name_2</c>, ...).</summary>
    Rename,
}

/// <summary>One LabelMe JSON found in the source folder.</summary>
public sealed class LabelMeImportItem
{
    internal LabelMeImportItem(string sourceJson, string sourceRel)
    {
        SourceJson = sourceJson;
        SourceRel = sourceRel;
    }

    public string SourceJson { get; }

    /// <summary>Path of the JSON relative to the chosen folder, with '/'.</summary>
    public string SourceRel { get; }

    /// <summary>Full path of the image that belongs to the JSON, or null when it was not found.</summary>
    public string? SourceImage { get; internal set; }

    /// <summary>Project-relative path the image will get (before a conflict is resolved), '/' separated.</summary>
    public string TargetRel { get; internal set; } = string.Empty;

    /// <summary>Set when the JSON cannot be read; such a file is never imported.</summary>
    public string? Error { get; internal set; }

    public int ShapeCount { get; internal set; }

    public IReadOnlyDictionary<string, int> ShapesByKind { get; internal set; } = new Dictionary<string, int>();

    public IReadOnlyList<string> Labels { get; internal set; } = Array.Empty<string>();

    /// <summary>True when the project already has this image, its JSON, or an image with the same name.</summary>
    public bool Conflict { get; internal set; }

    public bool ImageMissing => SourceImage == null;

    public bool CanImport => Error == null && SourceImage != null;
}

/// <summary>Result of scanning a folder; nothing is written until <see cref="LabelMeFolderImporter.Apply"/>.</summary>
public sealed class LabelMeImportPreview
{
    internal LabelMeImportPreview(string sourceFolder, string projectRoot, List<LabelMeImportItem> items)
    {
        SourceFolder = sourceFolder;
        ProjectRoot = projectRoot;
        Items = items;
    }

    public string SourceFolder { get; }

    public string ProjectRoot { get; }

    public IReadOnlyList<LabelMeImportItem> Items { get; }

    public int JsonFiles => Items.Count;

    public int InvalidJson => Items.Count(i => i.Error != null);

    public int ImagesFound => Items.Count(i => i.Error == null && i.SourceImage != null);

    public int ImagesMissing => Items.Count(i => i.Error == null && i.SourceImage == null);

    public int Conflicts => Items.Count(i => i.CanImport && i.Conflict);

    public int TotalShapes => Items.Where(i => i.Error == null).Sum(i => i.ShapeCount);

    /// <summary>Shape count per lower-case shape_type over the files that can be imported.</summary>
    public IReadOnlyDictionary<string, int> ShapesByKind { get; internal set; } = new Dictionary<string, int>();

    /// <summary>Labels of importable files that classes.txt of the project does not list yet (applied only by Apply).</summary>
    public IReadOnlyList<string> NewLabels { get; internal set; } = Array.Empty<string>();

    /// <summary>Labels of importable files that classes.txt already lists.</summary>
    public IReadOnlyList<string> ExistingLabels { get; internal set; } = Array.Empty<string>();
}

public sealed class LabelMeImportResult
{
    private readonly List<string> _imported = new List<string>();
    private readonly List<string> _reasons = new List<string>();
    private readonly List<string> _classesAdded = new List<string>();

    /// <summary>Project-relative image paths that were written.</summary>
    public IReadOnlyList<string> ImportedImages => _imported;

    public int Imported => _imported.Count;

    public int Skipped { get; internal set; }

    public int Replaced { get; internal set; }

    public int Renamed { get; internal set; }

    public int Failed { get; internal set; }

    /// <summary>True when the run was cancelled before every file was handled.</summary>
    public bool Cancelled { get; internal set; }

    /// <summary>One line per skipped or failed file.</summary>
    public IReadOnlyList<string> Reasons => _reasons;

    /// <summary>Labels appended to classes.txt.</summary>
    public IReadOnlyList<string> ClassesAdded => _classesAdded;

    internal void AddImported(string rel)
    {
        _imported.Add(rel);
    }

    internal void AddReason(string text)
    {
        _reasons.Add(text);
    }

    internal void AddClass(string name)
    {
        _classesAdded.Add(name);
    }
}

public sealed class LabelMeImportProgress
{
    public LabelMeImportProgress(int done, int total, string current)
    {
        Done = done;
        Total = total;
        Current = current;
    }

    public int Done { get; }

    public int Total { get; }

    public string Current { get; }
}

/// <summary>
/// Imports a folder tree of LabelMe JSON files into an EasyLabel project: the image is copied (relative sub-folders are kept) and
/// the JSON is written beside it through <see cref="LabelMeIO"/>, which keeps unknown keys and the embedded image data. Each file is
/// written atomically (temp file then move; a failed JSON removes the image copied for it). Nothing is ever overwritten unless the
/// caller asks for <see cref="LabelMeImportConflictMode.Replace"/>.
/// </summary>
public sealed class LabelMeFolderImporter
{
    private static readonly string[] ImageExtensions = { ".jpg", ".jpeg", ".png", ".bmp" };

    private readonly string _projectRoot;
    private readonly ProjectPaths _paths;
    private readonly LabelMeIO _io = new LabelMeIO();

    /// <summary>Importer for an image folder with the original metadata placement (<c>classes.txt</c> beside the images).</summary>
    public LabelMeFolderImporter(string projectRoot)
    {
        if (string.IsNullOrWhiteSpace(projectRoot))
        {
            throw new ArgumentException("Project folder is required.", nameof(projectRoot));
        }

        _projectRoot = Path.GetFullPath(projectRoot);
        _paths = ProjectPaths.Legacy(_projectRoot);
    }

    /// <summary>Importer for a project with the given paths (images in the image root, <c>classes.txt</c> where <see cref="ProjectPaths.ClassesFile"/> says).</summary>
    public LabelMeFolderImporter(ProjectPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _paths = paths;
        _projectRoot = paths.ImageRoot;
    }

    /// <summary>Scans <paramref name="sourceFolder"/> recursively (dot-folders skipped) and reports what an import would do. Writes nothing.</summary>
    public LabelMeImportPreview Preview(string sourceFolder, CancellationToken cancellationToken = default)
        => Preview(sourceFolder, null, cancellationToken);

    /// <summary>
    /// Same as <see cref="Preview(string, CancellationToken)"/> with an optional image folder. When it is empty, a sibling folder named
    /// "images" (the usual json/images/labels layout, imagePath "..\images\x.png") is also searched for the image.
    /// </summary>
    public LabelMeImportPreview Preview(string sourceFolder, string? imageFolder, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sourceFolder) || !Directory.Exists(sourceFolder))
        {
            throw new DirectoryNotFoundException("The LabelMe folder does not exist.");
        }

        string source = Path.GetFullPath(sourceFolder);
        string? imageRoot = FindImageRoot(source, imageFolder);
        string projectPrefix = _projectRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if ((source.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar).StartsWith(projectPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The LabelMe folder is the project folder or inside it; choose a folder outside the project.");
        }

        List<string> files = new List<string>();
        CollectJson(source, files);
        files.Sort(StringComparer.OrdinalIgnoreCase);

        HashSet<string> existingClasses = new HashSet<string>(ReadClasses(), StringComparer.OrdinalIgnoreCase);
        List<LabelMeImportItem> items = new List<LabelMeImportItem>();
        SortedDictionary<string, int> kinds = new SortedDictionary<string, int>(StringComparer.Ordinal);
        List<string> newLabels = new List<string>();
        List<string> knownLabels = new List<string>();

        foreach (string file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string rel = Path.GetRelativePath(source, file).Replace('\\', '/');
            LabelMeImportItem item = new LabelMeImportItem(file, rel);
            items.Add(item);

            LabelMeDocument document;
            try
            {
                document = _io.Read(file);
            }
            catch (Exception ex) when (ex is Newtonsoft.Json.JsonException || ex is IOException || ex is UnauthorizedAccessException)
            {
                item.Error = "cannot read the JSON (" + ex.Message + ")";
                continue;
            }

            FillShapes(item, document);
            item.SourceImage = ResolveImage(file, document, source, imageRoot);
            if (item.SourceImage == null)
            {
                continue;
            }

            item.TargetRel = TargetRelOf(source, file, item.SourceImage);
            item.Conflict = FindConflict(TargetPath(item.TargetRel)) != null;

            foreach (KeyValuePair<string, int> pair in item.ShapesByKind)
            {
                kinds[pair.Key] = (kinds.TryGetValue(pair.Key, out int current) ? current : 0) + pair.Value;
            }

            foreach (string label in item.Labels)
            {
                List<string> target = existingClasses.Contains(label) ? knownLabels : newLabels;
                if (!newLabels.Contains(label, StringComparer.OrdinalIgnoreCase) && !knownLabels.Contains(label, StringComparer.OrdinalIgnoreCase))
                {
                    target.Add(label);
                }
            }
        }

        return new LabelMeImportPreview(source, _projectRoot, items)
        {
            ShapesByKind = kinds,
            NewLabels = newLabels,
            ExistingLabels = knownLabels,
        };
    }

    /// <summary>
    /// Imports every importable item of <paramref name="preview"/>. Conflicts are evaluated again against the disk at the moment of the
    /// write. classes.txt gets the labels of the files that were really imported, appended at the end.
    /// </summary>
    public LabelMeImportResult Apply(LabelMeImportPreview preview, LabelMeImportConflictMode mode, IProgress<LabelMeImportProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        if (preview == null)
        {
            throw new ArgumentNullException(nameof(preview));
        }

        LabelMeImportResult result = new LabelMeImportResult();
        List<string> importedLabels = new List<string>();
        // Position projects keep oriented boxes and circles only; the other shapes of a file are left out (reported per file).
        bool position = EasyLabel.Core.Projects.ProjectKindExtensions.IsInstanceKind(EasyLabel.Core.Projects.ProjectKindStore.Read(_paths));
        int total = preview.Items.Count;
        for (int index = 0; index < total; index++)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                // Files already written stay (each one is atomic); their labels still go to classes.txt below.
                result.Cancelled = true;
                break;
            }

            LabelMeImportItem item = preview.Items[index];
            progress?.Report(new LabelMeImportProgress(index, total, item.SourceRel));
            if (item.Error != null)
            {
                result.Failed++;
                result.AddReason(item.SourceRel + ": " + item.Error);
                continue;
            }

            if (item.SourceImage == null)
            {
                result.Skipped++;
                result.AddReason(item.SourceRel + ": image not found");
                continue;
            }

            try
            {
                ImportOne(preview, item, mode, result, importedLabels, position);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Any other failure (bad name, long path, invalid JSON content) fails this file only; the classes of the
                // files already written are still added below so the project stays consistent.
                result.Failed++;
                result.AddReason(item.SourceRel + ": " + ex.Message);
            }
        }

        progress?.Report(new LabelMeImportProgress(total, total, string.Empty));
        ExtendClasses(importedLabels, result);
        return result;
    }

    private void ImportOne(LabelMeImportPreview preview, LabelMeImportItem item, LabelMeImportConflictMode mode, LabelMeImportResult result, List<string> importedLabels, bool position)
    {
        string targetImage = TargetPath(item.TargetRel);
        string directory = Path.GetDirectoryName(targetImage)!;
        string extension = Path.GetExtension(targetImage);
        string stem = Path.GetFileNameWithoutExtension(targetImage);
        bool replacing = false;
        bool renamed = false;

        string? conflict = FindConflict(targetImage);
        if (conflict != null)
        {
            if (mode == LabelMeImportConflictMode.Skip)
            {
                result.Skipped++;
                result.AddReason(item.SourceRel + ": " + conflict + " (kept)");
                return;
            }

            if (mode == LabelMeImportConflictMode.Replace)
            {
                if (OtherImageWithStem(directory, stem, extension))
                {
                    result.Skipped++;
                    result.AddReason(item.SourceRel + ": another image with the same name but a different extension exists (kept)");
                    return;
                }

                replacing = true;
            }
            else
            {
                string? free = FreeStem(directory, stem);
                if (free == null)
                {
                    result.Failed++;
                    result.AddReason(item.SourceRel + ": no free file name");
                    return;
                }

                stem = free;
                targetImage = Path.Combine(directory, stem + extension);
                renamed = true;
            }
        }

        string targetJson = Path.Combine(directory, stem + ".json");
        Directory.CreateDirectory(directory);
        string temp = Path.Combine(directory, "." + Path.GetFileName(targetImage) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        bool createdImage = false;
        try
        {
            File.Copy(item.SourceImage!, temp, false);
            File.Move(temp, targetImage, replacing);
            createdImage = !replacing;

            LabelMeDocument document = _io.Read(item.SourceJson);
            if (position && document.Shapes != null)
            {
                int before = document.Shapes.Count;
                document.Shapes = document.Shapes.Where(s => s != null && EasyLabel.Core.Editing.ProjectKindToolRules.IsShapeTypeAllowed(EasyLabel.Core.Projects.ProjectKind.Position, s.ShapeType)).ToList();
                int refusedShapes = before - document.Shapes.Count;
                if (refusedShapes > 0)
                {
                    result.AddReason(item.SourceRel + ": " + refusedShapes.ToString(System.Globalization.CultureInfo.InvariantCulture) + " shape(s) not imported: a Position project keeps only oriented boxes and circles");
                }
            }

            document.ImagePath = Path.GetFileName(targetImage);
            _io.WriteKeepingImageData(document, targetJson, targetImage);
            foreach (LabelShape shape in document.Shapes)
            {
                string label = (shape.Label ?? string.Empty).Trim();
                if (label.Length > 0)
                {
                    importedLabels.Add(label);
                }
            }
        }
        catch
        {
            TryDelete(temp);
            if (createdImage)
            {
                TryDelete(targetImage);
            }

            throw;
        }

        result.AddImported(RelOf(targetImage));
        if (replacing)
        {
            result.Replaced++;
        }

        if (renamed)
        {
            result.Renamed++;
        }
    }

    private void ExtendClasses(List<string> labels, LabelMeImportResult result)
    {
        if (labels.Count == 0)
        {
            return;
        }

        string path = _paths.ClassesFile;
        string text = File.Exists(path) ? File.ReadAllText(path) : string.Empty;
        HashSet<string> existing = new HashSet<string>(
            text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0),
            StringComparer.OrdinalIgnoreCase);
        List<string> added = new List<string>();
        foreach (string label in labels)
        {
            if (existing.Add(label))
            {
                added.Add(label);
            }
        }

        if (added.Count == 0)
        {
            return;
        }

        string prefix = text.Length == 0 || text.EndsWith('\n') ? text : text + Environment.NewLine;
        AtomicFile.WriteAllText(path, prefix + string.Join(Environment.NewLine, added) + Environment.NewLine);
        foreach (string name in added)
        {
            result.AddClass(name);
        }
    }

    private static void FillShapes(LabelMeImportItem item, LabelMeDocument document)
    {
        SortedDictionary<string, int> kinds = new SortedDictionary<string, int>(StringComparer.Ordinal);
        List<string> labels = new List<string>();
        foreach (LabelShape shape in document.Shapes)
        {
            string kind = string.IsNullOrWhiteSpace(shape.ShapeType) ? "polygon" : shape.ShapeType.Trim().ToLowerInvariant();
            kinds[kind] = (kinds.TryGetValue(kind, out int count) ? count : 0) + 1;
            string label = (shape.Label ?? string.Empty).Trim();
            if (label.Length > 0 && !labels.Contains(label, StringComparer.OrdinalIgnoreCase))
            {
                labels.Add(label);
            }
        }

        item.ShapeCount = document.Shapes.Count;
        item.ShapesByKind = kinds;
        item.Labels = labels;
    }

    private static string? FindImageRoot(string source, string? imageFolder)
    {
        if (!string.IsNullOrWhiteSpace(imageFolder))
        {
            return Directory.Exists(imageFolder) ? Path.GetFullPath(imageFolder) : null;
        }

        string? parent = Path.GetDirectoryName(source.TrimEnd(Path.DirectorySeparatorChar));
        string sibling = parent == null ? string.Empty : Path.Combine(parent, "images");
        return sibling.Length > 0 && Directory.Exists(sibling) ? sibling : null;
    }

    private static string? ResolveImage(string jsonPath, LabelMeDocument document, string sourceRoot, string? imageRoot)
    {
        string directory = Path.GetDirectoryName(jsonPath)!;
        List<string> candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(document.ImagePath))
        {
            string normalized = document.ImagePath.Replace('\\', '/').Replace('/', Path.DirectorySeparatorChar);
            candidates.Add(Path.Combine(directory, normalized));
            candidates.Add(Path.Combine(directory, Path.GetFileName(normalized)));
            if (imageRoot != null)
            {
                candidates.Add(Path.Combine(imageRoot, Path.GetFileName(normalized)));
            }
        }

        string stem = Path.GetFileNameWithoutExtension(jsonPath);
        foreach (string extension in ImageExtensions)
        {
            candidates.Add(Path.Combine(directory, stem + extension));
            if (imageRoot != null)
            {
                candidates.Add(Path.Combine(imageRoot, stem + extension));
            }
        }

        foreach (string candidate in candidates)
        {
            try
            {
                string full = Path.GetFullPath(candidate);
                string rootPrefix = Path.GetFullPath(sourceRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                string? imagePrefix = imageRoot == null ? null : imageRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                // An imagePath with an absolute path or '..' must not pull an image from outside the chosen folder or the image folder.
                if ((full.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase)
                        || (imagePrefix != null && full.StartsWith(imagePrefix, StringComparison.OrdinalIgnoreCase)))
                    && ImageExtensions.Contains(Path.GetExtension(full), StringComparer.OrdinalIgnoreCase) && File.Exists(full))
                {
                    return full;
                }
            }
            catch (ArgumentException)
            {
                // An imagePath with invalid characters: try the next candidate.
            }
        }

        return null;
    }

    private static string TargetRelOf(string sourceRoot, string jsonPath, string imagePath)
    {
        string relDirectory = Path.GetRelativePath(sourceRoot, Path.GetDirectoryName(jsonPath)!).Replace('\\', '/');
        string name = Path.GetFileName(imagePath);
        return relDirectory == "." || relDirectory.Length == 0 ? name : relDirectory + "/" + name;
    }

    private string TargetPath(string rel)
    {
        return Path.Combine(_projectRoot, rel.Replace('/', Path.DirectorySeparatorChar));
    }

    private string RelOf(string fullPath)
    {
        return Path.GetRelativePath(_projectRoot, fullPath).Replace('\\', '/');
    }

    /// <summary>Why the target cannot be written without touching project files, or null when it is free.</summary>
    private static string? FindConflict(string targetImage)
    {
        string directory = Path.GetDirectoryName(targetImage)!;
        string stem = Path.GetFileNameWithoutExtension(targetImage);
        if (File.Exists(targetImage))
        {
            return "the image already exists in the project";
        }

        if (OtherImageWithStem(directory, stem, Path.GetExtension(targetImage)))
        {
            return "an image with the same name exists in the project";
        }

        if (File.Exists(Path.Combine(directory, stem + ".json")))
        {
            return "a JSON with the same name exists in the project";
        }

        return null;
    }

    private static bool OtherImageWithStem(string directory, string stem, string extension)
    {
        foreach (string other in ImageExtensions)
        {
            if (!string.Equals(other, extension, StringComparison.OrdinalIgnoreCase) && File.Exists(Path.Combine(directory, stem + other)))
            {
                return true;
            }
        }

        return false;
    }

    private static string? FreeStem(string directory, string stem)
    {
        for (int attempt = 1; attempt < 10000; attempt++)
        {
            string candidate = stem + "_" + attempt.ToString(CultureInfo.InvariantCulture);
            bool taken = File.Exists(Path.Combine(directory, candidate + ".json"));
            foreach (string extension in ImageExtensions)
            {
                taken |= File.Exists(Path.Combine(directory, candidate + extension));
            }

            if (!taken)
            {
                return candidate;
            }
        }

        return null;
    }

    private IReadOnlyList<string> ReadClasses()
    {
        string path = _paths.ClassesFile;
        if (!File.Exists(path))
        {
            return Array.Empty<string>();
        }

        return File.ReadAllLines(path).Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
    }

    private static void CollectJson(string directory, List<string> files)
    {
        foreach (string file in Directory.EnumerateFiles(directory, "*.json"))
        {
            files.Add(file);
        }

        foreach (string sub in Directory.EnumerateDirectories(directory))
        {
            if (!ProjectLayout.IsSkippedFolderName(Path.GetFileName(sub)))
            {
                CollectJson(sub, files);
            }
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
