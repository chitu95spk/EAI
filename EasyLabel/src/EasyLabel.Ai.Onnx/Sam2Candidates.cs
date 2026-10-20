using System.Drawing;
using OpenCvSharp;
using Point = OpenCvSharp.Point;
using Size = OpenCvSharp.Size;

namespace EasyLabel.Ai.Onnx;

/// <summary>One object outline SAM2 found on its own (no prompt from a model).</summary>
public sealed record Sam2Candidate(int Id, IReadOnlyList<PointF> Outer, RectangleF Box, float Score)
{
    public PointF Centre => new PointF(Box.X + Box.Width / 2f, Box.Y + Box.Height / 2f);
}

/// <summary>
/// Automatic outlines: SAM2 is asked at a grid of points over the foreground, the outlines are filtered by size and score and the duplicates
/// removed. The agent then only has to say which numbered outline it wants, instead of locating every object itself.
/// </summary>
public static class Sam2Candidates
{
    public const double MinAreaShare = 0.0004;
    public const double MaxAreaShare = 0.04;
    public const float MinScore = 0.7f;
    public const double DuplicateIou = 0.6;

    public const int Chunk = 12;
    public const int MaxRounds = 6;
    private const double WorkSide = 640.0;

    public static IReadOnlyList<Sam2Candidate> Generate(Sam2Backend backend, AiImage image, CancellationToken ct)
    {
        double scale = Math.Min(1.0, WorkSide / Math.Max(image.Width, image.Height));
        using var bgr = new Mat(image.Height, image.Width, MatType.CV_8UC3);
        System.Runtime.InteropServices.Marshal.Copy(image.Pixels, 0, bgr.Data, image.Pixels.Length);
        using var small = new Mat();
        Cv2.Resize(bgr, small, new Size(Math.Max(1, (int)(image.Width * scale)), Math.Max(1, (int)(image.Height * scale))), 0, 0, InterpolationFlags.Area);
        using var gray = new Mat();
        Cv2.CvtColor(small, gray, ColorConversionCodes.BGR2GRAY);
        using Mat fg = ImageMarkdown.Foreground(gray);
        using var dist = new Mat();
        Cv2.DistanceTransform(fg, dist, DistanceTypes.L2, DistanceTransformMasks.Mask3);

        double imageArea = (double)image.Width * image.Height;
        var accepted = new List<(AiProposal P, Mat M, int Count)>();
        using var covered = new Mat(small.Size(), MatType.CV_8UC1, Scalar.All(0));
        try
        {
            for (int round = 0; round < MaxRounds; round++)
            {
                ct.ThrowIfCancellationRequested();
                List<AiPoint> batch = NextSeeds(dist, covered, scale);
                if (batch.Count == 0) break;
                var found = new AiProposal?[batch.Count];
                void RunOne(int i)
                {
                    try
                    {
                        IReadOnlyList<AiProposal> r = backend.RunAsync(image, new AiPrompt(new[] { batch[i] }), ct).GetAwaiter().GetResult();
                        if (r.Count > 0) found[i] = r[0];
                    }
                    catch (AiModelException)
                    {
                    }
                }

                // The first call of the image computes the embedding; the others of the round then run at the same time.
                if (accepted.Count == 0 && round == 0) RunOne(0);
                Parallel.For(accepted.Count == 0 && round == 0 ? 1 : 0, batch.Count, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount), CancellationToken = ct }, RunOne);

                for (int i = 0; i < batch.Count; i++)
                {
                    AiProposal? p = found[i];
                    // Whatever the result, the seed is marked so the next round looks elsewhere.
                    Cv2.Circle(covered, new Point((int)(batch[i].X * scale), (int)(batch[i].Y * scale)), 4, Scalar.All(255), -1);
                    if (p == null || p.Score < MinScore) continue;
                    Mat m = Raster(p, image.Width, image.Height, scale, small.Size());
                    int count = Cv2.CountNonZero(m);
                    double area = count / (scale * scale);
                    if (area < MinAreaShare * imageArea || area > MaxAreaShare * imageArea || IsDuplicate(m, count, accepted))
                    {
                        m.Dispose();
                        continue;
                    }

                    Cv2.BitwiseOr(covered, m, covered);
                    accepted.Add((p, m, count));
                }
            }

            return accepted.OrderBy(r => r.P.Box.Y + r.P.Box.Height / 2f).ThenBy(r => r.P.Box.X)
                .Select((r, i) => new Sam2Candidate(i + 1, r.P.Outer, r.P.Box, r.P.Score)).ToList();
        }
        finally
        {
            foreach ((AiProposal _, Mat m, int _) in accepted) m.Dispose();
        }
    }

    // The thickest not yet covered spots of the foreground (distance-transform peaks, spaced apart): each one lies on an object.
    private static List<AiPoint> NextSeeds(Mat dist, Mat covered, double scale)
    {
        using var open = new Mat();
        Cv2.Threshold(covered, open, 0, 255, ThresholdTypes.BinaryInv);
        using var masked = new Mat();
        dist.CopyTo(masked, open);
        var seeds = new List<AiPoint>();
        for (int k = 0; k < Chunk; k++)
        {
            Cv2.MinMaxLoc(masked, out _, out double max, out _, out Point at);
            if (max < MinThickness) break;
            seeds.Add(new AiPoint((float)(at.X / scale), (float)(at.Y / scale), true));
            Cv2.Circle(masked, at, Math.Max(6, (int)(max * 2)), Scalar.All(0), -1);
        }

        return seeds;
    }

    public const double MinThickness = 2.5;

    private static bool IsDuplicate(Mat m, int count, List<(AiProposal P, Mat M, int Count)> accepted)
    {
        foreach ((AiProposal _, Mat other, int otherCount) in accepted)
        {
            using var inter = new Mat();
            Cv2.BitwiseAnd(m, other, inter);
            int i = Cv2.CountNonZero(inter);
            if (i == 0) continue;
            int u = count + otherCount - i;
            if ((double)i / u > DuplicateIou || (double)i / Math.Min(count, otherCount) > 0.85) return true;
        }

        return false;
    }

    private static Mat Raster(AiProposal p, int width, int height, double scale, Size size)
    {
        var m = new Mat(size, MatType.CV_8UC1, Scalar.All(0));
        var pts = p.Outer.Select(q => new Point((int)(q.X * scale), (int)(q.Y * scale))).ToArray();
        Cv2.FillPoly(m, new[] { pts }, Scalar.All(255));
        return m;
    }
}
