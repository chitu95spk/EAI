using Newtonsoft.Json;

namespace EasyLabel.Core.Models;

/// <summary>
/// The <c>manifest.json</c> at the root of a model pack (schema 1). Data only: nothing listed in a pack is executed.
/// Each file is installed to <c>{models root}\{Target}\{FileName}</c>. Target is a single folder name.
/// </summary>
public sealed class ModelPackManifest
{
    public const int CurrentSchema = 1;

    [JsonProperty("schema")]
    public int Schema { get; set; }

    [JsonProperty("id")]
    public string Id { get; set; } = string.Empty;

    [JsonProperty("files")]
    public List<ModelPackFile> Files { get; set; } = new List<ModelPackFile>();
}

/// <summary>One file in a model pack. <see cref="Sha256"/> is the lowercase hex digest of the file bytes.</summary>
public sealed class ModelPackFile
{
    [JsonProperty("file")]
    public string FileName { get; set; } = string.Empty;

    [JsonProperty("size")]
    public long Size { get; set; }

    [JsonProperty("sha256")]
    public string Sha256 { get; set; } = string.Empty;

    /// <summary>Licence text shown to the user before installation; required.</summary>
    [JsonProperty("licence")]
    public string Licence { get; set; } = string.Empty;

    /// <summary>Install folder name directly under the models root, e.g. <c>classify-backbone</c>.</summary>
    [JsonProperty("target")]
    public string Target { get; set; } = string.Empty;
}
