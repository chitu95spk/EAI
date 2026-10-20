using EasyLabel.Ai.Mcp;
using Newtonsoft.Json.Linq;
using OpenCvSharp;

namespace EasyLabel.Ai.Mcp.Tests;

public sealed class ShapeMeasureTests
{
    private static MeasuredShape Rect(string name, double x1, double y1, double x2, double y2) =>
        new MeasuredShape(name, "L", "rectangle", ShapeMeasure.ToRing("rectangle", new List<double[]> { new[] { x1, y1 }, new[] { x2, y2 } }));

    private static MeasuredShape Poly(string name, params double[][] points) =>
        new MeasuredShape(name, "L", "polygon", ShapeMeasure.ToRing("polygon", points));

    [Fact]
    public void Stats_OfARectangle_AreSizeAndArea()
    {
        ShapeStats s = ShapeMeasure.Stats(Rect("a", 10, 20, 40, 60));
        Assert.Equal(30, s.Width, 6);
        Assert.Equal(40, s.Height, 6);
        Assert.Equal(1200, s.Area, 6);
        Assert.Equal(10, s.X, 6);
        Assert.Equal(20, s.Y, 6);
    }

    [Fact]
    public void Compare_TwoOverlappingRectangles_GivesTheHandCheckedIou()
    {
        // 10x10 and a 10x10 shifted by 5 in x: intersection 50, union 150.
        PairStats p = ShapeMeasure.Compare(Rect("a", 0, 0, 10, 10), Rect("b", 5, 0, 15, 10));
        Assert.Equal(50, p.Intersection, 6);
        Assert.Equal(1.0 / 3.0, p.Iou, 6);
        Assert.Equal(0.5, p.CoverA, 6);
        Assert.Equal(0.5, p.CoverB, 6);
        Assert.Equal("overlap", p.Relation);
        Assert.False(p.Duplicate);
    }

    [Fact]
    public void Compare_DisjointRectangles_HaveNoIntersection()
    {
        PairStats p = ShapeMeasure.Compare(Rect("a", 0, 0, 10, 10), Rect("b", 20, 20, 30, 30));
        Assert.Equal(0, p.Intersection, 6);
        Assert.Equal("disjoint", p.Relation);
    }

    [Fact]
    public void Compare_SmallRectangleInsideBigOne_IsContainment()
    {
        PairStats p = ShapeMeasure.Compare(Rect("big", 0, 0, 100, 100), Rect("small", 10, 10, 20, 20));
        Assert.Equal(100, p.Intersection, 6);
        Assert.Equal(0.01, p.CoverA, 6);
        Assert.Equal(1.0, p.CoverB, 6);
        Assert.Equal("b_inside_a", p.Relation);
        Assert.False(p.Duplicate);
        Assert.Equal("a_inside_b", ShapeMeasure.Compare(Rect("small", 10, 10, 20, 20), Rect("big", 0, 0, 100, 100)).Relation);
    }

    [Fact]
    public void Compare_NearlyIdenticalRectangles_AreFlaggedAsDuplicates()
    {
        // 100x100 against the same box shifted by 1 px both ways: intersection 99*99, IoU 0.9606 (> 0.85).
        PairStats dup = ShapeMeasure.Compare(Rect("a", 0, 0, 100, 100), Rect("b", 1, 1, 101, 101));
        Assert.Equal(9801, dup.Intersection, 6);
        Assert.Equal(9801.0 / 10199.0, dup.Iou, 6);
        Assert.True(dup.Duplicate);

        // Shifted by 10 px in x: intersection 90*100, IoU 9000/11000 = 0.818 (< 0.85).
        PairStats notDup = ShapeMeasure.Compare(Rect("a", 0, 0, 100, 100), Rect("b", 10, 0, 110, 100));
        Assert.Equal(9000.0 / 11000.0, notDup.Iou, 6);
        Assert.False(notDup.Duplicate);
    }

    [Fact]
    public void Compare_ConvexPolygon_UsesExactClipping()
    {
        // Right triangle with legs 10 against the square (2,2)-(8,8): the part with x + y <= 10 is a triangle with legs 6, area 18.
        MeasuredShape tri = Poly("t", new[] { 0.0, 0.0 }, new[] { 10.0, 0.0 }, new[] { 0.0, 10.0 });
        Assert.Equal(50, ShapeMeasure.Area(tri.Ring), 6);
        PairStats p = ShapeMeasure.Compare(tri, Rect("r", 2, 2, 8, 8));
        Assert.Equal(18, p.Intersection, 6);
        // Ring order must not matter.
        MeasuredShape reversed = Poly("t", new[] { 0.0, 10.0 }, new[] { 10.0, 0.0 }, new[] { 0.0, 0.0 });
        Assert.Equal(18, ShapeMeasure.Compare(reversed, Rect("r", 2, 2, 8, 8)).Intersection, 6);
    }

    [Fact]
    public void Compare_ConcavePolygon_UsesTheRasterFallback()
    {
        // L shape: 10x10 square minus the 6x6 corner (4..10, 4..10); area 64.
        MeasuredShape l = Poly("l", new[] { 0.0, 0.0 }, new[] { 10.0, 0.0 }, new[] { 10.0, 4.0 }, new[] { 4.0, 4.0 }, new[] { 4.0, 10.0 }, new[] { 0.0, 10.0 });
        Assert.Equal(64, ShapeMeasure.Area(l.Ring), 6);

        // The removed corner holds no L area: the square (5,5)-(10,10) lies fully in the notch.
        Assert.Equal(0, ShapeMeasure.Compare(l, Rect("notch", 5, 5, 10, 10)).Intersection, 6);

        // Top half (0,0)-(10,5): 10x4 from the arm plus 4x1 from the stem = 44 (raster, within 1.5 percent).
        Assert.InRange(ShapeMeasure.Compare(l, Rect("top", 0, 0, 10, 5)).Intersection, 43.3, 44.7);

        // The whole bounding square: the L itself.
        Assert.InRange(ShapeMeasure.Compare(l, Rect("all", 0, 0, 10, 10)).Intersection, 63.0, 65.0);
    }

    [Fact]
    public void Compare_DegenerateShapes_DoNotThrow()
    {
        MeasuredShape line = Poly("line", new[] { 0.0, 0.0 }, new[] { 10.0, 10.0 });
        PairStats p = ShapeMeasure.Compare(line, Rect("r", 0, 0, 10, 10));
        Assert.Equal(0, p.Intersection, 6);
        Assert.Equal(0, p.Iou, 6);
        Assert.Equal(0, ShapeMeasure.Stats(Poly("empty")).Area, 6);
    }
}

public sealed class AnalysisToolsTests
{
    private static (byte[] Jpeg, string Text) Unpack(JObject result)
    {
        Assert.False((bool)result["isError"]!);
        JArray content = (JArray)result["content"]!;
        Assert.Equal("image", (string?)content[0]["type"]);
        return (Convert.FromBase64String((string)content[0]["data"]!), (string)content[1]["text"]!);
    }

    private static JObject Report(JObject result) => JObject.Parse(TestRun.ResultText(result));

    [Fact]
    public void Schemas_AreObjectsAndNameTheDocumentedArguments()
    {
        using var run = new TestRun();
        JArray tools = run.Tools.Describe();
        JToken grid = tools.First(t => (string?)t["name"] == "grid_crop")["inputSchema"]!["properties"]!;
        foreach (string name in new[] { "x", "y", "w", "h", "grid", "scale" }) Assert.NotNull(grid[name]);
        JToken overlay = tools.First(t => (string?)t["name"] == "render_overlay")["inputSchema"]!["properties"]!;
        Assert.Equal(new[] { "existing", "proposals", "both" }, overlay["include"]!["enum"]!.Select(v => (string)v!).ToArray());
        JToken measure = tools.First(t => (string?)t["name"] == "measure")["inputSchema"]!["properties"]!;
        Assert.NotNull(measure["ids"]);
        Assert.NotNull(measure["shapes"]);
        Assert.True(run.Tools.Has("grid_crop"));
        Assert.True(run.Tools.Has("render_overlay"));
        Assert.True(run.Tools.Has("measure"));
    }

    [Fact]
    public void ChooseScale_FitsDefaultsAndCaps()
    {
        Assert.Equal(1024.0 / 2000.0, AnalysisTools.ChooseScale(2000, 1000, null), 6);
        Assert.Equal(4.0, AnalysisTools.ChooseScale(50, 40, null), 6);
        Assert.Equal(AnalysisTools.OutputMaxSide / 2000.0, AnalysisTools.ChooseScale(2000, 1000, 5.0), 6);
        Assert.Equal(8.0, AnalysisTools.ChooseScale(100, 100, 50.0), 6);
        Assert.Equal(2.0, AnalysisTools.ChooseScale(100, 100, 2.0), 6);
    }

    [Fact]
    public void ChooseGridStep_PicksANiceStepAndLimitsTheLineCount()
    {
        Assert.Equal(200, AnalysisTools.ChooseGridStep(2000, 1000, null), 6);
        Assert.Equal(10, AnalysisTools.ChooseGridStep(80, 60, null), 6);
        Assert.Equal(40, AnalysisTools.ChooseGridStep(2000, 1000, 1.0), 6);
        Assert.Equal(75, AnalysisTools.ChooseGridStep(2000, 1000, 75.0), 6);
    }

    [Fact]
    public void GridCrop_WholeImage_HasTheCappedDimensionsAndALabelledGrid()
    {
        using var run = new TestRun();
        (byte[] jpeg, string text) = Unpack(run.Call("grid_crop", "{}"));
        Assert.StartsWith("grid_crop 0,0,2000,1000", text);
        Assert.Contains("ORIGINAL image pixel", text);
        using Mat decoded = Cv2.ImDecode(jpeg, ImreadModes.Color);
        Assert.Equal(1024, decoded.Width);
        Assert.Equal(512, decoded.Height);

        // Grid every 200 image px: the line at x = 200 is at output column 102. Background G is 90; a blended yellow line is far above.
        int maxGreen = 0;
        for (int col = 101; col <= 103; col++) maxGreen = Math.Max(maxGreen, decoded.At<Vec3b>(400, col).Item1);
        Assert.True(maxGreen > 115, "grid line pixel G=" + maxGreen);
        Assert.True(File.Exists(Path.Combine(run.Directory, "tools", "grid_crop-001.jpg")));
    }

    [Fact]
    public void GridCrop_SmallRegion_IsEnlargedAndClamped()
    {
        using var run = new TestRun();
        (byte[] jpeg, string text) = Unpack(run.Call("grid_crop", "{\"x\":100,\"y\":100,\"w\":50,\"h\":40}"));
        Assert.StartsWith("grid_crop 100,100,50,40", text);
        using Mat decoded = Cv2.ImDecode(jpeg, ImreadModes.Color);
        Assert.Equal(200, decoded.Width);
        Assert.Equal(160, decoded.Height);

        (_, string clamped) = Unpack(run.Call("grid_crop", "{\"x\":-50,\"y\":900,\"w\":400,\"h\":500}"));
        Assert.StartsWith("grid_crop 0,900,350,100", clamped);
    }

    [Fact]
    public void GridCrop_OutputLongerSideIsCappedAt1190AndTheTextReportsTheRealSize()
    {
        using var run = new TestRun();
        (byte[] jpeg, string text) = Unpack(run.Call("grid_crop", "{\"scale\":50}"));
        using Mat decoded = Cv2.ImDecode(jpeg, ImreadModes.Color);
        Assert.Equal(1190, decoded.Width);
        Assert.Equal(595, decoded.Height);
        Assert.Contains("Returned picture 1190x595 px", text);
        Assert.Contains("image_x = 0 + out_x / 0.595", text);
        Assert.Contains("image_y = 0 + out_y / 0.595", text);
    }

    [Theory]
    [InlineData("{\"x\":5000,\"y\":0,\"w\":10,\"h\":10}")]
    [InlineData("{\"grid\":0}")]
    [InlineData("{\"grid\":-5}")]
    [InlineData("{\"scale\":0}")]
    [InlineData("{\"scale\":\"big\"}")]
    [InlineData("{\"w\":\"wide\"}")]
    [InlineData("{\"x\":10,\"y\":10,\"w\":0,\"h\":10}")]
    public void GridCrop_InvalidArguments_AreArgumentExceptions(string args)
    {
        using var run = new TestRun();
        Assert.Throws<ArgumentException>(() => run.Call("grid_crop", args));
    }

    [Fact]
    public void RenderOverlay_OnTheSyntheticImage_ListsEveryShapeByItsId()
    {
        using var run = new TestRun();
        Report(run.Call("propose_shapes", "{\"shapes\":[{\"id\":\"a\",\"label\":\"NG\",\"shapeType\":\"rectangle\",\"points\":[[500,300],[900,600]]}]}"));

        (byte[] jpeg, string text) = Unpack(run.Call("render_overlay", "{}"));
        Assert.StartsWith("render_overlay 0,0,2000,1000", text);
        Assert.Contains("Shown: e0, p:a.", text);
        using Mat decoded = Cv2.ImDecode(jpeg, ImreadModes.Color);
        Assert.Equal(1024, decoded.Width);
        Assert.Equal(512, decoded.Height);

        // Left edge of the proposed rectangle: image x = 500 -> output column 256, orange (R 255); the background R is 150.
        int maxRed = 0;
        for (int col = 254; col <= 258; col++) maxRed = Math.Max(maxRed, decoded.At<Vec3b>(200, col).Item2);
        Assert.True(maxRed > 200, "edge pixel R=" + maxRed);
        Assert.True(File.Exists(Path.Combine(run.Directory, "tools", "render_overlay-001.jpg")));
    }

    [Fact]
    public void RenderOverlay_IncludeFiltersAndRegionHidesShapesOutsideTheCrop()
    {
        using var run = new TestRun();
        Report(run.Call("propose_shapes", "{\"shapes\":[{\"id\":\"a\",\"label\":\"NG\",\"shapeType\":\"rectangle\",\"points\":[[500,300],[900,600]]}]}"));

        (_, string existing) = Unpack(run.Call("render_overlay", "{\"include\":\"existing\"}"));
        Assert.Contains("Shown: e0.", existing);
        (_, string proposals) = Unpack(run.Call("render_overlay", "{\"include\":\"proposals\"}"));
        Assert.Contains("Shown: p:a.", proposals);

        (_, string region) = Unpack(run.Call("render_overlay", "{\"x\":1500,\"y\":500,\"w\":300,\"h\":300}"));
        Assert.Contains("Shown: none.", region);
        Assert.Contains("Outside this crop: e0, p:a.", region);
    }

    [Theory]
    [InlineData("{\"include\":\"all\"}")]
    [InlineData("{\"include\":5}")]
    [InlineData("{\"x\":9999,\"y\":0,\"w\":5,\"h\":5}")]
    public void RenderOverlay_InvalidArguments_AreArgumentExceptions(string args)
    {
        using var run = new TestRun();
        Assert.Throws<ArgumentException>(() => run.Call("render_overlay", args));
    }

    [Fact]
    public void Measure_DefaultCoversExistingAndProposed_AndFlagsTheDuplicate()
    {
        using var run = new TestRun();
        // A plain add on top of e0 is rejected by the duplicate guard, so the overlapping proposal is a replace of e0 (never blocked).
        Report(run.Call("propose_shapes", "{\"shapes\":[{\"id\":\"a\",\"target\":0,\"action\":\"replace\",\"note\":\"same\",\"label\":\"OK\",\"shapeType\":\"rectangle\",\"points\":[[1,2],[30,40]]}]}"));

        JObject report = Report(run.Call("measure", "{}"));
        Assert.Equal(2, (int)report["count"]!);
        JToken first = report["shapes"]![0]!;
        Assert.Equal("e0", (string?)first["ref"]);
        Assert.Equal(29, (double)first["width"]!, 6);
        Assert.Equal(38, (double)first["height"]!, 6);
        Assert.Equal(1102, (double)first["area"]!, 6);
        Assert.Equal("p:a", (string?)report["shapes"]![1]!["ref"]);

        JToken pair = report["overlappingPairs"]![0]!;
        Assert.Equal(1.0, (double)pair["iou"]!, 6);
        Assert.Equal("same", (string?)pair["relation"]);
        Assert.True((bool)pair["duplicate"]!);
        Assert.Equal("e0 ~ p:a", (string?)report["nearDuplicates"]![0]);
    }

    [Fact]
    public void Measure_ByIds_AcceptsNumbersAndStrings()
    {
        using var run = new TestRun();
        Report(run.Call("propose_shapes", "{\"shapes\":[{\"id\":\"a\",\"target\":0,\"action\":\"replace\",\"note\":\"same\",\"label\":\"OK\",\"shapeType\":\"rectangle\",\"points\":[[1,2],[30,40]]},{\"id\":\"b\",\"label\":\"NG\",\"shapeType\":\"rectangle\",\"points\":[[100,100],[200,200]]}]}"));

        JObject report = Report(run.Call("measure", "{\"ids\":[0,\"p:b\",\"a\",\"e0\"]}"));
        Assert.Equal(3, (int)report["count"]!);
        Assert.Equal(new[] { "e0", "p:b", "p:a" }, report["shapes"]!.Select(s => (string)s["ref"]!).ToArray());
        Assert.Equal(10000, (double)report["shapes"]![1]!["area"]!, 6);
        Assert.Single((JArray)report["overlappingPairs"]!);
    }

    [Fact]
    public void Measure_AdHocShapes_GiveTheHandCheckedIouAndContainment()
    {
        using var run = new TestRun();
        JObject overlap = Report(run.Call("measure", "{\"shapes\":[{\"points\":[[0,0],[10,10]],\"shapeType\":\"rectangle\"},{\"points\":[[5,0],[15,10]],\"shapeType\":\"rectangle\"}]}"));
        JToken pair = overlap["overlappingPairs"]![0]!;
        Assert.Equal("s0", (string?)pair["a"]);
        Assert.Equal("s1", (string?)pair["b"]);
        Assert.Equal(50, (double)pair["intersection"]!, 6);
        Assert.Equal(0.333, (double)pair["iou"]!, 3);
        Assert.False((bool)pair["duplicate"]!);

        JObject inside = Report(run.Call("measure", "{\"shapes\":[{\"points\":[[0,0],[100,100]],\"shapeType\":\"rectangle\"},{\"points\":[[10,10],[20,20]],\"shapeType\":\"rectangle\"}]}"));
        Assert.Equal("b_inside_a", (string?)inside["overlappingPairs"]![0]!["relation"]);
        Assert.Empty((JArray)inside["nearDuplicates"]!);
    }

    [Theory]
    [InlineData("{\"ids\":[7]}")]
    [InlineData("{\"ids\":[-1]}")]
    [InlineData("{\"ids\":[0.5]}")]
    [InlineData("{\"ids\":[\"nope\"]}")]
    [InlineData("{\"ids\":[\"e9\"]}")]
    [InlineData("{\"ids\":[true]}")]
    [InlineData("{\"ids\":\"0\"}")]
    [InlineData("{\"shapes\":[{\"points\":[[1,1]]}]}")]
    [InlineData("{\"shapes\":[{\"points\":[[1,1],[2,2]],\"shapeType\":\"circle\"}]}")]
    [InlineData("{\"shapes\":[{\"points\":[[1,\"x\"],[2,2],[3,3]]}]}")]
    [InlineData("{\"shapes\":[5]}")]
    public void Measure_InvalidArguments_AreArgumentExceptions(string args)
    {
        using var run = new TestRun();
        Assert.Throws<ArgumentException>(() => run.Call("measure", args));
    }

    [Fact]
    public void Measure_RemovalProposalHasNoOutline()
    {
        using var run = new TestRun();
        Report(run.Call("propose_shapes", "{\"shapes\":[{\"id\":\"r\",\"target\":0,\"action\":\"remove\",\"note\":\"duplicate\"}]}"));
        Assert.Throws<ArgumentException>(() => run.Call("measure", "{\"ids\":[\"r\"]}"));
        Assert.Equal(1, (int)Report(run.Call("measure", "{}"))["count"]!);
    }
}
