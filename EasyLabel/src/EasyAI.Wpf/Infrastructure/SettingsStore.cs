using System.Text.Json;
using EasyLabel.Core.Io;

namespace EasyAI.Desktop.Infrastructure;

/// <summary>settings.json + layout.json under the settings directory. Corrupt or missing files give defaults.</summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly object _gate = new object();

    private readonly bool _persistent = true;

    public SettingsStore(string directory)
    {
        Directory = directory;
    }

    private SettingsStore()
    {
        Directory = string.Empty;
        _persistent = false;
    }

    /// <summary>Store that never touches the disk; used when no settings directory is usable.</summary>
    public static SettingsStore CreateInMemory() => new SettingsStore();

    public string Directory { get; }

    public string SettingsPath => System.IO.Path.Combine(Directory, "settings.json");

    public string LayoutPath => System.IO.Path.Combine(Directory, "layout.json");

    public string LogPath => System.IO.Path.Combine(Directory, "error.log");

    /// <summary>True when settings.json exists (read before <see cref="LoadSettings"/>; a first start has none). Never true for the in-memory store.</summary>
    public bool SettingsFileExists => _persistent && File.Exists(SettingsPath);

    public static string ResolveDirectory(StartupOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.SettingsDir))
        {
            return System.IO.Path.GetFullPath(options.SettingsDir);
        }

        string? env = Environment.GetEnvironmentVariable("EASYAI_SETTINGS_DIR");
        if (!string.IsNullOrWhiteSpace(env))
        {
            return System.IO.Path.GetFullPath(env);
        }

        return System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "EasyAI");
    }

    public AppSettings LoadSettings() => Load<AppSettings>(SettingsPath) ?? new AppSettings();

    public LayoutSettings LoadLayout() => Load<LayoutSettings>(LayoutPath) ?? LayoutSettings.CreateDefault();

    public bool SaveSettings(AppSettings settings) => Save(SettingsPath, settings);

    public bool SaveLayout(LayoutSettings layout) => Save(LayoutPath, layout);

    public void Log(string text)
    {
        if (!_persistent)
        {
            return;
        }

        try
        {
            lock (_gate)
            {
                System.IO.Directory.CreateDirectory(Directory);
                File.AppendAllText(LogPath, DateTime.Now.ToString("O") + " " + text + Environment.NewLine);
            }
        }
        catch (Exception)
        {
            // Logging must never throw.
        }
    }

    private T? Load<T>(string path) where T : class
    {
        if (!_persistent)
        {
            return null;
        }

        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonOptions);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private bool Save<T>(string path, T value)
    {
        if (!_persistent)
        {
            return true;
        }

        try
        {
            lock (_gate)
            {
                System.IO.Directory.CreateDirectory(Directory);
                AtomicFile.WriteAllText(path, JsonSerializer.Serialize(value, JsonOptions));
            }

            return true;
        }
        catch (Exception ex)
        {
            Log("Save failed: " + path + " " + ex.Message);
            return false;
        }
    }
}
