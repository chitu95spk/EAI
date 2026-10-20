namespace EasyLabel.Core.Batch
{
    public sealed class AugmentImageOperation
    {
        public bool IsClone { get; set; }

        public AugmentVariant? Variant { get; set; }

        public string SourcePath { get; set; } = string.Empty;

        public string OutputPath { get; set; } = string.Empty;

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
