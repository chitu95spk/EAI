p = r'E:\Code\EasyVision_Unisen - NoService\EasyLabel\tests\EasyLabel.SegAI.Tests\AgentCodexTests.cs'
raw = open(p, 'rb').read()
bom = raw.startswith(b'\xef\xbb\xbf')
crlf = b'\r\n' in raw
s = raw.decode('utf-8-sig').replace('\r\n', '\n')
i = s.index('    private const string Banner')
t = r'''    private const string Banner = "Reading additional input from stdin...\nOpenAI Codex v0.162.0-alpha.17.2\n--------\nworkdir: C:\\x\nmodel: gpt-6-sol\nprovider: openai\napproval: never\nsandbox: read-only\nsession id: 01a132fa\n--------\n";

    [Fact]
    public void FailedCodexRunShowsTheRealErrorNotTheStartBanner()
    {
        AgentRunResult withError = AgentOutcomeClassifier.ClassifyCodex(1, string.Empty, Banner + "user\nlong prompt\nERROR: The model gpt-6-sol does not exist", string.Empty);
        Assert.Equal(AgentOutcomeKind.Failed, withError.Kind);
        Assert.Equal("ERROR: The model gpt-6-sol does not exist", withError.Message);

        AgentRunResult noError = AgentOutcomeClassifier.ClassifyCodex(1, string.Empty, Banner + "turn limit reached", string.Empty);
        Assert.Equal("turn limit reached", noError.Message);
    }
}
'''
s = s[:i] + t
if crlf:
    s = s.replace('\n', '\r\n')
open(p, 'wb').write((b'\xef\xbb\xbf' if bom else b'') + s.encode('utf-8'))
