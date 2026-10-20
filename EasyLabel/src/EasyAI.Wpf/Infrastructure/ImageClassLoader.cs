using EasyLabel.Core.Model;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace EasyAI.Desktop.Infrastructure;

/// <summary>
/// Reads the OK/NG class of an image from its LabelMe JSON. Streams the file and stops at the top-level "flags" property, so the
/// embedded imageData (often megabytes of base64) is skipped token by token and never deserialized. Plain file read, no UI.
/// </summary>
public static class ImageClassLoader
{
    /// <summary>The class stored in the JSON; <see cref="ImageClassFlags.Unmarked"/> when there is no JSON or it cannot be read.</summary>
    public static byte ReadClass(string jsonPath)
    {
        if (string.IsNullOrEmpty(jsonPath) || !File.Exists(jsonPath))
        {
            return ImageClassFlags.Unmarked;
        }

        try
        {
            using var stream = new FileStream(jsonPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024, FileOptions.SequentialScan);
            using var text = new StreamReader(stream);
            using var json = new JsonTextReader(text);
            if (!json.Read() || json.TokenType != JsonToken.StartObject)
            {
                return ImageClassFlags.Unmarked;
            }

            while (json.Read() && json.TokenType == JsonToken.PropertyName)
            {
                string? name = json.Value as string;
                json.Read();
                if (string.Equals(name, "flags", StringComparison.Ordinal))
                {
                    // Lenient like the other LabelMe readers: a flag that is not a boolean (null, number, text) is ignored, not fatal.
                    return ImageClassFlags.Read(BooleanFlags(JToken.ReadFrom(json)));
                }

                json.Skip();
            }

            return ImageClassFlags.Unmarked;
        }
        catch (Exception)
        {
            // A damaged or locked JSON counts as unmarked here; the canvas reports the problem when the image is opened.
            return ImageClassFlags.Unmarked;
        }
    }

    /// <summary>The boolean flags of a "flags" token; other values are skipped.</summary>
    private static Dictionary<string, bool> BooleanFlags(JToken? token)
    {
        var flags = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        if (token is JObject properties)
        {
            foreach (JProperty property in properties.Properties())
            {
                if (property.Value.Type == JTokenType.Boolean)
                {
                    flags[property.Name] = (bool)property.Value;
                }
            }
        }

        return flags;
    }
}
