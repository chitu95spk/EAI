namespace EasyLabel.App.Canvas;

public readonly record struct EditHitTestResult(int ShapeIndex, EditHitKind Kind, int VertexIndex)
{
    public static EditHitTestResult None => new(-1, EditHitKind.None, -1);
}
