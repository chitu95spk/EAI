namespace EasyLabel.Core.Convert
{
    public sealed class StandaloneConvertRequest
    {
        public string InputDirectory { get; set; } = string.Empty;

        public string OutputDirectory { get; set; } = string.Empty;

        public LabelConvertFormat SourceFormat { get; set; }

        public LabelConvertFormat TargetFormat { get; set; }
    }
}
