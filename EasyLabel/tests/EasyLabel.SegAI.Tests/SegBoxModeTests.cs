using System.Drawing;
using EasyLabel.Core.Model;
using EasyLabel.SegAI.AutoLabel;
using EasyLabel.SegAI.Convert;
using EasyLabel.SegAI.Data;
using EasyLabel.SegAI.Options;
using EasyLabel.SegAI.Project;
using EasyLabel.SegAI.Training;
using Newtonsoft.Json;

namespace EasyLabel.SegAI.Tests;

// EAI-POS P9: Detect box mode. The rule, rectangle labels as theta-0 instances, the persisted output kind, the suggestion kinds, and the
// unchanged polygon path. No native DLL is needed.
public sealed class SegBoxModeTests
{
    private static SegAiInstanceResult Box(float cx, float cy, float w, float h, float theta, float score = 0.5f)
    {
        return new SegAiInstanceResult { Kind = SegInstance.KindBox, Cx = cx, Cy = cy, W = w, H = h, Theta = theta, Score = score };
    }

    private static LabelShape Shape(string type, params double[][] points)
    {
        var shape = new LabelShape { Label = "NG", ShapeType = type };
        foreach (double[] p in points) shape.Points.Add(p);
        return shape;
    }

    [Fact]
    public void Rule_PolygonAnywhere_KeepsMaskPath()
    {
        Assert.Null(SegBoxModeRule.Decide(polygonRegions: 1, rectangleLabels: 5, orientedLabels: 0));
        Assert.Null(SegBoxModeRule.Decide(polygonRegions: 2, rectangleLabels: 0, orientedLabels: 3));
    }

    [Fact]
    public void Rule_NoBoxLabels_KeepsMaskPath()
    {
        Assert.Null(SegBoxModeRule.Decide(0, 0, 0));
    }

    [Fact]
    public void Rule_BoxesOnly_OutputKindFollowsShapes()
    {
        Assert.Equal(SegBoxModeRule.RectangleOutput, SegBoxModeRule.Decide(0, 2, 0));
        Assert.Equal(SegBoxModeRule.OrientedOutput, SegBoxModeRule.Decide(0, 0, 1));
        Assert.Equal(SegBoxModeRule.OrientedOutput, SegBoxModeRule.Decide(0, 3, 1)); // mixed: any oriented label makes the output oriented
    }

    [Fact]
    public void OutputShapeOf_ReadsStoredKind_AndDefaultsToOriented()
    {
        Assert.Equal(SegBoxModeRule.RectangleOutput, SegBoxModeRule.OutputShapeOf("{\"OutputShape\":\"rectangle\"}"));
        Assert.Equal(SegBoxModeRule.OrientedOutput, SegBoxModeRule.OutputShapeOf("{\"OutputShape\":\"oriented_rectangle\"}"));
        Assert.Equal(SegBoxModeRule.OrientedOutput, SegBoxModeRule.OutputShapeOf("{\"Schema\":1}")); // legacy and Position reports
        Assert.Equal(SegBoxModeRule.OrientedOutput, SegBoxModeRule.OutputShapeOf(null));
        Assert.Equal(SegBoxModeRule.OrientedOutput, SegBoxModeRule.OutputShapeOf("not json"));
        Assert.Equal(SegBoxModeRule.OrientedOutput, SegBoxModeRule.OutputShapeOf("{\"OutputShape\":5}"));
    }

    [Fact]
    public void Report_PolygonRun_WritesNoOutputKey_AndBoxRunRoundTrips()
    {
        string polygon = JsonConvert.SerializeObject(new SegTrainingReport { Version = 2 });
        Assert.DoesNotContain("OutputShape", polygon);

        string box = JsonConvert.SerializeObject(new SegTrainingReport { Version = 2, OutputShape = SegBoxModeRule.RectangleOutput });
        Assert.Contains("\"OutputShape\":\"rectangle\"", box);
        Assert.Equal(SegBoxModeRule.RectangleOutput, JsonConvert.DeserializeObject<SegTrainingReport>(box)!.OutputShape);
        Assert.Equal(SegBoxModeRule.RectangleOutput, SegBoxModeRule.OutputShapeOf(box));
    }

    [Fact]
    public void Settings_BoxOff_HasNoInstanceKeys_BoxOn_HasHeadAndSymmetry()
    {
        var options = new SegTrainingOptions { V2LegacyPrior = true };
        string off = SegAiTrainingPipeline.BuildV2SettingsJson(options, "models/b.onnx", "work", false, Array.Empty<int>(), false, null, false, Array.Empty<int>(), 0);
        Assert.DoesNotContain("instanceHead", off);
        Assert.DoesNotContain("angleSymmetry", off);

        string on = SegAiTrainingPipeline.BuildV2SettingsJson(options, "models/b.onnx", "work", false, Array.Empty<int>(), false, null, false, Array.Empty<int>(), 0, instanceHead: true);
        Assert.Contains("\"instanceHead\":1", on);
        Assert.Contains("\"angleSymmetry\":1", on); // full 360 degrees: keeps a rectangle's own orientation, theta near 0
    }

    [Fact]
    public void Converter_RectangleIsBoxAtThetaZero_OnlyWhenIncluded()
    {
        var map = new SegClassMap();
        LabelShape rect = Shape("rectangle", new[] { 30.0, 50.0 }, new[] { 10.0, 20.0 }); // corners in reverse order

        Assert.Empty(SegLabelMeConverter.ToInstances(new[] { rect }, map)); // Position path: unchanged

        SegInstance box = Assert.Single(SegLabelMeConverter.ToInstances(new[] { rect }, map, includeRectangles: true));
        Assert.Equal(SegInstance.KindBox, box.Kind);
        Assert.Equal(0f, box.Theta);
        Assert.True(Math.Abs(box.Cx - 20) < 1e-4, "centre x");
        Assert.True(Math.Abs(box.Cy - 35) < 1e-4, "centre y");
        Assert.True(Math.Abs(box.W - 20) < 1e-4, "w = x extent");
        Assert.True(Math.Abs(box.H - 30) < 1e-4, "h = y extent");

        LabelShape degenerate = Shape("rectangle", new[] { 5.0, 5.0 }, new[] { 5.0, 9.0 });
        Assert.Empty(SegLabelMeConverter.ToInstances(new[] { degenerate }, map, includeRectangles: true));
    }

    [Fact]
    public void Suggestions_RectangleModel_EmitsRectangleAtBounds()
    {
        IReadOnlyList<SegAiSuggestion> list = SegAiSuggestionBuilder.BuildInstances("a.png", new[] { Box(50, 40, 20, 8, 0f) }, SegBoxModeRule.RectangleOutput);

        SegAiSuggestion only = Assert.Single(list);
        Assert.Equal("rectangle", only.ShapeType);
        Assert.Equal(new PointF(40, 36), only.Outer[0]);
        Assert.Equal(new PointF(60, 36), only.Outer[1]);
        Assert.Equal(new PointF(60, 44), only.Outer[2]);
        Assert.Equal(new PointF(40, 44), only.Outer[3]);
    }

    [Fact]
    public void Suggestions_RectangleModel_NinetyDegrees_SwapsSides()
    {
        float quarter = (float)(Math.PI / 2);
        SegAiSuggestion only = Assert.Single(SegAiSuggestionBuilder.BuildInstances("a.png", new[] { Box(50, 40, 20, 8, quarter) }, SegBoxModeRule.RectangleOutput));

        // A 20 x 8 box turned by 90 degrees is 8 wide and 20 tall: the rectangle is x 46..54, y 30..50.
        Assert.Equal("rectangle", only.ShapeType);
        Assert.True(Math.Abs(only.Outer[0].X - 46) < 1e-3 && Math.Abs(only.Outer[0].Y - 30) < 1e-3, "top-left");
        Assert.True(Math.Abs(only.Outer[2].X - 54) < 1e-3 && Math.Abs(only.Outer[2].Y - 50) < 1e-3, "bottom-right");
    }

    [Fact]
    public void Suggestions_RectangleModel_SmallAngleNearZero_KeepsUnrotatedBox()
    {
        float fiveDegrees = (float)(5 * Math.PI / 180);
        SegAiSuggestion only = Assert.Single(SegAiSuggestionBuilder.BuildInstances("a.png", new[] { Box(50, 40, 20, 8, fiveDegrees) }, SegBoxModeRule.RectangleOutput));

        Assert.Equal(new PointF(40, 36), only.Outer[0]);
        Assert.Equal(new PointF(60, 44), only.Outer[2]);
    }

    [Fact]
    public void Suggestions_RectangleModel_NearNinetyWithinTolerance_SwapsSides()
    {
        float sevenPastQuarter = (float)((97 * Math.PI) / 180);
        SegAiSuggestion only = Assert.Single(SegAiSuggestionBuilder.BuildInstances("a.png", new[] { Box(50, 40, 20, 8, sevenPastQuarter) }, SegBoxModeRule.RectangleOutput));

        Assert.True(Math.Abs(only.Outer[0].X - 46) < 1e-3 && Math.Abs(only.Outer[0].Y - 30) < 1e-3, "top-left");
        Assert.True(Math.Abs(only.Outer[2].X - 54) < 1e-3 && Math.Abs(only.Outer[2].Y - 50) < 1e-3, "bottom-right");
    }

    [Fact]
    public void Suggestions_RectangleModel_OtherAngle_UsesEnclosingBounds()
    {
        float thirtyDegrees = (float)(Math.PI / 6);
        SegAiSuggestion only = Assert.Single(SegAiSuggestionBuilder.BuildInstances("a.png", new[] { Box(50, 40, 20, 8, thirtyDegrees) }, SegBoxModeRule.RectangleOutput));

        // Enclosing half sizes at 30 degrees: x = 10 cos30 + 4 sin30 = 10.66, y = 10 sin30 + 4 cos30 = 8.46.
        Assert.True(Math.Abs(only.Outer[0].X - (50 - 10.66)) < 0.01 && Math.Abs(only.Outer[0].Y - (40 - 8.46)) < 0.01, "top-left");
        Assert.True(Math.Abs(only.Outer[2].X - (50 + 10.66)) < 0.01 && Math.Abs(only.Outer[2].Y - (40 + 8.46)) < 0.01, "bottom-right");
    }

    [Fact]
    public void Resolve_BoxOnRectangles_IsOnWithNote()
    {
        string? output = SegBoxModeRule.Resolve(0, 2, 0, 2, retrain: false, originalInput: false, out string? note);
        Assert.Equal(SegBoxModeRule.RectangleOutput, output);
        Assert.Equal("Box mode on: rectangle output.", note);
    }

    [Fact]
    public void Resolve_PolygonMixedWithBoxes_IsOffWithNote()
    {
        Assert.Null(SegBoxModeRule.Resolve(1, 2, 0, 2, retrain: false, originalInput: false, out string? note));
        Assert.Equal("Box mode off: polygon labels present.", note);
    }

    [Fact]
    public void Resolve_PolygonOnly_IsOffWithoutNote()
    {
        // Unchanged polygon projects: no decision and no report note.
        Assert.Null(SegBoxModeRule.Resolve(3, 0, 0, 2, retrain: false, originalInput: false, out string? note));
        Assert.Null(note);
    }

    [Fact]
    public void Resolve_V1_IsOffWithNote()
    {
        Assert.Null(SegBoxModeRule.Resolve(0, 2, 0, 1, retrain: false, originalInput: false, out string? note));
        Assert.Equal("Box mode off: V1 model.", note);
    }

    [Fact]
    public void Resolve_Retrain_IsOffWithNote()
    {
        Assert.Null(SegBoxModeRule.Resolve(0, 2, 0, 2, retrain: true, originalInput: false, out string? note));
        Assert.Equal("Box mode off: Retrain keeps the mask-only path.", note);
    }

    [Fact]
    public void Resolve_OriginalInput_IsOffWithNote()
    {
        Assert.Null(SegBoxModeRule.Resolve(0, 0, 1, 2, retrain: false, originalInput: true, out string? note));
        Assert.Equal("Box mode off: original input size.", note);
    }

    [Fact]
    public void BoxRectangle_OnlyForInstancePathRectangles()
    {
        Assert.True(AutoLabelService.IsBoxRectangle(true, "rectangle", 4));
        Assert.False(AutoLabelService.IsBoxRectangle(false, "rectangle", 4)); // no head loaded: old flow
        Assert.False(AutoLabelService.IsBoxRectangle(true, "polygon", 4));
        Assert.False(AutoLabelService.IsBoxRectangle(true, "rectangle", 3));
    }

    [Fact]
    public void Accept_RectangleWithoutInstanceHead_KeepsPolygonPath()
    {
        using SegAiSession session = SegAiSession.Create(TrainFixtures.Folder("boxaccept"));
        TrainFixtures.WriteImage(session.ProjectRoot, "a.png", 1, 64, 64);
        var rect = new SegAiSuggestion("a.png", 0, new[] { new PointF(10, 10), new PointF(30, 10), new PointF(30, 20), new PointF(10, 20) },
            Array.Empty<PointF[]>(), 0.9f, null, "rectangle");

        Assert.Equal(1, new AutoLabelService(session).Accept("a.png", new[] { rect }));
        LabelShape shape = Assert.Single(session.LoadForEdit("a.png").Document.Shapes);
        Assert.Equal("polygon", shape.ShapeType);
        Assert.Equal(4, shape.Points.Count);
    }

    [Fact]
    public void Suggestions_OrientedAndLegacyModels_KeepOrientedRectangle()
    {
        SegAiSuggestion rot = Assert.Single(SegAiSuggestionBuilder.BuildInstances("a.png", new[] { Box(50, 40, 20, 8, 0.3f) }, SegBoxModeRule.OrientedOutput));
        Assert.Equal("oriented_rectangle", rot.ShapeType);

        // A legacy or Position model (no stored kind) gives the same result as before the parameter existed.
        SegAiSuggestion legacy = Assert.Single(SegAiSuggestionBuilder.BuildInstances("a.png", new[] { Box(50, 40, 20, 8, 0.3f) }));
        Assert.Equal("oriented_rectangle", legacy.ShapeType);
        Assert.Equal(rot.Outer, legacy.Outer);
    }

    [Fact]
    public void Suggestions_CircleStaysCircle_WhateverTheBoxKind()
    {
        var circle = new SegAiInstanceResult { Kind = SegInstance.KindCircle, Cx = 30, Cy = 40, W = 10, H = 10, Theta = 0, Score = 0.4f };
        SegAiSuggestion only = Assert.Single(SegAiSuggestionBuilder.BuildInstances("a.png", new[] { circle }, SegBoxModeRule.RectangleOutput));
        Assert.Equal(SegAiSuggestionBuilder.CircleShape, only.ShapeType);
    }

    private static SegInstance Oriented(float w, float h)
    {
        return new SegInstance { Kind = SegInstance.KindBox, Cx = 0, Cy = 0, W = w, H = h, Theta = 0, ClassIndex = 0 };
    }

    private static SegInstance Circle(float r)
    {
        return new SegInstance { Kind = SegInstance.KindCircle, Cx = 0, Cy = 0, W = 2 * r, H = 2 * r, Theta = 0, ClassIndex = 0 };
    }

    // EAI-POS follow-up: the angle symmetry rule (median |w/h - 1| of the oriented boxes, circles ignored).
    [Fact]
    public void AngleSymmetry_NearSquareBoxes_ChooseFour()
    {
        // Deviations 0.011, 0.019, 0.020: median 0.019.
        Assert.Equal(4, SegBoxModeRule.AngleSymmetry(new[] { Oriented(270, 267), Oriented(270, 265), Oriented(200, 196) }));
        Assert.Equal(4, SegBoxModeRule.AngleSymmetry(new[] { Oriented(114, 100) })); // 0.14 is under the 0.15 tolerance
    }

    [Fact]
    public void AngleSymmetry_ElongatedBoxes_KeepOne()
    {
        Assert.Equal(1, SegBoxModeRule.AngleSymmetry(new[] { Oriented(300, 100), Oriented(260, 120) }));
        Assert.Equal(1, SegBoxModeRule.AngleSymmetry(new[] { Oriented(116, 100) })); // 0.16 is over the tolerance
    }

    [Fact]
    public void AngleSymmetry_Mixed_UsesTheMedian()
    {
        // Two square and three elongated: the median is 0.3, so one.
        Assert.Equal(1, SegBoxModeRule.AngleSymmetry(new[] { Oriented(100, 102), Oriented(100, 103), Oriented(130, 100), Oriented(150, 100), Oriented(200, 100) }));
        // Three square and one elongated: even count, median (0.03 + 0.04) / 2 = 0.035, so four.
        Assert.Equal(4, SegBoxModeRule.AngleSymmetry(new[] { Oriented(100, 102), Oriented(100, 103), Oriented(100, 104), Oriented(150, 100) }));
    }

    [Fact]
    public void AngleSymmetry_EmptyOrCirclesOnly_KeepOne()
    {
        Assert.Equal(1, SegBoxModeRule.AngleSymmetry(Array.Empty<SegInstance>()));
        Assert.Equal(1, SegBoxModeRule.AngleSymmetry(null));
        Assert.Equal(1, SegBoxModeRule.AngleSymmetry(new[] { Circle(20), Circle(21) })); // circles are square but ignored
    }

    [Fact]
    public void AngleSymmetry_RectanglesDoNotCount_OrientedSquareDoes()
    {
        var map = new SegClassMap();
        LabelShape rect = Shape("rectangle", new[] { 10.0, 10.0 }, new[] { 110.0, 110.0 }); // a 100 x 100 rectangle
        Assert.Equal(1, SegBoxModeRule.AngleSymmetry(SegLabelMeConverter.ToInstances(new[] { rect }, map))); // Rect-only project: Position path, no rectangles

        LabelShape rot = Shape("oriented_rectangle", new[] { 10.0, 10.0 }, new[] { 120.0, 10.0 }, new[] { 120.0, 110.0 }, new[] { 10.0, 110.0 });
        Assert.Equal(4, SegBoxModeRule.AngleSymmetry(SegLabelMeConverter.ToInstances(new[] { rot }, map))); // 110 x 100 oriented box: 0.1
    }

    [Fact]
    public void AngleSymmetryOverride_ParsesOneTwoFourOnly()
    {
        Assert.Null(SegBoxModeRule.ParseAngleSymmetryOverride(null));
        Assert.Null(SegBoxModeRule.ParseAngleSymmetryOverride(""));
        Assert.Null(SegBoxModeRule.ParseAngleSymmetryOverride("auto"));
        Assert.Null(SegBoxModeRule.ParseAngleSymmetryOverride("3"));
        Assert.Null(SegBoxModeRule.ParseAngleSymmetryOverride("0"));
        Assert.Null(SegBoxModeRule.ParseAngleSymmetryOverride("4.0"));
        Assert.Equal(1, SegBoxModeRule.ParseAngleSymmetryOverride("1"));
        Assert.Equal(2, SegBoxModeRule.ParseAngleSymmetryOverride(" 2 "));
        Assert.Equal(4, SegBoxModeRule.ParseAngleSymmetryOverride("4"));
    }

    [Fact]
    public void AngleSymmetryNote_NamesTheValueAndItsSource()
    {
        Assert.Equal("Angle symmetry 4 (near-square boxes).", SegBoxModeRule.AngleSymmetryNote(4, overridden: false));
        Assert.Equal("Angle symmetry 1.", SegBoxModeRule.AngleSymmetryNote(1, overridden: false));
        Assert.Equal("Angle symmetry 2 (project override).", SegBoxModeRule.AngleSymmetryNote(2, overridden: true));
    }

    [Fact]
    public void Settings_AngleSymmetry_WrittenOnlyWithInstanceHead()
    {
        var options = new SegTrainingOptions { V2LegacyPrior = true };
        string four = SegAiTrainingPipeline.BuildV2SettingsJson(options, "models/b.onnx", "work", false, Array.Empty<int>(), false, null, false, Array.Empty<int>(), 0, instanceHead: true, angleSymmetry: 4);
        Assert.Contains("\"instanceHead\":1", four);
        Assert.Contains("\"angleSymmetry\":4", four);

        string polygon = SegAiTrainingPipeline.BuildV2SettingsJson(options, "models/b.onnx", "work", false, Array.Empty<int>(), false, null, false, Array.Empty<int>(), 0, instanceHead: false, angleSymmetry: 4);
        Assert.DoesNotContain("angleSymmetry", polygon); // polygon Detect and old runs: no key at all
    }

    [Fact]
    public void AngleSymmetryOverride_StoredInAdvancedJson_AutoIsNull()
    {
        string root = TrainFixtures.Folder("eai-pos-symmetry");
        TrainFixtures.WriteImage(root, "a.png", 2, 64, 64);
        using SegAiSession session = SegAiSession.Create(root);
        var store = new SegProjectStore(session.Paths);
        Directory.CreateDirectory(store.EasyAiDir);
        string file = Path.Combine(store.EasyAiDir, "advanced.json");
        if (File.Exists(file)) File.Delete(file);

        Assert.Null(store.LoadAngleSymmetryOverride()); // missing file: auto
        File.WriteAllText(file, "{ not json");
        Assert.Null(store.LoadAngleSymmetryOverride()); // invalid file: auto
        File.WriteAllText(file, "{\"angleSymmetry\":\"auto\",\"segClasses\":[]}");
        Assert.Null(store.LoadAngleSymmetryOverride());

        store.SaveAngleSymmetryOverride(4);
        Assert.Equal(4, store.LoadAngleSymmetryOverride());
        Assert.Contains("segClasses", File.ReadAllText(file)); // other keys survive the save

        store.SaveAngleSymmetryOverride(null);
        Assert.Contains("auto", File.ReadAllText(file)); // stored as the word, not as a number
        Assert.Null(store.LoadAngleSymmetryOverride());

        Assert.Throws<ArgumentOutOfRangeException>(() => store.SaveAngleSymmetryOverride(3));
        Assert.Null(store.LoadAngleSymmetryOverride()); // a rejected value is not written
    }

    [Fact]
    public void Settings_InstanceEpochs_WrittenOnlyInsideInstanceHead()
    {
        var set = new SegTrainingOptions { V2LegacyPrior = true, InstanceEpochs = 300 };
        var unset = new SegTrainingOptions { V2LegacyPrior = true };

        string head = SegAiTrainingPipeline.BuildV2SettingsJson(set, "models/b.onnx", "work", false, Array.Empty<int>(), false, null, false, Array.Empty<int>(), 0, instanceHead: true, angleSymmetry: 4);
        Assert.Contains("\"instanceHead\":1", head);
        Assert.Contains("\"instanceEpochs\":300", head);

        string headNative = SegAiTrainingPipeline.BuildV2SettingsJson(unset, "models/b.onnx", "work", false, Array.Empty<int>(), false, null, false, Array.Empty<int>(), 0, instanceHead: true, angleSymmetry: 4);
        Assert.DoesNotContain("instanceEpochs", headNative); // 0 = native default: the key is left out

        // Polygon Detect (no instance head) keeps the same bytes whatever the instance epochs value is.
        string polygonUnset = SegAiTrainingPipeline.BuildV2SettingsJson(unset, "models/b.onnx", "work", false, Array.Empty<int>(), false, null, false, Array.Empty<int>(), 0);
        string polygonSet = SegAiTrainingPipeline.BuildV2SettingsJson(set, "models/b.onnx", "work", false, Array.Empty<int>(), false, null, false, Array.Empty<int>(), 0);
        Assert.Equal(polygonUnset, polygonSet);
        Assert.DoesNotContain("instanceEpochs", polygonSet);
        Assert.DoesNotContain("instanceHead", polygonSet);
    }

    [Fact]
    public void Options_InstanceEpochs_ZeroOrOneToThousand_Valid()
    {
        new SegTrainingOptions { Version = 2, InstanceEpochs = 0 }.Validate();
        new SegTrainingOptions { Version = 2, InstanceEpochs = 1 }.Validate();
        new SegTrainingOptions { Version = 2, InstanceEpochs = 1000 }.Validate();
        Assert.Throws<ArgumentException>(() => new SegTrainingOptions { Version = 2, InstanceEpochs = 1001 }.Validate());
        Assert.Throws<ArgumentException>(() => new SegTrainingOptions { Version = 2, InstanceEpochs = -1 }.Validate());
    }

    [Fact]
    public void InstanceEpochsNote_NamesTheEffectiveValue_OnlyWithInstanceHead()
    {
        Assert.Equal("Instance epochs 300.", SegAiTrainingPipeline.InstanceEpochsNote(new SegTrainingOptions { InstanceEpochs = 300 }, true));
        Assert.Equal("Instance epochs 60 (native default).", SegAiTrainingPipeline.InstanceEpochsNote(new SegTrainingOptions(), true));
        Assert.Null(SegAiTrainingPipeline.InstanceEpochsNote(new SegTrainingOptions { InstanceEpochs = 300 }, false));
        Assert.Equal(300, SegTrainingOptions.DefaultInstanceEpochs);
    }

    [Fact]
    public void InstanceEpochs_StoredInAdvancedJson_AutoIsNull()
    {
        string root = TrainFixtures.Folder("eai-pos-epochs");
        TrainFixtures.WriteImage(root, "a.png", 2, 64, 64);
        using SegAiSession session = SegAiSession.Create(root);
        var store = new SegProjectStore(session.Paths);
        Directory.CreateDirectory(store.EasyAiDir);
        string file = Path.Combine(store.EasyAiDir, "advanced.json");
        if (File.Exists(file)) File.Delete(file);

        Assert.Null(store.LoadInstanceEpochs()); // missing file: auto (the session then uses 300)
        File.WriteAllText(file, "{ not json");
        Assert.Null(store.LoadInstanceEpochs()); // invalid file: auto
        File.WriteAllText(file, "{\"instanceEpochs\":\"auto\",\"segClasses\":[]}");
        Assert.Null(store.LoadInstanceEpochs());
        File.WriteAllText(file, "{\"instanceEpochs\":5000}");
        Assert.Null(store.LoadInstanceEpochs()); // out of 1..1000: auto
        File.WriteAllText(file, "{\"instanceEpochs\":300.5}");
        Assert.Null(store.LoadInstanceEpochs());

        File.WriteAllText(file, "{\"segClasses\":[]}");
        store.SaveInstanceEpochs(150);
        Assert.Equal(150, store.LoadInstanceEpochs());
        Assert.Contains("segClasses", File.ReadAllText(file)); // other keys survive the save

        store.SaveInstanceEpochs(null);
        Assert.Contains("\"instanceEpochs\": \"auto\"", File.ReadAllText(file)); // stored as the word, not as a number
        Assert.Null(store.LoadInstanceEpochs());

        Assert.Throws<ArgumentOutOfRangeException>(() => store.SaveInstanceEpochs(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => store.SaveInstanceEpochs(1001));
        Assert.Null(store.LoadInstanceEpochs()); // a rejected value is not written
    }
}
