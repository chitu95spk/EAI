namespace EasyLabel.Core.Batch
{
    public sealed class AugmentOutput
    {
        public string ImagePath { get; set; } = string.Empty;

        public string JsonPath { get; set; } = string.Empty;

        public string SidecarPath { get; set; } = string.Empty;

        public bool IsClone { get; set; }

        public AugmentVariant? Variant { get; set; }
    }
}
