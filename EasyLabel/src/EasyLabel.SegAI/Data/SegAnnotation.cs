// Cloned from BeeCore/Unit/SegmentAI.Dataset.cs@4e7a4bf10 (SegAnnotation, :17-26)
using System.Drawing;

namespace EasyLabel.SegAI.Data;

[Serializable]
public sealed class SegAnnotation
{
    /// <summary>Kind of a Position-only disc: Points = centre and one edge point. Never produced for Detect projects.</summary>
    public const string CircleKind = "Circle";

    public string Kind = "Polygon";
    public byte Label = 1;
    public PointF[] Points = new PointF[0];
    public float Score;
    public PointF[][] Holes = new PointF[0][];
    public SegAnnotation Clone()
    {
        return new SegAnnotation
        {
            Kind = Kind, Label = Label, Score = Score, Points = (PointF[])Points.Clone(),
            Holes = Holes == null ? new PointF[0][] : Holes.Select(h => (PointF[])h.Clone()).ToArray()
        };
    }
}
