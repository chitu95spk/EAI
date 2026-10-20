base = r'E:\Code\EasyVision_Unisen - NoService\EasyLabel'
src = base + r'\src\EasyAI.Wpf'

def edit(path, pairs):
    raw = open(path, 'rb').read()
    bom = raw.startswith(b'\xef\xbb\xbf')
    crlf = b'\r\n' in raw
    s = raw.decode('utf-8-sig').replace('\r\n', '\n')
    for old, new in pairs:
        assert old in s, (path, old)
        s = s.replace(old, new, 1)
    if crlf:
        s = s.replace('\n', '\r\n')
    open(path, 'wb').write((b'\xef\xbb\xbf' if bom else b'') + s.encode('utf-8'))

edit(src + r'\Services\Agent\AgentRunFiles.cs', [
    ('        string? vocabularyBlock = null)\n    {\n        Kind = kind;',
     '        string? vocabularyBlock = null,\n        IReadOnlyList<string>? references = null)\n    {\n        Kind = kind;\n        References = references ?? Array.Empty<string>();'),
    ('    /// <summary>The project memory file (<c>.easyai/agent-memory.md</c>)',
     '    /// <summary>\n    /// Full paths of the files the user attached to the prompt as hints (example pictures of the defect, short notes); the agent reads them with\n    /// get_reference. Empty = none, and then request.json, the prompt and the cache key are exactly as before.\n    /// </summary>\n    public IReadOnlyList<string> References { get; }\n\n    /// <summary>The project memory file (<c>.easyai/agent-memory.md</c>)'),
    ('OutcomeDigest, ExperienceBlock, PendingShapes, kind, VocabularyBlock);', 'OutcomeDigest, ExperienceBlock, PendingShapes, kind, VocabularyBlock, References);'),
    ('OutcomeDigest, experienceBlock, PendingShapes, kind, VocabularyBlock);', 'OutcomeDigest, experienceBlock, PendingShapes, kind, VocabularyBlock, References);'),
    ('        if (pendingShapes.Count > 0)\n        {\n            // Optional field: written only when there is something pending, so an ordinary request.json is unchanged.\n            root["pendingShapes"] = pendingShapes;\n        }\n',
     '        if (pendingShapes.Count > 0)\n        {\n            // Optional field: written only when there is something pending, so an ordinary request.json is unchanged.\n            root["pendingShapes"] = pendingShapes;\n        }\n\n        if (input.References.Count > 0)\n        {\n            // Optional field, same rule: only when the user attached hint files.\n            root["references"] = new JArray(input.References.Select(path => (object)new JObject { ["path"] = path, ["name"] = Path.GetFileName(path) }));\n        }\n'),
    ('    /// <summary>The system prompt: the fixed sentence followed by the skill texts',
     '''    /// <summary>
    /// The system-prompt block that tells the agent about the attached hint files (empty when none). The size and time of every file are part of
    /// the text, so the cache key (it hashes this block) changes when an attachment is replaced or edited.
    /// </summary>
    public static string ReferenceBlock(IReadOnlyList<string> references)
    {
        if (references == null || references.Count == 0)
        {
            return string.Empty;
        }

        StringBuilder text = new StringBuilder("The user attached " + references.Count + " hint file(s). Call get_reference (index 0.." + (references.Count - 1)
            + ") before you search: they show what to find (for example an example picture of the defect with its box). They are NOT the image to label.");
        for (int i = 0; i < references.Count; i++)
        {
            string signature = string.Empty;
            try
            {
                FileInfo file = new FileInfo(references[i]);
                signature = file.Exists ? " (" + file.Length + " bytes, " + file.LastWriteTimeUtc.Ticks + ")" : " (missing)";
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException)
            {
                signature = " (unreadable)";
            }

            text.Append("\\n").Append(i).Append(": ").Append(Path.GetFileName(references[i])).Append(signature);
        }

        return text.ToString();
    }

    /// <summary>The system prompt: the fixed sentence followed by the skill texts'''),
])

edit(src + r'\Services\AgentRunner.cs', [
    ('        string facts = AgentRunFiles.ImageFactsBlock(input);\n        string memory',
     '        string facts = AgentRunFiles.ImageFactsBlock(input);\n        string referenceBlock = AgentRunFiles.ReferenceBlock(input.References);\n        if (referenceBlock.Length > 0)\n        {\n            facts = facts.Length > 0 ? facts + "\\n\\n" + referenceBlock : referenceBlock;\n        }\n\n        string memory'),
])

edit(src + r'\Services\Agent\AgentCommandLine.cs', [
    ('mcp__easyai__get_crop,mcp__easyai__list_shapes,mcp__easyai__sam2_segment', 'mcp__easyai__get_crop,mcp__easyai__get_reference,mcp__easyai__list_shapes,mcp__easyai__sam2_segment'),
    ('{ "get_image_info", "get_crop", "list_shapes", "sam2_segment"', '{ "get_image_info", "get_crop", "get_reference", "list_shapes", "sam2_segment"'),
])

edit(base + r'\tests\EasyLabel.SegAI.Tests\AgentCodexTests.cs', [
    ('Assert.Equal(10, AgentCommandLine.CodexTools.Length);', 'Assert.Equal(11, AgentCommandLine.CodexTools.Length);'),
])
