using EasyLabel.Core;
using EasyLabel.Core.Io;
using EasyLabel.Core.Model;
using EasyLabel.Core.Projects;
using EasyLabel.SegAI.Data;
using EasyLabel.SegAI.Export;
using OpenCvSharp;
using PointF = System.Drawing.PointF;

namespace EasyLabel.SegAI.Tests;

/// <summary>Named projects on the Segment AI side: classes.txt in the metadata folder, metadata paths refused as images, split-layout saves and exports.</summary>
public sealed class SegAiNamedProjectTests
{
    private static string NewFolder(string name)
    {
        string dir = Path.Combine(SegAiFixtures.ScratchFolder("namedproject"), name + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return Path.GetFullPath(dir);
    }

    /// <summary>Image root X and a metadata folder Y outside X.</summary>
    private static (string X, string Y) Outside(string name)
    {
        return (NewFolder(name + "-img"), Path.Combine(NewFolder(name + "-meta"), "Proj.easyai.data"));
    }

    private static void WriteImage(string folder, string fileName)
    {
        Directory.CreateDirectory(folder);
        using var image = new Mat(120, 160, MatType.CV_8UC3, new Scalar(30, 60, 90));
        Assert.True(Cv2.ImWrite(Path.Combine(folder, fileName), image));
    }

    private static PointF[] Box(float x0, float y0, float x1, float y1)
    {
        return new[] { new PointF(x0, y0), new PointF(x1, y0), new PointF(x1, y1), new PointF(x0, y1) };
    }

    private static SegAnnotation Region()
    {
        return new SegAnnotation { Label = 1, Points = Box(20, 20, 100, 80) };
    }

    private static string[] Names(string folder)
    {
        return Directory.GetFileSystemEntries(folder).Select(p => Path.GetFileName(p)!).OrderBy(n => n, StringComparer.Ordinal).ToArray();
    }

    // ---- classes.txt placement ----

    [Fact]
    public void Create_ForANamedProject_WritesClassesTxtToTheMetadataFolderOnly()
    {
        (string x, string y) = Outside("classes");

        using (SegAiSession session = SegAiSession.Create(x, y))
        {
            Assert.Equal(Path.Combine(y, "classes.txt"), session.Paths.ClassesFile);
        }

        Assert.Equal(new[] { "NG", "OK" }, File.ReadAllLines(Path.Combine(y, "classes.txt")));
        Assert.Empty(Directory.GetFileSystemEntries(x));
    }

    [Fact]
    public void Create_ForALegacyProject_StillWritesClassesTxtBesideTheImages()
    {
        string root = NewFolder("legacyclasses");

        using (SegAiSession session = SegAiSession.Create(root))
        {
            Assert.Equal(Path.Combine(root, "classes.txt"), session.Paths.ClassesFile);
        }

        Assert.Equal(new[] { "NG", "OK" }, File.ReadAllLines(Path.Combine(root, "classes.txt")));
        Assert.False(File.Exists(Path.Combine(root, ".easyai", "classes.txt")));
    }

    [Fact]
    public void SaveClassTable_ForANamedProject_RefreshesTheClassesFileInTheMetadataFolder()
    {
        (string x, string y) = Outside("classtable");
        using SegAiSession session = SegAiSession.Create(x, y);

        session.SaveClassTable(new EasyLabel.SegAI.Model.SegAiClassTable(new[]
        {
            new EasyLabel.SegAI.Model.SegAiClassEntry("scratch", EasyLabel.SegAI.Model.SegAiClassKind.Defect, true),
            new EasyLabel.SegAI.Model.SegAiClassEntry("good", EasyLabel.SegAI.Model.SegAiClassKind.Good, true),
        }));

        string[] lines = File.ReadAllLines(Path.Combine(y, "classes.txt"));
        Assert.Contains("scratch", lines);
        Assert.Contains("good", lines);
        Assert.False(File.Exists(Path.Combine(x, "classes.txt")));
    }

    [Fact]
    public void ProjectManagerCreate_ThenSegAiSession_KeepsEverythingButTheImagesAndTheirJsonInTheDataFolder()
    {
        string location = NewFolder("manager");
        ProjectOpenResult created = new ProjectManager().Create("Seg", location, null);
        WriteImage(created.Paths.ImageRoot, "a.png");

        using (SegAiSession session = SegAiSession.Create(created.Paths))
        {
            Assert.Equal(created.Paths.ImageRoot, session.ProjectRoot);
            session.SetAnnotations("a.png", new[] { Region() });
            Assert.Single(session.Samples);
        }

        Assert.Equal(new[] { "a.json", "a.png" }, Names(created.Paths.ImageRoot));
        Assert.True(File.Exists(Path.Combine(created.Paths.MetaDir, "cache", "a.seg.json")));
        Assert.Equal(new[] { "NG", "OK" }, File.ReadAllLines(created.Paths.ClassesFile));
        Assert.Equal(new[] { "Seg.easyai", "Seg.easyai.data", "Seg.images" }, Names(location));
    }

    // ---- metadata paths are not images ----

    [Fact]
    public void ResolveRel_RejectsAnImagePathInsideTheMetadataFolder_WhateverItIsCalled()
    {
        string x = NewFolder("metaimage");
        string y = Path.Combine(x, "my-metadata");
        WriteImage(x, "a.png");
        WriteImage(Path.Combine(y, "cache"), "ghost.png");

        using SegAiSession session = SegAiSession.Create(x, y);

        Assert.Throws<ArgumentException>(() => session.GetAnnotations("my-metadata/cache/ghost.png"));
        Assert.Throws<ArgumentException>(() => session.SetAnnotations("my-metadata/cache/ghost.png", new[] { Region() }));
        Assert.Throws<ArgumentException>(() => session.SetMode("my-metadata/cache/ghost.png", SegTrainingMode.Val));
        Assert.Throws<ArgumentException>(() => session.LoadForEdit("my-metadata/cache/ghost.png"));
        Assert.Equal(new[] { "a.png" }, session.Samples.Select(s => s.Rel).ToArray());
        Assert.False(File.Exists(Path.Combine(y, "cache", "ghost.json")));
    }

    [Fact]
    public void AddImage_OfAFileInsideTheMetadataFolder_IsCopiedIntoTheProjectInsteadOfBeingListedInPlace()
    {
        string x = NewFolder("addmeta");
        string y = Path.Combine(x, "my-metadata");
        WriteImage(Path.Combine(y, "cache"), "ghost.png");

        using SegAiSession session = SegAiSession.Create(x, y);
        SegAiSampleInfo info = session.AddImage(Path.Combine(y, "cache", "ghost.png"));

        Assert.Equal("ghost.png", info.Rel);
        Assert.True(File.Exists(Path.Combine(x, "ghost.png")));
    }

    // ---- editor save ----

    [Fact]
    public void SaveEditedShapes_WithASplitLayout_WritesTheSidecarInTheMetadataFolderAndNothingInTheImageFolderBesideTheJson()
    {
        (string x, string y) = Outside("editsave");
        WriteImage(x, "a.png");
        using SegAiSession session = SegAiSession.Create(x, y);
        session.SetAnnotations("a.png", new[] { Region() });

        SegAiEditDocument edit = session.LoadForEdit("a.png");
        edit.Sidecar.ReviewStatus = "reviewed";
        session.SaveEditedShapes("a.png", edit.Document, edit.Sidecar, shape => edit.Holes.TryGet(shape, out PointF[][] holes) ? holes : null);

        Assert.True(File.Exists(Path.Combine(y, "meta", "a.json")));
        Assert.Equal("reviewed", new SidecarIO().Read(Path.Combine(y, "meta", "a.json")).ReviewStatus);
        Assert.False(Directory.Exists(Path.Combine(x, ".easyai")));
        Assert.Equal(new[] { "a.json", "a.png" }, Names(x));
        Assert.True(File.Exists(Path.Combine(y, "cache", "a.seg.json")));
        Assert.Single(session.GetAnnotations("a.png"));
    }

    // ---- export ----

    private static LabelMeDocument Document(string fileName)
    {
        LabelMeDocument document = new LabelMeDocument { ImagePath = fileName, ImageWidth = 160, ImageHeight = 120 };
        document.Shapes.Add(new LabelShape { Label = "NG", ShapeType = "rectangle", Points = { new[] { 10.0, 20.0 }, new[] { 50.0, 60.0 } } });
        ImageModeFlags.Apply(document.Flags, ImageModeFlags.Train);
        return document;
    }

    private static ExportResult Run(ProjectPaths paths, string output)
    {
        return new ExportService().Run(
            new ExportRequest { Paths = paths, Format = ExportFormat.YoloDetect, Set = ExportSet.All, OutputDirectory = output },
            null,
            CancellationToken.None);
    }

    [Fact]
    public void Export_WithASplitLayout_ReadsClassesFromTheMetadataFolder_AndLeavesTheProjectUntouched()
    {
        (string x, string y) = Outside("export");
        WriteImage(x, "a.png");
        new LabelMeIO().Write(Document("a.png"), Path.Combine(x, "a.json"));
        Directory.CreateDirectory(y);
        File.WriteAllText(Path.Combine(y, "classes.txt"), "NG\nOK\n");
        string output = Path.Combine(NewFolder("export-out"), "dataset");

        ExportResult result = Run(new ProjectPaths(x, y), output);

        Assert.Equal(1, result.ImagesExported);
        Assert.True(File.Exists(Path.Combine(output, "images", "train", "a.png")));
        Assert.Equal(new[] { "NG", "OK" }, File.ReadAllLines(Path.Combine(output, "classes.txt")).Select(l => l.Trim()).Where(l => l.Length > 0).ToArray());
        Assert.Contains("0: 'NG'", File.ReadAllText(Path.Combine(output, "data.yaml")));
        Assert.Equal(new[] { "a.json", "a.png" }, Names(x));
        Assert.False(Directory.Exists(Path.Combine(x, ".easyai")));
    }

    [Fact]
    public void Export_WithASplitLayout_NeedsTheClassesFileOfTheMetadataFolder()
    {
        (string x, string y) = Outside("export-noclasses");
        WriteImage(x, "a.png");
        new LabelMeIO().Write(Document("a.png"), Path.Combine(x, "a.json"));
        File.WriteAllText(Path.Combine(x, "classes.txt"), "NG\nOK\n"); // a stray file beside the images does not count
        string output = Path.Combine(NewFolder("export-noclasses-out"), "dataset");

        Assert.Throws<ExportException>(() => Run(new ProjectPaths(x, y), output));
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public void Export_RefusesAnOutputFolderThatIsContainsOrLiesInsideTheMetadataFolder()
    {
        (string x, string y) = Outside("export-meta");
        WriteImage(x, "a.png");
        new LabelMeIO().Write(Document("a.png"), Path.Combine(x, "a.json"));
        Directory.CreateDirectory(y);
        File.WriteAllText(Path.Combine(y, "classes.txt"), "NG\nOK\n");
        ProjectPaths paths = new ProjectPaths(x, y);
        string[] before = Names(y);

        Assert.Throws<ExportException>(() => Run(paths, y));
        Assert.Throws<ExportException>(() => Run(paths, Path.Combine(y, "export")));
        Assert.Throws<ExportException>(() => Run(paths, Path.Combine(y, "cache", "deep", "export")));
        Assert.Throws<ExportException>(() => Run(paths, Path.GetDirectoryName(y)!));

        Assert.Equal(before, Names(y));
        Assert.False(Directory.Exists(Path.Combine(y, "export")));
        Assert.Equal(new[] { "a.json", "a.png" }, Names(x));
    }
}
