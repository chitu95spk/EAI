using EasyAI.Desktop.Services.Agent;

namespace EasyLabel.SegAI.Tests;

public sealed class AgentLocatorTests
{
    private const string Profile = @"C:\Users\tester";
    private const string Explicit = @"D:\tools\my claude\claude.exe";
    private const string OnPath = @"C:\bin\claude.exe";
    private const string Local = Profile + @"\.claude\local\claude.exe";

    private static string Extension(string root, string version)
    {
        return Profile + "\\" + root + "\\extensions\\anthropic.claude-code-" + version + "-win32-x64\\resources\\native-binary\\claude.exe";
    }

    private static FakeAgentEnvironment Machine()
    {
        FakeAgentEnvironment env = new FakeAgentEnvironment { UserProfile = Profile };
        env.Variables["PATH"] = @"C:\Windows;C:\bin;C:\other";
        return env;
    }

    [Fact]
    public void NothingInstalledGivesNull()
    {
        Assert.Null(ClaudeLocator.Find(Machine()));
    }

    [Fact]
    public void ExplicitVariableWinsOverEverything()
    {
        FakeAgentEnvironment env = Machine();
        env.Variables[ClaudeLocator.EnvironmentName] = "\"" + Explicit + "\"";
        env.AddFile(Explicit);
        env.AddFile(OnPath);
        env.AddFile(Local);
        Assert.Equal(Explicit, ClaudeLocator.Find(env));
    }

    [Fact]
    public void ExplicitVariableThatPointsNowhereFallsThroughToPath()
    {
        FakeAgentEnvironment env = Machine();
        env.Variables[ClaudeLocator.EnvironmentName] = Explicit;
        env.AddFile(OnPath);
        Assert.Equal(OnPath, ClaudeLocator.Find(env));
    }

    [Fact]
    public void ExplicitScriptIsIgnored()
    {
        FakeAgentEnvironment env = Machine();
        env.Variables[ClaudeLocator.EnvironmentName] = @"D:\tools\claude.cmd";
        env.AddFile(@"D:\tools\claude.cmd");
        Assert.Null(ClaudeLocator.Find(env));
    }

    [Fact]
    public void PathBeatsTheLocalInstallAndTheLocalInstallBeatsTheExtension()
    {
        FakeAgentEnvironment env = Machine();
        env.AddFile(Extension(".vscode", "2.1.292"));
        Assert.Equal(Extension(".vscode", "2.1.292"), ClaudeLocator.Find(env));

        env.AddFile(Local);
        Assert.Equal(Local, ClaudeLocator.Find(env));

        env.AddFile(OnPath);
        Assert.Equal(OnPath, ClaudeLocator.Find(env));
    }

    [Fact]
    public void NewestExtensionVersionWinsAcrossEditorsAndComparesNumerically()
    {
        FakeAgentEnvironment env = Machine();
        env.AddFile(Extension(".vscode", "2.9.9"));
        env.AddFile(Extension(".vscode-insiders", "2.10.0"));
        env.AddFile(Extension(".cursor", "2.1.292"));
        Assert.Equal(Extension(".vscode-insiders", "2.10.0"), ClaudeLocator.Find(env));

        env.AddFile(Extension(".windsurf", "3.0.1"));
        Assert.Equal(Extension(".windsurf", "3.0.1"), ClaudeLocator.Find(env));
    }

    [Fact]
    public void ExtensionFolderWithoutTheNativeBinaryIsIgnored()
    {
        FakeAgentEnvironment env = Machine();
        env.AddFile(Profile + @"\.vscode\extensions\anthropic.claude-code-9.9.9-win32-x64\package.json");
        env.AddFile(Extension(".vscode", "2.1.292"));
        Assert.Equal(Extension(".vscode", "2.1.292"), ClaudeLocator.Find(env));
    }

    [Fact]
    public void NpmShimIsUsedOnlyThroughTheNativeBinaryBesideIt()
    {
        FakeAgentEnvironment env = Machine();
        env.AddFile(@"C:\bin\claude.cmd");
        Assert.Null(ClaudeLocator.Find(env));

        string native = @"C:\bin\node_modules\@anthropic-ai\claude-code\bin\claude.exe";
        env.AddFile(native);
        Assert.Equal(native, ClaudeLocator.Find(env));

        // A real executable anywhere earlier in the order beats the shim.
        env.AddFile(Local);
        Assert.Equal(Local, ClaudeLocator.Find(env));
    }

    [Fact]
    public void ExtensionVersionIsReadFromTheFolderName()
    {
        Assert.Equal(new Version(2, 1, 292), ClaudeLocator.ExtensionVersion("anthropic.claude-code-2.1.292-win32-x64"));
        Assert.Equal(new Version(0, 0), ClaudeLocator.ExtensionVersion("anthropic.claude-code-beta-win32-x64"));
        Assert.Equal(new Version(0, 0), ClaudeLocator.ExtensionVersion("something-else"));
    }

    [Fact]
    public void McpNeedsTheExeAndAtLeastOneSkillText()
    {
        FakeAgentEnvironment env = new FakeAgentEnvironment();
        string exe = @"C:\app\agent\EasyLabel.Ai.Mcp.exe";
        Assert.Null(McpLocator.Find(env, @"C:\app"));

        env.AddFile(exe);
        Assert.Null(McpLocator.Find(env, @"C:\app"));

        env.AddFile(@"C:\app\agent\Skills\read-image.md", "x");
        Assert.Equal(exe, McpLocator.Find(env, @"C:\app"));
    }
}
