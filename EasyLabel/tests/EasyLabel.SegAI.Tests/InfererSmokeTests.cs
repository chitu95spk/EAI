using System.Runtime.InteropServices;
using EasyLabel.SegAI.Engine;
using OpenCvSharp;

namespace EasyLabel.SegAI.Tests;

public sealed class InfererSmokeTests
{
    [SegAiFixtureFact(true, SegAiFixtures.Synthetic, SegAiFixtures.V2Both, SegAiFixtures.Scan)]
    public void Predict_FixtureModel_ReturnsMaskOfReportedSize()
    {
        string model = SegAiFixtures.ResolveAny(SegAiFixtures.Synthetic, SegAiFixtures.V2Both, SegAiFixtures.Scan)!;
        var inferer = new SegAiInferer();
        try
        {
            Assert.Equal(0, inferer.SetGpu(false));
            Assert.Equal(0, inferer.Load(model));

            using Mat image = CreateNoiseImage(320, 240, 1234);
            SegAiPredictResult result = inferer.Predict(image, new OpenCvSharp.Rect(0, 0, 320, 240), 0.5f);

            Assert.True(result.Width > 0 && result.Height > 0);
            Assert.Equal(result.Width * result.Height, result.Mask.Length);
            if (result.Confidence is not null)
            {
                Assert.Equal(result.Mask.Length, result.Confidence.Length);
            }
        }
        finally
        {
            inferer.Dispose();
            inferer.Dispose();
        }

        Assert.False(inferer.IsOpened);
        Assert.Throws<ObjectDisposedException>(() => inferer.SetGpu(false));
    }

    [NativeSegAiFact]
    public void CreateAndDispose_Twice_IsIdempotent()
    {
        var inferer = new SegAiInferer();
        Assert.True(inferer.IsOpened);
        inferer.Dispose();
        inferer.Dispose();
        Assert.False(inferer.IsOpened);
    }

    [Fact]
    public void CancelFlag_RoundTrip()
    {
        var flag = new SegAiCancelFlag();
        try
        {
            Assert.NotEqual(IntPtr.Zero, flag.Pointer);
            Assert.Equal(0, Marshal.ReadInt32(flag.Pointer));
            flag.Cancel();
            Assert.Equal(1, Marshal.ReadInt32(flag.Pointer));
            Assert.True(flag.IsCancelled);
            flag.Reset();
            Assert.Equal(0, Marshal.ReadInt32(flag.Pointer));
            Assert.False(flag.IsCancelled);
        }
        finally
        {
            flag.Dispose();
            flag.Dispose();
        }

        Assert.Throws<ObjectDisposedException>(() => flag.Cancel());
    }

    private static Mat CreateNoiseImage(int width, int height, int seed)
    {
        var bytes = new byte[width * height * 3];
        new Random(seed).NextBytes(bytes);
        var mat = new Mat(height, width, MatType.CV_8UC3);
        Marshal.Copy(bytes, 0, mat.Data, bytes.Length);
        return mat;
    }
}
