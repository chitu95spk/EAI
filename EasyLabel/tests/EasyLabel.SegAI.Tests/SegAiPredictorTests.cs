using System.Runtime.InteropServices;
using EasyLabel.SegAI.Inspection;
using OpenCvSharp;

namespace EasyLabel.SegAI.Tests;

public sealed class SegAiPredictorTests
{
    private static string NewFolder(string name)
    {
        string dir = Path.Combine(SegAiFixtures.ScratchFolder("predictor"), name + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static Mat NoiseImage(int width, int height, int seed)
    {
        var bytes = new byte[width * height * 3];
        new Random(seed).NextBytes(bytes);
        var mat = new Mat(height, width, MatType.CV_8UC3);
        Marshal.Copy(bytes, 0, mat.Data, bytes.Length);
        return mat;
    }

    [Fact]
    public void Predict_WithoutModel_Throws()
    {
        using var session = SegAiSession.Create(NewFolder("nomodel"));
        using Mat image = NoiseImage(32, 32, 1);
        Assert.Throws<InvalidOperationException>(() => session.Predict(image));
        Assert.Throws<InvalidOperationException>(() => session.PredictFile("x.png"));
        Assert.Null(session.LoadedModel);
    }

    [Fact]
    public void LoadModel_CorruptFile_ReturnsProblemAndLoadsNothing()
    {
        string folder = NewFolder("corrupt");
        string path = Path.Combine(folder, "corrupt.segai");
        var bytes = new byte[512];
        new Random(7).NextBytes(bytes);
        File.WriteAllBytes(path, bytes);

        using var session = SegAiSession.Create(folder);
        var info = session.LoadModel(path);
        Assert.NotNull(info.Problem);
        Assert.Null(session.LoadedModel);
    }

    [Fact]
    public void Analyzer_TwoBlobs_SortedByCentroidAndFilteredByArea()
    {
        using var mask = new Mat(64, 64, MatType.CV_8UC1, Scalar.All(0));
        Cv2.Rectangle(mask, new OpenCvSharp.Rect(5, 5, 10, 10), Scalar.All(255), -1);
        Cv2.Rectangle(mask, new OpenCvSharp.Rect(40, 30, 6, 6), Scalar.All(255), -1);

        DefectRegionAnalysis all = DefectRegionAnalyzer.Analyze(mask);
        Assert.Equal(2, all.Count);
        Assert.Equal(136, all.ForegroundPixelCount);
        Assert.Equal(136, all.PixelArea);
        Assert.Equal(100, all.Regions[0].PixelArea);
        Assert.Equal(36, all.Regions[1].PixelArea);
        Assert.Equal(new System.Drawing.Rectangle(5, 5, 10, 10), all.Regions[0].Bounds);

        DefectRegionAnalysis large = DefectRegionAnalyzer.Analyze(mask, null, 50);
        Assert.Equal(1, large.Count);
        Assert.Equal(136, large.ForegroundPixelCount);

        using var empty = new Mat(8, 8, MatType.CV_8UC1, Scalar.All(0));
        Assert.Equal(0, DefectRegionAnalyzer.Analyze(empty).Count);
    }

    [SegAiFixtureFact(true, SegAiFixtures.Synthetic, SegAiFixtures.V2Both, SegAiFixtures.Scan)]
    public void Predict_FixtureModel_IsDeterministicAndConsistent()
    {
        string model = SegAiFixtures.ResolveAny(SegAiFixtures.Synthetic, SegAiFixtures.V2Both, SegAiFixtures.Scan)!;
        using var session = SegAiSession.Create(NewFolder("predict"));
        Assert.False(session.Inference.EnableGpu);

        var info = session.LoadModel(model);
        Assert.Null(info.Problem);
        Assert.NotNull(session.LoadedModel);

        using Mat image = NoiseImage(320, 240, 1234);
        SegAiResult first = session.Predict(image);
        SegAiResult second = session.Predict(image, new OpenCvSharp.Rect(0, 0, 320, 240));

        Assert.Null(first.Error);
        Assert.Equal(320 * 240, first.Mask.Length);
        Assert.Equal(first.Width * first.Height, first.Mask.Length);
        Assert.Equal(first.Mask.Length, first.RawMask.Length);
        Assert.Equal(first.RawMask, second.RawMask);
        Assert.Equal(first.Mask, second.Mask);
        Assert.Equal(first.IsOK, second.IsOK);
        Assert.Equal(first.Score, second.Score);
        Assert.All(first.Mask, b => Assert.True(b == 0 || b == 255 || first.RawConfidence == null));
        if (first.RawConfidence != null)
        {
            Assert.Equal(first.Mask.Length, first.RawConfidence.Length);
            Assert.NotNull(first.RegionScores);
            Assert.Equal(first.Regions.Count, first.RegionScores!.Length);
        }

        // An ROI outside the image is reported as an error result, not thrown.
        SegAiResult invalid = session.Predict(image, new OpenCvSharp.Rect(0, 0, 400, 240));
        Assert.NotNull(invalid.Error);
        Assert.False(invalid.IsOK);
    }

    [SegAiFixtureFact(true, SegAiFixtures.Synthetic, SegAiFixtures.V2Both, SegAiFixtures.Scan)]
    public void LoadModel_CorruptFile_KeepsPreviousModel()
    {
        string model = SegAiFixtures.ResolveAny(SegAiFixtures.Synthetic, SegAiFixtures.V2Both, SegAiFixtures.Scan)!;
        string folder = NewFolder("keep");
        string corrupt = Path.Combine(folder, "corrupt.segai");
        var bytes = new byte[512];
        new Random(11).NextBytes(bytes);
        File.WriteAllBytes(corrupt, bytes);

        using var session = SegAiSession.Create(folder);
        Assert.Null(session.LoadModel(model).Problem);
        Assert.NotNull(session.LoadModel(corrupt).Problem);
        Assert.Equal(model, session.LoadedModel!.Path);

        using Mat image = NoiseImage(320, 240, 5);
        Assert.Null(session.Predict(image).Error);
    }
}
