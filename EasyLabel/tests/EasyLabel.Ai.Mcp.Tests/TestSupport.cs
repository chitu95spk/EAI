using EasyLabel.Ai.Mcp;
using Newtonsoft.Json.Linq;
using OpenCvSharp;

namespace EasyLabel.Ai.Mcp.Tests;

/// <summary>A run folder under the test output directory with a generated image, request.json and tools. Deleted on dispose.</summary>
internal sealed class TestRun : IDisposable
{
    public const int ImageWidth = 2000;
    public const int ImageHeight = 1000;

    public TestRun(string[]? classNames = null, bool withModelsFolder = false, bool allowOutOfBounds = true)
    {
        Directory = Path.Combine(AppContext.BaseDirectory, "mcp-tests", Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(Directory);
        ModelsFolder = Path.Combine(Directory, "models");
        if (withModelsFolder) System.IO.Directory.CreateDirectory(ModelsFolder);

        ImagePath = Path.Combine(Directory, "image.png");
        using (var mat = new Mat(ImageHeight, ImageWidth, MatType.CV_8UC3, new Scalar(30, 90, 150)))
        {
            Cv2.Rectangle(mat, new Rect(100, 100, 300, 200), new Scalar(255, 255, 255), -1);
            if (!Cv2.ImEncode(".png", mat, out byte[] png)) throw new IOException("png encode failed");
            File.WriteAllBytes(ImagePath, png);
        }

        Request = new RunRequest
        {
            ImagePath = ImagePath,
            ImageWidth = ImageWidth,
            ImageHeight = ImageHeight,
            ClassNames = new List<string>(classNames ?? new[] { "NG", "OK" }),
            Prompt = "find defects",
            AllowOutOfBounds = allowOutOfBounds,
            ExistingShapes = new List<RunShape>
            {
                new RunShape { Label = "OK", ShapeType = "rectangle", Points = new List<double[]> { new[] { 1.0, 2.0 }, new[] { 30.0, 40.0 } } },
            },
        };
        Request.Save(Path.Combine(Directory, RunRequest.FileName));
        Tools = new EasyAiTools(Directory, Request, ModelsFolder);
    }

    public string Directory { get; }

    public string ModelsFolder { get; }

    public string ImagePath { get; }

    public RunRequest Request { get; }

    public EasyAiTools Tools { get; }

    public string ProposalsPath => Path.Combine(Directory, "proposals.json");

    public JObject Call(string tool, string argumentsJson)
    {
        return Tools.Call(tool, JObject.Parse(argumentsJson), CancellationToken.None);
    }

    public static string ResultText(JObject result) => (string)result["content"]![0]!["text"]!;

    public void Dispose()
    {
        Tools.Dispose();
        try
        {
            System.IO.Directory.Delete(Directory, true);
        }
        catch (IOException)
        {
        }
    }
}

internal static class Wire
{
    public static string Request(object id, string method, string? paramsJson = null)
    {
        string idText = id is string s ? "\"" + s + "\"" : id.ToString()!;
        return "{\"jsonrpc\":\"2.0\",\"id\":" + idText + ",\"method\":\"" + method + "\"" + (paramsJson == null ? "" : ",\"params\":" + paramsJson) + "}";
    }

    public static string Notification(string method) => "{\"jsonrpc\":\"2.0\",\"method\":\"" + method + "\"}";

    /// <summary>Feeds the lines to a server and returns every output line parsed as JSON.</summary>
    public static List<JObject> Exchange(IToolSet tools, params string[] lines)
    {
        var input = new StringReader(string.Join("\n", lines) + "\n");
        var output = new StringWriter { NewLine = "\n" };
        new McpServer(tools).Run(input, output);
        return output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => JObject.Parse(l)).ToList();
    }
}
