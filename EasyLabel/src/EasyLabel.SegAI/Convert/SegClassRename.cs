using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace EasyLabel.SegAI.Convert;

/// <summary>
/// Positional class rename: a class keeps its table position (its id), so the shapes still labelled with its old name are
/// relabelled to the new name. Pure text work; the session reads and writes the files.
/// </summary>
public static class SegClassRename
{
    /// <summary>Old name to new name for every table position whose name changed. Keys are trimmed; lookups ignore case.</summary>
    public static IReadOnlyDictionary<string, string> RenamesBetween(IReadOnlyList<string> oldNames, IReadOnlyList<string> newNames)
    {
        ArgumentNullException.ThrowIfNull(oldNames);
        ArgumentNullException.ThrowIfNull(newNames);
        var renames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int count = Math.Min(oldNames.Count, newNames.Count);
        for (int i = 0; i < count; i++)
        {
            if (!string.Equals(oldNames[i], newNames[i], StringComparison.Ordinal)) renames[oldNames[i].Trim()] = newNames[i];
        }
        return renames;
    }

    /// <summary>
    /// Relabels every shape whose label (trimmed, ignoring case) is an old name, in one pass, so a swap of two names cannot chain.
    /// Returns the new JSON text, or null when no shape changed. The JSON is edited as a tree, so imageData, unknown keys and the
    /// easyai holes object keep their content. Throws <see cref="JsonException"/> for text that is not JSON.
    /// </summary>
    public static string? RelabelJson(string json, IReadOnlyDictionary<string, string> renames)
    {
        ArgumentNullException.ThrowIfNull(json);
        ArgumentNullException.ThrowIfNull(renames);
        if (renames.Count == 0) return null;

        var lookup = new Dictionary<string, string>(renames, StringComparer.OrdinalIgnoreCase);
        JObject root = JObject.Parse(json);
        if (root["shapes"] is not JArray shapes) return null;

        bool changed = false;
        foreach (JToken token in shapes)
        {
            if (token is not JObject shape) continue;
            if (shape["label"] is not JValue label || label.Type != JTokenType.String) continue;
            string? text = (string?)label;
            if (text is null || !lookup.TryGetValue(text.Trim(), out string? newName)) continue;
            shape["label"] = newName;
            changed = true;
        }
        return changed ? root.ToString(Formatting.Indented) : null;
    }
}
