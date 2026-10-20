using Newtonsoft.Json;

namespace EasyLabel.Ai.Detect;

public sealed class DetectionResult
{
    [JsonProperty("classId")]
    public int ClassId { get; set; }

    [JsonProperty("label")]
    public string Label { get; set; } = string.Empty;

    [JsonProperty("confidence")]
    public double Confidence { get; set; }

    [JsonProperty("shapeType")]
    public string ShapeType { get; set; } = "rectangle";

    [JsonProperty("points")]
    public List<double[]> Points { get; set; } = new();
}
