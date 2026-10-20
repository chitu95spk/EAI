using EasyLabel.SegAI.Model;

namespace EasyLabel.SegAI.Tests;

public sealed class ModelFileTests
{
    [SegAiFixtureFact(false, SegAiFixtures.Synthetic)]
    public void Inspect_SyntheticFixture_IsValid() => AssertValid(SegAiFixtures.Synthetic);

    [SegAiFixtureFact(false, SegAiFixtures.V2Both)]
    public void Inspect_V2BothFixture_IsValid() => AssertValid(SegAiFixtures.V2Both);

    [SegAiFixtureFact(false, SegAiFixtures.Scan)]
    public void Inspect_ScanFixture_IsValid() => AssertValid(SegAiFixtures.Scan);

    [Fact]
    public void Inspect_CorruptFile_ReportsProblemWithoutThrowing()
    {
        string path = Path.Combine(SegAiFixtures.ScratchFolder("model-file"), "corrupt.segai");
        var bytes = new byte[512];
        new Random(42).NextBytes(bytes);
        File.WriteAllBytes(path, bytes);

        SegAiModelInfo info = SegAiModelFile.Inspect(path);

        Assert.NotNull(info.Problem);
        Assert.False(string.IsNullOrEmpty(info.Problem!.Vi));
        Assert.False(string.IsNullOrEmpty(info.Problem.En));
    }

    [Fact]
    public void Inspect_MissingAndEmptyFiles_ReportProblemWithoutThrowing()
    {
        string folder = SegAiFixtures.ScratchFolder("model-file");
        string empty = Path.Combine(folder, "empty.segai");
        File.WriteAllBytes(empty, Array.Empty<byte>());

        Assert.NotNull(SegAiModelFile.Inspect(empty).Problem);
        Assert.NotNull(SegAiModelFile.Inspect(Path.Combine(folder, "does-not-exist.segai")).Problem);
    }

    private const string Yaml = "%YAML:1.0\n---\nnative_v2: 1\npostprocess_version: 2\nside: 224\n";

    private static SegAiClassEntry[] ThreeClasses() => new[]
    {
        new SegAiClassEntry("Scratch", SegAiClassKind.Defect, true),
        new SegAiClassEntry("Dent", SegAiClassKind.Defect, false),
        new SegAiClassEntry("Tốt", SegAiClassKind.Good, true),
    };

    /// <summary>Raw table bytes without validation, so invalid tables can be built. Pass a count to lie about N.</summary>
    private static byte[] RawTable(IReadOnlyList<SegAiClassEntry> classes, uint? declaredCount = null, bool breakCrc = false)
    {
        using var buffer = new MemoryStream();
        using (var writer = new BinaryWriter(buffer))
        {
            writer.Write(new[] { (byte)'S', (byte)'C', (byte)'L', (byte)'S' });
            writer.Write(1u);
            writer.Write(declaredCount ?? (uint)classes.Count);
            foreach (SegAiClassEntry entry in classes)
            {
                byte[] name = System.Text.Encoding.UTF8.GetBytes(entry.Name);
                writer.Write((byte)entry.Kind);
                writer.Write((byte)(entry.Enabled ? 1 : 0));
                writer.Write((ushort)name.Length);
                writer.Write(name);
            }
        }

        byte[] body = buffer.ToArray();
        uint crc = SegAiClassTable.Crc32(body, 0, body.Length);
        byte[] result = new byte[body.Length + 4];
        body.CopyTo(result, 0);
        BitConverter.GetBytes(breakCrc ? crc ^ 1u : crc).CopyTo(result, body.Length);
        return result;
    }

    /// <summary>Header (72 bytes) + YAML + optional class table, laid out like a native-written file.</summary>
    private static byte[] BuildFile(uint version, uint numClasses, byte[]? table, uint? reservedA = null)
    {
        byte[] yaml = System.Text.Encoding.UTF8.GetBytes(Yaml);
        var file = new byte[SegAiModelFile.HeaderSize + yaml.Length + (table?.Length ?? 0)];
        new byte[] { 83, 69, 71, 65, 73, 0, 0, 0 }.CopyTo(file, 0);
        BitConverter.GetBytes(version).CopyTo(file, 8);
        BitConverter.GetBytes(0x02000000u).CopyTo(file, 12);
        BitConverter.GetBytes(numClasses).CopyTo(file, 16);
        BitConverter.GetBytes(448u).CopyTo(file, 20);
        BitConverter.GetBytes(1u).CopyTo(file, 28);
        BitConverter.GetBytes(reservedA ?? (uint)(table?.Length ?? 0)).CopyTo(file, 52);
        BitConverter.GetBytes((ulong)yaml.Length).CopyTo(file, 56);
        BitConverter.GetBytes(SegAiModelFile.FastCrc(yaml, 0, yaml.Length)).CopyTo(file, 64);
        yaml.CopyTo(file, SegAiModelFile.HeaderSize);
        table?.CopyTo(file, SegAiModelFile.HeaderSize + yaml.Length);
        return file;
    }

    private static string Write(string name, byte[] bytes)
    {
        string path = Path.Combine(SegAiFixtures.ScratchFolder("model-file-v4"), name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    [Fact]
    public void V4_ValidFile_IsAcceptedAndTableReadBack()
    {
        SegAiClassEntry[] classes = ThreeClasses();
        string path = Write("valid.segai", BuildFile(4, 4, new SegAiClassTable(classes).Serialize()));

        SegAiModelInfo info = SegAiModelFile.Inspect(path);

        Assert.Null(info.Problem);
        Assert.Equal(4, info.FormatVersion);
        Assert.True(info.IsV2);
        Assert.False(info.IsStructural);
        Assert.Equal(1, info.TrainedMinimumArea);
        Assert.Equal(1, SegAiModelFile.ReadTrainedMinimumArea(path));
        SegAiClassTable? table = SegAiModelFile.ReadClassTable(path, out SegAiModelProblem? problem);
        Assert.Null(problem);
        Assert.Equal(classes, table!.Classes);
    }

    [Fact]
    public void V4_TableRoundTrip_IsStable()
    {
        byte[] first = new SegAiClassTable(ThreeClasses()).Serialize();
        Assert.True(SegAiClassTable.TryParse(first, 0, first.Length, 4, out SegAiClassTable? parsed, out SegAiModelProblem? problem));
        Assert.Null(problem);
        Assert.Equal(ThreeClasses(), parsed!.Classes);
        Assert.Equal(first, parsed.Serialize());
        Assert.True(first.Length <= SegAiClassTable.MaxBytes);
    }

    [Fact]
    public void V4_Truncated_ReportsSizeMismatch()
    {
        byte[] file = BuildFile(4, 4, new SegAiClassTable(ThreeClasses()).Serialize());
        string path = Write("truncated.segai", file.AsSpan(0, file.Length - 10).ToArray());

        SegAiModelProblem? problem = SegAiModelFile.Inspect(path).Problem;

        Assert.NotNull(problem);
        Assert.Contains("class table", problem!.En);
        Assert.Contains("truncated", problem.En);
    }

    [Fact]
    public void V4_BadTableCrc_IsRejectedWithClearCause()
    {
        string path = Write("badcrc.segai", BuildFile(4, 4, RawTable(ThreeClasses(), breakCrc: true)));

        Assert.Null(SegAiModelFile.Inspect(path).Problem);
        SegAiModelProblem rejected = SegAiModelFile.ClassifyRejectedModel(path, -5, null);

        Assert.Contains("class table", rejected.En);
        Assert.Contains("CRC", rejected.En);
        Assert.Null(SegAiModelFile.ReadClassTable(path, out SegAiModelProblem? problem));
        Assert.Contains("CRC", problem!.En);
    }

    [Fact]
    public void V4_ClassCountMismatch_IsRejected()
    {
        // Header says N + 1 = 3 (N = 2) but the table holds three classes.
        string path = Write("mismatch.segai", BuildFile(4, 3, new SegAiClassTable(ThreeClasses()).Serialize()));

        Assert.Null(SegAiModelFile.ReadClassTable(path, out SegAiModelProblem? problem));
        Assert.Contains("does not match", problem!.En);
        Assert.Contains("class table", SegAiModelFile.ClassifyRejectedModel(path, -5, null).En);
    }

    [Fact]
    public void V4_DuplicateName_IsRejected()
    {
        var classes = new[]
        {
            new SegAiClassEntry("A", SegAiClassKind.Defect, true),
            new SegAiClassEntry("A", SegAiClassKind.Defect, true),
        };
        string path = Write("duplicate.segai", BuildFile(4, 3, RawTable(classes)));

        Assert.Null(SegAiModelFile.ReadClassTable(path, out SegAiModelProblem? problem));
        Assert.Contains("duplicate", problem!.En);
        Assert.Throws<InvalidDataException>(() => new SegAiClassTable(classes).Serialize());
    }

    [Fact]
    public void V4_TwoGoodClasses_AreRejected()
    {
        var classes = new[]
        {
            new SegAiClassEntry("A", SegAiClassKind.Good, true),
            new SegAiClassEntry("B", SegAiClassKind.Good, true),
        };
        string path = Write("twogood.segai", BuildFile(4, 3, RawTable(classes)));

        Assert.Null(SegAiModelFile.ReadClassTable(path, out SegAiModelProblem? problem));
        Assert.Contains("more than one GOOD", problem!.En);
    }

    [Fact]
    public void V4_NoEnabledClass_IsRejected()
    {
        var classes = new[]
        {
            new SegAiClassEntry("A", SegAiClassKind.Defect, false),
            new SegAiClassEntry("B", SegAiClassKind.Good, false),
        };
        string path = Write("noenabled.segai", BuildFile(4, 3, RawTable(classes)));

        Assert.Null(SegAiModelFile.ReadClassTable(path, out SegAiModelProblem? problem));
        Assert.Contains("no enabled class", problem!.En);
    }

    [Fact]
    public void Version5_IsReportedAsUnsupportedNewer()
    {
        string path = Write("v5.segai", BuildFile(5, 4, new SegAiClassTable(ThreeClasses()).Serialize()));

        SegAiModelProblem? problem = SegAiModelFile.Inspect(path).Problem;

        Assert.NotNull(problem);
        Assert.Contains("unsupported file version 5", problem!.En);
        Assert.Contains("newer", problem.En);
        Assert.Throws<InvalidDataException>(() => SegAiModelFile.ReadTrainedMinimumArea(path));
    }

    [Fact]
    public void V4_MetadataAppend_PreservesTable()
    {
        SegAiClassEntry[] classes = ThreeClasses();
        string path = Write("append.segai", BuildFile(4, 4, new SegAiClassTable(classes).Serialize()));

        SegAiModelFile.AppendModelMetadata(path, SegAiModelFile.ReportKey, "{\"a\":1}");

        SegAiModelInfo info = SegAiModelFile.Inspect(path);
        Assert.Null(info.Problem);
        Assert.Equal("{\"a\":1}", SegAiModelFile.ReadReportJson(path));
        SegAiClassTable? table = SegAiModelFile.ReadClassTable(path, out SegAiModelProblem? problem);
        Assert.Null(problem);
        Assert.Equal(classes, table!.Classes);
    }

    [Fact]
    public void Legacy_V3_IsUnchangedAndGetsInMemoryNgOkTable()
    {
        string path = Write("legacy-v3.segai", BuildFile(3, 3, null));

        SegAiModelInfo info = SegAiModelFile.Inspect(path);

        Assert.Null(info.Problem);
        Assert.Equal(3, info.FormatVersion);
        Assert.True(info.IsV2);
        SegAiClassTable? table = SegAiModelFile.ReadClassTable(path, out SegAiModelProblem? problem);
        Assert.Null(problem);
        Assert.Equal(SegAiClassTable.Legacy().Classes, table!.Classes);

        // A v3 file with trailing bytes must still be refused (exact size rule), as before.
        string trailing = Write("legacy-v3-trailing.segai", BuildFile(3, 3, new byte[] { 1, 2, 3 }));
        Assert.NotNull(SegAiModelFile.Inspect(trailing).Problem);
    }

    private static void AssertValid(string relative)
    {
        string path = SegAiFixtures.Resolve(relative)!;

        SegAiModelInfo info = SegAiModelFile.Inspect(path);

        Assert.Null(info.Problem);
        Assert.InRange(info.FormatVersion, 1, 4);
        Assert.Equal(info.FormatVersion == 2, info.IsStructural);
        Assert.Equal(info.FormatVersion == 3 || info.FormatVersion == 4, info.IsV2);
        Assert.True(info.TrainedMinimumArea >= 1);
    }
}
