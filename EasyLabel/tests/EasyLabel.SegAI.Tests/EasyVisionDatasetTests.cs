using System.IO.Compression;
using System.Text;
using EasyLabel.SegAI.Data;
using OpenCvSharp;

namespace EasyLabel.SegAI.Tests;

public sealed class EasyVisionDatasetTests
{
    private static string Scratch(string name)
    {
        string dir = Path.Combine(AppContext.BaseDirectory, "scratch", "evdataset", name + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static byte[] Png(int seed)
    {
        using var image = new Mat(64, 64, MatType.CV_8UC3, new Scalar(seed * 40, 100, 200 - seed * 30));
        Cv2.Circle(image, new Point(10 + seed, 20), 5, new Scalar(255, 255, 255), -1);
        Cv2.ImEncode(".png", image, out byte[] bytes);
        return bytes;
    }

    private static List<EvDatasetEntry> Entries()
    {
        var list = new List<EvDatasetEntry>();
        for (int i = 0; i < 3; i++)
        {
            var regions = new List<SegAnnotation>();
            if (i == 1) regions.Add(SegLabelRasterTests.Square(1, 8, 8, 56, 56, SegLabelRasterTests.Box(24, 24, 40, 40)));
            else if (i == 2) regions.Add(SegLabelRasterTests.Square(2, 4, 4, 20, 20));
            list.Add(new EvDatasetEntry("img" + i + ".bmp", null, null, Png(i), null, regions, (byte)(i == 2 ? 2 : 0)));
        }
        return list;
    }

    private static void WriteManifestZip(string path, string manifest, params string[] extraEntries)
    {
        using var stream = File.Create(path);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create);
        using (var w = new StreamWriter(zip.CreateEntry("dataset.json").Open(), new UTF8Encoding(false))) w.Write(manifest);
        foreach (string name in extraEntries)
        {
            using var s = zip.CreateEntry(name).Open();
            byte[] png = Png(0);
            s.Write(png, 0, png.Length);
        }
    }

    [Fact]
    public void ReadAll_CapsTheCopy_RegardlessOfDeclaredLength()
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
        {
            using var s = zip.CreateEntry("big.bin").Open();
            s.Write(new byte[1000], 0, 1000);
        }

        stream.Position = 0;
        using var read = new ZipArchive(stream, ZipArchiveMode.Read);
        ZipArchiveEntry entry = read.GetEntry("big.bin")!;
        Assert.Equal(1000, EasyVisionDataset.ReadAll(entry, 1000).Length);
        Assert.Throws<InvalidDataException>(() => EasyVisionDataset.ReadAll(entry, 999));
    }

    [Fact]
    public void Zip_round_trip_keeps_regions_and_mask_bytes()
    {
        string dir = Scratch("zip");
        string zipPath = Path.Combine(dir, "out.zip");
        var source = Entries();
        EasyVisionDataset.WriteZip(zipPath, source);
        Assert.Empty(Directory.GetFiles(dir, "*.tmp"));
        var read = EasyVisionDataset.ReadZip(zipPath);
        Assert.Equal(3, read.Count);
        for (int i = 0; i < 3; i++)
        {
            Assert.Equal(source[i].Name, read[i].Name);
            Assert.Equal(source[i].Mode, read[i].Mode);
            Assert.Equal(source[i].ImageBytes, read[i].ImageBytes);
            Assert.Equal(source[i].Regions.Count, read[i].Regions.Count);
            for (int r = 0; r < source[i].Regions.Count; r++)
            {
                Assert.Equal(source[i].Regions[r].Label, read[i].Regions[r].Label);
                Assert.Equal(source[i].Regions[r].Points, read[i].Regions[r].Points);
                Assert.Equal(source[i].Regions[r].Holes.Length, read[i].Regions[r].Holes.Length);
            }
            using var mask = read[i].LoadMask();
            Assert.NotNull(mask);
            using var expected = SegLabelRaster.Rasterize(source[i].Regions, 64, 64);
            using var diff = new Mat();
            Cv2.Compare(mask!, expected, diff, CmpTypes.NE);
            Assert.Equal(0, Cv2.CountNonZero(diff));
        }
        Assert.Single(read[1].Regions[0].Holes);
    }

    [Fact]
    public void ReadFolder_reads_valid_and_rejects_version_2_and_dotdot_names()
    {
        string good = Scratch("folder-good");
        File.WriteAllBytes(Path.Combine(good, "a.png"), Png(0));
        File.WriteAllBytes(Path.Combine(good, "m.labels.png"), Png(1));
        File.WriteAllText(good + "/dataset.json", "{\"Version\":1,\"Images\":[{\"Name\":\"x.bmp\",\"Image\":\"a.png\",\"Mask\":\"m.labels.png\",\"Key\":\"k\",\"Regions\":[],\"Mode\":2}]}");
        var entries = EasyVisionDataset.ReadFolder(good);
        Assert.Single(entries);
        Assert.Equal(2, entries[0].Mode);
        Assert.Equal("k", entries[0].Key);

        string v2 = Scratch("folder-v2");
        File.WriteAllText(v2 + "/dataset.json", "{\"Version\":2,\"Images\":[]}");
        Assert.Throws<InvalidDataException>(() => EasyVisionDataset.ReadFolder(v2));

        string bad = Scratch("folder-bad");
        File.WriteAllText(bad + "/dataset.json", "{\"Version\":1,\"Images\":[{\"Name\":\"x\",\"Image\":\"..\\\\a.png\",\"Mask\":\"m.png\",\"Regions\":[]}]}");
        Assert.Throws<InvalidDataException>(() => EasyVisionDataset.ReadFolder(bad));
    }

    [Fact]
    public void ReadZip_rejects_traversal_entry_name()
    {
        string dir = Scratch("zip-bad");
        string zipPath = Path.Combine(dir, "bad.zip");
        WriteManifestZip(zipPath, "{\"Version\":1,\"Images\":[{\"Name\":\"x\",\"Image\":\"../x.png\",\"Mask\":\"mask0.png\",\"Regions\":[]}]}", "mask0.png");
        Assert.Throws<InvalidDataException>(() => EasyVisionDataset.ReadZip(zipPath));
    }

    [Fact]
    public void WriteZip_rejects_dotdot_destination()
    {
        Assert.Throws<ArgumentException>(() => EasyVisionDataset.WriteZip(Path.Combine("scratch", "..", "x.zip"), Entries()));
    }

    [Fact]
    public void ImageKey_is_stable_lowercase_hex()
    {
        using var image = Cv2.ImDecode(Png(1), ImreadModes.Color);
        string key = ImageKey.Compute(image);
        Assert.Equal(64, key.Length);
        Assert.Equal(key.ToLowerInvariant(), key);
        Assert.Equal(key, ImageKey.Compute(image));
    }

    [Fact]
    public void Zip_without_table_has_no_classes_key_and_reads_back_null()
    {
        string zipPath = Path.Combine(Scratch("noclasses"), "out.zip");
        EasyVisionDataset.WriteZip(zipPath, Entries());
        using (var zip = ZipFile.OpenRead(zipPath))
        using (var reader = new StreamReader(zip.GetEntry("dataset.json")!.Open()))
            Assert.DoesNotContain("Classes", reader.ReadToEnd());
        EasyVisionDataset.ReadZip(zipPath, out var classes);
        Assert.Null(classes);
    }

    [Fact]
    public void Zip_with_class_table_round_trips_labels_above_two()
    {
        string zipPath = Path.Combine(Scratch("classes"), "out.zip");
        var table = new EasyLabel.SegAI.Model.SegAiClassTable(new[]
        {
            new EasyLabel.SegAI.Model.SegAiClassEntry("scratch", EasyLabel.SegAI.Model.SegAiClassKind.Defect, true),
            new EasyLabel.SegAI.Model.SegAiClassEntry("dent", EasyLabel.SegAI.Model.SegAiClassKind.Defect, true),
            new EasyLabel.SegAI.Model.SegAiClassEntry("good", EasyLabel.SegAI.Model.SegAiClassKind.Good, true),
        });
        var regions = new List<SegAnnotation> { SegLabelRasterTests.Square(3, 5, 5, 30, 30) };
        var entry = new EvDatasetEntry("a.png", null, null, Png(1), null, regions, 0);
        EasyVisionDataset.WriteZip(zipPath, new[] { entry }, table);
        var read = EasyVisionDataset.ReadZip(zipPath, out var back);
        Assert.NotNull(back);
        Assert.Equal(new[] { "scratch", "dent", "good" }, back!.Classes.Select(c => c.Name).ToArray());
        Assert.Equal((byte)3, read[0].Regions[0].Label);
    }
}
