using Newtonsoft.Json;

namespace EasyLabel.Ai.Detect;

public sealed class DetectImageResult
{
    [JsonProperty("image")]
    public string Image { get; set; } = string.Empty;

    [JsonProperty("width")]
    public int Width { get; set; }

    [JsonProperty("height")]
    public int Height { get; set; }

    [JsonProperty("detections")]
    public List<DetectionResult> Detections { get; set; } = new();
}
