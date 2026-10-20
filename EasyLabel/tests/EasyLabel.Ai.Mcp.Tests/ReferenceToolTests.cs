using EasyLabel.Ai.Mcp;
using Newtonsoft.Json.Linq;
using OpenCvSharp;
using Xunit;

namespace EasyLabel.Ai.Mcp.Tests;

/// <summary>The get_reference tool: files the user attached to the prompt as hints.</summary>
public sealed class ReferenceToolTests
{
    private static string WriteReferenceImage(TestRun run, string name)
    {
        string path = Path.Combine(run.Directory, name);
        using var mat = new Mat(300, 400, MatType.CV_8UC3, new Scalar(10, 200, 30));
        Cv2.Rectangle(mat, new Rect(50, 50, 100, 80), new Scalar(0, 0, 255), 3);
        Assert.True(Cv2.ImEncode(".png", mat, out byte[] png));
        File.WriteAllBytes(path, png);
        return path;
    }

    [Fact]
    public void ToolIsDescribedAndRefusesARunWithoutAttachments()
    {
        using TestRun run = new TestRun();

        Assert.True(run.Tools.Has("get_reference"));
        Assert.Contains(run.Tools.Describe(), t => (string?)t["name"] == "get_reference");
        Assert.Throws<ArgumentException>(() => run.Call("get_reference", "{}"));
        Assert.DoesNotContain("references", TestRun.ResultText(run.Call("get_image_info", "{}")));
    }

    [Fact]
    public void ImageReferenceIsReturnedAsAPictureAndListedInTheImageInfo()
    {
        using TestRun run = new TestRun();
        string path = WriteReferenceImage(run, "defect-example.png");
        run.Request.References.Add(new RunReference { Path = path, Name = "defect-example.png" });

        JObject result = run.Call("get_reference", "{\"index\":0}");

        Assert.Equal("image", (string?)result["content"]![0]!["type"]);
        Assert.Equal("image/jpeg", (string?)result["content"]![0]!["mimeType"]);
        string text = (string)result["content"]![1]!["text"]!;
        Assert.Contains("defect-example.png", text);
        Assert.Contains("NOT the image to label", text);
        JObject info = JObject.Parse(TestRun.ResultText(run.Call("get_image_info", "{}")));
        Assert.Equal("image", (string?)info["references"]![0]!["kind"]);
        Assert.Equal("defect-example.png", (string?)info["references"]![0]!["name"]);
    }

    [Fact]
    public void TextReferenceIsReturnedCutAtTheLimit()
    {
        using TestRun run = new TestRun();
        string path = Path.Combine(run.Directory, "note.txt");
        File.WriteAllText(path, new string('x', 6000));
        run.Request.References.Add(new RunReference { Path = path, Name = "note.txt" });

        string text = TestRun.ResultText(run.Call("get_reference", "{}"));

        Assert.Contains("note.txt", text);
        Assert.Contains("cut at 4000", text);
        Assert.True(text.Length < 4200);
    }

    [Fact]
    public void BadIndexAndMissingFileAreRefused()
    {
        using TestRun run = new TestRun();
        run.Request.References.Add(new RunReference { Path = Path.Combine(run.Directory, "gone.png"), Name = "gone.png" });

        Assert.Throws<ArgumentException>(() => run.Call("get_reference", "{\"index\":3}"));
        Assert.Throws<ArgumentException>(() => run.Call("get_reference", "{\"index\":-1}"));
        Assert.Throws<ArgumentException>(() => run.Call("get_reference", "{\"index\":0}"));
    }

    [Fact]
    public void RequestFileWithoutReferencesStillLoadsAndTheListRoundTrips()
    {
        using TestRun run = new TestRun();
        string file = Path.Combine(run.Directory, RunRequest.FileName);
        Assert.Empty(RunRequest.Load(file).References);

        JObject root = JObject.Parse(File.ReadAllText(file));
        root["references"] = new JArray(new JObject { ["path"] = "C:\\x\\a.png", ["name"] = "a.png" });
        File.WriteAllText(file, root.ToString());

        RunRequest loaded = RunRequest.Load(file);
        Assert.Single(loaded.References);
        Assert.Equal("a.png", loaded.References[0].Name);
    }
}
