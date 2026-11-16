using EasyLabel.Core.Editing;
using EasyLabel.Core.Model;
using EasyLabel.Core.Projects;
using Newtonsoft.Json.Linq;

namespace EasyLabel.Core.Tests;

/// <summary>The locked project kind (Detect / Position) in project.json and the tool gate it drives.</summary>
public sealed class ProjectKindTests
{
    private static string NewFolder(string name)
    {
        string folder = Path.Combine(AppContext.BaseDirectory, "scratch", "kind-" + name + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return Path.GetFullPath(folder);
    }

    private static ProjectPaths NewPaths(string name)
    {
        return new ProjectPaths(NewFolder(name + "-img"), Path.Combine(NewFolder(name + "-meta"), "Proj.easyai.data"));
    }

    [Fact]
    public void ProjectKind_MissingKey_IsDetect()
    {
        ProjectPaths paths = NewPaths("missing");
        Assert.Equal(ProjectKind.Detect, ProjectKindStore.Read(paths)); // no project.json at all

        paths.EnsureProjectFile();
        string before = File.ReadAllText(paths.ProjectFile);
        Assert.Equal(ProjectKind.Detect, ProjectKindStore.Read(paths));
        Assert.Equal(before, File.ReadAllText(paths.ProjectFile)); // reading never rewrites

        File.WriteAllText(paths.ProjectFile, "{ \"projectKind\": \"banana\" }");
        Assert.Equal(ProjectKind.Detect, ProjectKindStore.Read(paths));
        File.WriteAllText(paths.ProjectFile, "not json");
        Assert.Equal(ProjectKind.Detect, ProjectKindStore.Read(paths));
    }

    [Fact]
    public void ProjectKind_UnknownKeysSurvive()
    {
        ProjectPaths paths = NewPaths("keys");
        Directory.CreateDirectory(paths.MetaDir);
        File.WriteAllText(paths.ProjectFile, "{ \"schema\": 2, \"futureKey\": { \"a\": [1, 2] } }");

        ProjectKindStore.Write(paths, ProjectKind.Position);

        JObject doc = JObject.Parse(File.ReadAllText(paths.ProjectFile));
        Assert.Equal("position", (string?)doc["projectKind"]);
        Assert.Equal(2, (int?)doc["schema"]);
        Assert.Equal(2, doc["futureKey"]!["a"]!.Count());
        Assert.Equal(ProjectKind.Position, ProjectKindStore.Read(paths));
        Assert.Empty(Directory.GetFiles(paths.MetaDir, "*.tmp"));
    }

    [Fact]
    public void ProjectKind_CounterAi_RoundTripsAndIsNotAnInstanceKind()
    {
        ProjectPaths paths = NewPaths("counterai");
        Directory.CreateDirectory(paths.MetaDir);
        File.WriteAllText(paths.ProjectFile, "{ \"schema\": 2 }");

        ProjectKindStore.Write(paths, ProjectKind.CounterAi);

        Assert.Equal("counterai", (string?)JObject.Parse(File.ReadAllText(paths.ProjectFile))["projectKind"]);
        Assert.Equal(2, (int?)JObject.Parse(File.ReadAllText(paths.ProjectFile))["schema"]);
        Assert.Equal(ProjectKind.CounterAi, ProjectKindStore.Read(paths));
        Assert.False(ProjectKind.CounterAi.IsInstanceKind());
        Assert.NotEqual(ProjectKind.Counter, ProjectKind.CounterAi);
    }

    [Fact]
    public void ProjectManager_CreateCounterAi_WritesProjectKindCounterAi()
    {
        ProjectOpenResult created = new ProjectManager().Create("Wires", NewFolder("mgr"), null, ProjectKind.CounterAi);
        Assert.Equal("counterai", (string?)JObject.Parse(File.ReadAllText(created.Paths.ProjectFile))["projectKind"]);
        Assert.Equal(ProjectKind.CounterAi, ProjectKindStore.Read(created.Paths));
        Assert.Equal(ProjectKind.CounterAi, created.Project.Kind);
    }

    [Fact]
    public void ProjectKind_Counter_RoundTripsAndUsesInstanceRules()
    {
        ProjectPaths paths = NewPaths("counter");
        Directory.CreateDirectory(paths.MetaDir);
        File.WriteAllText(paths.ProjectFile, "{ \"schema\": 2 }");

        ProjectKindStore.Write(paths, ProjectKind.Counter);

        Assert.Equal("counter", (string?)JObject.Parse(File.ReadAllText(paths.ProjectFile))["projectKind"]);
        Assert.Equal(2, (int?)JObject.Parse(File.ReadAllText(paths.ProjectFile))["schema"]);
        Assert.Equal(ProjectKind.Counter, ProjectKindStore.Read(paths));

        Assert.True(ProjectKind.Counter.IsInstanceKind());
        Assert.True(ProjectKind.Position.IsInstanceKind());
        Assert.False(ProjectKind.Detect.IsInstanceKind());
        Assert.True(ProjectKindToolRules.IsShapeTypeAllowed(ProjectKind.Counter, "oriented_rectangle"));
        Assert.True(ProjectKindToolRules.IsShapeTypeAllowed(ProjectKind.Counter, "circle"));
        Assert.False(ProjectKindToolRules.IsShapeTypeAllowed(ProjectKind.Counter, "polygon"));
        Assert.False(ProjectKindToolRules.IsToolAllowed(ProjectKind.Counter, CanvasTool.Polygon));
        Assert.True(ProjectKindToolRules.IsToolAllowed(ProjectKind.Counter, CanvasTool.Circle));
    }

    [Fact]
    public void ProjectKind_SaveAsCopies()
    {
        ProjectManager manager = new ProjectManager();
        string location = NewFolder("create");
        ProjectOpenResult created = manager.Create("Src", location, null, ProjectKind.Position);
        Assert.Equal(ProjectKind.Position, created.Project.Kind);

        string destination = NewFolder("saveas");
        ProjectOpenResult copy = manager.SaveAs(created.Project, "Copy", destination, false, null, CancellationToken.None);
        Assert.Equal(ProjectKind.Position, copy.Project.Kind);

        // A project created without a kind is Detect and stays Detect through Save As.
        ProjectOpenResult plain = manager.Create("Plain", location, null);
        Assert.Equal(ProjectKind.Detect, plain.Project.Kind);
        ProjectOpenResult plainCopy = manager.SaveAs(plain.Project, "PlainCopy", destination, false, null, CancellationToken.None);
        Assert.Equal(ProjectKind.Detect, plainCopy.Project.Kind);
    }

    [Theory]
    [InlineData(CanvasTool.Polygon)]
    [InlineData(CanvasTool.Rect)]
    [InlineData(CanvasTool.Point)]
    [InlineData(CanvasTool.Line)]
    [InlineData(CanvasTool.LineStrip)]
    [InlineData(CanvasTool.AiPoints)]
    public void PositionMode_RejectsPolygonTool(CanvasTool tool)
    {
        Assert.False(ProjectKindToolRules.IsToolAllowed(ProjectKind.Position, tool));
        Assert.True(ProjectKindToolRules.IsToolAllowed(ProjectKind.Detect, tool));
    }

    [Theory]
    [InlineData(CanvasTool.Select)]
    [InlineData(CanvasTool.Rot)]
    [InlineData(CanvasTool.Circle)]
    [InlineData(CanvasTool.AiBox)]
    public void PositionMode_AllowsSelectRotCircleAiBox(CanvasTool tool)
    {
        Assert.True(ProjectKindToolRules.IsToolAllowed(ProjectKind.Position, tool));
        Assert.True(ProjectKindToolRules.IsToolAllowed(ProjectKind.Detect, tool));
    }

    [Fact]
    public void EveryProjectKind_AllowsHandTool()
    {
        foreach (ProjectKind kind in Enum.GetValues(typeof(ProjectKind)))
        {
            Assert.True(ProjectKindToolRules.IsToolAllowed(kind, CanvasTool.Hand));
        }
    }

    [Theory]
    [InlineData("oriented_rectangle")]
    [InlineData("circle")]
    public void PositionMode_KeepsOrientedBoxAndCircleShapes(string shapeType)
    {
        Assert.True(ProjectKindToolRules.IsShapeTypeAllowed(ProjectKind.Position, shapeType));
    }

    [Theory]
    [InlineData("polygon")]
    [InlineData("rectangle")]
    [InlineData("point")]
    [InlineData("line")]
    [InlineData("linestrip")]
    [InlineData("")]
    [InlineData(null)]
    public void PositionMode_RefusesOtherShapeTypes(string? shapeType)
    {
        Assert.False(ProjectKindToolRules.IsShapeTypeAllowed(ProjectKind.Position, shapeType));
        Assert.True(ProjectKindToolRules.IsShapeTypeAllowed(ProjectKind.Detect, shapeType));
    }
}
