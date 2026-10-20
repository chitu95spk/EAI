using EasyLabel.Ai.Mcp;
using Newtonsoft.Json.Linq;

namespace EasyLabel.Ai.Mcp.Tests;

public sealed class EntryTests
{
    private static string NewScratch()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "mcp-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static int Run(string[] args, string stdin, out string stdout, out string stderr)
    {
        var outWriter = new StringWriter { NewLine = "\n" };
        var errWriter = new StringWriter();
        int code = McpEntry.Run(args, new StringReader(stdin), outWriter, errWriter);
        stdout = outWriter.ToString();
        stderr = errWriter.ToString();
        return code;
    }

    [Fact]
    public void MissingRunArgument_ExitsWith2()
    {
        int code = Run(Array.Empty<string>(), string.Empty, out string stdout, out string stderr);
        Assert.Equal(2, code);
        Assert.Equal(string.Empty, stdout);
        Assert.Contains("--run", stderr);
    }

    [Fact]
    public void MissingRunFolder_ExitsWith2_AndWritesNothingToStdout()
    {
        string missing = Path.Combine(AppContext.BaseDirectory, "mcp-tests", Guid.NewGuid().ToString("N"));
        int code = Run(new[] { "--run", missing }, string.Empty, out string stdout, out string stderr);
        Assert.Equal(2, code);
        Assert.Equal(string.Empty, stdout);
        Assert.Contains("not found", stderr);
        Assert.False(Directory.Exists(missing));
    }

    [Fact]
    public void MissingRequestJson_ExitsWith2()
    {
        string dir = NewScratch();
        try
        {
            int code = Run(new[] { "--run", dir }, string.Empty, out string stdout, out string stderr);
            Assert.Equal(2, code);
            Assert.Equal(string.Empty, stdout);
            Assert.Contains("request.json", stderr);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void CorruptRequestJson_ExitsWith2()
    {
        string dir = NewScratch();
        try
        {
            File.WriteAllText(Path.Combine(dir, RunRequest.FileName), "{ nope");
            int code = Run(new[] { "--run", dir }, string.Empty, out _, out string stderr);
            Assert.Equal(2, code);
            Assert.Contains("invalid", stderr);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void ValidRun_ServesProtocolOnStdoutOnly_AndLogsToStderrAndFile()
    {
        using var run = new TestRun();
        string input = Wire.Request(1, "initialize", "{\"protocolVersion\":\"2024-11-05\"}") + "\n" + Wire.Notification("notifications/initialized") + "\n" + Wire.Request(2, "tools/list") + "\n";

        int code = Run(new[] { "--run", run.Directory, "--models", run.ModelsFolder }, input, out string stdout, out string stderr);

        Assert.Equal(0, code);
        string[] lines = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        Assert.All(lines, l => Assert.Equal("2.0", (string?)JObject.Parse(l)["jsonrpc"]));
        Assert.Contains("started", stderr);
        Assert.True(File.Exists(Path.Combine(run.Directory, McpEntry.LogFileName)));
    }

    [Fact]
    public void RunRequest_RoundTripsAndRejectsMissingImagePath()
    {
        string dir = NewScratch();
        try
        {
            string path = Path.Combine(dir, RunRequest.FileName);
            new RunRequest { ImagePath = "a.png", ImageWidth = 3, ImageHeight = 4, ClassNames = new List<string> { "NG" }, Prompt = "p" }.Save(path);
            RunRequest loaded = RunRequest.Load(path);
            Assert.Equal("a.png", loaded.ImagePath);
            Assert.Equal(4, loaded.ImageHeight);
            Assert.Equal(new[] { "NG" }, loaded.ClassNames);

            File.WriteAllText(path, "{\"classNames\":[\"NG\"]}");
            Assert.Throws<InvalidDataException>(() => RunRequest.Load(path));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
