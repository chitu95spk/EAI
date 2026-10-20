namespace EasyLabel.Core.Model;

public sealed class ShapeProvenance
{
    public string Source { get; set; } = "manual";

    public double? Confidence { get; set; }

    public string? Model { get; set; }
}
