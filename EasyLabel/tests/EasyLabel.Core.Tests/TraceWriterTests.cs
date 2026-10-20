using System.Security.Cryptography;
using System.Text;
using EasyLabel.Core.Hardware;
using EasyLabel.Core.Trace;
using Newtonsoft.Json;

namespace EasyLabel.Core.Tests;

public sealed class TraceWriterTests : IDisposable
{
    private readonly string _work;
    private readonly string _imageRoot;
    private readonly string _model;

    public TraceWriterTests()
    {
        _work = Path.Combine(AppContext.BaseDirectory, "trace-tests", Guid.NewGuid().ToString("N"));
        _imageRoot = Path.Combine(_work, "img");
        Directory.CreateDirectory(Path.Combine(_imageRoot, "ok"));
        Directory.CreateDirectory(Path.Combine(_imageRoot, "ng"));
        File.WriteAllText(Path.Combine(_imageRoot, "ok", "a.png"), "image-a");
        File.WriteAllText(Path.Combine(_imageRoot, "ng", "b,1.png"), "image-b");
        File.WriteAllText(Path.Combine(_imageRoot, "ok", "c.png"), "image-c");
        _model = Path.Combine(_work, "model.onnx");
        File.WriteAllText(_model, "model-bytes");
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_work))
            {
                Directory.Delete(_work, true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public void Write_SameInputsTwice_ProducesIdenticalManifestAndDatasetHash()
    {
        TraceHashCache cache = new TraceHashCache();

        TraceWriteResult first = TraceWriter.Write(Request(), Path.Combine(_work, "out1"), cache);
        TraceWriteResult second = TraceWriter.Write(Request(), Path.Combine(_work, "out2"), cache);

        Assert.Equal(File.ReadAllBytes(first.ManifestCsvPath), File.ReadAllBytes(second.ManifestCsvPath));
        Assert.Equal(first.DatasetHash, second.DatasetHash);
        Assert.Equal(1, first.Version);
        Assert.Equal(3, first.ImageCount);
    }

    [Fact]
    public void Write_SecondRunReusesCachedHashes()
    {
        TraceHashCache cache = new TraceHashCache();

        TraceWriter.Write(Request(), Path.Combine(_work, "out1"), cache);
        int afterFirst = cache.ComputationCount;
        TraceWriter.Write(Request(), Path.Combine(_work, "out2"), cache);

        // Three images plus the model file are hashed once; the second run hits the cache for all of them.
        Assert.Equal(4, afterFirst);
        Assert.Equal(afterFirst, cache.ComputationCount);
    }

    [Fact]
    public void Write_ChangedImage_IsRehashed()
    {
        TraceHashCache cache = new TraceHashCache();
        TraceWriter.Write(Request(), Path.Combine(_work, "out1"), cache);
        int before = cache.ComputationCount;

        File.WriteAllText(Path.Combine(_imageRoot, "ok", "a.png"), "image-a-changed-length");
        TraceWriteResult changed = TraceWriter.Write(Request(), Path.Combine(_work, "out2"), cache);

        Assert.Equal(before + 1, cache.ComputationCount);
        Assert.Contains("\nok/a.png,", File.ReadAllText(changed.ManifestCsvPath), StringComparison.Ordinal);
    }

    [Fact]
    public void Write_SameFolderTwice_IncrementsVersionAndKeepsFirstFiles()
    {
        string outDir = Path.Combine(_work, "out");
        TraceWriteResult first = TraceWriter.Write(Request(), outDir);
        string firstCsv = File.ReadAllText(first.ManifestCsvPath);

        TraceWriteResult second = TraceWriter.Write(Request(), outDir);

        Assert.Equal(1, first.Version);
        Assert.Equal(2, second.Version);
        Assert.True(File.Exists(first.TraceJsonPath));
        Assert.True(File.Exists(second.TraceJsonPath));
        Assert.Equal(firstCsv, File.ReadAllText(first.ManifestCsvPath));
        Assert.EndsWith("defect_v2.trace.json", second.TraceJsonPath, StringComparison.Ordinal);
    }

    [Fact]
    public void Write_CsvSortsRowsAndQuotesCommas()
    {
        TraceWriteResult result = TraceWriter.Write(Request(), Path.Combine(_work, "out"));

        string[] lines = File.ReadAllText(result.ManifestCsvPath).Split('\n');

        Assert.Equal("path,sha256,tag,split", lines[0]);
        Assert.StartsWith("\"ng/b,1.png\",", lines[1], StringComparison.Ordinal);
        Assert.StartsWith("ok/a.png,", lines[2], StringComparison.Ordinal);
        Assert.StartsWith("ok/c.png,", lines[3], StringComparison.Ordinal);
    }

    [Fact]
    public void Write_RecordHoldsModelHashDatasetHashAndHardware()
    {
        TraceWriteResult result = TraceWriter.Write(Request(), Path.Combine(_work, "out"));

        TraceRecord record = JsonConvert.DeserializeObject<TraceRecord>(File.ReadAllText(result.TraceJsonPath))!;

        Assert.Equal(Sha256File(_model), record.Model.Sha256);
        Assert.Equal(result.DatasetHash, record.DatasetHash);
        Assert.Equal("segment", record.Model.Kind);
        Assert.Equal("defect_v1.manifest.csv", record.ManifestFile);
        Assert.Equal(4, record.Hardware.LogicalCores);
        Assert.Equal(0.5, record.Metrics["map50"]);
        Assert.False(record.Metrics.ContainsKey("bad"));
    }

    [Fact]
    public void Write_BlankOperatorAndNote_AreOmittedFromJson()
    {
        TraceWriteResult blank = TraceWriter.Write(Request(operatorName: "   ", note: ""), Path.Combine(_work, "blank"));
        TraceWriteResult filled = TraceWriter.Write(Request(operatorName: "Kim", note: "lot 7"), Path.Combine(_work, "filled"));

        string blankJson = File.ReadAllText(blank.TraceJsonPath);
        string filledJson = File.ReadAllText(filled.TraceJsonPath);

        Assert.DoesNotContain("\"operator\"", blankJson, StringComparison.Ordinal);
        Assert.DoesNotContain("\"note\"", blankJson, StringComparison.Ordinal);
        Assert.Contains("\"operator\": \"Kim\"", filledJson, StringComparison.Ordinal);
        Assert.Contains("\"note\": \"lot 7\"", filledJson, StringComparison.Ordinal);
    }

    [Fact]
    public void Write_ImageOutsideRoot_IsRejected()
    {
        string outside = Path.Combine(_work, "outside.png");
        File.WriteAllText(outside, "x");
        TraceWriteRequest request = Request(extraImage: new TraceImageInput(outside, "ok", "train"));

        Assert.Throws<ArgumentException>(() => TraceWriter.Write(request, Path.Combine(_work, "out")));
    }

    [Fact]
    public void Write_UnknownKind_IsRejected()
    {
        TraceWriteRequest request = Request(kind: "yolo-v9");

        Assert.Throws<ArgumentException>(() => TraceWriter.Write(request, Path.Combine(_work, "out")));
    }

    private TraceWriteRequest Request(string? operatorName = null, string? note = null, string kind = "segment", TraceImageInput? extraImage = null)
    {
        List<TraceImageInput> images = new List<TraceImageInput>
        {
            new TraceImageInput(Path.Combine(_imageRoot, "ok", "a.png"), "ok", "train"),
            new TraceImageInput(Path.Combine(_imageRoot, "ng", "b,1.png"), "ng", "val"),
            new TraceImageInput(Path.Combine(_imageRoot, "ok", "c.png"), "ok", string.Empty),
        };
        if (extraImage != null)
        {
            images.Add(extraImage);
        }

        return new TraceWriteRequest
        {
            ModelKind = kind,
            ModelName = "defect",
            ModelFilePath = _model,
            ImageRoot = _imageRoot,
            Images = images,
            Metrics = new Dictionary<string, double> { ["map50"] = 0.5, ["bad"] = double.NaN },
            Settings = new Dictionary<string, string> { ["imgsz"] = "640" },
            Hardware = new HardwareInfo(2, 4, 8L << 30, 2L << 30, null, "Test OS"),
            AppVersion = "test",
            Operator = operatorName,
            Note = note,
            CreatedUtc = new DateTime(2026, 10, 12, 0, 0, 0, DateTimeKind.Utc),
        };
    }

    private static string Sha256File(string path)
    {
        return System.Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    }
}
