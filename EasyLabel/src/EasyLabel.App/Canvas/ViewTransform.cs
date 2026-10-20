namespace EasyLabel.App.Canvas;

public readonly struct ViewTransform
{
    public ViewTransform(double scale, double offsetX, double offsetY)
    {
        Scale = scale;
        OffsetX = offsetX;
        OffsetY = offsetY;
    }

    public double Scale { get; }

    public double OffsetX { get; }

    public double OffsetY { get; }

    public PointF ToScreen(double ix, double iy)
    {
        return new PointF(
            (float)(ix * Scale + OffsetX),
            (float)(iy * Scale + OffsetY));
    }

    public PointF ToImage(float sx, float sy)
    {
        if (Scale <= 0)
        {
            return PointF.Empty;
        }

        return new PointF(
            (float)((sx - OffsetX) / Scale),
            (float)((sy - OffsetY) / Scale));
    }
}
