using System.Globalization;
using System.Text;
using EasyLabel.Core;
using EasyLabel.Core.Convert;
using EasyLabel.Core.Io;
using EasyLabel.Core.Model;
using EasyLabel.SegAI.Convert;
using EasyLabel.SegAI.Data;
using EasyLabel.SegAI.Project;
using Newtonsoft.Json;
using OpenCvSharp;
using CvPoint = OpenCvSharp.Point;

namespace EasyLabel.SegAI.Export;

/// <summary>
/// The one entry point for exporting a project (UI free). It reads the LabelMe JSON beside each image, picks the images of the chosen
/// Train/Val set, and writes the chosen format through the existing converters (YOLO, COCO, Pascal VOC, EasyVision dataset). A shape a
/// format cannot hold is left out and listed in <see cref="ExportResult.Skips"/>; nothing is dropped silently. The project is only read.
/// </summary>
public sealed class ExportService
{
    private static readonly string[] ImageExtensions = { ".jpg", ".jpeg", ".png", ".bmp" };

    private readonly LabelMeIO _labelMe = new LabelMeIO();

    /// <summary>Exports the project. Throws <see cref="ExportException"/> before writing anything when the request is refused;
    /// <see cref="OperationCanceledException"/> after removing what this run had written.</summary>
    public ExportResult Run(ExportRequest request, IProgress<ExportProgress>? progress, CancellationToken ct)
    {
        if (request == null) throw new ArgumentNullException(nameof(request));
        string requestRoot = request.Paths?.ImageRoot ?? request.ProjectRoot;
        if (string.IsNullOrWhiteSpace(requestRoot) || !Directory.Exists(requestRoot))
            throw new ExportException("The project folder does not exist: " + requestRoot);
        if (!Enum.IsDefined(typeof(ExportFormat), request.Format) || !Enum.IsDefined(typeof(ExportSet), request.Set))
            throw new ExportException("The export format or set is not valid.");

        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(requestRoot));
        ProjectPaths paths = request.Paths ?? ProjectPaths.Legacy(root);
        string outDir = ValidateOutput(root, paths.MetaDir, request.OutputDirectory, request.OverwriteExisting);
        string slug = Slug(request.Format);
        string zipPath = Path.Combine(outDir, slug + ".zip");
        if (request.WriteZip && File.Exists(zipPath) && !request.OverwriteExisting)
            throw new ExportException("The file already exists: " + zipPath);

        bool needsClasses = request.Format != ExportFormat.LabelMeJson && request.Format != ExportFormat.EasyVisionDataset;
        List<string> classes = ReadClasses(paths.ClassesFile, needsClasses);

        var store = new SegProjectStore(paths);
        var context = new ExportContext
        {
            Request = request,
            Root = root,
            OutDir = outDir,
            ClassesFile = paths.ClassesFile,
            Classes = classes,
            Map = store.LoadClassMap(),
            Store = store,
            Progress = progress,
            Ct = ct,
            Items = EnumerateImages(paths, root, outDir),
            Result = new ExportResult { Format = request.Format, OutputPath = request.WriteZip ? zipPath : outDir },
        };

        Snapshot snapshot = Snapshot.Take(outDir);
        try
        {
            Execute(context, slug, zipPath);
            progress?.Report(new ExportProgress(context.Items.Count, context.Items.Count, string.Empty));
            return context.Result;
        }
        catch
        {
            snapshot.Rollback(outDir);
            throw;
        }
    }

    // ---- request checks ----

    private static string ValidateOutput(string root, string metaDir, string outputDirectory, bool overwrite)
    {
        if (string.IsNullOrWhiteSpace(outputDirectory)) throw new ExportException("The output folder is required.");
        string full;
        try
        {
            full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(outputDirectory));
        }
        catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
        {
            throw new ExportException("The output folder is not a valid path: " + outputDirectory);
        }

        if (File.Exists(full)) throw new ExportException("The output path is a file, not a folder: " + full);
        if (SamePath(full, root) || IsUnder(full, root))
            throw new ExportException("The output folder is the project folder or contains it: " + full);

        // The metadata folder may live outside the image root (a named project): never export into it, around it or over it.
        string meta = Path.TrimEndingDirectorySeparator(Path.GetFullPath(metaDir));
        if (SamePath(full, meta) || IsUnder(full, meta) || IsUnder(meta, full))
            throw new ExportException("The output folder is the project's data folder, contains it or lies inside it: " + full);

        bool exists = Directory.Exists(full);
        bool empty = !exists || !Directory.EnumerateFileSystemEntries(full).Any();
        if (IsUnder(root, full))
        {
            // Later project scans would list the exported image copies (and their JSON) as project images.
            throw new ExportException("The output folder must be outside the project folder: " + full);
        }

        if (!empty && !overwrite)
        {
            throw new ExportException("The output folder is not empty: " + full);
        }

        return full;
    }

    private static List<string> ReadClasses(string path, bool required)
    {
        if (!File.Exists(path))
        {
            if (required) throw new ExportException("classes.txt is missing in the project.");
            return new List<string>();
        }

        return File.ReadAllLines(path).Select(line => line.Trim()).Where(line => line.Length > 0).ToList();
    }

    private static bool SamePath(string a, string b)
    {
        return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }

    // True when child lies below parent.
    private static bool IsUnder(string parent, string child)
    {
        string prefix = parent.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return child.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static List<ImageItem> EnumerateImages(ProjectPaths paths, string root, string outDir)
    {
        var found = new List<ImageItem>();
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            string directory = pending.Pop();
            List<string> subdirectories;
            List<string> files;
            try
            {
                subdirectories = Directory.EnumerateDirectories(directory).ToList();
                files = Directory.EnumerateFiles(directory).ToList();
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                continue;
            }

            foreach (string sub in subdirectories)
            {
                if (paths.IsSkippedDirectory(sub)) continue;
                if (SamePath(sub, outDir) || IsUnder(outDir, sub)) continue;
                pending.Push(sub);
            }

            foreach (string file in files)
            {
                if (!ImageExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase)) continue;
                string rel = Path.GetRelativePath(root, file).Replace('\\', '/');
                found.Add(new ImageItem(rel, file, Path.ChangeExtension(file, ".json")));
            }
        }

        found.Sort((a, b) => string.Compare(a.Rel, b.Rel, StringComparison.OrdinalIgnoreCase));
        return found;
    }

    private static string Slug(ExportFormat format)
    {
        switch (format)
        {
            case ExportFormat.LabelMeJson: return "labelme";
            case ExportFormat.YoloDetect: return "yolo-detect";
            case ExportFormat.YoloSegment: return "yolo-segment";
            case ExportFormat.CocoInstances: return "coco";
            case ExportFormat.PascalVoc: return "voc";
            case ExportFormat.MaskPng: return "masks";
            case ExportFormat.EasyVisionDataset: return "easyvision-dataset";
            default: return "csv";
        }
    }

    // ---- driver ----

    private void Execute(ExportContext ctx, string slug, string zipPath)
    {
        ExportRequest request = ctx.Request;
        if (request.Format == ExportFormat.EasyVisionDataset)
        {
            // Streamed: each entry is produced, written and released before the next one is read.
            IEnumerable<EvDatasetEntry> entries = EvEntries(ctx);
            if (request.WriteZip)
            {
                EasyVisionDataset.WriteZipCore(zipPath, entries, ctx.Map.HasNamedTable ? ctx.Map.Table : null);
                ctx.Result.FilesWritten.Add(zipPath);
            }
            else
            {
                Snapshot before = Snapshot.Take(ctx.OutDir);
                EasyVisionDataset.WriteFolderCore(ctx.OutDir, entries, ctx.Map.HasNamedTable ? ctx.Map.Table : null);
                ctx.Result.FilesWritten.AddRange(before.NewFiles(ctx.OutDir));
            }

            return;
        }

        ExportSink sink = request.WriteZip ? new ZipExportSink(zipPath, request.OverwriteExisting) : (ExportSink)new FolderExportSink(ctx.OutDir, request.OverwriteExisting);
        using (sink)
        {
            ctx.Sink = sink;
            var coco = new List<CocoImageSource>();
            var csv = new StringBuilder();
            if (request.Format == ExportFormat.Csv) csv.Append(CsvHeader(ctx.Classes)).Append("\r\n");
            YoloAnnotationConverter? yolo = request.Format == ExportFormat.YoloDetect ? new YoloAnnotationConverter(LabelConvertFormat.YoloDetect)
                : request.Format == ExportFormat.YoloSegment ? new YoloAnnotationConverter(LabelConvertFormat.YoloSegment) : null;

            foreach (Visit visit in Visits(ctx))
            {
                switch (request.Format)
                {
                    case ExportFormat.LabelMeJson: AddLabelMe(ctx, visit); break;
                    case ExportFormat.YoloDetect:
                    case ExportFormat.YoloSegment: AddYolo(ctx, visit, yolo!); break;
                    case ExportFormat.CocoInstances: AddCoco(ctx, visit, coco); break;
                    case ExportFormat.PascalVoc: AddVoc(ctx, visit); break;
                    case ExportFormat.MaskPng: AddMask(ctx, visit); break;
                    default: AddCsvRow(ctx, visit, csv); break;
                }
            }

            Finish(ctx, coco, csv);
            sink.Complete();
            ctx.Result.FilesWritten.AddRange(sink.Written);
        }
    }

    private void Finish(ExportContext ctx, List<CocoImageSource> coco, StringBuilder csv)
    {
        ExportSink sink = ctx.Sink!;
        switch (ctx.Request.Format)
        {
            case ExportFormat.LabelMeJson:
                {
                    if (File.Exists(ctx.ClassesFile)) sink.CopyFile("classes.txt", ctx.ClassesFile);
                    break;
                }
            case ExportFormat.YoloDetect:
            case ExportFormat.YoloSegment:
                {
                    string dataPath = ctx.Request.WriteZip ? "." : ctx.OutDir;
                    List<string> yaml = UltralyticsExportService.BuildDataYamlLines(dataPath, ctx.Classes, false);
                    // A split with no image has no folder: Ultralytics fails on a data.yaml that points to a missing one.
                    if (!ctx.UsedSplits.Contains("train")) yaml.RemoveAll(line => line.StartsWith("train:", StringComparison.Ordinal));
                    if (!ctx.UsedSplits.Contains("val")) yaml.RemoveAll(line => line.StartsWith("val:", StringComparison.Ordinal));
                    sink.WriteText("data.yaml", string.Join("\n", yaml) + "\n");
                    sink.WriteText("classes.txt", string.Join("\n", ctx.Classes) + "\n");
                    break;
                }
            case ExportFormat.CocoInstances:
                {
                    CocoDataset dataset = new CocoExporter().Export(coco, ctx.Classes);
                    sink.WriteText("annotations.coco.json", JsonConvert.SerializeObject(dataset, Formatting.Indented));
                    break;
                }
            case ExportFormat.PascalVoc:
            case ExportFormat.MaskPng:
                sink.WriteText("classes.txt", string.Join("\n", ctx.Classes) + "\n");
                break;
            case ExportFormat.Csv:
                sink.WriteText("labels.csv", csv.ToString());
                break;
        }
    }

    // One pass over the images: reports progress, honours cancellation, reads the JSON, applies the set and the annotated filter.
    private IEnumerable<Visit> Visits(ExportContext ctx)
    {
        ExportRequest request = ctx.Request;
        int done = 0;
        foreach (ImageItem item in ctx.Items)
        {
            ctx.Ct.ThrowIfCancellationRequested();
            ctx.Progress?.Report(new ExportProgress(done, ctx.Items.Count, item.Rel));
            done++;

            LabelMeDocument? doc = null;
            string? readError = null;
            if (File.Exists(item.JsonPath))
            {
                try
                {
                    doc = _labelMe.Read(item.JsonPath);
                    doc.Shapes ??= new List<LabelShape>();
                    doc.Flags ??= new Dictionary<string, bool>();
                }
                catch (Exception ex) when (ex is JsonException || ex is IOException || ex is UnauthorizedAccessException)
                {
                    readError = ex.Message;
                }
            }

            if (readError != null)
            {
                SkipImage(ctx, item.Rel, "the LabelMe JSON could not be read: " + readError);
                continue;
            }

            byte mode = doc == null ? ImageModeFlags.Auto : ImageModeFlags.Read(doc.Flags);
            if (request.Set == ExportSet.Train && mode == ImageModeFlags.Val) continue;
            if (request.Set == ExportSet.Val && mode != ImageModeFlags.Val) continue;

            if (request.OnlyAnnotated && (doc == null || doc.Shapes.Count == 0))
            {
                SkipImage(ctx, item.Rel, "no annotations");
                continue;
            }

            yield return new Visit(item, doc, mode);
        }
    }

    // ---- LabelMe JSON ----

    private void AddLabelMe(ExportContext ctx, Visit visit)
    {
        ctx.Sink!.CopyFile(visit.Item.Rel, visit.Item.ImagePath);
        if (visit.Doc != null) ctx.Sink.CopyFile(Path.ChangeExtension(visit.Item.Rel, ".json"), visit.Item.JsonPath);
        ctx.Result.ImagesExported++;
        ctx.Result.ShapesExported += visit.Doc?.Shapes.Count ?? 0;
    }

    // ---- YOLO ----

    private void AddYolo(ExportContext ctx, Visit visit, YoloAnnotationConverter converter)
    {
        bool segment = ctx.Request.Format == ExportFormat.YoloSegment;
        string formatName = segment ? "YOLO segment" : "YOLO detect";
        List<LabelShape> kept = FilterShapes(ctx, visit, formatName, segment);
        if (!HasContent(ctx, visit, kept)) return;
        if (!TryGetSize(visit, false, out int width, out int height))
        {
            SkipImage(ctx, visit.Item.Rel, "the image size could not be read");
            return;
        }

        kept = ClipShapesToImage(ctx, visit, kept, width, height);
        if (!HasContent(ctx, visit, kept)) return;
        string stem = ctx.Alloc.Stem(visit.Item.Rel);
        string flat = stem + Path.GetExtension(visit.Item.Rel);
        string split = visit.Mode == ImageModeFlags.Val ? "val" : "train";
        var document = new LabelMeDocument { ImagePath = flat, ImageWidth = width, ImageHeight = height, Shapes = kept };
        string[] lines = converter.Export(document, ctx.Classes);

        ctx.UsedSplits.Add(split);
        ctx.Sink!.CopyFile("images/" + split + "/" + flat, visit.Item.ImagePath);
        ctx.Sink.WriteText("labels/" + split + "/" + stem + ".txt", lines.Length == 0 ? string.Empty : string.Join("\n", lines) + "\n");
        ctx.Result.ImagesExported++;
        ctx.Result.ShapesExported += kept.Count;
    }

    // ---- COCO ----

    private void AddCoco(ExportContext ctx, Visit visit, List<CocoImageSource> sources)
    {
        List<LabelShape> kept = FilterShapes(ctx, visit, "COCO instances", true);
        if (!HasContent(ctx, visit, kept)) return;
        if (!TryGetSize(visit, false, out int width, out int height))
        {
            SkipImage(ctx, visit.Item.Rel, "the image size could not be read");
            return;
        }

        kept = ClipShapesToImage(ctx, visit, kept, width, height);
        if (!HasContent(ctx, visit, kept)) return;
        string flat = ctx.Alloc.Stem(visit.Item.Rel) + Path.GetExtension(visit.Item.Rel);
        var document = new LabelMeDocument { ImagePath = flat, ImageWidth = width, ImageHeight = height, Shapes = kept };
        sources.Add(new CocoImageSource(sources.Count + 1, flat, document));
        ctx.Sink!.CopyFile("images/" + flat, visit.Item.ImagePath);
        ctx.Result.ImagesExported++;
        ctx.Result.ShapesExported += kept.Count;
    }

    // ---- Pascal VOC ----

    private void AddVoc(ExportContext ctx, Visit visit)
    {
        List<LabelShape> kept = FilterShapes(ctx, visit, "Pascal VOC", false);
        if (!HasContent(ctx, visit, kept)) return;
        if (!TryGetSize(visit, false, out int width, out int height))
        {
            SkipImage(ctx, visit.Item.Rel, "the image size could not be read");
            return;
        }

        kept = ClipShapesToImage(ctx, visit, kept, width, height);
        if (!HasContent(ctx, visit, kept)) return;
        string stem = ctx.Alloc.Stem(visit.Item.Rel);
        string flat = stem + Path.GetExtension(visit.Item.Rel);
        var document = new LabelMeDocument { ImagePath = flat, ImageWidth = width, ImageHeight = height, Shapes = kept };
        string[] lines = new PascalVocConverter().Export(document, ctx.Classes);
        ctx.Sink!.CopyFile("images/" + flat, visit.Item.ImagePath);
        ctx.Sink.WriteText("annotations/" + stem + ".xml", string.Join("\n", lines) + "\n");
        ctx.Result.ImagesExported++;
        ctx.Result.ShapesExported += kept.Count;
    }

    // ---- Mask PNG ----

    private void AddMask(ExportContext ctx, Visit visit)
    {
        List<LabelShape> kept = FilterShapes(ctx, visit, "mask PNG", false);
        // A class number above 255 does not fit an 8-bit mask.
        for (int i = kept.Count - 1; i >= 0; i--)
        {
            int index = ctx.Classes.IndexOf(kept[i].Label ?? string.Empty);
            if (index + 1 > 255)
            {
                SkipShape(ctx, visit.Item.Rel, DescribeShape(visit, kept[i]), "the class number is above 255 (8-bit mask)");
                kept.RemoveAt(i);
            }
        }

        if (!HasContent(ctx, visit, kept)) return;
        if (!TryGetSize(visit, true, out int width, out int height))
        {
            SkipImage(ctx, visit.Item.Rel, "the image could not be read");
            return;
        }

        string stem = ctx.Alloc.Stem(visit.Item.Rel);
        string flat = stem + Path.GetExtension(visit.Item.Rel);
        byte[] png;
        using (var mask = new Mat(height, width, MatType.CV_8UC1, Scalar.All(0)))
        {
            foreach (LabelShape shape in kept)
            {
                byte value = (byte)(ctx.Classes.IndexOf(shape.Label ?? string.Empty) + 1);
                using (var region = new Mat(mask.Size(), MatType.CV_8UC1, Scalar.All(0)))
                {
                    Cv2.FillPoly(region, new[] { OutlineOf(shape).Select(Round).ToArray() }, Scalar.All(255));
                    foreach (System.Drawing.PointF[] hole in SegLabelMeConverter.ReadHoles(shape))
                        if (hole.Length >= 3)
                            Cv2.FillPoly(region, new[] { hole.Select(p => new CvPoint((int)Math.Round(p.X), (int)Math.Round(p.Y))).ToArray() }, Scalar.All(0));
                    mask.SetTo(new Scalar(value), region);
                }
            }

            if (!Cv2.ImEncode(".png", mask, out png)) throw new IOException("Could not encode the mask of " + visit.Item.Rel);
        }

        ctx.Sink!.CopyFile("images/" + flat, visit.Item.ImagePath);
        ctx.Sink.WriteBytes("masks/" + stem + ".png", png);
        ctx.Result.ImagesExported++;
        ctx.Result.ShapesExported += kept.Count;
    }

    // ---- CSV ----

    private static string CsvHeader(IReadOnlyList<string> classes)
    {
        var cells = new List<string> { "image", "mode", "width", "height", "shapes", "regions" };
        cells.AddRange(classes.Select(c => "label:" + c));
        cells.Add("other");
        return string.Join(",", cells.Select(Csv));
    }

    private void AddCsvRow(ExportContext ctx, Visit visit, StringBuilder csv)
    {
        TryGetSize(visit, false, out int width, out int height);
        List<LabelShape> shapes = visit.Doc?.Shapes ?? new List<LabelShape>();
        var counts = new int[ctx.Classes.Count];
        int other = 0;
        int regions = 0;
        foreach (LabelShape shape in shapes)
        {
            if (shape == null) continue;
            int index = ctx.Classes.IndexOf(shape.Label ?? string.Empty);
            if (index >= 0) counts[index]++; else other++;
            if (ctx.Map.LabelOf(shape.Label) != null && EvUnsupported(shape) == null) regions++;
        }

        var cells = new List<string>
        {
            visit.Item.Rel,
            visit.Mode == ImageModeFlags.Val ? "val" : visit.Mode == ImageModeFlags.Train ? "train" : "auto",
            width.ToString(CultureInfo.InvariantCulture),
            height.ToString(CultureInfo.InvariantCulture),
            shapes.Count.ToString(CultureInfo.InvariantCulture),
            regions.ToString(CultureInfo.InvariantCulture),
        };
        cells.AddRange(counts.Select(c => c.ToString(CultureInfo.InvariantCulture)));
        cells.Add(other.ToString(CultureInfo.InvariantCulture));
        csv.Append(string.Join(",", cells.Select(Csv))).Append("\r\n");
        ctx.Result.ImagesExported++;
        ctx.Result.ShapesExported += shapes.Count;
    }

    private static string Csv(string value)
    {
        if (value.IndexOfAny(new[] { ',', '"', '\r', '\n' }) < 0) return value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    // ---- EasyVision dataset ----

    private IEnumerable<EvDatasetEntry> EvEntries(ExportContext ctx)
    {
        foreach (Visit visit in Visits(ctx))
        {
            EvDatasetEntry? entry = BuildEvEntry(ctx, visit);
            if (entry != null)
            {
                ctx.Result.ImagesExported++;
                yield return entry;
            }
        }
    }

    private static EvDatasetEntry? BuildEvEntry(ExportContext ctx, Visit visit)
    {
        string rel = visit.Item.Rel;
        List<LabelShape> shapes = visit.Doc?.Shapes ?? new List<LabelShape>();
        var mapped = new List<LabelShape>();
        for (int i = 0; i < shapes.Count; i++)
        {
            LabelShape shape = shapes[i];
            if (shape == null) continue;
            string description = DescribeShape(visit, shape);
            if (ctx.Map.LabelOf(shape.Label) == null)
            {
                SkipShape(ctx, rel, description, "the label '" + (shape.Label ?? string.Empty) + "' is not mapped to " + (ctx.Map.HasNamedTable ? "a class (" + string.Join("/", ctx.Map.Table.Classes.Select(c => c.Name)) + ")" : "NG or OK"));
                continue;
            }

            mapped.Add(shape);
            string? why = EvUnsupported(shape);
            if (why != null) SkipShape(ctx, rel, description, why);
        }

        // The mapped shapes (not only the convertible ones) are what the cached region file was hashed over.
        SegSidecar? sidecar = ctx.Store.ReadSidecar(rel);
        List<SegAnnotation> regions = SegLabelMeConverter.FromLabelMe(mapped, sidecar, ctx.Map, out _, out _);
        if (ctx.Request.OnlyAnnotated && regions.Count == 0)
        {
            SkipImage(ctx, rel, "no shape that EasyVision can use");
            return null;
        }

        string name = ctx.Alloc.Stem(rel) + Path.GetExtension(rel);
        byte[]? pngBytes = null;
        try
        {
            byte[] file = File.ReadAllBytes(visit.Item.ImagePath);
            using (Mat decoded = Cv2.ImDecode(file, ImreadModes.Color))
            {
                if (decoded.Empty()) throw new InvalidDataException("the image could not be decoded");
                regions = SegLabelRaster.ClipToImage(regions, decoded.Width, decoded.Height); // a part cut by the border is exported as its visible polygon
                SegLabelRaster.ValidateAnnotations(regions, decoded.Width, decoded.Height, ctx.Map.Count);
                if (!string.Equals(Path.GetExtension(visit.Item.ImagePath), ".png", StringComparison.OrdinalIgnoreCase)
                    && !Cv2.ImEncode(".png", decoded, out pngBytes))
                    throw new IOException("the image could not be encoded as PNG");
            }
        }
        catch (Exception ex) when (ex is InvalidDataException || ex is IOException || ex is UnauthorizedAccessException || ex is OpenCVException)
        {
            SkipImage(ctx, rel, ex.Message);
            return null;
        }

        ctx.Result.ShapesExported += regions.Count;
        return new EvDatasetEntry(name, pngBytes == null ? visit.Item.ImagePath : null, null, pngBytes, null, regions, visit.Mode);
    }

    // The shape types SegLabelMeConverter.FromLabelMe turns into regions.
    private static string? EvUnsupported(LabelShape shape)
    {
        string type = shape.ShapeType ?? string.Empty;
        List<double[]> pts = shape.Points ?? new List<double[]>();
        int need;
        bool exact = false;
        switch (type)
        {
            case "polygon": need = 3; break;
            case "rectangle": need = 2; exact = true; break;
            case "rotation":
            case "oriented_rectangle": need = 4; break;
            case "circle":
            case "point":
            case "line":
            case "linestrip":
                return type + " shapes cannot be written to the EasyVision dataset";
            default:
                return "the shape type '" + type + "' is not supported by the EasyVision dataset";
        }

        if (pts.Count < need || (exact && pts.Count != need) || pts.Any(p => p == null || p.Length < 2))
            return type + " needs " + (exact ? "exactly " : "at least ") + need + " valid points";
        return null;
    }

    // ---- shared helpers ----

    // Keeps the shapes the format can hold and whose label is in classes.txt; every other shape goes into the skip list.
    /// <summary>
    /// A shape cut by the image border (out-of-bounds labelling) is exported as its visible part: polygons and oriented boxes become the clipped
    /// polygon, a rectangle the clipped rectangle. A shape fully inside keeps its instance (output unchanged); one with no visible area is
    /// skipped and listed. Circles, points and lines are left as they are.
    /// </summary>
    private static List<LabelShape> ClipShapesToImage(ExportContext ctx, Visit visit, List<LabelShape> shapes, int width, int height)
    {
        var result = new List<LabelShape>(shapes.Count);
        foreach (LabelShape shape in shapes)
        {
            string type = shape.ShapeType ?? string.Empty;
            List<double[]> pts = shape.Points ?? new List<double[]>();
            bool box = type == "rectangle" || type == "oriented_rectangle" || type == "polygon";
            if (!box || pts.All(p => p[0] >= 0 && p[0] <= width && p[1] >= 0 && p[1] <= height))
            {
                result.Add(shape);
                continue;
            }

            var ring = new List<System.Drawing.PointF>();
            if (type == "rectangle" && pts.Count == 2)
            {
                double x1 = Math.Min(pts[0][0], pts[1][0]), x2 = Math.Max(pts[0][0], pts[1][0]), y1 = Math.Min(pts[0][1], pts[1][1]), y2 = Math.Max(pts[0][1], pts[1][1]);
                ring.AddRange(new[] { new System.Drawing.PointF((float)x1, (float)y1), new System.Drawing.PointF((float)x2, (float)y1), new System.Drawing.PointF((float)x2, (float)y2), new System.Drawing.PointF((float)x1, (float)y2) });
            }
            else
            {
                ring.AddRange(pts.Select(p => new System.Drawing.PointF((float)p[0], (float)p[1])));
            }

            List<SegAnnotation> clipped = SegLabelRaster.ClipToImage(new[] { new SegAnnotation { Points = ring.ToArray() } }, width, height);
            if (clipped.Count == 0 || clipped[0].Points.Length < 3)
            {
                SkipShape(ctx, visit.Item.Rel, DescribeShape(visit, shape), "the shape lies outside the image (no visible area)");
                continue;
            }

            System.Drawing.PointF[] c = clipped[0].Points;
            var copy = new LabelShape { Label = shape.Label, GroupId = shape.GroupId, Flags = shape.Flags, Description = shape.Description, Direction = shape.Direction };
            if (type == "rectangle")
            {
                copy.ShapeType = "rectangle";
                copy.Points = new List<double[]> { new double[] { c.Min(p => p.X), c.Min(p => p.Y) }, new double[] { c.Max(p => p.X), c.Max(p => p.Y) } };
            }
            else
            {
                copy.ShapeType = "polygon";
                copy.Points = c.Select(p => new double[] { p.X, p.Y }).ToList();
            }

            result.Add(copy);
        }

        return result;
    }

    private static List<LabelShape> FilterShapes(ExportContext ctx, Visit visit, string formatName, bool warnHoles)
    {
        var kept = new List<LabelShape>();
        List<LabelShape> shapes = visit.Doc?.Shapes ?? new List<LabelShape>();
        for (int i = 0; i < shapes.Count; i++)
        {
            LabelShape shape = shapes[i];
            if (shape == null) continue;
            string description = "#" + (i + 1).ToString(CultureInfo.InvariantCulture) + " " + (shape.Label ?? string.Empty) + " (" + (shape.ShapeType ?? string.Empty) + ")";
            string? why = BoxUnsupported(shape, formatName);
            if (why == null && !ctx.Classes.Contains(shape.Label ?? string.Empty, StringComparer.Ordinal))
                why = "the label '" + (shape.Label ?? string.Empty) + "' is not in classes.txt";
            if (why != null)
            {
                SkipShape(ctx, visit.Item.Rel, description, why);
                continue;
            }

            if (warnHoles && SegLabelMeConverter.ReadHoles(shape).Length > 0)
                ctx.Result.Skips.Add(new ExportSkip(visit.Item.Rel, description, "the holes are not written (" + formatName + " has no holes)", true));
            kept.Add(shape);
        }

        return kept;
    }

    private static string DescribeShape(Visit visit, LabelShape shape)
    {
        List<LabelShape> shapes = visit.Doc?.Shapes ?? new List<LabelShape>();
        int index = shapes.IndexOf(shape);
        return "#" + (index + 1).ToString(CultureInfo.InvariantCulture) + " " + (shape.Label ?? string.Empty) + " (" + (shape.ShapeType ?? string.Empty) + ")";
    }

    // The shape types the box, polygon and mask formats can hold.
    private static string? BoxUnsupported(LabelShape shape, string formatName)
    {
        string type = shape.ShapeType ?? string.Empty;
        List<double[]> pts = shape.Points ?? new List<double[]>();
        int need;
        switch (type)
        {
            case "polygon": need = 3; break;
            case "rectangle": need = 2; break;
            case "oriented_rectangle": need = 4; break;
            case "circle":
            case "point":
            case "line":
            case "linestrip":
                return type + " shapes cannot be written to " + formatName;
            default:
                return "the shape type '" + type + "' is not supported by " + formatName;
        }

        if (pts.Count < need || pts.Any(p => p == null || p.Length < 2))
            return type + " needs at least " + need + " valid points";
        return null;
    }

    // False (and the image is counted as skipped) when OnlyAnnotated is set and no shape survived.
    private static bool HasContent(ExportContext ctx, Visit visit, List<LabelShape> kept)
    {
        if (kept.Count > 0 || !ctx.Request.OnlyAnnotated) return true;
        SkipImage(ctx, visit.Item.Rel, "no shape that this format can hold");
        return false;
    }

    private static void SkipImage(ExportContext ctx, string rel, string reason)
    {
        ctx.Result.ImagesSkipped++;
        ctx.Result.Skips.Add(new ExportSkip(rel, string.Empty, reason, false));
    }

    private static void SkipShape(ExportContext ctx, string rel, string shape, string reason)
    {
        ctx.Result.ShapesSkipped++;
        ctx.Result.Skips.Add(new ExportSkip(rel, shape, reason, false));
    }

    // Image size from the JSON, else (or when decode is true) from the decoded image.
    private static bool TryGetSize(Visit visit, bool decode, out int width, out int height)
    {
        width = visit.Doc?.ImageWidth ?? 0;
        height = visit.Doc?.ImageHeight ?? 0;
        if (!decode && width > 0 && height > 0) return true;
        try
        {
            using (Mat image = Cv2.ImDecode(File.ReadAllBytes(visit.Item.ImagePath), ImreadModes.Color))
            {
                if (image.Empty()) return false;
                width = image.Width;
                height = image.Height;
                return true;
            }
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is OpenCVException)
        {
            return false;
        }
    }

    // Outline of a rectangle (four corners), rotation (its four corners) or polygon.
    private static IEnumerable<System.Drawing.PointF> OutlineOf(LabelShape shape)
    {
        if (shape.ShapeType == "rectangle")
        {
            float x0 = (float)Math.Min(shape.Points[0][0], shape.Points[1][0]), x1 = (float)Math.Max(shape.Points[0][0], shape.Points[1][0]);
            float y0 = (float)Math.Min(shape.Points[0][1], shape.Points[1][1]), y1 = (float)Math.Max(shape.Points[0][1], shape.Points[1][1]);
            return new[] { new System.Drawing.PointF(x0, y0), new System.Drawing.PointF(x1, y0), new System.Drawing.PointF(x1, y1), new System.Drawing.PointF(x0, y1) };
        }

        IEnumerable<double[]> points = shape.ShapeType == "oriented_rectangle" ? shape.Points.Take(4) : shape.Points;
        return points.Select(p => new System.Drawing.PointF((float)p[0], (float)p[1])).ToList();
    }

    private static CvPoint Round(System.Drawing.PointF p)
    {
        return new CvPoint((int)Math.Round(p.X), (int)Math.Round(p.Y));
    }

    // ---- state ----

    private sealed class ImageItem
    {
        public ImageItem(string rel, string imagePath, string jsonPath)
        {
            Rel = rel;
            ImagePath = imagePath;
            JsonPath = jsonPath;
        }

        public string Rel { get; }

        public string ImagePath { get; }

        public string JsonPath { get; }
    }

    private sealed class Visit
    {
        public Visit(ImageItem item, LabelMeDocument? doc, byte mode)
        {
            Item = item;
            Doc = doc;
            Mode = mode;
        }

        public ImageItem Item { get; }

        public LabelMeDocument? Doc { get; }

        public byte Mode { get; }
    }

    private sealed class ExportContext
    {
        public ExportRequest Request { get; init; } = null!;

        public string Root { get; init; } = string.Empty;

        public string OutDir { get; init; } = string.Empty;

        /// <summary>The project's classes.txt (<see cref="ProjectPaths.ClassesFile"/>).</summary>
        public string ClassesFile { get; init; } = string.Empty;

        public List<string> Classes { get; init; } = new List<string>();

        public HashSet<string> UsedSplits { get; } = new HashSet<string>(StringComparer.Ordinal);

        public SegClassMap Map { get; init; } = null!;

        public SegProjectStore Store { get; init; } = null!;

        public IProgress<ExportProgress>? Progress { get; init; }

        public CancellationToken Ct { get; init; }

        public List<ImageItem> Items { get; init; } = new List<ImageItem>();

        public ExportResult Result { get; init; } = null!;

        public ExportSink? Sink { get; set; }

        public NameAllocator Alloc { get; } = new NameAllocator();
    }

    // Flat, unique, file-system safe names: the relative path with '/' turned into '_', numbered on a clash.
    private sealed class NameAllocator
    {
        private readonly HashSet<string> _used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public string Stem(string rel)
        {
            string withoutExtension = rel.Substring(0, rel.Length - Path.GetExtension(rel).Length);
            char[] invalid = Path.GetInvalidFileNameChars();
            string baseStem = new string(withoutExtension.Replace('/', '_').Select(c => invalid.Contains(c) ? '_' : c).ToArray());
            if (baseStem.Length == 0) baseStem = "image";
            string stem = baseStem;
            int n = 1;
            while (!_used.Add(stem)) stem = baseStem + "_" + (n++).ToString(CultureInfo.InvariantCulture);
            return stem;
        }
    }

    // Files and folders of the output folder before the run, so a failed or cancelled run removes only what it created.
    private sealed class Snapshot
    {
        private bool _dirExisted;
        private readonly HashSet<string> _files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public static Snapshot Take(string dir)
        {
            var snapshot = new Snapshot { _dirExisted = Directory.Exists(dir) };
            if (snapshot._dirExisted)
            {
                foreach (string file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)) snapshot._files.Add(file);
                foreach (string sub in Directory.EnumerateDirectories(dir, "*", SearchOption.AllDirectories)) snapshot._dirs.Add(sub);
            }

            return snapshot;
        }

        public List<string> NewFiles(string dir)
        {
            if (!Directory.Exists(dir)) return new List<string>();
            return Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Where(f => !_files.Contains(f)).ToList();
        }

        public void Rollback(string dir)
        {
            try
            {
                if (!Directory.Exists(dir)) return;
                foreach (string file in NewFiles(dir))
                {
                    try { File.Delete(file); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }

                foreach (string sub in Directory.EnumerateDirectories(dir, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length).ToList())
                {
                    if (_dirs.Contains(sub)) continue;
                    try { if (!Directory.EnumerateFileSystemEntries(sub).Any()) Directory.Delete(sub); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }

                if (!_dirExisted && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
