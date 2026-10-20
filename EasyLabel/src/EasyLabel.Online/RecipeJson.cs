using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EasyLabel.Online
{
    /// <summary>Closed, case-sensitive JSON. Canonical output sorts object keys ordinally.</summary>
    public static class RecipeJson
    {
        private static JsonSerializerOptions Options()
        {
            return new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                PropertyNameCaseInsensitive = false,
                UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
                MaxDepth = 48
            };
        }

        public static Recipe Parse(string json)
        {
            Recipe recipe = Decode<Recipe>(json);
            using (JsonDocument document = JsonDocument.Parse(json))
            {
                JsonElement map = document.RootElement.GetProperty("classMap");
                if (map.ValueKind == JsonValueKind.Object)
                    foreach (JsonProperty property in map.EnumerateObject())
                    {
                        int index;
                        RecipeValidation.Require(int.TryParse(property.Name, NumberStyles.None, CultureInfo.InvariantCulture, out index)
                            && property.Name == index.ToString(CultureInfo.InvariantCulture), "classMap keys must be canonical nonnegative integers.");
                    }
            }
            RecipeValidation.Validate(recipe);
            return recipe;
        }

        public static byte[] Serialize(Recipe recipe)
        {
            RecipeValidation.Validate(recipe);
            return Encode(recipe);
        }

        internal static T Decode<T>(string json)
        {
            try
            {
                using (JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 48 }))
                    CheckDuplicates(document.RootElement);
                T value = JsonSerializer.Deserialize<T>(json, Options());
                if (value == null) throw new JsonException("Root must not be null.");
                return value;
            }
            catch (Exception ex) when (ex is JsonException || ex is NotSupportedException || ex is ArgumentException)
            {
                throw new RecipeInvalid(RecipeInvalidReason.BadSchema, "Invalid JSON: " + ex.Message, ex);
            }
        }

        internal static byte[] Encode<T>(T value)
        {
            using (JsonDocument document = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(value, Options())))
            using (MemoryStream stream = new MemoryStream())
            {
                using (Utf8JsonWriter writer = new Utf8JsonWriter(stream)) WriteCanonical(writer, document.RootElement);
                return stream.ToArray();
            }
        }

        private static void CheckDuplicates(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                HashSet<string> names = new HashSet<string>(StringComparer.Ordinal);
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    if (!names.Add(property.Name)) throw new JsonException("Duplicate field: " + property.Name);
                    CheckDuplicates(property.Value);
                }
            }
            else if (element.ValueKind == JsonValueKind.Array)
                foreach (JsonElement item in element.EnumerateArray()) CheckDuplicates(item);
        }

        private static void WriteCanonical(Utf8JsonWriter writer, JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                writer.WriteStartObject();
                foreach (JsonProperty property in element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value);
                }
                writer.WriteEndObject();
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                writer.WriteStartArray();
                foreach (JsonElement item in element.EnumerateArray()) WriteCanonical(writer, item);
                writer.WriteEndArray();
            }
            else element.WriteTo(writer);
        }
    }

    internal static class RecipeValidation
    {
        internal static void Require(bool condition, string message)
        {
            if (!condition) throw new RecipeInvalid(RecipeInvalidReason.BadSchema, message);
        }

        internal static bool Finite(double value) { return !double.IsNaN(value) && !double.IsInfinity(value); }
        internal static bool Range(double value, double min, double max) { return Finite(value) && value >= min && value <= max; }
        internal static bool Text(string value, int max) { return !string.IsNullOrWhiteSpace(value) && value.Length <= max; }
        internal static bool Identifier(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length > 64 || !Letter(value[0])) return false;
            return value.All(c => Letter(c) || (c >= '0' && c <= '9'));
        }
        private static bool Letter(char c) { return (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || c == '_'; }
        internal static bool Hash(string value)
        {
            return value != null && value.Length == 64 && value.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'));
        }

        internal static void Validate(Recipe recipe)
        {
            Require(recipe != null && recipe.SchemaRev == 1, "schemaRev must be 1.");
            Require(Text(recipe.CatalogVersion, 128), "catalogVersion is required (max 128 characters).");
            Require(recipe.Classes != null && recipe.Classes.Count >= 1 && recipe.Classes.Count <= 1024, "classes must contain 1..1024 entries.");
            HashSet<string> names = new HashSet<string>(StringComparer.Ordinal);
            foreach (RecipeClass item in recipe.Classes)
            {
                Require(item != null && Identifier(item.Name) && names.Add(item.Name), "Class names must be unique ASCII identifiers (max 64).");
                Require(Range(item.MinScore, 0, 1), "minScore must be 0..1.");
                Require(Range(item.MinAreaPx, 0, 1e12), "minAreaPx must be 0..1e12.");
                Require(item.MaxCount >= 0 && item.MaxCount <= 1000000, "maxCount must be 0..1000000.");
                Require(item.Roi == null || (item.Roi.Length == 4 && Range(item.Roi[0], 0, 1e7) && Range(item.Roi[1], 0, 1e7)
                    && Range(item.Roi[2], double.Epsilon, 1e7) && Range(item.Roi[3], double.Epsilon, 1e7)), "roi must be null or [x,y,positive width,positive height], bounded by 1e7.");
            }
            Require(recipe.ClassMap != null && recipe.ClassMap.Count == names.Count, "classMap must exactly match classes.");
            HashSet<string> mapped = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < names.Count; i++)
            {
                string name;
                Require(recipe.ClassMap.TryGetValue(i, out name) && names.Contains(name) && mapped.Add(name), "classMap indices must be contiguous from zero with each class exactly once.");
            }
            PositionParameters position = recipe.Position;
            Require(position != null && (position.Mode == "bbox" || position.Mode == "centroid" || position.Mode == "polygon"), "position.mode must be bbox, centroid or polygon.");
            Require(position.Units == "px" || position.Units == "mm", "position.units must be px or mm.");
            Require(position.Calib == null || Identifier(position.Calib), "position.calib must be null or an identifier.");
            Require(position.Units != "mm" || position.Calib != null, "mm units require position.calib.");
            Require(Range(recipe.Nms, 0, 1), "nms must be 0..1.");
            Require(recipe.Verdict != null && Text(recipe.Verdict.NgIf, 4096), "verdict.ng_if is required (max 4096 characters).");
            try { VerdictParser.Parse(recipe.Verdict.NgIf, names); }
            catch (VerdictParseException ex) { throw new RecipeInvalid(RecipeInvalidReason.BadSchema, ex.Message, ex); }
            PreprocessParameters prep = recipe.Preprocess;
            Require(prep != null && prep.InputWidth >= 1 && prep.InputWidth <= 16384 && prep.InputHeight >= 1 && prep.InputHeight <= 16384, "Input dimensions must be 1..16384.");
            Require((prep.Channels == 1 && prep.ColorOrder == "GRAY") || (prep.Channels == 3 && (prep.ColorOrder == "RGB" || prep.ColorOrder == "BGR")), "channels/colorOrder mismatch.");
            Require(prep.Interpolation == "nearest" || prep.Interpolation == "bilinear", "interpolation must be nearest or bilinear.");
            Require(Range(prep.Scale, double.Epsilon, 1e6), "scale must be positive and <= 1e6.");
            Require(prep.Mean != null && prep.Std != null && prep.Mean.Length == prep.Channels && prep.Std.Length == prep.Channels, "mean/std lengths must equal channels.");
            Require(prep.Mean.All(x => Range(x, -1e6, 1e6)) && prep.Std.All(x => Range(x, double.Epsilon, 1e6)), "mean/std out of range.");
            Require(prep.Seed >= 0, "seed must be nonnegative.");
            Require(recipe.Wire != null && recipe.Wire.Codec == "json" && recipe.Wire.MaxPayloadBytes >= 1 && recipe.Wire.MaxPayloadBytes <= 16777216, "wire requires json codec and maxPayloadBytes 1..16777216.");
            Require(recipe.Thresholds != null && recipe.Thresholds.Count <= 128 && recipe.Thresholds.All(p => Identifier(p.Key) && Range(p.Value, 0, 1)), "thresholds require identifier keys and 0..1 values (max 128).");
            Require(recipe.Model != null && (recipe.Model.FileName == "model.onnx" || recipe.Model.FileName == "model.segai") && Hash(recipe.Model.Sha256), "model requires a fixed model.onnx/model.segai fileName and lowercase SHA256.");
        }

        internal static void Validate(RecipeProvenance provenance)
        {
            Require(provenance != null && Text(provenance.Prompt, 65536) && Text(provenance.AgentModel, 256)
                && Text(provenance.AgentEffort, 64) && Text(provenance.AgentRunId, 256) && Text(provenance.Author, 256)
                && Hash(provenance.DatasetHash) && provenance.Date != default(DateTimeOffset), "Invalid provenance: all metadata, SHA256 datasetHash and date are required.");
        }

        internal static void Validate(Calibration calibration, Recipe recipe)
        {
            Require(calibration == null ? recipe.Position.Calib == null : Identifier(calibration.Id) && calibration.Id == recipe.Position.Calib
                && Range(calibration.MmPerPixelX, double.Epsilon, 1e6) && Range(calibration.MmPerPixelY, double.Epsilon, 1e6)
                && Range(calibration.OriginXmm, -1e12, 1e12) && Range(calibration.OriginYmm, -1e12, 1e12), "Calibration must match position.calib with finite positive pixel scales.");
        }
    }
}
