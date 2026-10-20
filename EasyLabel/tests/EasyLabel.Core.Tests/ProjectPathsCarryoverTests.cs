using EasyLabel.Core.Batch;
using EasyLabel.Core.Convert;
using EasyLabel.Core.Migration;
using EasyLabel.Core.Model;
using Newtonsoft.Json.Linq;

namespace EasyLabel.Core.Tests;

/// <summary>classes.txt placement, the ProjectPaths overlap guard and the cross-volume migration fallback (named projects, slice P2).</summary>
public sealed class ProjectPathsCarryoverTests
{
    private static (string X, string Y) Split(string name)
    {
        return (ProjectTestSupport.NewFolder(name + "-img"), Path.Combine(ProjectTestSupport.NewFolder(name + "-meta"), "Proj.easyai.data"));
    }

    // ---- ProjectPaths constructor guard ----

    [Fact]
    public void Constructor_RejectsAMetadataFolderThatIsOrContainsTheImageRoot()
    {
        string x = ProjectTestSupport.NewFolder("guard");

        Assert.Throws<ArgumentException>(() => new ProjectPaths(x, x));
        Assert.Throws<ArgumentException>(() => new ProjectPaths(x, x + Path.DirectorySeparatorChar));
        Assert.Throws<ArgumentException>(() => new ProjectPaths(x, x.ToUpperInvariant()));
        Assert.Throws<ArgumentException>(() => new ProjectPaths(x, Path.Combine(x, "sub", "..")));
        Assert.Throws<ArgumentException>(() => new ProjectPaths(Path.Combine(x, "images"), x));
        Assert.Throws<ArgumentException>(() => new ProjectPaths(Path.Combine(x, "a", "b", "c"), Path.Combine(x, "a")));
    }

    [Fact]
    public void Constructor_AllowsMetadataInsideTheImageRootAndSiblingFolders()
    {
        string x = ProjectTestSupport.NewFolder("guard-ok");

        Assert.False(new ProjectPaths(x, Path.Combine(x, "Proj.easyai.data")).IsLegacyPlacement);
        Assert.False(new ProjectPaths(x, Path.Combine(x, "deep", "meta")).IsLegacyPlacement);
        Assert.False(new ProjectPaths(Path.Combine(x, "a"), Path.Combine(x, "b")).IsLegacyPlacement);
        Assert.False(new ProjectPaths(Path.Combine(x, "same"), Path.Combine(x, "same-meta")).IsLegacyPlacement);
        Assert.True(ProjectPaths.Legacy(x).IsLegacyPlacement);
    }

    // ---- classes.txt placement ----

    [Fact]
    public void ClassesFile_IsBesideTheImagesForTheLegacyPlacement_AndInTheMetadataFolderOtherwise()
    {
        string x = ProjectTestSupport.NewFolder("classesfile");
        string y = Path.Combine(ProjectTestSupport.NewFolder("classesfile-meta"), "Proj.easyai.data");

        Assert.Equal(Path.Combine(x, "classes.txt"), ProjectPaths.Legacy(x).ClassesFile);
        Assert.Equal(Path.Combine(y, "classes.txt"), new ProjectPaths(x, y).ClassesFile);
        Assert.Equal(Path.Combine(x, "Proj.easyai.data", "classes.txt"), new ProjectPaths(x, Path.Combine(x, "Proj.easyai.data")).ClassesFile);
    }

    [Fact]
    public void ProjectService_NamedProject_KeepsClassesInTheMetadataFolder_LegacyStaysBesideTheImages()
    {
        (string x, string y) = Split("service-classes");
        ProjectTestSupport.Write(Path.Combine(y, "classes.txt"), "screw\nbolt\n");
        ProjectTestSupport.WriteImage(x, "a.png", false);

        Project named = new ProjectService().Open(new ProjectPaths(x, y));

        Assert.Equal(new[] { "screw", "bolt" }, named.Classes);
        Assert.False(File.Exists(Path.Combine(x, "classes.txt")));
        Assert.Equal(new[] { "a.png" }, ProjectTestSupport.Names(x));

        string legacyRoot = ProjectTestSupport.NewFolder("service-classes-legacy");
        ProjectTestSupport.Write(Path.Combine(legacyRoot, "classes.txt"), "nut\n");
        Project legacy = new ProjectService().Open(legacyRoot);
        Assert.Equal(new[] { "nut" }, legacy.Classes);

        string emptyLegacy = ProjectTestSupport.NewFolder("service-classes-legacy-new");
        new ProjectService().Open(emptyLegacy);
        Assert.True(File.Exists(Path.Combine(emptyLegacy, "classes.txt")));
    }

    [Fact]
    public void ClassRemap_ForANamedProject_RewritesTheClassesFileInTheMetadataFolder()
    {
        (string x, string y) = Split("remap-classes");
        ProjectTestSupport.Write(Path.Combine(y, "classes.txt"), "screw\nbolt\n");
        ProjectTestSupport.WriteImage(x, "a.jpg", true, "screw");

        ClassRemapResult result = new ClassRemapService().Apply(new ClassRemapRequest
        {
            Paths = new ProjectPaths(x, y),
            Operation = ClassRemapOperation.Rename,
            SourceClass = "screw",
            TargetClass = "washer",
        });

        Assert.True(result.ClassesChanged);
        Assert.Equal(new[] { "washer", "bolt" }, File.ReadAllLines(Path.Combine(y, "classes.txt")));
        Assert.False(File.Exists(Path.Combine(x, "classes.txt")));
        Assert.Contains("washer", File.ReadAllText(Path.Combine(x, "a.json")));
    }

    [Fact]
    public void BulkLabelEdit_ForANamedProject_RewritesTheClassesFileInTheMetadataFolder()
    {
        (string x, string y) = Split("bulk-classes");
        ProjectTestSupport.Write(Path.Combine(y, "classes.txt"), "screw\nbolt\n");
        ProjectTestSupport.WriteImage(x, "a.jpg", true, "screw");

        BulkLabelEditResult result = new BulkLabelEditService().Apply(new BulkLabelEditRequest
        {
            Paths = new ProjectPaths(x, y),
            Operation = BulkLabelEditOperation.ReplaceLabel,
            SourceLabel = "screw",
            TargetLabel = "bolt",
        });

        Assert.True(result.ClassesChanged);
        Assert.Equal(new[] { "bolt" }, File.ReadAllLines(Path.Combine(y, "classes.txt")));
        Assert.False(File.Exists(Path.Combine(x, "classes.txt")));
    }

    [Fact]
    public void UltralyticsExport_ForANamedProject_ReadsClassesFromTheMetadataFolder()
    {
        (string x, string y) = Split("ultra-classes");
        ProjectTestSupport.Write(Path.Combine(y, "classes.txt"), "box\npart\n");
        ProjectTestSupport.WriteImage(x, "a.jpg", true, "box");
        string output = Path.Combine(ProjectTestSupport.NewFolder("ultra-classes-out"), "dataset");

        UltralyticsExportResult result = new UltralyticsExportService().Export(new UltralyticsExportRequest
        {
            Paths = new ProjectPaths(x, y),
            OutputDirectory = output,
            LabelFormat = LabelConvertFormat.YoloDetect,
            TrainRatio = 1.0,
            ValRatio = 0.0,
            TestRatio = 0.0,
        });

        Assert.Equal(1, result.TotalImages);
        string yaml = File.ReadAllText(Path.Combine(output, "data.yaml"));
        Assert.Contains("0: 'box'", yaml);
        Assert.Contains("1: 'part'", yaml);

        // Without the metadata classes.txt the export is refused, even if a stray one sits beside the images.
        File.Delete(Path.Combine(y, "classes.txt"));
        ProjectTestSupport.Write(Path.Combine(x, "classes.txt"), "stray\n");
        Assert.Throws<FileNotFoundException>(() => new UltralyticsExportService().Export(new UltralyticsExportRequest
        {
            Paths = new ProjectPaths(x, y),
            OutputDirectory = Path.Combine(ProjectTestSupport.NewFolder("ultra-classes-out2"), "dataset"),
            LabelFormat = LabelConvertFormat.YoloDetect,
            TrainRatio = 1.0,
            ValRatio = 0.0,
            TestRatio = 0.0,
        }));
    }

    // ---- migration archive fallback ----

    [Fact]
    public void Migration_WhenTheBackupFolderIsOnAnotherVolume_RenamesTheLegacyFolderInPlace()
    {
        (string x, string y) = Split("archive");
        ProjectTestSupport.WriteImage(x, "a.png", false);
        ProjectTestSupport.Write(Path.Combine(x, ".easylabel", "project.json"), "{\"legacyOnly\":\"keep-me\"}");
        ProjectTestSupport.Write(Path.Combine(x, ".easylabel", "recovery", "a.png.json"), "{\"recovered\":true}");
        ProjectPaths paths = new ProjectPaths(x, y);
        // Directory.Move refuses a move to another volume with an IOException; simulate that for every move into the backup folder.
        ProjectMigrationService service = new ProjectMigrationService
        {
            MoveDirectory = (source, target) =>
            {
                if (target.Contains(Path.DirectorySeparatorChar + "backup" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                {
                    throw new IOException("The source and destination path must have identical roots.");
                }

                Directory.Move(source, target);
            },
        };

        ProjectMigrationReport first = service.MigrateIfNeeded(paths);

        Assert.True(first.Needed);
        Assert.True(first.Completed, string.Join("; ", first.Errors));
        Assert.False(Directory.Exists(Path.Combine(x, ".easylabel")));
        string renamed = Path.Combine(x, ".easylabel.migrated");
        Assert.Equal(new[] { renamed }, first.ArchivedFolders);
        Assert.Equal("{\"legacyOnly\":\"keep-me\"}", File.ReadAllText(Path.Combine(renamed, "project.json")));
        Assert.True(File.Exists(Path.Combine(renamed, "recovery", "a.png.json")));
        Assert.True(File.Exists(Path.Combine(y, "recovery", "a.png.json")));
        Assert.Equal("keep-me", JObject.Parse(File.ReadAllText(Path.Combine(y, "project.json"))).Value<string>("legacyOnly"));

        // It no longer counts as legacy, so the next open does not migrate again.
        Assert.False(service.IsLegacy(paths));
        Assert.False(service.MigrateIfNeeded(paths).Needed);
        Assert.True(Directory.Exists(renamed));
    }

    [Fact]
    public void Migration_WhenEvenTheInPlaceRenameFails_ReportsTheErrorAndDeletesNothing()
    {
        (string x, string y) = Split("archive-fail");
        ProjectTestSupport.WriteImage(x, "a.png", false);
        ProjectTestSupport.Write(Path.Combine(x, ".easylabel", "project.json"), "{}");
        ProjectPaths paths = new ProjectPaths(x, y);
        ProjectMigrationService service = new ProjectMigrationService
        {
            MoveDirectory = (source, target) => throw new IOException("locked"),
        };

        ProjectMigrationReport report = service.MigrateIfNeeded(paths);

        Assert.False(report.Completed);
        Assert.Contains(report.Errors, e => e.Contains("locked", StringComparison.Ordinal));
        Assert.True(File.Exists(Path.Combine(x, ".easylabel", "project.json")));
    }
}
