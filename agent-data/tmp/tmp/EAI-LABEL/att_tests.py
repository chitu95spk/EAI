base = r'E:\Code\EasyVision_Unisen - NoService\EasyLabel\tests\EasyLabel.SegAI.Tests'
p = base + r'\AgentRunFilesTests.cs'
raw = open(p, 'rb').read()
bom = raw.startswith(b'\xef\xbb\xbf')
crlf = b'\r\n' in raw
s = raw.decode('utf-8-sig').replace('\r\n', '\n')
t = r'''
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
'''
i = s.rstrip().rfind('}')
s = s[:i].rstrip('\n') + '\n' + t
if crlf:
    s = s.replace('\n', '\r\n')
open(p, 'wb').write((b'\xef\xbb\xbf' if bom else b'') + s.encode('utf-8'))
