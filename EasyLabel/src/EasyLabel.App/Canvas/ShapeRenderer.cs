using EasyLabel.Core.Model;

namespace EasyLabel.App.Canvas;

public sealed class ShapeRenderer
{
    private const float HandleSize = 7f;
    private readonly ClassPalette _palette = new();

    public void Render(Graphics graphics, LabelMeDocument? document, ViewTransform transform, int selectedIndex)
    {
        if (document is null)
        {
            return;
        }

        for (int i = 0; i < document.Shapes.Count; i++)
        {
            LabelShape shape = document.Shapes[i];
            Color color = _palette.ForLabel(shape.Label);
            bool selected = i == selectedIndex;
            DrawShape(graphics, shape, transform, color, selected);
        }
    }

    private static void DrawShape(Graphics graphics, LabelShape shape, ViewTransform transform, Color color, bool selected)
    {
        using Pen pen = new(color, selected ? 2.5f : 1.5f);
        using Brush textBrush = new SolidBrush(color);
        using Brush translucentFill = new SolidBrush(Color.FromArgb(42, color));

        if (shape.ShapeType == "rectangle" && shape.Points.Count >= 2)
        {
            RectangleF rect = ToScreenRectangle(shape.Points[0], shape.Points[1], transform);
            graphics.DrawRectangle(pen, rect.X, rect.Y, rect.Width, rect.Height);
            DrawLabel(graphics, shape.Label, rect.Location, textBrush);
        }
        else if (EditController.IsOrientedRectangle(shape) && shape.Points.Count >= 4)
        {
            PointF[] points = ToScreenPoints(shape.Points.Take(4), transform);
            graphics.DrawPolygon(pen, points);
            DrawLabel(graphics, shape.Label, points[0], textBrush);
        }
        else if (shape.ShapeType == "polygon" && shape.Points.Count >= 2)
        {
            PointF[] points = ToScreenPoints(shape.Points, transform);
            if (points.Length >= 3)
            {
                graphics.FillPolygon(translucentFill, points);
                graphics.DrawPolygon(pen, points);
            }
            else
            {
                graphics.DrawLines(pen, points);
            }

            DrawLabel(graphics, shape.Label, points[0], textBrush);
        }
        else if (shape.ShapeType == "circle" && shape.Points.Count >= 2)
        {
            PointF center = transform.ToScreen(shape.Points[0][0], shape.Points[0][1]);
            PointF edge = transform.ToScreen(shape.Points[1][0], shape.Points[1][1]);
            float radius = Distance(center, edge);
            RectangleF ellipse = new(center.X - radius, center.Y - radius, radius * 2f, radius * 2f);
            graphics.DrawEllipse(pen, ellipse);
            DrawLabel(graphics, shape.Label, ellipse.Location, textBrush);
        }
        else if (shape.ShapeType == "line" && shape.Points.Count >= 2)
        {
            PointF[] points = ToScreenPoints(shape.Points.Take(2), transform);
            graphics.DrawLine(pen, points[0], points[1]);
            DrawLabel(graphics, shape.Label, points[0], textBrush);
        }
        else if (shape.ShapeType == "linestrip" && shape.Points.Count >= 2)
        {
            PointF[] points = ToScreenPoints(shape.Points, transform);
            graphics.DrawLines(pen, points);
            DrawLabel(graphics, shape.Label, points[0], textBrush);
        }
        else if (shape.ShapeType == "point" && shape.Points.Count >= 1)
        {
            PointF point = transform.ToScreen(shape.Points[0][0], shape.Points[0][1]);
            DrawPoint(graphics, pen, point);
            DrawLabel(graphics, shape.Label, point, textBrush);
        }
        else if (shape.ShapeType == "mask" && shape.Points.Count >= 2)
        {
            PointF[] points = ToScreenPoints(shape.Points, transform);
            if (points.Length >= 3)
            {
                graphics.FillPolygon(translucentFill, points);
                graphics.DrawPolygon(pen, points);
                DrawLabel(graphics, shape.Label, points[0], textBrush);
            }
            else
            {
                RectangleF rect = ToScreenRectangle(shape.Points[0], shape.Points[1], transform);
                graphics.FillRectangle(translucentFill, rect);
                graphics.DrawRectangle(pen, rect.X, rect.Y, rect.Width, rect.Height);
                DrawLabel(graphics, shape.Label, rect.Location, textBrush);
            }
        }

        if (selected)
        {
            DrawHandles(graphics, shape, transform);
        }
    }

    private static void DrawHandles(Graphics graphics, LabelShape shape, ViewTransform transform)
    {
        using Brush fill = new SolidBrush(Color.White);
        using Pen border = new(Color.FromArgb(38, 45, 56));

        foreach (PointF point in GetHandlePoints(shape))
        {
            PointF screen = transform.ToScreen(point.X, point.Y);
            RectangleF handle = new(
                screen.X - HandleSize / 2f,
                screen.Y - HandleSize / 2f,
                HandleSize,
                HandleSize);
            graphics.FillRectangle(fill, handle);
            graphics.DrawRectangle(border, handle.X, handle.Y, handle.Width, handle.Height);
        }

        if (EditController.IsOrientedRectangle(shape) && shape.Points.Count >= 4)
        {
            foreach (PointF rotateHandle in EditController.GetRotationHandleScreenPoints(shape, transform))
            {
                RectangleF marker = new(rotateHandle.X - 4f, rotateHandle.Y - 4f, 8f, 8f);
                graphics.FillEllipse(fill, marker);
                graphics.DrawEllipse(border, marker);
            }
        }
    }

    private static IEnumerable<PointF> GetHandlePoints(LabelShape shape)
    {
        if (shape.ShapeType == "rectangle" && shape.Points.Count >= 2)
        {
            return EditController.GetRectangleImageCorners(shape);
        }

        return shape.Points.Select(point => new PointF((float)point[0], (float)point[1]));
    }

    private static RectangleF ToScreenRectangle(double[] first, double[] second, ViewTransform transform)
    {
        PointF p1 = transform.ToScreen(first[0], first[1]);
        PointF p2 = transform.ToScreen(second[0], second[1]);
        float left = Math.Min(p1.X, p2.X);
        float top = Math.Min(p1.Y, p2.Y);
        return new RectangleF(left, top, Math.Abs(p2.X - p1.X), Math.Abs(p2.Y - p1.Y));
    }

    private static PointF[] ToScreenPoints(IEnumerable<double[]> points, ViewTransform transform)
    {
        return points
            .Select(point => transform.ToScreen(point[0], point[1]))
            .ToArray();
    }

    private static void DrawPoint(Graphics graphics, Pen pen, PointF point)
    {
        const float markerRadius = 4f;
        graphics.DrawEllipse(pen, point.X - markerRadius, point.Y - markerRadius, markerRadius * 2f, markerRadius * 2f);
        graphics.DrawLine(pen, point.X - markerRadius - 2f, point.Y, point.X + markerRadius + 2f, point.Y);
        graphics.DrawLine(pen, point.X, point.Y - markerRadius - 2f, point.X, point.Y + markerRadius + 2f);
    }

    private static void DrawLabel(Graphics graphics, string? label, PointF point, Brush brush)
    {
        if (!string.IsNullOrWhiteSpace(label))
        {
            Font font = SystemFonts.DefaultFont ?? new Font(FontFamily.GenericSansSerif, 8.25f);
            graphics.DrawString(label, font, brush, point.X + 4f, point.Y + 4f);
        }
    }

    private static float Distance(PointF a, PointF b)
    {
        float dx = a.X - b.X;
        float dy = a.Y - b.Y;
        return MathF.Sqrt(dx * dx + dy * dy);
    }
}
