namespace EasyLabel.Core.Model;

public sealed class EasyLabelSidecar
{
    public string ReviewStatus { get; set; } = "unlabeled";

    public List<ShapeProvenance> Shapes { get; set; } = new();
}
