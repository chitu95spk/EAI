namespace EasyLabel.Core.Editing;

/// <summary>Label, group id and description of one or several shapes (the content of the "Shape label" dialog).</summary>
public sealed class ShapeLabelInfo
{
    /// <summary>Null when the selected shapes carry different labels.</summary>
    public string? Label { get; set; }

    public int? GroupId { get; set; }

    public string? Description { get; set; } = string.Empty;
}
