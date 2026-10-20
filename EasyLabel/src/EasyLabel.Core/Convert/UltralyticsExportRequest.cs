namespace EasyLabel.Core.Convert
{
    public sealed class UltralyticsExportRequest
    {
        public string ProjectRoot { get; set; } = string.Empty;

        /// <summary>Paths of the project; when null the original placement (<c>ProjectRoot\.easyai</c>) is used. Takes precedence over <see cref="ProjectRoot"/>.</summary>
        public ProjectPaths? Paths { get; set; }

        internal ProjectPaths ResolvePaths()
        {
            return Paths ?? ProjectPaths.Legacy(ProjectRoot);
        }

        internal string EffectiveRoot => Paths?.ImageRoot ?? ProjectRoot;

        public string OutputDirectory { get; set; } = string.Empty;

        public LabelConvertFormat LabelFormat { get; set; } = LabelConvertFormat.YoloDetect;

        public double TrainRatio { get; set; } = 0.8;

        public double ValRatio { get; set; } = 0.1;

        public double TestRatio { get; set; } = 0.1;

        public int Seed { get; set; } = 1337;

        public bool StratifyByClass { get; set; }
    }
}
