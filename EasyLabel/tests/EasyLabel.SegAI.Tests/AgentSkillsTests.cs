using EasyAI.Desktop.Services.Agent;

namespace EasyLabel.SegAI.Tests;

public sealed class AgentSkillsTests : IDisposable
{
    private readonly string _root = Path.Combine(AppContext.BaseDirectory, "tmp-skills", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }

    private AgentSkillStore Store() => new AgentSkillStore(Path.Combine(_root, "skills"));

    [Theory]
    [InlineData("label-boxes", true)]
    [InlineData("a", true)]
    [InlineData("skill_2", true)]
    [InlineData("", false)]
    [InlineData("Upper", false)]
    [InlineData("-lead", false)]
    [InlineData("a b", false)]
    [InlineData("..", false)]
    [InlineData("a/b", false)]
    [InlineData("a\\b", false)]
    public void NameValidation(string name, bool ok)
    {
        Assert.Equal(ok, AgentSkillStore.IsValidName(name));
    }

    [Fact]
    public void CreateListEnableDisableDelete()
    {
        AgentSkillStore store = Store();
        Assert.Empty(store.List());

        AgentSkillEntry made = store.Create("find-defects", AgentSkillStore.Template("find-defects", "Find scratches"));
        Assert.True(made.Enabled);
        Assert.Equal("Find scratches", made.Description);
        Assert.Single(store.List());

        AgentSkillEntry off = store.SetEnabled(made, false);
        Assert.False(off.Enabled);
        Assert.False(Directory.Exists(made.Folder));
        Assert.True(File.Exists(off.SkillFile));
        AgentSkillEntry listed = Assert.Single(store.List());
        Assert.False(listed.Enabled);

        AgentSkillEntry on = store.SetEnabled(off, true);
        Assert.True(File.Exists(on.SkillFile));
        Assert.True(Assert.Single(store.List()).Enabled);

        store.Delete(on);
        Assert.Empty(store.List());
        Assert.False(Directory.Exists(on.Folder));
    }

    [Fact]
    public void CreateRefusesDuplicatesAndBadNames()
    {
        AgentSkillStore store = Store();
        AgentSkillEntry made = store.Create("one", "x");
        Assert.Throws<IOException>(() => store.Create("one", "y"));
        store.SetEnabled(made, false);
        Assert.Throws<IOException>(() => store.Create("one", "y"));
        Assert.Throws<ArgumentException>(() => store.Create("../escape", "z"));
        Assert.False(Directory.Exists(Path.Combine(_root, "escape")));
    }

    [Fact]
    public void WriteTextUpdatesTheDescription()
    {
        AgentSkillStore store = Store();
        AgentSkillEntry made = store.Create("doc", "---\nname: doc\ndescription: old\n---\n");
        AgentSkillEntry changed = store.WriteText(made, "---\nname: doc\ndescription: \"new words\"\n---\nbody");
        Assert.Equal("new words", changed.Description);
        Assert.Equal("---\nname: doc\ndescription: \"new words\"\n---\nbody", store.ReadText(changed));
    }

    [Fact]
    public void AnEntryOutsideTheRootsIsRefused()
    {
        AgentSkillStore store = Store();
        AgentSkillEntry stray = new AgentSkillEntry("x", string.Empty, Path.Combine(_root, "elsewhere", "x"), true);
        Assert.Throws<ArgumentException>(() => store.Delete(stray));
        Assert.Throws<ArgumentException>(() => store.SetEnabled(stray, false));
    }

    [Fact]
    public void ListSkipsSystemFoldersAndFoldersWithoutSkillFile()
    {
        AgentSkillStore store = Store();
        Directory.CreateDirectory(Path.Combine(store.Root, ".system"));
        File.WriteAllText(Path.Combine(store.Root, ".system", "SKILL.md"), "---\n---\n");
        Directory.CreateDirectory(Path.Combine(store.Root, "empty"));
        store.Create("real", "x");
        Assert.Equal(new[] { "real" }, store.List().Select(s => s.Name).ToArray());
    }

    [Fact]
    public void WithNameFillsOnlyABlankNameLine()
    {
        Assert.Contains("name: fresh", AgentSkillStore.WithName(AgentSkillStore.Template(string.Empty, "d"), "fresh"));
        Assert.Contains("name: kept", AgentSkillStore.WithName("---\nname: kept\n---\n", "other"));
    }

    [Fact]
    public void RootsFollowTheProfileAndCodexHome()
    {
        FakeAgentEnvironment env = new FakeAgentEnvironment { UserProfile = @"C:\Users\tester" };
        Assert.Equal(@"C:\Users\tester\.claude\skills", AgentSkillRoots.For(AgentTool.Claude, env));
        Assert.Equal(@"C:\Users\tester\.codex\skills", AgentSkillRoots.For(AgentTool.Codex, env));
        env.Variables["CODEX_HOME"] = @"D:\codex";
        Assert.Equal(@"D:\codex\skills", AgentSkillRoots.For(AgentTool.Codex, env));
    }

    [Fact]
    public void CodexLocatorPrefersTheOverrideThenPathThenTheNewestExtension()
    {
        FakeAgentEnvironment env = new FakeAgentEnvironment();
        Assert.Null(CodexLocator.Find(env));

        env.Files.Add(@"C:\Users\tester\.vscode\extensions\openai.chatgpt-26.1.1-win32-x64\bin\windows-x86_64\codex.exe");
        env.Files.Add(@"C:\Users\tester\.vscode\extensions\openai.chatgpt-26.10.2-win32-x64\bin\windows-x86_64\codex.exe");
        Assert.Equal(@"C:\Users\tester\.vscode\extensions\openai.chatgpt-26.10.2-win32-x64\bin\windows-x86_64\codex.exe", CodexLocator.Find(env));

        env.Files.Add(@"C:\tools\codex.exe");
        env.Variables["PATH"] = @"C:\tools";
        Assert.Equal(@"C:\tools\codex.exe", CodexLocator.Find(env));

        env.Files.Add(@"D:\mine\codex.exe");
        env.Variables[CodexLocator.EnvironmentName] = @"D:\mine\codex.exe";
        Assert.Equal(@"D:\mine\codex.exe", CodexLocator.Find(env));
    }

    private const string AppAgent = @"C:\app\agent";
    private const string UserRoot = @"C:\u\skills";

    private static FakeAgentEnvironment RunEnv()
    {
        FakeAgentEnvironment env = new FakeAgentEnvironment { UserProfile = @"C:\Users\tester" };
        env.AddFile(AppAgent + @"\Skills\read-image.md", "# Skill: read the image\n1. x");
        env.AddFile(AppAgent + @"\Skills\detect.md", "# Skill: detect\n1. y");
        env.AddFile(AppAgent + @"\Skills\prompt-suggestion.md", "# Skill: suggest");
        env.AddFile(UserRoot + @"\mine\SKILL.md", "---\nname: mine\ndescription: my words\n---\nbody");
        env.AddFile(@"C:\Users\tester\.claude\skills\home-one\SKILL.md", "---\nname: home-one\ndescription: from home\n---\n");
        return env;
    }

    [Fact]
    public void BuiltInSkillFilesIncludeTheFourNewNames()
    {
        string? folder = null;
        for (string? dir = AppContext.BaseDirectory; dir != null && folder == null; dir = Path.GetDirectoryName(dir))
        {
            string candidate = Path.Combine(dir, "EasyLabel", "src", "EasyLabel.Ai.Mcp", "Skills");
            if (Directory.Exists(candidate))
            {
                folder = candidate;
            }
            else
            {
                candidate = Path.Combine(dir, "src", "EasyLabel.Ai.Mcp", "Skills");
                if (Directory.Exists(candidate))
                {
                    folder = candidate;
                }
            }
        }

        Assert.NotNull(folder);
        foreach (string name in new[] { "detect", "judge", "check", "repeat", "read-image" })
        {
            Assert.True(File.Exists(Path.Combine(folder!, name + ".md")), name);
        }
    }

    [Fact]
    public void RunSkillListHasBuiltInUserAndReadOnlyHomeSkills()
    {
        FakeAgentEnvironment env = RunEnv();
        IReadOnlyList<RunSkillInfo> list = RunSkillCatalog.List(env, AppAgent, UserRoot, null, new[] { @"C:\Users\tester\.claude\skills" });

        Assert.Equal(new[] { "detect", "read-image", "mine", "home-one" }, list.Select(s => s.Name).ToArray());
        Assert.Equal(RunSkillKind.BuiltIn, list[0].Kind);
        Assert.Equal("Skill: detect", list[0].Description);
        Assert.Equal(RunSkillKind.User, list[2].Kind);
        Assert.Equal("my words", list[2].Description);
        Assert.True(list[2].Enabled);
        Assert.False(list[2].ReadOnly);
        Assert.Equal(RunSkillKind.Home, list[3].Kind);
        Assert.True(list[3].ReadOnly);
    }

    [Fact]
    public void ADisabledSkillIsFilteredFromTheTextsAndChangesThePackHash()
    {
        FakeAgentEnvironment env = RunEnv();
        IReadOnlyList<string> all = AgentRunFiles.ReadSkillTexts(env, AppAgent, new List<string>(), UserRoot);
        Assert.Equal(3, all.Count);
        Assert.Contains(all, t => t.Contains("# Skill: detect", StringComparison.Ordinal));

        IReadOnlyList<string> filtered = AgentRunFiles.ReadSkillTexts(env, AppAgent, new List<string> { "detect", "MINE" }, UserRoot);
        Assert.Equal(new[] { "# Skill: read the image\n1. x" }, filtered.ToArray());

        Assert.NotEqual(AgentRunFiles.SkillPackHash(all), AgentRunFiles.SkillPackHash(filtered));
        Assert.Equal(AgentRunFiles.SkillPackHash(all), AgentRunFiles.SkillPackHash(AgentRunFiles.ReadSkillTexts(env, AppAgent, null, UserRoot)));

        IReadOnlyList<RunSkillInfo> list = RunSkillCatalog.List(env, AppAgent, UserRoot, new[] { "detect" }, null);
        Assert.False(list.Single(s => s.Name == "detect").Enabled);
        Assert.True(list.Single(s => s.Name == "read-image").Enabled);
    }

    [Fact]
    public void HomeSkillsAreNeverPartOfTheRunTexts()
    {
        FakeAgentEnvironment env = RunEnv();
        IReadOnlyList<string> texts = AgentRunFiles.ReadSkillTexts(env, AppAgent, null, UserRoot);
        Assert.DoesNotContain(texts, t => t.Contains("from home", StringComparison.Ordinal));
        Assert.Equal(AgentRunFiles.ReadSkillTexts(env, AppAgent).Count + 1, texts.Count);
    }

    [Fact]
    public void ClaudeStatusJson()
    {
        AgentAuthStatus on = AgentAuthParser.ParseClaude("{\n  \"loggedIn\": true,\n  \"authMethod\": \"claude.ai\",\n  \"email\": \"a@b.c\"\n}");
        Assert.Equal(AgentAuthState.SignedIn, on.State);
        Assert.Equal("a@b.c", on.Detail);
        Assert.Equal(AgentAuthState.SignedOut, AgentAuthParser.ParseClaude("{\"loggedIn\": false}").State);
        Assert.Equal(AgentAuthState.SignedOut, AgentAuthParser.ParseClaude("garbage").State);
    }

    [Fact]
    public void CodexStatusText()
    {
        AgentAuthStatus on = AgentAuthParser.ParseCodex("Logged in using ChatGPT\n", 0);
        Assert.Equal(AgentAuthState.SignedIn, on.State);
        Assert.Equal("ChatGPT", on.Detail);
        Assert.Equal(AgentAuthState.SignedOut, AgentAuthParser.ParseCodex("Not logged in", 1).State);
        Assert.Equal(AgentAuthState.SignedOut, AgentAuthParser.ParseCodex("Not logged in", 0).State);
    }
}
