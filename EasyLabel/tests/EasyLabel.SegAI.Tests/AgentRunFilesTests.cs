using System.Diagnostics;
using EasyAI.Desktop.Services;
using EasyAI.Desktop.Services.Agent;
using Newtonsoft.Json.Linq;

namespace EasyLabel.SegAI.Tests;

public sealed class AgentRunFilesTests
{
    private static AgentRunInput Input(string prompt = "mark every scratch", string imagePath = @"E:\My Images\a b.png")
    {
        AgentShapeInfo existing = new AgentShapeInfo("NG", "polygon", new List<double[]> { new[] { 1.0, 2.0 }, new[] { 3.5, 4.5 }, new[] { 5.0, 6.0 } });
        return new AgentRunInput(imagePath, "a b.png", 2520, 2600, new[] { "NG", "OK" }, new[] { existing }, prompt, @"C:\Users\x\AppData\Local\EasyAI\models", 1, false);
    }

    [Fact]
    public void CommandLineIsTheProvenFlagSetInOrder()
    {
        IReadOnlyList<string> args = AgentCommandLine.BuildArguments("the prompt", @"C:\run\mcp.json", @"C:\run\system.md");

        Assert.Equal(
            new[]
            {
                "-p", "the prompt",
                "--mcp-config", @"C:\run\mcp.json",
                "--strict-mcp-config",
                "--tools", "",
                "--allowedTools", "mcp__easyai__get_image_info,mcp__easyai__get_crop,mcp__easyai__get_reference,mcp__easyai__list_shapes,mcp__easyai__sam2_segment,mcp__easyai__detect_points,mcp__easyai__pick_candidates,mcp__easyai__propose_shapes,mcp__easyai__memory_read,mcp__easyai__memory_add,mcp__easyai__grid_crop,mcp__easyai__render_overlay,mcp__easyai__measure",
                "--system-prompt-file", @"C:\run\system.md",
                "--disable-slash-commands",
                "--setting-sources", "",
                "--no-session-persistence",
                "--output-format", "stream-json", "--verbose",
                "--max-turns", "14",
                "--model", "sonnet",
                "--effort", "medium",
            },
            args.ToArray());
    }

    [Fact]
    public void ArgumentsWithSpacesQuotesAndNewLinesStayOneArgumentEach()
    {
        string prompt = "mark \"every\" scratch\r\nand the \\ dent";
        string mcp = @"C:\Users\Nguyen Van A\AppData\Local\EasyAI\agent-runs\20261008-101010-abcdef12\mcp.json";
        IReadOnlyList<string> args = AgentCommandLine.BuildArguments(prompt, mcp, @"C:\run\system.md");

        ProcessStartInfo info = AgentRunner.DefaultStartInfo(@"C:\claude\claude.exe", args, @"C:\run", @"C:\run\t");

        Assert.Equal(args.Count, info.ArgumentList.Count);
        Assert.Equal(prompt, info.ArgumentList[1]);
        Assert.Equal(mcp, info.ArgumentList[3]);
        Assert.Equal(string.Empty, info.ArgumentList[6]);
        Assert.Equal(args.ToArray(), info.ArgumentList.ToArray());
    }

    [Fact]
    public void DefaultStartInfoIsHiddenRedirectedAndUsesTheRunFolder()
    {
        ProcessStartInfo info = AgentRunner.DefaultStartInfo(@"C:\claude\claude.exe", new[] { "-p", "x" }, @"C:\run", @"C:\run\t");

        Assert.Equal(@"C:\claude\claude.exe", info.FileName);
        Assert.Equal(@"C:\run", info.WorkingDirectory);
        Assert.False(info.UseShellExecute);
        Assert.True(info.CreateNoWindow);
        Assert.True(info.RedirectStandardInput);
        Assert.True(info.RedirectStandardOutput);
        Assert.True(info.RedirectStandardError);
        Assert.Equal(@"C:\run\t", info.Environment["TEMP"]);
        Assert.Equal(@"C:\run\t", info.Environment["TMP"]);
        Assert.Equal(TimeSpan.FromMinutes(5), AgentRunner.DefaultTimeout);
    }

    [Fact]
    public void RequestJsonHasTheFieldsOfTheMcpRunRequest()
    {
        JObject json = JObject.Parse(AgentRunFiles.BuildRequestJson(Input()));

        Assert.Equal(@"E:\My Images\a b.png", json.Value<string>("imagePath"));
        Assert.Equal(2520, json.Value<int>("imageWidth"));
        Assert.Equal(2600, json.Value<int>("imageHeight"));
        Assert.Equal(new[] { "NG", "OK" }, json["classNames"]!.Select(t => t.Value<string>()).ToArray());
        Assert.Equal("mark every scratch", json.Value<string>("prompt"));
        Assert.Equal(@"C:\Users\x\AppData\Local\EasyAI\models", json.Value<string>("modelsFolder"));

        JObject shape = (JObject)json["existingShapes"]![0]!;
        Assert.Equal("NG", shape.Value<string>("label"));
        Assert.Equal("polygon", shape.Value<string>("shapeType"));
        Assert.Equal(3, shape["points"]!.Count());
        Assert.Equal(3.5, shape["points"]![1]![0]!.Value<double>());
        Assert.Equal(4.5, shape["points"]![1]![1]!.Value<double>());
    }

    [Fact]
    public void RequestJsonWithoutModelsFolderHasNull()
    {
        AgentRunInput input = new AgentRunInput("a.png", "a.png", 10, 10, new[] { "NG", "OK" }, Array.Empty<AgentShapeInfo>(), "p", null, 0, false);
        JObject json = JObject.Parse(AgentRunFiles.BuildRequestJson(input));
        Assert.Equal(JTokenType.Null, json["modelsFolder"]!.Type);
        Assert.Empty(json["existingShapes"]!);
    }

    [Fact]
    public void McpConfigNamesTheEasyAiServerWithTheRunFolder()
    {
        string exe = @"C:\Program Files\EasyAI\agent\EasyLabel.Ai.Mcp.exe";
        string run = @"C:\Users\Nguyen Van A\AppData\Local\EasyAI\agent-runs\20261008-101010-abcdef12";

        JObject json = JObject.Parse(AgentRunFiles.BuildMcpConfigJson(exe, run));

        JObject server = (JObject)json["mcpServers"]!["easyai"]!;
        Assert.Equal(exe, server.Value<string>("command"));
        Assert.Equal(new[] { "--run", run }, server["args"]!.Select(t => t.Value<string>()).ToArray());
        Assert.Single(((JObject)json["mcpServers"]!).Properties());
    }

    [Fact]
    public void SystemPromptIsThePrefixAndTheSkillsInOrder()
    {
        string text = AgentRunFiles.BuildSystemPrompt(new[] { "# A\nfirst", "# B\nsecond\n" });

        Assert.StartsWith("You label images for an annotation tool through the easyai MCP tools. Follow these procedures.", text);
        Assert.True(text.IndexOf("# A", StringComparison.Ordinal) < text.IndexOf("# B", StringComparison.Ordinal));
        Assert.Equal(
            "You label images for an annotation tool through the easyai MCP tools. Follow these procedures.\n\n# A\nfirst\n\n# B\nsecond\n",
            text);
    }

    [Fact]
    public void SkillTextsAreReadInFileNameOrderAndTheHashFollowsTheContent()
    {
        FakeAgentEnvironment env = new FakeAgentEnvironment();
        env.AddFile(@"C:\app\agent\Skills\read-image.md", "read");
        env.AddFile(@"C:\app\agent\Skills\find-objects.md", "find");
        env.AddFile(@"C:\app\agent\Skills\label-rules.md", "rules");
        env.AddFile(@"C:\app\agent\Skills\notes.txt", "ignored");

        IReadOnlyList<string> texts = AgentRunFiles.ReadSkillTexts(env, @"C:\app\agent");

        Assert.Equal(new[] { "find", "rules", "read" }, texts.ToArray());
        string hash = AgentRunFiles.SkillPackHash(texts);
        Assert.Equal(64, hash.Length);
        Assert.Equal(hash, AgentRunFiles.SkillPackHash(new[] { "find", "rules", "read" }));
        Assert.NotEqual(hash, AgentRunFiles.SkillPackHash(new[] { "find", "rules", "read2" }));
        Assert.NotEqual(hash, AgentRunFiles.SkillPackHash(new[] { "rules", "find", "read" }));
    }

    [Fact]
    public void RunFolderHoldsTheThreeInputFilesAndATempFolder()
    {
        using AgentScratch scratch = new AgentScratch("runfolder");
        Guid id = Guid.Parse("abcdef12-0000-0000-0000-000000000000");

        AgentRunFolder folder = AgentRunFolder.Create(
            scratch.Path, new DateTime(2026, 10, 8, 13, 5, 9), id, Input(), @"C:\app\agent\EasyLabel.Ai.Mcp.exe", "system text");

        Assert.Equal(scratch.Combine("20261008-130509-abcdef12"), folder.Path);
        Assert.True(AgentRunRetention.IsRunFolderName(Path.GetFileName(folder.Path)));
        Assert.True(File.Exists(folder.RequestPath));
        Assert.True(File.Exists(folder.McpConfigPath));
        Assert.Equal("system text", File.ReadAllText(folder.SystemPromptPath));
        Assert.True(Directory.Exists(folder.TempPath));
        Assert.Equal(folder.Path, JObject.Parse(File.ReadAllText(folder.McpConfigPath))["mcpServers"]!["easyai"]!["args"]![1]!.Value<string>());
        Assert.Equal(Path.Combine(folder.Path, "proposals.json"), folder.ProposalsPath);
    }

    [Fact]
    public void RunFolderNamesAreRecognisedStrictly()
    {
        Assert.True(AgentRunRetention.IsRunFolderName("20261008-130509-abcdef12"));
        Assert.True(AgentRunRetention.IsRunFolderName("20261008-130509-ABCDEF12"));
        Assert.False(AgentRunRetention.IsRunFolderName("log.csv"));
        Assert.False(AgentRunRetention.IsRunFolderName("20261008-130509-abcdefg2"));
        Assert.False(AgentRunRetention.IsRunFolderName("20261008-130509-abcdef1"));
        Assert.False(AgentRunRetention.IsRunFolderName("2026100a-130509-abcdef12"));
        Assert.False(AgentRunRetention.IsRunFolderName(string.Empty));
    }

    [Fact]
    public void RetentionKeepsTheNewest50AndNothingOlderThan7Days()
    {
        DateTime now = new DateTime(2026, 10, 8, 12, 0, 0, DateTimeKind.Utc);
        List<AgentRunEntry> entries = new List<AgentRunEntry>();
        for (int i = 0; i < 55; i++)
        {
            // Entry i is i hours old: 55 recent folders, all inside 7 days.
            entries.Add(new AgentRunEntry("20261008-0000" + (i % 10) + "0-" + i.ToString("x8"), now.AddHours(-i)));
        }

        entries.Add(new AgentRunEntry("20260101-000000-00000000", now.AddDays(-30)));
        entries.Add(new AgentRunEntry("log.csv", now.AddDays(-400)));
        entries.Add(new AgentRunEntry("my notes", now.AddDays(-400)));

        IReadOnlyList<string> doomed = AgentRunRetention.SelectToDelete(entries, now);

        // The 5 oldest of the 55 recent ones (index 50..54) and the 30 day old one; names that are not run folders are never listed.
        Assert.Equal(6, doomed.Count);
        Assert.Contains("20260101-000000-00000000", doomed);
        for (int i = 50; i < 55; i++)
        {
            Assert.Contains(entries[i].Name, doomed);
        }

        Assert.DoesNotContain("log.csv", doomed);
        Assert.DoesNotContain("my notes", doomed);
        Assert.DoesNotContain(entries[0].Name, doomed);
    }

    [Fact]
    public void RetentionDeletesOnlyRunFoldersInsideTheRoot()
    {
        using AgentScratch scratch = new AgentScratch("retention");
        DateTime now = DateTime.UtcNow;
        string old = scratch.Combine("20200101-000000-aaaaaaaa");
        string fresh = scratch.Combine("20261008-000000-bbbbbbbb");
        string other = scratch.Combine("keep-me");
        foreach (string folder in new[] { old, fresh, other })
        {
            Directory.CreateDirectory(Path.Combine(folder, "t"));
        }

        File.WriteAllText(Path.Combine(old, "request.json"), "{}");
        File.WriteAllText(scratch.Combine("log.csv"), "x");
        Directory.SetLastWriteTimeUtc(old, now.AddDays(-30));
        Directory.SetLastWriteTimeUtc(fresh, now.AddHours(-1));
        Directory.SetLastWriteTimeUtc(other, now.AddDays(-30));

        int deleted = AgentRunRetention.Cleanup(scratch.Path, now);

        Assert.Equal(1, deleted);
        Assert.False(Directory.Exists(old));
        Assert.True(Directory.Exists(fresh));
        Assert.True(Directory.Exists(other));
        Assert.True(File.Exists(scratch.Combine("log.csv")));
        Assert.Equal(0, AgentRunRetention.Cleanup(scratch.Combine("missing"), now));
    }

    [Fact]
    public void ReferencesAreWrittenToRequestJsonOnlyWhenAttached()
    {
        AgentRunInput none = new AgentRunInput("C:\\img\\a.png", "a.png", 100, 100, new[] { "NG" }, Array.Empty<AgentShapeInfo>(), "p", null, 0, false);
        AgentRunInput with = new AgentRunInput("C:\\img\\a.png", "a.png", 100, 100, new[] { "NG" }, Array.Empty<AgentShapeInfo>(), "p", null, 0, false,
            references: new[] { "C:\\hints\\defect.png", "C:\\hints\\note.txt" });

        Assert.DoesNotContain("references", AgentRunFiles.BuildRequestJson(none));
        Newtonsoft.Json.Linq.JObject root = Newtonsoft.Json.Linq.JObject.Parse(AgentRunFiles.BuildRequestJson(with));
        Assert.Equal(2, root["references"]!.Count());
        Assert.Equal("defect.png", (string?)root["references"]![0]!["name"]);
        Assert.Equal("C:\\hints\\note.txt", (string?)root["references"]![1]!["path"]);
        Assert.Equal(2, with.WithExperienceBlock("x").References.Count);
        Assert.Equal(2, with.WithKind(AgentRunKind.Check).References.Count);
    }

    [Fact]
    public void ReferenceBlockIsEmptyWithoutFilesAndChangesWhenAFileChanges()
    {
        Assert.Equal(string.Empty, AgentRunFiles.ReferenceBlock(Array.Empty<string>()));

        string folder = Path.Combine(Path.GetTempPath(), "easyai-ref-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string file = Path.Combine(folder, "defect.png");
            File.WriteAllBytes(file, new byte[] { 1, 2, 3 });
            string first = AgentRunFiles.ReferenceBlock(new[] { file });
            File.WriteAllBytes(file, new byte[] { 1, 2, 3, 4, 5 });
            string second = AgentRunFiles.ReferenceBlock(new[] { file });

            Assert.Contains("get_reference", first);
            Assert.Contains("defect.png", first);
            Assert.NotEqual(first, second);
            Assert.Contains("(missing)", AgentRunFiles.ReferenceBlock(new[] { Path.Combine(folder, "gone.png") }));
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }
}
