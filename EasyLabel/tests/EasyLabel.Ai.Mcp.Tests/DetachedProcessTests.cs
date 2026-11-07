using System.Diagnostics;
using Xunit;

namespace EasyLabel.Ai.Mcp.Tests;

public class DetachedProcessTests
{
    [Fact]
    public void Start_ReturnsImmediately_ChildKeepsRunning_AndWritesStdoutAndStderrToTheLog()
    {
        if (!OperatingSystem.IsWindows()) return;
        string dir = Path.Combine(Path.GetTempPath(), "detached-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string log = Path.Combine(dir, "autoloop.log");
        string code = "import sys,time; print('to-out',flush=True); print('to-err',file=sys.stderr,flush=True); print('stdin=' + repr(sys.stdin.read()),flush=True); time.sleep(2); print('done',flush=True)";

        int pid = DetachedProcess.Start("python", new[] { "-I", "-u", "-c", code }, dir, log);

        using Process child = Process.GetProcessById(pid);
        Assert.False(child.HasExited);
        Assert.True(child.WaitForExit(30000));

        string text = File.ReadAllText(log);
        Assert.Contains("to-out", text);
        Assert.Contains("to-err", text);
        Assert.Contains("stdin=''", text);
        Assert.Contains("done", text);
    }
}
