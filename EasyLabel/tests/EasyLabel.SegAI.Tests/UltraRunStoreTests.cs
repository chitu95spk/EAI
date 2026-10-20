using EasyLabel.SegAI.Training.Ultralytics;

namespace EasyLabel.SegAI.Tests;

/// <summary>ultra-run.json (schema 1): round trip, newest-first order and tolerance of missing or corrupt files.</summary>
public sealed class UltraRunStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(AppContext.BaseDirectory, "ultra-run-store-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }

    private static UltraRunRecord Record(string name, DateTime created, string status = "success")
    {
        return new UltraRunRecord
        {
            RunName = name,
            CreatedUtc = created,
            Family = "yolov8n-seg",
            Task = "segment",
            Epochs = 50,
            ImgSize = 640,
            Batch = 8,
            Classes = new List<string> { "scratch", "dent" },
            Status = status,
            BestPt = @"C:\p\best.pt",
            Onnx = @"C:\p\model.onnx",
            Map50 = 0.81,
            Map5095 = 0.52,
            ElapsedSeconds = 123.4,
        };
    }

    [Fact]
    public void WriteThenRead_RoundTripsEveryField()
    {
        string runDir = Path.Combine(_root, "20261009T080000000");
        UltraRunRecord written = Record("20261009T080000000", new DateTime(2026, 10, 9, 8, 0, 0, DateTimeKind.Utc));
        written.ErrorCode = "x";
        written.Message = "msg";
        UltraRunStore.Write(runDir, written);

        Assert.True(File.Exists(Path.Combine(runDir, UltraRunStore.FileName)));
        Assert.False(File.Exists(Path.Combine(runDir, UltraRunStore.FileName + ".tmp")));
        IReadOnlyList<UltraRunRecord> read = UltraRunStore.Read(_root);
        UltraRunRecord one = Assert.Single(read);
        Assert.Equal(1, one.Schema);
        Assert.Equal("20261009T080000000", one.RunName);
        Assert.Equal(written.CreatedUtc, one.CreatedUtc);
        Assert.Equal("yolov8n-seg", one.Family);
        Assert.Equal("segment", one.Task);
        Assert.Equal(50, one.Epochs);
        Assert.Equal(640, one.ImgSize);
        Assert.Equal(8, one.Batch);
        Assert.Equal(new[] { "scratch", "dent" }, one.Classes);
        Assert.Equal("success", one.Status);
        Assert.Equal("x", one.ErrorCode);
        Assert.Equal("msg", one.Message);
        Assert.Equal(@"C:\p\best.pt", one.BestPt);
        Assert.Equal(@"C:\p\model.onnx", one.Onnx);
        Assert.Equal(0.81, one.Map50);
        Assert.Equal(0.52, one.Map5095);
        Assert.Equal(123.4, one.ElapsedSeconds, 6);
        Assert.Equal(runDir, one.RunDirectory);
    }

    [Fact]
    public void Read_IsNewestFirst()
    {
        UltraRunStore.Write(Path.Combine(_root, "a"), Record("a", new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc)));
        UltraRunStore.Write(Path.Combine(_root, "c"), Record("c", new DateTime(2026, 10, 3, 0, 0, 0, DateTimeKind.Utc), "failed"));
        UltraRunStore.Write(Path.Combine(_root, "b"), Record("b", new DateTime(2026, 10, 2, 0, 0, 0, DateTimeKind.Utc), "cancelled"));

        Assert.Equal(new[] { "c", "b", "a" }, UltraRunStore.Read(_root).Select(r => r.RunName).ToArray());
    }

    [Fact]
    public void Read_ToleratesMissingFolderEmptyFolderAndCorruptJson()
    {
        Assert.Empty(UltraRunStore.Read(Path.Combine(_root, "missing")));
        Assert.Empty(UltraRunStore.Read(string.Empty));

        Directory.CreateDirectory(Path.Combine(_root, "no-file"));
        Directory.CreateDirectory(Path.Combine(_root, "corrupt"));
        File.WriteAllText(Path.Combine(_root, "corrupt", UltraRunStore.FileName), "{ not json");
        Directory.CreateDirectory(Path.Combine(_root, "wrong-shape"));
        File.WriteAllText(Path.Combine(_root, "wrong-shape", UltraRunStore.FileName), "[1,2,3]");
        UltraRunStore.Write(Path.Combine(_root, "good"), Record("good", new DateTime(2026, 10, 5, 0, 0, 0, DateTimeKind.Utc)));

        IReadOnlyList<UltraRunRecord> read = UltraRunStore.Read(_root);
        Assert.Equal("good", Assert.Single(read).RunName);
    }

    [Fact]
    public void Read_FillsNameFromFolderAndKeepsMissingFieldsAtDefaults()
    {
        Directory.CreateDirectory(Path.Combine(_root, "folder-name"));
        File.WriteAllText(Path.Combine(_root, "folder-name", UltraRunStore.FileName), "{\"schema\":1,\"status\":\"failed\",\"classes\":null}");

        UltraRunRecord one = Assert.Single(UltraRunStore.Read(_root));
        Assert.Equal("folder-name", one.RunName);
        Assert.Equal("failed", one.Status);
        Assert.Empty(one.Classes);
        Assert.Null(one.Map50);
        Assert.Null(one.Batch);
    }
}
