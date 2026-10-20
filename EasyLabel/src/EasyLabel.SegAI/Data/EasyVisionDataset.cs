// Cloned from BeeCore/Unit/SegmentAI.Dataset.cs@4e7a4bf10 (DatasetEntry/DatasetFile :187-202, ParseDataset :432-439,
// DatasetEntryPath :427-431, ExportDataset :441-469, ImportDataset :473-514). The JSON member names are identical.
using System.IO.Compression;
using System.Text;
using EasyLabel.SegAI.Model;
using Newtonsoft.Json;
using OpenCvSharp;

namespace EasyLabel.SegAI.Data;

/// <summary>
/// One image of an EasyVision dataset. Folder reads fill the paths (lazy), zip reads fill the byte arrays.
/// </summary>
/// <param name="Name">Original image name.</param>
/// <param name="ImagePath">PNG file of a folder dataset.</param>
/// <param name="MaskPath">Label mask file (<c>.labels.png</c>) of a folder dataset.</param>
/// <param name="ImageBytes">PNG bytes of a zip dataset or an entry to be written.</param>
/// <param name="MaskBytes">Mask PNG bytes of a zip dataset or an entry to be written.</param>
/// <param name="Regions">Annotations (never null).</param>
/// <param name="Mode">0 Auto, 1 Train, 2 Val.</param>
/// <param name="Key">EasyVision registered-image key from a folder dataset; not comparable with <see cref="ImageKey"/>.</param>
public sealed record EvDatasetEntry(
    string Name,
    string? ImagePath,
    string? MaskPath,
    byte[]? ImageBytes,
    byte[]? MaskBytes,
    IReadOnlyList<SegAnnotation> Regions,
    byte Mode,
    string? Key = null)
{
    /// <summary>Decodes the BGR image. The caller disposes it.</summary>
    public Mat LoadImage()
    {
        Mat image = ImageBytes != null ? Cv2.ImDecode(ImageBytes, ImreadModes.Color)
            : ImagePath != null ? Cv2.ImRead(ImagePath, ImreadModes.Color) : new Mat();
        if (image.Empty()) { image.Dispose(); throw new InvalidDataException("Dataset image could not be decoded: " + Name); }
        if ((long)image.Width * image.Height > EasyVisionDataset.MaxPixels) { image.Dispose(); throw new InvalidDataException("Dataset image is too large."); }
        return image;
    }

    /// <summary>Decodes the label mask as a single channel image, or null when the entry has none. The caller disposes it.</summary>
    public Mat? LoadMask()
    {
        Mat mask = MaskBytes != null ? Cv2.ImDecode(MaskBytes, ImreadModes.Grayscale)
            : MaskPath != null && File.Exists(MaskPath) ? Cv2.ImRead(MaskPath, ImreadModes.Grayscale) : new Mat();
        if (mask.Empty()) { mask.Dispose(); return null; }
        return mask;
    }

    /// <summary>PNG bytes of the image without decoding it.</summary>
    public byte[] ReadImageBytes()
    {
        if (ImageBytes != null) return ImageBytes;
        if (ImagePath != null) return File.ReadAllBytes(ImagePath);
        throw new InvalidDataException("Dataset entry has no image: " + Name);
    }

    /// <summary>PNG bytes of the mask, or null when the entry has none.</summary>
    public byte[]? ReadMaskBytes()
    {
        if (MaskBytes != null) return MaskBytes;
        if (MaskPath != null && File.Exists(MaskPath)) return File.ReadAllBytes(MaskPath);
        return null;
    }
}

/// <summary>Reads EasyVision's dataset folder (dataset.json version 1) and dataset zip export. Never writes into a source folder.</summary>
public static class EasyVisionDataset
{
    internal const long MaxPixels = 40000000;
    private const long MaxManifestBytes = 16L * 1024 * 1024;
    private const long MaxImageBytes = 128L * 1024 * 1024;

    private sealed class DatasetEntry
    {
        public string? Name, Image, Mask, Key;
        public List<SegAnnotation>? Regions;
        // V148: additive Train/Val pin (0 Auto, 1 Train, 2 Val); absent in older manifests => 0. DatasetFile.Version stays 1.
        public int Mode;
    }

    private sealed class DatasetClass
    {
        public string? Name, Kind;
        public bool Enabled = true;
    }

    private sealed class DatasetFile
    {
        public int Version = 1;
        // Additive named-class table (class id = position + 1); absent => the two-class NG/OK dataset. Version stays 1.
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public List<DatasetClass>? Classes;
        public bool ResetLegacy;
        public List<string> RemovedNames = new List<string>();
        public List<DatasetEntry> Images = new List<DatasetEntry>();
    }

    private static readonly JsonSerializerSettings DatasetJson = new JsonSerializerSettings { TypeNameHandling = TypeNameHandling.None, MaxDepth = 32 };

    /// <summary>
    /// Reads <c>dataset.json</c> of a dataset folder. Entries whose image or mask file is missing are skipped. Files are
    /// not decoded here; use <see cref="EvDatasetEntry.LoadImage"/> / <see cref="EvDatasetEntry.LoadMask"/>.
    /// </summary>
    public static IReadOnlyList<EvDatasetEntry> ReadFolder(string datasetDir) => ReadFolder(datasetDir, out _);

    /// <summary>Same as <see cref="ReadFolder(string)"/>; <paramref name="classes"/> is the manifest class table, or null for a two-class NG/OK dataset.</summary>
    public static IReadOnlyList<EvDatasetEntry> ReadFolder(string datasetDir, out SegAiClassTable? classes)
    {
        string manifest = Path.Combine(datasetDir, "dataset.json");
        if (!File.Exists(manifest)) throw new FileNotFoundException("dataset.json not found.", manifest);
        if (new FileInfo(manifest).Length > MaxManifestBytes) throw new InvalidDataException("Dataset manifest is too large.");
        var data = ParseDataset(File.ReadAllText(manifest));
        classes = ParseClasses(data);
        var result = new List<EvDatasetEntry>();
        foreach (var entry in data.Images)
        {
            string image = EntryPath(datasetDir, entry.Image), mask = EntryPath(datasetDir, entry.Mask);
            if (!File.Exists(image) || !File.Exists(mask)) continue;
            if (new FileInfo(image).Length > MaxImageBytes) throw new InvalidDataException("Missing/oversized dataset image.");
            result.Add(new EvDatasetEntry(entry.Name!, image, mask, null, null, entry.Regions ?? new List<SegAnnotation>(),
                SegSample.NormalizeMode(entry.Mode), entry.Key));
        }
        return result;
    }

    /// <summary>Reads a zip export. Every item is validated before anything is returned; no archive path is extracted.</summary>
    public static IReadOnlyList<EvDatasetEntry> ReadZip(string zipPath) => ReadZip(zipPath, out _);

    /// <summary>Same as <see cref="ReadZip(string)"/>; <paramref name="classes"/> is the manifest class table, or null for a two-class NG/OK dataset.</summary>
    public static IReadOnlyList<EvDatasetEntry> ReadZip(string zipPath, out SegAiClassTable? classes)
    {
        using var stream = File.OpenRead(zipPath);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        if (zip.Entries.Count > 10000 || zip.Entries.Sum(e => e.Length) > 1024L * 1024 * 1024) throw new InvalidDataException("Dataset is too large.");
        var manifest = zip.GetEntry("dataset.json");
        if (manifest == null || manifest.Length > MaxManifestBytes) throw new InvalidDataException("Missing dataset manifest.");
        DatasetFile data;
        using (var reader = new StreamReader(manifest.Open(), Encoding.UTF8)) data = ParseDataset(reader.ReadToEnd());
        classes = ParseClasses(data);
        int classCount = classes?.Classes.Count ?? 2;
        var result = new List<EvDatasetEntry>();
        foreach (var entry in data.Images)
        {
            string imageName = ValidName(entry.Image);
            var imageEntry = zip.GetEntry(imageName);
            if (imageEntry == null || imageEntry.Length > MaxImageBytes) throw new InvalidDataException("Missing/oversized dataset image.");
            byte[] imageBytes = ReadAll(imageEntry);
            var regions = entry.Regions ?? new List<SegAnnotation>();
            using (var decoded = Cv2.ImDecode(imageBytes, ImreadModes.Color))
            {
                if (decoded.Empty()) throw new InvalidDataException("Dataset image could not be decoded.");
                if ((long)decoded.Width * decoded.Height > MaxPixels) throw new InvalidDataException("Dataset image is too large.");
                SegLabelRaster.ValidateAnnotations(regions, decoded.Width, decoded.Height, classCount);
            }
            byte[]? maskBytes = null;
            if (!string.IsNullOrWhiteSpace(entry.Mask))
            {
                var maskEntry = zip.GetEntry(ValidName(entry.Mask));
                if (maskEntry != null)
                {
                    if (maskEntry.Length > MaxImageBytes) throw new InvalidDataException("Missing/oversized dataset image.");
                    maskBytes = ReadAll(maskEntry);
                }
            }
            result.Add(new EvDatasetEntry(entry.Name!, null, null, imageBytes, maskBytes, regions, SegSample.NormalizeMode(entry.Mode)));
        }
        return result;
    }

    /// <summary>
    /// Writes a zip (<c>image{i}.png</c>, <c>mask{i}.png</c>, <c>dataset.json</c> without Key) through a temp file in the
    /// destination folder, then moves it into place. An entry without a mask gets one rasterized from its regions.
    /// </summary>
    public static void WriteZip(string zipPath, IReadOnlyList<EvDatasetEntry> entries, SegAiClassTable? classes = null)
    {
        ArgumentNullException.ThrowIfNull(entries);
        WriteZipCore(zipPath, entries, classes);
    }

    /// <summary>Same as <see cref="WriteZip"/> for a lazily produced sequence: each entry is written as it is produced, so the images are never all held in memory.</summary>
    internal static void WriteZipCore(string zipPath, IEnumerable<EvDatasetEntry> entries, SegAiClassTable? classes = null)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (string.IsNullOrWhiteSpace(zipPath) || zipPath.Split('/', '\\').Contains("..")) throw new ArgumentException("Invalid destination path.", nameof(zipPath));
        string fullPath = Path.GetFullPath(zipPath);
        string folder = Path.GetDirectoryName(fullPath)!;
        Directory.CreateDirectory(folder);
        string staged = Path.Combine(folder, Path.GetFileName(fullPath) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(staged, FileMode.CreateNew, FileAccess.Write))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                var data = new DatasetFile { Classes = ToDatasetClasses(classes) };
                int index = 0;
                foreach (var entry in entries)
                    data.Images.Add(AppendEntry(index++, entry, (name, bytes) => WriteEntry(zip, name, bytes)));
                var json = zip.CreateEntry("dataset.json");
                using (var writer = new StreamWriter(json.Open(), new UTF8Encoding(false))) writer.Write(JsonConvert.SerializeObject(data, DatasetJson));
            }
            File.Move(staged, fullPath, true);
        }
        finally { if (File.Exists(staged)) File.Delete(staged); }
    }

    /// <summary>
    /// Writes a dataset folder (<c>image{i}.png</c>, <c>mask{i}.png</c>, <c>dataset.json</c>) that <see cref="ReadFolder"/> reads.
    /// The folder is created when missing; an existing file of the same name is replaced.
    /// </summary>
    public static void WriteFolder(string folder, IReadOnlyList<EvDatasetEntry> entries, SegAiClassTable? classes = null)
    {
        ArgumentNullException.ThrowIfNull(entries);
        WriteFolderCore(folder, entries, classes);
    }

    /// <summary>Same as <see cref="WriteFolder"/> for a lazily produced sequence (one entry in memory at a time).</summary>
    internal static void WriteFolderCore(string folder, IEnumerable<EvDatasetEntry> entries, SegAiClassTable? classes = null)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (string.IsNullOrWhiteSpace(folder) || folder.Split('/', '\\').Contains("..")) throw new ArgumentException("Invalid destination path.", nameof(folder));
        string fullFolder = Path.GetFullPath(folder);
        Directory.CreateDirectory(fullFolder);
        var data = new DatasetFile { Classes = ToDatasetClasses(classes) };
        int index = 0;
        foreach (var entry in entries)
            data.Images.Add(AppendEntry(index++, entry, (name, bytes) => File.WriteAllBytes(Path.Combine(fullFolder, name), bytes)));
        File.WriteAllText(Path.Combine(fullFolder, "dataset.json"), JsonConvert.SerializeObject(data, DatasetJson), new UTF8Encoding(false));
    }

    // Shared by the zip and folder writers: names the entry, rasterizes a missing mask from the regions, writes image and mask.
    private static DatasetEntry AppendEntry(int index, EvDatasetEntry entry, Action<string, byte[]> write)
    {
        if (string.IsNullOrWhiteSpace(entry.Name)) throw new ArgumentException("Dataset entry has no name.", nameof(entry));
        string image = "image" + index + ".png", mask = "mask" + index + ".png";
        byte[] imageBytes = entry.ReadImageBytes();
        byte[]? maskBytes = entry.ReadMaskBytes();
        if (maskBytes == null)
        {
            using var decoded = Cv2.ImDecode(imageBytes, ImreadModes.Color);
            if (decoded.Empty()) throw new InvalidDataException("Dataset image could not be decoded: " + entry.Name);
            using var raster = SegLabelRaster.Rasterize(entry.Regions, decoded.Width, decoded.Height);
            if (!Cv2.ImEncode(".png", raster, out maskBytes)) throw new IOException("Could not encode mask.");
        }
        write(image, imageBytes);
        write(mask, maskBytes);
        return new DatasetEntry { Name = entry.Name, Image = image, Mask = mask, Regions = entry.Regions.ToList(), Mode = SegSample.NormalizeMode(entry.Mode) };
    }

    private static void WriteEntry(ZipArchive zip, string name, byte[] bytes)
    {
        using var output = zip.CreateEntry(name).Open();
        output.Write(bytes, 0, bytes.Length);
    }

    // The declared entry length is not trusted: the copy itself is capped.
    internal static byte[] ReadAll(ZipArchiveEntry entry, long maxBytes = MaxImageBytes)
    {
        using var input = entry.Open();
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[81920];
        int read;
        while ((read = input.Read(chunk, 0, chunk.Length)) > 0)
        {
            if (buffer.Length + read > maxBytes) throw new InvalidDataException("Missing/oversized dataset image.");
            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    // Names are plain file names: no separators, no drive/root, no "..".
    private static string ValidName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name != Path.GetFileName(name) || name.Contains(':') || name.Contains("..")
            || name.Contains('/') || name.Contains('\\') || Path.IsPathRooted(name))
            throw new InvalidDataException("Invalid dataset file name.");
        return name;
    }

    private static string EntryPath(string folder, string? name) { return Path.Combine(folder, ValidName(name)); }

    private static List<DatasetClass>? ToDatasetClasses(SegAiClassTable? table)
        => table?.Classes.Select(c => new DatasetClass { Name = c.Name, Kind = c.Kind == SegAiClassKind.Good ? "good" : "defect", Enabled = c.Enabled }).ToList();

    private static SegAiClassTable? ParseClasses(DatasetFile data)
    {
        if (data.Classes == null) return null;
        var entries = data.Classes.Select(c => new SegAiClassEntry(c?.Name ?? string.Empty,
            string.Equals(c?.Kind, "good", StringComparison.OrdinalIgnoreCase) ? SegAiClassKind.Good : SegAiClassKind.Defect, c?.Enabled ?? true)).ToList();
        SegAiModelProblem? problem = SegAiClassTable.Validate(entries);
        if (problem != null) throw new InvalidDataException("Invalid dataset class table: " + problem.En);
        return new SegAiClassTable(entries);
    }

    private static DatasetFile ParseDataset(string text)
    {
        if (text.Length > MaxManifestBytes) throw new InvalidDataException("Dataset manifest is too large.");
        var data = JsonConvert.DeserializeObject<DatasetFile>(text, DatasetJson);
        if (data == null || data.Version != 1 || data.Images == null || data.Images.Count > 4096) throw new InvalidDataException("Invalid dataset.");
        if (data.Images.Any(e => e == null || string.IsNullOrWhiteSpace(e.Name))) throw new InvalidDataException("Missing image name.");
        return data;
    }
}
