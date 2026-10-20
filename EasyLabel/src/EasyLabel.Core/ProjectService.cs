using EasyLabel.Core.Migration;
using EasyLabel.Core.Model;
using EasyLabel.Core.Io;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace EasyLabel.Core;

public sealed class ProjectService
{
    private static readonly string[] ImagePatterns =
    {
        "*.jpg",
        "*.jpeg",
        "*.png",
        "*.bmp"
    };

    private readonly ProjectMigrationService _migration = new();

    /// <summary>Opens an image folder with the original metadata placement (<c>&lt;rootDir&gt;\.easyai</c>).</summary>
    public Project Open(string rootDir)
    {
        if (string.IsNullOrWhiteSpace(rootDir))
        {
            throw new ArgumentException("Project folder is required.", nameof(rootDir));
        }

        return Open(ProjectPaths.Legacy(rootDir));
    }

    /// <summary>Opens a project whose image root and metadata folder are given separately; the metadata folder is created when missing.</summary>
    public Project Open(ProjectPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        string fullRoot = paths.ImageRoot;
        if (!Directory.Exists(fullRoot))
        {
            throw new DirectoryNotFoundException($"Project folder does not exist: {fullRoot}");
        }

        // A legacy project (.easylabel, .easyai/seg, name.easylabel.json) is moved into the single .easyai layout first.
        // Problems are reported, never thrown: the project still opens.
        ProjectMigrationReport migration = _migration.MigrateIfNeeded(paths);

        Directory.CreateDirectory(paths.MetaDir);
        paths.EnsureProjectFile();

        List<string> classes = LoadOrCreateClasses(paths);
        ProjectSettings settings = LoadOrCreateSettings(paths);
        settings.LastOpenedUtc = DateTime.UtcNow;
        SaveSettings(paths, settings);

        IReadOnlyList<ImageItem> images = EnumerateImages(paths);
        return new Project(fullRoot, images, classes, settings, migration, paths);
    }

    private static IReadOnlyList<ImageItem> EnumerateImages(ProjectPaths paths)
    {
        List<ImageItem> images = new();

        foreach (string pattern in ImagePatterns)
        {
            foreach (string imagePath in EnumerateFilesSkippingDotDirectories(paths, pattern))
            {
                string jsonPath = Path.ChangeExtension(imagePath, ".json");
                string sidecarPath = paths.MetaSidecarPath(imagePath);

                images.Add(new ImageItem(
                    imagePath,
                    jsonPath,
                    sidecarPath,
                    ReadReviewStatus(sidecarPath),
                    File.Exists(jsonPath)));
            }
        }

        return images
            .OrderBy(image => image.ImagePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    // Recursive scan that never descends into child directories whose name starts with '.'
    // (e.g. .easylabel, .easyai), into '*.easyai.data' folders, or into the metadata folder wherever it lives;
    // the root itself may be dot-named.
    private static IEnumerable<string> EnumerateFilesSkippingDotDirectories(ProjectPaths paths, string pattern)
    {
        Stack<string> pending = new();
        pending.Push(paths.ImageRoot);
        while (pending.Count > 0)
        {
            string current = pending.Pop();
            foreach (string file in Directory.EnumerateFiles(current, pattern, SearchOption.TopDirectoryOnly))
            {
                yield return file;
            }

            List<string> children;
            try
            {
                children = Directory.EnumerateDirectories(current, "*", SearchOption.TopDirectoryOnly).ToList();
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException || ex is IOException)
            {
                continue;
            }

            foreach (string child in children)
            {
                if (!paths.IsSkippedDirectory(child))
                {
                    pending.Push(child);
                }
            }
        }
    }

    private static List<string> LoadOrCreateClasses(ProjectPaths paths)
    {
        string classesPath = paths.ClassesFile;
        if (!File.Exists(classesPath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(classesPath)!);
            File.WriteAllText(classesPath, string.Empty);
            return new List<string>();
        }

        return File.ReadAllLines(classesPath)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .ToList();
    }

    // .easyai/project.json is shared with Segment AI (schema, createdUtc, migratedFrom, ...): only the two settings keys are
    // read and written here, every other field is kept as it is.
    private ProjectSettings LoadOrCreateSettings(ProjectPaths paths)
    {
        string settingsPath = paths.ProjectFile;
        if (!File.Exists(settingsPath))
        {
            ProjectSettings created = new();
            SaveSettings(paths, created);
            return created;
        }

        try
        {
            ProjectSettings? settings = JsonConvert.DeserializeObject<ProjectSettings>(File.ReadAllText(settingsPath));
            return settings ?? new ProjectSettings();
        }
        catch (JsonException)
        {
            return new ProjectSettings();
        }
    }

    private void SaveSettings(ProjectPaths paths, ProjectSettings settings)
    {
        string settingsPath = paths.ProjectFile;
        JObject document = new();
        if (File.Exists(settingsPath))
        {
            try
            {
                document = JObject.Parse(File.ReadAllText(settingsPath));
            }
            catch (JsonException)
            {
                // A damaged project file is replaced by the settings alone; the migration/other writers never produce one.
                document = new JObject();
            }
        }

        JObject values = JObject.FromObject(settings);
        foreach (JProperty property in values.Properties())
        {
            document[property.Name] = property.Value;
        }

        if (document["schema"] == null)
        {
            document["schema"] = ProjectLayout.CurrentSchema;
        }

        AtomicFile.WriteAllText(settingsPath, document.ToString(Formatting.Indented));
    }

    private static string ReadReviewStatus(string sidecarPath)
    {
        if (!File.Exists(sidecarPath))
        {
            return "unlabeled";
        }

        try
        {
            // Same reader as the writers (SidecarIO): the property casing of the file is whatever it writes.
            string? reviewStatus = new SidecarIO().Read(sidecarPath).ReviewStatus;
            return string.IsNullOrWhiteSpace(reviewStatus) ? "unlabeled" : reviewStatus;
        }
        catch (JsonException)
        {
            return "unlabeled";
        }
        catch (IOException)
        {
            return "unlabeled";
        }
    }
}
