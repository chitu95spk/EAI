using EasyLabel.Core;
using EasyLabel.Core.Classify;

namespace EasyLabel.Core.Tests;

public sealed class ClassifyModelFileTests : IDisposable
{
    private const string Sha = "9f2c1a7e0b3d4c5e6f708192a3b4c5d6e7f8091a2b3c4d5e6f708192a3b4c5d6";

    private readonly string _root = Path.Combine(AppContext.BaseDirectory, "classify-files-" + Guid.NewGuid().ToString("N"));

    public ClassifyModelFileTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private static ClassifyHead SmallHead() => new ClassifyHead(
        new[] { 0.5f, -1.25f, 3f },
        new[] { 2f, 0.75f, 1f },
        new[] { 0.125f, -2.5f, 0.0625f },
        -0.3125f);

    private static ClassifyModelDocument Doc(string sha = Sha) => new ClassifyModelDocument(
        "okng",
        new DateTime(2026, 10, 12, 1, 2, 3, DateTimeKind.Utc),
        "mobilenetv2-12-cls.onnx",
        sha,
        ClassifyModelFile.FeatureSpecV1,
        3,
        448,
        SmallHead(),
        new ClassifyMetrics(4, 2, 2, 3, 1, 1, 0.75, 0.5, 0.5),
        ClassifyModelFile.MetricsBasisHeldOut,
        2,
        2);

    [Fact]
    public void Each_save_takes_the_next_version_and_never_overwrites()
    {
        string first = ClassifyModelFile.Write(_root, Doc());
        string second = ClassifyModelFile.Write(_root, Doc());

        Assert.EndsWith("okng_v001.eaicls", first);
        Assert.EndsWith("okng_v002.eaicls", second);
        Assert.True(File.Exists(first));
        Assert.True(File.Exists(second));
        Assert.Empty(Directory.EnumerateFiles(_root, "*.tmp"));
    }

    [Fact]
    public void Next_path_skips_existing_versions_case_insensitively()
    {
        File.WriteAllText(Path.Combine(_root, "OKNG_v005.eaicls"), "{}");
        Assert.EndsWith("okng_v006.eaicls", ClassifyModelFile.NextPath(_root, "okng"));
    }

    [Fact]
    public void Round_trip_keeps_head_metrics_and_predictions()
    {
        string path = ClassifyModelFile.Write(_root, Doc());
        ClassifyModelLoad loaded = ClassifyModelFile.Load(path, Sha);

        Assert.Equal(ClassifyModelStatus.Ok, loaded.Status);
        ClassifyModelDocument doc = loaded.Document!;
        Assert.Equal("okng", doc.Name);
        Assert.Equal(new DateTime(2026, 10, 12, 1, 2, 3, DateTimeKind.Utc), doc.CreatedUtc);
        Assert.Equal(448, doc.InputLongSide);
        Assert.Equal(3, doc.FeatureCount);
        Assert.Equal(Doc().Metrics, doc.Metrics);
        Assert.Equal(ClassifyModelFile.MetricsBasisHeldOut, doc.MetricsBasis);

        float[] probe = { 1f, 2f, 3f };
        Assert.Equal(SmallHead().PredictNgProbability(probe), doc.Head.PredictNgProbability(probe));
    }

    [Fact]
    public void Changed_backbone_hash_is_refused_and_case_is_ignored()
    {
        string path = ClassifyModelFile.Write(_root, Doc());

        ClassifyModelLoad other = ClassifyModelFile.Load(path, new string('0', 64));
        Assert.Equal(ClassifyModelStatus.BackboneMismatch, other.Status);
        Assert.Null(other.Document);

        Assert.Equal(ClassifyModelStatus.Ok, ClassifyModelFile.Load(path, Sha.ToUpperInvariant()).Status);
    }

    [Fact]
    public void Missing_wrong_schema_corrupt_and_damaged_files_are_reported_not_thrown()
    {
        Assert.Equal(ClassifyModelStatus.Missing, ClassifyModelFile.Load(Path.Combine(_root, "none.eaicls"), Sha).Status);

        string schema2 = Path.Combine(_root, "schema2.eaicls");
        File.WriteAllText(schema2, "{\"schema\":2}");
        Assert.Equal(ClassifyModelStatus.UnsupportedSchema, ClassifyModelFile.Load(schema2, Sha).Status);

        string corrupt = Path.Combine(_root, "corrupt.eaicls");
        File.WriteAllText(corrupt, "{not json");
        Assert.Equal(ClassifyModelStatus.Invalid, ClassifyModelFile.Load(corrupt, Sha).Status);

        string damaged = Path.Combine(_root, "damaged.eaicls");
        File.WriteAllText(damaged, "{\"schema\":1,\"name\":\"x\"}");
        Assert.Equal(ClassifyModelStatus.Invalid, ClassifyModelFile.Load(damaged, Sha).Status);
    }

    [Fact]
    public void Model_name_must_be_a_plain_file_name()
    {
        Assert.Throws<ArgumentException>(() => ClassifyModelFile.Write(_root, Doc() with { Name = "../evil" }));
        Assert.Throws<ArgumentException>(() => ClassifyModelFile.Write(_root, Doc() with { Name = "a.b" }));
        Assert.Throws<ArgumentException>(() => ClassifyModelFile.Write(_root, Doc() with { Name = " " }));
    }

    [Fact]
    public void Classify_folder_sits_under_the_metadata_folder()
    {
        string meta = Path.Combine(_root, ".easyai");
        var paths = new ProjectPaths(_root, meta);
        Assert.Equal(Path.Combine(meta, "classify"), paths.ClassifyDir);
    }
}
