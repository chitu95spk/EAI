using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using OpenCvSharp;

namespace EasyLabel.Ai.Mcp;

/// <summary>
/// The read-only helper tools grid_crop, render_overlay and measure. They read only the run image, the request and the proposals of this
/// run; the only thing they write is a copy of each returned picture under <c>&lt;run&gt;/tools/</c>.
/// </summary>
internal sealed class AnalysisTools
{
    public const int OutputMaxSide = 1190;
    public const int DefaultOutputSide = 1024;
    public const double MaxZoom = 8.0;
    public const int MaxGridLines = 50;
    public const int MaxMeasureShapes = 100;
    public const int MaxReportedPairs = 200;
    public const string ToolsFolder = "tools";
    private const int MaxSavedFiles = 100;

    private static readonly Regex ExistingRef = new Regex("^[eE](\\d+)$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly string _runDirectory;
    private readonly RunRequest _request;
    private readonly ImageSource _image;
    private readonly ProposalStore _proposals;
    private int _saved;

    public AnalysisTools(string runDirectory, RunRequest request, ImageSource image, ProposalStore proposals)
    {
        _runDirectory = runDirectory;
        _request = request;
        _image = image;
        _proposals = proposals;
    }

    public static bool Handles(string name) => name == "grid_crop" || name == "render_overlay" || name == "measure";

    public JObject Call(string name, JObject args)
    {
        switch (name)
        {
            case "grid_crop": return GridCrop(args);
            case "render_overlay": return RenderOverlay(args);
            case "measure": return Measure(args);
            default: throw new ArgumentException("Unknown tool: " + name);
        }
    }

    /// <summary>Output scale (output pixels per image pixel): the requested zoom or a default that fills about 1024 px, never above 8x and never above 1600 px on the longer side.</summary>
    public static double ChooseScale(int cropWidth, int cropHeight, double? requested)
    {
        double longer = Math.Max(1, Math.Max(cropWidth, cropHeight));
        double s = requested ?? Math.Min(4.0, DefaultOutputSide / longer);
        s = Math.Min(s, MaxZoom);
        s = Math.Min(s, OutputMaxSide / longer);
        return Math.Min(s, ImageSource.PixelCapScale(Math.Max(1, cropWidth), Math.Max(1, cropHeight)));
    }

    /// <summary>The sentence that tells the model how to map a point of the returned picture back to original image pixels (the REAL output size and scale).</summary>
    private static string BackMapping(Rect r, int ow, int oh)
    {
        string sx = Num(ow / (double)r.Width);
        string sy = Num(oh / (double)r.Height);
        return "Returned picture " + ow + "x" + oh + " px (never resized afterwards, at most " + OutputMaxSide + " px on the long side). Back to ORIGINAL image pixels: image_x = "
            + r.X + " + out_x / " + sx + ", image_y = " + r.Y + " + out_y / " + sy + ".";
    }

    /// <summary>Grid spacing in image pixels: the requested one (never so small that more than 50 lines per axis are drawn) or a 1/2/5 step giving about 8 lines.</summary>
    public static double ChooseGridStep(int cropWidth, int cropHeight, double? requested)
    {
        double longer = Math.Max(1, Math.Max(cropWidth, cropHeight));
        double floor = longer / MaxGridLines;
        if (requested.HasValue) return Math.Max(requested.Value, floor);
        double raw = longer / 8.0;
        double pow = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        double n = raw / pow;
        double step = (n <= 1.5 ? 1 : n <= 3.5 ? 2 : n <= 7.5 ? 5 : 10) * pow;
        return Math.Max(step, Math.Max(1.0, floor));
    }

    private JObject GridCrop(JObject args)
    {
        double? grid = OptionalNumber(args, "grid");
        double? zoom = OptionalNumber(args, "scale");
        if (grid.HasValue && grid.Value <= 0) throw new ArgumentException("grid must be greater than 0 (image pixels between grid lines).");
        if (zoom.HasValue && zoom.Value <= 0) throw new ArgumentException("scale must be greater than 0 (output pixels per image pixel).");
        Rect r = _image.ClampRegion(OptionalNumber(args, "x"), OptionalNumber(args, "y"), OptionalNumber(args, "w"), OptionalNumber(args, "h"));

        double scale = ChooseScale(r.Width, r.Height, zoom);
        int ow = Math.Max(1, (int)Math.Round(r.Width * scale));
        int oh = Math.Max(1, (int)Math.Round(r.Height * scale));
        double step = ChooseGridStep(r.Width, r.Height, grid);

        using Mat canvas = _image.ExtractRegion(r, ow, oh);
        DrawGrid(canvas, r, ow / (double)r.Width, oh / (double)r.Height, step);
        string text = "grid_crop " + r.X + "," + r.Y + "," + r.Width + "," + r.Height + " (x,y,w,h) scale=" + Num(ow / (double)r.Width)
            + ". Grid every " + Num(step) + " image px; the labels are ORIGINAL image pixel coordinates (x on top, y on the left): read positions from them. "
            + BackMapping(r, ow, oh);
        return ImageResult(canvas, "grid_crop", text);
    }

    private JObject RenderOverlay(JObject args)
    {
        string include = "both";
        JToken? includeToken = args["include"];
        if (includeToken != null && includeToken.Type != JTokenType.Null)
        {
            if (includeToken.Type != JTokenType.String) throw new ArgumentException("include must be 'existing', 'proposals' or 'both'.");
            include = (includeToken.Value<string>() ?? string.Empty).Trim().ToLowerInvariant();
            if (include != "existing" && include != "proposals" && include != "both") throw new ArgumentException("include must be 'existing', 'proposals' or 'both'.");
        }

        Rect r = _image.ClampRegion(OptionalNumber(args, "x"), OptionalNumber(args, "y"), OptionalNumber(args, "w"), OptionalNumber(args, "h"));
        double scale = ChooseScale(r.Width, r.Height, null);
        int ow = Math.Max(1, (int)Math.Round(r.Width * scale));
        int oh = Math.Max(1, (int)Math.Round(r.Height * scale));
        double sx = ow / (double)r.Width;
        double sy = oh / (double)r.Height;

        IReadOnlyList<StoredProposal> proposals = _proposals.Snapshot();
        var removed = new HashSet<int>(proposals.Where(p => p.Action == ProposalStore.ActionRemove && p.TargetIndex.HasValue).Select(p => p.TargetIndex!.Value));
        var shown = new List<string>();
        var hidden = new List<string>();

        using Mat canvas = _image.ExtractRegion(r, ow, oh);
        if (include != "proposals")
        {
            for (int i = 0; i < _request.ExistingShapes.Count; i++)
            {
                RunShape s = _request.ExistingShapes[i];
                var color = removed.Contains(i) ? new Scalar(0, 0, 255) : new Scalar(0, 200, 0);
                string name = "e" + i.ToString(CultureInfo.InvariantCulture);
                DrawShape(canvas, r, sx, sy, ShapeMeasure.ToRing(s.ShapeType, s.Points ?? new List<double[]>()), name + " " + Short(s.Label), color, name, shown, hidden, removed.Contains(i) ? 3 : 2);
            }
        }

        if (include != "existing")
        {
            foreach (StoredProposal p in proposals)
            {
                if (p.Action == ProposalStore.ActionRemove) continue;
                string name = "p:" + p.Id;
                DrawShape(canvas, r, sx, sy, ShapeMeasure.ToRing(p.ShapeType, p.Points), name + " " + Short(p.Label), new Scalar(0, 140, 255), name, shown, hidden, 2);
            }
        }

        var sb = new StringBuilder();
        sb.Append("render_overlay ").Append(r.X).Append(',').Append(r.Y).Append(',').Append(r.Width).Append(',').Append(r.Height)
            .Append(" (x,y,w,h) scale=").Append(Num(sx)).Append(". ").Append(BackMapping(r, ow, oh)).Append(" include=").Append(include)
            .Append(". Green = existing shape labelled e<id> (id from list_shapes); orange = proposed shape labelled p:<id>; red = existing shape that a remove proposal targets.");
        sb.Append(" Shown: ").Append(shown.Count == 0 ? "none" : string.Join(", ", shown.Take(80))).Append('.');
        if (hidden.Count > 0) sb.Append(" Outside this crop: ").Append(string.Join(", ", hidden.Take(80))).Append('.');
        return ImageResult(canvas, "render_overlay", sb.ToString());
    }

    private JObject Measure(JObject args)
    {
        var shapes = new List<MeasuredShape>();
        JToken? ids = args["ids"];
        JToken? adhoc = args["shapes"];
        bool hasIds = ids != null && ids.Type != JTokenType.Null;
        bool hasShapes = adhoc != null && adhoc.Type != JTokenType.Null;
        IReadOnlyList<StoredProposal> proposals = _proposals.Snapshot();

        if (hasIds)
        {
            if (ids is not JArray idArray) throw new ArgumentException("ids must be an array (numbers = existing shape ids, strings = proposal ids).");
            foreach (JToken token in idArray)
            {
                MeasuredShape shape = ResolveRef(token, proposals);
                if (!shapes.Any(s => s.Ref == shape.Ref)) shapes.Add(shape);
            }
        }

        if (hasShapes)
        {
            if (adhoc is not JArray list) throw new ArgumentException("shapes must be an array of {points, shapeType?, label?}.");
            for (int i = 0; i < list.Count; i++) shapes.Add(ParseAdHoc(list[i], i));
        }

        if (!hasIds && !hasShapes)
        {
            for (int i = 0; i < _request.ExistingShapes.Count; i++) shapes.Add(ExistingAt(i));
            foreach (StoredProposal p in proposals)
            {
                if (p.Action != ProposalStore.ActionRemove) shapes.Add(ProposalShape(p));
            }
        }

        if (shapes.Count > MaxMeasureShapes) throw new ArgumentException("Too many shapes (" + shapes.Count + "; at most " + MaxMeasureShapes + "): pass ids.");

        var stats = new JArray();
        foreach (MeasuredShape s in shapes)
        {
            ShapeStats st = ShapeMeasure.Stats(s);
            stats.Add(new JObject
            {
                ["ref"] = st.Ref,
                ["label"] = st.Label,
                ["shapeType"] = st.ShapeType,
                ["bbox"] = new JArray(Math.Round(st.X, 2), Math.Round(st.Y, 2), Math.Round(st.Width, 2), Math.Round(st.Height, 2)),
                ["width"] = Math.Round(st.Width, 2),
                ["height"] = Math.Round(st.Height, 2),
                ["area"] = Math.Round(st.Area, 2),
            });
        }

        var pairs = new List<PairStats>();
        for (int i = 0; i < shapes.Count; i++)
        {
            for (int j = i + 1; j < shapes.Count; j++)
            {
                PairStats pair = ShapeMeasure.Compare(shapes[i], shapes[j]);
                if (pair.Intersection > 0) pairs.Add(pair);
            }
        }

        pairs = pairs.OrderByDescending(p => p.Iou).ToList();
        var pairArray = new JArray();
        foreach (PairStats p in pairs.Take(MaxReportedPairs))
        {
            pairArray.Add(new JObject
            {
                ["a"] = p.A,
                ["b"] = p.B,
                ["intersection"] = Math.Round(p.Intersection, 2),
                ["iou"] = Math.Round(p.Iou, 3),
                ["coverA"] = Math.Round(p.CoverA, 3),
                ["coverB"] = Math.Round(p.CoverB, 3),
                ["relation"] = p.Relation,
                ["duplicate"] = p.Duplicate,
            });
        }

        var result = new JObject
        {
            ["count"] = shapes.Count,
            ["shapes"] = stats,
            ["overlappingPairs"] = pairArray,
            ["nearDuplicates"] = new JArray(pairs.Where(p => p.Duplicate).Select(p => (object)(p.A + " ~ " + p.B))),
            ["duplicateIouThreshold"] = ShapeMeasure.DuplicateIou,
            ["note"] = "Only pairs that overlap are listed. coverA = share of a inside b, coverB = share of b inside a. Holes are ignored.",
        };
        if (pairs.Count > MaxReportedPairs) result["pairsOmitted"] = pairs.Count - MaxReportedPairs;
        return ToolResults.Text(result.ToString(Formatting.None));
    }

    private MeasuredShape ResolveRef(JToken token, IReadOnlyList<StoredProposal> proposals)
    {
        if (token.Type == JTokenType.Integer || token.Type == JTokenType.Float)
        {
            if (!Json.TryNumber(token, out double number) || number != Math.Floor(number) || number < 0 || number >= _request.ExistingShapes.Count)
            {
                throw new ArgumentException("No existing shape with id " + token + " (the image has " + _request.ExistingShapes.Count + " shapes; see list_shapes).");
            }

            return ExistingAt((int)number);
        }

        if (token.Type != JTokenType.String) throw new ArgumentException("ids entries must be numbers (existing shapes) or strings (proposal ids).");
        string text = (token.Value<string>() ?? string.Empty).Trim();
        Match m = ExistingRef.Match(text);
        if (m.Success)
        {
            if (!int.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int index) || index >= _request.ExistingShapes.Count)
            {
                throw new ArgumentException("No existing shape " + text + " (the image has " + _request.ExistingShapes.Count + " shapes).");
            }

            return ExistingAt(index);
        }

        string id = text.StartsWith("p:", StringComparison.Ordinal) ? text.Substring(2) : text;
        StoredProposal? proposal = proposals.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.Ordinal));
        if (proposal == null) throw new ArgumentException("No proposal with id '" + id + "' (use a number for an existing shape).");
        if (proposal.Action == ProposalStore.ActionRemove) throw new ArgumentException("Proposal '" + id + "' is a removal and has no outline.");
        return ProposalShape(proposal);
    }

    private MeasuredShape ExistingAt(int index)
    {
        RunShape s = _request.ExistingShapes[index];
        return new MeasuredShape("e" + index.ToString(CultureInfo.InvariantCulture), s.Label ?? string.Empty, s.ShapeType ?? "polygon", ShapeMeasure.ToRing(s.ShapeType ?? "polygon", s.Points ?? new List<double[]>()));
    }

    private static MeasuredShape ProposalShape(StoredProposal p)
    {
        return new MeasuredShape("p:" + p.Id, p.Label, p.ShapeType, ShapeMeasure.ToRing(p.ShapeType, p.Points));
    }

    private static MeasuredShape ParseAdHoc(JToken item, int index)
    {
        string name = "s" + index.ToString(CultureInfo.InvariantCulture);
        if (item is not JObject o) throw new ArgumentException("shapes[" + index + "] must be an object.");
        if (o["points"] is not JArray raw) throw new ArgumentException("shapes[" + index + "].points must be an array of [x,y].");
        var points = new List<double[]>();
        foreach (JToken p in raw)
        {
            if (p is not JArray pair || pair.Count != 2 || !Json.TryNumber(pair[0], out double px) || !Json.TryNumber(pair[1], out double py))
            {
                throw new ArgumentException("shapes[" + index + "]: every point must be [x,y] with finite numbers.");
            }

            points.Add(new[] { px, py });
        }

        string type = o["shapeType"]?.Type == JTokenType.String ? (o["shapeType"]!.Value<string>() ?? string.Empty).Trim().ToLowerInvariant() : "polygon";
        if (type != "polygon" && type != "rectangle" && type != "oriented_rectangle") throw new ArgumentException("shapes[" + index + "].shapeType must be 'polygon', 'rectangle' or 'oriented_rectangle'.");
        int minimum = type == "rectangle" ? 2 : 3;
        if (points.Count < minimum) throw new ArgumentException("shapes[" + index + "] needs at least " + minimum + " points.");
        string label = o["label"]?.Type == JTokenType.String ? (o["label"]!.Value<string>() ?? string.Empty) : string.Empty;
        return new MeasuredShape(name, label, type, ShapeMeasure.ToRing(type, points));
    }

    private static void DrawShape(Mat canvas, Rect region, double sx, double sy, List<double[]> ring, string caption, Scalar color, string name, List<string> shown, List<string> hidden, int thickness)
    {
        if (ring.Count < 2)
        {
            hidden.Add(name);
            return;
        }

        double minX = ring.Min(p => p[0]), maxX = ring.Max(p => p[0]);
        double minY = ring.Min(p => p[1]), maxY = ring.Max(p => p[1]);
        if (maxX < region.X || minX > region.X + region.Width || maxY < region.Y || minY > region.Y + region.Height)
        {
            hidden.Add(name);
            return;
        }

        var pts = new List<Point>(ring.Count);
        foreach (double[] p in ring) pts.Add(new Point(ToPixel((p[0] - region.X) * sx), ToPixel((p[1] - region.Y) * sy)));
        Cv2.Polylines(canvas, new[] { pts }, true, color, thickness, LineTypes.AntiAlias);

        int tx = Math.Max(2, Math.Min(canvas.Width - 60, ToPixel((minX - region.X) * sx)));
        int ty = Math.Max(14, Math.Min(canvas.Height - 4, ToPixel((minY - region.Y) * sy) - 3));
        PutLabel(canvas, caption, tx, ty, color);
        shown.Add(name);
    }

    private static int ToPixel(double value)
    {
        return (int)Math.Round(Math.Max(-100000.0, Math.Min(100000.0, value)));
    }

    private static void DrawGrid(Mat canvas, Rect r, double sx, double sy, double step)
    {
        var xs = new List<double>();
        var ys = new List<double>();
        for (double gx = Math.Ceiling(r.X / step) * step; gx < r.X + r.Width && xs.Count <= MaxGridLines; gx += step) xs.Add(gx);
        for (double gy = Math.Ceiling(r.Y / step) * step; gy < r.Y + r.Height && ys.Count <= MaxGridLines; gy += step) ys.Add(gy);

        var lineColor = new Scalar(0, 255, 255);
        using (Mat layer = canvas.Clone())
        {
            foreach (double gx in xs)
            {
                int px = ToPixel((gx - r.X) * sx);
                Cv2.Line(layer, new Point(px, 0), new Point(px, canvas.Height - 1), lineColor, 1);
            }

            foreach (double gy in ys)
            {
                int py = ToPixel((gy - r.Y) * sy);
                Cv2.Line(layer, new Point(0, py), new Point(canvas.Width - 1, py), lineColor, 1);
            }

            Cv2.AddWeighted(layer, 0.5, canvas, 0.5, 0, canvas);
        }

        foreach (double gx in xs) PutLabel(canvas, Num(gx), ToPixel((gx - r.X) * sx) + 2, 12, lineColor);
        foreach (double gy in ys) PutLabel(canvas, Num(gy), 2, Math.Max(12, ToPixel((gy - r.Y) * sy) - 2), lineColor);
    }

    private static void PutLabel(Mat canvas, string text, int x, int y, Scalar color)
    {
        Cv2.PutText(canvas, text, new Point(x, y), HersheyFonts.HersheySimplex, 0.4, new Scalar(0, 0, 0), 3, LineTypes.AntiAlias);
        Cv2.PutText(canvas, text, new Point(x, y), HersheyFonts.HersheySimplex, 0.4, color, 1, LineTypes.AntiAlias);
    }

    private JObject ImageResult(Mat canvas, string tool, string text)
    {
        if (!Cv2.ImEncode(".jpg", canvas, out byte[] jpeg, new[] { (int)ImwriteFlags.JpegQuality, ImageSource.JpegQuality }))
        {
            throw new IOException("Could not encode the picture as JPEG.");
        }

        Save(tool, jpeg);
        return ToolResults.Content(
            new JObject { ["type"] = "image", ["data"] = Convert.ToBase64String(jpeg), ["mimeType"] = "image/jpeg" },
            new JObject { ["type"] = "text", ["text"] = text });
    }

    // Best effort copy for later inspection; a failure never fails the tool.
    private void Save(string tool, byte[] jpeg)
    {
        if (_saved >= MaxSavedFiles) return;
        try
        {
            string folder = Path.Combine(_runDirectory, ToolsFolder);
            Directory.CreateDirectory(folder);
            _saved++;
            File.WriteAllBytes(Path.Combine(folder, tool + "-" + _saved.ToString("000", CultureInfo.InvariantCulture) + ".jpg"), jpeg);
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
        }
    }

    private static string Short(string? label)
    {
        string text = label ?? string.Empty;
        return text.Length <= 20 ? text : text.Substring(0, 20);
    }

    private static string Num(double value) => value.ToString("0.####", CultureInfo.InvariantCulture);

    private static double? OptionalNumber(JObject args, string name)
    {
        JToken? token = args[name];
        if (token == null || token.Type == JTokenType.Null) return null;
        if (!Json.TryNumber(token, out double value)) throw new ArgumentException(name + " must be a finite number.");
        return value;
    }

    public static JArray Describe()
    {
        return JArray.Parse("""
        [
          {
            "name": "grid_crop",
            "description": "Like get_crop but draws a labelled coordinate grid: the tick labels are ORIGINAL image pixel coordinates (x on top, y on the left), so you can read positions straight off the picture. Region x, y, w, h in image pixels (clamped; default the whole image). grid = spacing between grid lines in image pixels (default automatic, at most 50 lines per axis). scale = output pixels per image pixel (default fits about 1024 px, at most 8; the output never exceeds 1.15 megapixels / about 1190 px on the longer side, larger requests are reduced). The text part gives the real output size and scale: convert picture positions with it.",
            "inputSchema": {
              "type": "object",
              "properties": {
                "x": { "type": "number", "description": "Left edge in image pixels (default 0)." },
                "y": { "type": "number", "description": "Top edge in image pixels (default 0)." },
                "w": { "type": "number", "description": "Width in image pixels (default: to the right edge)." },
                "h": { "type": "number", "description": "Height in image pixels (default: to the bottom edge)." },
                "grid": { "type": "number", "description": "Grid spacing in image pixels, greater than 0." },
                "scale": { "type": "number", "description": "Output pixels per image pixel, greater than 0." }
              },
              "required": []
            }
          },
          {
            "name": "render_overlay",
            "description": "Picture of the image or a region with shapes drawn on it, each labelled with its id: existing shapes green 'e<id>' (id from list_shapes), shapes proposed so far in this run orange 'p:<id>', existing shapes marked for removal red. Use it to check a proposal against the image. include = existing | proposals | both (default both). Region x, y, w, h in image pixels (default the whole image).",
            "inputSchema": {
              "type": "object",
              "properties": {
                "x": { "type": "number", "description": "Left edge in image pixels (default 0)." },
                "y": { "type": "number", "description": "Top edge in image pixels (default 0)." },
                "w": { "type": "number", "description": "Width in image pixels (default: to the right edge)." },
                "h": { "type": "number", "description": "Height in image pixels (default: to the bottom edge)." },
                "include": { "type": "string", "enum": ["existing", "proposals", "both"], "description": "Which shapes to draw (default both)." }
              },
              "required": []
            }
          },
          {
            "name": "measure",
            "description": "Geometry of shapes, no image needed: bbox width/height and area of each, and for every overlapping pair the intersection, IoU, the share of each inside the other, a relation (same, a_inside_b, b_inside_a, overlap) and a duplicate flag (IoU above 0.85). ids = existing shape ids (numbers, from list_shapes) and proposal ids (strings); shapes = extra hypothetical shapes {points, shapeType?, label?}. With neither given it measures every existing shape and every proposal so far.",
            "inputSchema": {
              "type": "object",
              "properties": {
                "ids": { "type": "array", "items": { "type": ["integer", "string"] }, "description": "Numbers = existing shape ids; strings = proposal ids (a string 'e3' also means existing shape 3)." },
                "shapes": {
                  "type": "array",
                  "items": {
                    "type": "object",
                    "properties": {
                      "points": { "type": "array", "items": { "type": "array", "items": { "type": "number" }, "minItems": 2, "maxItems": 2 } },
                      "shapeType": { "type": "string", "enum": ["polygon", "rectangle", "oriented_rectangle"] },
                      "label": { "type": "string" }
                    },
                    "required": ["points"]
                  }
                }
              },
              "required": []
            }
          }
        ]
        """);
    }
}
