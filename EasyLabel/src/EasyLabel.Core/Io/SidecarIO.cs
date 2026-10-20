using EasyLabel.Core.Model;
using Newtonsoft.Json;

namespace EasyLabel.Core.Io;

public sealed class SidecarIO
{
    private static readonly JsonSerializerSettings Settings = new()
    {
        Formatting = Formatting.Indented,
        NullValueHandling = NullValueHandling.Include
    };

    public EasyLabelSidecar Read(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Sidecar path is required.", nameof(path));
        }

        if (!File.Exists(path))
        {
            return new EasyLabelSidecar();
        }

        string json = File.ReadAllText(path);
        EasyLabelSidecar? sidecar = JsonConvert.DeserializeObject<EasyLabelSidecar>(json, Settings);
        return sidecar ?? new EasyLabelSidecar();
    }

    public void Write(EasyLabelSidecar sidecar, string path)
    {
        if (sidecar is null)
        {
            throw new ArgumentNullException(nameof(sidecar));
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Sidecar path is required.", nameof(path));
        }

        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string json = JsonConvert.SerializeObject(sidecar, Settings);
        AtomicFile.WriteAllText(path, json);
    }
}
