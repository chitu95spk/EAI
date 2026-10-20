using EasyLabel.SegAI.Engine;

namespace EasyLabel.SegAI.Tests;

public sealed class SegAiSplitTimingTests
{
    [Fact]
    public void SplitTimings_BeforeAnyPredict_ReturnsNullWhenExportExists()
    {
        // SKIPPED (nothing proven) when the DLL predates EASYAI_SEGAI_SPLIT_TIMING: the export is absent, so there is nothing to read.
        if (SegAiInferer.SplitTimingApiVersion < 1) return;

        using var inferer = new SegAiInferer();
        // No Predict has run on this handle, so no split was measured.
        Assert.Null(inferer.GetNativeSplitTimings());
    }
}
