// Cloned from BeeInterface/ShapeEditing/GeometryHelper.cs@10f0871f5
using System.Drawing;

namespace EasyLabel.Core.Geometry;

/// <summary>
/// Immutable, ordered four-point box. Point order is always TL, TR, BR, BL with a positive
/// cross product (x axis, y axis); malformed input is rejected rather than silently reordered.
/// Image coordinates: x right, y down.
/// </summary>
public readonly struct RotatedBoxGeometry
{
    private const float Epsilon = 0.0001f;

    private readonly PointF _topLeft;
    private readonly PointF _xAxis;
    private readonly PointF _yAxis;
    private readonly float _width;
    private readonly float _height;
    // Keeps the authored angle representation (270 rather than -90) so absolute snapping stays stable.
    private readonly float _rotationDegrees;

    private RotatedBoxGeometry(PointF topLeft, PointF xAxis, PointF yAxis, float width, float height, float rotationDegrees)
    {
        _topLeft = topLeft;
        _xAxis = xAxis;
        _yAxis = yAxis;
        _width = width;
        _height = height;
        _rotationDegrees = rotationDegrees;
    }

    public PointF Center => Add(_topLeft, Add(Scale(_xAxis, _width * .5f), Scale(_yAxis, _height * .5f)));

    public float RotationDegrees => _rotationDegrees;

    public float Width => _width;

    public float Height => _height;

    /// <summary>Unit vector along TL to TR.</summary>
    public PointF XAxis => _xAxis;

    /// <summary>Unit vector along TL to BL.</summary>
    public PointF YAxis => _yAxis;

    public static bool TryCreate(PointF[]? points, out RotatedBoxGeometry box)
    {
        box = default;
        if (points == null || points.Length != 4 || !AreFinite(points))
            return false;

        PointF x = Subtract(points[1], points[0]);
        PointF y = Subtract(points[3], points[0]);
        float width = Length(x);
        float height = Length(y);
        if (width < Epsilon || height < Epsilon)
            return false;

        x = Scale(x, 1f / width);
        y = Scale(y, 1f / height);
        if (Math.Abs(Dot(x, y)) > .001f || Cross(x, y) <= Epsilon)
            return false;

        PointF expectedBottomRight = Add(points[0], Add(Scale(x, width), Scale(y, height)));
        if (Distance(expectedBottomRight, points[2]) > Math.Max(.01f, Math.Max(width, height) * .001f))
            return false;

        box = new RotatedBoxGeometry(points[0], x, y, width, height, (float)(Math.Atan2(x.Y, x.X) * 180.0 / Math.PI));
        return true;
    }

    /// <summary>Builds a box from a local rectangle (relative to <paramref name="center"/>) rotated by <paramref name="oriented_rectangle"/> degrees.</summary>
    public static RotatedBoxGeometry FromRectRotate(RectangleF rect, PointF center, float rotation)
    {
        PointF x = RotateVector(new PointF(1f, 0f), rotation);
        PointF y = RotateVector(new PointF(0f, 1f), rotation);
        PointF topLeft = Add(center, Add(Scale(x, rect.Left), Scale(y, rect.Top)));
        return new RotatedBoxGeometry(topLeft, x, y, Math.Max(2f, rect.Width), Math.Max(2f, rect.Height), rotation);
    }

    public static PointF RotateVector(PointF v, float deg)
    {
        float rad = deg * (float)Math.PI / 180f;
        float cos = (float)Math.Cos(rad), sin = (float)Math.Sin(rad);
        return new PointF(v.X * cos - v.Y * sin, v.X * sin + v.Y * cos);
    }

    public PointF[] ToPoints()
    {
        PointF topRight = Add(_topLeft, Scale(_xAxis, _width));
        PointF bottomLeft = Add(_topLeft, Scale(_yAxis, _height));
        return new[] { _topLeft, topRight, Add(topRight, Scale(_yAxis, _height)), bottomLeft };
    }

    public bool TryMove(PointF mouseDown, PointF mouseNow, out RotatedBoxGeometry candidate)
    {
        PointF delta = Subtract(mouseNow, mouseDown);
        candidate = new RotatedBoxGeometry(Add(_topLeft, delta), _xAxis, _yAxis, _width, _height, _rotationDegrees);
        return IsFinite(candidate.Center);
    }

    public bool TryResize(int vertexIndex, PointF mouseDown, PointF mouseNow, float minimumSize, out RotatedBoxGeometry candidate)
    {
        candidate = default;
        if (vertexIndex < 0 || vertexIndex > 3 || minimumSize <= 0f)
            return false;

        PointF[] original = ToPoints();
        PointF movedCorner = Add(original[vertexIndex], Subtract(mouseNow, mouseDown));
        int opposite = (vertexIndex + 2) % 4;
        PointF anchor = original[opposite];
        PointF delta = Subtract(movedCorner, anchor);
        float signedWidth = Dot(delta, _xAxis);
        float signedHeight = Dot(delta, _yAxis);
        bool xPositive = vertexIndex == 1 || vertexIndex == 2;
        bool yPositive = vertexIndex == 2 || vertexIndex == 3;
        float width = xPositive ? Math.Max(minimumSize, signedWidth) : Math.Max(minimumSize, -signedWidth);
        float height = yPositive ? Math.Max(minimumSize, signedHeight) : Math.Max(minimumSize, -signedHeight);
        PointF topLeft;
        switch (vertexIndex)
        {
            case 0: topLeft = Add(anchor, Add(Scale(_xAxis, -width), Scale(_yAxis, -height))); break;
            case 1: topLeft = Add(anchor, Scale(_yAxis, -height)); break;
            case 2: topLeft = anchor; break;
            default: topLeft = Add(anchor, Scale(_xAxis, -width)); break;
        }
        candidate = new RotatedBoxGeometry(topLeft, _xAxis, _yAxis, width, height, _rotationDegrees);
        return IsFinite(candidate.Center);
    }

    public bool TryRotate(PointF mouseDown, PointF mouseNow, bool snapToFifteenDegrees, out RotatedBoxGeometry candidate)
    {
        candidate = default;
        PointF center = Center;
        PointF start = Subtract(mouseDown, center);
        PointF now = Subtract(mouseNow, center);
        if (Length(start) < Epsilon || Length(now) < Epsilon)
            return false;
        float delta = (float)((Math.Atan2(now.Y, now.X) - Math.Atan2(start.Y, start.X)) * 180.0 / Math.PI);
        while (delta > 180f) delta -= 360f;
        while (delta < -180f) delta += 360f;
        float finalAngle = _rotationDegrees + delta;
        if (snapToFifteenDegrees)
            finalAngle = (float)Math.Round(finalAngle / 15f) * 15f;
        float appliedDelta = finalAngle - _rotationDegrees;
        PointF x = RotateVector(_xAxis, appliedDelta);
        PointF y = RotateVector(_yAxis, appliedDelta);
        PointF topLeft = Add(center, Add(Scale(x, -_width * .5f), Scale(y, -_height * .5f)));
        candidate = new RotatedBoxGeometry(topLeft, x, y, _width, _height, finalAngle);
        return IsFinite(candidate.Center);
    }

    public bool TryConstrainMoveToBounds(SizeF bounds, out RotatedBoxGeometry candidate)
    {
        candidate = this;
        if (bounds.Width <= 0f || bounds.Height <= 0f)
            return false;
        PointF[] points = ToPoints();
        float minX = points.Min(p => p.X), maxX = points.Max(p => p.X);
        float minY = points.Min(p => p.Y), maxY = points.Max(p => p.Y);
        if (maxX - minX > bounds.Width + Epsilon || maxY - minY > bounds.Height + Epsilon)
            return false;
        float dx = minX < 0f ? -minX : maxX > bounds.Width ? bounds.Width - maxX : 0f;
        float dy = minY < 0f ? -minY : maxY > bounds.Height ? bounds.Height - maxY : 0f;
        candidate = new RotatedBoxGeometry(Add(_topLeft, new PointF(dx, dy)), _xAxis, _yAxis, _width, _height, _rotationDegrees);
        return true;
    }

    public bool IsInside(SizeF bounds)
    {
        if (bounds.Width <= 0f || bounds.Height <= 0f) return false;
        return ToPoints().All(p => p.X >= -Epsilon && p.Y >= -Epsilon && p.X <= bounds.Width + Epsilon && p.Y <= bounds.Height + Epsilon);
    }

    private static bool AreFinite(IEnumerable<PointF> points) => points.All(IsFinite);
    private static bool IsFinite(PointF p) => !float.IsNaN(p.X) && !float.IsNaN(p.Y) && !float.IsInfinity(p.X) && !float.IsInfinity(p.Y);
    private static PointF Add(PointF a, PointF b) => new PointF(a.X + b.X, a.Y + b.Y);
    private static PointF Subtract(PointF a, PointF b) => new PointF(a.X - b.X, a.Y - b.Y);
    private static PointF Scale(PointF p, float amount) => new PointF(p.X * amount, p.Y * amount);
    private static float Dot(PointF a, PointF b) => a.X * b.X + a.Y * b.Y;
    private static float Cross(PointF a, PointF b) => a.X * b.Y - a.Y * b.X;
    private static float Length(PointF p) => (float)Math.Sqrt(Dot(p, p));
    private static float Distance(PointF a, PointF b) => Length(Subtract(a, b));
}
