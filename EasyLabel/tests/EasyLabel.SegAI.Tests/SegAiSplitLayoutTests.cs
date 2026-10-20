using EasyLabel.Core;
using EasyLabel.SegAI.Data;
using EasyLabel.SegAI.Inspection;
using EasyLabel.SegAI.Project;
using OpenCvSharp;
using PointF = System.Drawing.PointF;

namespace EasyLabel.SegAI.Tests;

/// <summary>Image root and metadata folder are independent: a split layout keeps everything derived under the metadata folder.</summary>
public sealed class SegAiSplitLayoutTests
{
    private static string NewFolder(string name)
    {
        string dir = Path.Combine(SegAiFixtures.ScratchFolder("splitlayout"), name + "-" + Guid.NewGuid().ToString("N"));
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
        return new SegAnnotation { Label = 1, Points = Box(20, 20, 100, 80), Holes = new[] { Box(40, 40, 60, 60) } };
    }

    [Fact]
    public void LegacyStore_HasTheOriginalFolders()
    {
        string root = NewFolder("legacystore");
        var store = new SegProjectStore(root);

        Assert.Equal(Path.Combine(root, ".easyai"), store.EasyAiDir);
        Assert.Equal(Path.Combine(root, ".easyai", "cache"), store.CacheDir);
        Assert.Equal(Path.Combine(root, ".easyai", "models"), store.ModelsDir);
        Assert.Equal(Path.Combine(root, ".easyai", "runs"), store.RunsDir);
        Assert.Equal(ProjectLayout.CachePaths(root, "sub/a.png").Sidecar, store.SidecarPath("sub/a.png"));
        Assert.Equal(ProjectLayout.CachePaths(root, "sub/a.png").Labels, store.LabelsPath("sub/a.png"));
        Assert.Equal(root, store.ProjectRoot);
    }

    [Fact]
    public void EnsureLayout_CreatesTheSplitFoldersOnlyUnderTheMetadataFolder()
    {
        (string x, string y) = Outside("ensure");
        var store = new SegProjectStore(new ProjectPaths(x, y));

        store.EnsureLayout();

        Assert.Equal(new[] { "cache", "models", "project.json", "runs" },
            Directory.GetFileSystemEntries(y).Select(Path.GetFileName).OrderBy(n => n).ToArray()!);
        Assert.False(Directory.Exists(Path.Combine(x, ".easyai")));
        Assert.Empty(Directory.GetFileSystemEntries(x));
    }

    [Fact]
    public void WriteSample_RoundTripsWithTheSplitLayout()
    {
        (string x, string y) = Outside("sample");
        var store = new SegProjectStore(new ProjectPaths(x, y));

        store.WriteSample("sub/a.png", 160, 120, new List<SegAnnotation> { Region() }, 1, null, new List<EasyLabel.Core.Model.LabelShape>());

        Assert.Equal(Path.Combine(y, "cache", "sub", "a.seg.json"), store.SidecarPath("sub/a.png"));
        Assert.True(File.Exists(Path.Combine(y, "cache", "sub", "a.seg.json")));
        Assert.True(File.Exists(Path.Combine(y, "cache", "sub", "a.labels.png")));
        Assert.NotNull(store.ReadSidecar("sub/a.png"));
        using Mat? labels = store.ReadLabels("sub/a.png");
        Assert.NotNull(labels);
        Assert.False(Directory.Exists(Path.Combine(x, ".easyai")));
    }

    [Fact]
    public void Session_WithASplitLayout_KeepsJsonBesideTheImageAndEverythingElseInTheMetadataFolder()
    {
        (string x, string y) = Outside("session");
        WriteImage(x, "a.png");

        using (SegAiSession session = SegAiSession.Create(x, y))
        {
            Assert.Equal(x, session.ProjectRoot);
            Assert.Equal(y, session.Paths.MetaDir);
            session.SetAnnotations("a.png", new[] { Region() });
            Assert.Equal("a.png", Assert.Single(session.Samples).Rel);
        }

        Assert.True(File.Exists(Path.Combine(x, "a.json")));
        Assert.True(File.Exists(Path.Combine(y, "classes.txt")));
        Assert.False(File.Exists(Path.Combine(x, "classes.txt")));
        Assert.True(File.Exists(Path.Combine(y, "cache", "a.seg.json")));
        Assert.True(File.Exists(Path.Combine(y, "cache", "a.labels.png")));
        Assert.True(File.Exists(Path.Combine(y, "project.json")));
        Assert.False(Directory.Exists(Path.Combine(x, ".easyai")));
        string[] top = Directory.GetFileSystemEntries(x).Select(Path.GetFileName).OrderBy(n => n).ToArray()!;
        Assert.Equal(new[] { "a.json", "a.png" }, top);

        using SegAiSession reopened = SegAiSession.Open(x, y);
        SegAiSessionTests.AssertSame(new List<SegAnnotation> { Region() }, reopened.GetAnnotations("a.png"));
        Assert.Equal(
            new[] { Path.Combine(x, "a.json"), Path.Combine(y, "cache", "a.seg.json"), Path.Combine(y, "cache", "a.labels.png") },
            reopened.AnnotationFiles("a.png"));
    }

    [Fact]
    public void Session_Open_NeedsTheProjectFileInTheMetadataFolder()
    {
        (string x, string y) = Outside("openmissing");
        Directory.CreateDirectory(Path.Combine(x, ".easyai"));
        File.WriteAllText(Path.Combine(x, ".easyai", "project.json"), "{\"schema\":2}");

        Assert.Throws<FileNotFoundException>(() => SegAiSession.Open(x, y));
    }

    [Fact]
    public void Session_IgnoresMetadataFoldersInsideTheImageRoot()
    {
        string x = NewFolder("nested");
        string y = Path.Combine(x, "Proj.easyai.data");
        WriteImage(x, "a.png");
        WriteImage(Path.Combine(x, "Other.easyai.data"), "stray.png");
        WriteImage(Path.Combine(y, "cache"), "ghost.png");

        using SegAiSession session = SegAiSession.Create(x, y);
        session.SetAnnotations("a.png", new[] { Region() });

        Assert.Equal(new[] { "a.png" }, session.Samples.Select(s => s.Rel).ToArray());
        Assert.Throws<ArgumentException>(() => session.GetAnnotations("Other.easyai.data/stray.png"));
        Assert.False(Directory.Exists(Path.Combine(x, ".easyai")));
    }

    [Fact]
    public void Session_LegacyOpenStillUsesTheEasyAiFolder()
    {
        string root = NewFolder("legacysession");
        WriteImage(root, "a.png");

        using SegAiSession session = SegAiSession.Create(root);
        session.SetAnnotations("a.png", new[] { Region() });

        Assert.True(session.Paths.IsLegacyPlacement);
        Assert.Equal(Path.Combine(root, ".easyai"), session.Paths.MetaDir);
        Assert.True(File.Exists(Path.Combine(root, ".easyai", "cache", "a.seg.json")));
    }

    [Fact]
    public void InspectList_WithASplitLayout_IsStoredInTheMetadataFolderWithPathsRelativeToTheImageRoot()
    {
        (string x, string y) = Outside("inspect");
        string inside = Path.Combine(x, "sub", "a.png");
        Directory.CreateDirectory(Path.GetDirectoryName(inside)!);
        File.WriteAllBytes(inside, new byte[] { 1 });
        var store = new InspectListStore(new ProjectPaths(x, y));

        store.Save(new[] { inside });

        Assert.Equal(Path.Combine(y, "inspect.json"), store.FilePath);
        Assert.True(File.Exists(store.FilePath));
        Assert.Contains("\"sub/a.png\"", File.ReadAllText(store.FilePath));
        Assert.Equal(new[] { inside }, new InspectListStore(new ProjectPaths(x, y)).Load());
        Assert.False(Directory.Exists(Path.Combine(x, ".easyai")));
    }

    [Fact]
    public void InspectList_ScanFolder_SkipsEasyAiDataFolders()
    {
        string x = NewFolder("inspectscan");
        WriteImage(x, "a.png");
        WriteImage(Path.Combine(x, "Proj.easyai.data", "cache"), "ghost.png");

        IReadOnlyList<string> found = InspectListStore.ScanFolder(x);

        Assert.Equal(new[] { Path.Combine(x, "a.png") }, found);
    }
}
