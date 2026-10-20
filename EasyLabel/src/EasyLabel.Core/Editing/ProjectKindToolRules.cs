using EasyLabel.Core.Projects;

namespace EasyLabel.Core.Editing;

/// <summary>Which drawing tools a project kind offers. Detect keeps every tool; Position only places oriented boxes and circles.</summary>
public static class ProjectKindToolRules
{
    public static bool IsToolAllowed(ProjectKind kind, CanvasTool tool)
    {
        if (kind != ProjectKind.Position) return true;
        return tool == CanvasTool.Hand || tool == CanvasTool.Select || tool == CanvasTool.Rot || tool == CanvasTool.Circle || tool == CanvasTool.AiBox;
    }

    /// <summary>
    /// Whether a project kind keeps a shape of this LabelMe shape type (new shapes from the palettes, AI suggestions, agent proposals,
    /// imports and Convert). Detect keeps every type; Position keeps only "oriented_rectangle" and "circle".
    /// </summary>
    public static bool IsShapeTypeAllowed(ProjectKind kind, string? shapeType)
    {
        if (kind != ProjectKind.Position) return true;
        return shapeType == "oriented_rectangle" || shapeType == "circle";
    }
}
