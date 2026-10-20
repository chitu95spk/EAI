using System.Drawing;
using System.Runtime.InteropServices;
using EasyLabel.Core.Io;
using EasyLabel.Core.Model;
using EasyLabel.Core.Projects;
using EasyLabel.SegAI.Convert;
using EasyLabel.SegAI.Data;
using EasyLabel.SegAI.Interop;
using EasyLabel.SegAI.Options;
using EasyLabel.SegAI.Project;
using EasyLabel.SegAI.Training;

namespace EasyLabel.SegAI.Tests;

// EAI-POS P5 follow-up: struct sizes, circle and non-finite conversion, quality option, settings keys, and Position circles kept on save.
public sealed class SegInstanceWiringTests
{
    private static PointF[] Box(float x0, float y0, float x1, float y1)
    {
        return new[] { new PointF(x0, y0), new PointF(x1, y0), new PointF(x1, y1), new PointF(x0, y1) };
    }

    [Fact]
    public void NativeStructs_MatchHeaderSizes()
    {
        // SegInstanceV1 is 32 bytes and SegInstanceResultV1 is 40 bytes in BeeNativeSegAI/SegAINativeExport.h.
        Assert.Equal(32, Marshal.SizeOf(typeof(NativeInstanceV1)));
        Assert.Equal(40, Marshal.SizeOf(typeof(NativeInstanceResultV1)));
    }

    [Fact]
    public void NativeInstance_From_CopiesEveryField()
    {
        NativeInstanceV1 native = NativeInstanceV1.From(new SegInstance
        {
            Kind = SegInstance.KindCircle, Cx = 1.5f, Cy = 2.5f, W = 6f, H = 6f, Theta = 0.25f, ClassIndex = 1,
        });
        Assert.Equal(32, native.StructSize);
        Assert.Equal(SegInstance.KindCircle, native.Kind);
        Assert.Equal(1.5f, native.Cx);
        Assert.Equal(2.5f, native.Cy);
        Assert.Equal(6f, native.W);
        Assert.Equal(6f, native.H);
        Assert.Equal(0.25f, native.Theta);
        Assert.Equal(1, native.ClassIndex);
    }

    [Fact]
    public void Converter_CircleToInstance_Radius()
    {
        var circle = new LabelShape { Label = "OK", ShapeType = "circle" };
        circle.Points.Add(new[] { 30.0, 40.0 });
        circle.Points.Add(new[] { 30.0, 45.0 });

        SegInstance one = Assert.Single(SegLabelMeConverter.ToInstances(new[] { circle }, new SegClassMap()));
        Assert.Equal(SegInstance.KindCircle, one.Kind);
        Assert.Equal(1, one.ClassIndex); // OK is label 2
        Assert.True(Math.Abs(one.Cx - 30) < 1e-3, "centre x");
        Assert.True(Math.Abs(one.Cy - 40) < 1e-3, "centre y");
        Assert.True(Math.Abs(one.W - 10) < 1e-3, "w = 2r");
        Assert.True(Math.Abs(one.H - 10) < 1e-3, "h = 2r");
    }

    [Fact]
    public void Converter_NonFiniteCoordinatesAreSkipped()
    {
        var circle = new LabelShape { Label = "NG", ShapeType = "circle" };
        circle.Points.Add(new[] { double.NaN, 40.0 });
        circle.Points.Add(new[] { 30.0, 45.0 });
        var box = new LabelShape { Label = "NG", ShapeType = "oriented_rectangle" };
        for (int i = 0; i < 4; i++) box.Points.Add(new[] { double.PositiveInfinity, 1.0 });

        Assert.Empty(SegLabelMeConverter.ToInstances(new[] { circle, box }, new SegClassMap()));
    }

    [Fact]
    public void InstanceQuality_DefaultsToQuality_AndPersistsChoice()
    {
        string root = TrainFixtures.Folder("eai-pos-quality");
        TrainFixtures.WriteImage(root, "a.png", 2, 64, 64);
        using SegAiSession session = SegAiSession.Create(root);
        var store = new SegProjectStore(session.Paths);
        Directory.CreateDirectory(store.EasyAiDir);
        string file = Path.Combine(store.EasyAiDir, "advanced.json");
        if (File.Exists(file)) File.Delete(file);

        Assert.Equal(SegInstanceQuality.Quality, store.LoadInstanceQuality()); // missing file
        File.WriteAllText(file, "{ not json");
        Assert.Equal(SegInstanceQuality.Quality, store.LoadInstanceQuality()); // invalid file
        File.WriteAllText(file, "{\"segClasses\":[]}");
        Assert.Equal(SegInstanceQuality.Quality, store.LoadInstanceQuality()); // old file without the key

        store.SaveInstanceQuality(SegInstanceQuality.Performance);
        Assert.Equal(SegInstanceQuality.Performance, store.LoadInstanceQuality());
        Assert.Contains("segClasses", File.ReadAllText(file)); // other keys survive the save
    }

    [Fact]
    public void Settings_InstanceHeadKeysOnlyWhenRequested()
    {
        var options = new SegTrainingOptions();
        string plain = SegAiTrainingPipeline.BuildV2SettingsJson(options, "C:\\m\\b.onnx", "C:\\w", false, new int[0], false, null, false, new int[0], 0);
        string explicitOff = SegAiTrainingPipeline.BuildV2SettingsJson(options, "C:\\m\\b.onnx", "C:\\w", false, new int[0], false, null, false, new int[0], 0, false);
        Assert.Equal(plain, explicitOff);
        Assert.DoesNotContain("instanceHead", plain);

        string head = SegAiTrainingPipeline.BuildV2SettingsJson(options, "C:\\m\\b.onnx", "C:\\w", false, new int[0], false, null, false, new int[0], 0, true);
        Assert.Contains("\"instanceHead\":1", head);
        Assert.Contains("\"angleSymmetry\":1", head);
    }

    [Fact]
    public void Position_SetAnnotations_KeepsCircleAndStaysValid()
    {
        string root = TrainFixtures.Folder("eai-pos-circle");
        TrainFixtures.WriteImage(root, "a.png", 1, 160, 120);
        string json = Path.Combine(root, "a.json");
        using (SegAiSession first = SegAiSession.Create(root))
        {
            first.SetAnnotations("a.png", new List<SegAnnotation> { new SegAnnotation { Label = 1, Points = Box(10, 10, 40, 40) } });
            ProjectKindStore.Write(first.Paths, ProjectKind.Position);
        }

        // A circle drawn in Position mode is in the document but never a region.
        LabelMeDocument doc = new LabelMeIO().Read(json);
        var circle = new LabelShape { Label = "NG", ShapeType = "circle" };
        circle.Points.Add(new[] { 100.0, 60.0 });
        circle.Points.Add(new[] { 120.0, 60.0 });
        doc.Shapes.Add(circle);
        new LabelMeIO().Write(doc, json);

        using SegAiSession session = SegAiSession.Open(root);
        session.SetAnnotations("a.png", new List<SegAnnotation> { new SegAnnotation { Label = 2, Points = Box(50, 10, 90, 40) } });

        LabelMeDocument after = new LabelMeIO().Read(json);
        Assert.Single(after.Shapes, s => s.ShapeType == "circle");
        SegAiSampleInfo info = Assert.Single(session.Samples);
        Assert.False(info.Stale);
        Assert.Equal(2, info.AnnotationCount); // one rectangle region and the circle
    }
}
