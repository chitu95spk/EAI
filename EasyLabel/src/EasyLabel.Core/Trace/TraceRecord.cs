using EasyLabel.Core.Hardware;
using Newtonsoft.Json;

namespace EasyLabel.Core.Trace;

/// <summary>
/// The <c>&lt;model&gt;_v&lt;n&gt;.trace.json</c> record (schema 1). Per-image rows live in the matching manifest CSV;
/// <see cref="DatasetHash"/> is the SHA-256 of that CSV, so the CSV can be checked against the record.
/// </summary>
public sealed class TraceRecord
{
    public const int CurrentSchema = 1;

    [JsonProperty("schema")]
    public int Schema { get; set; } = CurrentSchema;

    [JsonProperty("createdUtc")]
    public string CreatedUtc { get; set; } = string.Empty;

    [JsonProperty("appVersion")]
    public string AppVersion { get; set; } = string.Empty;

    [JsonProperty("model")]
    public TraceModel Model { get; set; } = new TraceModel();

    [JsonProperty("metrics")]
    public SortedDictionary<string, double> Metrics { get; set; } = new SortedDictionary<string, double>(StringComparer.Ordinal);

    [JsonProperty("settings")]
    public SortedDictionary<string, string> Settings { get; set; } = new SortedDictionary<string, string>(StringComparer.Ordinal);

    [JsonProperty("imageCount")]
    public int ImageCount { get; set; }

    [JsonProperty("datasetHash")]
    public string DatasetHash { get; set; } = string.Empty;

    [JsonProperty("manifestFile")]
    public string ManifestFile { get; set; } = string.Empty;

    [JsonProperty("hardware")]
    public TraceHardware Hardware { get; set; } = new TraceHardware();

    /// <summary>Optional free text, omitted from the JSON when empty.</summary>
    [JsonProperty("operator", NullValueHandling = NullValueHandling.Ignore)]
    public string? Operator { get; set; }

    /// <summary>Optional free text, omitted from the JSON when empty.</summary>
    [JsonProperty("note", NullValueHandling = NullValueHandling.Ignore)]
    public string? Note { get; set; }
}

public sealed class TraceModel
{
    /// <summary>One of <c>segment</c>, <c>ultra</c> or <c>classify</c>.</summary>
    [JsonProperty("kind")]
    public string Kind { get; set; } = string.Empty;

    [JsonProperty("name")]
    public string Name { get; set; } = string.Empty;

    [JsonProperty("version")]
    public int Version { get; set; }

    /// <summary>Lowercase hex SHA-256 of the model file.</summary>
    [JsonProperty("sha256")]
    public string Sha256 { get; set; } = string.Empty;
}

/// <summary>Hardware summary for the record. Zero or empty means the value could not be read.</summary>
public sealed class TraceHardware
{
    [JsonProperty("physicalCores")]
    public int PhysicalCores { get; set; }

    [JsonProperty("logicalCores")]
    public int LogicalCores { get; set; }

    [JsonProperty("totalRamMb")]
    public long TotalRamMb { get; set; }

    /// <summary>Name of the first NVIDIA adapter, empty when none.</summary>
    [JsonProperty("gpu")]
    public string Gpu { get; set; } = string.Empty;

    [JsonProperty("gpuVramMb")]
    public long GpuVramMb { get; set; }

    [JsonProperty("os")]
    public string Os { get; set; } = string.Empty;

    public static TraceHardware From(HardwareInfo? info)
    {
        if (info == null)
        {
            return new TraceHardware();
        }

        string gpu = string.Empty;
        foreach (GpuInfo g in info.Gpus)
        {
            if (g.Vendor == GpuVendor.Nvidia)
            {
                gpu = g.Name;
                break;
            }
        }

        return new TraceHardware
        {
            PhysicalCores = info.PhysicalCores,
            LogicalCores = info.LogicalCores,
            TotalRamMb = info.TotalRamBytes / (1024 * 1024),
            Gpu = gpu,
            GpuVramMb = info.CudaVramBytes / (1024 * 1024),
            Os = info.OsDescription,
        };
    }
}
