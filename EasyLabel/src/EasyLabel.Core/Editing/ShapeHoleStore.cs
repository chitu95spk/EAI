using System.Drawing;
using System.Globalization;
using System.Text;
using EasyLabel.Core.Model;

namespace EasyLabel.Core.Editing;

/// <summary>Lets the edit session tell a hole store that a shape's geometry changed (or was copied).</summary>
public interface IHoleCarrier
{
    /// <summary>
    /// Registers the holes of <paramref name="from"/> under the geometry of <paramref name="to"/>.
    /// <paramref name="map"/> is the rigid point transform (move/rotate) or null to keep the holes unchanged.
    /// </summary>
    void Carry(LabelShape from, LabelShape to, Func<PointF, PointF>? map);

    /// <summary>True when holes are registered for the geometry of <paramref name="shape"/>.</summary>
    bool HasHoles(LabelShape shape) => false;
}

/// <summary>
/// Geometry key (shape type + points, NOT label) to holes. Grow-only for one image session, so after
/// undo/redo restores a geometry its holes are found again. <typeparamref name="TH"/> is the hole payload
/// (SegAI uses <c>PointF[][]</c>); <c>transform</c> applies a point mapping to a payload.
/// </summary>
public sealed class ShapeHoleStore<TH> : IHoleCarrier
{
    private readonly Dictionary<string, TH> _holes = new Dictionary<string, TH>(StringComparer.Ordinal);
    private readonly Func<TH, Func<PointF, PointF>, TH> _transform;

    public ShapeHoleStore(Func<TH, Func<PointF, PointF>, TH> transform)
    {
        _transform = transform ?? throw new ArgumentNullException(nameof(transform));
    }

    public int Count => _holes.Count;

    public static string KeyOf(LabelShape shape)
    {
        var sb = new StringBuilder(shape.ShapeType ?? string.Empty);
        foreach (double[] p in shape.Points)
        {
            if (p == null || p.Length < 2) continue;
            sb.Append('|').Append(p[0].ToString("R", CultureInfo.InvariantCulture))
              .Append(',').Append(p[1].ToString("R", CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    public void Set(LabelShape shape, TH holes) => _holes[KeyOf(shape)] = holes;

    public bool HasHoles(LabelShape shape) => _holes.ContainsKey(KeyOf(shape));

    public bool TryGet(LabelShape shape, out TH holes) => _holes.TryGetValue(KeyOf(shape), out holes!);

    public void Carry(LabelShape from, LabelShape to, Func<PointF, PointF>? map)
    {
        if (!_holes.TryGetValue(KeyOf(from), out TH? source)) return;
        string target = KeyOf(to);
        // Never overwrite: an existing entry is either identical geometry or user-authored holes.
        if (_holes.ContainsKey(target)) return;
        _holes[target] = map == null ? source : _transform(source, map);
    }
}
