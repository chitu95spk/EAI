using EasyLabel.Core.Io;
using EasyLabel.Core.Migration;
using EasyLabel.Core.Model;
using Newtonsoft.Json.Linq;

namespace EasyLabel.Core.Tests;

public sealed class ProjectMigrationTests
{
    private static string NewRoot(string name)
    {
        string root = Path.Combine(AppContext.BaseDirectory, "scratch", "migrate-" + name + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return Path.GetFullPath(root);
    }

    private static LabelMeDocument Document(string image, params KeyValuePair<string, bool>[] flags)
    {
        var document = new LabelMeDocument { ImagePath = image, ImageWidth = 100, ImageHeight = 80 };
        foreach (KeyValuePair<string, bool> flag in flags) document.Flags[flag.Key] = flag.Value;
        document.Shapes.Add(new LabelShape
        {
            Label = "NG",
            ShapeType = "polygon",
            Points = { new[] { 1.0, 1.0 }, new[] { 20.0, 1.0 }, new[] { 20.0, 20.0 } }
        });
        return document;
    }

    private static void WriteSegSidecar(string root, string stemRel, int mode)
    {
        string file = Path.Combine(root, ".easyai", "seg", stemRel.Replace('/', Path.DirectorySeparatorChar) + ".seg.json");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "{\"Schema\":1,\"ImageRel\":\"" + stemRel + ".png\",\"Mode\":" + mode + ",\"ShapesHash\":\"h\",\"Regions\":[]}");
        File.WriteAllBytes(Path.ChangeExtension(Path.ChangeExtension(file, null), ".labels.png"), new byte[] { 9, 9 });
    }

    /// <summary>Builds the layout written by the previous builds: .easylabel + name.easylabel.json + .easyai/seg with Mode.</summary>
    private static string BuildLegacyProject(string name)
    {
        string root = NewRoot(name);
        File.WriteAllLines(Path.Combine(root, "classes.txt"), new[] { "NG", "OK" });
        Directory.CreateDirectory(Path.Combine(root, "sub"));
        File.WriteAllBytes(Path.Combine(root, "a.png"), new byte[] { 1 });
        File.WriteAllBytes(Path.Combine(root, "sub", "b.png"), new byte[] { 1 });

        var labelMe = new LabelMeIO();
        labelMe.Write(Document("a.png", new KeyValuePair<string, bool>("other", true)), Path.Combine(root, "a.json"));
        labelMe.Write(Document("b.png"), Path.Combine(root, "sub", "b.json"));

        var sidecars = new SidecarIO();
        sidecars.Write(new EasyLabelSidecar { ReviewStatus = "reviewed" }, Path.Combine(root, "a.easylabel.json"));
        sidecars.Write(new EasyLabelSidecar { ReviewStatus = "in_progress" }, Path.Combine(root, "sub", "b.easylabel.json"));

        Directory.CreateDirectory(Path.Combine(root, ".easylabel", "recovery"));
        File.WriteAllText(
            Path.Combine(root, ".easylabel", "project.json"),
            "{\"Version\":\"1.0\",\"LastOpenedUtc\":\"2026-01-01T00:00:00Z\",\"legacyOnly\":\"keep-me\"}");
        File.WriteAllText(Path.Combine(root, ".easylabel", "recovery", "a.png.json"), "{\"recovered\":true}");

        Directory.CreateDirectory(Path.Combine(root, ".easyai", "models"));
        File.WriteAllText(
            Path.Combine(root, ".easyai", "project.json"),
            "{\"schema\":1,\"createdUtc\":\"2026-01-01T00:00:00Z\",\"unknownField\":{\"k\":1}}");
        File.WriteAllText(Path.Combine(root, ".easyai", "models", "registry.json"), "{\"schema\":1}");
        WriteSegSidecar(root, "a", 2);
        WriteSegSidecar(root, "sub/b", 1);
        return root;
    }

    private static string[] Archives(string folder, string pattern)
    {
        return Directory.GetDirectories(folder, pattern);
    }

    [Fact]
    public void Migrate_CopiesMergesConvertsModesAndArchivesOldFolders()
    {
        string root = BuildLegacyProject("full");

        ProjectMigrationReport report = new ProjectMigrationService().MigrateIfNeeded(root);

        Assert.True(report.Needed);
        Assert.True(report.Completed, string.Join("; ", report.Errors));
        Assert.Equal(2, report.SidecarsMoved);
        Assert.Equal(4, report.CacheFilesCopied);
        Assert.Equal(1, report.RecoveryFilesCopied);
        Assert.Equal(2, report.ModesConverted);
        Assert.Equal(2, report.ArchivedFolders.Count);

        // sidecars: copied into meta/, original kept as .migrated
        Assert.Equal("reviewed", new SidecarIO().Read(ProjectLayout.MetaSidecarPath(root, "a.png")).ReviewStatus);
        Assert.Equal("in_progress", new SidecarIO().Read(ProjectLayout.MetaSidecarPath(root, "sub/b.png")).ReviewStatus);
        Assert.False(File.Exists(Path.Combine(root, "a.easylabel.json")));
        Assert.NotEmpty(BackupFiles(root, "sidecars", "a.easylabel.json"));
        Assert.NotEmpty(BackupFiles(root, "sidecars", "b.easylabel.json"));

        // cache + recovery
        Assert.True(File.Exists(ProjectLayout.CachePaths(root, "a.png").Sidecar));
        Assert.True(File.Exists(ProjectLayout.CachePaths(root, "a.png").Labels));
        Assert.True(File.Exists(ProjectLayout.CachePaths(root, "sub/b.png").Sidecar));
        Assert.True(File.Exists(ProjectLayout.RecoveryPath(root, "a.png")));

        // old folders renamed, never deleted
        Assert.False(Directory.Exists(Path.Combine(root, ".easylabel")));
        Assert.False(Directory.Exists(Path.Combine(root, ".easyai", "seg")));
        string backup = Assert.Single(BackupDirs(root, "easylabel"));
        Assert.True(File.Exists(Path.Combine(backup, "project.json")));
        Assert.True(File.Exists(Path.Combine(backup, "recovery", "a.png.json")));
        string segBackup = Assert.Single(BackupDirs(root, "seg"));
        Assert.True(File.Exists(Path.Combine(segBackup, "a.seg.json")));

        // merged project.json: schema 2, both files' fields kept, provenance recorded
        JObject project = JObject.Parse(File.ReadAllText(ProjectLayout.ProjectFile(root)));
        Assert.Equal(2, project.Value<int>("schema"));
        Assert.Equal("keep-me", project.Value<string>("legacyOnly"));
        Assert.Equal(1, project["unknownField"]!.Value<int>("k"));
        Assert.Equal("1.0", project.Value<string>("Version"));
        Assert.Equal(2026, project.Value<DateTime>("createdUtc").Year);
        Assert.NotEmpty((JArray)project["migratedFrom"]!);
        Assert.NotNull(project["migratedUtc"]);
        Assert.True(File.Exists(Path.Combine(root, ".easyai", "models", "registry.json")));

        // mode -> LabelMe flags; the rest of the JSON is untouched
        LabelMeDocument a = new LabelMeIO().Read(Path.Combine(root, "a.json"));
        Assert.True(a.Flags["val"]);
        Assert.False(a.Flags.ContainsKey("train"));
        Assert.True(a.Flags["other"]);
        Assert.Single(a.Shapes);
        LabelMeDocument b = new LabelMeIO().Read(Path.Combine(root, "sub", "b.json"));
        Assert.True(b.Flags["train"]);
        Assert.False(b.Flags.ContainsKey("val"));
    }

    [Fact]
    public void Migrate_IsIdempotent()
    {
        string root = BuildLegacyProject("twice");
        var service = new ProjectMigrationService();
        service.MigrateIfNeeded(root);
        string projectBefore = File.ReadAllText(ProjectLayout.ProjectFile(root));
        string jsonBefore = File.ReadAllText(Path.Combine(root, "a.json"));

        ProjectMigrationReport second = service.MigrateIfNeeded(root);

        Assert.False(second.Needed);
        Assert.True(second.Completed);
        Assert.Equal(0, second.SidecarsMoved + second.CacheFilesCopied + second.ModesConverted);
        Assert.Equal(projectBefore, File.ReadAllText(ProjectLayout.ProjectFile(root)));
        Assert.Equal(jsonBefore, File.ReadAllText(Path.Combine(root, "a.json")));
        Assert.Single(BackupDirs(root, "easylabel"));
        Assert.False(service.IsLegacy(root));
    }

    [Fact]
    public void Migrate_NeverOverwritesExistingNewLayoutFiles()
    {
        string root = BuildLegacyProject("noclobber");
        string target = ProjectLayout.MetaSidecarPath(root, "a.png");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllText(target, "{\"reviewStatus\":\"new-layout-wins\"}");
        string cache = ProjectLayout.CachePaths(root, "a.png").Sidecar;
        Directory.CreateDirectory(Path.GetDirectoryName(cache)!);
        File.WriteAllText(cache, "sentinel");

        ProjectMigrationReport report = new ProjectMigrationService().MigrateIfNeeded(root);

        Assert.True(report.Completed, string.Join("; ", report.Errors));
        Assert.Equal("new-layout-wins", new SidecarIO().Read(target).ReviewStatus);
        Assert.Equal("sentinel", File.ReadAllText(cache));
        Assert.True(report.ExistingKept >= 2);
        Assert.NotEmpty(BackupFiles(root, "sidecars", "a.easylabel.json"));
    }

    [Fact]
    public void Migrate_BlockedTarget_ReportsErrorKeepsOldDataAndResumesLater()
    {
        string root = BuildLegacyProject("blocked");
        Directory.CreateDirectory(ProjectLayout.MetaDir(root));
        string cacheDir = ProjectLayout.CacheDir(root);
        File.WriteAllText(cacheDir, "i am a file where the cache folder should be");

        ProjectMigrationReport first = new ProjectMigrationService().MigrateIfNeeded(root);

        Assert.True(first.Needed);
        Assert.False(first.Completed);
        Assert.NotEmpty(first.Errors);
        Assert.Equal(0, first.CacheFilesCopied);
        Assert.Equal("i am a file where the cache folder should be", File.ReadAllText(cacheDir));
        Assert.True(File.Exists(Path.Combine(root, ".easyai", "seg", "a.seg.json")), "old seg data must stay in place");
        Assert.Empty(BackupDirs(root, "seg"));
        Assert.Equal(2, first.SidecarsMoved); // the independent steps still ran

        // The project still opens, and the notice carries the problem.
        Project opened = new ProjectService().Open(root);
        Assert.NotNull(opened.Migration);
        Assert.False(opened.Migration!.Completed);
        Assert.Equal(2, opened.Images.Count);

        // Unblock: the next run finishes the job.
        File.Delete(cacheDir);
        ProjectMigrationReport resumed = new ProjectMigrationService().MigrateIfNeeded(root);
        Assert.True(resumed.Completed, string.Join("; ", resumed.Errors));
        Assert.Equal(4, resumed.CacheFilesCopied);
        Assert.Single(BackupDirs(root, "seg"));
        Assert.True(File.Exists(ProjectLayout.CachePaths(root, "a.png").Sidecar));
    }

    [Fact]
    public void Migrate_DoesNotOverrideAFlagAlreadyDecidedInTheJson()
    {
        string root = BuildLegacyProject("decided");
        new LabelMeIO().Write(
            Document("a.png", new KeyValuePair<string, bool>("train", true)),
            Path.Combine(root, "a.json"));

        new ProjectMigrationService().MigrateIfNeeded(root);

        LabelMeDocument a = new LabelMeIO().Read(Path.Combine(root, "a.json"));
        Assert.True(a.Flags["train"]);
        Assert.False(a.Flags.ContainsKey("val"));
    }

    [Fact]
    public void Migrate_ModeWithoutJson_IsCountedNotInvented()
    {
        string root = BuildLegacyProject("nojson");
        File.Delete(Path.Combine(root, "sub", "b.json"));

        ProjectMigrationReport report = new ProjectMigrationService().MigrateIfNeeded(root);

        Assert.True(report.Completed);
        Assert.Equal(1, report.ModesConverted);
        Assert.Equal(1, report.ModesSkipped);
        Assert.False(File.Exists(Path.Combine(root, "sub", "b.json")));
    }

    [Fact]
    public void Migrate_OnlyLegacySidecars_IsDetectedAndMoved()
    {
        string root = NewRoot("sidecars-only");
        File.WriteAllBytes(Path.Combine(root, "a.png"), new byte[] { 1 });
        new SidecarIO().Write(new EasyLabelSidecar { ReviewStatus = "reviewed" }, Path.Combine(root, "a.easylabel.json"));

        Project project = new ProjectService().Open(root);

        Assert.True(project.Migration!.Needed);
        Assert.True(project.Migration.Completed);
        Assert.Equal(1, project.Migration.SidecarsMoved);
        ImageItem image = Assert.Single(project.Images);
        Assert.Equal(ProjectLayout.MetaSidecarPath(root, "a.png"), image.SidecarPath);
        Assert.Equal("reviewed", image.ReviewStatus);
        Assert.NotEmpty(project.Migration.Summary);
    }

    [Fact]
    public void Migrate_FreshFolder_DoesNothing()
    {
        string root = NewRoot("fresh");
        File.WriteAllBytes(Path.Combine(root, "a.png"), new byte[] { 1 });

        ProjectMigrationReport report = new ProjectMigrationService().MigrateIfNeeded(root);

        Assert.False(report.Needed);
        Assert.Equal(string.Empty, report.Summary);
        Assert.False(Directory.Exists(ProjectLayout.MetaDir(root)));
    }

    private static string[] BackupDirs(string root, string name)
    {
        string backup = Path.Combine(root, ".easyai", "backup");
        if (!Directory.Exists(backup)) return new string[0];
        return Directory.GetDirectories(backup).Select(d => Path.Combine(d, name)).Where(Directory.Exists).ToArray();
    }

    private static string[] BackupFiles(string root, string folder, string fileName)
    {
        string backup = Path.Combine(root, ".easyai", "backup");
        if (!Directory.Exists(backup)) return new string[0];
        return Directory.GetFiles(Path.Combine(backup), fileName, SearchOption.AllDirectories).Where(f => f.Contains(folder)).ToArray();
    }
}
