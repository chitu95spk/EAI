using EasyLabel.Core.Io;
using EasyLabel.Core.Model;
using EasyLabel.SegAI.Data;
using OpenCvSharp;
using PointF = System.Drawing.PointF;

namespace EasyLabel.SegAI.Tests;

public sealed class SegAiSessionTests
{
    private static string NewFolder(string name)
    {
        string dir = Path.Combine(SegAiFixtures.ScratchFolder("session"), name + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string WriteImage(string folder, string fileName, int shade, int width = 160, int height = 120)
    {
        string path = Path.Combine(folder, fileName);
        using var image = new Mat(height, width, MatType.CV_8UC3, new Scalar(30 + shade, 60, 90));
        Cv2.Circle(image, new OpenCvSharp.Point(20 + shade, 30), 6, new Scalar(255, 255, 255), -1);
        Assert.True(Cv2.ImWrite(path, image));
        return path;
    }

    private static PointF[] Box(float x0, float y0, float x1, float y1)
    {
        return new[] { new PointF(x0, y0), new PointF(x1, y0), new PointF(x1, y1), new PointF(x0, y1) };
    }

    private static SegAnnotation WithHole()
    {
        return new SegAnnotation { Label = 1, Points = Box(20, 20, 100, 80), Holes = new[] { Box(40, 40, 60, 60) } };
    }

    internal static void AssertSame(IReadOnlyList<SegAnnotation> expected, IReadOnlyList<SegAnnotation> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (int i = 0; i < expected.Count; i++)
        {
            Assert.Equal(expected[i].Label, actual[i].Label);
            Assert.Equal(expected[i].Kind, actual[i].Kind);
            Assert.Equal(expected[i].Points, actual[i].Points);
            Assert.Equal(expected[i].Holes.Length, actual[i].Holes.Length);
            for (int h = 0; h < expected[i].Holes.Length; h++) Assert.Equal(expected[i].Holes[h], actual[i].Holes[h]);
        }
    }

    [Fact]
    public void SetActiveModel_ModelThatDoesNotLoad_LeavesRegistryActiveUnchanged()
    {
        string root = NewFolder("setactive");
        using var session = SegAiSession.Create(root);
        var registry = new EasyLabel.SegAI.Model.SegAiRegistry(Path.Combine(root, ".easyai", "models"));
        string produced = Path.Combine(root, "candidate.segai");
        ModelStub.Write(produced);
        EasyLabel.SegAI.Model.SegAiRegistryEntry entry = registry.StageCandidate(produced, "stub", "fp", _ => null);

        Assert.ThrowsAny<Exception>(() => session.SetActiveModel(entry.File));

        Assert.Null(session.ActiveModel);
        registry.Load();
        Assert.Null(registry.Active);
    }

    [Theory]
    [InlineData("CON.png", "_CON")]
    [InlineData("nul.bmp", "_nul")]
    [InlineData("Com1.png", "_Com1")]
    [InlineData("lpt9.png", "_lpt9")]
    [InlineData("AUX.tar.png", "_AUX.tar")]
    [InlineData("COM0.png", "COM0")]
    [InlineData("console.png", "console")]
    public void SafeStem_PrefixesWindowsReservedDeviceNames(string name, string expected)
    {
        Assert.Equal(expected, SegAiSession.SafeStem(name));
    }

    [Fact]
    public void Create_AddImage_SetAnnotations_Reopen_KeepsHoleAndJsonStaysPure()
    {
        string root = NewFolder("roundtrip");
        string source = WriteImage(NewFolder("source"), "a.png", 0);
        var regions = new List<SegAnnotation> { WithHole(), new SegAnnotation { Label = 2, Points = Box(110, 10, 150, 50) } };

        using (var session = SegAiSession.Create(root))
        {
            Assert.True(File.Exists(Path.Combine(root, "classes.txt")));
            SegAiSampleInfo info = session.AddImage(source);
            Assert.Equal("a.png", info.Rel);
            Assert.Equal(160, info.Width);
            Assert.Equal(120, info.Height);
            Assert.Equal(SegTrainingMode.Ignored, info.Mode);
            session.SetAnnotations("a.png", regions);
            session.SaveDataset();
        }

        string json = File.ReadAllText(Path.Combine(root, "a.json"));
        Assert.Contains("\"shape_type\": \"polygon\"", json);
        // Only the shape that has a hole carries the optional "easyai" extension; the plain shape stays a pure LabelMe shape.
        Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(json, "\"holes\"").Count);
        Assert.False(Directory.Exists(Path.Combine(root, ".easyai", "seg")));
        Assert.True(File.Exists(Path.Combine(root, ".easyai", "cache", "a.seg.json")));
        Assert.True(File.Exists(Path.Combine(root, ".easyai", "cache", "a.labels.png")));

        using (var reopened = SegAiSession.Open(root))
        {
            AssertSame(regions, reopened.GetAnnotations("a.png"));
            SegAiSampleInfo info = Assert.Single(reopened.Samples);
            Assert.Equal(2, info.AnnotationCount);
            Assert.Equal(SegTrainingMode.Train, info.Mode);
            Assert.True(info.HasLabelMeJson);
            Assert.False(info.Stale);

            reopened.SetMode("a.png", SegTrainingMode.Val);
            Assert.Equal(SegTrainingMode.Val, Assert.Single(reopened.Samples).Mode);
        }

        // A foreign edit of one point makes the sidecar stale: regions are re-derived from the JSON and lose the hole.
        var io = new LabelMeIO();
        string jsonPath = Path.Combine(root, "a.json");
        LabelMeDocument document = io.Read(jsonPath);
        document.Shapes[0].Points[0][0] += 1;
        io.Write(document, jsonPath);
        using (var edited = SegAiSession.Open(root))
        {
            SegAiSampleInfo info = Assert.Single(edited.Samples);
            Assert.True(info.Stale);
            IReadOnlyList<SegAnnotation> derived = edited.GetAnnotations("a.png");
            Assert.Equal(2, derived.Count);
            Assert.Empty(derived[0].Holes);
            Assert.Equal(21f, derived[0].Points[0].X);
            Assert.Equal(SegTrainingMode.Val, info.Mode);
        }
    }

    [Fact]
    public void SetAnnotations_KeepsForeignShapesAndFlags()
    {
        string root = NewFolder("foreign");
        WriteImage(root, "b.png", 3);
        var document = new LabelMeDocument { ImagePath = "b.png", ImageWidth = 160, ImageHeight = 120 };
        document.Flags["reviewed"] = true;
        document.Shapes.Add(new LabelShape
        {
            Label = "scratch",
            ShapeType = "polygon",
            Points = new List<double[]> { new[] { 1.0, 1.0 }, new[] { 9.0, 1.0 }, new[] { 9.0, 9.0 } },
        });
        new LabelMeIO().Write(document, Path.Combine(root, "b.json"));

        using var session = SegAiSession.Create(root);
        session.SetAnnotations("b.png", new[] { WithHole() });
        session.SetAnnotations("b.png", new[] { WithHole() });

        LabelMeDocument after = new LabelMeIO().Read(Path.Combine(root, "b.json"));
        Assert.True(after.Flags["reviewed"]);
        Assert.Contains(after.Shapes, s => s.Label == "scratch");
        Assert.Equal(1, after.Shapes.Count(s => s.Label == "NG"));
        Assert.False(session.Samples.Single().Stale);
        AssertSame(new[] { WithHole() }, session.GetAnnotations("b.png"));
    }

    [Fact]
    public void AddImage_NeverOverwritesDifferentFile_AndReusesIdentical()
    {
        string root = NewFolder("unique");
        string first = WriteImage(NewFolder("s1"), "same.png", 0);
        string second = WriteImage(NewFolder("s2"), "same.png", 9);
        using var session = SegAiSession.Create(root);

        Assert.Equal("same.png", session.AddImage(first).Rel);
        Assert.Equal("same_1.png", session.AddImage(second).Rel);
        Assert.Equal("same.png", session.AddImage(first).Rel);
        Assert.Equal(2, session.Samples.Count);
        Assert.Equal(File.ReadAllBytes(first), File.ReadAllBytes(Path.Combine(root, "same.png")));
        Assert.Equal(File.ReadAllBytes(second), File.ReadAllBytes(Path.Combine(root, "same_1.png")));
        Assert.Throws<ArgumentException>(() => session.GetAnnotations("../x.png"));
    }

    [Fact]
    public void Open_MissingProject_Throws()
    {
        string root = NewFolder("missing");
        Assert.Throws<FileNotFoundException>(() => SegAiSession.Open(root));
        Assert.Throws<DirectoryNotFoundException>(() => SegAiSession.Open(Path.Combine(root, "nope")));
    }

    [Fact]
    public void ImportThenExportEasyVisionDataset_RoundTrips()
    {
        string work = NewFolder("ev");
        string zipPath = Path.Combine(work, "in.zip");
        var entries = new List<EvDatasetEntry>();
        var expected = new Dictionary<string, List<SegAnnotation>>();
        for (int i = 0; i < 3; i++)
        {
            string imagePath = WriteImage(work, "src" + i + ".png", i * 5, 64, 64);
            var regions = new List<SegAnnotation>();
            regions.Add(i == 1
                ? new SegAnnotation { Label = 1, Points = Box(8, 8, 56, 56), Holes = new[] { Box(24, 24, 40, 40) } }
                : new SegAnnotation { Label = (byte)(i == 2 ? 2 : 1), Points = Box(4 + i, 4, 30 + i, 30) });
            entries.Add(new EvDatasetEntry("img" + i + ".bmp", null, null, File.ReadAllBytes(imagePath), null, regions, (byte)(i == 2 ? 2 : 0)));
            expected["img" + i] = regions;
        }

        EasyVisionDataset.WriteZip(zipPath, entries);

        string root = NewFolder("evproject");
        using var session = SegAiSession.Create(root);
        SegAiImportReport report = session.ImportEasyVisionDataset(zipPath);
        Assert.Equal(3, report.Imported);
        Assert.Equal(0, report.Skipped);
        Assert.Equal(3, session.Samples.Count);
        Assert.Equal(SegTrainingMode.Val, session.Samples.Single(s => s.Rel == "img2.png").Mode);

        // A second import finds the identical images and adds nothing.
        SegAiImportReport again = session.ImportEasyVisionDataset(zipPath);
        Assert.Equal(0, again.Imported);
        Assert.Equal(3, again.Skipped);

        WriteImage(root, "plain.png", 40);
        string exportPath = Path.Combine(work, "out.zip");
        SegAiExportReport exported = session.ExportEasyVisionDataset(exportPath);
        Assert.Equal(3, exported.Exported);
        Assert.Equal(1, exported.Skipped);

        IReadOnlyList<EvDatasetEntry> read = EasyVisionDataset.ReadZip(exportPath);
        Assert.Equal(3, read.Count);
        foreach (EvDatasetEntry entry in read)
        {
            string stem = Path.GetFileNameWithoutExtension(entry.Name);
            AssertSame(expected[stem], entry.Regions);
            Assert.NotNull(entry.MaskBytes);
        }

        Assert.Equal(2, read.Single(e => e.Name == "img2.png").Mode);
    }
}
