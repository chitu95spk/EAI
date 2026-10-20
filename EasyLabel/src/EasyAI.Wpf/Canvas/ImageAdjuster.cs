using System.Windows.Media;
using System.Windows.Media.Imaging;
using EasyAI.Desktop.Infrastructure;

namespace EasyAI.Desktop.Annotation;

/// <summary>
/// Display-only brightness / contrast: produces an adjusted copy of the shown bitmap. The document, the file on disk and the
/// Segment AI inputs are never touched. The source pixels are converted once per image.
/// </summary>
public sealed class ImageAdjuster
{
    private BitmapSource? _source;
    private byte[]? _pixels;
    private int _width;
    private int _height;
    private int _stride;

    /// <summary>Sets the image to adjust (null clears). The pixels are read lazily on the first adjustment.</summary>
    public void SetSource(BitmapSource? source)
    {
        _source = source;
        _pixels = null;
    }

    /// <summary>The adjusted bitmap, or the source itself for 0/0 (and when the source cannot be read).</summary>
    public BitmapSource? Apply(int brightness, int contrast)
    {
        if (_source == null || ImageAdjustMath.IsIdentity(brightness, contrast))
        {
            return _source;
        }

        try
        {
            if (_pixels == null)
            {
                FormatConvertedBitmap converted = new FormatConvertedBitmap(_source, PixelFormats.Bgra32, null, 0);
                _width = converted.PixelWidth;
                _height = converted.PixelHeight;
                _stride = _width * 4;
                byte[] buffer = new byte[_stride * _height];
                converted.CopyPixels(buffer, _stride, 0);
                _pixels = buffer;
            }

            byte[] lut = ImageAdjustMath.BuildLut(brightness, contrast);
            byte[] source = _pixels;
            byte[] target = new byte[source.Length];
            for (int i = 0; i + 3 < source.Length; i += 4)
            {
                target[i] = lut[source[i]];
                target[i + 1] = lut[source[i + 1]];
                target[i + 2] = lut[source[i + 2]];
                target[i + 3] = source[i + 3];
            }

            BitmapSource result = BitmapSource.Create(_width, _height, _source.DpiX, _source.DpiY, PixelFormats.Bgra32, null, target, _stride);
            result.Freeze();
            return result;
        }
        catch (Exception)
        {
            return _source;
        }
    }
}
