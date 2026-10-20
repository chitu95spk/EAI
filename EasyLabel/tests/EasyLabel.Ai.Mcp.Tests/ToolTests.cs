using EasyLabel.Ai.Mcp;
using Newtonsoft.Json.Linq;
using OpenCvSharp;

namespace EasyLabel.Ai.Mcp.Tests;

public sealed class ToolTests
{
    private static JObject Report(JObject result) => JObject.Parse(TestRun.ResultText(result));

    [Fact]
    public void ToolsList_HasTheThirteenToolsWithSchemas()
    {
        using var run = new TestRun();
        JArray tools = run.Tools.Describe();
        string[] names = tools.Select(t => (string)t["name"]!).ToArray();
        Assert.Equal(new[] { "get_image_info", "pick_candidates", "get_crop", "get_reference", "memory_read", "memory_add", "list_shapes", "sam2_segment", "detect_points", "propose_shapes", "grid_crop", "render_overlay", "measure" }, names);
        Assert.All(tools, t => Assert.Equal("object", (string?)t["inputSchema"]!["type"]));
    }

    [Fact]
    public void DetectPoints_NeedsANonEmptyObjectList()
    {
        using var run = new TestRun();
        Assert.Throws<ArgumentException>(() => run.Call("detect_points", "{}"));
        Assert.Throws<ArgumentException>(() => run.Call("detect_points", "{\"objects\":[]}"));
    }

    [Fact]
    public void DetectPoints_RejectsAScaleOutsideTheRange()
    {
        using var run = new TestRun();
        Assert.Throws<ArgumentException>(() => run.Call("detect_points", "{\"objects\":[{\"x\":10,\"y\":10}],\"scale\":0}"));
        Assert.Throws<ArgumentException>(() => run.Call("detect_points", "{\"objects\":[{\"x\":10,\"y\":10}],\"scale\":1000}"));
    }

    [Fact]
    public void DetectPoints_RejectsMoreObjectsThanTheCap()
    {
        using var run = new TestRun();
        string objects = string.Join(",", Enumerable.Range(0, 201).Select(i => "{\"x\":" + i + ",\"y\":1}"));
        Assert.Throws<ArgumentException>(() => run.Call("detect_points", "{\"objects\":[" + objects + "]}"));
    }

    [Fact]
    public void OversizedObjects_RejectsAWholeImageOutlineAndOutliersAgainstTheMedian()
    {
        double image = 2448.0 * 2048.0;
        // Normal screws ~ 60 x 220 px; one outline covers the whole picture; one is 8x the others.
        double[] areas = { 13000, 12500, 14000, 0, 12800, image, 13200, 110000 };
        bool[] big = Sam2Runner.OversizedObjects(areas, image);
        Assert.Equal(new[] { false, false, false, false, false, true, false, true }, big);
    }

    [Fact]
    public void OversizedObjects_WithFewObjectsOnlyUsesTheImageShare()
    {
        double image = 1000.0 * 1000.0;
        bool[] big = Sam2Runner.OversizedObjects(new[] { 1000.0, 90000.0, 300000.0 }, image);
        Assert.Equal(new[] { false, false, true }, big);
    }

    [Fact]
    public void Memory_IsNotAvailableWithoutAMemoryPath()
    {
        using var run = new TestRun();
        Assert.Contains("not available", TestRun.ResultText(run.Call("memory_read", "{}")));
        Assert.Contains("not available", TestRun.ResultText(run.Call("memory_add", "{\"note\":\"x\"}")));
    }

    [Fact]
    public void Memory_AddThenReadRoundTripsAndKeepsOneLinePerNote()
    {
        using var run = new TestRun();
        run.Request.MemoryPath = Path.Combine(run.Directory, "memory", "agent-memory.md");
        Assert.Contains("empty", TestRun.ResultText(run.Call("memory_read", "{}")));
        Assert.Equal("Saved.", TestRun.ResultText(run.Call("memory_add", "{\"note\":\"Scratches on the blade are NG.\\nSecond line\"}")));
        Assert.Equal("Saved.", TestRun.ResultText(run.Call("memory_add", "{\"note\":\"Use label Pad for pads.\"}")));
        string memory = TestRun.ResultText(run.Call("memory_read", "{}"));
        string[] lines = memory.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        Assert.Contains("Scratches on the blade are NG. Second line", lines[0]);
        Assert.Contains("Use label Pad for pads.", lines[1]);
        Assert.Contains("not saved", TestRun.ResultText(run.Call("memory_add", "{\"note\":\"   \"}")), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetImageInfo_ReportsSizeClassesPromptAndShapeCount()
    {
        using var run = new TestRun();
        JObject info = Report(run.Call("get_image_info", "{}"));
        Assert.Equal(TestRun.ImageWidth, (int)info["width"]!);
        Assert.Equal(TestRun.ImageHeight, (int)info["height"]!);
        Assert.Equal(new[] { "NG", "OK" }, info["classNames"]!.Select(c => (string)c!).ToArray());
        Assert.Equal(1, (int)info["existingShapeCount"]!);
        Assert.Equal("find defects", (string?)info["prompt"]);
    }

    [Fact]
    public void ListShapes_ReturnsExistingShapes()
    {
        using var run = new TestRun();
        JObject list = Report(run.Call("list_shapes", "{}"));
        Assert.Equal(1, (int)list["count"]!);
        Assert.Equal("OK", (string?)list["shapes"]![0]!["label"]);
        Assert.Equal("rectangle", (string?)list["shapes"]![0]!["shapeType"]);
        Assert.Equal(30, (double)list["shapes"]![0]!["points"]![1]![0]!);
    }

    [Fact]
    public void ListShapes_ReportsIdAndFingerprintOfEachShape()
    {
        using var run = new TestRun();
        JObject list = Report(run.Call("list_shapes", "{}"));
        JToken shape = list["shapes"]![0]!;
        Assert.Equal(0, (int)shape["id"]!);
        string fp = (string)shape["fp"]!;
        Assert.Equal(EasyLabel.Core.Io.ShapeFingerprint.Length, fp.Length);
        Assert.Equal(EasyLabel.Core.Io.ShapeFingerprint.Compute("OK", "rectangle", new List<double[]> { new[] { 1.0, 2.0 }, new[] { 30.0, 40.0 } }), fp);
    }

    [Fact]
    public void ToolSchemas_DescribeTheReviewFields()
    {
        using var run = new TestRun();
        JToken propose = run.Tools.Describe().First(t => (string?)t["name"] == "propose_shapes");
        JToken props = propose["inputSchema"]!["properties"]!["shapes"]!["items"]!["properties"]!;
        Assert.Equal("integer", (string?)props["target"]!["type"]);
        Assert.Equal(new[] { "add", "replace", "remove" }, props["action"]!["enum"]!.Select(a => (string)a!).ToArray());
        Assert.NotNull(props["note"]);
        JToken list = run.Tools.Describe().First(t => (string?)t["name"] == "list_shapes");
        Assert.Contains("fp", (string)list["description"]!);
    }

    [Fact]
    public void ProposeShapes_RemoveNeedsNoPoints_AndRecordsTargetAndHash()
    {
        using var run = new TestRun();
        JObject report = Report(run.Call("propose_shapes", """{"shapes":[{"id":"r","target":0,"action":"remove","note":"duplicate"}]}"""));

        Assert.Equal(1, (int)report["accepted"]!);
        JToken p = JObject.Parse(File.ReadAllText(run.ProposalsPath))["proposals"]![0]!;
        Assert.Equal("remove", (string?)p["action"]);
        Assert.Equal(0, (int)p["target"]!);
        Assert.Equal("OK", (string?)p["label"]);
        Assert.Equal("duplicate", (string?)p["note"]);
        Assert.Empty((JArray)p["points"]!);
        Assert.Equal((string?)Report(run.Call("list_shapes", "{}"))["shapes"]![0]!["fp"], (string?)p["targetHash"]);
    }

    [Fact]
    public void ProposeShapes_ReplaceKeepsTheNewOutline_AndPlainShapesGetNoReviewFields()
    {
        using var run = new TestRun();
        Report(run.Call("propose_shapes", """
            {"shapes":[
              {"id":"a","target":0,"action":"replace","label":"OK","shapeType":"polygon","points":[[10,10],[100,10],[100,80]],"note":"tighter"},
              {"id":"b","label":"NG","shapeType":"polygon","points":[[10,10],[100,10],[100,80]]}
            ]}
            """));

        JArray proposals = (JArray)JObject.Parse(File.ReadAllText(run.ProposalsPath))["proposals"]!;
        Assert.Equal("replace", (string?)proposals[0]["action"]);
        Assert.Equal(3, ((JArray)proposals[0]["points"]!).Count);
        Assert.False(((JObject)proposals[1]).ContainsKey("action"));
        Assert.False(((JObject)proposals[1]).ContainsKey("target"));
        Assert.False(((JObject)proposals[1]).ContainsKey("targetHash"));
    }

    [Theory]
    [InlineData("""{"target":0,"action":"remove"}""", "note is required")]
    [InlineData("""{"target":0,"action":"replace","label":"OK","shapeType":"polygon","points":[[1,1],[9,1],[9,9]]}""", "note is required")]
    [InlineData("""{"target":0,"label":"OK","note":"x","shapeType":"polygon","points":[[1,1],[9,1],[9,9]]}""", "needs action")]
    [InlineData("""{"target":0,"action":"add","label":"OK","note":"x","shapeType":"polygon","points":[[1,1],[9,1],[9,9]]}""", "takes no target")]
    [InlineData("""{"action":"remove","note":"x"}""", "needs a target")]
    [InlineData("""{"target":5,"action":"remove","note":"x"}""", "does not exist")]
    [InlineData("""{"target":-1,"action":"remove","note":"x"}""", "non-negative integer")]
    [InlineData("""{"target":0.5,"action":"remove","note":"x"}""", "non-negative integer")]
    [InlineData("""{"target":"0","action":"remove","note":"x"}""", "non-negative integer")]
    [InlineData("""{"target":0,"action":"delete","note":"x"}""", "action must be")]
    [InlineData("""{"target":0,"action":"replace","label":"OK","note":"x"}""", "shapeType must be")]
    public void ProposeShapes_InvalidReviewCombinationsAreRejectedPerItem(string item, string reason)
    {
        using var run = new TestRun();
        JObject report = Report(run.Call("propose_shapes", "{\"shapes\":[" + item + "]}"));

        Assert.Equal(0, (int)report["accepted"]!);
        Assert.Contains(reason, (string)report["items"]![0]!["reason"]!);
        Assert.Empty(JObject.Parse(File.ReadAllText(run.ProposalsPath))["proposals"]!);
    }

    [Fact]
    public void ProposeShapes_SecondActionOnTheSameTargetIsRejected_ButTheSameIdMayBeReissued()
    {
        using var run = new TestRun();
        JObject first = Report(run.Call("propose_shapes", """
            {"shapes":[
              {"id":"a","target":0,"action":"remove","note":"one"},
              {"id":"b","target":0,"action":"replace","label":"OK","shapeType":"polygon","points":[[1,1],[9,1],[9,9]],"note":"two"}
            ]}
            """));

        Assert.Equal(1, (int)first["accepted"]!);
        Assert.Equal("rejected", (string?)first["items"]![1]!["status"]);
        Assert.Contains("at most one action per target", (string)first["items"]![1]!["reason"]!);

        JObject again = Report(run.Call("propose_shapes", """{"shapes":[{"id":"a","target":0,"action":"remove","note":"still"}]}"""));
        Assert.Equal(1, (int)again["accepted"]!);
        Assert.Equal(1, (int)again["totalProposals"]!);
    }

    [Fact]
    public void ProposeShapes_AddOnTopOfAnExistingShape_IsRejectedAsDuplicate_WhateverTheLabel()
    {
        using var run = new TestRun();
        // e0 is an OK rectangle (1,2)-(30,40). Same box, same label.
        JObject same = Report(run.Call("propose_shapes", """{"shapes":[{"id":"a","label":"OK","shapeType":"rectangle","points":[[1,2],[30,40]]}]}"""));
        Assert.Equal(0, (int)same["accepted"]!);
        Assert.Equal("rejected", (string?)same["items"]![0]!["status"]);
        Assert.Contains("duplicate of existing shape e0", (string)same["items"]![0]!["reason"]!);

        // A slightly shifted box with ANOTHER label still has IoU >= 0.5 with e0.
        JObject other = Report(run.Call("propose_shapes", """{"shapes":[{"id":"b","label":"NG","shapeType":"rectangle","points":[[3,4],[31,41]]}]}"""));
        Assert.Equal(0, (int)other["accepted"]!);
        Assert.Contains("duplicate of existing shape e0", (string)other["items"]![0]!["reason"]!);
        Assert.Empty(JObject.Parse(File.ReadAllText(run.ProposalsPath))["proposals"]!);
    }

    [Fact]
    public void ProposeShapes_SmallPartInsideAnExistingShape_IsKeptOnlyWithAnotherLabel()
    {
        using var run = new TestRun();
        JObject report = Report(run.Call("propose_shapes", """
            {"shapes":[
              {"id":"ng","label":"NG","shapeType":"rectangle","points":[[5,5],[10,10]]},
              {"id":"ok","label":"OK","shapeType":"rectangle","points":[[5,5],[10,10]]}
            ]}
            """));

        Assert.Equal(1, (int)report["accepted"]!);
        Assert.Equal("accepted", (string?)report["items"]![0]!["status"]);
        Assert.Equal("rejected", (string?)report["items"]![1]!["status"]);
        Assert.Contains("duplicate of existing shape e0", (string)report["items"]![1]!["reason"]!);
    }

    [Fact]
    public void ProposeShapes_ReplaceAndRemoveOfAnExistingShapeAreNeverBlocked()
    {
        using var run = new TestRun();
        JObject replace = Report(run.Call("propose_shapes", """{"shapes":[{"id":"r","target":0,"action":"replace","note":"tighter","label":"OK","shapeType":"rectangle","points":[[1,2],[30,40]]}]}"""));
        Assert.Equal(1, (int)replace["accepted"]!);

        // The shape e0 is being replaced, so a plain add may take its place.
        JObject add = Report(run.Call("propose_shapes", """{"shapes":[{"id":"n","label":"OK","shapeType":"rectangle","points":[[2,3],[29,39]]}]}"""));
        Assert.Equal(1, (int)add["accepted"]!);
        Assert.Equal(2, (int)add["totalProposals"]!);

        using var second = new TestRun();
        JObject remove = Report(second.Call("propose_shapes", """{"shapes":[{"id":"x","target":0,"action":"remove","note":"wrong"}]}"""));
        Assert.Equal(1, (int)remove["accepted"]!);
    }

    [Fact]
    public void ProposeShapes_AddOnTopOfAPendingSuggestion_IsRejected_AndGetImageInfoListsThem()
    {
        using var run = new TestRun();
        run.Request.PendingShapes.Add(new RunShape { Label = "NG", ShapeType = "oriented_rectangle", Points = new List<double[]> { new[] { 100.0, 100.0 }, new[] { 200.0, 100.0 }, new[] { 200.0, 150.0 }, new[] { 100.0, 150.0 } } });

        JObject info = Report(run.Call("get_image_info", "{}"));
        Assert.Equal("e0", (string?)info["existingShapes"]![0]!["id"]);
        Assert.Equal("OK", (string?)info["existingShapes"]![0]!["label"]);
        Assert.Equal(new[] { 1.0, 2.0, 29.0, 38.0 }, info["existingShapes"]![0]!["bbox"]!.Select(v => (double)v).ToArray());
        Assert.Equal("s0", (string?)info["pendingShapes"]![0]!["id"]);

        JObject report = Report(run.Call("propose_shapes", """{"shapes":[{"id":"p","label":"NG","shapeType":"rectangle","points":[[105,102],[205,152]]}]}"""));
        Assert.Equal(0, (int)report["accepted"]!);
        Assert.Contains("duplicate of pending suggestion s0", (string)report["items"]![0]!["reason"]!);
    }

    [Fact]
    public void GetImageInfo_WithoutPendingSuggestions_HasNoPendingShapesField()
    {
        using var run = new TestRun();
        JObject info = Report(run.Call("get_image_info", "{}"));
        Assert.False(info.ContainsKey("pendingShapes"));
    }

    [Fact]
    public void GetCrop_NoArguments_ReturnsDownscaledWholeImageJpeg()
    {
        using var run = new TestRun();
        JObject result = run.Call("get_crop", "{}");
        (byte[] jpeg, string text) = Unpack(result);
        Assert.Equal(0xFF, jpeg[0]);
        Assert.Equal(0xD8, jpeg[1]);
        Assert.StartsWith("crop 0,0,2000,1000 scale=0.384", text);
        using Mat decoded = Cv2.ImDecode(jpeg, ImreadModes.Color);
        Assert.Equal(768, decoded.Width);
        Assert.Equal(384, decoded.Height);
    }

    [Fact]
    public void GetCrop_Region_SmallerThanMaxSide_KeepsScaleOne()
    {
        using var run = new TestRun();
        (byte[] jpeg, string text) = Unpack(run.Call("get_crop", "{\"x\":100,\"y\":100,\"width\":300,\"height\":200}"));
        Assert.StartsWith("crop 100,100,300,200 scale=1", text);
        using Mat decoded = Cv2.ImDecode(jpeg, ImreadModes.Color);
        Assert.Equal(300, decoded.Width);
        Assert.Equal(200, decoded.Height);
    }

    [Fact]
    public void GetCrop_ClampsRectangleToImage()
    {
        using var run = new TestRun();
        (_, string text) = Unpack(run.Call("get_crop", "{\"x\":-50,\"y\":900,\"width\":400,\"height\":500}"));
        Assert.StartsWith("crop 0,900,350,100 scale=1", text);
    }

    [Fact]
    public void GetCrop_MaxSideIsClampedToThePixelCap()
    {
        // 2000x1000 is 2.0 MP: max_side 5000 is clamped to 1536 and then to 1.15 MP (scale about 0.7575).
        using var run = new TestRun();
        (byte[] jpeg, string text) = Unpack(run.Call("get_crop", "{\"max_side\":5000}"));
        using Mat decoded = Cv2.ImDecode(jpeg, ImreadModes.Color);
        Assert.True(decoded.Width <= 1536);
        Assert.True((double)decoded.Width * decoded.Height <= ImageSource.MaxOutputPixels);
        Assert.Contains("scale=0.757", text);
        Assert.Contains("Returned picture " + decoded.Width + "x" + decoded.Height + " px", text);
    }

    [Fact]
    public void GetCrop_OutsideTheImage_IsAnError()
    {
        using var run = new TestRun();
        var ex = Assert.Throws<ArgumentException>(() => run.Call("get_crop", "{\"x\":5000,\"y\":0,\"width\":10,\"height\":10}"));
        Assert.Contains("outside the image", ex.Message);
    }

    [Fact]
    public void ProposeShapes_AcceptsPolygonAndNormalisesRectangle_AndWritesFile()
    {
        using var run = new TestRun();
        JObject report = Report(run.Call("propose_shapes", """
            {"shapes":[
              {"id":"a","label":"ng","shapeType":"polygon","points":[[10,10],[100,10],[100,80]],"score":0.9,"note":"scratch"},
              {"id":"b","label":"OK","shapeType":"rectangle","points":[[300,200],[100,50]]},
              {"id":"c","label":"OK","shapeType":"rectangle","points":[[10,10],[50,10],[50,40],[10,40]]}
            ]}
            """));

        Assert.Equal(3, (int)report["accepted"]!);
        Assert.Equal(0, (int)report["rejected"]!);

        JObject file = JObject.Parse(File.ReadAllText(run.ProposalsPath));
        Assert.Equal(1, (int)file["version"]!);
        JArray proposals = (JArray)file["proposals"]!;
        Assert.Equal(3, proposals.Count);
        Assert.Equal("NG", (string?)proposals[0]["label"]);
        Assert.Equal("polygon", (string?)proposals[0]["shapeType"]);
        Assert.Equal("agent", (string?)proposals[0]["source"]);
        Assert.Equal("scratch", (string?)proposals[0]["note"]);
        Assert.Equal(0.9, (double)proposals[0]["score"]!, 3);
        Assert.Equal(0, ((JArray)proposals[0]["holes"]!).Count);
        Assert.Equal(new[] { 100.0, 50.0, 300.0, 200.0 }, new[] { (double)proposals[1]["points"]![0]![0]!, (double)proposals[1]["points"]![0]![1]!, (double)proposals[1]["points"]![1]![0]!, (double)proposals[1]["points"]![1]![1]! });
        Assert.Equal(2, ((JArray)proposals[2]["points"]!).Count);
        Assert.Empty(Directory.GetFiles(run.Directory, "*.tmp", SearchOption.TopDirectoryOnly).Where(f => Path.GetFileName(f).Contains("proposals")));
    }

    [Fact]
    public void ProposeShapes_CreatesNewLabels_ReusesClassSpelling_AndCapsTheNumberOfNewLabels()
    {
        using var run = new TestRun();
        JObject report = Report(run.Call("propose_shapes", """
            {"shapes":[
              {"id":"a","label":" Pad ","shapeType":"polygon","points":[[1,1],[5,1],[5,5]]},
              {"id":"b","label":"pad","shapeType":"polygon","points":[[1,1],[5,1],[5,5]]},
              {"id":"c","label":"ng","shapeType":"polygon","points":[[1,1],[5,1],[5,5]]}
            ]}
            """));
        Assert.Equal(3, (int)report["accepted"]!);
        JArray proposals = (JArray)JObject.Parse(File.ReadAllText(run.ProposalsPath))["proposals"]!;
        Assert.Equal("Pad", (string?)proposals[0]["label"]);
        Assert.Equal("Pad", (string?)proposals[1]["label"]);   // same new label, case-insensitive: first spelling wins
        Assert.Equal("NG", (string?)proposals[2]["label"]);

        var many = new System.Text.StringBuilder("{\"shapes\":[");
        for (int i = 0; i < ProposalStore.MaxNewLabels + 2; i++)
        {
            many.Append(i == 0 ? "" : ",").Append("{\"id\":\"n").Append(i).Append("\",\"label\":\"L").Append(i).Append("\",\"shapeType\":\"polygon\",\"points\":[[1,1],[5,1],[5,5]]}");
        }

        many.Append("]}");
        JObject capped = Report(run.Call("propose_shapes", many.ToString()));
        Assert.Equal(ProposalStore.MaxNewLabels - 1, (int)capped["accepted"]!);   // "Pad" already used one of the new-label slots
        Assert.Equal(3, (int)capped["rejected"]!);
        Assert.Contains("too many new labels", (string)((JArray)capped["items"]!)[ProposalStore.MaxNewLabels]["reason"]!);
    }

    [Fact]
    public void ProposeShapes_RejectsBadItemsWithReasons()
    {
        using var run = new TestRun();
        JObject report = Report(run.Call("propose_shapes", """
            {"shapes":[
              {"label":"LONGLONGLONGLONGLONGLONGLONGLONGLONGLONGLONGLONGLONGLONGLONGLONGLONGLONGLONGLONG","shapeType":"polygon","points":[[1,1],[5,1],[5,5]]},
              {"label":"NG","shapeType":"polygon","points":[[1,1],[5,1]]},
              {"label":"NG","shapeType":"rectangle","points":[[1,1],[5,1],[5,5]]},
              {"label":"NG","shapeType":"polygon","points":[[1,1],[5,"x"],[5,5]]},
              {"label":"NG","shapeType":"circle","points":[[1,1],[5,1],[5,5]]},
              {"label":"NG","fromSam2":"sam2-99"},
              {"label":"NG","shapeType":"rectangle","points":[[3000,10],[3500,20]]},
              {"label":"NG","shapeType":"polygon","points":[[1,1],[5,1],[5,5]],"score":"high"}
            ]}
            """));

        Assert.Equal(0, (int)report["accepted"]!);
        Assert.Equal(8, (int)report["rejected"]!);
        JArray items = (JArray)report["items"]!;
        Assert.Contains("too long", (string)items[0]["reason"]!);
        Assert.Contains("at least 3", (string)items[1]["reason"]!);
        Assert.Contains("2 or 4", (string)items[2]["reason"]!);
        Assert.Contains("finite numbers", (string)items[3]["reason"]!);
        Assert.Contains("shapeType", (string)items[4]["reason"]!);
        Assert.Contains("unknown", (string)items[5]["reason"]!);
        Assert.Contains("no area", (string)items[6]["reason"]!);
        Assert.Contains("score", (string)items[7]["reason"]!);
        Assert.Empty(JObject.Parse(File.ReadAllText(run.ProposalsPath))["proposals"]!);
    }

    [Fact]
    public void ProposeShapes_AcceptsOrientedRectangle_AndKeepsItsRotation()
    {
        using var run = new TestRun();
        // A 120 x 40 box centred at (500, 400), turned 30 degrees; corners TL, TR, BR, BL.
        JObject report = Report(run.Call("propose_shapes", """
            {"shapes":[{"id":"r","label":"NG","shapeType":"oriented_rectangle","points":[[458.0385,352.6795],[561.9615,412.6795],[541.9615,447.3205],[438.0385,387.3205]]}]}
            """));

        Assert.Equal(1, (int)report["accepted"]!);
        JObject stored = (JObject)((JArray)JObject.Parse(File.ReadAllText(run.ProposalsPath))["proposals"]!)[0];
        Assert.Equal("oriented_rectangle", (string?)stored["shapeType"]);
        JArray points = (JArray)stored["points"]!;
        Assert.Equal(4, points.Count);
        double dx = (double)points[1][0]! - (double)points[0][0]!, dy = (double)points[1][1]! - (double)points[0][1]!;
        double angle = Math.Atan2(dy, dx) * 180.0 / Math.PI;
        Assert.InRange(((angle % 90.0) + 90.0) % 90.0, 29.9, 30.1);   // the rotation survives (not reduced to 0 = axis-aligned)
        Assert.InRange(Math.Sqrt(dx * dx + dy * dy), 119.9, 120.1);
    }

    [Fact]
    public void ProposeShapes_OrientedRectangleFitsTheTiltedBoxToAnOutline()
    {
        using var run = new TestRun();
        // Ten outline points on the edges of the 120 x 40 box of the test above (all four corners included), turned 30 degrees.
        JObject report = Report(run.Call("propose_shapes", """
            {"shapes":[{"id":"o","label":"OK","shapeType":"oriented_rectangle","points":[[458.0385,352.6795],[484.0192,367.6795],[510.0,382.6795],[561.9615,412.6795],[551.9615,430.0],[541.9615,447.3205],[515.9808,432.3205],[490.0,417.3205],[438.0385,387.3205],[448.0385,370.0]]}]}
            """));

        Assert.Equal(1, (int)report["accepted"]!);
        JObject stored = (JObject)((JArray)JObject.Parse(File.ReadAllText(run.ProposalsPath))["proposals"]!)[0];
        Assert.Equal("oriented_rectangle", (string?)stored["shapeType"]);
        JArray points = (JArray)stored["points"]!;
        Assert.Equal(4, points.Count);
        double dx = (double)points[1][0]! - (double)points[0][0]!, dy = (double)points[1][1]! - (double)points[0][1]!;
        double angle = Math.Atan2(dy, dx) * 180.0 / Math.PI;
        Assert.InRange(((angle % 90.0) + 90.0) % 90.0, 28.0, 32.0);   // edge angle 30 degrees, tolerance 2
        double side0 = Math.Sqrt(dx * dx + dy * dy);
        double side1 = Math.Sqrt(Math.Pow((double)points[2][0]! - (double)points[1][0]!, 2) + Math.Pow((double)points[2][1]! - (double)points[1][1]!, 2));
        Assert.InRange(side0, 119.5, 120.5);
        Assert.InRange(side1, 39.5, 40.5);
    }

    [Fact]
    public void ProposeShapes_OrientedRectangleNeedsAtLeastThreePointsWithArea()
    {
        using var run = new TestRun();
        JObject report = Report(run.Call("propose_shapes", """
            {"shapes":[
              {"label":"NG","shapeType":"oriented_rectangle","points":[[10,10],[100,10]]},
              {"label":"NG","shapeType":"oriented_rectangle","points":[[10,10],[50,10],[90,10]]},
              {"label":"NG","shapeType":"oriented_rectangle","points":[[10,10],[20,20],[30,30],[40,40]]}
            ]}
            """));

        Assert.Equal(0, (int)report["accepted"]!);
        JArray items = (JArray)report["items"]!;
        Assert.Contains("at least 3", (string)items[0]["reason"]!);
        Assert.Contains("no area", (string)items[1]["reason"]!);
        Assert.Contains("no area", (string)items[2]["reason"]!);
        Assert.Empty(JObject.Parse(File.ReadAllText(run.ProposalsPath))["proposals"]!);
    }

    [Fact]
    public void ProposeShapes_OrientedRectangleAcceptsATriangleAsItsMinimumBox()
    {
        using var run = new TestRun();
        JObject report = Report(run.Call("propose_shapes", """
            {"shapes":[{"label":"NG","shapeType":"oriented_rectangle","points":[[10,10],[100,10],[100,60]]}]}
            """));

        Assert.Equal(1, (int)report["accepted"]!);
        JArray points = (JArray)((JObject)((JArray)JObject.Parse(File.ReadAllText(run.ProposalsPath))["proposals"]!)[0])["points"]!;
        Assert.Equal(4, points.Count);
    }

    [Fact]
    public void ProposeShapes_ClampsPointsToTheImage()
    {
        using var run = new TestRun(allowOutOfBounds: false);
        run.Call("propose_shapes", """{"shapes":[{"id":"p","label":"NG","shapeType":"polygon","points":[[-10,-5],[5000,20],[100,2000]]}]}""");
        JArray points = (JArray)JObject.Parse(File.ReadAllText(run.ProposalsPath))["proposals"]![0]!["points"]!;
        Assert.Equal(new[] { 0.0, 0.0 }, new[] { (double)points[0][0]!, (double)points[0][1]! });
        Assert.Equal(new[] { 2000.0, 20.0 }, new[] { (double)points[1][0]!, (double)points[1][1]! });
        Assert.Equal(new[] { 100.0, 1000.0 }, new[] { (double)points[2][0]!, (double)points[2][1]! });
    }

    private static double[][] StoredPoints(TestRun run, int index = 0)
    {
        JArray points = (JArray)JObject.Parse(File.ReadAllText(run.ProposalsPath))["proposals"]![index]!["points"]!;
        return points.Select(p => new[] { (double)p[0]!, (double)p[1]! }).ToArray();
    }

    [Fact]
    public void ProposeShapes_OrientedRectangleCutByTheBorder_KeepsItsUnclippedCorners()
    {
        // Image 2000x1000. A 100x60 box tilted 30 degrees whose left part sticks out of the image: the corners must stay as given.
        using var run = new TestRun();
        JObject report = Report(run.Call("propose_shapes", """
            {"shapes":[{"id":"b","label":"OK","shapeType":"oriented_rectangle","points":[[-30.0,200.0],[56.6,250.0],[26.6,302.0],[-59.98,252.0]]}]}
            """));
        Assert.Equal(1, (int)report["accepted"]!);
        double[][] p = StoredPoints(run);
        Assert.Equal(4, p.Length);
        Assert.Contains(p, q => q[0] < 0);
        Assert.Contains(p, q => Math.Abs(q[0] + 59.98) < 0.05 && Math.Abs(q[1] - 252.0) < 0.05);
    }

    [Fact]
    public void ProposeShapes_OrientedRectangleOutsideSetting_Off_ClipsAsBefore()
    {
        using var run = new TestRun(allowOutOfBounds: false);
        Report(run.Call("propose_shapes", """
            {"shapes":[{"id":"b","label":"OK","shapeType":"oriented_rectangle","points":[[-30.0,200.0],[70.0,200.0],[70.0,260.0],[-30.0,260.0]]}]}
            """));
        Assert.All(StoredPoints(run), q => Assert.True(q[0] >= 0 && q[0] <= 2000 && q[1] >= 0 && q[1] <= 1000));
    }

    [Fact]
    public void ProposeShapes_BoxMostlyOutsideTheImage_IsRejected()
    {
        using var run = new TestRun();
        JObject report = Report(run.Call("propose_shapes", """
            {"shapes":[
              {"id":"in","label":"OK","shapeType":"oriented_rectangle","points":[[-75.0,200.0],[25.0,200.0],[25.0,260.0],[-75.0,260.0]]},
              {"id":"out","label":"OK","shapeType":"oriented_rectangle","points":[[-95.0,400.0],[5.0,400.0],[5.0,460.0],[-95.0,460.0]]},
              {"id":"away","label":"OK","shapeType":"oriented_rectangle","points":[[-500.0,400.0],[-400.0,400.0],[-400.0,460.0],[-500.0,460.0]]}
            ]}
            """));
        JArray items = (JArray)report["items"]!;
        Assert.Equal("accepted", (string?)items[0]["status"]); // 25% inside
        Assert.Equal("rejected", (string?)items[1]["status"]); // 5% inside
        Assert.Equal("rejected", (string?)items[2]["status"]); // entirely outside
        Assert.Contains("mostly outside", (string?)items[2]["reason"]);
    }

    [Fact]
    public void ProposeShapes_RectangleAndPolygonMayLeaveTheImage()
    {
        using var run = new TestRun();
        JObject report = Report(run.Call("propose_shapes", """
            {"shapes":[
              {"id":"r","label":"NG","shapeType":"rectangle","points":[[-20,100],[80,160]]},
              {"id":"p","label":"NG","shapeType":"polygon","points":[[1950,300],[2040,300],[2040,380]]}
            ]}
            """));
        Assert.Equal(2, (int)report["accepted"]!);
        double[][] r = StoredPoints(run, 0);
        Assert.Equal(-20.0, r[0][0]);
        double[][] g = StoredPoints(run, 1);
        Assert.Equal(2040.0, g[1][0]);
    }

    [Fact]
    public void ProposeShapes_ReplacesSameIdAndKeepsFullList()
    {
        using var run = new TestRun();
        run.Call("propose_shapes", """{"shapes":[{"id":"x","label":"NG","shapeType":"polygon","points":[[101,101],[109,101],[109,109]]},{"id":"y","label":"OK","shapeType":"polygon","points":[[101,101],[109,101],[109,109]]}]}""");
        JObject second = Report(run.Call("propose_shapes", """{"shapes":[{"id":"x","label":"OK","shapeType":"polygon","points":[[102,102],[108,102],[108,108]]},{"label":"NG","shapeType":"polygon","points":[[103,103],[108,103],[108,108]]}]}"""));

        Assert.Equal(3, (int)second["totalProposals"]!);
        JArray proposals = (JArray)JObject.Parse(File.ReadAllText(run.ProposalsPath))["proposals"]!;
        Assert.Equal(3, proposals.Count);
        Assert.Equal("x", (string?)proposals[0]["id"]);
        Assert.Equal("OK", (string?)proposals[0]["label"]);
        Assert.Equal(102.0, (double)proposals[0]["points"]![0]![0]!);
        Assert.Equal("y", (string?)proposals[1]["id"]);
    }

    [Fact]
    public void ProposeShapes_EmptyList_WritesEmptyFile_AndRequiresArray()
    {
        using var run = new TestRun();
        JObject report = Report(run.Call("propose_shapes", "{\"shapes\":[]}"));
        Assert.Equal(0, (int)report["accepted"]!);
        Assert.Empty(JObject.Parse(File.ReadAllText(run.ProposalsPath))["proposals"]!);
        Assert.Throws<ArgumentException>(() => run.Call("propose_shapes", "{}"));
    }

    [Fact]
    public void Sam2Segment_WithoutModels_IsIsErrorMessageThroughTheServer()
    {
        using var run = new TestRun(withModelsFolder: true);
        List<JObject> replies = Wire.Exchange(run.Tools,
            Wire.Request(1, "tools/call", "{\"name\":\"sam2_segment\",\"arguments\":{\"box\":[100,100,400,300]}}"));

        JObject result = Assert.Single(replies)["result"]!.ToObject<JObject>()!;
        Assert.True((bool)result["isError"]!);
        string text = (string)result["content"]![0]!["text"]!;
        Assert.Contains("SAM2 files not found", text);
    }

    [Fact]
    public void Sam2Segment_WithoutPrompt_IsAnError()
    {
        using var run = new TestRun(withModelsFolder: true);
        var ex = Assert.Throws<ArgumentException>(() => run.Call("sam2_segment", "{}"));
        Assert.Contains("box", ex.Message);
    }

    private static (byte[] Jpeg, string Text) Unpack(JObject result)
    {
        JArray content = (JArray)result["content"]!;
        Assert.Equal("image", (string?)content[0]["type"]);
        Assert.Equal("image/jpeg", (string?)content[0]["mimeType"]);
        Assert.Equal("text", (string?)content[1]["type"]);
        return (Convert.FromBase64String((string)content[0]["data"]!), (string)content[1]["text"]!);
    }
}
