import re
base = r'E:\Code\EasyVision_Unisen - NoService\EasyLabel'

def edit(path, pairs, append=None):
    raw = open(path, 'rb').read()
    bom = raw.startswith(b'\xef\xbb\xbf')
    crlf = b'\r\n' in raw
    s = raw.decode('utf-8-sig').replace('\r\n', '\n')
    for old, new in pairs:
        assert old in s, old
        s = s.replace(old, new, 1)
    if append:
        i = s.rstrip().rfind('}')
        s = s[:i].rstrip('\n') + '\n' + append + '}\n'
    if crlf:
        s = s.replace('\n', '\r\n')
    open(path, 'wb').write((b'\xef\xbb\xbf' if bom else b'') + s.encode('utf-8'))

edit(base + r'\src\EasyAI.Wpf\Services\Agent\AgentResults.cs', [
    ('        "invalid api key",\n', '        "invalid api key",\n        "missing bearer",\n'),
    ('bool signedOut = AgentLoginDetector.LooksSignedOutShort(error) ||',
     '// The Codex stderr is a log, never an answer, so its length does not matter: a 401 after five reconnect lines is still a sign-in problem.\n        bool signedOut = AgentLoginDetector.LooksSignedOut(error) ||'),
    ('            if (trimmed.StartsWith("ERROR", StringComparison.OrdinalIgnoreCase) || trimmed.StartsWith("stream error", StringComparison.OrdinalIgnoreCase))\n            {\n                errors.Add(trimmed);\n            }',
     '            bool isError = trimmed.StartsWith("ERROR", StringComparison.OrdinalIgnoreCase) || trimmed.StartsWith("stream error", StringComparison.OrdinalIgnoreCase);\n            // "ERROR: Reconnecting... 2/5" repeats the same failure; the final error line says why.\n            if (isError && !trimmed.Contains("Reconnecting...", StringComparison.OrdinalIgnoreCase) && !errors.Contains(trimmed))\n            {\n                errors.Add(trimmed);\n            }'),
])

edit(base + r'\tests\EasyLabel.SegAI.Tests\AgentCodexTests.cs', [], append=r'''
    [Fact]
    public void CodexUnauthorizedAfterReconnectsIsNotSignedIn()
    {
        string stderr = Banner + "user\nprompt\nERROR: Reconnecting... 1/5\nERROR: Reconnecting... 2/5\nERROR: Reconnecting... 3/5\nERROR: Reconnecting... 4/5\nERROR: Reconnecting... 5/5\n"
            + "ERROR: unexpected status 401 Unauthorized: Missing bearer or basic authentication in header, url: https://api.openai.com/v1/responses\n"
            + "ERROR: unexpected status 401 Unauthorized: Missing bearer or basic authentication in header, url: https://api.openai.com/v1/responses";
        AgentRunResult result = AgentOutcomeClassifier.ClassifyCodex(1, string.Empty, stderr, string.Empty);

        Assert.Equal(AgentOutcomeKind.NotSignedIn, result.Kind);
        Assert.DoesNotContain("Reconnecting", result.Message);
        Assert.StartsWith("ERROR: unexpected status 401", result.Message);
    }
''')
