p = r'E:\Code\EasyVision_Unisen - NoService\EasyLabel\tests\EasyLabel.SegAI.Tests\BootstrapBaselineTests.cs'
raw = open(p, 'rb').read()
bom = raw.startswith(b'\xef\xbb\xbf')
crlf = b'\r\n' in raw
s = raw.decode('utf-8-sig').replace('\r\n', '\n')
old = "paths.AgentMemoryFile, digest, null, null, AgentRunKind.Label, vocabulary));"
new = "paths.AgentMemoryFile, digest, null, null, AgentRunKind.Label, vocabulary, referencePaths));"
assert old in s
s = s.replace(old, new, 1)
anchor = "        string modelsFolder = LabelRunEnv.Get(\"EAI_LABEL_MODELS\")"
assert anchor in s
s = s.replace(anchor, "        string[] referencePaths = (LabelRunEnv.Get(\"EAI_LABEL_REFERENCES\") ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(Path.GetFullPath).ToArray();\n" + anchor, 1)
s = s.replace("EAI_LABEL_TOOL (claude|codex, default claude),", "EAI_LABEL_TOOL (claude|codex, default claude), EAI_LABEL_REFERENCES (semicolon list of hint files attached to every image),", 1)
if crlf:
    s = s.replace('\n', '\r\n')
open(p, 'wb').write((b'\xef\xbb\xbf' if bom else b'') + s.encode('utf-8'))
