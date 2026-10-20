using System.Globalization;
using EasyLabel.Core.Editing;
using EasyLabel.Core.Io;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace EasyLabel.Ai.Mcp;

/// <summary>A SAM2 result waiting for propose_shapes: outer ring and holes in image pixels.</summary>
internal sealed record PendingShape(string Id, string? Label, List<double[]> Outer, List<List<double[]>> Holes, double Score);

internal sealed class StoredProposal
{
    public string Id { get; init; } = string.Empty;

    public string Label { get; init; } = string.Empty;

    public string ShapeType { get; init; } = "polygon";

    public List<double[]> Points { get; init; } = new List<double[]>();

    public List<List<double[]>> Holes { get; init; } = new List<List<double[]>>();

    public double? Score { get; init; }

    public string Note { get; init; } = string.Empty;

    public string Source { get; init; } = "agent";

    /// <summary>"replace" or "remove" for a review of an existing shape; null for a plain new shape.</summary>
    public string? Action { get; init; }

    /// <summary>Index of the reviewed existing shape (list_shapes id); null for a plain new shape.</summary>
    public int? TargetIndex { get; init; }

    /// <summary>Fingerprint of the reviewed shape as the run saw it (<see cref="ShapeFingerprint"/>).</summary>
    public string? TargetHash { get; init; }
}

/// <summary>
/// Validates agent proposals and keeps <c>&lt;run&gt;/proposals.json</c> equal to the full current list (atomic replace on every call).
/// </summary>
internal sealed class ProposalStore
{
    public const string FileName = "proposals.json";

    private readonly string _path;
    private readonly ImageSource _image;
    private readonly List<RunShape> _existing;
    private readonly List<RunShape> _pending;
    private List<double[]>?[]? _existingRings;
    private List<double[]>?[]? _pendingRings;
    private readonly Dictionary<string, string> _classes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    private readonly List<StoredProposal> _items = new List<StoredProposal>();
    private int _autoId;
    private int _newLabels;
    private readonly bool _allowOutside;

    public ProposalStore(string runDirectory, RunRequest request, ImageSource image)
    {
        _path = Path.Combine(runDirectory, FileName);
        _image = image;
        _existing = request.ExistingShapes ?? new List<RunShape>();
        _pending = request.PendingShapes ?? new List<RunShape>();
        _allowOutside = request.AllowOutOfBounds;
        foreach (string name in request.ClassNames)
        {
            string trimmed = (name ?? string.Empty).Trim();
            if (trimmed.Length > 0 && !_classes.ContainsKey(trimmed)) _classes[trimmed] = trimmed;
        }
    }

    public int Count => _items.Count;

    /// <summary>A copy of the proposals made so far (read-only helpers such as render_overlay and measure).</summary>
    public IReadOnlyList<StoredProposal> Snapshot() => _items.ToList();

    /// <summary>Longest accepted label text.</summary>
    public const int MaxLabelLength = 64;

    /// <summary>Most labels one run may add to the project's classes (a runaway agent must not flood the label list).</summary>
    public const int MaxNewLabels = 16;

    /// <summary>Smallest area (square image pixels) of an oriented_rectangle; less is rejected as having no area.</summary>
    private const double MinOrientedArea = 1.0;

    /// <summary>
    /// Out-of-bounds boxes (<see cref="RunRequest.AllowOutOfBounds"/>): a shape is kept unclipped (an object cut by the image border keeps its true
    /// extent) when at least this share of its area lies inside the image; a shape that is mostly outside is rejected.
    /// </summary>
    public const double MinVisibleFraction = 0.2;

    /// <summary>Out-of-bounds boxes: the unclipped area may not exceed this many image areas (a runaway box is rejected).</summary>
    public const double MaxAreaInImages = 4.0;

    /// <summary>Area of a fitted rotated box given as [TL, TR, BR, BL].</summary>
    private static double RectangleArea(IReadOnlyList<double[]> corners)
    {
        double ax = corners[1][0] - corners[0][0], ay = corners[1][1] - corners[0][1];
        double bx = corners[3][0] - corners[0][0], by = corners[3][1] - corners[0][1];
        return Math.Abs(ax * by - ay * bx);
    }

    /// <summary>
    /// A class of the project (case-insensitive, returned in its canonical spelling) or a NEW label the agent names: new labels are
    /// created when the user accepts the shape, so a task like "mark every Pad" works without adding the label first.
    /// </summary>
    private string? ResolveLabel(string rawLabel, out string? error)
    {
        error = null;
        foreach (string known in _classes.Values)
        {
            if (string.Equals(known, rawLabel, StringComparison.OrdinalIgnoreCase)) return known;
        }

        if (rawLabel.Length > MaxLabelLength)
        {
            error = "label is too long (at most " + MaxLabelLength + " characters)";
            return null;
        }

        foreach (char c in rawLabel)
        {
            if (char.IsControl(c))
            {
                error = "label must not contain control characters";
                return null;
            }
        }

        if (_newLabels >= MaxNewLabels)
        {
            error = "too many new labels (at most " + MaxNewLabels + " per run); use one of: " + string.Join(", ", _classes.Values);
            return null;
        }

        _classes[rawLabel] = rawLabel;
        _newLabels++;
        return rawLabel;
    }

    /// <summary>Validates every item, applies the accepted ones and rewrites the file. Returns the per-item report text (JSON).</summary>
    public string Propose(JArray shapes, Func<string, PendingShape?> findPending)
    {
        var report = new JArray();
        int accepted = 0;
        for (int i = 0; i < shapes.Count; i++)
        {
            string? reason;
            StoredProposal? proposal = null;
            try
            {
                reason = Validate(shapes[i], findPending, out proposal);
            }
            catch (Exception ex) when (ex is InvalidCastException || ex is FormatException || ex is ArgumentException)
            {
                reason = "malformed item: " + ex.Message;
            }

            var entry = new JObject { ["index"] = i };
            if (reason == null && proposal != null)
            {
                Apply(proposal);
                accepted++;
                entry["status"] = "accepted";
                entry["id"] = proposal.Id;
            }
            else
            {
                entry["status"] = "rejected";
                entry["reason"] = reason ?? "rejected";
            }

            report.Add(entry);
        }

        Write();
        return new JObject
        {
            ["accepted"] = accepted,
            ["rejected"] = shapes.Count - accepted,
            ["totalProposals"] = _items.Count,
            ["items"] = report,
        }.ToString(Formatting.None);
    }

    private string? Validate(JToken item, Func<string, PendingShape?> findPending, out StoredProposal? proposal)
    {
        proposal = null;
        if (item is not JObject o) return "item must be an object";

        string? reviewError = ParseReview(o, out string? action, out int? target);
        if (reviewError != null) return reviewError;
        bool remove = action == ActionRemove;
        RunShape? targetShape = target.HasValue ? _existing[target.Value] : null;
        string? targetHash = targetShape == null ? null : ShapeFingerprint.Compute(targetShape.Label, targetShape.ShapeType, targetShape.Points);

        string? sam2Id = o["fromSam2"]?.Type == JTokenType.String ? o["fromSam2"]!.Value<string>() : null;
        PendingShape? pending = null;
        if (!remove && !string.IsNullOrEmpty(sam2Id))
        {
            pending = findPending(sam2Id);
            if (pending == null) return "fromSam2 '" + sam2Id + "' is unknown (call sam2_segment first)";
        }

        string rawLabel = o["label"]?.Type == JTokenType.String ? (o["label"]!.Value<string>() ?? string.Empty).Trim() : string.Empty;
        if (rawLabel.Length == 0 && pending?.Label != null) rawLabel = pending.Label.Trim();
        // A remove keeps the label of the shape it deletes; no class is created for it.
        if (remove && rawLabel.Length == 0) rawLabel = (targetShape!.Label ?? string.Empty).Trim();
        if (rawLabel.Length == 0) return "label is required";
        string? labelError = null;
        string? label = remove ? rawLabel : ResolveLabel(rawLabel, out labelError);
        if (label == null) return labelError ?? "label is invalid";

        (int w, int h) = _image.ImageSize;
        double? score = null;
        JToken? scoreToken = o["score"];
        if (scoreToken != null && scoreToken.Type != JTokenType.Null)
        {
            if (!Json.TryNumber(scoreToken, out double s)) return "score must be a finite number";
            score = s;
        }

        string note = o["note"]?.Type == JTokenType.String ? o["note"]!.Value<string>() ?? string.Empty : string.Empty;
        string id = o["id"]?.Type == JTokenType.String ? (o["id"]!.Value<string>() ?? string.Empty).Trim() : string.Empty;
        if (id.Length == 0) id = "auto-" + (++_autoId).ToString(CultureInfo.InvariantCulture);

        if (target.HasValue && _items.Any(p => p.TargetIndex == target && !string.Equals(p.Id, id, StringComparison.Ordinal)))
        {
            return "target " + target.Value.ToString(CultureInfo.InvariantCulture) + " already has an action in this run (at most one action per target)";
        }

        if (remove)
        {
            proposal = new StoredProposal
            {
                Id = id,
                Label = label,
                ShapeType = "polygon",
                Score = score,
                Note = note,
                Source = "agent",
                Action = action,
                TargetIndex = target,
                TargetHash = targetHash,
            };
            return null;
        }

        string requestedType = o["shapeType"]?.Type == JTokenType.String ? (o["shapeType"]!.Value<string>() ?? string.Empty).Trim().ToLowerInvariant() : string.Empty;
        if (pending != null && (requestedType == "oriented_rectangle" || requestedType == "rectangle"))
        {
            // A box of the SAM2 outline: the rotated box fitted to the outline (oriented) or its axis-aligned bounds (rectangle).
            List<double[]> fittedPoints;
            if (requestedType == "oriented_rectangle")
            {
                List<double[]>? fitted = FitOriented(pending.Outer, w, h);
                if (fitted == null) return "the SAM2 outline has no area inside the image";
                fittedPoints = fitted;
            }
            else
            {
                List<double[]> clippedOutline = Geometry.ClipToImage(pending.Outer, w, h);
                if (clippedOutline.Count < 3) return "the SAM2 outline has no area inside the image";
                double bx1 = clippedOutline.Min(p => p[0]), bx2 = clippedOutline.Max(p => p[0]);
                double by1 = clippedOutline.Min(p => p[1]), by2 = clippedOutline.Max(p => p[1]);
                if (bx2 - bx1 <= 0 || by2 - by1 <= 0) return "the SAM2 outline has no area inside the image";
                fittedPoints = Geometry.Clamp(new List<double[]> { new[] { bx1, by1 }, new[] { bx2, by2 } }, w, h);
            }

            string? boxDuplicate = action == null ? DuplicateReason(label, requestedType, fittedPoints) : null;
            if (boxDuplicate != null) return boxDuplicate;
            proposal = new StoredProposal
            {
                Id = id,
                Label = label,
                ShapeType = requestedType,
                Points = fittedPoints,
                Score = score ?? pending.Score,
                Note = note,
                Source = "sam2",
                Action = action,
                TargetIndex = target,
                TargetHash = targetHash,
            };
            return null;
        }

        if (pending != null)
        {
            List<double[]> samPoints = Geometry.Clamp(pending.Outer, w, h);
            string? samDuplicate = action == null ? DuplicateReason(label, "polygon", samPoints) : null;
            if (samDuplicate != null) return samDuplicate;
            proposal = new StoredProposal
            {
                Id = id,
                Label = label,
                ShapeType = "polygon",
                Points = samPoints,
                Holes = pending.Holes.Select(hole => Geometry.Clamp(hole, w, h)).ToList(),
                Score = score ?? pending.Score,
                Note = note,
                Source = "sam2",
                Action = action,
                TargetIndex = target,
                TargetHash = targetHash,
            };
            return null;
        }

        string shapeType = o["shapeType"]?.Type == JTokenType.String ? (o["shapeType"]!.Value<string>() ?? string.Empty).Trim().ToLowerInvariant() : string.Empty;
        if (shapeType != "polygon" && shapeType != "rectangle" && shapeType != "oriented_rectangle") return "shapeType must be 'polygon', 'rectangle' or 'oriented_rectangle'";
        if (o["points"] is not JArray rawPoints) return "points must be an array of [x,y]";

        var points = new List<double[]>();
        foreach (JToken p in rawPoints)
        {
            if (p is not JArray pair || pair.Count != 2 || !Json.TryNumber(pair[0], out double px) || !Json.TryNumber(pair[1], out double py))
            {
                return "every point must be [x,y] with finite numbers";
            }

            points.Add(new[] { px, py });
        }

        if (shapeType == "oriented_rectangle")
        {
            if (points.Count < 3) return "oriented_rectangle needs at least 3 points with area (the outline of the part, or its 4 corners in order)";
            // Clipped to the image first, then fitted to ALL points (convex hull, then the smallest rotated rectangle around them): an outline of 8-16 points gives the tilt.
            List<double[]>? fitted = FitOriented(points, w, h);
            if (fitted == null) return _allowOutside
                ? "oriented_rectangle has no area, or is mostly outside the image (at least " + (int)(MinVisibleFraction * 100) + "% of its area must be inside)"
                : "oriented_rectangle has no area inside the image";
            points = fitted;
        }
        else if (shapeType == "rectangle" && _allowOutside)
        {
            if (points.Count != 2 && points.Count != 4) return "rectangle needs 2 or 4 points";
            double x1 = points.Min(p => p[0]), x2 = points.Max(p => p[0]);
            double y1 = points.Min(p => p[1]), y2 = points.Max(p => p[1]);
            if (x2 - x1 <= 0 || y2 - y1 <= 0) return "rectangle has no area";
            var box = new List<double[]> { new[] { x1, y1 }, new[] { x2, y1 }, new[] { x2, y2 }, new[] { x1, y2 } };
            if (!AcceptableOutside(box, w, h)) return "rectangle has no area, or is mostly outside the image (at least " + (int)(MinVisibleFraction * 100) + "% of its area must be inside)";
            points = Geometry.Round(new List<double[]> { new[] { x1, y1 }, new[] { x2, y2 } });
        }
        else if (shapeType == "rectangle")
        {
            points = Geometry.Clamp(points, w, h);
            if (points.Count != 2 && points.Count != 4) return "rectangle needs 2 or 4 points";
            double x1 = points.Min(p => p[0]), x2 = points.Max(p => p[0]);
            double y1 = points.Min(p => p[1]), y2 = points.Max(p => p[1]);
            if (x2 - x1 <= 0 || y2 - y1 <= 0) return "rectangle has no area inside the image";
            points = new List<double[]> { new[] { x1, y1 }, new[] { x2, y2 } };
        }
        else if (_allowOutside)
        {
            if (points.Count < 3) return "polygon needs at least 3 points";
            if (!AcceptableOutside(points, w, h)) return "polygon has no area, or is mostly outside the image (at least " + (int)(MinVisibleFraction * 100) + "% of its area must be inside)";
            points = Geometry.Round(points);
        }
        else
        {
            points = Geometry.Clamp(points, w, h);
            if (points.Count < 3) return "polygon needs at least 3 points";
        }

        // Only a plain new shape (no review action) can duplicate something; a replace or remove reviews an existing shape on purpose.
        string? duplicate = action == null ? DuplicateReason(label, shapeType, points) : null;
        if (duplicate != null) return duplicate;

        proposal = new StoredProposal
        {
            Id = id,
            Label = label,
            ShapeType = shapeType,
            Points = points,
            Score = score,
            Note = note,
            Source = "agent",
            Action = action,
            TargetIndex = target,
            TargetHash = targetHash,
        };
        return null;
    }

    /// <summary>
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
    /// The smallest rotated rectangle around the outline after clipping the outline to the image (so a part cut by the border is not
    /// distorted by clamping corners one by one), as [TL, TR, BR, BL]. When the fitted box would leave the image by more than half a
    /// pixel it falls back to the axis-aligned bounds of the clipped outline, which is a true rectangle. Null when nothing with area remains.
    /// </summary>
    private static List<double[]>? FitOrientedInImage(IReadOnlyList<double[]> outline, int w, int h)
    {
        List<double[]> clipped = Geometry.ClipToImage(outline, w, h);
        if (clipped.Count < 3) return null;
        List<double[]>? fitted = ShapeConverter.FitOrientedRectangle(clipped);
        if (fitted == null || RectangleArea(fitted) < MinOrientedArea) return null;

        const double tolerance = 0.5;
        if (fitted.All(p => p[0] >= -tolerance && p[0] <= w + tolerance && p[1] >= -tolerance && p[1] <= h + tolerance))
        {
            return Geometry.Clamp(fitted, w, h);
        }

        double x1 = clipped.Min(p => p[0]), x2 = clipped.Max(p => p[0]);
        double y1 = clipped.Min(p => p[1]), y2 = clipped.Max(p => p[1]);
        var bounds = new List<double[]> { new[] { x1, y1 }, new[] { x2, y1 }, new[] { x2, y2 }, new[] { x1, y2 } };
        return RectangleArea(bounds) < MinOrientedArea ? null : Geometry.Clamp(bounds, w, h);
    }

    /// <summary>
    /// The rejection reason when the new shape duplicates an existing shape or a pending suggestion (rule: <see cref="ShapeOverlap.IsDuplicate"/>),
    /// otherwise null. An existing shape that this run replaces or removes is not compared (the new shape may take its place).
    /// </summary>
    private string? DuplicateReason(string label, string shapeType, List<double[]> points)
    {
        List<double[]> ring = ShapeOverlap.ToRing(shapeType, points);
        if (ShapeOverlap.Area(ring) <= 0) return null;

        _existingRings ??= BuildRings(_existing);
        for (int i = 0; i < _existing.Count; i++)
        {
            List<double[]>? other = _existingRings[i];
            if (other == null) continue;
            int index = i;
            if (_items.Any(p => p.TargetIndex == index)) continue;
            if (ShapeOverlap.IsDuplicate(label, ring, _existing[i].Label, other, out double iou))
            {
                return "duplicate of existing shape e" + i.ToString(CultureInfo.InvariantCulture) + " (label " + _existing[i].Label + ", IoU "
                    + iou.ToString("0.00", CultureInfo.InvariantCulture) + "); the part is already labelled, do not propose it again";
            }
        }

        _pendingRings ??= BuildRings(_pending);
        for (int i = 0; i < _pending.Count; i++)
        {
            List<double[]>? other = _pendingRings[i];
            if (other == null) continue;
            if (ShapeOverlap.IsDuplicate(label, ring, _pending[i].Label, other, out double iou))
            {
                return "duplicate of pending suggestion s" + i.ToString(CultureInfo.InvariantCulture) + " (label " + _pending[i].Label + ", IoU "
                    + iou.ToString("0.00", CultureInfo.InvariantCulture) + "); the user has not reviewed it yet, do not propose it again";
            }
        }

        return null;
    }

    private static List<double[]>?[] BuildRings(List<RunShape> shapes)
    {
        var rings = new List<double[]>?[shapes.Count];
        for (int i = 0; i < shapes.Count; i++)
        {
            List<double[]> ring = ShapeOverlap.ToRing(shapes[i].ShapeType, shapes[i].Points);
            rings[i] = ShapeOverlap.Area(ring) > 0 ? ring : null;
        }

        return rings;
    }

    public const string ActionAdd = "add";

    public const string ActionReplace = "replace";

    public const string ActionRemove = "remove";

    /// <summary>
    /// Reads the optional review fields. <paramref name="action"/> is null for a plain new shape (no action or 'add'); otherwise
    /// 'replace' or 'remove' with a valid <paramref name="target"/>. Returns the rejection reason of an invalid combination.
    /// </summary>
    private string? ParseReview(JObject o, out string? action, out int? target)
    {
        action = null;
        target = null;
        JToken? actionToken = o["action"];
        JToken? targetToken = o["target"];
        bool hasAction = actionToken != null && actionToken.Type != JTokenType.Null;
        bool hasTarget = targetToken != null && targetToken.Type != JTokenType.Null;

        string requested = ActionAdd;
        if (hasAction)
        {
            if (actionToken!.Type != JTokenType.String) return "action must be 'add', 'replace' or 'remove'";
            requested = (actionToken.Value<string>() ?? string.Empty).Trim().ToLowerInvariant();
            if (requested != ActionAdd && requested != ActionReplace && requested != ActionRemove) return "action must be 'add', 'replace' or 'remove'";
        }

        if (requested == ActionAdd)
        {
            return hasTarget ? "target needs action 'replace' or 'remove' (action 'add' takes no target)" : null;
        }

        if (!hasTarget) return "action '" + requested + "' needs a target (the id of a shape from list_shapes)";
        if (!Json.TryNumber(targetToken, out double number) || number != Math.Floor(number) || number < 0 || number > int.MaxValue)
        {
            return "target must be a non-negative integer (a shape id from list_shapes)";
        }

        int index = (int)number;
        if (index >= _existing.Count)
        {
            return "target " + index.ToString(CultureInfo.InvariantCulture) + " does not exist (the image has " + _existing.Count.ToString(CultureInfo.InvariantCulture) + " shapes)";
        }

        string note = o["note"]?.Type == JTokenType.String ? (o["note"]!.Value<string>() ?? string.Empty).Trim() : string.Empty;
        if (note.Length == 0) return "note is required when target is given (the reason for the " + requested + ")";

        action = requested;
        target = index;
        return null;
    }

    private void Apply(StoredProposal proposal)
    {
        int index = _items.FindIndex(p => string.Equals(p.Id, proposal.Id, StringComparison.Ordinal));
        if (index >= 0) _items[index] = proposal;
        else _items.Add(proposal);
    }

    private void Write()
    {
        var list = new JArray();
        foreach (StoredProposal p in _items)
        {
            var entry = new JObject
            {
                ["id"] = p.Id,
                ["label"] = p.Label,
                ["shapeType"] = p.ShapeType,
                ["points"] = Json.Points(p.Points),
                ["holes"] = new JArray(p.Holes.Select(Json.Points)),
                ["score"] = p.Score.HasValue ? new JValue(Math.Round(p.Score.Value, 4)) : JValue.CreateNull(),
                ["note"] = p.Note,
                ["source"] = p.Source,
            };
            if (p.TargetIndex.HasValue && p.Action != null)
            {
                entry["action"] = p.Action;
                entry["target"] = p.TargetIndex.Value;
                entry["targetHash"] = p.TargetHash ?? string.Empty;
            }

            list.Add(entry);
        }

        var root = new JObject { ["version"] = 1, ["proposals"] = list };
        AtomicFile.WriteAllText(_path, root.ToString(Formatting.Indented));
    }
}

internal static class Geometry
{
    /// <summary>The polygon clipped to [0,width] x [0,height] (Sutherland-Hodgman against the four borders); fewer than 3 points when nothing is left.</summary>
    public static List<double[]> ClipToImage(IEnumerable<double[]> polygon, int width, int height)
    {
        List<double[]> ring = polygon.Select(p => new[] { p[0], p[1] }).ToList();
        ring = ClipHalf(ring, 0, 0, true);
        ring = ClipHalf(ring, 0, width, false);
        ring = ClipHalf(ring, 1, 0, true);
        ring = ClipHalf(ring, 1, height, false);
        return ring;
    }

    private static List<double[]> ClipHalf(List<double[]> input, int axis, double bound, bool keepGreater)
    {
        var output = new List<double[]>();
        for (int j = 0; j < input.Count; j++)
        {
            double[] cur = input[j];
            double[] prev = input[(j + input.Count - 1) % input.Count];
            bool curIn = keepGreater ? cur[axis] >= bound : cur[axis] <= bound;
            bool prevIn = keepGreater ? prev[axis] >= bound : prev[axis] <= bound;
            if (curIn != prevIn)
            {
                double t = (bound - prev[axis]) / (cur[axis] - prev[axis]);
                output.Add(new[] { prev[0] + t * (cur[0] - prev[0]), prev[1] + t * (cur[1] - prev[1]) });
            }

            if (curIn) output.Add(cur);
        }

        return output;
    }

    /// <summary>Rounds to 2 decimals without clamping (a shape that may leave the image).</summary>
    public static List<double[]> Round(IEnumerable<double[]> points)
    {
        return points.Select(p => new[] { Math.Round(p[0], 2), Math.Round(p[1], 2) }).ToList();
    }

    /// <summary>Clamps to [0,width] x [0,height] and rounds to 2 decimals.</summary>
    public static List<double[]> Clamp(IEnumerable<double[]> points, int width, int height)
    {
        return points
            .Select(p => new[] { Math.Round(Math.Min(width, Math.Max(0, p[0])), 2), Math.Round(Math.Min(height, Math.Max(0, p[1])), 2) })
            .ToList();
    }
}

internal static class Json
{
    public static bool TryNumber(JToken? token, out double value)
    {
        value = 0;
        if (token == null || (token.Type != JTokenType.Integer && token.Type != JTokenType.Float)) return false;
        value = token.Value<double>();
        return double.IsFinite(value);
    }

    public static JArray Points(IEnumerable<double[]> points, int decimals = 2)
    {
        return new JArray(points.Select(p => (object)new JArray(Math.Round(p[0], decimals), Math.Round(p[1], decimals))));
    }
}
