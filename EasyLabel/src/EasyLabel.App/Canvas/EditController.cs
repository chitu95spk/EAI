using EasyLabel.Core.Model;

namespace EasyLabel.App.Canvas;

public sealed class EditController
{
    private const float HitTolerance = 8f;

    public EditHitTestResult HitTest(LabelMeDocument? document, ViewTransform transform, Point screenPoint, int selectedIndex)
    {
        if (document is null)
        {
            return EditHitTestResult.None;
        }

        if (selectedIndex >= 0 && selectedIndex < document.Shapes.Count)
        {
            LabelShape selected = document.Shapes[selectedIndex];
            if (IsOrientedRectangle(selected) && selected.Points.Count >= 4)
            {
                PointF[] rotateHandles = GetRotationHandleScreenPoints(selected, transform);
                for (int handleIndex = 0; handleIndex < rotateHandles.Length; handleIndex++)
                {
                    if (Distance(rotateHandles[handleIndex], screenPoint) <= HitTolerance)
                    {
                        return new EditHitTestResult(selectedIndex, EditHitKind.Rotate, handleIndex);
                    }
                }
            }

            int vertex = HitVertex(selected, transform, screenPoint);
            if (vertex >= 0)
            {
                return new EditHitTestResult(selectedIndex, EditHitKind.Vertex, vertex);
            }
        }

        for (int i = document.Shapes.Count - 1; i >= 0; i--)
        {
            LabelShape shape = document.Shapes[i];
            if (Contains(shape, transform, screenPoint))
            {
                return new EditHitTestResult(i, EditHitKind.Move, -1);
            }
        }

        return EditHitTestResult.None;
    }

    public static bool IsOrientedRectangle(LabelShape shape)
    {
        return string.Equals(shape.ShapeType, "oriented_rectangle", StringComparison.Ordinal);
    }

    public static PointF[] GetRotationHandleScreenPoints(LabelShape shape, ViewTransform transform)
    {
        PointF[] corners = shape.Points.Take(4)
            .Select(point => transform.ToScreen(point[0], point[1])).ToArray();
        if (corners.Length != 4) return Array.Empty<PointF>();
        PointF center = new PointF(corners.Average(point => point.X), corners.Average(point => point.Y));
        PointF[] handles = new PointF[4];
        for (int i = 0; i < handles.Length; i++)
        {
            PointF a = corners[i];
            PointF b = corners[(i + 1) % corners.Length];
            PointF midpoint = new PointF((a.X + b.X) / 2f, (a.Y + b.Y) / 2f);
            float dx = midpoint.X - center.X;
            float dy = midpoint.Y - center.Y;
            float length = MathF.Sqrt(dx * dx + dy * dy);
            handles[i] = length < 0.001f
                ? midpoint
                : new PointF(midpoint.X + dx / length * 18f, midpoint.Y + dy / length * 18f);
        }

        return handles;
    }

    public static PointF[] GetRectangleImageCorners(LabelShape shape)
    {
        if (shape.Points.Count < 2)
        {
            return Array.Empty<PointF>();
        }

        double[] first = shape.Points[0];
        double[] second = shape.Points[1];
        float left = (float)Math.Min(first[0], second[0]);
        float top = (float)Math.Min(first[1], second[1]);
        float right = (float)Math.Max(first[0], second[0]);
        float bottom = (float)Math.Max(first[1], second[1]);

        return new[]
        {
            new PointF(left, top),
            new PointF(right, top),
            new PointF(right, bottom),
            new PointF(left, bottom)
        };
    }

    private static int HitVertex(LabelShape shape, ViewTransform transform, Point screenPoint)
    {
        if (shape.ShapeType == "rectangle" && shape.Points.Count >= 2)
        {
            PointF[] corners = GetRectangleImageCorners(shape);
            for (int i = 0; i < corners.Length; i++)
            {
                PointF screen = transform.ToScreen(corners[i].X, corners[i].Y);
                if (Distance(screen, screenPoint) <= HitTolerance)
                {
                    return i;
                }
            }

            return -1;
        }

        for (int i = 0; i < shape.Points.Count; i++)
        {
            double[] point = shape.Points[i];
            PointF screen = transform.ToScreen(point[0], point[1]);
            if (Distance(screen, screenPoint) <= HitTolerance)
            {
                return i;
            }
        }

        return -1;
    }

    private static bool Contains(LabelShape shape, ViewTransform transform, Point screenPoint)
    {
        if (shape.ShapeType == "rectangle" && shape.Points.Count >= 2)
        {
            PointF p1 = transform.ToScreen(shape.Points[0][0], shape.Points[0][1]);
            PointF p2 = transform.ToScreen(shape.Points[1][0], shape.Points[1][1]);
            RectangleF rect = new(
                Math.Min(p1.X, p2.X),
                Math.Min(p1.Y, p2.Y),
                Math.Abs(p2.X - p1.X),
                Math.Abs(p2.Y - p1.Y));
            return rect.Contains(screenPoint);
        }

        if (IsOrientedRectangle(shape) && shape.Points.Count >= 4)
        {
            PointF[] polygon = shape.Points.Take(4).Select(point => transform.ToScreen(point[0], point[1])).ToArray();
            return ContainsPolygon(polygon, screenPoint);
        }

        if (shape.Points.Count >= 3)
        {
            PointF[] polygon = shape.Points.Select(point => transform.ToScreen(point[0], point[1])).ToArray();
            return ContainsPolygon(polygon, screenPoint);
        }

        return false;
    }

    private static bool ContainsPolygon(PointF[] polygon, Point point)
    {
        bool inside = false;
        for (int i = 0, j = polygon.Length - 1; i < polygon.Length; j = i++)
        {
            bool intersects = polygon[i].Y > point.Y != polygon[j].Y > point.Y
                && point.X < (polygon[j].X - polygon[i].X) * (point.Y - polygon[i].Y) / (polygon[j].Y - polygon[i].Y) + polygon[i].X;
            if (intersects)
            {
                inside = !inside;
            }
        }

        return inside;
    }

    private static float Distance(PointF a, PointF b)
    {
        float dx = a.X - b.X;
        float dy = a.Y - b.Y;
        return MathF.Sqrt(dx * dx + dy * dy);
    }
}
