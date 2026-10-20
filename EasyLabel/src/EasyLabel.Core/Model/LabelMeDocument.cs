using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace EasyLabel.Core.Model;

public sealed class LabelMeDocument
{
    [JsonProperty("version")]
    public string Version { get; set; } = "5.5.0";

    [JsonProperty("flags")]
    public Dictionary<string, bool> Flags { get; set; } = new();

    private List<LabelShape> _shapes = new List<LabelShape>();

    /// <summary>Never null: a JSON "shapes": null or a missing key both load as an empty list.</summary>
    [JsonProperty("shapes")]
    public List<LabelShape> Shapes
    {
        get => _shapes;
        set => _shapes = value ?? new List<LabelShape>();
    }

    [JsonProperty("imagePath")]
    public string? ImagePath { get; set; }

    [JsonProperty("imageData")]
    public string? ImageData { get; set; }

    [JsonProperty("imageHeight")]
    public int ImageHeight { get; set; }

    [JsonProperty("imageWidth")]
    public int ImageWidth { get; set; }

    /// <summary>Unknown top-level keys of the file, kept so a save does not drop what other LabelMe tools wrote.</summary>
    [JsonExtensionData]
    public IDictionary<string, JToken>? ExtensionData { get; set; }
}
