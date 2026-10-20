using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using EasyLabel.Core.Io;
using EasyLabel.SegAI.Convert;
using EasyLabel.SegAI.Data;
using EasyLabel.SegAI.Engine;
using EasyLabel.SegAI.Project;
using OpenCvSharp;

namespace EasyLabel.OnlineBench;

internal sealed record AccuracySample(string Config, string Gpu, string Roi, string Device, string Model, string ModelSha256,
    string Image, string LabelSource, string RoiRect, long LabelledPixels, long CoveredPixels, long PredictedPixels,
    long OutsidePixels, double InferMs);

internal static class AccuracyProxy
{
    private sealed record LabelledImage(string Image, string Source, int Width, int Height, byte[] Labels, Rect Bounds);

    public static void Run(string root, Options options, WorkerResult result)
    {
        result.Notes.Add("Accuracy is a TRAINING-SET recall proxy, not held-out accuracy; the v512 evaluation uses the same images as training. Only NG pixels count as defects; OK labels and filename NG suffixes are never reinterpreted as defect labels.");
        LabelledImage[] images;
        try { images = ReadImages(options.Dataset, result.Notes).ToArray(); }
        catch (Exception error)
        {
            result.Notes.Add("Accuracy unavailable: label scan could not establish a reliable NG denominator: " + error.GetBaseException().Message);
            return;
        }
        if (images.Length == 0)
        {
            result.Notes.Add("Accuracy unavailable: no reliably parsed image with nonzero NG defect pixels. Need matching LabelMe NG shapes or a same-size .easyai/cache/<stem>.labels.png with NG=1 (legacy 200..255). OK=2 is not a defect. No recall denominator or defect-centred ROI exists; no accuracy values fabricated.");
            return;
        }
        using FileStream model = File.OpenRead(options.Model!);
        string hash = Convert.ToHexString(SHA256.HashData(model));
        foreach (bool gpu in options.GpuModes)
            foreach (string mode in RoiSelection.Modes)
            {
                string config = $"accuracy-segai-mask-gpu{(gpu ? "on" : "off")}-roi{mode}-threads{options.Worker}-EMULATED";
                var rows = new List<AccuracySample>();
                var splitRows = new List<Sample>();
                var splitTimings = new List<SplitTiming>();
                try
                {
                    using var pipeline = new SegPipeline(root, options.Model!, options.Worker!, gpu, mode, true);
                    for (int i = 0; i < images.Length; i++)
                    {
                        LabelledImage image = images[i];
                        using Mat input = Cv2.ImRead(image.Image, ImreadModes.Color);
                        if (input.Empty() || input.Width != image.Width || input.Height != image.Height)
                            throw new InvalidDataException("Image changed or cannot be decoded: " + image.Image);
                        object prepared = pipeline.Prepare(input);
                        try
                        {
                            Rect roi = RoiSelection.Select(mode, input.Width, input.Height, image.Bounds);
                            if (i == 0)
                                for (int warmup = 0; warmup < options.Warmup; warmup++) pipeline.InferAt(prepared, roi);
                            long start = Stopwatch.GetTimestamp();
                            var prediction = (SegAiPredictResult)pipeline.InferAt(prepared, roi);
                            double ms = (Stopwatch.GetTimestamp() - start) * 1000d / Stopwatch.Frequency;
                            SplitTiming? split = pipeline.CaptureSplitTimings();
                            // AnalyzePrediction applies the app's threshold/minimum-area gates to this mask in place.
                            pipeline.Postprocess(prediction);
                            if (prediction.Width != image.Width || prediction.Height != image.Height || prediction.Mask.Length != image.Labels.Length)
                                throw new InvalidDataException("Expected production full-image mask coordinates; refusing to resize/rebase a mismatched mask.");
                            long labelled = 0, covered = 0, predicted = 0, outside = 0;
                            for (int p = 0; p < image.Labels.Length; p++)
                            {
                                bool ng = image.Labels[p] == SegClassMap.Ng, hit = prediction.Mask[p] != 0;
                                if (ng) labelled++;
                                if (hit) { predicted++; if (ng) covered++; else outside++; }
                            }
                            if (!double.IsFinite(ms) || ms < 0) throw new InvalidDataException("Invalid accuracy timing sample.");
                            rows.Add(new(config, pipeline.Gpu, mode, pipeline.Device, pipeline.Model, hash, image.Image, image.Source,
                                RoiSelection.Describe(roi), labelled, covered, predicted, outside, ms));
                            if (split != null)
                            {
                                splitTimings.Add(split);
                                if (split.Available)
                                {
                                    splitRows.Add(new Sample(config, i, "backbone_ms", split.BackboneMs, 0, "native split diagnostic - overlaps infer, do not add"));
                                    splitRows.Add(new Sample(config, i, "head_ms", split.HeadMs, 0, "native split diagnostic - overlaps infer, do not add"));
                                }
                            }
                        }
                        finally { (prepared as IDisposable)?.Dispose(); }
                    }
                    // All-or-nothing per mode/device: failures cannot produce partial proxy summaries.
                    result.Accuracy.AddRange(rows);
                    result.Samples.AddRange(splitRows);
                    SplitTiming? unavailable = splitTimings.FirstOrDefault(value => !value.Available);
                    if (unavailable != null)
                        result.Notes.Add($"Split timings unavailable for {config}: API v{unavailable.ApiVersion}; {unavailable.Reason}.");
                }
                catch (Exception error) { result.Notes.Add($"Accuracy unavailable for {config}: requested device/ROI unavailable or scoring failed: {error.GetBaseException().Message}"); }
            }
    }

    internal static string FirstNgImage(string dataset)
    {
        var notes = new List<string>();
        List<LabelledImage> images = ReadImages(dataset, notes, 1);
        if (images.Count != 0) return images[0].Image;
        throw new InvalidDataException("No reliably parsed NG-labelled PNG found beneath " + dataset + " (read-only). " + string.Join(" ", notes));
    }

    private static List<LabelledImage> ReadImages(string dataset, List<string> notes, int limit = 40)
    {
        var store = new SegProjectStore(dataset); // Constructor and Read* methods only; never open a writable dataset session.
        SegClassMap map = store.LoadClassMap();
        if (map.HasNamedTable || map.LabelOf("NG") != 1 || map.LabelOf("OK") != 2)
            throw new NotSupportedException("Accuracy currently requires the inspected two-class NG=1/OK=2 map.");
        int okOnly = 0, missing = 0, invalid = 0, jsonSources = 0, cacheOnlySources = 0;
        long ngPixels = 0, okPixels = 0;
        var images = new List<LabelledImage>();
        foreach (string file in Paths.Files(dataset, "*.png").Where(p => !Path.GetRelativePath(dataset, p).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(s => s.StartsWith('.'))))
        {
            LabelledImage? item = null;
            try
            {
                string rel = Path.GetRelativePath(dataset, file), json = Path.ChangeExtension(file, ".json");
                bool hasJson = File.Exists(json), hasCache = File.Exists(store.LabelsPath(rel));
                if (!hasJson && !hasCache) { missing++; continue; }
                using Mat input = Cv2.ImRead(file, ImreadModes.Color);
                if (input.Empty()) throw new InvalidDataException("Cannot decode input.");
                using Mat labels = ReadLabels(store, map, rel, json, input.Width, input.Height, out string source);
                if (hasJson) jsonSources++; else cacheOnlySources++;
                byte[] bytes = new byte[checked(input.Width * input.Height)];
                Marshal.Copy(labels.Data, bytes, 0, bytes.Length);
                int left = input.Width, top = input.Height, right = -1, bottom = -1;
                for (int y = 0, p = 0; y < input.Height; y++)
                    for (int x = 0; x < input.Width; x++, p++)
                        if (bytes[p] == SegClassMap.Ng)
                        {
                            ngPixels++;
                            left = Math.Min(left, x); top = Math.Min(top, y); right = Math.Max(right, x); bottom = Math.Max(bottom, y);
                        }
                        else if (bytes[p] == SegClassMap.Ok) okPixels++;
                if (right < 0) { okOnly++; continue; }
                item = new(file, source, input.Width, input.Height, bytes, new Rect(left, top, right - left + 1, bottom - top + 1));
            }
            catch (Exception error) { invalid++; notes.Add($"Accuracy skipped {file}: {error.GetBaseException().Message}"); }
            if (item != null) images.Add(item);
            if (images.Count == limit) break;
        }
        notes.Add($"Accuracy label scan: {okOnly} images without NG pixels; {missing} without matching JSON/cache; {invalid} invalid. Parsed {jsonSources} matching LabelMe JSON files beside PNGs and {cacheOnlySources} cache-only masks under .easyai/cache; production rasters contained NG=1 pixels={ngPixels}, OK=2 pixels={okPixels}. Filename suffixes are not labels. Eligible NG images capped at {limit} in ordinal path order.");
        return images;
    }

    private static Mat ReadLabels(SegProjectStore store, SegClassMap map, string rel, string json, int width, int height, out string source)
    {
        if (File.Exists(json))
        {
            var document = new LabelMeIO().Read(json);
            if (document.ImageWidth != width || document.ImageHeight != height)
                throw new InvalidDataException("LabelMe/image dimensions differ.");
            if (document.Shapes.Any(s => s.Points.Any(p => p.Length < 2 || !double.IsFinite(p[0]) || !double.IsFinite(p[1]))))
                throw new InvalidDataException("Nonfinite or malformed LabelMe geometry.");
            List<SegAnnotation> regions = SegLabelMeConverter.FromLabelMe(document.Shapes, store.ReadSidecar(rel), map, out bool stale, out int skipped);
            if (skipped != 0) throw new InvalidDataException($"Production converter skipped {skipped} shapes; labels are not reliable.");
            SegLabelRaster.ValidateAnnotations(regions, width, height, map.Count, allowOutside: true);
            source = json + (stale ? " (production JSON/embedded holes)" : " (matching production sidecar/holes)");
            return SegLabelRaster.Rasterize(regions, width, height);
        }
        using Mat? raw = store.ReadLabels(rel);
        if (raw == null || raw.Width != width || raw.Height != height || raw.Type() != MatType.CV_8UC1)
            throw new InvalidDataException("Missing/unreadable or differently sized label cache.");
        source = store.LabelsPath(rel) + " (cache only; no matching JSON)";
        return SegLabelRaster.Normalize(raw);
    }
}
