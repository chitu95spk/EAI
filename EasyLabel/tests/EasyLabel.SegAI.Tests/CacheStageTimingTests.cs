using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using EasyLabel.SegAI.Project;
using EasyLabel.SegAI.Training;
using Newtonsoft.Json;
using OpenCvSharp;

namespace EasyLabel.SegAI.Tests;

/// <summary>
/// EAI-CACHE C3a: opt-in measurement of the per-frame work SegAiTrainingPipeline.PrepareTrainingFrames performs, to decide whether a
/// persisted per-frame hash cache (C3b) is worth building. Skips unless EASYAI_RUN_TIMING=1. Production code is untouched.
/// Input folder: EASYAI_TIMING_IMAGES (png/bmp/jpg) else 60 synthetic 2448x2048 frames; a 640x480 x 200 variant always runs as well.
/// Evidence: artifacts\EAI-CACHE\c3a\timing.csv and timing.md.
///
/// Called directly (internal, via InternalsVisibleTo): SegAiTrainingPipeline.ReadMat, ClassBitsOf, RawMatHash, ImageMetaCache.
/// Replicated with the identical OpenCvSharp/BCL calls (private in production): EnsureBgr, EncodePng, PngHash, LoadOrBuildMask file
/// branch (Cv2.ImDecode Grayscale of the mask bytes, WITHOUT SegLabelRaster.Normalize), snapshot File.WriteAllBytes.
/// </summary>
public sealed class CacheStageTimingTests
{
    private sealed class TimingFactAttribute : FactAttribute
    {
        public TimingFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("EASYAI_RUN_TIMING") != "1")
                Skip = "Cache stage timing is opt-in: set EASYAI_RUN_TIMING=1.";
        }
    }

    private static readonly string[] StageNames =
    {
        "00_decode_ReadMat",
        "01_EnsureBgr_clone",
        "02_mask_decode_png",
        "03_ClassBitsOf",
        "04_png_encode_input",
        "05_sha256_of_png",
        "06_write_input_snapshot",
        "07_mask_png_encode_write",
        "08_PngHash_nosnapshot",
        "09_RawMatHash_input",
        "10_RawMatHash_label",
    };

    private const int StageCount = 11;
    private const int Passes = 3;

    // Frame totals: the snapshot path (writeSnapshots=true) and the no-snapshot path (PngHash) of PrepareTrainingFrames.
    private static readonly int[] SnapshotPath = { 0, 1, 2, 3, 4, 5, 6, 7, 9, 10 };
    private static readonly int[] NoSnapshotPath = { 0, 1, 2, 3, 8, 9, 10 };

    // Conservative skip set: hash-only work. In the snapshot path the PNG encode stays because its bytes are written to disk.
    private static readonly int[] SkipSnapshotConservative = { 5, 9, 10 };
    private static readonly int[] SkipNoSnapshotConservative = { 8, 9, 10 };

    // Optimistic skip set: snapshot files would also be cached (encode + mask encode/write skipped as well).
    private static readonly int[] SkipSnapshotOptimistic = { 4, 5, 6, 7, 9, 10 };

    private sealed class DatasetResult
    {
        public string Name = string.Empty;
        public int Count;
        public string Resolution = string.Empty;
        public double[][][] Times = Array.Empty<double[][]>(); // [pass][image][stage] ms
        public double[] PassMetaCacheMs = Array.Empty<double>(); // total ms per pass
        public double DecodeSizeMsPerFrame; // median stage 0
        public readonly List<(int Entries, long Bytes, double MedianMs, double MinMs)> HashCache = new List<(int, long, double, double)>();
    }

    [TimingFact]
    public void MeasureCacheStages()
    {
        string root = ResolveRepoRoot();
        string outDir = Path.Combine(root, "artifacts", "EAI-CACHE", "c3a");
        Directory.CreateDirectory(outDir);
        string work = Path.Combine(AppContext.BaseDirectory, "cache-timing-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var results = new List<DatasetResult>();
            string? env = Environment.GetEnvironmentVariable("EASYAI_TIMING_IMAGES");
            string primaryDir;
            string primaryName;
            if (!string.IsNullOrWhiteSpace(env) && Directory.Exists(env))
            {
                primaryDir = env;
                primaryName = "real:" + env;
            }
            else
            {
                primaryDir = Path.Combine(work, "synth-2448x2048");
                GenerateSynthetic(primaryDir, 60, 2448, 2048, 20260101);
                primaryName = "synthetic-2448x2048x60";
            }

            results.Add(Measure(primaryName, primaryDir, Path.Combine(work, "primary")));
            string smallDir = Path.Combine(work, "synth-640x480");
            GenerateSynthetic(smallDir, 200, 640, 480, 20260202);
            results.Add(Measure("synthetic-640x480x200", smallDir, Path.Combine(work, "small")));

            WriteCsv(Path.Combine(outDir, "timing.csv"), results);
            WriteMarkdown(Path.Combine(outDir, "timing.md"), results);
        }
        finally
        {
            try { Directory.Delete(work, true); } catch (Exception) { }
        }
    }

    // ------------------------------------------------------------------ measurement

    private static DatasetResult Measure(string name, string imageDir, string scratch)
    {
        Directory.CreateDirectory(scratch);
        List<string> paths = Directory.EnumerateFiles(imageDir)
            .Where(p => IsImage(p)).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
        Assert.NotEmpty(paths);

        // One synthetic label raster (classes 1 and 2) per image, pre-encoded like a stored mask file; not part of the timed work.
        var maskBytes = new List<byte[]>();
        var sizes = new List<(int W, int H)>();
        foreach (string p in paths)
        {
            using Mat probe = EasyLabel.SegAI.Training.SegAiTrainingPipeline.ReadMat(p, ImreadModes.Color);
            sizes.Add((probe.Width, probe.Height));
            using Mat mask = new Mat(probe.Size(), MatType.CV_8UC1, Scalar.All(0));
            Cv2.Circle(mask, new OpenCvSharp.Point(probe.Width / 3, probe.Height / 2), Math.Min(probe.Width, probe.Height) / 5, Scalar.All(1), -1);
            Cv2.Rectangle(mask, new OpenCvSharp.Rect(probe.Width / 2, probe.Height / 4, probe.Width / 5, probe.Height / 4), Scalar.All(2), -1);
            Cv2.ImEncode(".png", mask, out byte[] mb);
            maskBytes.Add(mb);
        }

        string snapDir = Path.Combine(scratch, "snap");
        Directory.CreateDirectory(snapDir);

        RunPass(paths, maskBytes, snapDir); // warm-up, discarded
        var result = new DatasetResult { Name = name, Count = paths.Count, Resolution = sizes[0].W + "x" + sizes[0].H };
        result.Times = new double[Passes][][];
        for (int pass = 0; pass < Passes; pass++) result.Times[pass] = RunPass(paths, maskBytes, snapDir);

        // (8) ImageMetaCache: populate once, then each pass = new instance (JSON load) + TryGet for every image.
        string cacheDir = Path.Combine(scratch, "meta");
        Directory.CreateDirectory(cacheDir);
        var stamps = new List<(string Rel, long Len, long Ticks)>();
        for (int i = 0; i < paths.Count; i++)
        {
            var fi = new FileInfo(paths[i]);
            stamps.Add((Path.GetFileName(paths[i]).ToLowerInvariant(), fi.Length, fi.LastWriteTimeUtc.Ticks));
        }

        using (var seed = new ImageMetaCache(cacheDir, 0))
        {
            for (int i = 0; i < paths.Count; i++) seed.Put(stamps[i].Rel, stamps[i].Len, stamps[i].Ticks, sizes[i].W, sizes[i].H);
            seed.Flush();
        }

        result.PassMetaCacheMs = new double[Passes + 1];
        for (int pass = 0; pass < Passes + 1; pass++)
        {
            long t0 = Stopwatch.GetTimestamp();
            using (var cache = new ImageMetaCache(cacheDir, 0))
            {
                foreach ((string rel, long len, long ticks) in stamps)
                    if (!cache.TryGet(rel, len, ticks, out _, out _)) throw new InvalidOperationException("meta cache miss: " + rel);
            }

            result.PassMetaCacheMs[pass] = Ms(t0);
        }

        result.PassMetaCacheMs = result.PassMetaCacheMs.Skip(1).ToArray(); // first iteration is the warm-up
        result.DecodeSizeMsPerFrame = Median(Enumerable.Range(0, Passes).Select(p => result.Times[p].Average(r => r[0])).ToList());

        // (9) hypothetical per-frame hash cache: dictionary of N entries -> json -> read + parse.
        foreach (int n in new[] { paths.Count, 1000, 10000 }.Distinct())
        {
            var dict = new Dictionary<string, HashEntry>(StringComparer.OrdinalIgnoreCase);
            var rnd = new Random(77);
            for (int i = 0; i < n; i++)
            {
                dict["images/frame-" + i.ToString("D6", CultureInfo.InvariantCulture) + ".png"] = new HashEntry
                {
                    Length = 3000000 + i,
                    WriteTicks = 638000000000000000L + i,
                    PngHash = RandomBase64(rnd),
                    InputHash = RandomBase64(rnd),
                    LabelHash = RandomBase64(rnd),
                    ClassBits = 3,
                };
            }

            string file = Path.Combine(scratch, "hash-cache-" + n + ".json");
            File.WriteAllText(file, JsonConvert.SerializeObject(new HashFile { Version = 1, Entries = dict }));
            long bytes = new FileInfo(file).Length;
            var runs = new List<double>();
            for (int pass = 0; pass < Passes + 1; pass++)
            {
                long t0 = Stopwatch.GetTimestamp();
                HashFile? loaded = JsonConvert.DeserializeObject<HashFile>(File.ReadAllText(file));
                double ms = Ms(t0);
                Assert.Equal(n, loaded!.Entries!.Count);
                if (pass > 0) runs.Add(ms);
            }

            result.HashCache.Add((n, bytes, Median(runs), runs.Min()));
        }

        return result;
    }

    private sealed class HashEntry
    {
        public long Length { get; set; }
        public long WriteTicks { get; set; }
        public string PngHash { get; set; } = string.Empty;
        public string InputHash { get; set; } = string.Empty;
        public string LabelHash { get; set; } = string.Empty;
        public uint ClassBits { get; set; }
    }

    private sealed class HashFile
    {
        public int Version { get; set; }
        public Dictionary<string, HashEntry>? Entries { get; set; }
    }

    private static string RandomBase64(Random rnd)
    {
        byte[] b = new byte[32];
        rnd.NextBytes(b);
        return System.Convert.ToBase64String(b);
    }

    /// <summary>One pass over all images; returns [image][stage] milliseconds for the stages PrepareTrainingFrames runs per frame.</summary>
    private static double[][] RunPass(List<string> paths, List<byte[]> maskBytes, string snapDir)
    {
        var rows = new double[paths.Count][];
        for (int i = 0; i < paths.Count; i++)
        {
            var t = new double[StageCount];
            long s = Stopwatch.GetTimestamp();

            // 0: LoadSampleImage -> SegAiTrainingPipeline.ReadMat (File.ReadAllBytes + Cv2.ImDecode Color)
            using Mat source = SegAiTrainingPipeline.ReadMat(paths[i], ImreadModes.Color);
            t[0] = Ms(s);

            // 1: EnsureBgr (replicated)
            s = Stopwatch.GetTimestamp();
            using Mat input = EnsureBgr(source);
            t[1] = Ms(s);

            // 2: LoadOrBuildMask file branch (replicated, no Normalize)
            s = Stopwatch.GetTimestamp();
            using Mat aligned = Cv2.ImDecode(maskBytes[i], ImreadModes.Grayscale);
            t[2] = Ms(s);

            // 3: ClassBitsOf (direct)
            s = Stopwatch.GetTimestamp();
            uint bits = SegAiTrainingPipeline.ClassBitsOf(aligned, 2);
            t[3] = Ms(s);
            if (bits == 0) throw new InvalidOperationException("synthetic mask has no class");

            // 4 + 5 + 6: snapshot path: EncodePng(input), SHA256 of the png (base64), File.WriteAllBytes
            s = Stopwatch.GetTimestamp();
            byte[] imagePng = EncodePng(input);
            t[4] = Ms(s);
            s = Stopwatch.GetTimestamp();
            string hash = System.Convert.ToBase64String(SHA256.HashData(imagePng));
            t[5] = Ms(s);
            s = Stopwatch.GetTimestamp();
            File.WriteAllBytes(Path.Combine(snapDir, i + ".png"), imagePng);
            t[6] = Ms(s);

            // 7: mask EncodePng + write
            s = Stopwatch.GetTimestamp();
            File.WriteAllBytes(Path.Combine(snapDir, i + "-labels.png"), EncodePng(aligned));
            t[7] = Ms(s);

            // 8: no-snapshot path: PngHash(input) = EncodePng + SHA256 base64 (replicated)
            s = Stopwatch.GetTimestamp();
            string pngHash = System.Convert.ToBase64String(SHA256.HashData(EncodePng(input)));
            t[8] = Ms(s);
            if (pngHash != hash) throw new InvalidOperationException("PngHash differs from snapshot hash");

            // 9, 10: RawMatHash (direct)
            s = Stopwatch.GetTimestamp();
            string inputHash = SegAiTrainingPipeline.RawMatHash(input);
            t[9] = Ms(s);
            s = Stopwatch.GetTimestamp();
            string labelHash = SegAiTrainingPipeline.RawMatHash(aligned);
            t[10] = Ms(s);
            if (inputHash.Length == 0 || labelHash.Length == 0) throw new InvalidOperationException("empty hash");
            rows[i] = t;
        }

        return rows;
    }

    // Identical to the private SegAiTrainingPipeline.EnsureBgr.
    private static Mat EnsureBgr(Mat input)
    {
        if (input.Type() == MatType.CV_8UC3) return input.Clone();
        var bgr = new Mat();
        if (input.Channels() == 1) Cv2.CvtColor(input, bgr, ColorConversionCodes.GRAY2BGR);
        else if (input.Channels() == 4) Cv2.CvtColor(input, bgr, ColorConversionCodes.BGRA2BGR);
        else input.ConvertTo(bgr, MatType.CV_8UC3);
        return bgr;
    }

    // Identical to the private SegAiTrainingPipeline.EncodePng.
    private static byte[] EncodePng(Mat image)
    {
        if (!Cv2.ImEncode(".png", image, out byte[] png) || png == null || png.Length == 0) throw new IOException("Cannot encode training image.");
        return png;
    }

    private static double Ms(long startTimestamp)
    {
        return (Stopwatch.GetTimestamp() - startTimestamp) * 1000.0 / Stopwatch.Frequency;
    }

    private static bool IsImage(string path)
    {
        string e = Path.GetExtension(path).ToLowerInvariant();
        return e == ".png" || e == ".bmp" || e == ".jpg" || e == ".jpeg";
    }

    // ------------------------------------------------------------------ synthetic data

    private static void GenerateSynthetic(string dir, int count, int width, int height, int seed)
    {
        Directory.CreateDirectory(dir);
        var rnd = new Random(seed);
        for (int i = 0; i < count; i++)
        {
            using var noise = new Mat(Math.Max(8, height / 16), Math.Max(8, width / 16), MatType.CV_8UC3);
            var bytes = new byte[noise.Rows * noise.Cols * 3];
            rnd.NextBytes(bytes);
            System.Runtime.InteropServices.Marshal.Copy(bytes, 0, noise.Data, bytes.Length);
            using var big = new Mat();
            Cv2.Resize(noise, big, new OpenCvSharp.Size(width, height), 0, 0, InterpolationFlags.Cubic);
            Cv2.GaussianBlur(big, big, new OpenCvSharp.Size(0, 0), 3.0);

            // gradient overlay
            using var grad = new Mat(height, width, MatType.CV_8UC3);
            using var ramp = new Mat(1, width, MatType.CV_8UC3);
            for (int x = 0; x < width; x++)
            {
                byte v = (byte)(x * 255L / Math.Max(1, width - 1));
                ramp.Set(0, x, new Vec3b(v, (byte)(255 - v), (byte)((v + i * 7) & 255)));
            }

            Cv2.Repeat(ramp, height, 1, grad);
            Cv2.AddWeighted(big, 0.6, grad, 0.4, 0, big);

            for (int k = 0; k < 6; k++)
            {
                var color = new Scalar(rnd.Next(256), rnd.Next(256), rnd.Next(256));
                if (k % 2 == 0)
                    Cv2.Circle(big, new OpenCvSharp.Point(rnd.Next(width), rnd.Next(height)), 20 + rnd.Next(Math.Max(21, height / 6)), color, -1);
                else
                    Cv2.Rectangle(big, new OpenCvSharp.Rect(rnd.Next(width / 2), rnd.Next(height / 2), 30 + rnd.Next(width / 4), 30 + rnd.Next(height / 4)), color, -1);
            }

            Cv2.GaussianBlur(big, big, new OpenCvSharp.Size(0, 0), 1.2);
            string file = Path.Combine(dir, "frame-" + i.ToString("D4", CultureInfo.InvariantCulture) + ".png");
            if (!Cv2.ImWrite(file, big)) throw new IOException("Cannot write synthetic frame " + file);
        }
    }

    // ------------------------------------------------------------------ statistics and output

    private static double Median(List<double> values)
    {
        var s = values.OrderBy(v => v).ToList();
        int n = s.Count;
        return n % 2 == 1 ? s[n / 2] : (s[n / 2 - 1] + s[n / 2]) / 2.0;
    }

    /// <summary>Per-pass mean ms per frame of a stage set -> (median over passes, min over passes).</summary>
    private static (double Median, double Min) PerFrame(DatasetResult r, IEnumerable<int> stages)
    {
        int[] st = stages.ToArray();
        var perPass = new List<double>();
        for (int p = 0; p < Passes; p++) perPass.Add(r.Times[p].Average(row => st.Sum(k => row[k])));
        return (Median(perPass), perPass.Min());
    }

    private static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    private static void WriteCsv(string path, List<DatasetResult> results)
    {
        var sb = new StringBuilder();
        sb.Append("dataset,resolution,image_index,stage,median_ms,min_ms\n");
        foreach (DatasetResult r in results)
        {
            // per image: median/min over the three passes
            for (int i = 0; i < r.Count; i++)
            {
                for (int k = 0; k < StageCount; k++)
                {
                    var v = Enumerable.Range(0, Passes).Select(p => r.Times[p][i][k]).ToList();
                    sb.Append(Csv(r.Name)).Append(',').Append(r.Resolution).Append(',').Append(i).Append(',').Append(StageNames[k]).Append(',')
                        .Append(F(Median(v))).Append(',').Append(F(v.Min())).Append('\n');
                }
            }

            // aggregated: ms/frame (mean over images, median/min over passes)
            for (int k = 0; k < StageCount; k++)
            {
                (double med, double min) = PerFrame(r, new[] { k });
                sb.Append(Csv(r.Name)).Append(',').Append(r.Resolution).Append(",ALL,").Append(StageNames[k]).Append(',')
                    .Append(F(med)).Append(',').Append(F(min)).Append('\n');
            }

            (double sm, double smin) = PerFrame(r, SnapshotPath);
            (double nm, double nmin) = PerFrame(r, NoSnapshotPath);
            sb.Append(Csv(r.Name)).Append(',').Append(r.Resolution).Append(",ALL,total_snapshot_path_per_frame,").Append(F(sm)).Append(',').Append(F(smin)).Append('\n');
            sb.Append(Csv(r.Name)).Append(',').Append(r.Resolution).Append(",ALL,total_nosnapshot_path_per_frame,").Append(F(nm)).Append(',').Append(F(nmin)).Append('\n');
            sb.Append(Csv(r.Name)).Append(',').Append(r.Resolution).Append(",ALL,total_snapshot_path_set_s,").Append(F(sm * r.Count / 1000.0)).Append(',').Append(F(smin * r.Count / 1000.0)).Append('\n');
            sb.Append(Csv(r.Name)).Append(',').Append(r.Resolution).Append(",ALL,total_nosnapshot_path_set_s,").Append(F(nm * r.Count / 1000.0)).Append(',').Append(F(nmin * r.Count / 1000.0)).Append('\n');
            sb.Append(Csv(r.Name)).Append(',').Append(r.Resolution).Append(",ALL,ImageMetaCache_load_plus_lookup_per_frame,")
                .Append(F(Median(r.PassMetaCacheMs.ToList()) / r.Count)).Append(',').Append(F(r.PassMetaCacheMs.Min() / r.Count)).Append('\n');
            foreach (var h in r.HashCache)
                sb.Append(Csv(r.Name)).Append(',').Append(r.Resolution).Append(",ALL,hash_cache_json_read_parse_").Append(h.Entries).Append("_entries_total_ms,")
                    .Append(F(h.MedianMs)).Append(',').Append(F(h.MinMs)).Append('\n');
        }

        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
    }

    private static string Csv(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";

    private static void WriteMarkdown(string path, List<DatasetResult> results)
    {
        var sb = new StringBuilder();
        sb.Append("# EAI-CACHE C3a: per-frame training preparation stage timing\n\n");
        sb.Append("Machine: ").Append(Environment.ProcessorCount).Append(" logical cores, CPU affinity untouched. Warm-up pass + ")
            .Append(Passes).Append(" measured passes; values are ms per frame (mean over images), median and min over passes.\n");
        sb.Append("Mask is a synthetic 2-class raster decoded from a PNG; SegLabelRaster.Normalize is not included (small). ")
            .Append("Snapshot path = PrepareTrainingFrames with writeSnapshots=true; no-snapshot path uses PngHash.\n\n");

        double primaryConsShare = 0, primaryConsSave1000 = 0;
        bool primaryDone = false;
        foreach (DatasetResult r in results)
        {
            sb.Append("## ").Append(r.Name).Append(" (").Append(r.Count).Append(" images, ").Append(r.Resolution).Append(")\n\n");
            sb.Append("| stage | median ms/frame | min ms/frame |\n|---|---|---|\n");
            for (int k = 0; k < StageCount; k++)
            {
                (double med, double min) = PerFrame(r, new[] { k });
                sb.Append("| ").Append(StageNames[k]).Append(" | ").Append(F(med)).Append(" | ").Append(F(min)).Append(" |\n");
            }

            (double sm, double smin) = PerFrame(r, SnapshotPath);
            (double nm, double nmin) = PerFrame(r, NoSnapshotPath);
            sb.Append("| total snapshot path | ").Append(F(sm)).Append(" | ").Append(F(smin)).Append(" |\n");
            sb.Append("| total no-snapshot path | ").Append(F(nm)).Append(" | ").Append(F(nmin)).Append(" |\n\n");
            sb.Append("Whole set (").Append(r.Count).Append(" images): snapshot path ").Append(F(sm * r.Count / 1000.0)).Append(" s (min ")
                .Append(F(smin * r.Count / 1000.0)).Append(" s), no-snapshot path ").Append(F(nm * r.Count / 1000.0)).Append(" s (min ")
                .Append(F(nmin * r.Count / 1000.0)).Append(" s).\n\n");

            double metaPerFrame = Median(r.PassMetaCacheMs.ToList()) / r.Count;
            sb.Append("(8) Size via ImageMetaCache (load json + TryGet): ").Append(F(metaPerFrame)).Append(" ms/frame versus ")
                .Append(F(r.DecodeSizeMsPerFrame)).Append(" ms/frame for a full decode.\n\n");
            sb.Append("(9) Hypothetical hash cache json (Newtonsoft, 6 fields per entry), read + parse:\n\n| entries | file bytes | median ms | min ms |\n|---|---|---|---|\n");
            foreach (var h in r.HashCache)
                sb.Append("| ").Append(h.Entries).Append(" | ").Append(h.Bytes).Append(" | ").Append(F(h.MedianMs)).Append(" | ").Append(F(h.MinMs)).Append(" |\n");
            sb.Append('\n');

            // Savings per frame; cache read cost per entry is taken from the 1000-entry measurement.
            double cachePerFrame = r.HashCache.First(h => h.Entries == 1000).MedianMs / 1000.0;
            double consSnap = PerFrame(r, SkipSnapshotConservative).Median;
            double consNoSnap = PerFrame(r, SkipNoSnapshotConservative).Median;
            double optSnap = PerFrame(r, SkipSnapshotOptimistic).Median;
            sb.Append("| scenario | skippable ms/frame | share of per-frame time | net saving per 1000 images (s) |\n|---|---|---|---|\n");
            AppendScenario(sb, "snapshot path, hashes only (stages 5,9,10)", consSnap, sm, cachePerFrame);
            AppendScenario(sb, "no-snapshot path, hashes only (stages 8,9,10)", consNoSnap, nm, cachePerFrame);
            AppendScenario(sb, "snapshot path, snapshot files also cached (stages 4-7,9,10)", optSnap, sm, cachePerFrame);
            sb.Append('\n');

            if (!primaryDone)
            {
                primaryDone = true;
                // Decision uses the conservative variants on the first (large / real) dataset, worst of the two paths.
                double shareSnap = consSnap / sm * 100.0;
                double shareNo = consNoSnap / nm * 100.0;
                primaryConsShare = Math.Min(shareSnap, shareNo);
                primaryConsSave1000 = Math.Min((consSnap - cachePerFrame) * 1000.0, (consNoSnap - cachePerFrame) * 1000.0) / 1000.0;
                _optimisticShare = optSnap / sm * 100.0;
                _optimisticSave1000 = (optSnap - cachePerFrame) * 1000.0 / 1000.0;
            }
        }

        bool worthwhile = primaryConsShare >= 25.0 && primaryConsSave1000 >= 5.0;
        sb.Append("Share of total per-frame time a persisted hash cache could skip (conservative, worst of both paths, hashes only; stages 5/8,9,10 vs decode+clone+mask+classbits+writes): ")
            .Append(F(primaryConsShare)).Append(" % (optimistic with snapshot files cached: ").Append(F(_optimisticShare)).Append(" %).\n");
        sb.Append("Estimated net saving for 1000 images (hash cache read cost subtracted): ").Append(F(primaryConsSave1000)).Append(" s (optimistic: ")
            .Append(F(_optimisticSave1000)).Append(" s).\n");
        sb.Append("C3b worthwhile if >= 25 % of per-frame pipeline time AND >= 5 s on 1000 images: measured ").Append(F(primaryConsShare)).Append(" % and ")
            .Append(F(primaryConsSave1000)).Append(" s -> ").Append(worthwhile ? "WORTHWHILE" : "NOT WORTHWHILE").Append(".\n");
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
    }

    private static double _optimisticShare;
    private static double _optimisticSave1000;

    private static void AppendScenario(StringBuilder sb, string label, double skippable, double total, double cachePerFrame)
    {
        sb.Append("| ").Append(label).Append(" | ").Append(F(skippable)).Append(" | ").Append(F(skippable / total * 100.0)).Append(" % | ")
            .Append(F((skippable - cachePerFrame) * 1000.0 / 1000.0)).Append(" |\n");
    }

    // ------------------------------------------------------------------ repo root

    private static string ResolveRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "artifacts")) && Directory.Exists(Path.Combine(dir.FullName, "EasyLabel"))) return dir.FullName;
            if (File.Exists(Path.Combine(dir.FullName, "EasyLabel.sln")))
            {
                DirectoryInfo? parent = dir.Parent;
                if (parent != null && Directory.Exists(Path.Combine(parent.FullName, "artifacts"))) return parent.FullName;
                return dir.FullName;
            }
        }

        throw new DirectoryNotFoundException("Repo root (EasyLabel.sln) not found above " + AppContext.BaseDirectory);
    }
}
