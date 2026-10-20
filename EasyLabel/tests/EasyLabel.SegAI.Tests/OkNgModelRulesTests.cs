using System.Globalization;
using EasyAI.Desktop.ViewModels;
using EasyLabel.Core.Models;
using EasyLabel.SegAI.AutoLabel;
using EasyLabel.SegAI.Inspection;
using Xunit;

namespace EasyLabel.SegAI.Tests;

public sealed class OkNgModelRulesTests
{
    [Fact]
    public void TryParse_ReadsNameAndVersion()
    {
        Assert.True(OkNgModelRules.TryParse("okng_v003.eaicls", out string name, out int version));
        Assert.Equal("okng", name);
        Assert.Equal(3, version);
    }

    [Theory]
    [InlineData("okng_v2.eaicls")]
    [InlineData("okng.eaicls")]
    [InlineData("okng_v003.segai")]
    [InlineData("_v003.eaicls")]
    [InlineData("okng_vabc.eaicls")]
    public void TryParse_RejectsOtherNames(string fileName)
    {
        Assert.False(OkNgModelRules.TryParse(fileName, out _, out _));
    }

    [Fact]
    public void LatestPerName_KeepsNewestVersionOfEachName()
    {
        IReadOnlyList<string> latest = OkNgModelRules.LatestPerName(new[]
        {
            "a_v001.eaicls", "a_v010.eaicls", "a_v002.eaicls", "b_v001.eaicls", "notes.txt",
        });

        Assert.Equal(new[] { "a_v010.eaicls", "b_v001.eaicls" }, latest);
    }

    [Fact]
    public void BaseName_IsEmptyForOtherFiles()
    {
        Assert.Equal("okng", OkNgModelRules.BaseName("okng_v003.eaicls"));
        Assert.Equal(string.Empty, OkNgModelRules.BaseName("seg_v001.segai"));
    }

    [Fact]
    public void VerdictOf_EqualToThresholdIsNg()
    {
        Assert.Equal(InspectVerdict.NG, OkNgModelRules.VerdictOf(0.5));
        Assert.Equal(InspectVerdict.OK, OkNgModelRules.VerdictOf(0.49));
        Assert.Equal(InspectVerdict.Error, OkNgModelRules.VerdictOf(double.NaN));
    }

    [Fact]
    public void ConfidenceOf_IsTheLargerProbability()
    {
        Assert.Equal(0.8, OkNgModelRules.ConfidenceOf(0.8), 9);
        Assert.Equal(0.8, OkNgModelRules.ConfidenceOf(0.2), 9);
        Assert.True(double.IsNaN(OkNgModelRules.ConfidenceOf(double.NaN)));
    }

    [Fact]
    public void ResultOf_CarriesVerdictScoreAndClassifierUncertainty()
    {
        DateTime now = new DateTime(2026, 10, 12, 0, 0, 0, DateTimeKind.Utc);
        InspectResult result = OkNgModelRules.ResultOf("C:\\img\\a.png", 0.9, "okng_v001.eaicls", 12.5, now);

        Assert.Equal(InspectVerdict.NG, result.Verdict);
        Assert.Equal(0.9f, result.Score, 3);
        Assert.Equal(0, result.Regions);
        Assert.Null(result.Error);
        Assert.NotNull(result.Uncertainty);
        Assert.Equal(UncertaintySignal.ClassProbability, result.Uncertainty!.Value.Signal);
    }

    [Fact]
    public void ResultOf_ProbabilityAtHalfIsUnsure()
    {
        InspectResult result = OkNgModelRules.ResultOf("a.png", 0.5, "okng_v001.eaicls", 1, DateTime.UtcNow);

        Assert.Equal(UncertaintyBucket.Unsure, result.Uncertainty!.Value.Bucket);
    }

    [Fact]
    public void ResultOf_NotANumberIsAnErrorRow()
    {
        InspectResult result = OkNgModelRules.ResultOf("a.png", double.NaN, "okng_v001.eaicls", 1, DateTime.UtcNow);

        Assert.Equal(InspectVerdict.Error, result.Verdict);
        Assert.NotNull(result.Error);
        Assert.Null(result.Uncertainty);
    }

    [Fact]
    public void SendBlockKey_BlocksOnlyOkNgModels()
    {
        Assert.Equal("SendOkNgUnsupported", OkNgModelRules.SendBlockKey("okng_v001.eaicls"));
        Assert.Null(OkNgModelRules.SendBlockKey("seg_v001.segai"));
        Assert.Null(OkNgModelRules.SendBlockKey(null));
    }

    [Fact]
    public void ModelPackErrorKey_DamagedFileHasItsOwnText()
    {
        Assert.Equal("ModelPackBadHash", OkNgModelRules.ModelPackErrorKey(ModelPackError.BadHash));
        Assert.Equal("ModelPackFailed", OkNgModelRules.ModelPackErrorKey(ModelPackError.Io));
        Assert.Null(OkNgModelRules.ModelPackErrorKey(ModelPackError.None));
    }

    [Fact]
    public void Licences_AreTrimmedAndDistinctInManifestOrder()
    {
        var manifest = new ModelPackManifest
        {
            Files = new List<ModelPackFile>
            {
                new ModelPackFile { Licence = "MIT " },
                new ModelPackFile { Licence = "MIT" },
                new ModelPackFile { Licence = " " },
                new ModelPackFile { Licence = "Apache-2.0" },
            },
        };

        Assert.Equal(new[] { "MIT", "Apache-2.0" }, OkNgModelRules.Licences(manifest));
    }

    [Fact]
    public void ConfidenceText_ShowsOnlyForClassifierVerdicts()
    {
        CultureInfo culture = CultureInfo.InvariantCulture;
        InspectResult classified = OkNgModelRules.ResultOf("a.png", 0.1, "okng_v001.eaicls", 1, DateTime.UtcNow);
        InspectResult segmented = new InspectResult("b.png", InspectVerdict.OK, 0, 0.2f, 1, "seg_v001.segai", DateTime.UtcNow, null, false, Array.Empty<SegAiSuggestion>(), 0, 0);

        Assert.StartsWith("90.0", InspectRules.ConfidenceText(classified, culture));
        Assert.Equal("-", InspectRules.ConfidenceText(segmented, culture));
        Assert.Equal("-", InspectRules.ConfidenceText(null, culture));
    }
}
