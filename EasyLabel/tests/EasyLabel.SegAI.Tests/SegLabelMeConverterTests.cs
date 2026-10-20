using System.Drawing;
using EasyLabel.Core.Datasets;
using EasyLabel.Core.Model;
using EasyLabel.SegAI.Convert;
using EasyLabel.SegAI.Data;
using EasyLabel.SegAI.Project;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace EasyLabel.SegAI.Tests;

public sealed class SegLabelMeConverterTests
{
    private static List<SegAnnotation> Regions()
    {
        return new List<SegAnnotation>
        {
            SegLabelRasterTests.Square(1, 10, 10, 60, 60, SegLabelRasterTests.Box(25, 25, 35, 35)),
            SegLabelRasterTests.Square(1, 70, 70, 90, 90),
            SegLabelRasterTests.Square(2, 100, 10, 140, 50)
        };
    }

    private static string Scratch(string name)
    {
        string dir = Path.Combine(AppContext.BaseDirectory, "scratch", name + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void Sidecar_with_matching_hash_keeps_holes_and_is_not_stale()
    {
        var map = new SegClassMap();
        var regions = Regions();
        List<LabelShape> shapes = SegLabelMeConverter.ToLabelMe(regions, map);
        Assert.Equal(3, shapes.Count);
        Assert.All(shapes, s => Assert.Equal("polygon", s.ShapeType));
        Assert.Equal(new int?[] { 0, 1, 2 }, shapes.Select(s => s.GroupId).ToArray());
        Assert.Equal("NG", shapes[0].Label);
        Assert.Equal("OK", shapes[2].Label);

        var sidecar = new SegSidecar { ShapesHash = SegLabelMeConverter.Hash(shapes), Regions = regions };
        var back = SegLabelMeConverter.FromLabelMe(shapes, sidecar, map, out bool stale);

        Assert.False(stale);
        Assert.Equal(3, back.Count);
        Assert.Single(back[0].Holes);
        Assert.Equal(2, back[2].Label);
    }

    [Fact]
    public void Editing_a_point_makes_result_stale_and_drops_holes()
    {
        var map = new SegClassMap();
        var regions = Regions();
        List<LabelShape> shapes = SegLabelMeConverter.ToLabelMe(regions, map);
        var sidecar = new SegSidecar { ShapesHash = SegLabelMeConverter.Hash(shapes), Regions = regions };

        shapes[0].Points[0][0] += 1;
        var back = SegLabelMeConverter.FromLabelMe(shapes, sidecar, map, out bool stale);

        Assert.True(stale);
        Assert.Equal(3, back.Count);
        Assert.Empty(back[0].Holes);
        Assert.Equal(11f, back[0].Points[0].X);
    }

    [Fact]
    public void Unknown_class_is_skipped_and_counted()
    {
        var map = new SegClassMap();
        var shapes = SegLabelMeConverter.ToLabelMe(Regions(), map);
        shapes[1].Label = "Scratch";
        var back = SegLabelMeConverter.FromLabelMe(shapes, null, map, out bool stale, out int skipped);

        Assert.True(stale);
        Assert.Equal(1, skipped);
        Assert.Equal(2, back.Count);
    }

    [Fact]
    public void Rect_and_Rot_kinds_map_to_shape_types_and_import_adapter_reads_them()
    {
        var map = new SegClassMap();
        var rect = SegLabelRasterTests.Square(1, 5, 6, 25, 36);
        rect.Kind = "Rect";
        var rot = new SegAnnotation
        {
            Kind = "Rot",
            Label = 2,
            Points = new[] { new PointF(20, 10), new PointF(40, 20), new PointF(30, 40), new PointF(10, 30) }
        };
        var shapes = SegLabelMeConverter.ToLabelMe(new[] { rect, rot }, map);

        Assert.Equal("rectangle", shapes[0].ShapeType);
        Assert.Equal(2, shapes[0].Points.Count);
        Assert.Equal("oriented_rectangle", shapes[1].ShapeType);
        Assert.Equal(4, shapes[1].Points.Count);
        Assert.NotNull(shapes[1].Direction);

        var root = new JObject { ["version"] = "5.0.0", ["shapes"] = JArray.Parse(JsonConvert.SerializeObject(shapes)) };
        DatasetImportPreview preview = new LabelMeImportAdapter().Import(root);
        Assert.True(preview.Validation.IsValid);
        Assert.Equal(CanonicalAnnotationKind.Rectangle, preview.Document.Annotations[0].Kind);
        Assert.Equal(CanonicalAnnotationKind.OrientedRectangle, preview.Document.Annotations[1].Kind);

        var back = SegLabelMeConverter.FromLabelMe(shapes, null, map, out _);
        Assert.Equal("Rect", back[0].Kind);
        Assert.Equal(4, back[0].Points.Length);
        Assert.Equal("Rot", back[1].Kind);
        Assert.Equal(2, back[1].Label);
    }

    [Fact]
    public void Hash_is_stable_and_changes_with_a_point()
    {
        var shapes = SegLabelMeConverter.ToLabelMe(Regions(), new SegClassMap());
        string a = SegLabelMeConverter.Hash(shapes);
        Assert.Equal(a, SegLabelMeConverter.Hash(shapes));
        Assert.Equal(64, a.Length);
        shapes[2].Points[1][1] += 0.5;
        Assert.NotEqual(a, SegLabelMeConverter.Hash(shapes));
    }

    [Fact]
    public void Classes_file_append_preserves_existing_lines()
    {
        string dir = Scratch("classes");
        string path = Path.Combine(dir, "classes.txt");
        File.WriteAllText(path, "Zebra\r\nOK\r\nScratch");

        IReadOnlyList<string> ignored = new SegClassMap().EnsureClassesFile(path);

        string[] lines = File.ReadAllLines(path);
        Assert.Equal(new[] { "Zebra", "OK", "Scratch", "NG" }, lines);
        Assert.Equal(new[] { "Zebra", "Scratch" }, ignored.ToArray());
        string before = File.ReadAllText(path);
        new SegClassMap().EnsureClassesFile(path);
        Assert.Equal(before, File.ReadAllText(path));
    }

    [Fact]
    public void Class_map_override_changes_names()
    {
        var map = new SegClassMap(new Dictionary<string, byte> { ["Defect"] = 1, ["Good"] = 2 });
        Assert.Equal((byte)1, map.LabelOf("defect"));
        Assert.Equal("Good", map.NameOf(2));
        Assert.Null(map.LabelOf("NG"));
    }

    [Fact]
    public void Store_rejects_traversal_and_rooted_paths()
    {
        var store = new SegProjectStore(Scratch("trav"));
        Assert.Throws<ArgumentException>(() => store.SidecarPath("..\\evil.png"));
        Assert.Throws<ArgumentException>(() => store.SidecarPath("a/../../evil.png"));
        Assert.Throws<ArgumentException>(() => store.LabelsPath("/abs/evil.png"));
        Assert.Throws<ArgumentException>(() => store.LabelsPath("C:\\evil.png"));
        Assert.EndsWith(Path.Combine(".easyai", "cache", "sub", "a.seg.json"), store.SidecarPath("sub\\a.png"));
        Assert.EndsWith(Path.Combine(".easyai", "cache", "sub", "a.labels.png"), store.LabelsPath("sub/a.png"));
    }

    [Fact]
    public void Store_write_then_read_round_trips_and_corrupt_sidecar_is_null()
    {
        string root = Scratch("store");
        var store = new SegProjectStore(root);
        var map = new SegClassMap();
        var regions = Regions();
        var shapes = SegLabelMeConverter.ToLabelMe(regions, map);

        Assert.Null(store.ReadSidecar("img/a.png"));
        store.WriteSample("img\\a.png", 160, 120, regions, 2, "key1", shapes);

        Assert.True(File.Exists(Path.Combine(root, ".easyai", "project.json")));
        Assert.True(Directory.Exists(Path.Combine(root, ".easyai", "models")));
        Assert.True(Directory.Exists(Path.Combine(root, ".easyai", "runs")));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(root, ".easyai", "cache"), "*.tmp", SearchOption.AllDirectories));
        Assert.Equal(new[] { ".easyai" }, Directory.GetFileSystemEntries(root).Select(Path.GetFileName).ToArray());

        SegSidecar? sidecar = store.ReadSidecar("img/a.png");
        Assert.NotNull(sidecar);
        Assert.Equal("img/a.png", sidecar!.ImageRel);
        Assert.Equal("key1", sidecar.ImageKey);
        Assert.Equal((byte)2, sidecar.Mode);
        Assert.Equal(SegLabelMeConverter.Hash(shapes), sidecar.ShapesHash);
        Assert.Single(sidecar.Regions[0].Holes);
        Assert.Equal(new PointF(25, 25), sidecar.Regions[0].Holes[0][0]);
        var back = SegLabelMeConverter.FromLabelMe(shapes, sidecar, map, out bool stale);
        Assert.False(stale);
        Assert.Single(back[0].Holes);

        using (var labels = store.ReadLabels("img/a.png"))
        {
            Assert.NotNull(labels);
            Assert.Equal(160, labels!.Width);
            Assert.Equal(120, labels.Height);
            Assert.Equal((byte)1, labels.At<byte>(15, 15));
            Assert.Equal((byte)0, labels.At<byte>(30, 30));
            Assert.Equal((byte)2, labels.At<byte>(30, 120));
        }

        File.WriteAllText(store.SidecarPath("img/a.png"), "{ not json");
        Assert.Null(store.ReadSidecar("img/a.png"));
        Assert.Null(store.ReadLabels("img/missing.png"));
    }

    [Fact]
    public void Store_loads_class_map_from_advanced_json()
    {
        string root = Scratch("adv");
        Directory.CreateDirectory(Path.Combine(root, ".easyai"));
        File.WriteAllText(Path.Combine(root, ".easyai", "advanced.json"), "{\"segClassMap\":{\"Defect\":1,\"Good\":2,\"Bad\":7}}");
        var map = new SegProjectStore(root).LoadClassMap();
        Assert.Equal((byte)1, map.LabelOf("Defect"));
        Assert.Null(map.LabelOf("Bad"));
    }
}
