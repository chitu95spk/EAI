using EasyLabel.Core.Convert;
using Newtonsoft.Json;

namespace EasyLabel.Core.Package
{
    public sealed class DatasetPackageManifest
    {
        [JsonProperty("version")]
        public string Version { get; set; } = "1.0";

        [JsonProperty("createdAtUtc")]
        public DateTime CreatedAtUtc { get; set; }

        [JsonProperty("sourceSummary")]
        public DatasetPackageSourceSummary SourceSummary { get; set; } = new DatasetPackageSourceSummary();

        [JsonProperty("augmentConfigPath")]
        public string AugmentConfigPath { get; set; } = "augment-config.json";

        [JsonProperty("changelogPath")]
        public string ChangelogPath { get; set; } = "CHANGELOG.txt";

        [JsonProperty("files")]
        public List<DatasetPackageManifestFile> Files { get; set; } = new List<DatasetPackageManifestFile>();
    }

    public sealed class DatasetPackageSourceSummary
    {
        [JsonProperty("sourceFolderName")]
        public string SourceFolderName { get; set; } = string.Empty;

        [JsonProperty("labelFormat")]
        public LabelConvertFormat LabelFormat { get; set; }

        [JsonProperty("trainRatio")]
        public double TrainRatio { get; set; }

        [JsonProperty("valRatio")]
        public double ValRatio { get; set; }

        [JsonProperty("testRatio")]
        public double TestRatio { get; set; }

        [JsonProperty("seed")]
        public int Seed { get; set; }

        [JsonProperty("stratifyByClass")]
        public bool StratifyByClass { get; set; }

        [JsonProperty("totalImages")]
        public int TotalImages { get; set; }

        [JsonProperty("totalLabels")]
        public int TotalLabels { get; set; }

        [JsonProperty("splits")]
        public List<DatasetPackageSplitSummary> Splits { get; set; } = new List<DatasetPackageSplitSummary>();
    }

    public sealed class DatasetPackageSplitSummary
    {
        [JsonProperty("name")]
        public string Name { get; set; } = string.Empty;

        [JsonProperty("images")]
        public int Images { get; set; }

        [JsonProperty("labels")]
        public int Labels { get; set; }
    }

    public sealed class DatasetPackageManifestFile
    {
        [JsonProperty("path")]
        public string Path { get; set; } = string.Empty;

        [JsonProperty("size")]
        public long Size { get; set; }

        [JsonProperty("sha256")]
        public string Sha256 { get; set; } = string.Empty;
    }
}
