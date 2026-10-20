using System.Globalization;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace EasyLabel.Core.Projects;

/// <summary>Reads and writes the <c>&lt;Name&gt;.easyai</c> manifest. Writes are atomic (temporary file, then replace, retried briefly) and keep unknown fields.</summary>
public static class ProjectManifestStore
{
    private const string TimeFormat = "o";

    /// <summary>A new manifest (not written yet): schema 3, created and last opened now, the image folder stored relative when possible.</summary>
    public static ProjectManifest CreateNew(string manifestPath, string name, string imageRootFullPath, string? dataDirName = null)
    {
        string full = Path.GetFullPath(manifestPath);
        string trimmed = ProjectNameRules.Normalize(name);
        DateTime now = DateTime.UtcNow;
        return new ProjectManifest
        {
            Schema = ProjectManifest.CurrentSchema,
            Name = trimmed,
            CreatedUtc = now,
            LastOpenedUtc = now,
            ImageRoot = ProjectManifest.FormatImageRoot(Path.GetDirectoryName(full)!, imageRootFullPath),
            DataDir = string.IsNullOrWhiteSpace(dataDirName) ? ProjectManifest.DefaultDataDirName(trimmed) : dataDirName,
            ManifestPath = full,
        };
    }

    /// <summary>
    /// Reads a manifest. Throws <see cref="ProjectException"/> (<see cref="ProjectErrorCode.ManifestMissing"/> or
    /// <see cref="ProjectErrorCode.ManifestInvalid"/>) when the file is missing, is a folder, is not JSON or lacks a required field.
    /// </summary>
    public static ProjectManifest Read(string manifestPath)
    {
        if (string.IsNullOrWhiteSpace(manifestPath)) throw new ArgumentException("Manifest path is required.", nameof(manifestPath));
        string full = Path.GetFullPath(manifestPath);
        if (Directory.Exists(full))
            throw new ProjectException(ProjectErrorCode.ManifestInvalid, "This is a folder, not a project file: " + full, full);
        if (!File.Exists(full))
            throw new ProjectException(ProjectErrorCode.ManifestMissing, "The project file does not exist: " + full, full);

        JObject document;
        try
        {
            using JsonTextReader reader = new JsonTextReader(new StringReader(File.ReadAllText(full))) { DateParseHandling = DateParseHandling.None };
            document = JObject.Load(reader);
        }
        catch (JsonException ex)
        {
            throw new ProjectException(ProjectErrorCode.ManifestInvalid, "The project file is not valid: " + full + " (" + ex.Message + ")", full, ex);
        }

        int schema = document["schema"]?.Type == JTokenType.Integer ? document["schema"]!.Value<int>() : 0;
        if (schema < ProjectManifest.CurrentSchema)
            throw new ProjectException(ProjectErrorCode.ManifestInvalid, "The project file has no supported schema (expected " + ProjectManifest.CurrentSchema + "): " + full, full);

        string? imageRoot = Str(document, "imageRoot");
        if (string.IsNullOrWhiteSpace(imageRoot))
            throw new ProjectException(ProjectErrorCode.ManifestInvalid, "The project file does not say where the images are (imageRoot): " + full, full);

        string name = Str(document, "name") ?? string.Empty;
        if (name.Trim().Length == 0) name = Path.GetFileNameWithoutExtension(full);
        string dataDir = Str(document, "dataDir") ?? string.Empty;
        if (dataDir.Trim().Length == 0) dataDir = ProjectManifest.DefaultDataDirName(name);
        if (!IsPlainFolderName(dataDir))
            throw new ProjectException(ProjectErrorCode.ManifestInvalid, "The data folder name in the project file is not a plain folder name (dataDir): " + dataDir, full);

        DateTime created = Time(document, "createdUtc") ?? File.GetCreationTimeUtc(full);
        DateTime opened = Time(document, "lastOpenedUtc") ?? created;
        ProjectManifest manifest = new ProjectManifest
        {
            Schema = schema,
            Name = name.Trim(),
            CreatedUtc = created,
            LastOpenedUtc = opened,
            ImageRoot = imageRoot.Trim(),
            DataDir = dataDir.Trim(),
            ManifestPath = full,
            Raw = document,
            AgentConsent = ReadConsent(document["agentConsent"] as JObject),
        };

        try
        {
            _ = manifest.ImageRootFullPath;
        }
        catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
        {
            throw new ProjectException(ProjectErrorCode.ManifestInvalid, "The image folder in the project file is not a valid path (imageRoot): " + imageRoot, full, ex);
        }

        return manifest;
    }

    /// <summary>Writes the manifest to <see cref="ProjectManifest.ManifestPath"/>: atomically, keeping every field this version does not know.</summary>
    public static void Write(ProjectManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (string.IsNullOrWhiteSpace(manifest.ManifestPath)) throw new ArgumentException("The manifest has no path.", nameof(manifest));

        JObject document = manifest.Raw != null ? (JObject)manifest.Raw.DeepClone() : new JObject();
        document["schema"] = manifest.Schema;
        document["name"] = manifest.Name;
        document["createdUtc"] = FormatTime(manifest.CreatedUtc);
        document["lastOpenedUtc"] = FormatTime(manifest.LastOpenedUtc);
        document["imageRoot"] = manifest.ImageRoot;
        document["dataDir"] = manifest.DataDir;
        // Written only when set; a revoked consent (null) removes the key, every other key of the document stays as it was.
        if (manifest.AgentConsent != null) document["agentConsent"] = WriteConsent(manifest.AgentConsent);
        else document.Remove("agentConsent");

        WriteAtomic(Path.GetFullPath(manifest.ManifestPath), document.ToString(Formatting.Indented));
        manifest.Raw = document;
    }

    private static void WriteAtomic(string path, string text)
    {
        string directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        string temp = Path.Combine(directory, "." + Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.WriteAllText(temp, text, new UTF8Encoding(false));
            MoveWithRetry(temp, path);
        }
        catch
        {
            try
            {
                if (File.Exists(temp)) File.Delete(temp);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
            }

            throw;
        }
    }

    // A virus scanner or the search indexer can hold a freshly written file for a few milliseconds; the replace then fails with "access
    // denied" although nothing in this process holds it. Retry briefly before giving up.
    private static void MoveWithRetry(string temp, string path)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                File.Move(temp, path, true);
                return;
            }
            catch (Exception ex) when (attempt < 6 && (ex is IOException || ex is UnauthorizedAccessException))
            {
                Thread.Sleep(15 * (attempt + 1));
            }
        }
    }

    private static bool IsPlainFolderName(string name)
    {
        string trimmed = name.Trim();
        return trimmed.Length > 0
            && trimmed != "." && trimmed != ".."
            && trimmed.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
            && !Path.IsPathRooted(trimmed);
    }

    private static string FormatTime(DateTime time)
    {
        DateTime utc = time.Kind == DateTimeKind.Local ? time.ToUniversalTime() : DateTime.SpecifyKind(time, DateTimeKind.Utc);
        return utc.ToString(TimeFormat, CultureInfo.InvariantCulture);
    }

    private static string? Str(JObject document, string name)
    {
        JToken? token = document[name];
        return token != null && token.Type == JTokenType.String ? token.Value<string>() : null;
    }

    // A damaged consent object reads as "not decided": the project still opens and the user is asked again.
    private static ProjectAgentConsent? ReadConsent(JObject? consent)
    {
        if (consent == null) return null;
        JToken? granted = consent["granted"];
        if (granted == null || granted.Type != JTokenType.Boolean) return null;
        JToken? version = consent["version"];
        List<string> tools = new List<string>();
        if (consent["tools"] is JArray array)
        {
            foreach (JToken item in array)
            {
                if (item.Type == JTokenType.String) tools.Add(item.Value<string>() ?? string.Empty);
            }
        }

        return new ProjectAgentConsent
        {
            Version = version != null && version.Type == JTokenType.Integer ? version.Value<int>() : 0,
            Granted = granted.Value<bool>(),
            Utc = Time(consent, "utc") ?? DateTime.MinValue,
            Tools = tools.ToArray(),
        };
    }

    private static JObject WriteConsent(ProjectAgentConsent consent)
    {
        JArray tools = new JArray();
        foreach (string tool in consent.Tools) tools.Add(tool);
        return new JObject
        {
            ["version"] = consent.Version,
            ["granted"] = consent.Granted,
            ["utc"] = FormatTime(consent.Utc),
            ["tools"] = tools,
        };
    }

    private static DateTime? Time(JObject document, string name)
    {
        string? text = Str(document, name);
        if (text != null && DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime parsed))
            return DateTime.SpecifyKind(parsed, DateTimeKind.Utc);
        return null;
    }
}
