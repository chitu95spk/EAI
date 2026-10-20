using EasyLabel.Core.Batch;
using EasyLabel.Core.Io;
using EasyLabel.Core.Migration;
using EasyLabel.Core.Model;
using Newtonsoft.Json.Linq;

namespace EasyLabel.Core.Tests;

/// <summary>Image root and metadata folder as two independent values: the legacy placement is unchanged, a split placement is honoured everywhere.</summary>
public sealed class ProjectPathsTests
{
    private static string NewFolder(string name)
    {
        string folder = Path.Combine(AppContext.BaseDirectory, "scratch", "paths-" + name + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return Path.GetFullPath(folder);
    }

    /// <summary>Image root X and a metadata folder Y that is NOT inside X.</summary>
    private static (string X, string Y, ProjectPaths Paths) Split(string name)
    {
        string x = NewFolder(name + "-img");
        string y = Path.Combine(NewFolder(name + "-meta"), "Proj.easyai.data");
        return (x, y, new ProjectPaths(x, y));
    }

    private static void Touch(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[] { 1 });
    }

    [Fact]
    public void Legacy_EveryPathEqualsTheValueTheStaticLayoutAlwaysHad()
    {
        string root = NewFolder("legacy");
        string meta = Path.Combine(root, ".easyai");
        ProjectPaths paths = ProjectPaths.Legacy(root);

        Assert.Equal(root, paths.ImageRoot);
        Assert.Equal(meta, paths.MetaDir);
        Assert.True(paths.IsLegacyPlacement);
        Assert.Equal(Path.Combine(meta, "project.json"), paths.ProjectFile);
        Assert.Equal(Path.Combine(meta, "meta"), paths.MetaSubdir);
        Assert.Equal(Path.Combine(meta, "recovery"), paths.RecoveryDir);
        Assert.Equal(Path.Combine(meta, "cache"), paths.CacheDir);
        Assert.Equal(Path.Combine(meta, "models"), paths.ModelsDir);
        Assert.Equal(Path.Combine(meta, "runs"), paths.RunsDir);
        Assert.Equal(Path.Combine(meta, "ultra"), paths.UltraDir);
        Assert.Equal(Path.Combine(meta, "advanced.json"), paths.AdvancedFile);
        Assert.Equal(Path.Combine(meta, "agent-memory.md"), paths.AgentMemoryFile);
        Assert.Equal(Path.Combine(meta, "agent-outcomes.jsonl"), paths.AgentOutcomesFile);
        Assert.Equal(Path.Combine(meta, "inspect.json"), paths.InspectFile);
        Assert.Equal(Path.Combine(root, ".easylabel"), paths.LegacyMetaDir);
        Assert.Equal(Path.Combine(meta, "seg"), paths.LegacySegDir);
        Assert.Equal(Path.Combine(meta, "meta", "sub", "a.json"), paths.MetaSidecarPath("sub\\a.png"));
        Assert.Equal(Path.Combine(meta, "meta", "sub", "a.json"), paths.MetaSidecarPath(Path.Combine(root, "sub", "a.jpg")));
        Assert.Equal(Path.Combine(meta, "meta", "sub", "a.json"), paths.MetaSidecarPathForStem("sub/a"));
        Assert.Equal(Path.Combine(meta, "recovery", "sub", "a.png.json"), paths.RecoveryPath("sub/a.png"));
        (string seg, string labels) = paths.CachePaths("sub/a.png");
        Assert.Equal(Path.Combine(meta, "cache", "sub", "a.seg.json"), seg);
        Assert.Equal(Path.Combine(meta, "cache", "sub", "a.labels.png"), labels);
        Assert.Equal("sub/a.png", paths.NormalizeRel(Path.Combine(root, "sub", "a.png")));
        Assert.Equal("sub/a.png", paths.RelativeOf(Path.Combine(root, "sub", "a.png")));
        Assert.Equal(string.Empty, paths.RelativeOf(root));
        Assert.True(paths.IsMetadataPath(Path.Combine(meta, "meta", "a.json")));
        Assert.True(paths.IsMetadataPath(Path.Combine(root, ".easylabel", "recovery", "a.json")));
        Assert.False(paths.IsMetadataPath(Path.Combine(root, "sub", "a.json")));
        Assert.False(paths.IsMetadataPath(Path.Combine(root, "sub", ".easyai", "x.json")));
    }

    [Fact]
    public void StaticLayout_IsAThinWrapperOverLegacy()
    {
        string root = NewFolder("wrapper");
        ProjectPaths paths = ProjectPaths.Legacy(root);

        Assert.Equal(paths.MetaDir, ProjectLayout.MetaDir(root));
        Assert.Equal(paths.ProjectFile, ProjectLayout.ProjectFile(root));
        Assert.Equal(paths.MetaSubdir, ProjectLayout.MetaFolder(root));
        Assert.Equal(paths.RecoveryDir, ProjectLayout.RecoveryDir(root));
        Assert.Equal(paths.CacheDir, ProjectLayout.CacheDir(root));
        Assert.Equal(paths.ModelsDir, ProjectLayout.ModelsDir(root));
        Assert.Equal(paths.RunsDir, ProjectLayout.RunsDir(root));
        Assert.Equal(paths.AgentMemoryFile, ProjectLayout.AgentMemoryFile(root));
        Assert.Equal(paths.AgentOutcomesFile, ProjectLayout.AgentOutcomesFile(root));
        Assert.Equal(paths.InspectFile, ProjectLayout.InspectFile(root));
        Assert.Equal(paths.LegacyMetaDir, ProjectLayout.LegacyMetaDir(root));
        Assert.Equal(paths.LegacySegDir, ProjectLayout.LegacySegDir(root));
        Assert.Equal(paths.MetaSidecarPath("a.png"), ProjectLayout.MetaSidecarPath(root, "a.png"));
        Assert.Equal(paths.RecoveryPath("a.png"), ProjectLayout.RecoveryPath(root, "a.png"));
        Assert.Equal(paths.CachePaths("a.png"), ProjectLayout.CachePaths(root, "a.png"));
    }

    [Fact]
    public void Constructor_RejectsEmptyFolders_AndFullPathsTheInput()
    {
        string root = NewFolder("ctor");

        Assert.Throws<ArgumentException>(() => new ProjectPaths(" ", root));
        Assert.Throws<ArgumentException>(() => new ProjectPaths(root, ""));
        Assert.Throws<ArgumentException>(() => ProjectPaths.Legacy(""));
        ProjectPaths paths = new ProjectPaths(Path.Combine(root, "a", ".."), Path.Combine(root, "m", "..", "meta"));
        Assert.Equal(root, paths.ImageRoot);
        Assert.Equal(Path.Combine(root, "meta"), paths.MetaDir);
        Assert.False(paths.IsLegacyPlacement);
    }

    [Fact]
    public void SplitLayout_PlacesEveryMetadataPathUnderTheMetadataFolder_AndKeepsRelativePathsOnTheImageRoot()
    {
        (string x, string y, ProjectPaths paths) = Split("placement");

        Assert.Equal(x, paths.ImageRoot);
        Assert.Equal(y, paths.MetaDir);
        Assert.Equal(Path.Combine(y, "project.json"), paths.ProjectFile);
        Assert.Equal(Path.Combine(y, "meta", "sub", "a.json"), paths.MetaSidecarPath(Path.Combine(x, "sub", "a.png")));
        Assert.Equal(Path.Combine(y, "recovery", "sub", "a.png.json"), paths.RecoveryPath("sub/a.png"));
        Assert.Equal(Path.Combine(y, "cache", "sub", "a.seg.json"), paths.CachePaths("sub/a.png").Sidecar);
        Assert.Equal(Path.Combine(y, "inspect.json"), paths.InspectFile);
        Assert.Equal(Path.Combine(y, "agent-outcomes.jsonl"), paths.AgentOutcomesFile);
        Assert.Equal(Path.Combine(x, ".easylabel"), paths.LegacyMetaDir);
        Assert.Equal("sub/a.png", paths.NormalizeRel(Path.Combine(x, "sub", "a.png")));
        Assert.Throws<ArgumentException>(() => paths.NormalizeRel(Path.Combine(y, "meta", "a.json")));
        Assert.Throws<ArgumentException>(() => paths.MetaSidecarPath("..\\evil.png"));
        Assert.True(paths.IsMetadataPath(Path.Combine(y, "meta", "a.json")));
        Assert.False(paths.IsMetadataPath(Path.Combine(x, "a.json")));
    }

    [Fact]
    public void MetadataInsideTheImageRoot_IsSkippedByNameAndByLocation()
    {
        string x = NewFolder("inside");
        ProjectPaths named = new ProjectPaths(x, Path.Combine(x, "Proj.easyai.data"));
        ProjectPaths odd = new ProjectPaths(x, Path.Combine(x, "my-metadata"));

        Assert.True(named.IsMetadataPath(Path.Combine(x, "Proj.easyai.data", "meta", "a.json")));
        Assert.True(named.IsSkippedDirectory(Path.Combine(x, "Proj.easyai.data")));
        Assert.True(named.IsSkippedDirectory(Path.Combine(x, "Other.easyai.data")));
        Assert.True(odd.IsSkippedDirectory(Path.Combine(x, "my-metadata")));
        Assert.True(odd.IsMetadataPath(Path.Combine(x, "my-metadata", "meta", "a.json")));
        Assert.False(odd.IsSkippedDirectory(Path.Combine(x, "images")));
        Assert.False(odd.IsMetadataPath(Path.Combine(x, "images", "a.json")));
        Assert.True(ProjectLayout.IsMetadataPath("Proj.easyai.data/meta/a.json"));
        Assert.True(ProjectLayout.IsMetadataPath("sub/Proj.EASYAI.DATA/a.json"));
        Assert.False(ProjectLayout.IsMetadataPath("sub/easyai.data/a.json"));
    }

    [Fact]
    public void EnsureProjectFile_CreatesSchema2InTheMetadataFolderOnly_AndNeverOverwrites()
    {
        (string x, string y, ProjectPaths paths) = Split("ensure");

        paths.EnsureProjectFile();

        Assert.Equal(2, JObject.Parse(File.ReadAllText(Path.Combine(y, "project.json"))).Value<int>("schema"));
        Assert.False(Directory.Exists(Path.Combine(x, ".easyai")));
        File.WriteAllText(paths.ProjectFile, "{\"schema\":2,\"custom\":\"keep\"}");
        paths.EnsureProjectFile();
        Assert.Contains("keep", File.ReadAllText(paths.ProjectFile));
    }

    [Fact]
    public void ProjectService_OpensASplitProject_WritesOnlyUnderTheMetadataFolder_AndRoundTripsASidecar()
    {
        (string x, string y, ProjectPaths paths) = Split("service");
        Touch(Path.Combine(x, "a.png"));
        Touch(Path.Combine(x, "sub", "b.jpg"));

        Project project = new ProjectService().Open(paths);

        Assert.Same(paths, project.Paths);
        Assert.Equal(x, project.RootDir);
        Assert.Equal(2, project.Images.Count);
        Assert.True(File.Exists(Path.Combine(y, "project.json")));
        Assert.NotNull(JObject.Parse(File.ReadAllText(Path.Combine(y, "project.json")))["LastOpenedUtc"]);
        Assert.False(Directory.Exists(Path.Combine(x, ".easyai")));
        Assert.True(File.Exists(Path.Combine(y, "classes.txt")));
        Assert.False(File.Exists(Path.Combine(x, "classes.txt")));

        ImageItem image = project.Images.Single(i => i.ImagePath.EndsWith("a.png", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(Path.Combine(y, "meta", "a.json"), image.SidecarPath);
        Assert.Equal(Path.Combine(x, "a.json"), image.JsonPath);
        Directory.CreateDirectory(Path.GetDirectoryName(image.SidecarPath)!);
        new SidecarIO().Write(new EasyLabelSidecar { ReviewStatus = "reviewed" }, image.SidecarPath);

        Project reopened = new ProjectService().Open(paths);
        Assert.Equal("reviewed", reopened.Images.Single(i => i.ImagePath.EndsWith("a.png", StringComparison.OrdinalIgnoreCase)).ReviewStatus);
        Assert.False(Directory.Exists(Path.Combine(x, ".easyai")));
    }

    [Fact]
    public void ProjectService_LegacyOpen_StillUsesTheEasyAiFolder()
    {
        string root = NewFolder("legacyopen");
        Touch(Path.Combine(root, "a.png"));

        Project project = new ProjectService().Open(root);

        Assert.True(project.Paths.IsLegacyPlacement);
        Assert.Equal(ProjectLayout.MetaSidecarPath(root, "a.png"), Assert.Single(project.Images).SidecarPath);
        Assert.True(File.Exists(Path.Combine(root, ".easyai", "project.json")));
    }

    [Fact]
    public void ProjectService_ImageEnumeration_IgnoresMetadataFoldersInsideTheImageRoot()
    {
        string x = NewFolder("enumerate");
        string y = Path.Combine(x, "Proj.easyai.data");
        Touch(Path.Combine(x, "a.png"));
        Touch(Path.Combine(x, "Other.easyai.data", "stray.png"));
        Touch(Path.Combine(x, "my-metadata", "hidden.png"));
        Touch(Path.Combine(x, "images", "c.bmp"));
        Touch(Path.Combine(y, "cache", "ghost.png"));
        ProjectPaths paths = new ProjectPaths(x, Path.Combine(x, "my-metadata"));

        Project project = new ProjectService().Open(paths);

        string[] names = project.Images.Select(i => Path.GetFileName(i.ImagePath)).OrderBy(n => n).ToArray();
        Assert.Equal(new[] { "a.png", "c.bmp" }, names);
    }

    [Fact]
    public void BatchScanners_SkipASplitMetadataFolderInsideTheImageRoot()
    {
        string x = NewFolder("batch");
        string y = Path.Combine(x, "Proj.easyai.data");
        ProjectPaths paths = new ProjectPaths(x, y);
        var labelMe = new LabelMeIO();
        var document = new LabelMeDocument { ImagePath = "a.png", ImageWidth = 10, ImageHeight = 10 };
        labelMe.Write(document, Path.Combine(x, "a.json"));
        Directory.CreateDirectory(Path.Combine(y, "meta"));
        File.WriteAllText(Path.Combine(y, "meta", "a.json"), "{\"reviewStatus\":\"reviewed\"}");
        File.WriteAllText(Path.Combine(y, "project.json"), "{\"schema\":2}");

        BulkLabelEditResult bulk = new BulkLabelEditService().Preview(new BulkLabelEditRequest
        {
            ProjectRoot = x,
            Paths = paths,
            Operation = BulkLabelEditOperation.DeleteZeroArea
        });
        ClassRemapResult remap = new ClassRemapService().Preview(new ClassRemapRequest
        {
            ProjectRoot = x,
            Paths = paths,
            Operation = ClassRemapOperation.Delete,
            SourceClass = "x"
        });

        Assert.Equal(1, bulk.FilesScanned);
        Assert.Equal(1, remap.FilesScanned);
    }

    [Fact]
    public void BatchScanners_WithAPathsOnlyRequest_UseTheImageRootOfThePaths()
    {
        (string x, string y, ProjectPaths paths) = Split("pathsonly");
        new LabelMeIO().Write(new LabelMeDocument { ImagePath = "a.png", ImageWidth = 10, ImageHeight = 10 }, Path.Combine(x, "a.json"));
        Directory.CreateDirectory(Path.Combine(y, "meta"));
        File.WriteAllText(Path.Combine(y, "meta", "a.json"), "{}");

        BulkLabelEditResult bulk = new BulkLabelEditService().Preview(new BulkLabelEditRequest
        {
            Paths = paths,
            Operation = BulkLabelEditOperation.DeleteZeroArea
        });

        Assert.Equal(1, bulk.FilesScanned);
    }

    [Fact]
    public void Migration_OfASplitProject_ReadsLegacyDataBesideTheImagesAndWritesIntoTheMetadataFolder()
    {
        (string x, string y, ProjectPaths paths) = Split("migrate");
        Touch(Path.Combine(x, "a.png"));
        new SidecarIO().Write(new EasyLabelSidecar { ReviewStatus = "reviewed" }, Path.Combine(x, "a.easylabel.json"));
        Directory.CreateDirectory(Path.Combine(x, ".easylabel", "recovery"));
        File.WriteAllText(Path.Combine(x, ".easylabel", "project.json"), "{\"legacyOnly\":\"keep-me\"}");
        File.WriteAllText(Path.Combine(x, ".easylabel", "recovery", "a.png.json"), "{\"recovered\":true}");

        ProjectMigrationReport report = new ProjectMigrationService().MigrateIfNeeded(paths);

        Assert.True(report.Needed);
        Assert.True(report.Completed, string.Join("; ", report.Errors));
        Assert.Equal(1, report.SidecarsMoved);
        Assert.Equal(1, report.RecoveryFilesCopied);
        Assert.True(File.Exists(Path.Combine(y, "meta", "a.json")));
        Assert.True(File.Exists(Path.Combine(y, "recovery", "a.png.json")));
        Assert.Equal("keep-me", JObject.Parse(File.ReadAllText(Path.Combine(y, "project.json"))).Value<string>("legacyOnly"));
        Assert.Equal(2, JObject.Parse(File.ReadAllText(Path.Combine(y, "project.json"))).Value<int>("schema"));
        Assert.False(Directory.Exists(Path.Combine(x, ".easyai")));
        Assert.False(Directory.Exists(Path.Combine(x, ".easylabel")));
        Assert.True(Directory.Exists(Path.Combine(y, "backup")));
        Assert.False(new ProjectMigrationService().IsLegacy(paths));
    }
}
