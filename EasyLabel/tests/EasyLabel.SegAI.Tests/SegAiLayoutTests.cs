using EasyLabel.Core;
using EasyLabel.Core.Io;
using EasyLabel.Core.Model;
using EasyLabel.SegAI.Data;
using Newtonsoft.Json.Linq;
using OpenCvSharp;
using PointF = System.Drawing.PointF;

namespace EasyLabel.SegAI.Tests;

/// <summary>Single .easyai layout: mode in LabelMe flags, cache relocation and regeneration, holes extension, legacy open.</summary>
public sealed class SegAiLayoutTests
{
    private static string NewFolder(string name)
    {
        string dir = Path.Combine(SegAiFixtures.ScratchFolder("layout"), name + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void WriteImage(string folder, string fileName)
    {
        using var image = new Mat(120, 160, MatType.CV_8UC3, new Scalar(30, 60, 90));
        Assert.True(Cv2.ImWrite(Path.Combine(folder, fileName), image));
    }

    private static PointF[] Box(float x0, float y0, float x1, float y1)
    {
        return new[] { new PointF(x0, y0), new PointF(x1, y0), new PointF(x1, y1), new PointF(x0, y1) };
    }

    private static SegAnnotation WithHole()
    {
        return new SegAnnotation { Label = 1, Points = Box(20, 20, 100, 80), Holes = new[] { Box(40, 40, 60, 60) } };
    }

    private static SegAnnotation Plain()
    {
        return new SegAnnotation { Label = 2, Points = Box(110, 10, 150, 50) };
    }

    private static string Prepare(string name, params SegAnnotation[] regions)
    {
        string root = NewFolder(name);
        WriteImage(root, "a.png");
        using SegAiSession session = SegAiSession.Create(root);
        session.SetAnnotations("a.png", regions);
        return root;
    }

    [Fact]
    public void NewProject_UsesTheSingleEasyAiFolder()
    {
        string root = NewFolder("fresh");
        using (SegAiSession.Create(root)) { }

        string[] top = Directory.GetFileSystemEntries(root).Select(Path.GetFileName).OrderBy(n => n).ToArray()!;
        Assert.Contains(".easyai", top);
        Assert.DoesNotContain(".easylabel", top);
        string[] inside = Directory.GetFileSystemEntries(Path.Combine(root, ".easyai")).Select(Path.GetFileName).OrderBy(n => n).ToArray()!;
        Assert.Equal(new[] { "cache", "models", "project.json", "runs" }, inside);
        Assert.Equal(2, JObject.Parse(File.ReadAllText(ProjectLayout.ProjectFile(root))).Value<int>("schema"));
    }

    [Fact]
    public void SetMode_WritesTrainValFlags_AndKeepsEverythingElse()
    {
        string root = Prepare("mode", WithHole(), Plain());
        string jsonPath = Path.Combine(root, "a.json");
        var io = new LabelMeIO();
        LabelMeDocument seeded = io.Read(jsonPath);
        seeded.Flags["reviewed"] = true;
        io.Write(seeded, jsonPath);

        using SegAiSession session = SegAiSession.Open(root);
        session.SetMode("a.png", SegTrainingMode.Val);
        LabelMeDocument val = io.Read(jsonPath);
        Assert.True(val.Flags["val"]);
        Assert.False(val.Flags.ContainsKey("train"));
        Assert.True(val.Flags["reviewed"]);
        Assert.Equal(2, val.Shapes.Count);
        Assert.Equal(SegTrainingMode.Val, session.Samples.Single().Mode);

        session.SetMode("a.png", SegTrainingMode.Train);
        LabelMeDocument train = io.Read(jsonPath);
        Assert.True(train.Flags["train"]);
        Assert.False(train.Flags.ContainsKey("val"));
        Assert.Equal(SegTrainingMode.Train, session.Samples.Single().Mode);

        session.SetMode("a.png", SegTrainingMode.Auto);
        LabelMeDocument auto = io.Read(jsonPath);
        Assert.False(auto.Flags.ContainsKey("train"));
        Assert.False(auto.Flags.ContainsKey("val"));
        Assert.True(auto.Flags["reviewed"]);
        Assert.Equal(SegTrainingMode.Train, session.Samples.Single().Mode); // Auto resolves to Train for a labelled image
        Assert.False(session.Samples.Single().Stale);
    }

    [Fact]
    public void Mode_IsReadFromLabelmeStyleJson_AndForeignKeysSurviveASave()
    {
        string root = NewFolder("labelme");
        WriteImage(root, "a.png");
        using (SegAiSession.Create(root)) { }
        File.WriteAllText(Path.Combine(root, "a.json"), @"{
  ""version"": ""5.5.0"",
  ""flags"": { ""val"": true, ""defective"": false },
  ""shapes"": [ { ""label"": ""NG"", ""points"": [[10,10],[50,10],[50,50]], ""group_id"": null, ""shape_type"": ""polygon"", ""flags"": {}, ""other_tool_key"": 7 } ],
  ""imagePath"": ""a.png"",
  ""imageData"": null,
  ""imageHeight"": 120,
  ""imageWidth"": 160,
  ""custom_top_level"": { ""keep"": true }
}");

        using SegAiSession session = SegAiSession.Open(root);
        Assert.Equal(SegTrainingMode.Val, session.Samples.Single().Mode);

        session.SetMode("a.png", SegTrainingMode.Train);

        JObject json = JObject.Parse(File.ReadAllText(Path.Combine(root, "a.json")));
        Assert.True(json["flags"]!.Value<bool>("train"));
        Assert.False(json["flags"]!.Value<bool>("defective"));
        Assert.Null(json["flags"]!["val"]);
        Assert.True(json["custom_top_level"]!.Value<bool>("keep"));
        Assert.Equal(7, json["shapes"]![0]!.Value<int>("other_tool_key"));
    }

    [Fact]
    public void SetMode_Val_IsReflectedInSamplesAfterReopen()
    {
        string root = Prepare("split", Plain());
        using (SegAiSession first = SegAiSession.Open(root)) first.SetMode("a.png", SegTrainingMode.Val);

        using SegAiSession session = SegAiSession.Open(root);
        SegAiSampleInfo info = session.Samples.Single();

        Assert.Equal(SegTrainingMode.Val, info.Mode);
        Assert.Equal(1, info.AnnotationCount);
    }

    [Fact]
    public void Cache_LivesUnderEasyAiCache_AndIsRegeneratedFromTheJsonAfterDeletion()
    {
        string root = Prepare("regen", WithHole(), Plain());
        var expected = new List<SegAnnotation> { WithHole(), Plain() };
        Assert.True(File.Exists(Path.Combine(root, ".easyai", "cache", "a.seg.json")));
        Assert.True(File.Exists(Path.Combine(root, ".easyai", "cache", "a.labels.png")));

        Directory.Delete(Path.Combine(root, ".easyai", "cache"), true);

        using SegAiSession session = SegAiSession.Open(root);
        SegAiSessionTests.AssertSame(expected, session.GetAnnotations("a.png"));
        SegAiSampleInfo info = session.Samples.Single();
        Assert.Equal(2, info.AnnotationCount);
        Assert.Equal(SegTrainingMode.Train, info.Mode);

        // The hole also reaches the editor and survives the next save, which rebuilds the cache.
        SegAiEditDocument edit = session.LoadForEdit("a.png");
        Assert.NotNull(edit.Holes);
        session.SaveEditedShapes("a.png", edit.Document, edit.Sidecar, shape => edit.Holes.TryGet(shape, out PointF[][] h) ? h : null);
        Assert.True(File.Exists(Path.Combine(root, ".easyai", "cache", "a.seg.json")));
        SegAiSessionTests.AssertSame(expected, session.GetAnnotations("a.png"));
        Assert.False(session.Samples.Single().Stale);
    }

    [Fact]
    public void HolesExtension_IsWrittenOnlyForShapesWithHoles()
    {
        string root = Prepare("ext", WithHole(), Plain());

        JObject json = JObject.Parse(File.ReadAllText(Path.Combine(root, "a.json")));
        JArray shapes = (JArray)json["shapes"]!;

        Assert.Equal(2, shapes.Count);
        JToken? withHole = shapes.Single(s => s["easyai"] != null);
        Assert.Single((JArray)withHole["easyai"]!["holes"]!);
        Assert.Equal(1, shapes.Count(s => s["easyai"] != null));
    }

    [Fact]
    public void HolesExtension_IsIgnoredWhenTheShapeWasMovedByAnotherTool()
    {
        string root = Prepare("moved", WithHole());
        string jsonPath = Path.Combine(root, "a.json");
        var io = new LabelMeIO();
        LabelMeDocument document = io.Read(jsonPath);
        document.Shapes[0].Points[0][0] += 3; // moved in another editor, its easyai object is left behind
        io.Write(document, jsonPath);
        Directory.Delete(Path.Combine(root, ".easyai", "cache"), true);

        using SegAiSession session = SegAiSession.Open(root);
        IReadOnlyList<SegAnnotation> regions = session.GetAnnotations("a.png");

        Assert.Single(regions);
        Assert.Empty(regions[0].Holes);
        Assert.Equal(23f, regions[0].Points[0].X);
    }

    [Fact]
    public void ShapeWithoutHoles_CarriesNoExtensionKey()
    {
        string root = Prepare("plain", Plain());

        Assert.DoesNotContain("easyai", File.ReadAllText(Path.Combine(root, "a.json")), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EditedShape_RemovesItsExtension_WhenTheHoleIsDropped()
    {
        string root = Prepare("drop", WithHole());
        using SegAiSession session = SegAiSession.Open(root);
        SegAiEditDocument edit = session.LoadForEdit("a.png");

        session.SaveEditedShapes("a.png", edit.Document, edit.Sidecar, _ => null);

        Assert.DoesNotContain("holes", File.ReadAllText(Path.Combine(root, "a.json")), StringComparison.OrdinalIgnoreCase);
        Assert.Empty(session.GetAnnotations("a.png")[0].Holes);
    }

    [Fact]
    public void SaveEditedShapes_KeepsTheStoredModeEvenWhenTheEditorCopyIsOlder()
    {
        string root = Prepare("race", Plain());
        using SegAiSession session = SegAiSession.Open(root);
        SegAiEditDocument edit = session.LoadForEdit("a.png"); // editor holds a copy without flags
        session.SetMode("a.png", SegTrainingMode.Val);

        session.SaveEditedShapes("a.png", edit.Document, edit.Sidecar, _ => null);

        Assert.Equal(SegTrainingMode.Val, session.Samples.Single().Mode);
    }

    [Fact]
    public void AnnotationFiles_ListTheNewLayoutPaths()
    {
        string root = Prepare("files", Plain());
        using SegAiSession session = SegAiSession.Open(root);
        new SidecarIO().Write(new EasyLabelSidecar { ReviewStatus = "reviewed" }, ProjectLayout.MetaSidecarPath(root, "a.png"));

        IReadOnlyList<string> files = session.AnnotationFiles("a.png");

        Assert.Contains(Path.Combine(root, "a.json"), files);
        Assert.Contains(ProjectLayout.MetaSidecarPath(root, "a.png"), files);
        Assert.Contains(ProjectLayout.CachePaths(root, "a.png").Sidecar, files);
        Assert.Contains(ProjectLayout.CachePaths(root, "a.png").Labels, files);
    }

    [Fact]
    public void OpeningALegacyProject_Migrates_AndKeepsModeAndHoles()
    {
        string root = Prepare("legacy", WithHole(), Plain());

        // Turn the project into what the previous build wrote: .easyai/seg with the mode inside, schema 1, sidecar beside the image.
        Directory.Move(Path.Combine(root, ".easyai", "cache"), Path.Combine(root, ".easyai", "seg"));
        string seg = Path.Combine(root, ".easyai", "seg", "a.seg.json");
        JObject sidecar = JObject.Parse(File.ReadAllText(seg));
        sidecar["Mode"] = 2;
        File.WriteAllText(seg, sidecar.ToString());
        File.WriteAllText(ProjectLayout.ProjectFile(root), "{\"schema\":1,\"createdUtc\":\"2026-01-01T00:00:00Z\",\"keepMe\":true}");
        new SidecarIO().Write(new EasyLabelSidecar { ReviewStatus = "reviewed" }, Path.Combine(root, "a.easylabel.json"));
        var io = new LabelMeIO();
        LabelMeDocument document = io.Read(Path.Combine(root, "a.json"));
        document.Flags.Remove("train");
        document.Flags.Remove("val");
        io.Write(document, Path.Combine(root, "a.json"));

        using SegAiSession session = SegAiSession.Open(root);

        Assert.True(session.Migration.Needed);
        Assert.True(session.Migration.Completed, string.Join("; ", session.Migration.Errors));
        Assert.Equal(SegTrainingMode.Val, session.Samples.Single().Mode);
        Assert.True(io.Read(Path.Combine(root, "a.json")).Flags["val"]);
        Assert.False(session.Samples.Single().Stale);
        SegAiSessionTests.AssertSame(new List<SegAnnotation> { WithHole(), Plain() }, session.GetAnnotations("a.png"));
        Assert.Equal("reviewed", session.LoadForEdit("a.png").Sidecar.ReviewStatus);
        Assert.True(JObject.Parse(File.ReadAllText(ProjectLayout.ProjectFile(root))).Value<bool>("keepMe"));
        Assert.False(Directory.Exists(Path.Combine(root, ".easyai", "seg")));
    }
}
