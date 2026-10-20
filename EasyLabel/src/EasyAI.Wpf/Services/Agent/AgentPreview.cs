using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using EasyLabel.Ai.Onnx;

namespace EasyAI.Desktop.Services.Agent;

/// <summary>
/// The downscaled picture a Codex run gets with its first message, so the model can look at the image without a get_crop turn.
/// Tool coordinates stay in original pixels; the note added to the prompt tells the model the factor.
/// </summary>
public static class AgentPreview
{
    public const string FileName = "preview.png";

    /// <summary>Longest side of the preview.</summary>
    public const int MaxSide = 612;

    /// <summary>Distance between two grid lines, in preview pixels.</summary>
    public const int GridStep = 50;

    // The picture with a numbered grid: the model reads positions off the labels instead of estimating them from the pixels.
    private static BitmapSource DrawGrid(BitmapSource source)
    {
        int w = source.PixelWidth, h = source.PixelHeight;
        Pen pen = new Pen(new SolidColorBrush(Color.FromArgb(125, 255, 90, 0)), 1);
        pen.Freeze();
        DrawingVisual visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
        {
            dc.DrawImage(source, new Rect(0, 0, w, h));
            for (int x = GridStep; x < w; x += GridStep)
            {
                dc.DrawLine(pen, new Point(x + 0.5, 0), new Point(x + 0.5, h));
                Label(dc, x.ToString(CultureInfo.InvariantCulture), x + 1, 0);
                Label(dc, x.ToString(CultureInfo.InvariantCulture), x + 1, h - 13);
            }

            for (int y = GridStep; y < h; y += GridStep)
            {
                dc.DrawLine(pen, new Point(0, y + 0.5), new Point(w, y + 0.5));
                Label(dc, y.ToString(CultureInfo.InvariantCulture), 1, y + 1);
                Label(dc, y.ToString(CultureInfo.InvariantCulture), w - 26, y + 1);
            }

            for (int x = 2 * GridStep; x < w; x += 2 * GridStep)
            {
                for (int y = 2 * GridStep; y < h; y += 2 * GridStep)
                {
                    Label(dc, x.ToString(CultureInfo.InvariantCulture) + "," + y.ToString(CultureInfo.InvariantCulture), x + 2, y + 2);
                }
            }
        }

        RenderTargetBitmap target = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        target.Freeze();
        return target;
    }

    private static void Label(DrawingContext dc, string text, double x, double y)
    {
        Typeface face = new Typeface("Segoe UI");
        FormattedText shadow = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, 9, Brushes.Black, 1.0);
        FormattedText front = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, 9, Brushes.Yellow, 1.0);
        dc.DrawText(shadow, new Point(x + 1, y + 1));
        dc.DrawText(front, new Point(x, y));
    }

    // The image as text (blob table + occupancy map), made here so the model reads it from the prompt instead of spending a tool turn.
    private static string BlobTable(string imagePath)
    {
        try
        {
            using OpenCvSharp.Mat mat = OpenCvSharp.Cv2.ImDecode(File.ReadAllBytes(imagePath), OpenCvSharp.ImreadModes.Color | OpenCvSharp.ImreadModes.IgnoreOrientation);
            return mat.Empty() ? string.Empty : "\n\nThe image as text, made by the app (positions are in ORIGINAL pixels; a row marked merged? holds touching objects, so read those from the picture):\n" + ImageMarkdown.Build(mat);
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    /// <summary>Writes <c>preview.png</c> into the run folder; null (and an empty note) when the image cannot be decoded.</summary>
    public static string? TryWrite(string imagePath, string runFolder, int imageWidth, int imageHeight, string? modelsFolder, out string note)
    {
        note = string.Empty;
        string? numbered = Sam2Preview.TryWrite(imagePath, runFolder, modelsFolder, out note);
        if (numbered != null)
        {
            return numbered;
        }

        note = string.Empty;
        try
        {
            if (imageWidth <= 0 || imageHeight <= 0)
            {
                return null;
            }

            double scale = Math.Min(1.0, (double)MaxSide / Math.Max(imageWidth, imageHeight));
            BitmapImage bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.UriSource = new Uri(Path.GetFullPath(imagePath));
            if (scale < 1.0)
            {
                bitmap.DecodePixelWidth = Math.Max(1, (int)Math.Round(imageWidth * scale));
            }

            bitmap.EndInit();
            bitmap.Freeze();
            BitmapSource picture = bitmap;
            bool grid = false;
            try
            {
                picture = DrawGrid(bitmap);
                grid = true;
            }
            catch (Exception)
            {
                // No grid: the plain preview still works.
            }

            string path = Path.Combine(runFolder, FileName);
            PngBitmapEncoder encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(picture));
            using (FileStream stream = File.Create(path))
            {
                encoder.Save(stream);
            }

            double factor = (double)imageWidth / bitmap.PixelWidth;
            note = "The image is attached to this message as a "
                + bitmap.PixelWidth.ToString(CultureInfo.InvariantCulture) + "x" + bitmap.PixelHeight.ToString(CultureInfo.InvariantCulture)
                + " preview of the " + imageWidth.ToString(CultureInfo.InvariantCulture) + "x" + imageHeight.ToString(CultureInfo.InvariantCulture)
                + " original (factor " + factor.ToString("0.###", CultureInfo.InvariantCulture)
                + ")." + (grid ? " A coordinate grid is drawn on it: a line every " + GridStep.ToString(CultureInfo.InvariantCulture) + " preview pixels, numbered with the preview-pixel x along the top and bottom and y along the left and right (and 'x,y' at every second crossing); read each position from these numbers (interpolate between lines), do not guess it." : string.Empty) + " Look at it first and do not call get_crop only to see the image. Read every position from the preview in preview pixels and give them to detect_points with scale=" + factor.ToString("0.####", CultureInfo.InvariantCulture) + " (the tool converts to original pixels); any other tool takes original pixels, so multiply preview coordinates by the factor yourself.";
            note += BlobTable(imagePath);
            return path;
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is NotSupportedException || ex is InvalidOperationException || ex is ArgumentException)
        {
            note = string.Empty;
            return null;
        }
    }
}
