using EasyLabel.Core.Convert;

namespace EasyLabel.Core.Package
{
    public sealed class DatasetPackageRequest
    {
        public string ProjectRoot { get; set; } = string.Empty;

        /// <summary>Paths of the project; when null the original placement (<c>ProjectRoot\.easyai</c>) is used. Forwarded to the export.</summary>
        public ProjectPaths? Paths { get; set; }

        public string PackagePath { get; set; } = string.Empty;

        public LabelConvertFormat LabelFormat { get; set; } = LabelConvertFormat.YoloDetect;

        public double TrainRatio { get; set; } = 0.8;

        public double ValRatio { get; set; } = 0.1;

        public double TestRatio { get; set; } = 0.1;

        public int Seed { get; set; } = 1337;

        public bool StratifyByClass { get; set; }

        public string AugmentConfigJson { get; set; } = "{}";

        public string Changelog { get; set; } = string.Empty;

        public DateTime? CreatedAtUtc { get; set; }
    }
}
