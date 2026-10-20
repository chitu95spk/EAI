// Idea cloned from EasyLabel.App/Canvas/ViewTransform.cs (pure scale + offset, no UI reference)
using System.Drawing;

namespace EasyLabel.Core.Geometry;

public readonly struct ViewTransform
{
    public const double MinScale = 0.05;
    public const double MaxScale = 40.0;
    public const double ZoomStep = 1.25;

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
        => new PointF((float)(ix * Scale + OffsetX), (float)(iy * Scale + OffsetY));

    public PointF ToImage(float sx, float sy)
    {
        if (Scale <= 0)
            return PointF.Empty;
        return new PointF((float)((sx - OffsetX) / Scale), (float)((sy - OffsetY) / Scale));
    }

    /// <summary>
    /// Keeps the scale and the fractional image position at the viewport centre, clamping each axis to the image edges.
    /// An image smaller than the viewport is centred on that axis; unknown previous dimensions use the image centre.
    /// </summary>
    public static ViewTransform KeepCentre(ViewTransform previous, double previousImageWidth, double previousImageHeight,
        double imageWidth, double imageHeight, double viewWidth, double viewHeight)
    {
        double width = imageWidth * previous.Scale;
        double height = imageHeight * previous.Scale;
        double offsetX = previousImageWidth == imageWidth ? previous.OffsetX
            : previousImageWidth > 0.0 ? viewWidth / 2.0 - (viewWidth / 2.0 - previous.OffsetX) * (imageWidth / previousImageWidth)
            : (viewWidth - width) / 2.0;
        double offsetY = previousImageHeight == imageHeight ? previous.OffsetY
            : previousImageHeight > 0.0 ? viewHeight / 2.0 - (viewHeight / 2.0 - previous.OffsetY) * (imageHeight / previousImageHeight)
            : (viewHeight - height) / 2.0;
        offsetX = width <= viewWidth ? (viewWidth - width) / 2.0 : Math.Min(0.0, Math.Max(viewWidth - width, offsetX));
        offsetY = height <= viewHeight ? (viewHeight - height) / 2.0 : Math.Min(0.0, Math.Max(viewHeight - height, offsetY));
        return new ViewTransform(previous.Scale, offsetX, offsetY);
    }

    /// <summary>Zooms by <paramref name="steps"/> x1.25 steps (negative = out), keeping the image point under the screen point fixed.</summary>
    public ViewTransform ZoomAt(double screenX, double screenY, int steps)
        => ZoomAtFactor(screenX, screenY, Math.Pow(ZoomStep, steps));

    public ViewTransform ZoomAtFactor(double screenX, double screenY, double factor)
    {
        double newScale = Math.Min(MaxScale, Math.Max(MinScale, Scale * factor));
        if (Scale <= 0 || double.IsNaN(newScale))
            return this;
        double ratio = newScale / Scale;
        return new ViewTransform(newScale, screenX - (screenX - OffsetX) * ratio, screenY - (screenY - OffsetY) * ratio);
    }
}
