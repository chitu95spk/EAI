using System.Drawing;
using EasyLabel.SegAI.Data;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace EasyLabel.SegAI.Project;

/// <summary>Per-image Segment AI cache stored under .easyai/cache: exact regions (with holes) plus the LabelMe shape hash they were derived from.</summary>
public sealed class SegSidecar
{
    public int Schema { get; set; } = 1;
    public string ImageRel { get; set; } = "";
    public string? ImageKey { get; set; }

    /// <summary>Informational copy only (kept for files written by older builds). The authoritative mode is the train/val flag of the LabelMe JSON.</summary>
    public byte Mode { get; set; }
    public string ShapesHash { get; set; } = "";
    public List<SegAnnotation> Regions { get; set; } = new();

    internal static JsonSerializerSettings CreateSettings()
    {
        var settings = new JsonSerializerSettings { Formatting = Formatting.Indented };
        settings.Converters.Add(new PointFConverter());
        return settings;
    }

    /// <summary>Writes a PointF as [x, y] using invariant culture.</summary>
    private sealed class PointFConverter : JsonConverter
    {
        public override bool CanConvert(Type objectType) { return objectType == typeof(PointF) || objectType == typeof(PointF?); }

        public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer)
        {
            if (value == null) { writer.WriteNull(); return; }
            var p = (PointF)value;
            writer.WriteStartArray();
            writer.WriteValue(p.X);
            writer.WriteValue(p.Y);
            writer.WriteEndArray();
        }

        public override object? ReadJson(JsonReader reader, Type objectType, object? existingValue, JsonSerializer serializer)
        {
            if (reader.TokenType == JsonToken.Null) return objectType == typeof(PointF?) ? null : new PointF();
            var array = JArray.Load(reader);
            if (array.Count != 2) throw new JsonSerializationException("Point must have two coordinates.");
            return new PointF(array[0].Value<float>(), array[1].Value<float>());
        }
    }
}
