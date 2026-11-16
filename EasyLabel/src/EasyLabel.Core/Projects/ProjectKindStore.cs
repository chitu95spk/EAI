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

    /// <summary>Counter ("Couter" in EasyVision): counts objects one by one. Same instance head and rotated box / circle shapes as Position.</summary>
    Counter,

    /// <summary>CounterAI: FastNet point-heatmap counter (e.g. copper wire strands). Labels are points, not boxes; not an instance kind.</summary>
    CounterAi,
}

public static class ProjectKindExtensions
{
    /// <summary>Position and Counter label instances with rotated boxes and circles only, and train the instance head.</summary>
    public static bool IsInstanceKind(this ProjectKind kind) => kind == ProjectKind.Position || kind == ProjectKind.Counter;
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
            if (string.Equals(text, "position", StringComparison.OrdinalIgnoreCase)) return ProjectKind.Position;
            if (string.Equals(text, "counter", StringComparison.OrdinalIgnoreCase)) return ProjectKind.Counter;
            if (string.Equals(text, "counterai", StringComparison.OrdinalIgnoreCase)) return ProjectKind.CounterAi;
            return ProjectKind.Detect;
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

        doc[KeyName] = kind == ProjectKind.Position ? "position" : kind == ProjectKind.Counter ? "counter" : kind == ProjectKind.CounterAi ? "counterai" : "detect";
        AtomicFile.WriteAllText(file, doc.ToString(Formatting.Indented));
    }
}
