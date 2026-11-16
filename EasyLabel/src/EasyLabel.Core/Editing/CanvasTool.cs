namespace EasyLabel.Core.Editing;

/// <summary>Active canvas tool. Select edits existing shapes; the others create one shape kind.</summary>
public enum CanvasTool
{
    Select,
    /// <summary>Axis-aligned "rectangle" (press, drag, release).</summary>
    Rect,
    /// <summary>"oriented_rectangle" box: two clicks for one edge, a third click for the width.</summary>
    Rot,
    /// <summary>"polygon": one click per vertex.</summary>
    Polygon,
    /// <summary>"circle": click the centre, click a point on the edge.</summary>
    Circle,
    /// <summary>"point": one click.</summary>
    Point,
    /// <summary>"line": click both end points.</summary>
    Line,
    /// <summary>"linestrip": one click per vertex, Enter or double-click finishes.</summary>
    LineStrip,
    /// <summary>AI-Points: positive / negative click prompts for an AI model. The edit session ignores pointer input; the UI owns the prompt.</summary>
    AiPoints,
    /// <summary>AI-Box: a dragged rectangle prompt for an AI model. The edit session ignores pointer input; the UI owns the prompt.</summary>
    AiBox,
    /// <summary>Hand: drag the image to pan the view without editing shapes.</summary>
    Hand,
    /// <summary>Count: click pastes a fixed-size circle or square, right click deletes the nearest one (fastnet_gui.py behaviour).</summary>
    Count
}

[Flags]
public enum EditModifiers
{
    None = 0,
    Shift = 1,
    Ctrl = 2,
    Alt = 4
}

public enum EditPointerButton
{
    Left,
    Right,
    Middle
}
