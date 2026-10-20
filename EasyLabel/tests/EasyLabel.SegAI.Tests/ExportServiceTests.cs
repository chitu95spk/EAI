using System.Globalization;
using System.IO.Compression;
using System.Xml.Linq;
using EasyLabel.Core.Io;
using EasyLabel.Core.Model;
using EasyLabel.SegAI.Convert;
using EasyLabel.SegAI.Data;
using EasyLabel.SegAI.Export;
using Newtonsoft.Json.Linq;
using OpenCvSharp;

namespace EasyLabel.SegAI.Tests;

public sealed class ExportServiceTests
{
    private const int W = 100, H = 80;

    private static string Scratch(string name)
    {
        string dir = Path.Combine(AppContext.BaseDirectory, "scratch", "export", name + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void WriteImage(string path, int seed)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var image = new Mat(H, W, MatType.CV_8UC3, new Scalar(seed * 40, 100, 200 - seed * 30));
        Cv2.ImEncode(".png", image, out byte[] bytes);
        File.WriteAllBytes(path, bytes);
    }

    private static LabelShape Rect(string label, double x0, double y0, double x1, double y1)
    {
        return new LabelShape { Label = label, ShapeType = "rectangle", Points = { new[] { x0, y0 }, new[] { x1, y1 } } };
    }

    private static LabelShape Poly(string label, params double[][] points)
    {
        var shape = new LabelShape { Label = label, ShapeType = "polygon" };
        shape.Points.AddRange(points);
        return shape;
    }

    private static LabelShape Circle(string label, double cx, double cy, double ex, double ey)
    {
        return new LabelShape { Label = label, ShapeType = "circle", Points = { new[] { cx, cy }, new[] { ex, ey } } };
    }

    private static void WriteJson(string imagePath, byte mode, params LabelShape[] shapes)
    {
        var doc = new LabelMeDocument { ImagePath = Path.GetFileName(imagePath), ImageWidth = W, ImageHeight = H };
        doc.Shapes.AddRange(shapes);
        ImageModeFlags.Apply(doc.Flags, mode);
        new LabelMeIO().Write(doc, Path.ChangeExtension(imagePath, ".json"));
    }

    /// <summary>
    /// a.png (train): rect NG, polygon OK, circle NG, rect "Scratch". sub/b.png (val): polygon NG with a hole, rect OK.
    /// c.png: no JSON. classes.txt = NG, OK.
    /// </summary>
    private static string BuildProject()
    {
        string root = Scratch("proj");
        File.WriteAllText(Path.Combine(root, "classes.txt"), "NG\nOK\n");
        string a = Path.Combine(root, "a.png");
        WriteImage(a, 0);
        WriteJson(
            a,
            ImageModeFlags.Train,
            Rect("NG", 10, 20, 50, 60),
            Poly("OK", new[] { 60.0, 10 }, new[] { 90.0, 10 }, new[] { 75.0, 40 }),
            Circle("NG", 20, 20, 25, 20),
            Rect("Scratch", 1, 1, 5, 5));

        string b = Path.Combine(root, "sub", "b.png");
        WriteImage(b, 1);
        LabelShape withHole = Poly("NG", new[] { 10.0, 10 }, new[] { 70.0, 10 }, new[] { 70.0, 60 }, new[] { 10.0, 60 });
        SegLabelMeConverter.WriteHoles(
            withHole,
            new[] { new[] { new System.Drawing.PointF(30, 30), new System.Drawing.PointF(50, 30), new System.Drawing.PointF(50, 45), new System.Drawing.PointF(30, 45) } });
        WriteJson(b, ImageModeFlags.Val, withHole, Rect("OK", 72, 5, 95, 25));

        WriteImage(Path.Combine(root, "c.png"), 2);
        return root;
    }

    private static string OutDir()
    {
        return Path.Combine(Scratch("out"), "export");
    }

    private static ExportResult Run(string root, ExportFormat format, ExportSet set, string outDir, bool zip = false, bool onlyAnnotated = true)
    {
        return new ExportService().Run(
            new ExportRequest { ProjectRoot = root, Format = format, Set = set, OutputDirectory = outDir, WriteZip = zip, OnlyAnnotated = onlyAnnotated },
            null,
            CancellationToken.None);
    }

    private static string[] Lines(string path)
    {
        return File.ReadAllText(path).Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
    }

    private static double[] Numbers(string line)
    {
        return line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(t => double.Parse(t, CultureInfo.InvariantCulture)).ToArray();
    }

    private static void AssertClose(double expected, double actual)
    {
        Assert.True(Math.Abs(expected - actual) < 1e-9, "expected " + expected + " but was " + actual);
    }

    private static string BuildCutProject()
    {
        string root = Scratch("cut");
        File.WriteAllText(Path.Combine(root, "classes.txt"), "NG\nOK\n");
        string a = Path.Combine(root, "a.png");
        WriteImage(a, 0);
        WriteJson(
            a,
            ImageModeFlags.Train,
            Rect("NG", 10, 10, 50, 50),          // inside
            Rect("NG", -20, 10, 30, 50),         // partly outside: visible x 0..30
            Rect("OK", -90, 10, 5, 50),          // mostly outside: visible x 0..5
            Rect("OK", -50, 10, -10, 50),        // fully outside
            new LabelShape
            {
                Label = "OK", ShapeType = "oriented_rectangle",
                Points = { new[] { 80.0, 60 }, new[] { 120.0, 60 }, new[] { 120.0, 70 }, new[] { 80.0, 70 } },
            });
        return root;
    }

    [Fact]
    public void YoloDetect_clips_boxes_cut_by_the_border_and_skips_the_ones_outside()
    {
        ExportResult result = Run(BuildCutProject(), ExportFormat.YoloDetect, ExportSet.All, out string output);

        string[] lines = Lines(Path.Combine(output, "labels", "train", "a.txt"));
        Assert.Equal(4, lines.Length);
        Assert.All(lines.SelectMany(Numbers).Skip(0), v => Assert.InRange(v, 0.0, 1.0));
        double[] inside = Numbers(lines[0]);
        AssertClose(0.3, inside[1]);                       // unchanged by the clipping
        double[] partly = Numbers(lines[1]);
        AssertClose(0.15, partly[1]);                      // x 0..30 -> centre 15 / 100
        AssertClose(0.3, partly[3]);                       // width 30 / 100
        double[] mostly = Numbers(lines[2]);
        AssertClose(0.025, mostly[1]);                     // x 0..5
        Assert.Contains(result.Skips, s => !s.IsWarning && s.Reason.Contains("outside the image"));
        Assert.Equal(1, result.ShapesSkipped);
    }

    [Fact]
    public void YoloSegment_writes_the_visible_polygon_of_an_oriented_box_cut_by_the_border()
    {
        Run(BuildCutProject(), ExportFormat.YoloSegment, ExportSet.All, out string output);

        string[] lines = Lines(Path.Combine(output, "labels", "train", "a.txt"));
        double[] last = Numbers(lines[^1]);
        Assert.All(last.Skip(1), v => Assert.InRange(v, 0.0, 1.0));
        Assert.Equal(1 + 8, last.Length); // class + 4 clipped corners
        AssertClose(1.0, last.Skip(1).Where((v, i) => i % 2 == 0).Max()); // reaches the right border
    }

    [Fact]
    public void Voc_and_Coco_stay_inside_the_image_for_boxes_cut_by_the_border()
    {
        string root = BuildCutProject();
        Run(root, ExportFormat.PascalVoc, ExportSet.All, out string vocOut);
        XDocument xml = XDocument.Load(Directory.GetFiles(Path.Combine(vocOut, "annotations"), "*.xml").Single());
        foreach (XElement box in xml.Descendants("bndbox"))
        {
            Assert.InRange(double.Parse(box.Element("xmin")!.Value, CultureInfo.InvariantCulture), 0, W);
            Assert.InRange(double.Parse(box.Element("xmax")!.Value, CultureInfo.InvariantCulture), 0, W);
        }

        Assert.Equal(4, xml.Descendants("object").Count());

        Run(root, ExportFormat.CocoInstances, ExportSet.All, out string cocoOut);
        string cocoFile = Directory.GetFiles(cocoOut, "*.json", SearchOption.AllDirectories).First(f => File.ReadAllText(f).Contains("\"annotations\""));
        JArray annotations = (JArray)JObject.Parse(File.ReadAllText(cocoFile))["annotations"]!;
        Assert.Equal(4, annotations.Count);
        foreach (JToken a in annotations)
        {
            double[] bbox = a["bbox"]!.Select(v => (double)v).ToArray();
            Assert.True(bbox[0] >= 0 && bbox[1] >= 0 && bbox[0] + bbox[2] <= W + 1e-6 && bbox[1] + bbox[3] <= H + 1e-6);
        }
    }

    private static ExportResult Run(string root, ExportFormat format, ExportSet set, out string output)
    {
        output = OutDir();
        return Run(root, format, set, output);
    }

    [Fact]
    public void YoloDetect_writes_split_files_normalised_lines_and_reports_skipped_shapes()
    {
        string root = BuildProject();
        string output = OutDir();
        ExportResult result = Run(root, ExportFormat.YoloDetect, ExportSet.All, output);

        Assert.Equal(2, result.ImagesExported);
        Assert.Equal(1, result.ImagesSkipped);
        Assert.Equal(4, result.ShapesExported);
        Assert.Equal(2, result.ShapesSkipped);
        Assert.True(File.Exists(Path.Combine(output, "images", "train", "a.png")));
        Assert.True(File.Exists(Path.Combine(output, "images", "val", "sub_b.png")));
        Assert.True(File.Exists(Path.Combine(output, "data.yaml")));

        string[] a = Lines(Path.Combine(output, "labels", "train", "a.txt"));
        Assert.Equal(2, a.Length);
        double[] box = Numbers(a[0]);
        Assert.Equal(5, box.Length);
        Assert.Equal(0, box[0]);
        AssertClose(0.3, box[1]);
        AssertClose(0.5, box[2]);
        AssertClose(0.4, box[3]);
        AssertClose(0.5, box[4]);
        double[] ok = Numbers(a[1]);
        Assert.Equal(1, ok[0]);
        AssertClose(0.75, ok[1]);
        AssertClose(0.3125, ok[2]);
        Assert.Equal(2, Lines(Path.Combine(output, "labels", "val", "sub_b.txt")).Length);

        Assert.Contains(result.Skips, s => s.Image == "a.png" && s.Reason.Contains("circle"));
        Assert.Contains(result.Skips, s => s.Image == "a.png" && s.Reason.Contains("Scratch") && s.Reason.Contains("classes.txt"));
        Assert.Contains(result.Skips, s => s.Image == "c.png" && s.Shape.Length == 0);
        Assert.All(result.FilesWritten, f => Assert.StartsWith(output, f));
    }

    [Fact]
    public void YoloSegment_writes_polygon_lines_and_warns_about_dropped_holes()
    {
        string root = BuildProject();
        string output = OutDir();
        ExportResult result = Run(root, ExportFormat.YoloSegment, ExportSet.All, output);

        string[] a = Lines(Path.Combine(output, "labels", "train", "a.txt"));
        Assert.Equal(9, Numbers(a[0]).Length);
        Assert.Equal(7, Numbers(a[1]).Length);
        Assert.Contains(result.Skips, s => s.IsWarning && s.Image == "sub/b.png" && s.Reason.Contains("holes"));
        Assert.Equal(2, result.ShapesSkipped);
    }

    [Fact]
    public void Set_filter_uses_the_train_and_val_flags()
    {
        string root = BuildProject();

        string trainOut = OutDir();
        ExportResult train = Run(root, ExportFormat.YoloDetect, ExportSet.Train, trainOut);
        Assert.Equal(1, train.ImagesExported);
        Assert.True(File.Exists(Path.Combine(trainOut, "images", "train", "a.png")));
        Assert.False(Directory.Exists(Path.Combine(trainOut, "images", "val")));

        string valOut = OutDir();
        ExportResult val = Run(root, ExportFormat.YoloDetect, ExportSet.Val, valOut);
        Assert.Equal(1, val.ImagesExported);
        Assert.True(File.Exists(Path.Combine(valOut, "images", "val", "sub_b.png")));
        Assert.False(Directory.Exists(Path.Combine(valOut, "images", "train")));
    }

    [Fact]
    public void Not_only_annotated_also_exports_images_without_shapes()
    {
        string root = BuildProject();
        string output = OutDir();
        ExportResult result = Run(root, ExportFormat.YoloDetect, ExportSet.All, output, onlyAnnotated: false);
        Assert.Equal(3, result.ImagesExported);
        Assert.Equal(0, result.ImagesSkipped);
        Assert.Empty(Lines(Path.Combine(output, "labels", "train", "c.txt")));
    }

    [Fact]
    public void Coco_has_schema_fields_and_counts()
    {
        string root = BuildProject();
        string output = OutDir();
        ExportResult result = Run(root, ExportFormat.CocoInstances, ExportSet.All, output);

        JObject coco = JObject.Parse(File.ReadAllText(Path.Combine(output, "annotations.coco.json")));
        Assert.Equal(2, ((JArray)coco["images"]!).Count);
        Assert.Equal(4, ((JArray)coco["annotations"]!).Count);
        Assert.Equal(2, ((JArray)coco["categories"]!).Count);
        JToken image = coco["images"]![0]!;
        Assert.Equal("a.png", (string?)image["file_name"]);
        Assert.Equal(W, (int)image["width"]!);
        Assert.Equal(H, (int)image["height"]!);
        JToken annotation = coco["annotations"]![0]!;
        Assert.Equal(4, ((JArray)annotation["bbox"]!).Count);
        Assert.Equal(1, (int)annotation["category_id"]!);
        Assert.NotNull(annotation["segmentation"]);
        Assert.True(File.Exists(Path.Combine(output, "images", "a.png")));
        Assert.True(File.Exists(Path.Combine(output, "images", "sub_b.png")));
        Assert.Equal(2, result.ShapesSkipped);
        Assert.Contains(result.Skips, s => s.IsWarning && s.Reason.Contains("holes"));
    }

    [Fact]
    public void PascalVoc_xml_parses_with_size_and_objects()
    {
        string root = BuildProject();
        string output = OutDir();
        Run(root, ExportFormat.PascalVoc, ExportSet.All, output);

        XDocument xml = XDocument.Load(Path.Combine(output, "annotations", "a.xml"));
        Assert.Equal("annotation", xml.Root!.Name.LocalName);
        Assert.Equal("a.png", xml.Root.Element("filename")!.Value);
        Assert.Equal("100", xml.Root.Element("size")!.Element("width")!.Value);
        XElement[] objects = xml.Root.Elements("object").ToArray();
        Assert.Equal(2, objects.Length);
        Assert.Equal("NG", objects[0].Element("name")!.Value);
        Assert.Equal(10, double.Parse(objects[0].Element("bndbox")!.Element("xmin")!.Value, CultureInfo.InvariantCulture));
        Assert.True(File.Exists(Path.Combine(output, "images", "a.png")));
    }

    [Fact]
    public void MaskPng_has_image_size_and_class_index_values_with_holes_cut_out()
    {
        string root = BuildProject();
        string output = OutDir();
        ExportResult result = Run(root, ExportFormat.MaskPng, ExportSet.All, output);

        using Mat a = Cv2.ImDecode(File.ReadAllBytes(Path.Combine(output, "masks", "a.png")), ImreadModes.Grayscale);
        Assert.Equal(W, a.Width);
        Assert.Equal(H, a.Height);
        Assert.Equal(1, a.At<byte>(40, 30));
        Assert.Equal(2, a.At<byte>(20, 75));
        Assert.Equal(0, a.At<byte>(2, 2));
        Assert.Equal(0, a.At<byte>(3, 3));

        using Mat b = Cv2.ImDecode(File.ReadAllBytes(Path.Combine(output, "masks", "sub_b.png")), ImreadModes.Grayscale);
        Assert.Equal(1, b.At<byte>(20, 20));
        Assert.Equal(0, b.At<byte>(38, 40));
        Assert.Equal(2, b.At<byte>(15, 80));
        Assert.Contains(result.Skips, s => s.Reason.Contains("circle"));
        Assert.Equal("NG\nOK", string.Join("\n", Lines(Path.Combine(output, "classes.txt"))));
    }

    [Fact]
    public void Csv_has_one_row_per_image_with_counts()
    {
        string root = BuildProject();
        string output = OutDir();
        Run(root, ExportFormat.Csv, ExportSet.All, output);
        string[] rows = Lines(Path.Combine(output, "labels.csv"));
        Assert.Equal(3, rows.Length);
        string[] header = rows[0].Split(',');
        string[] a = rows[1].Split(',');
        Assert.Equal("a.png", a[Array.IndexOf(header, "image")]);
        Assert.Equal("train", a[Array.IndexOf(header, "mode")]);
        Assert.Equal("100", a[Array.IndexOf(header, "width")]);
        Assert.Equal("80", a[Array.IndexOf(header, "height")]);
        Assert.Equal("4", a[Array.IndexOf(header, "shapes")]);
        Assert.Equal("2", a[Array.IndexOf(header, "regions")]);
        Assert.Equal("2", a[Array.IndexOf(header, "label:NG")]);
        Assert.Equal("1", a[Array.IndexOf(header, "label:OK")]);
        Assert.Equal("1", a[Array.IndexOf(header, "other")]);
        Assert.Equal("val", rows[2].Split(',')[Array.IndexOf(header, "mode")]);

        string all = OutDir();
        Run(root, ExportFormat.Csv, ExportSet.All, all, onlyAnnotated: false);
        Assert.Equal(4, Lines(Path.Combine(all, "labels.csv")).Length);
    }

    [Fact]
    public void LabelMeJson_copies_images_and_json_with_the_flags()
    {
        string root = BuildProject();
        string output = OutDir();
        ExportResult result = Run(root, ExportFormat.LabelMeJson, ExportSet.All, output);
        Assert.Equal(2, result.ImagesExported);
        Assert.True(File.Exists(Path.Combine(output, "a.png")));
        Assert.True(File.Exists(Path.Combine(output, "sub", "b.png")));
        Assert.False(File.Exists(Path.Combine(output, "c.png")));
        LabelMeDocument b = new LabelMeIO().Read(Path.Combine(output, "sub", "b.json"));
        Assert.Equal(ImageModeFlags.Val, ImageModeFlags.Read(b.Flags));
        Assert.Equal(2, b.Shapes.Count);
        Assert.True(File.Exists(Path.Combine(output, "classes.txt")));
    }

    [Fact]
    public void Zip_option_packs_everything_into_one_file()
    {
        string root = BuildProject();
        string output = OutDir();
        ExportResult result = Run(root, ExportFormat.YoloDetect, ExportSet.All, output, zip: true);
        string zipPath = Path.Combine(output, "yolo-detect.zip");
        Assert.Equal(zipPath, result.OutputPath);
        Assert.Equal(new[] { zipPath }, result.FilesWritten.ToArray());
        Assert.Single(Directory.GetFileSystemEntries(output));
        using ZipArchive zip = ZipFile.OpenRead(zipPath);
        string[] names = zip.Entries.Select(e => e.FullName).ToArray();
        Assert.Contains("images/train/a.png", names);
        Assert.Contains("labels/val/sub_b.txt", names);
        Assert.Contains("data.yaml", names);
    }

    [Fact]
    public void EasyVisionDataset_zip_is_read_back_and_imported_with_equal_region_counts()
    {
        string root = BuildProject();
        string output = OutDir();
        ExportResult result = Run(root, ExportFormat.EasyVisionDataset, ExportSet.All, output, zip: true);
        string zipPath = Path.Combine(output, "easyvision-dataset.zip");
        Assert.Equal(zipPath, result.OutputPath);
        Assert.Equal(2, result.ImagesExported);
        Assert.Contains(result.Skips, s => s.Image == "a.png" && s.Reason.Contains("not mapped"));
        Assert.Contains(result.Skips, s => s.Image == "a.png" && s.Reason.Contains("circle"));

        IReadOnlyList<EvDatasetEntry> entries = EasyVisionDataset.ReadZip(zipPath);
        Assert.Equal(2, entries.Count);
        Assert.Equal("a.png", entries[0].Name);
        Assert.Equal(1, entries[0].Mode);
        Assert.Equal(2, entries[0].Regions.Count);
        Assert.Equal(2, entries[1].Mode);
        Assert.Single(entries[1].Regions[0].Holes);

        string fresh = Scratch("fresh");
        using SegAiSession session = SegAiSession.Create(fresh);
        SegAiImportReport report = session.ImportEasyVisionDataset(zipPath);
        Assert.Equal(2, report.Imported);
        Assert.Equal(0, report.Skipped);
        Assert.Equal(new[] { 2, 2 }, report.ImportedImages.Select(rel => session.GetAnnotations(rel).Count).ToArray());
    }

    [Fact]
    public void EasyVisionDataset_folder_is_readable_by_the_folder_reader()
    {
        string root = BuildProject();
        string output = OutDir();
        Run(root, ExportFormat.EasyVisionDataset, ExportSet.Val, output);
        IReadOnlyList<EvDatasetEntry> entries = EasyVisionDataset.ReadFolder(output);
        Assert.Single(entries);
        Assert.Equal(2, entries[0].Regions.Count);
        Assert.Equal(2, entries[0].Mode);
    }

    [Fact]
    public void Non_empty_output_is_refused_and_left_untouched()
    {
        string root = BuildProject();
        string output = OutDir();
        Directory.CreateDirectory(output);
        string existing = Path.Combine(output, "keep.txt");
        File.WriteAllText(existing, "keep");
        Assert.Throws<ExportException>(() => Run(root, ExportFormat.YoloDetect, ExportSet.All, output));
        Assert.Equal("keep", File.ReadAllText(existing));
        Assert.Single(Directory.GetFileSystemEntries(output));
    }

    [Fact]
    public void Output_inside_easyai_or_the_project_itself_is_refused()
    {
        string root = BuildProject();
        Assert.Throws<ExportException>(() => Run(root, ExportFormat.Csv, ExportSet.All, Path.Combine(root, ".easyai", "export")));
        Assert.Throws<ExportException>(() => Run(root, ExportFormat.Csv, ExportSet.All, root));
        Assert.Throws<ExportException>(() => Run(root, ExportFormat.Csv, ExportSet.All, Path.GetDirectoryName(root)!));
        Assert.False(Directory.Exists(Path.Combine(root, ".easyai", "export")));
    }

    [Fact]
    public void Output_inside_the_project_is_refused_so_copies_are_never_scanned_as_images()
    {
        string root = BuildProject();
        Assert.Throws<ExportException>(() => Run(root, ExportFormat.YoloDetect, ExportSet.All, Path.Combine(root, "exported")));
        Assert.False(Directory.Exists(Path.Combine(root, "exported")));
    }

    [Fact]
    public void Yolo_data_yaml_omits_a_split_without_images()
    {
        string root = BuildProject();
        string output = Path.Combine(Path.GetDirectoryName(root)!, Path.GetFileName(root) + "_trainonly");
        Run(root, ExportFormat.YoloDetect, ExportSet.Train, output);
        string yaml = File.ReadAllText(Path.Combine(output, "data.yaml"));
        Assert.Contains("train:", yaml);
        Assert.DoesNotContain("val:", yaml);
    }

    [Fact]
    public void Cancelled_export_throws_and_removes_what_it_wrote()
    {
        string root = BuildProject();
        string output = OutDir();
        using var cts = new CancellationTokenSource();
        var progress = new CancelAfterFirstImage(cts);
        Assert.Throws<OperationCanceledException>(() => new ExportService().Run(
            new ExportRequest { ProjectRoot = root, Format = ExportFormat.YoloDetect, Set = ExportSet.All, OutputDirectory = output },
            progress,
            cts.Token));
        Assert.False(Directory.Exists(output));

        string zipOutput = OutDir();
        using var cts2 = new CancellationTokenSource();
        Assert.Throws<OperationCanceledException>(() => new ExportService().Run(
            new ExportRequest { ProjectRoot = root, Format = ExportFormat.EasyVisionDataset, Set = ExportSet.All, OutputDirectory = zipOutput, WriteZip = true },
            new CancelAfterFirstImage(cts2),
            cts2.Token));
        Assert.False(Directory.Exists(zipOutput));
    }

    [Fact]
    public void Missing_classes_txt_refuses_class_based_formats()
    {
        string root = BuildProject();
        File.Delete(Path.Combine(root, "classes.txt"));
        string output = OutDir();
        Assert.Throws<ExportException>(() => Run(root, ExportFormat.YoloDetect, ExportSet.All, output));
        Assert.False(Directory.Exists(output));
        // The EasyVision dataset needs no classes.txt (it uses the NG / OK map).
        Assert.Equal(2, Run(root, ExportFormat.EasyVisionDataset, ExportSet.All, output, zip: true).ImagesExported);
    }

    private sealed class CancelAfterFirstImage : IProgress<ExportProgress>
    {
        private readonly CancellationTokenSource _cts;

        public CancelAfterFirstImage(CancellationTokenSource cts)
        {
            _cts = cts;
        }

        public void Report(ExportProgress value)
        {
            if (value.Done >= 1) _cts.Cancel();
        }
    }
}
