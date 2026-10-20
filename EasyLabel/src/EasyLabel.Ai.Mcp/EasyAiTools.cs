using System.Globalization;
using EasyLabel.Core.Editing;
using EasyLabel.Core.Io;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace EasyLabel.Ai.Mcp;

/// <summary>The EasyAI tools (including the read-only grid_crop, render_overlay and measure) over one run folder. One instance per process; the protocol loop calls it sequentially.</summary>
public sealed class EasyAiTools : IToolSet, IDisposable
{
    private readonly RunRequest _request;
    private readonly ImageSource _image;
    private readonly ProposalStore _proposals;
    private readonly Sam2Runner _sam2;
    private readonly CandidateStore _candidates;
    private readonly AnalysisTools _analysis;
    private readonly JArray _descriptions;
    private readonly HashSet<string> _names = new HashSet<string>(StringComparer.Ordinal);

    public EasyAiTools(string runDirectory, RunRequest request, string? modelsFolder)
    {
        ArgumentNullException.ThrowIfNull(runDirectory);
        ArgumentNullException.ThrowIfNull(request);
        _request = request;
        _image = new ImageSource(request);
        _proposals = new ProposalStore(runDirectory, request, _image);
        string? folder = string.IsNullOrWhiteSpace(modelsFolder) ? request.ModelsFolder : modelsFolder;
        _sam2 = new Sam2Runner(folder, _image);
        _candidates = new CandidateStore(runDirectory);
        _analysis = new AnalysisTools(runDirectory, request, _image, _proposals);
        _descriptions = BuildDescriptions();
        foreach (JToken extra in AnalysisTools.Describe()) _descriptions.Add(extra);
        foreach (JToken tool in _descriptions) _names.Add(tool["name"]!.Value<string>()!);
    }

    /// <summary>Starts loading SAM2 and embedding the image in the background (production server only).</summary>
    public void WarmUpSam2() => _sam2.StartWarmUp();

    public JArray Describe() => (JArray)_descriptions.DeepClone();

    public bool Has(string name) => _names.Contains(name);

    public JObject Call(string name, JObject arguments, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        switch (name)
        {
            case "get_image_info": return ToolResults.Text(ImageInfo());
            case "get_crop": return GetCrop(arguments);
            case "get_reference": return GetReference(arguments);
            case "list_shapes": return ToolResults.Text(ListShapes());
            case "sam2_segment": return ToolResults.Text(_sam2.Segment(arguments, ct));
            case "propose_shapes": return ToolResults.Text(ProposeShapes(arguments));
            case "pick_candidates": return ToolResults.Text(PickCandidates(arguments));
            case "detect_points": return ToolResults.Text(DetectPoints(arguments, ct));
            case "memory_read": return ToolResults.Text(MemoryRead());
            case "memory_add": return ToolResults.Text(MemoryAdd(arguments));
            case "grid_crop":
            case "render_overlay":
            case "measure":
                return _analysis.Call(name, arguments);
            default: throw new ArgumentException("Unknown tool: " + name);
        }
    }

    private string MemoryRead()
    {
        if (string.IsNullOrWhiteSpace(_request.MemoryPath))
        {
            return "Memory is not available in this run.";
        }

        string text = EasyLabel.Core.Io.AgentMemoryFile.Read(_request.MemoryPath);
        return text.Trim().Length == 0 ? "(memory is empty)" : text;
    }

    private string MemoryAdd(JObject args)
    {
        if (string.IsNullOrWhiteSpace(_request.MemoryPath))
        {
            return "Memory is not available in this run; nothing was saved.";
        }

        string note = args["note"]?.Type == JTokenType.String ? args["note"]!.Value<string>()! : string.Empty;
        try
        {
            EasyLabel.Core.Io.AgentMemoryFile.Add(_request.MemoryPath, note, DateTime.Now);
            return "Saved.";
        }
        catch (Exception ex) when (ex is ArgumentException || ex is IOException || ex is UnauthorizedAccessException)
        {
            return "Not saved: " + ex.Message;
        }
    }

    private string ImageInfo()
    {
        (int w, int h) = _image.ImageSize;
        var info = new JObject
        {
            ["width"] = w,
            ["height"] = h,
            ["classNames"] = new JArray(_request.ClassNames),
            ["existingShapeCount"] = _request.ExistingShapes.Count,
            ["existingShapes"] = CompactShapes(_request.ExistingShapes, "e"),
            ["prompt"] = _request.Prompt,
        };
        if (_request.References.Count > 0)
        {
            // The attached hint files: read them with get_reference (index = the number below).
            JArray references = new JArray();
            for (int i = 0; i < _request.References.Count; i++)
            {
                references.Add(new JObject { ["index"] = i, ["name"] = _request.References[i].Name, ["kind"] = IsReferenceImage(_request.References[i].Path) ? "image" : "text" });
            }

            info["references"] = references;
        }

        if (_request.PendingShapes.Count > 0)
        {
            info["pendingShapes"] = CompactShapes(_request.PendingShapes, "s");
        }

        return info.ToString(Formatting.None);
    }

    /// <summary>Most shapes <see cref="CompactShapes"/> lists (the rest only count, list_shapes has them all).</summary>
    private const int MaxCompactShapes = 100;

    /// <summary>Id (e0.. for existing shapes, s0.. for pending suggestions), label, shapeType and bounding box [x, y, width, height] of each shape.</summary>
    private static JArray CompactShapes(List<RunShape> shapes, string prefix)
    {
        var list = new JArray();
        for (int i = 0; i < shapes.Count && i < MaxCompactShapes; i++)
        {
            RunShape s = shapes[i];
            List<double[]> ring = ShapeOverlap.ToRing(s.ShapeType, s.Points);
            var entry = new JObject
            {
                ["id"] = prefix + i.ToString(CultureInfo.InvariantCulture),
                ["label"] = s.Label,
                ["shapeType"] = s.ShapeType,
            };
            if (ring.Count > 0)
            {
                double minX = ring.Min(p => p[0]), maxX = ring.Max(p => p[0]);
                double minY = ring.Min(p => p[1]), maxY = ring.Max(p => p[1]);
                entry["bbox"] = new JArray(Math.Round(minX, 1), Math.Round(minY, 1), Math.Round(maxX - minX, 1), Math.Round(maxY - minY, 1));
            }

            list.Add(entry);
        }

        return list;
    }

    private JObject GetCrop(JObject args)
    {
        double? maxSide = OptionalNumber(args, "max_side");
        int side = maxSide.HasValue ? (int)Math.Round(Math.Min(ImageSource.MaxMaxSide, Math.Max(ImageSource.MinMaxSide, maxSide.Value))) : ImageSource.DefaultMaxSide;
        CropResult crop = _image.Crop(OptionalNumber(args, "x"), OptionalNumber(args, "y"), OptionalNumber(args, "width"), OptionalNumber(args, "height"), side);
        string scale = crop.Scale.ToString("0.####", CultureInfo.InvariantCulture);
        string scaleY = (crop.OutHeight / (double)crop.Height).ToString("0.####", CultureInfo.InvariantCulture);
        string text = "crop " + crop.X + "," + crop.Y + "," + crop.Width + "," + crop.Height + " scale=" + scale
            + ". Returned picture " + crop.OutWidth + "x" + crop.OutHeight + " px (never resized afterwards, at most about 1.15 megapixels). Back to ORIGINAL image pixels: image_x = "
            + crop.X + " + crop_x / " + scale + ", image_y = " + crop.Y + " + crop_y / " + scaleY + ".";
        return ToolResults.Content(
            new JObject { ["type"] = "image", ["data"] = Convert.ToBase64String(crop.Jpeg), ["mimeType"] = "image/jpeg" },
            new JObject { ["type"] = "text", ["text"] = text });
    }

    private static readonly string[] ReferenceImageExtensions = { ".png", ".jpg", ".jpeg", ".bmp", ".tif", ".tiff" };

    private const int ReferenceTextLimit = 4000;

    private static bool IsReferenceImage(string path)
    {
        return Array.IndexOf(ReferenceImageExtensions, System.IO.Path.GetExtension(path ?? string.Empty).ToLowerInvariant()) >= 0;
    }

    private JObject GetReference(JObject args)
    {
        double? requested = OptionalNumber(args, "index");
        int index = requested.HasValue ? (int)Math.Round(requested.Value) : 0;
        if (_request.References.Count == 0) throw new ArgumentException("No file is attached to this run.");
        if (index < 0 || index >= _request.References.Count) throw new ArgumentException("index must be 0.." + (_request.References.Count - 1) + ".");
        RunReference reference = _request.References[index];
        if (!File.Exists(reference.Path)) throw new ArgumentException("The attached file is missing: " + reference.Name);
        if (IsReferenceImage(reference.Path))
        {
            // The user's hint picture (for example an example defect with its box); it is a different picture from the one being labelled.
            using ImageSource source = new ImageSource(new RunRequest { ImagePath = reference.Path });
            CropResult picture = source.Crop(null, null, null, null, 1024);
            string info = "reference " + index + " (" + reference.Name + "), original " + picture.Width + "x" + picture.Height + " px, returned " + picture.OutWidth + "x" + picture.OutHeight + " px. "
                + "It is a hint from the user, NOT the image to label; do not use its pixels as coordinates of the image to label.";
            return ToolResults.Content(
                new JObject { ["type"] = "image", ["data"] = Convert.ToBase64String(picture.Jpeg), ["mimeType"] = "image/jpeg" },
                new JObject { ["type"] = "text", ["text"] = info });
        }

        string text = File.ReadAllText(reference.Path);
        if (text.Length > ReferenceTextLimit) text = text.Substring(0, ReferenceTextLimit) + "\n(cut at " + ReferenceTextLimit + " characters)";
        return ToolResults.Text("reference " + index + " (" + reference.Name + "):\n" + text);
    }

    private string ListShapes()
    {
        var shapes = new JArray();
        for (int i = 0; i < _request.ExistingShapes.Count; i++)
        {
            RunShape s = _request.ExistingShapes[i];
            shapes.Add(new JObject
            {
                ["id"] = i,
                ["fp"] = ShapeFingerprint.Compute(s.Label, s.ShapeType, s.Points),
                ["label"] = s.Label,
                ["shapeType"] = s.ShapeType,
                ["points"] = Json.Points(s.Points ?? new List<double[]>()),
            });
        }

        return new JObject { ["count"] = shapes.Count, ["shapes"] = shapes }.ToString(Formatting.None);
    }

    private string ProposeShapes(JObject args)
    {
        if (args["shapes"] is not JArray shapes) throw new ArgumentException("shapes must be an array (use [] when nothing was found).");
        return _proposals.Propose(shapes, _sam2.FindPending);
    }

    /// <summary>Most objects one detect_points call takes (a runaway list must not hold the server for minutes).</summary>
    private const int MaxDetectObjects = 200;

    // One call for a whole list: SAM2 per object, then propose_shapes for every outline that was found (duplicates are rejected by the store).
    private string DetectPoints(JObject args, CancellationToken ct)
    {
        if (args["objects"] is not JArray objects || objects.Count == 0) throw new ArgumentException("objects must be a non-empty array of {x, y, box?} in image pixels.");
        if (objects.Count > MaxDetectObjects) throw new ArgumentException("at most " + MaxDetectObjects.ToString(CultureInfo.InvariantCulture) + " objects per call.");
        string? label = args["label"]?.Type == JTokenType.String ? args["label"]!.Value<string>() : null;
        string shapeType = args["shapeType"]?.Type == JTokenType.String ? args["shapeType"]!.Value<string>()! : "oriented_rectangle";
        double scale = OptionalNumber(args, "scale") ?? 1.0;
        if (scale < 0.01 || scale > 100) throw new ArgumentException("scale must be between 0.01 and 100.");
        if (scale != 1.0) objects = ScaleObjects(objects, scale);

        IReadOnlyList<(string? Id, string? Error)> segmented = _sam2.SegmentMany(objects, label, ct);
        return ProposeSegmented(segmented, label, shapeType);
    }

    // One call for numbered outlines the app made: each object is a list of candidate ids (head + shaft = one object), joined into one outline.
    private string PickCandidates(JObject args)
    {
        if (!_candidates.Exists) throw new ArgumentException("There are no numbered candidates for this run; use detect_points.");
        if (args["objects"] is not JArray objects || objects.Count == 0) throw new ArgumentException("objects must be a non-empty array of {ids: [numbers]}.");
        if (objects.Count > MaxDetectObjects) throw new ArgumentException("at most " + MaxDetectObjects.ToString(CultureInfo.InvariantCulture) + " objects per call.");
        string? label = args["label"]?.Type == JTokenType.String ? args["label"]!.Value<string>() : null;
        string shapeType = args["shapeType"]?.Type == JTokenType.String ? args["shapeType"]!.Value<string>()! : "oriented_rectangle";
        (int w, int h) = _image.ImageSize;
        var segmented = new List<(string? Id, string? Error)>();
        foreach (JToken item in objects)
        {
            var ids = new List<int>();
            JToken? list = item is JArray ? item : item["ids"];
            if (list is JArray array)
            {
                foreach (JToken t in array)
                {
                    if (t.Type != JTokenType.Integer) throw new ArgumentException("ids must be integers.");
                    ids.Add(t.Value<int>());
                }
            }

            (List<double[]>? outer, List<List<double[]>> holes, double score, string? error) = _candidates.Union(ids, w, h);
            segmented.Add(outer == null ? ((string?)null, error) : (_sam2.AddPending(label, outer, holes, score), (string?)null));
        }

        return ProposeSegmented(segmented, label, shapeType);
    }

    private string ProposeSegmented(IReadOnlyList<(string? Id, string? Error)> segmented, string? label, string shapeType)
    {
        var shapes = new JArray();
        var indexOfShape = new List<int>();
        for (int i = 0; i < segmented.Count; i++)
        {
            if (segmented[i].Id == null) continue;
            var shape = new JObject { ["fromSam2"] = segmented[i].Id, ["shapeType"] = shapeType };
            if (!string.IsNullOrWhiteSpace(label)) shape["label"] = label;
            shapes.Add(shape);
            indexOfShape.Add(i);
        }

        JObject proposed = shapes.Count > 0 ? JObject.Parse(_proposals.Propose(shapes, _sam2.FindPending)) : new JObject { ["accepted"] = 0, ["rejected"] = 0, ["totalProposals"] = _proposals.Count, ["items"] = new JArray() };
        var failed = new JArray();
        for (int i = 0; i < segmented.Count; i++)
        {
            if (segmented[i].Id == null) failed.Add(new JObject { ["object"] = i, ["reason"] = segmented[i].Error });
        }

        if (proposed["items"] is JArray items)
        {
            for (int k = 0; k < items.Count && k < indexOfShape.Count; k++)
            {
                if (items[k]["status"]?.Value<string>() == "rejected") failed.Add(new JObject { ["object"] = indexOfShape[k], ["reason"] = items[k]["reason"] });
            }
        }

        return new JObject
        {
            ["objects"] = segmented.Count,
            ["accepted"] = proposed["accepted"],
            ["totalProposals"] = proposed["totalProposals"],
            ["failed"] = failed,
            ["next"] = failed.Count == 0 ? "All objects were proposed. Finish with one short line; do not call propose_shapes again." : "Retry only the failed objects with a tighter box or another point, or accept the result.",
        }.ToString(Formatting.None);
    }

    // Positions read from a downscaled picture: x, y and box values times the picture's factor give original pixels.
    private static JArray ScaleObjects(JArray objects, double scale)
    {
        var scaled = new JArray();
        foreach (JToken item in objects)
        {
            if (item is not JObject o) throw new ArgumentException("each object must be {x, y, box?}.");
            var copy = new JObject();
            foreach (string axis in new[] { "x", "y" })
            {
                if (o[axis] == null) continue;
                if (!Json.TryNumber(o[axis], out double v)) throw new ArgumentException(axis + " must be a finite number.");
                copy[axis] = v * scale;
            }

            if (o["box"] is JArray box)
            {
                var values = new JArray();
                foreach (JToken b in box)
                {
                    if (!Json.TryNumber(b, out double v)) throw new ArgumentException("box values must be finite numbers.");
                    values.Add(v * scale);
                }

                copy["box"] = values;
            }

            scaled.Add(copy);
        }

        return scaled;
    }

    private static double? OptionalNumber(JObject args, string name)
    {
        JToken? token = args[name];
        if (token == null || token.Type == JTokenType.Null) return null;
        if (!Json.TryNumber(token, out double value)) throw new ArgumentException(name + " must be a finite number.");
        return value;
    }

    private static JArray BuildDescriptions()
    {
        return JArray.Parse("""
        [
          {
            "name": "get_image_info",
            "description": "Image size in pixels, the existing class names (prefer these; a label not in the list creates a new class when the user accepts the shape), the existing shapes (id e0.., label, shapeType, bbox [x, y, width, height]; the first 100), the user's not yet reviewed suggestions as pendingShapes (id s0..), and the task prompt. propose_shapes rejects a new shape that duplicates an existing shape or a pending suggestion, so do not propose parts that are already labelled. Call this first.",
            "inputSchema": { "type": "object", "properties": {}, "required": [] }
          },
          {
            "name": "pick_candidates",
            "description": "Only when the prompt lists numbered SAM2 candidates: proposes one shape per object, each object given as the list of candidate numbers that make it up (a screw = its head candidate + its shaft candidate). The outlines are joined, the rotated box is fitted and everything is proposed in this ONE call. Candidates that are not wanted are simply not listed. An object without a candidate: use detect_points for it afterwards.",
            "inputSchema": {
              "type": "object",
              "properties": {
                "objects": { "type": "array", "items": { "type": "object", "properties": { "ids": { "type": "array", "items": { "type": "integer" }, "minItems": 1 } }, "required": ["ids"] }, "minItems": 1 },
                "label": { "type": "string", "description": "Class name given to every shape." },
                "shapeType": { "type": "string", "description": "polygon, rectangle or oriented_rectangle (default oriented_rectangle)." }
              },
              "required": ["objects"]
            }
          },
          {
            "name": "get_crop",
            "description": "Returns a JPEG of the image or of a rectangle (image pixels, clamped to the image), downscaled so its longer side is at most max_side and it never exceeds 1.15 megapixels (about 1190 px long side for a 2448x2048 image). With no arguments returns the whole image (a small overview: do not read exact coordinates from it). The text part reports the REAL returned size and 'scale': a point at (crop_x, crop_y) in the returned picture is at image_x = x + crop_x / scale, image_y = y + crop_y / scale in the original image (x, y = crop origin, 0 for the whole image); always convert with that scale, never use picture pixels as image pixels. Every crop costs tokens: use few.",
            "inputSchema": {
              "type": "object",
              "properties": {
                "x": { "type": "number", "description": "Left edge of the rectangle in image pixels (default 0)." },
                "y": { "type": "number", "description": "Top edge of the rectangle in image pixels (default 0)." },
                "width": { "type": "number", "description": "Rectangle width in image pixels (default: to the right edge)." },
                "height": { "type": "number", "description": "Rectangle height in image pixels (default: to the bottom edge)." },
                "max_side": { "type": "integer", "description": "Longest side of the returned picture in pixels (default 768, max 1536; also limited to 1.15 megapixels in total)." }
              },
              "required": []
            }
          },
          {
            "name": "get_reference",
            "description": "Returns one file the user attached as a hint (listed as references in get_image_info): an example picture of the defect or object (returned as a JPEG) or a short text note. It is NOT the image to label. Look at a reference before you search for the objects; match what it shows.",
            "inputSchema": {
              "type": "object",
              "properties": {
                "index": { "type": "integer", "description": "Which attached file (0-based, default 0)." }
              },
              "required": []
            }
          },
          {
            "name": "memory_read",
            "description": "The project memory: short notes about this project's labelling (naming rules, corrections of the user, facts about the parts). Read it once at the start of a run.",
            "inputSchema": { "type": "object", "properties": {}, "required": [] }
          },
          {
            "name": "memory_add",
            "description": "Adds ONE short note (at most 400 characters) to the project memory for later runs. Save only what stays true for this project and is not obvious from the image: a label naming rule, a correction the user made, a recurring defect type. Never save coordinates or one-off details.",
            "inputSchema": {
              "type": "object",
              "properties": { "note": { "type": "string", "description": "One short sentence." } },
              "required": ["note"]
            }
          },
          {
            "name": "list_shapes",
            "description": "Shapes already present on the image: id (index, use it as target in propose_shapes), fp (shape hash), label, shapeType, points in image pixels. Do not propose duplicates of them.",
            "inputSchema": { "type": "object", "properties": {}, "required": [] }
          },
          {
            "name": "sam2_segment",
            "description": "Segments one object with SAM2 from a box and/or clicks (image pixels). Returns the polygon and an id; nothing is proposed yet. To keep the result call propose_shapes with {fromSam2: id, label}. Prefer this over typing polygon points by hand.",
            "inputSchema": {
              "type": "object",
              "properties": {
                "box": { "type": "array", "items": { "type": "number" }, "minItems": 4, "maxItems": 4, "description": "[x1, y1, x2, y2] around the object, image pixels." },
                "points": {
                  "type": "array",
                  "items": {
                    "type": "object",
                    "properties": {
                      "x": { "type": "number" },
                      "y": { "type": "number" },
                      "positive": { "type": "boolean", "description": "true = on the object (default), false = background." }
                    },
                    "required": ["x", "y"]
                  },
                  "description": "Clicks in image pixels."
                },
                "label": { "type": "string", "description": "Optional label (class name or a new label) remembered for propose_shapes." }
              },
              "required": []
            }
          },
          {
            "name": "detect_points",
            "description": "FASTEST way to label many similar objects: ONE call for the whole list. Give one click point on each object (image pixels; the centre of the object) and optionally a rough box [x1,y1,x2,y2] around it; the app runs SAM2 for every object, fits the shape and proposes all of them at once (same as sam2_segment + propose_shapes for each). Returns how many were accepted and which objects failed. Use it instead of many sam2_segment calls and a long propose_shapes list; do not type box corners yourself.",
            "inputSchema": {
              "type": "object",
              "properties": {
                "objects": {
                  "type": "array",
                  "items": {
                    "type": "object",
                    "properties": {
                      "x": { "type": "number", "description": "Click on the object, image pixels." },
                      "y": { "type": "number", "description": "Click on the object, image pixels." },
                      "box": { "type": "array", "items": { "type": "number" }, "minItems": 4, "maxItems": 4, "description": "Optional rough [x1, y1, x2, y2] around the whole object, image pixels." }
                    },
                    "required": []
                  }
                },
                "label": { "type": "string", "description": "A classNames entry (or the label the task names) for every object of this call." },
                "shapeType": { "type": "string", "enum": ["oriented_rectangle", "rectangle", "polygon"], "description": "Default oriented_rectangle (rotated box fitted to the SAM2 outline)." },
                "scale": { "type": "number", "description": "Factor from your picture to the original image (original = picture x scale). Give it when x, y and box were read from a downscaled preview; default 1 = original pixels." }
              },
              "required": ["objects"]
            }
          },
          {
            "name": "propose_shapes",
            "description": "Submits the final shapes (writes proposals.json; the user reviews them). Each shape: label (an entry of classNames, or a new label name the task asks for); shapeType 'polygon' (>= 3 points), 'rectangle' (2 corners or 4 points, axis-aligned) or 'oriented_rectangle' (for a rotated or tilted part, an OBB or a rotated box: trace the object's OUTLINE as 8-16 points around its visible edge, or give its 4 corners if it is clearly a rectangle; the app fits the minimum-area rotated box to all the points and stores its 4 corners. Never output an axis-aligned box for a part that is visibly tilted: a tilted box needs corner coordinates that do not share x or y values); points as [[x,y],...] in image pixels; or fromSam2 with an id from sam2_segment instead of points (with shapeType 'oriented_rectangle' or 'rectangle' the app fits that box to the SAM2 outline: the best way to get an exact rotated box). An item with an existing id replaces that proposal. To review an EXISTING shape (id from list_shapes) give target plus action 'replace' (points or fromSam2 = the corrected shape) or 'remove' (no points; the shape is wrong or a duplicate) and a note with the reason; at most one item per target; action 'add' (default) takes no target. Send [] if nothing was found.",
            "inputSchema": {
              "type": "object",
              "properties": {
                "shapes": {
                  "type": "array",
                  "items": {
                    "type": "object",
                    "properties": {
                      "id": { "type": "string", "description": "Optional; reuse an id to replace that proposal." },
                      "label": { "type": "string", "description": "A classNames entry, or a new label name when the task asks for one (the user's own wording, at most 64 characters)." },
                      "shapeType": { "type": "string", "enum": ["polygon", "rectangle", "oriented_rectangle"], "description": "oriented_rectangle: the object's outline as 8-16 points around its visible edge (or its 4 corners if it is clearly a rectangle); the app fits the rotated box. Never axis-aligned for a tilted part." },
                      "points": { "type": "array", "items": { "type": "array", "items": { "type": "number" }, "minItems": 2, "maxItems": 2 } },
                      "fromSam2": { "type": "string", "description": "id returned by sam2_segment, used instead of points. With shapeType oriented_rectangle or rectangle the box is fitted to the SAM2 outline; otherwise the polygon is kept." },
                      "score": { "type": "number" },
                      "note": { "type": "string", "description": "Short reason, shown to the user. Required when target is given." },
                      "target": { "type": "integer", "minimum": 0, "description": "Review only: id of an existing shape from list_shapes. Requires action 'replace' or 'remove' and a note." },
                      "action": { "type": "string", "enum": ["add", "replace", "remove"], "description": "add (default): a new shape. replace: the corrected version of target. remove: delete target (no points, label optional)." }
                    },
                    "required": []
                  }
                }
              },
              "required": ["shapes"]
            }
          }
        ]
        """);
    }

    public void Dispose()
    {
        _sam2.Dispose();
        _image.Dispose();
    }
}
