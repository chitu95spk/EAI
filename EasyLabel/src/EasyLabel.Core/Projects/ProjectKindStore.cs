using EasyLabel.Core.Io;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace EasyLabel.Core.Projects;

/// <summary>What a project labels. Chosen when the project is created and never changed afterwards.</summary>
public enum ProjectKind
{
    /// <summary>Region/box/polygon labelling and detection (the original behaviour; every tool is available).</summary>
    Detect,

    /// <summary>Position of objects: rotated boxes and circles (plus AI boxes) only.</summary>
    Position,
}

/// <summary>
/// Reads and writes the <c>projectKind</c> key of <c>project.json</c>. A missing, unknown or unreadable value means
/// <see cref="ProjectKind.Detect"/>; opening a project never rewrites the file. Write keeps every other key.
/// </summary>
public static class ProjectKindStore
{
    public const string KeyName = "projectKind";

    public static ProjectKind Read(ProjectPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        try
        {
            string file = paths.ProjectFile;
            if (!File.Exists(file)) return ProjectKind.Detect;
            JObject doc = JObject.Parse(File.ReadAllText(file));
            string? text = doc[KeyName]?.Type == JTokenType.String ? doc[KeyName]!.Value<string>() : null;
            return string.Equals(text, "position", StringComparison.OrdinalIgnoreCase) ? ProjectKind.Position : ProjectKind.Detect;
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is JsonException)
        {
            return ProjectKind.Detect;
        }
    }

    /// <summary>Merges the kind into <c>project.json</c> (created when missing), keeping all other keys; atomic temp-file replace.</summary>
    public static void Write(ProjectPaths paths, ProjectKind kind)
    {
        ArgumentNullException.ThrowIfNull(paths);
        string file = paths.ProjectFile;
        JObject doc;
        try
        {
            doc = File.Exists(file) ? JObject.Parse(File.ReadAllText(file)) : new JObject();
        }
        catch (JsonException)
        {
            // An unreadable file is not silently replaced: unknown content would be lost.
            throw new IOException("project.json is not valid JSON: " + file);
        }

        doc[KeyName] = kind == ProjectKind.Position ? "position" : "detect";
        AtomicFile.WriteAllText(file, doc.ToString(Formatting.Indented));
    }
}
