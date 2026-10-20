using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace EasyLabel.Core.Model;

public sealed class LabelShape
{
    [JsonProperty("label")]
    public string? Label { get; set; }

    [JsonProperty("points")]
    public List<double[]> Points { get; set; } = new();

    [JsonProperty("group_id")]
    public int? GroupId { get; set; }

    private string? _shapeType;

    /// <summary>LabelMe shape type; the legacy EasyLabel name <c>"rotation"</c> is read as <c>"oriented_rectangle"</c> and saved under the new name.</summary>
    [JsonProperty("shape_type")]
    public string? ShapeType
    {
        get => _shapeType;
        set => _shapeType = string.Equals(value, "rotation", System.StringComparison.Ordinal) ? "oriented_rectangle" : value;
    }

    [JsonProperty("flags")]
    public Dictionary<string, bool> Flags { get; set; } = new();

    [JsonProperty("description")]
    public string? Description { get; set; }

    [JsonProperty("direction")]
    public double? Direction { get; set; }

    /// <summary>Optional EasyAI extension object (<c>"easyai"</c>); written only when it has content, e.g. <c>{"holes": [...]}</c>.</summary>
    [JsonProperty("easyai", NullValueHandling = NullValueHandling.Ignore)]
    public JObject? EasyAi { get; set; }

    /// <summary>Unknown keys of the file, kept so a save does not drop what other LabelMe tools wrote.</summary>
    [JsonExtensionData]
    public IDictionary<string, JToken>? ExtensionData { get; set; }
}
