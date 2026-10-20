p = r'E:\Code\EasyVision_Unisen - NoService\EasyLabel\tests\EasyLabel.SegAI.Tests\BootstrapBaselineTests.cs'
raw = open(p, 'rb').read()
bom = raw.startswith(b'\xef\xbb\xbf')
crlf = b'\r\n' in raw
s = raw.decode('utf-8-sig').replace('\r\n', '\n')

def rep(old, new):
    global s
    assert old in s, old
    s = s.replace(old, new, 1)

rep("        public AgentRunOptions Options = AgentRunOptions.Default;\n    }",
    "        public AgentRunOptions Options = AgentRunOptions.Default;\n        public AgentTool Tool = AgentTool.Claude;\n    }")
rep("        string? skillsText = LabelRunEnv.Get(\"EAI_LABEL_DISABLED_SKILLS\");",
    "        string toolText = (LabelRunEnv.Get(\"EAI_LABEL_TOOL\") ?? \"claude\").ToLowerInvariant();\n        if (toolText != \"claude\" && toolText != \"codex\")\n        {\n            throw new InvalidOperationException(\"EAI_LABEL_TOOL must be claude or codex: \" + toolText);\n        }\n\n        config.Tool = toolText == \"codex\" ? AgentTool.Codex : AgentTool.Claude;\n\n        string? skillsText = LabelRunEnv.Get(\"EAI_LABEL_DISABLED_SKILLS\");")
rep("                Options = levers.Options,\n", "                Options = levers.Options,\n                Tool = levers.Tool,\n")
rep("string? claudePath = agent.FindClaude();", "string? claudePath = levers.Tool == AgentTool.Codex ? agent.FindAgent() : agent.FindClaude();")
rep("/// EAI_LABEL_MODEL (default empty", "/// EAI_LABEL_TOOL (claude|codex, default claude), EAI_LABEL_MODEL (default empty")
if crlf:
    s = s.replace('\n', '\r\n')
open(p, 'wb').write((b'\xef\xbb\xbf' if bom else b'') + s.encode('utf-8'))
