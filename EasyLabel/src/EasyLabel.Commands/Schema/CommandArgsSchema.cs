using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace EasyLabel.Commands.Schema;

/// <summary>The command catalog's JSON Schema subset; definitions and arguments are detached from caller documents.</summary>
public sealed class CommandArgsSchema
{
    private static readonly TimeSpan PatternTimeout = TimeSpan.FromMilliseconds(100);
    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "type", "enum", "minimum", "maximum", "default", "pattern", "required",
        "properties", "additionalProperties", "maxLength", "items"
    };

    public static CommandArgsSchema Empty { get; } = Parse(
        "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}");

    public CommandArgsSchema(JsonElement definition)
    {
        CheckDefinition(definition, "$schema");
        if (!definition.TryGetProperty("type", out JsonElement type) || type.GetString() != "object")
            throw new ArgumentException("Command arguments require an object schema.", nameof(definition));
        Definition = definition.Clone();
    }

    public JsonElement Definition { get; }

    public static CommandArgsSchema Parse(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return new(document.RootElement);
    }

    public bool TryValidate(JsonElement args, out JsonElement validated, out string? error)
    {
        validated = default;
        error = null;
        if (args.ValueKind != JsonValueKind.Object)
        {
            error = "$: expected object";
            return false;
        }
        if (!HasUniqueProperties(args))
        {
            error = "$: duplicate argument property";
            return false;
        }

        JsonNode? value = JsonNode.Parse(args.GetRawText());
        if (!Validate(Definition, value, "$", out error)) return false;
        validated = JsonSerializer.SerializeToElement(value);
        return true;
    }

    private static bool Validate(JsonElement schema, JsonNode? value, string path, out string? error)
    {
        error = null;
        JsonElement actual = JsonSerializer.SerializeToElement(value);
        if (schema.TryGetProperty("type", out JsonElement type) && !MatchesType(type.GetString()!, actual))
            return Fail($"{path}: expected {type.GetString()}", out error);

        if (schema.TryGetProperty("enum", out JsonElement choices) &&
            !choices.EnumerateArray().Any(choice => JsonNode.DeepEquals(value, JsonNode.Parse(choice.GetRawText()))))
            return Fail($"{path}: value is outside enum", out error);

        if (actual.ValueKind == JsonValueKind.Number)
        {
            if (!actual.TryGetDouble(out double number) || !double.IsFinite(number))
                return Fail($"{path}: number is outside the supported range", out error);
            if (schema.TryGetProperty("minimum", out JsonElement minimum) && number < minimum.GetDouble())
                return Fail($"{path}: below minimum", out error);
            if (schema.TryGetProperty("maximum", out JsonElement maximum) && number > maximum.GetDouble())
                return Fail($"{path}: above maximum", out error);
        }

        if (actual.ValueKind == JsonValueKind.String)
        {
            string text = actual.GetString()!;
            if (schema.TryGetProperty("maxLength", out JsonElement maxLength) &&
                text.EnumerateRunes().Count() > maxLength.GetInt32())
                return Fail($"{path}: exceeds maxLength", out error);
            if (schema.TryGetProperty("pattern", out JsonElement pattern))
            {
                try
                {
                    if (!Regex.IsMatch(text, pattern.GetString()!, RegexOptions.CultureInvariant, PatternTimeout))
                        return Fail($"{path}: pattern mismatch", out error);
                }
                catch (RegexMatchTimeoutException)
                {
                    return Fail($"{path}: pattern timed out", out error);
                }
            }
        }

        if (value is JsonObject obj)
        {
            bool hasProperties = schema.TryGetProperty("properties", out JsonElement properties);
            bool allowUnknown = schema.TryGetProperty("additionalProperties", out JsonElement additional) && additional.GetBoolean();
            foreach (KeyValuePair<string, JsonNode?> property in obj.ToArray())
            {
                if (!hasProperties || !properties.TryGetProperty(property.Key, out JsonElement childSchema))
                {
                    if (!allowUnknown) return Fail($"{path}.{property.Key}: unknown argument", out error);
                }
                else if (!Validate(childSchema, property.Value, $"{path}.{property.Key}", out error)) return false;
            }

            if (hasProperties)
            {
                foreach (JsonProperty property in properties.EnumerateObject())
                {
                    if (!obj.ContainsKey(property.Name) && property.Value.TryGetProperty("default", out JsonElement fallback))
                    {
                        JsonNode? defaultValue = JsonNode.Parse(fallback.GetRawText());
                        if (!Validate(property.Value, defaultValue, $"{path}.{property.Name}", out error)) return false;
                        obj.Add(property.Name, defaultValue);
                    }
                }
            }

            if (schema.TryGetProperty("required", out JsonElement required))
                foreach (JsonElement name in required.EnumerateArray())
                    if (!obj.ContainsKey(name.GetString()!))
                        return Fail($"{path}.{name.GetString()}: required argument missing", out error);
        }

        if (value is JsonArray array && schema.TryGetProperty("items", out JsonElement itemSchema))
            for (int index = 0; index < array.Count; index++)
                if (!Validate(itemSchema, array[index], $"{path}[{index}]", out error)) return false;

        return true;
    }

    private static bool MatchesType(string type, JsonElement value) => type switch
    {
        "object" => value.ValueKind == JsonValueKind.Object,
        "array" => value.ValueKind == JsonValueKind.Array,
        "string" => value.ValueKind == JsonValueKind.String,
        "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "null" => value.ValueKind == JsonValueKind.Null,
        "number" => value.ValueKind == JsonValueKind.Number,
        "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out decimal number) && decimal.Truncate(number) == number,
        _ => false
    };

    private static void CheckDefinition(JsonElement schema, string path)
    {
        if (schema.ValueKind != JsonValueKind.Object || !HasUniqueProperties(schema))
            throw new ArgumentException($"{path}: expected schema object with unique properties.");
        foreach (JsonProperty property in schema.EnumerateObject())
        {
            if (!Keywords.Contains(property.Name))
                throw new ArgumentException($"{path}: unsupported schema keyword '{property.Name}'.");
            JsonElement value = property.Value;
            bool valid = property.Name switch
            {
                "type" => value.ValueKind == JsonValueKind.String && value.GetString() is
                    "object" or "array" or "string" or "boolean" or "null" or "number" or "integer",
                "enum" => value.ValueKind == JsonValueKind.Array && value.GetArrayLength() > 0,
                "minimum" or "maximum" => value.ValueKind == JsonValueKind.Number &&
                    value.TryGetDouble(out double number) && double.IsFinite(number),
                "maxLength" => value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int length) && length >= 0,
                "pattern" => value.ValueKind == JsonValueKind.String,
                "required" => value.ValueKind == JsonValueKind.Array &&
                    value.EnumerateArray().All(name => name.ValueKind == JsonValueKind.String),
                "properties" or "items" => value.ValueKind == JsonValueKind.Object,
                "additionalProperties" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
                "default" => true,
                _ => false
            };
            if (!valid) throw new ArgumentException($"{path}: invalid '{property.Name}' schema value.");
            if (property.Name == "properties")
                foreach (JsonProperty child in value.EnumerateObject()) CheckDefinition(child.Value, $"{path}.{child.Name}");
            if (property.Name == "items") CheckDefinition(value, $"{path}.items");
            if (property.Name == "pattern")
                _ = new Regex(value.GetString()!, RegexOptions.CultureInvariant, PatternTimeout);
        }
        if (schema.TryGetProperty("minimum", out JsonElement minimum) &&
            schema.TryGetProperty("maximum", out JsonElement maximum) && minimum.GetDouble() > maximum.GetDouble())
            throw new ArgumentException($"{path}: minimum exceeds maximum.");
        if (schema.TryGetProperty("default", out JsonElement fallback) &&
            !Validate(schema, JsonNode.Parse(fallback.GetRawText()), path, out string? error))
            throw new ArgumentException($"{path}: invalid default: {error}");
    }

    private static bool HasUniqueProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Array)
            return value.EnumerateArray().All(HasUniqueProperties);
        if (value.ValueKind != JsonValueKind.Object) return true;
        var names = new HashSet<string>(StringComparer.Ordinal);
        return value.EnumerateObject().All(property => names.Add(property.Name) && HasUniqueProperties(property.Value));
    }

    private static bool Fail(string message, out string? error)
    {
        error = message;
        return false;
    }
}
