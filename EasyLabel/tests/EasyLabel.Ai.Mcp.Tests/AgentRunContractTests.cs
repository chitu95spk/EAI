using EasyAI.Desktop.Services.Agent;

namespace EasyLabel.Ai.Mcp.Tests;

/// <summary>EasyAI writes request.json; the MCP server reads it with <see cref="RunRequest"/>. These tests keep the two in step.</summary>
public sealed class AgentRunContractTests
{
    [Fact]
    public void RequestWrittenByEasyAiIsReadBackByTheMcpServer()
    {
        string folder = Path.Combine(AppContext.BaseDirectory, "agent-contract", Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(folder);
        try
        {
            AgentShapeInfo existing = new AgentShapeInfo("NG", "rectangle", new List<double[]> { new[] { 1.5, 2.5 }, new[] { 30.0, 40.0 } });
            AgentRunInput input = new AgentRunInput(
                @"E:\My Images\a b.png", "a b.png", 2520, 2600, new[] { "NG", "OK" }, new[] { existing }, "mark \"every\" scratch\nplease", @"C:\models", 1, false);
            string path = Path.Combine(folder, RunRequest.FileName);
            File.WriteAllText(path, AgentRunFiles.BuildRequestJson(input));

            RunRequest request = RunRequest.Load(path);

            Assert.Equal(@"E:\My Images\a b.png", request.ImagePath);
            Assert.Equal(2520, request.ImageWidth);
            Assert.Equal(2600, request.ImageHeight);
            Assert.Equal(new[] { "NG", "OK" }, request.ClassNames.ToArray());
            Assert.Equal("mark \"every\" scratch\nplease", request.Prompt);
            Assert.Equal(@"C:\models", request.ModelsFolder);
            RunShape shape = Assert.Single(request.ExistingShapes);
            Assert.Equal("NG", shape.Label);
            Assert.Equal("rectangle", shape.ShapeType);
            Assert.Equal(2, shape.Points.Count);
            Assert.Equal(1.5, shape.Points[0][0]);
            Assert.Equal(40.0, shape.Points[1][1]);
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void PendingShapesAreWrittenOnlyWhenThereAreSome_AndReadBackByTheMcpServer()
    {
        string folder = Path.Combine(AppContext.BaseDirectory, "agent-contract", Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(folder);
        try
        {
            AgentRunInput none = new AgentRunInput("a.png", "a.png", 10, 20, new[] { "NG" }, Array.Empty<AgentShapeInfo>(), "p", null, 0, false);
            Assert.Empty(none.PendingShapes);
            Assert.False(Newtonsoft.Json.Linq.JObject.Parse(AgentRunFiles.BuildRequestJson(none)).ContainsKey("pendingShapes"));

            AgentShapeInfo pending = new AgentShapeInfo("NG", "polygon", new List<double[]> { new[] { 1.0, 2.0 }, new[] { 9.0, 2.0 }, new[] { 9.0, 8.0 } });
            AgentRunInput with = new AgentRunInput("a.png", "a.png", 10, 20, new[] { "NG" }, Array.Empty<AgentShapeInfo>(), "p", null, 0, false, null, null, null, new[] { pending });
            string path = Path.Combine(folder, RunRequest.FileName);
            File.WriteAllText(path, AgentRunFiles.BuildRequestJson(with));

            RunRequest request = RunRequest.Load(path);
            RunShape shape = Assert.Single(request.PendingShapes);
            Assert.Equal("NG", shape.Label);
            Assert.Equal("polygon", shape.ShapeType);
            Assert.Equal(3, shape.Points.Count);
            Assert.Equal(8.0, shape.Points[2][1]);

            // The experience block copy keeps the pending shapes.
            Assert.Single(with.WithExperienceBlock("block").PendingShapes);
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void OldRequestFileWithoutPendingShapesStillLoadsWithAnEmptyList()
    {
        string folder = Path.Combine(AppContext.BaseDirectory, "agent-contract", Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(folder);
        try
        {
            string path = Path.Combine(folder, RunRequest.FileName);
            File.WriteAllText(path, "{\"imagePath\":\"a.png\",\"imageWidth\":10,\"imageHeight\":20,\"classNames\":[\"NG\"],"
                + "\"existingShapes\":[{\"label\":\"NG\",\"shapeType\":\"rectangle\",\"points\":[[1,2],[3,4]]}],\"prompt\":\"p\",\"modelsFolder\":null,\"memoryPath\":null}");

            RunRequest request = RunRequest.Load(path);

            Assert.Empty(request.PendingShapes);
            Assert.Single(request.ExistingShapes);
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void RequestWithoutModelsFolderStillLoads()
    {
        string folder = Path.Combine(AppContext.BaseDirectory, "agent-contract", Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(folder);
        try
        {
            AgentRunInput input = new AgentRunInput("a.png", "a.png", 10, 20, new[] { "NG", "OK" }, Array.Empty<AgentShapeInfo>(), "p", null, 0, false);
            string path = Path.Combine(folder, RunRequest.FileName);
            File.WriteAllText(path, AgentRunFiles.BuildRequestJson(input));

            RunRequest request = RunRequest.Load(path);

            Assert.Null(request.ModelsFolder);
            Assert.Empty(request.ExistingShapes);
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }
}
