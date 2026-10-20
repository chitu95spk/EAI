using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using EasyLabel.Commands.Discovery;

namespace EasyLabel.Commands.Catalog
{
    public sealed class CatalogWriter
    {
        public string Write(IReadOnlyList<DiscoveredCommand> commands)
        {
            if (commands == null) throw new ArgumentNullException(nameof(commands));
            using (var stream = new MemoryStream())
            {
                using (var writer = new Utf8JsonWriter(stream))
                {
                    writer.WriteStartObject();
                    writer.WriteString("catalogVersion", "1.0.0");
                    writer.WriteStartArray("commands");
                    foreach (DiscoveredCommand command in commands.OrderBy(c => c.Descriptor.Id.Value, StringComparer.Ordinal))
                    {
                        CommandDescriptor d = command.Descriptor;
                        writer.WriteStartObject();
                        writer.WriteString("id", d.Id.Value);
                        writer.WriteString("kind", d.Kind.ToString().ToLowerInvariant());
                        writer.WriteString("since", "1.0.0");
                        writer.WriteNumber("schemaRev", 1);
                        writer.WriteString("name", d.NameKey);
                        writer.WriteString("desc", d.DescriptionKey);
                        writer.WritePropertyName("args");
                        WriteCanonical(writer, d.ArgsSchema.Definition);
                        writer.WriteNumber("when", (int)d.When);
                        writer.WriteString("cost", d.Cost.ToString());
                        writer.WriteString("risk", d.Risk.ToString());
                        writer.WriteBoolean("undo", d.Undo);
                        writer.WriteBoolean("headless", command.Headless);
                        WriteStrings(writer, "modes", d.Modes);
                        WriteStrings(writer, "aliases", d.Aliases);
                        writer.WriteEndObject();
                    }
                    writer.WriteEndArray();
                    writer.WriteEndObject();
                }
                return Encoding.UTF8.GetString(stream.ToArray());
            }
        }

        private static void WriteStrings(Utf8JsonWriter writer, string name, IEnumerable<string> values)
        {
            writer.WriteStartArray(name);
            foreach (string value in values.OrderBy(v => v, StringComparer.Ordinal)) writer.WriteStringValue(value);
            writer.WriteEndArray();
        }

        private static void WriteCanonical(Utf8JsonWriter writer, JsonElement value)
        {
            if (value.ValueKind == JsonValueKind.Object)
            {
                writer.WriteStartObject();
                foreach (JsonProperty property in value.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value);
                }
                writer.WriteEndObject();
            }
            else if (value.ValueKind == JsonValueKind.Array)
            {
                writer.WriteStartArray();
                foreach (JsonElement item in value.EnumerateArray()) WriteCanonical(writer, item);
                writer.WriteEndArray();
            }
            else value.WriteTo(writer);
        }
    }
}
