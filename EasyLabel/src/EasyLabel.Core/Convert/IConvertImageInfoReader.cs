namespace EasyLabel.Core.Convert
{
    public interface IConvertImageInfoReader
    {
        bool TryRead(string annotationPath, out ImageInfo imageInfo);
    }
}
