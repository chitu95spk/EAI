using EasyAI.Desktop.Services.Agent;
using Newtonsoft.Json.Linq;

namespace EasyLabel.SegAI.Tests;

public sealed class McpServerRegistryTests : IDisposable
{
    private readonly string _root = Path.Combine(AppContext.BaseDirectory, "tmp-mcp", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }

    private string FilePath => Path.Combine(_root, "mcp-servers.json");

    private static McpServerEntry Entry(string name = "fs-reader", string version = "1.2.0")
    {
        return new McpServerEntry
        {
            Name = name,
            SourceUrl = "https://example.invalid/fs-reader",
            Version = version,
            Command = @"C:\Tools\fs-reader.exe",
            Args = new List<string> { "--root", "{run}", "--readonly" },
            AccessSummary = "Reads files below the run folder",
            Tools = new List<string> { "read_file", "list_dir" },
        };
    }

    private McpServerRegistry NewRegistry() => new McpServerRegistry(FilePath);

    private static McpServerEntry Approved(string name = "fs-reader", string version = "1.2.0")
    {
        McpServerEntry entry = Entry(name, version);
        entry.ApprovedHash = McpServerRegistry.ComputeHash(entry);
        return entry;
    }

    [Fact]
    public void HashIsStableAndOrderOfToolsDoesNotMatter()
    {
        McpServerEntry a = Entry();
        McpServerEntry b = Entry();
        b.Tools.Reverse();

        Assert.Equal(McpServerRegistry.ComputeHash(a), McpServerRegistry.ComputeHash(b));
        Assert.Equal(64, McpServerRegistry.ComputeHash(a).Length);
        Assert.Equal(McpServerRegistry.ComputeHash(a), McpServerRegistry.ComputeHash(a.Clone()));
    }

    [Fact]
    public void HashDoesNotDependOnSourceOrAccessText()
    {
        McpServerEntry a = Entry();
        McpServerEntry b = Entry();
        b.SourceUrl = "https://other.invalid";
        b.AccessSummary = "something else";

        Assert.Equal(McpServerRegistry.ComputeHash(a), McpServerRegistry.ComputeHash(b));
    }

    [Fact]
    public void EachLaunchFieldChangeUnApprovesTheEntry()
    {
        Assert.True(McpServerRegistry.IsApproved(Approved()));

        System.Action<McpServerEntry>[] edits =
        {
            e => e.Name = "fs-reader2",
            e => e.Version = "1.2.1",
            e => e.Command = @"C:\Tools\other.exe",
            e => e.Args.Add("--extra"),
            e => e.Args[0] = "--root2",
            e => e.Args.Reverse(),
            e => e.Tools.Add("write_file"),
            e => e.Tools[0] = "read_files",
        };
        foreach (System.Action<McpServerEntry> edit in edits)
        {
            McpServerEntry entry = Approved();
            edit(entry);
            Assert.False(McpServerRegistry.IsApproved(entry));
        }
    }

    [Fact]
    public void FieldBoundariesAreUnambiguous()
    {
        McpServerEntry a = Entry();
        a.Args = new List<string> { "a b" };
        McpServerEntry b = Entry();
        b.Args = new List<string> { "a", "b" };
        McpServerEntry c = Entry();
        c.Command = "x";
        c.Version = "yz";
        McpServerEntry d = Entry();
        d.Command = "xy";
        d.Version = "z";

        Assert.NotEqual(McpServerRegistry.ComputeHash(a), McpServerRegistry.ComputeHash(b));
        Assert.NotEqual(McpServerRegistry.ComputeHash(c), McpServerRegistry.ComputeHash(d));
    }

    [Fact]
    public void EntryWithoutHashOrIncompleteIsNeverApproved()
    {
        Assert.False(McpServerRegistry.IsApproved(Entry()));
        Assert.False(McpServerRegistry.IsApproved(null));

        McpServerEntry noTools = Entry();
        noTools.Tools = new List<string>();
        noTools.ApprovedHash = McpServerRegistry.ComputeHash(noTools);
        Assert.False(McpServerRegistry.IsApproved(noTools));

        McpServerEntry wildcard = Entry();
        wildcard.Tools = new List<string> { "*" };
        wildcard.ApprovedHash = McpServerRegistry.ComputeHash(wildcard);
        Assert.False(McpServerRegistry.IsApproved(wildcard));
    }

    [Fact]
    public void ApproveStoresTheHashAndAnyUpdateClearsIt()
    {
        McpServerRegistry registry = NewRegistry();
        registry.Add(Entry());
        Assert.False(McpServerRegistry.IsApproved(registry.Find("fs-reader")));

        registry.Approve("fs-reader");
        Assert.True(McpServerRegistry.IsApproved(registry.Find("fs-reader")));
        Assert.True(McpServerRegistry.IsApproved(McpServerRegistry.Load(FilePath).Find("fs-reader")));

        // Even an update that changes nothing (only the access text) clears the approval.
        McpServerEntry same = registry.Find("fs-reader")!.Clone();
        same.AccessSummary = "changed text";
        registry.Update("fs-reader", same);
        Assert.Equal(string.Empty, registry.Find("fs-reader")!.ApprovedHash);
        Assert.False(McpServerRegistry.IsApproved(McpServerRegistry.Load(FilePath).Find("fs-reader")));
    }

    [Fact]
    public void AddDropsAnApprovalHashBroughtByTheCaller()
    {
        McpServerRegistry registry = NewRegistry();
        registry.Add(Approved());
        Assert.False(McpServerRegistry.IsApproved(registry.Find("fs-reader")));
    }

    [Fact]
    public void AddRejectsDuplicateAndInvalidNamesAndRemoveWorks()
    {
        McpServerRegistry registry = NewRegistry();
        registry.Add(Entry());

        Assert.Throws<ArgumentException>(() => registry.Add(Entry("FS-READER")));
        Assert.Throws<ArgumentException>(() => registry.Add(Entry("bad name")));
        Assert.Throws<ArgumentException>(() => registry.Approve("missing"));
        Assert.True(registry.Remove("fs-reader"));
        Assert.False(registry.Remove("fs-reader"));
        Assert.Empty(McpServerRegistry.Load(FilePath).Entries);
    }

    [Fact]
    public void LoadIsTolerant()
    {
        Assert.Empty(McpServerRegistry.Load(FilePath).Entries);

        Directory.CreateDirectory(_root);
        File.WriteAllText(FilePath, "{ not json");
        Assert.Empty(McpServerRegistry.Load(FilePath).Entries);

        File.WriteAllText(FilePath, "{\"a\":1}");
        Assert.Empty(McpServerRegistry.Load(FilePath).Entries);

        File.WriteAllText(FilePath, "null");
        Assert.Empty(McpServerRegistry.Load(FilePath).Entries);

        File.WriteAllText(FilePath, "[null, {\"name\":\"easyai\"}, {\"name\":\"ok-one\",\"version\":\"1\"}]");
        McpServerRegistry loaded = McpServerRegistry.Load(FilePath);
        Assert.Single(loaded.Entries);
        Assert.Equal("ok-one", loaded.Entries[0].Name);
        Assert.False(McpServerRegistry.IsApproved(loaded.Entries[0]));

        Assert.Empty(McpServerRegistry.Load(_root).Entries);
    }

    [Fact]
    public void SaveWritesTheFileAndKeepsTheFieldsAndTheRunFolderWorkingFolder()
    {
        McpServerRegistry registry = NewRegistry();
        registry.Add(Entry());
        registry.Approve("fs-reader");

        JArray json = JArray.Parse(File.ReadAllText(FilePath));
        JObject item = (JObject)json[0];
        Assert.Equal("fs-reader", item.Value<string>("name"));
        Assert.Equal("{run}", item.Value<string>("workingFolder"));
        Assert.Equal(new[] { "read_file", "list_dir" }, item["tools"]!.Select(t => t.Value<string>()).ToArray());
        Assert.False(File.Exists(FilePath + ".tmp"));
    }

    [Theory]
    [InlineData("fs-reader", true)]
    [InlineData("Search_2", true)]
    [InlineData("a", true)]
    [InlineData("", false)]
    [InlineData("easyai", false)]
    [InlineData("EasyAI", false)]
    [InlineData("my-easyai-x", false)]
    [InlineData("a b", false)]
    [InlineData("a/b", false)]
    [InlineData("a__b", false)]
    [InlineData("a.b", false)]
    public void NameValidation(string name, bool ok)
    {
        Assert.Equal(ok, McpServerRegistry.IsValidName(name));
    }

    [Fact]
    public void ApprovedSetHashIsEmptyWithoutApprovedServersAndKeyIsUnchanged()
    {
        Assert.Equal(string.Empty, McpServerRegistry.ApprovedSetHash(null));
        Assert.Equal(string.Empty, McpServerRegistry.ApprovedSetHash(new[] { Entry() }));

        IReadOnlyList<string> skills = new[] { "skill" };
        IReadOnlyList<string> before = AgentRunFiles.CacheHashTexts(skills, "memory", "digest", "tag");
        IReadOnlyList<string> empty = AgentRunFiles.CacheHashTexts(skills, "memory", "digest", "tag", string.Empty, McpServerRegistry.ApprovedSetHash(new[] { Entry() }));

        Assert.Equal(before, empty);
        Assert.Equal(AgentRunFiles.SkillPackHash(before), AgentRunFiles.SkillPackHash(empty));
    }

    [Fact]
    public void ApprovedSetHashChangesWithTheSetAndIgnoresOrder()
    {
        McpServerEntry one = Approved("one");
        McpServerEntry two = Approved("two");
        string hashOne = McpServerRegistry.ApprovedSetHash(new[] { one });
        string hashBoth = McpServerRegistry.ApprovedSetHash(new[] { one, two });
        string hashBothReversed = McpServerRegistry.ApprovedSetHash(new[] { two, one });

        Assert.NotEqual(string.Empty, hashOne);
        Assert.NotEqual(hashOne, hashBoth);
        Assert.Equal(hashBoth, hashBothReversed);
        Assert.Equal(hashOne, McpServerRegistry.ApprovedSetHash(new[] { one, Entry("three") }));

        McpServerEntry newVersion = Approved("one", "2.0.0");
        Assert.NotEqual(hashOne, McpServerRegistry.ApprovedSetHash(new[] { newVersion }));

        IReadOnlyList<string> skills = new[] { "skill" };
        string plainKey = AgentRunFiles.SkillPackHash(AgentRunFiles.CacheHashTexts(skills, "memory", string.Empty, "tag"));
        string withOne = AgentRunFiles.SkillPackHash(AgentRunFiles.CacheHashTexts(skills, "memory", string.Empty, "tag", string.Empty, hashOne));
        string withBoth = AgentRunFiles.SkillPackHash(AgentRunFiles.CacheHashTexts(skills, "memory", string.Empty, "tag", string.Empty, hashBoth));
        Assert.NotEqual(plainKey, withOne);
        Assert.NotEqual(withOne, withBoth);
    }

    private const string Run = @"C:\Users\x\AppData\Local\EasyAI\agent-runs\20261010-101010-abcdef12";
    private const string Exe = @"C:\App\agent\EasyLabel.Ai.Mcp.exe";

    [Fact]
    public void McpJsonWithNoApprovedServerHasOnlyTheBuiltInServerAsBefore()
    {
        string plain = AgentRunFiles.BuildMcpConfigJson(Exe, Run);

        Assert.Equal(plain, AgentRunFiles.BuildMcpConfigJson(Exe, Run, null));
        Assert.Equal(plain, AgentRunFiles.BuildMcpConfigJson(Exe, Run, Array.Empty<McpServerEntry>()));
        Assert.Equal(plain, AgentRunFiles.BuildMcpConfigJson(Exe, Run, new[] { Entry() }));
        Assert.Single(((JObject)JObject.Parse(plain)["mcpServers"]!).Properties());
    }

    [Fact]
    public void McpJsonHasTheBuiltInAndEachApprovedServerWithRunReplaced()
    {
        McpServerEntry one = Approved("one");
        McpServerEntry two = Approved("two");
        two.Args = new List<string> { "--dir={run}\\data", "plain" };
        two.ApprovedHash = McpServerRegistry.ComputeHash(two);

        JObject servers = (JObject)JObject.Parse(AgentRunFiles.BuildMcpConfigJson(Exe, Run, new[] { one, two, Entry("not-approved") }))["mcpServers"]!;

        Assert.Equal(new[] { "easyai", "one", "two" }, servers.Properties().Select(p => p.Name).ToArray());
        Assert.Equal(Exe, servers["easyai"]!.Value<string>("command"));
        Assert.Equal(new[] { "--run", Run }, servers["easyai"]!["args"]!.Select(t => t.Value<string>()).ToArray());
        Assert.Equal(@"C:\Tools\fs-reader.exe", servers["one"]!.Value<string>("command"));
        Assert.Equal(new[] { "--root", Run, "--readonly" }, servers["one"]!["args"]!.Select(t => t.Value<string>()).ToArray());
        Assert.Equal(new[] { "--dir=" + Run + "\\data", "plain" }, servers["two"]!["args"]!.Select(t => t.Value<string>()).ToArray());
        Assert.Null(servers["not-approved"]);
    }

    [Fact]
    public void AnEditedApprovedEntryDoesNotReachMcpJsonOrTheCommandLine()
    {
        McpServerEntry edited = Approved("one");
        edited.Version = "9.9.9";

        Assert.Single(((JObject)JObject.Parse(AgentRunFiles.BuildMcpConfigJson(Exe, Run, new[] { edited }))["mcpServers"]!).Properties());
        Assert.Equal(AgentCommandLine.AllowedTools, AgentCommandLine.BuildAllowedTools(new[] { edited }));
    }

    [Fact]
    public void AllowedToolsListEachApprovedToolOneByOneWithoutWildcard()
    {
        Assert.Equal(AgentCommandLine.AllowedTools, AgentCommandLine.BuildAllowedTools(null));
        Assert.Equal(AgentCommandLine.AllowedTools, AgentCommandLine.BuildAllowedTools(new[] { Entry() }));

        string list = AgentCommandLine.BuildAllowedTools(new[] { Approved("one"), Approved("two"), Entry("not-approved") });
        string[] parts = list.Split(',');

        Assert.Equal(AgentCommandLine.AllowedTools.Split(',').Concat(new[] { "mcp__one__read_file", "mcp__one__list_dir", "mcp__two__read_file", "mcp__two__list_dir" }).ToArray(), parts);
        Assert.DoesNotContain(parts, p => p.Contains('*'));
        Assert.DoesNotContain(parts, p => p.Contains("not-approved"));

        IReadOnlyList<string> args = AgentCommandLine.BuildArguments("p", "mcp.json", "sys.md", null, new[] { Approved("one") });
        Assert.Equal(AgentCommandLine.AllowedTools + ",mcp__one__read_file,mcp__one__list_dir", args[args.ToList().IndexOf("--allowedTools") + 1]);
        Assert.Contains("--strict-mcp-config", args);

        IReadOnlyList<string> plain = AgentCommandLine.BuildArguments("p", "mcp.json", "sys.md");
        Assert.Equal(plain.ToArray(), AgentCommandLine.BuildArguments("p", "mcp.json", "sys.md", null, Array.Empty<McpServerEntry>()).ToArray());
    }

    [Fact]
    public void CodexGetsCommandArgsAndEnabledToolsPerApprovedServerOnly()
    {
        IReadOnlyList<string> plain = AgentCommandLine.BuildCodexArguments("S", "p", Exe, Run, "o.txt", false);
        IReadOnlyList<string> none = AgentCommandLine.BuildCodexArguments("S", "p", Exe, Run, "o.txt", false, null, new[] { Entry() });
        Assert.Equal(plain.ToArray(), none.ToArray());

        IReadOnlyList<string> args = AgentCommandLine.BuildCodexArguments("S", "p", Exe, Run, "o.txt", false, null, new[] { Approved("one"), Entry("not-approved") });

        Assert.Contains("mcp_servers.one.command=\"C:\\\\Tools\\\\fs-reader.exe\"", args);
        Assert.Contains("mcp_servers.one.args=[\"--root\",\"" + Run.Replace("\\", "\\\\") + "\",\"--readonly\"]", args);
        Assert.Contains("mcp_servers.one.enabled_tools=[\"read_file\",\"list_dir\"]", args);
        Assert.Contains("mcp_servers.one.default_tools_approval_mode=\"approve\"", args);
        Assert.DoesNotContain(args, a => a.Contains("not-approved"));
        Assert.Contains("mcp_servers.easyai.command=\"C:\\\\App\\\\agent\\\\EasyLabel.Ai.Mcp.exe\"", args);
        Assert.StartsWith("Use only the easyai MCP tools", args[^1]);
    }

    [Fact]
    public void RunFolderWritesMcpJsonWithApprovedServers()
    {
        AgentRunInput input = new AgentRunInput("a.png", "a.png", 10, 10, new[] { "NG" }, Array.Empty<AgentShapeInfo>(), "p", null, 0, false);
        AgentRunFolder folder = AgentRunFolder.Create(_root, new DateTime(2026, 10, 10, 10, 10, 10), Guid.Parse("abcdef12-0000-0000-0000-000000000000"), input, Exe, "system", new[] { Approved("one") });

        JObject servers = (JObject)JObject.Parse(File.ReadAllText(folder.McpConfigPath))["mcpServers"]!;
        Assert.Equal(new[] { "easyai", "one" }, servers.Properties().Select(p => p.Name).ToArray());
        Assert.Equal(folder.Path, servers["one"]!["args"]![1]!.Value<string>());
    }
}
