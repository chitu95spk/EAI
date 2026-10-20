namespace EasyLabel.Core.Batch
{
    public sealed class AugmentRequest
    {
        public string ProjectRoot { get; set; } = string.Empty;

        /// <summary>Paths of the project; when null the original placement (<c>ProjectRoot\.easyai</c>) is used. Takes precedence over <see cref="ProjectRoot"/>.</summary>
        public ProjectPaths? Paths { get; set; }

        internal ProjectPaths ResolvePaths()
        {
            return Paths ?? ProjectPaths.Legacy(ProjectRoot);
        }

        internal string EffectiveRoot => Paths?.ImageRoot ?? ProjectRoot;

        public string ImagePath { get; set; } = string.Empty;

        public string JsonPath { get; set; } = string.Empty;

        public string SidecarPath { get; set; } = string.Empty;

        public int CloneCount { get; set; }

        public List<AugmentVariant> Variants { get; } = new List<AugmentVariant>();

        public double ArbitraryRotationDegrees { get; set; }

        public int HsvHueDelta { get; set; }

        public double HsvSaturationScale { get; set; } = 1.0;

        public double HsvValueScale { get; set; } = 1.0;

        public double ScaleFactor { get; set; } = 1.0;

        public int CropX { get; set; }

        public int CropY { get; set; }

        public int CropWidth { get; set; }

        public int CropHeight { get; set; }
    }
}
