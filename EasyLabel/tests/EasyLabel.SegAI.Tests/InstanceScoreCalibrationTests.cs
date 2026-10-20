using EasyLabel.SegAI.Options;
using EasyLabel.SegAI.Project;
using EasyLabel.Core;

namespace EasyLabel.SegAI.Tests;

/// <summary>EAI-LOGO: the instance score calibration is the identity when off, monotone, and maps the anchor to 0.90.</summary>
public sealed class InstanceScoreCalibrationTests
{
    [Theory]
    [InlineData(0f)]
    [InlineData(1f)]
    [InlineData(-0.2f)]
    public void OffAnchorsKeepRawScores(float anchor)
    {
        Assert.Equal(0.37f, SegInferenceSettings.CalibrateInstanceScore(0.37f, anchor));
    }

    [Fact]
    public void AnchorMapsToNinetyPercentAndIsMonotone()
    {
        Assert.Equal(0.9f, SegInferenceSettings.CalibrateInstanceScore(0.45f, 0.45f), 4);
        Assert.Equal(1f, SegInferenceSettings.CalibrateInstanceScore(1f, 0.45f), 4);
        Assert.Equal(0f, SegInferenceSettings.CalibrateInstanceScore(0f, 0.45f), 4);
        float previous = -1f;
        for (float raw = 0f; raw <= 1.0001f; raw += 0.01f)
        {
            float mapped = SegInferenceSettings.CalibrateInstanceScore(raw, 0.45f);
            Assert.True(mapped >= previous);
            Assert.InRange(mapped, 0f, 1f);
            previous = mapped;
        }
    }

    [Fact]
    public void StoreRoundTripsAndRejectsOutOfRange()
    {
        string root = Path.Combine(Path.GetTempPath(), "isc-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, ".easyai"));
            SegProjectStore store = new SegProjectStore(ProjectPaths.Legacy(root));
            Assert.Equal(0f, store.LoadInstanceScoreAnchor());
            store.SaveInstanceScoreAnchor(0.45f);
            Assert.Equal(0.45f, store.LoadInstanceScoreAnchor(), 4);
            store.SaveInstanceScoreAnchor(0f);
            Assert.Equal(0f, store.LoadInstanceScoreAnchor());
            Assert.Throws<ArgumentOutOfRangeException>(() => store.SaveInstanceScoreAnchor(1.5f));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }
}
