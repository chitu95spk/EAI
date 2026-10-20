using Newtonsoft.Json;

namespace EasyLabel.Ai.Detect;

public sealed class DetectRequest
{
    [JsonProperty("task")]
    public string Task { get; set; } = "detect";

    [JsonProperty("model")]
    public string Model { get; set; } = string.Empty;

    [JsonProperty("images")]
    public List<string> Images { get; set; } = new();

    [JsonProperty("conf")]
    public double Conf { get; set; } = 0.25;

    [JsonProperty("iou")]
    public double Iou { get; set; } = 0.7;

    [JsonProperty("classes")]
    public List<int>? Classes { get; set; }
}
