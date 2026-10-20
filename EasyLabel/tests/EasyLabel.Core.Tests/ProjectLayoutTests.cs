using EasyLabel.Core.Batch;
using EasyLabel.Core.Io;
using EasyLabel.Core.Model;
using Newtonsoft.Json.Linq;

namespace EasyLabel.Core.Tests;

public sealed class ProjectLayoutTests
{
    private static string NewRoot(string name)
    {
        string root = Path.Combine(AppContext.BaseDirectory, "scratch", "layout-" + name + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return Path.GetFullPath(root);
    }

    [Fact]
    public void Paths_AreUnderOneEasyAiFolder()
    {
        string root = NewRoot("paths");
        string meta = Path.Combine(root, ".easyai");

        Assert.Equal(meta, ProjectLayout.MetaDir(root));
        Assert.Equal(Path.Combine(meta, "project.json"), ProjectLayout.ProjectFile(root));
        Assert.Equal(Path.Combine(meta, "models"), ProjectLayout.ModelsDir(root));
        Assert.Equal(Path.Combine(meta, "runs"), ProjectLayout.RunsDir(root));
        Assert.Equal(Path.Combine(meta, "meta", "sub", "a.json"), ProjectLayout.MetaSidecarPath(root, "sub\\a.png"));
        Assert.Equal(Path.Combine(meta, "meta", "sub", "a.json"), ProjectLayout.MetaSidecarPath(root, Path.Combine(root, "sub", "a.jpg")));
        Assert.Equal(Path.Combine(meta, "recovery", "sub", "a.png.json"), ProjectLayout.RecoveryPath(root, "sub/a.png"));
        (string seg, string labels) = ProjectLayout.CachePaths(root, "sub/a.png");
        Assert.Equal(Path.Combine(meta, "cache", "sub", "a.seg.json"), seg);
        Assert.Equal(Path.Combine(meta, "cache", "sub", "a.labels.png"), labels);
    }

    [Fact]
    public void Paths_RejectTraversalAndEmpty()
    {
        string root = NewRoot("escape");

        Assert.Throws<ArgumentException>(() => ProjectLayout.MetaSidecarPath(root, "..\\evil.png"));
        Assert.Throws<ArgumentException>(() => ProjectLayout.RecoveryPath(root, "a/../../evil.png"));
        Assert.Throws<ArgumentException>(() => ProjectLayout.CachePaths(root, Path.Combine(root, "..", "evil.png")));
        Assert.Throws<ArgumentException>(() => ProjectLayout.MetaSidecarPath(root, " "));
    }

    [Theory]
    [InlineData(".easyai/project.json", true)]
    [InlineData(".easyai", true)]
    [InlineData(".EASYAI\\meta\\a.json", true)]
    [InlineData(".easylabel/project.json", true)]
    [InlineData(".easylabel.migrated-20260101-000000/recovery/a.json", true)]
    [InlineData("a.json", false)]
    [InlineData("sub/.easyai/x.json", false)]
    [InlineData("easyai/a.json", false)]
    public void IsMetadataPath_SkipsOnlyTheMetadataFolders(string relative, bool expected)
    {
        Assert.Equal(expected, ProjectLayout.IsMetadataPath(relative));
    }

    [Fact]
    public void EnsureProjectFile_CreatesSchema2_AndNeverOverwrites()
    {
        string root = NewRoot("ensure");

        ProjectLayout.EnsureProjectFile(root);
        JObject created = JObject.Parse(File.ReadAllText(ProjectLayout.ProjectFile(root)));
        Assert.Equal(2, created.Value<int>("schema"));

        File.WriteAllText(ProjectLayout.ProjectFile(root), "{\"schema\":2,\"custom\":\"keep\"}");
        ProjectLayout.EnsureProjectFile(root);
        Assert.Contains("keep", File.ReadAllText(ProjectLayout.ProjectFile(root)));
    }

    [Fact]
    public void NewProject_IsCreatedDirectlyInTheNewLayout()
    {
        string root = NewRoot("fresh");
        File.WriteAllBytes(Path.Combine(root, "a.png"), new byte[] { 1 });

        Project project = new ProjectService().Open(root);

        Assert.False(project.Migration!.Needed);
        Assert.True(File.Exists(ProjectLayout.ProjectFile(root)));
        Assert.False(Directory.Exists(Path.Combine(root, ".easylabel")));
        Assert.Equal(ProjectLayout.MetaSidecarPath(root, "a.png"), Assert.Single(project.Images).SidecarPath);
        JObject file = JObject.Parse(File.ReadAllText(ProjectLayout.ProjectFile(root)));
        Assert.Equal(2, file.Value<int>("schema"));
        Assert.NotNull(file["LastOpenedUtc"]);
    }

    [Fact]
    public void Open_KeepsUnknownProjectFileFields()
    {
        string root = NewRoot("keepfields");
        Directory.CreateDirectory(ProjectLayout.MetaDir(root));
        File.WriteAllText(ProjectLayout.ProjectFile(root), "{\"schema\":2,\"activeModel\":\"v003.segai\",\"options\":{\"a\":1}}");

        new ProjectService().Open(root);

        JObject file = JObject.Parse(File.ReadAllText(ProjectLayout.ProjectFile(root)));
        Assert.Equal("v003.segai", file.Value<string>("activeModel"));
        Assert.Equal(1, file["options"]!.Value<int>("a"));
    }

    [Fact]
    public void BatchScanners_SkipEveryMetadataFolder()
    {
        string root = NewRoot("scan");
        LabelMeDocument document = new LabelMeDocument { ImagePath = "a.png", ImageWidth = 10, ImageHeight = 10 };
        new LabelMeIO().Write(document, Path.Combine(root, "a.json"));
        Directory.CreateDirectory(Path.Combine(root, ".easyai", "meta"));
        File.WriteAllText(Path.Combine(root, ".easyai", "project.json"), "{\"schema\":2}");
        File.WriteAllText(Path.Combine(root, ".easyai", "meta", "a.json"), "{\"reviewStatus\":\"reviewed\"}");
        Directory.CreateDirectory(Path.Combine(root, ".easylabel.migrated-1", "recovery"));
        File.WriteAllText(Path.Combine(root, ".easylabel.migrated-1", "recovery", "a.json"), "{}");
        File.WriteAllText(Path.Combine(root, "old.easylabel.json.migrated"), "{}");

        BulkLabelEditResult result = new BulkLabelEditService().Preview(new BulkLabelEditRequest
        {
            ProjectRoot = root,
            Operation = BulkLabelEditOperation.DeleteZeroArea
        });
        Assert.Equal(1, result.FilesScanned);

        ClassRemapResult remap = new ClassRemapService().Preview(new ClassRemapRequest
        {
            ProjectRoot = root,
            Operation = ClassRemapOperation.Delete,
            SourceClass = "x"
        });
        Assert.Equal(1, remap.FilesScanned);
    }
}
