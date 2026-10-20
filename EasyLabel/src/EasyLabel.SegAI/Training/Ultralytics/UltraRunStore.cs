using Newtonsoft.Json;

namespace EasyLabel.SegAI.Training.Ultralytics;

/// <summary>What <c>ultra-run.json</c> (schema 1) records about one finished, failed or cancelled Ultralytics run.</summary>
public sealed class UltraRunRecord
{
    public const string StatusSuccess = "success";

    public const string StatusFailed = "failed";

    public const string StatusCancelled = "cancelled";

    [JsonProperty("schema")]
    public int Schema { get; set; } = UltraRunStore.SchemaVersion;

    [JsonProperty("runName")]
    public string RunName { get; set; } = string.Empty;

    [JsonProperty("createdUtc")]
    public DateTime CreatedUtc { get; set; }

    [JsonProperty("family")]
    public string Family { get; set; } = string.Empty;

    [JsonProperty("task")]
    public string Task { get; set; } = string.Empty;

    [JsonProperty("epochs")]
    public int Epochs { get; set; }

    [JsonProperty("imgsz")]
    public int ImgSize { get; set; }

    [JsonProperty("batch")]
    public int? Batch { get; set; }

    [JsonProperty("classes")]
    public List<string> Classes { get; set; } = new List<string>();

    /// <summary>success, failed or cancelled.</summary>
    [JsonProperty("status")]
    public string Status { get; set; } = StatusFailed;

    [JsonProperty("errorCode")]
    public string ErrorCode { get; set; } = string.Empty;

    [JsonProperty("message")]
    public string Message { get; set; } = string.Empty;

    [JsonProperty("bestPt")]
    public string BestPt { get; set; } = string.Empty;

    [JsonProperty("onnx")]
    public string? Onnx { get; set; }

    [JsonProperty("map50")]
    public double? Map50 { get; set; }

    [JsonProperty("map5095")]
    public double? Map5095 { get; set; }

    [JsonProperty("elapsedSeconds")]
    public double ElapsedSeconds { get; set; }

    /// <summary>The run folder (set by <see cref="UltraRunStore.Read"/>; not stored).</summary>
    [JsonIgnore]
    public string RunDirectory { get; set; } = string.Empty;
}

/// <summary>Reads and writes <c>ultra-run.json</c> in the run folders below one runs folder.</summary>
public static class UltraRunStore
{
    public const int SchemaVersion = 1;

    public const string FileName = "ultra-run.json";

    /// <summary>Writes the record into <paramref name="runDirectory"/> (created when missing) through a temp file.</summary>
    public static void Write(string runDirectory, UltraRunRecord record)
    {
        Directory.CreateDirectory(runDirectory);
        string path = Path.Combine(runDirectory, FileName);
        string temp = path + ".tmp";
        record.Schema = SchemaVersion;
        File.WriteAllText(temp, JsonConvert.SerializeObject(record, Formatting.Indented));
        File.Move(temp, path, true);
    }

    /// <summary>
    /// The records of all run folders, newest first (by <see cref="UltraRunRecord.CreatedUtc"/>, then by name). A missing runs folder,
    /// a folder without the file and a corrupt or unreadable file give no entry and never throw.
    /// </summary>
    public static IReadOnlyList<UltraRunRecord> Read(string runsDirectory)
    {
        List<UltraRunRecord> list = new List<UltraRunRecord>();
        if (string.IsNullOrWhiteSpace(runsDirectory) || !Directory.Exists(runsDirectory))
        {
            return list;
        }

        string[] folders;
        try
        {
            folders = Directory.GetDirectories(runsDirectory);
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            return list;
        }

        foreach (string folder in folders)
        {
            UltraRunRecord? record = TryReadOne(folder);
            if (record != null)
            {
                list.Add(record);
            }
        }

        list.Sort((a, b) =>
        {
            int byDate = b.CreatedUtc.CompareTo(a.CreatedUtc);
            return byDate != 0 ? byDate : string.Compare(b.RunName, a.RunName, StringComparison.OrdinalIgnoreCase);
        });
        return list;
    }

    private static UltraRunRecord? TryReadOne(string folder)
    {
        string path = Path.Combine(folder, FileName);
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            UltraRunRecord? record = JsonConvert.DeserializeObject<UltraRunRecord>(File.ReadAllText(path));
            if (record == null)
            {
                return null;
            }

            if (string.IsNullOrWhiteSpace(record.RunName))
            {
                record.RunName = Path.GetFileName(folder);
            }

            record.Classes ??= new List<string>();
            record.RunDirectory = folder;
            return record;
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is JsonException)
        {
            return null;
        }
    }
}
