using System.Drawing;
using EasyLabel.Core.Commands;
using EasyLabel.Core.Model;

namespace EasyLabel.Core.Editing;

/// <summary>In-memory copy/paste of label shapes. Pasted shapes are offset and kept fully inside the image.</summary>
public sealed class ShapeClipboard
{
    public const double DefaultOffset = 10.0;

    private readonly List<LabelShape> _items = new List<LabelShape>();
    private int _pasteCount;

    public bool HasContent => _items.Count > 0;

    public IReadOnlyList<LabelShape> Items => _items;

    public void Copy(IEnumerable<LabelShape> shapes)
    {
        _items.Clear();
        foreach (LabelShape s in shapes) _items.Add(DocumentEditor.Clone(s));
        _pasteCount = 0;
    }

    /// <summary>Next paste: clipboard shapes offset by a growing step, clamped inside the image.</summary>
    public List<LabelShape> NextPaste(int imageWidth, int imageHeight, out PointF appliedOffset)
    {
        _pasteCount++;
        return Translate(_items, DefaultOffset * _pasteCount, DefaultOffset * _pasteCount, imageWidth, imageHeight, out appliedOffset);
    }

    /// <summary>
    /// Clones <paramref name="shapes"/> translated by (dx, dy); the whole group is then pushed back so its
    /// bounding box stays inside the image (offset reduced if needed).
    /// </summary>
    public static List<LabelShape> Translate(IEnumerable<LabelShape> shapes, double dx, double dy,
        int imageWidth, int imageHeight, out PointF appliedOffset)
    {
        List<LabelShape> clones = shapes.Select(DocumentEditor.Clone).ToList();
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (LabelShape s in clones)
            foreach (double[] p in s.Points)
            {
                minX = Math.Min(minX, p[0]); maxX = Math.Max(maxX, p[0]);
                minY = Math.Min(minY, p[1]); maxY = Math.Max(maxY, p[1]);
            }
        if (clones.Count == 0 || minX > maxX)
        {
            appliedOffset = PointF.Empty;
            return clones;
        }
        dx = Math.Min(Math.Max(dx, -minX), imageWidth - maxX);
        dy = Math.Min(Math.Max(dy, -minY), imageHeight - maxY);
        foreach (LabelShape s in clones)
            foreach (double[] p in s.Points)
            {
                p[0] += dx;
                p[1] += dy;
            }
        appliedOffset = new PointF((float)dx, (float)dy);
        return clones;
    }
}
