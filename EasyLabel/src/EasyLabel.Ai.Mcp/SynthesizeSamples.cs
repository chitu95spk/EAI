using System.Globalization;
using EasyLabel.LabelHarness;
using OpenCvSharp;

namespace EasyLabel.Ai.Mcp;

/// <summary>
/// synthesize_samples (OPT-IN ONLY, not the default: swapped backgrounds generalise poorly; real frames from capture_frames are the default): makes extra training images from the confirmed shapes of an attached reference image and writes each as
/// image + labelme .json into a NEW folder beside the reference (never into an existing one, so nothing is overwritten).
/// </summary>
internal static class SynthesizeSamples
{
    public const int DefaultCount = 20;
    public const int MaxCount = 200;

    /// <summary>Returns the new folder and the number of image+json pairs written.</summary>
    public static (string Folder, int Written) Run(Mat reference, string referencePath, IReadOnlyList<SeedObject> seeds, int countPerSeed, int rngSeed)
    {
        if (countPerSeed < 1 || countPerSeed > MaxCount) throw new ArgumentException("count must be 1.." + MaxCount.ToString(CultureInfo.InvariantCulture) + ".");
        using Mat bgr = reference.Channels() == 3 && reference.Depth() == MatType.CV_8U ? reference.Clone() : ToBgr(reference);

        string folder = Path.Combine(Path.GetDirectoryName(referencePath)!, "synthetic-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
        if (Directory.Exists(folder)) throw new ArgumentException("The output folder already exists, retry in a second: " + folder);
        Directory.CreateDirectory(folder);

        var synthesizer = new Synthesizer();
        int written = 0;
        for (int s = 0; s < seeds.Count; s++)
        {
            foreach (SyntheticSample sample in synthesizer.Generate(bgr, seeds[s], countPerSeed, rngSeed + s))
            {
                using (sample)
                {
                    string name = "syn_" + s.ToString(CultureInfo.InvariantCulture) + "_" + written.ToString("D4", CultureInfo.InvariantCulture) + ".png";
                    Cv2.ImWrite(Path.Combine(folder, name), sample.Image);
                    LabelmeJson.Write(Path.Combine(folder, name), sample.Image.Width, sample.Image.Height, sample.Objects.Select(o => (o.Label, (IReadOnlyList<double[]>)o.Polygon)));
                    written++;
                }
            }
        }

        return (folder, written);
    }

    private static Mat ToBgr(Mat src)
    {
        using Mat eight = new Mat();
        if (src.Depth() == MatType.CV_8U) src.CopyTo(eight); else src.ConvertTo(eight, MatType.CV_8U);
        var bgr = new Mat();
        if (eight.Channels() == 1) Cv2.CvtColor(eight, bgr, ColorConversionCodes.GRAY2BGR);
        else if (eight.Channels() == 4) Cv2.CvtColor(eight, bgr, ColorConversionCodes.BGRA2BGR);
        else throw new ArgumentException("Unsupported image format for synthesize_samples.");
        return bgr;
    }
}
