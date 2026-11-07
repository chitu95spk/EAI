using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace EasyLabel.Ai.Mcp;

/// <summary>Writes the labelme .json (polygon shapes) beside a generated image; the same layout for synthetic and real frames.</summary>
internal static class LabelmeJson
{
    public static void Write(string imagePath, int width, int height, IEnumerable<(string Label, IReadOnlyList<double[]> Polygon)> shapes)
    {
        var array = new JArray(shapes.Select(o => new JObject
        {
            ["label"] = o.Label,
            ["points"] = new JArray(o.Polygon.Select(p => new JArray(Math.Round(p[0], 1), Math.Round(p[1], 1)))),
            ["group_id"] = null,
            ["shape_type"] = "polygon",
            ["flags"] = new JObject(),
        }));
        var doc = new JObject
        {
            ["version"] = "5.0.0",
            ["flags"] = new JObject(),
            ["shapes"] = array,
            ["imagePath"] = Path.GetFileName(imagePath),
            ["imageData"] = null,
            ["imageHeight"] = height,
            ["imageWidth"] = width,
        };
        File.WriteAllText(Path.ChangeExtension(imagePath, ".json"), doc.ToString(Formatting.Indented));
    }
}
