using EasyLabel.Core.Model;
using Newtonsoft.Json;

namespace EasyLabel.Core.Io;

public sealed class LabelMeIO
{
    private static readonly JsonSerializerSettings Settings = new()
    {
        Formatting = Formatting.Indented,
        NullValueHandling = NullValueHandling.Include
    };

    public LabelMeDocument Read(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("LabelMe path is required.", nameof(path));
        }

        string json = File.ReadAllText(path);
        LabelMeDocument? document = JsonConvert.DeserializeObject<LabelMeDocument>(json, Settings);
        return document ?? throw new JsonSerializationException($"Could not read LabelMe document: {path}");
    }

    public void Write(LabelMeDocument document, string path)
    {
        Write(document, path, null);
    }

    /// <summary>
    /// Writes the document and keeps an <c>imageData</c> that the file already carried (re-encoded from <paramref name="imagePath"/>),
    /// so an edit that only touches flags never strips the embedded image.
    /// </summary>
    public void WriteKeepingImageData(LabelMeDocument document, string path, string imagePath)
    {
        if (document is null)
        {
            throw new ArgumentNullException(nameof(document));
        }

        bool embed = !string.IsNullOrEmpty(document.ImageData) && !string.IsNullOrEmpty(imagePath) && File.Exists(imagePath);
        Write(document, path, embed ? imagePath : null);
    }

    /// <summary>
    /// Writes the document. With <paramref name="embedImagePath"/> the image file is stored base64-encoded in <c>imageData</c>
    /// (the LabelMe "save with image data" option); without it <c>imageData</c> is null. The in-memory document never keeps the data.
    /// </summary>
    public void Write(LabelMeDocument document, string path, string? embedImagePath)
    {
        if (document is null)
        {
            throw new ArgumentNullException(nameof(document));
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("LabelMe path is required.", nameof(path));
        }

        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string json;
        if (string.IsNullOrEmpty(embedImagePath))
        {
            document.ImageData = null;
            json = JsonConvert.SerializeObject(document, Settings);
        }
        else
        {
            string encoded = System.Convert.ToBase64String(File.ReadAllBytes(embedImagePath));
            document.ImageData = encoded;
            try
            {
                json = JsonConvert.SerializeObject(document, Settings);
            }
            finally
            {
                document.ImageData = null;
            }
        }

        AtomicFile.WriteAllText(path, json);
    }
}
