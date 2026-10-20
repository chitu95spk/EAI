import sys
def load(p):
    s = open(p, encoding='utf-8', newline='').read()
    return s.replace('\r\n', '\n'), '\r\n' in s

def save(p, s, crlf):
    if crlf:
        s = s.replace('\n', '\r\n')
    open(p, 'w', encoding='utf-8', newline='').write(s)

def rep(s, a, b):
    assert a in s, a
    return s.replace(a, b, 1)

p = 'ProposalStore.cs'
s, crlf = load(p)
s = rep(s, """    private int _newLabels;

    public ProposalStore(""", """    private int _newLabels;
    private readonly bool _allowOutside;

    public ProposalStore(""")
s = rep(s, """        _pending = request.PendingShapes ?? new List<RunShape>();
""", """        _pending = request.PendingShapes ?? new List<RunShape>();
        _allowOutside = request.AllowOutOfBounds;
""")
s = rep(s, """    /// <summary>Area of a fitted rotated box given as [TL, TR, BR, BL].</summary>""", """    /// <summary>
    /// Out-of-bounds boxes (<see cref="RunRequest.AllowOutOfBounds"/>): a shape is kept unclipped (an object cut by the image border keeps its true
    /// extent) when at least this share of its area lies inside the image; a shape that is mostly outside is rejected.
    /// </summary>
    public const double MinVisibleFraction = 0.2;

    /// <summary>Out-of-bounds boxes: the unclipped area may not exceed this many image areas (a runaway box is rejected).</summary>
    public const double MaxAreaInImages = 4.0;

    /// <summary>Area of a fitted rotated box given as [TL, TR, BR, BL].</summary>""")
s = rep(s, """List<double[]>? fitted = FitOrientedInImage(pending.Outer, w, h);""", """List<double[]>? fitted = FitOriented(pending.Outer, w, h);""")
s = rep(s, """            List<double[]>? fitted = FitOrientedInImage(points, w, h);
            if (fitted == null) return "oriented_rectangle has no area inside the image";
            points = fitted;""", """            List<double[]>? fitted = FitOriented(points, w, h);
            if (fitted == null) return _allowOutside
                ? "oriented_rectangle is degenerate or mostly outside the image (at least " + (int)(MinVisibleFraction * 100) + "% of its area must be inside)"
                : "oriented_rectangle has no area inside the image";
            points = fitted;""")
s = rep(s, """        else if (shapeType == "rectangle")
        {
            points = Geometry.Clamp(points, w, h);""", """        else if (shapeType == "rectangle" && _allowOutside)
        {
            if (points.Count != 2 && points.Count != 4) return "rectangle needs 2 or 4 points";
            double x1 = points.Min(p => p[0]), x2 = points.Max(p => p[0]);
            double y1 = points.Min(p => p[1]), y2 = points.Max(p => p[1]);
            if (x2 - x1 <= 0 || y2 - y1 <= 0) return "rectangle has no area";
            var box = new List<double[]> { new[] { x1, y1 }, new[] { x2, y1 }, new[] { x2, y2 }, new[] { x1, y2 } };
            if (!AcceptableOutside(box, w, h)) return "rectangle is mostly outside the image (at least " + (int)(MinVisibleFraction * 100) + "% of its area must be inside)";
            points = Geometry.Round(new List<double[]> { new[] { x1, y1 }, new[] { x2, y2 } });
        }
        else if (shapeType == "rectangle")
        {
            points = Geometry.Clamp(points, w, h);""")
s = rep(s, """        else
        {
            points = Geometry.Clamp(points, w, h);
            if (points.Count < 3) return "polygon needs at least 3 points";
        }""", """        else if (_allowOutside)
        {
            if (points.Count < 3) return "polygon needs at least 3 points";
            if (!AcceptableOutside(points, w, h)) return "polygon is mostly outside the image (at least " + (int)(MinVisibleFraction * 100) + "% of its area must be inside)";
            points = Geometry.Round(points);
        }
        else
        {
            points = Geometry.Clamp(points, w, h);
            if (points.Count < 3) return "polygon needs at least 3 points";
        }""")
s = rep(s, """    /// <summary>
    /// The smallest rotated rectangle around the outline after clipping""", """    /// <summary>
    /// The smallest rotated rectangle around ALL points of the outline, as [TL, TR, BR, BL]. With out-of-bounds boxes allowed the outline is NOT
    /// clipped, so a part cut by the image border keeps the box it really has; the box is rejected (null) when it is degenerate or fails
    /// <see cref="AcceptableOutside"/>. Without the setting it is <see cref="FitOrientedInImage"/> (clipped to the image).
    /// </summary>
    private List<double[]>? FitOriented(IReadOnlyList<double[]> outline, int w, int h)
    {
        if (!_allowOutside) return FitOrientedInImage(outline, w, h);
        List<double[]>? fitted = ShapeConverter.FitOrientedRectangle(outline.Select(p => new[] { p[0], p[1] }).ToList());
        if (fitted == null || fitted.Count != 4 || RectangleArea(fitted) < MinOrientedArea) return null;
        return AcceptableOutside(fitted, w, h) ? Geometry.Round(fitted) : null;
    }

    /// <summary>
    /// True when a shape that may leave the image is acceptable: it has area, at least <see cref="MinVisibleFraction"/> of that area lies inside
    /// the image, and it is not larger than <see cref="MaxAreaInImages"/> image areas. A shape with no area (a line) must lie fully inside.
    /// </summary>
    internal static bool AcceptableOutside(IReadOnlyList<double[]> ring, int w, int h)
    {
        double area = ShapeOverlap.Area(ring.ToList());
        if (!(area > 0)) return ring.All(p => p[0] >= 0 && p[0] <= w && p[1] >= 0 && p[1] <= h);
        if (area > MaxAreaInImages * w * h) return false;
        List<double[]> clipped = Geometry.ClipToImage(ring, w, h);
        return clipped.Count >= 3 && ShapeOverlap.Area(clipped) >= MinVisibleFraction * area;
    }

    /// <summary>
    /// The smallest rotated rectangle around the outline after clipping""")
s = rep(s, """    /// <summary>Clamps to [0,width] x [0,height] and rounds to 2 decimals.</summary>""", """    /// <summary>Rounds to 2 decimals without clamping (a shape that may leave the image).</summary>
    public static List<double[]> Round(IEnumerable<double[]> points)
    {
        return points.Select(p => new[] { Math.Round(p[0], 2), Math.Round(p[1], 2) }).ToList();
    }

    /// <summary>Clamps to [0,width] x [0,height] and rounds to 2 decimals.</summary>""")
save(p, s, crlf)

p = 'RunRequest.cs'
s, crlf = load(p)
s = rep(s, """    [JsonProperty("modelsFolder")]""", """    /// <summary>
    /// True (the default, also when the field is absent): a proposed box may extend outside the image (an object cut by the border keeps its true
    /// box; at least <see cref="ProposalStore.MinVisibleFraction"/> of it must be inside). False: proposals are clipped to the image as before.
    /// </summary>
    [JsonProperty("allowOutOfBounds")]
    public bool AllowOutOfBounds { get; set; } = true;

    [JsonProperty("modelsFolder")]""")
save(p, s, crlf)
