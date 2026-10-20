using System.Globalization;
using EasyLabel.Core.Io;
using EasyLabel.Core.Model;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace EasyLabel.Core.Migration;

/// <summary>
/// Moves a legacy project (.easylabel folder, .easyai/seg, name.easylabel.json sidecars, schema 1 project files) into the single
/// <c>.easyai</c> layout. UI free, non-destructive (old data is renamed, never deleted), idempotent and resumable: existing files in the
/// new layout are never overwritten, a failed step leaves its old data in place and the next run continues.
/// </summary>
public sealed class ProjectMigrationService
{
    private static readonly string[] ImageExtensions = { ".jpg", ".jpeg", ".png", ".bmp" };

    private readonly LabelMeIO _labelMe = new();

    /// <summary>True when the folder still has any part of the legacy layout.</summary>
    public bool IsLegacy(string projectRoot)
    {
        return IsLegacy(ProjectPaths.Legacy(projectRoot));
    }

    /// <summary>Same as <see cref="IsLegacy(string)"/> for a project whose metadata folder is given separately.</summary>
    public bool IsLegacy(ProjectPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (!Directory.Exists(paths.ImageRoot)) return false;
        return Directory.Exists(paths.LegacyMetaDir)
            || Directory.Exists(paths.LegacySegDir)
            || ReadSchema(paths.ProjectFile) is int schema && schema < ProjectLayout.CurrentSchema
            || EnumerateLegacySidecars(paths).Any();
    }

    /// <summary>Migrates when <see cref="IsLegacy(string)"/>; otherwise returns an empty report. Never throws for a damaged legacy file: the problem is listed in the report.</summary>
    public ProjectMigrationReport MigrateIfNeeded(string projectRoot)
    {
        return MigrateIfNeeded(ProjectPaths.Legacy(projectRoot));
    }

    /// <summary>Migrates when <see cref="IsLegacy(ProjectPaths)"/>; the results land in <c>paths.MetaDir</c>, the old data is read from the image root.</summary>
    public ProjectMigrationReport MigrateIfNeeded(ProjectPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var report = new ProjectMigrationReport();
        string root = paths.ImageRoot;
        try
        {
            if (!IsLegacy(paths)) return report;
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            report.Needed = true;
            report.AddError("Could not inspect the project folder: " + ex.Message);
            return report;
        }

        report.Needed = true;
        string stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        string metaDir = paths.MetaDir;
        try
        {
            if (File.Exists(metaDir)) throw new IOException("'" + metaDir + "' is a file, not a folder.");
            Directory.CreateDirectory(metaDir);
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            report.AddError("Could not create " + metaDir + ": " + ex.Message);
            return report;
        }

        string legacyDir = paths.LegacyMetaDir;
        string segDir = paths.LegacySegDir;
        var sources = new List<string>();

        bool recoveryOk = true;
        string legacyRecovery = Path.Combine(legacyDir, "recovery");
        if (Directory.Exists(legacyRecovery))
        {
            sources.Add("easylabel/recovery");
            int copied = 0;
            recoveryOk = CopyTree(legacyRecovery, paths.RecoveryDir, report, () => copied++);
            report.RecoveryFilesCopied += copied;
        }

        bool cacheOk = true;
        if (Directory.Exists(segDir))
        {
            sources.Add("easyai/seg");
            int copied = 0;
            cacheOk = CopyTree(segDir, paths.CacheDir, report, () => copied++);
            report.CacheFilesCopied += copied;
        }

        if (MoveSidecars(paths, BackupDir(metaDir, stamp), report)) sources.Add("easylabel sidecars");

        bool modesOk = !Directory.Exists(segDir) || ConvertModes(root, segDir, report);

        bool projectOk = MergeProjectFile(paths, legacyDir, sources, report);

        if (Directory.Exists(legacyDir) && recoveryOk && projectOk)
            Archive(legacyDir, Path.Combine(BackupDir(metaDir, stamp), "easylabel"), report);
        if (Directory.Exists(segDir) && cacheOk && modesOk)
            Archive(segDir, Path.Combine(BackupDir(metaDir, stamp), "seg"), report);

        return report;
    }

    // ---- steps ----

    /// <summary>Copies every legacy <c>name.easylabel.json</c> into <c>.easyai/meta</c>, then renames the original. Returns true when one was found.</summary>
    private static bool MoveSidecars(ProjectPaths paths, string backupDir, ProjectMigrationReport report)
    {
        bool any = false;
        List<string> files;
        try
        {
            files = EnumerateLegacySidecars(paths).ToList();
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            report.AddError("Could not list the legacy sidecars: " + ex.Message);
            return false;
        }

        foreach (string file in files)
        {
            any = true;
            try
            {
                string rel = paths.RelativeOf(file);
                string stem = rel.Substring(0, rel.Length - ProjectLayout.LegacySidecarSuffix.Length);
                string target = paths.MetaSidecarPathForStem(stem);
                if (File.Exists(target)) report.ExistingKept++;
                else CopyAtomic(file, target);

                string backup = UniqueName(Path.Combine(backupDir, "sidecars", rel.Replace('/', Path.DirectorySeparatorChar)));
                Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                File.Move(file, backup);
                report.SidecarsMoved++;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException)
            {
                report.AddError("Sidecar " + file + ": " + ex.Message);
            }
        }

        return any;
    }

    /// <summary>Writes the old per-image mode (Train/Val of the seg sidecar) into the LabelMe flags. Returns false when a conversion failed.</summary>
    private bool ConvertModes(string root, string segDir, ProjectMigrationReport report)
    {
        bool ok = true;
        List<string> files;
        try
        {
            files = Directory.EnumerateFiles(segDir, "*" + ProjectLayout.SegSidecarSuffix, SearchOption.AllDirectories).ToList();
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            report.AddError("Could not list " + segDir + ": " + ex.Message);
            return false;
        }

        foreach (string file in files)
        {
            try
            {
                byte mode = ReadMode(file);
                if (mode != ImageModeFlags.Train && mode != ImageModeFlags.Val) continue;

                string rel = ProjectLayout.RelativeOf(segDir, file);
                string stem = rel.Substring(0, rel.Length - ProjectLayout.SegSidecarSuffix.Length);
                string basePath = Path.Combine(root, stem.Replace('/', Path.DirectorySeparatorChar));
                string jsonPath = basePath + ".json";
                if (!File.Exists(jsonPath))
                {
                    report.ModesSkipped++;
                    continue;
                }

                LabelMeDocument document = _labelMe.Read(jsonPath);
                document.Flags ??= new Dictionary<string, bool>();
                if (ImageModeFlags.HasKey(document.Flags)) continue; // already decided in the new layout

                string imagePath = ImageExtensions.Select(ext => basePath + ext).FirstOrDefault(File.Exists) ?? string.Empty;
                if (!string.IsNullOrEmpty(document.ImageData) && imagePath.Length == 0)
                    throw new IOException("the JSON embeds its image but the image file is missing, so it cannot be rewritten safely");

                ImageModeFlags.Apply(document.Flags, mode);
                _labelMe.WriteKeepingImageData(document, jsonPath, imagePath);
                report.ModesConverted++;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is JsonException || ex is ArgumentException)
            {
                ok = false;
                report.AddError("Mode of " + file + ": " + ex.Message);
            }
        }

        return ok;
    }

    /// <summary>Merges the legacy project.json into <c>.easyai/project.json</c> (schema 2); every existing and unknown field is kept.</summary>
    private static bool MergeProjectFile(ProjectPaths paths, string legacyDir, List<string> sources, ProjectMigrationReport report)
    {
        string file = paths.ProjectFile;
        try
        {
            JObject target = File.Exists(file) ? JObject.Parse(File.ReadAllText(file)) : new JObject();
            string legacyFile = Path.Combine(legacyDir, "project.json");
            if (File.Exists(legacyFile))
            {
                sources.Add("easylabel/project.json");
                foreach (JProperty property in JObject.Parse(File.ReadAllText(legacyFile)).Properties())
                    if (target[property.Name] == null) target[property.Name] = property.Value.DeepClone();
            }

            if ((target["schema"]?.Type == JTokenType.Integer ? target["schema"]!.Value<int>() : 0) < ProjectLayout.CurrentSchema)
            {
                if (target["schema"] != null) sources.Add("easyai/project.json schema " + target["schema"]);
                target["schema"] = ProjectLayout.CurrentSchema;
            }

            if (target["createdUtc"] == null) target["createdUtc"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);

            var origin = target["migratedFrom"] as JArray ?? new JArray();
            foreach (string source in sources)
                if (!origin.Any(t => t.Type == JTokenType.String && t.Value<string>() == source)) origin.Add(source);
            target["migratedFrom"] = origin;
            target["migratedUtc"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);

            AtomicFile.WriteAllText(file, target.ToString(Formatting.Indented));
            return true;
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is JsonException)
        {
            report.AddError("project.json: " + ex.Message);
            return false;
        }
    }

    /// <summary>All backups of one migration live inside the single metadata folder: <c>.easyai/backup/&lt;utc&gt;</c>.</summary>
    private static string BackupDir(string metaDir, string stamp) => Path.Combine(metaDir, "backup", stamp);

    /// <summary>Folder rename used by <see cref="Archive"/>; a seam for tests that need the cross-volume refusal of <see cref="Directory.Move(string, string)"/>.</summary>
    internal Action<string, string> MoveDirectory { get; set; } = Directory.Move;

    private void Archive(string folder, string desiredName, ProjectMigrationReport report)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(desiredName)!);
            string target = UniqueName(desiredName);
            MoveDirectory(folder, target);
            report.AddArchived(target);
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            // The backup folder is on another volume than the legacy folder (a named project keeps its data elsewhere), so the move is
            // refused. Rename the legacy folder in place instead: it stops counting as legacy and nothing is deleted or copied.
            try
            {
                string inPlace = UniqueName(folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + ".migrated");
                MoveDirectory(folder, inPlace);
                report.AddArchived(inPlace);
            }
            catch (Exception inner) when (inner is IOException || inner is UnauthorizedAccessException)
            {
                report.AddError("Could not rename " + folder + " to a backup name: " + ex.Message + " / " + inner.Message);
            }
        }
    }

    // ---- helpers ----

    private static bool CopyTree(string source, string target, ProjectMigrationReport report, Action copied)
    {
        bool ok = true;
        List<string> files;
        try
        {
            files = Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories).ToList();
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            report.AddError("Could not list " + source + ": " + ex.Message);
            return false;
        }

        foreach (string file in files)
        {
            try
            {
                string destination = Path.Combine(target, Path.GetRelativePath(source, file));
                if (File.Exists(destination))
                {
                    report.ExistingKept++;
                    continue;
                }

                CopyAtomic(file, destination);
                copied();
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                ok = false;
                report.AddError(file + ": " + ex.Message);
            }
        }

        return ok;
    }

    /// <summary>Copies through a temp file and a move, so the destination never holds a partial file. An existing destination is an error.</summary>
    private static void CopyAtomic(string source, string destination)
    {
        string directory = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(directory);
        string temp = Path.Combine(directory, "." + Path.GetFileName(destination) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.Copy(source, temp, false);
            File.Move(temp, destination, false);
        }
        catch
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
    }

    private static string UniqueName(string desired)
    {
        string candidate = desired;
        for (int i = 2; File.Exists(candidate) || Directory.Exists(candidate); i++)
            candidate = desired + "-" + i.ToString(CultureInfo.InvariantCulture);
        return candidate;
    }

    private static byte ReadMode(string segSidecarFile)
    {
        JObject sidecar = JObject.Parse(File.ReadAllText(segSidecarFile));
        JToken? token = sidecar.GetValue("Mode", StringComparison.OrdinalIgnoreCase);
        if (token == null || token.Type != JTokenType.Integer) return ImageModeFlags.Auto;
        long mode = token.Value<long>();
        return mode == ImageModeFlags.Train || mode == ImageModeFlags.Val ? (byte)mode : ImageModeFlags.Auto;
    }

    private static int? ReadSchema(string projectFile)
    {
        try
        {
            if (!File.Exists(projectFile)) return null;
            JToken? schema = JObject.Parse(File.ReadAllText(projectFile))["schema"];
            return schema != null && schema.Type == JTokenType.Integer ? schema.Value<int>() : 0;
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is JsonException)
        {
            return null; // damaged: leave it alone, it is not a migration trigger
        }
    }

    /// <summary>All <c>name.easylabel.json</c> files of the project, never descending into dot-folders, <c>*.easyai.data</c> folders or the metadata folder.</summary>
    private static IEnumerable<string> EnumerateLegacySidecars(ProjectPaths paths)
    {
        var pending = new Stack<string>();
        pending.Push(paths.ImageRoot);
        while (pending.Count > 0)
        {
            string current = pending.Pop();
            IEnumerable<string> files;
            List<string> children;
            try
            {
                files = Directory.EnumerateFiles(current, "*" + ProjectLayout.LegacySidecarSuffix, SearchOption.TopDirectoryOnly).ToList();
                children = Directory.EnumerateDirectories(current).ToList();
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                continue;
            }

            foreach (string file in files)
                if (file.EndsWith(ProjectLayout.LegacySidecarSuffix, StringComparison.OrdinalIgnoreCase)) yield return file;
            foreach (string child in children)
                if (!paths.IsSkippedDirectory(child)) pending.Push(child);
        }
    }
}
