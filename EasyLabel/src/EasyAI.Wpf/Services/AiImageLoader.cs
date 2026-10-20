using System.Windows.Media;
using System.Windows.Media.Imaging;
using EasyAI.Desktop.ViewModels;
using EasyLabel.Ai.Onnx;

namespace EasyAI.Desktop.Services;

/// <summary>Decodes an image file to the BGR layout the AI backends take. Runs on any thread; never touches the canvas bitmap.</summary>
public static class AiImageLoader
{
    /// <summary>Embedding-cache key of the file as it is now (path, last-write ticks, length).</summary>
    public static string KeyOf(string path)
    {
        FileInfo info = new FileInfo(path);
        return AiSuggestionMapper.ImageKey(path, info.LastWriteTimeUtc.Ticks, info.Length);
    }

    /// <summary>Full-resolution BGR24 pixels, rows tightly packed (the canvas coordinates are those of the real image, not of the preview).</summary>
    public static AiImage Load(string path, string cacheKey, CancellationToken ct)
    {
        byte[] bytes = File.ReadAllBytes(path);
        ct.ThrowIfCancellationRequested();
        using MemoryStream stream = new MemoryStream(bytes, false);
        BitmapDecoder decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
        BitmapSource source = decoder.Frames[0];
        if (source.Format != PixelFormats.Bgr24)
        {
            source = new FormatConvertedBitmap(source, PixelFormats.Bgr24, null, 0);
        }

        int width = source.PixelWidth;
        int height = source.PixelHeight;
        byte[] pixels = new byte[checked(width * height * 3)];
        ct.ThrowIfCancellationRequested();
        source.CopyPixels(pixels, width * 3, 0);
        return new AiImage(pixels, width, height, cacheKey);
    }
}
