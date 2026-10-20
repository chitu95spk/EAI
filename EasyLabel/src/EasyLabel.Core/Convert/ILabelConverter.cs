using EasyLabel.Core.Model;

namespace EasyLabel.Core.Convert
{
    public interface ILabelConverter
    {
        LabelConvertFormat SourceFormat { get; }

        LabelConvertFormat TargetFormat { get; }

        string[] Export(LabelMeDocument document, IReadOnlyList<string> classes);

        LabelMeDocument Import(string[] lines, IReadOnlyList<string> classes, int imageWidth, int imageHeight, string imagePath);
    }
}
