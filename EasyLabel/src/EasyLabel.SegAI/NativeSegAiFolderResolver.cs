namespace EasyLabel.SegAI.Native;

public sealed record NativeSegAiResolution(
    string? Folder,
    IReadOnlyList<string> ExtraFolders,
    IReadOnlyList<string> Probed);

public static class NativeSegAiFolderResolver
{
    public const string DllName = "BeeNativeSegAI.dll";
    public const string EnvironmentVariable = "EASYAI_SEGAI_DIR";

    public static NativeSegAiResolution Resolve(NativeSegAiOptions options)
    {
        var probed = new List<string>();
        var extras = new List<string>();

        bool Probe(string? folder)
        {
            if (string.IsNullOrWhiteSpace(folder))
            {
                return false;
            }

            string full;
            try
            {
                full = Path.GetFullPath(folder);
            }
            catch (Exception)
            {
                return false;
            }

            if (probed.Contains(full, StringComparer.OrdinalIgnoreCase))
            {
                return false;
            }

            probed.Add(full);
            return File.Exists(Path.Combine(full, DllName));
        }

        if (Probe(options.PreferredFolder))
        {
            return new NativeSegAiResolution(probed[^1], extras, probed);
        }

        if (!options.SearchDefaultLocations)
        {
            return new NativeSegAiResolution(null, extras, probed);
        }

        if (Probe(Environment.GetEnvironmentVariable(EnvironmentVariable)))
        {
            return new NativeSegAiResolution(probed[^1], extras, probed);
        }

        if (Probe(AppContext.BaseDirectory))
        {
            return new NativeSegAiResolution(probed[^1], extras, probed);
        }

        string? dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && !string.IsNullOrEmpty(dir); i++)
        {
            dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.IsNullOrEmpty(dir))
            {
                break;
            }

            if (Probe(Path.Combine(dir, "bin", "Release")))
            {
                string torch = Path.Combine(dir, "BeeNativeSegAI", "x64", "Release");
                if (Directory.Exists(torch))
                {
                    extras.Add(torch);
                }

                return new NativeSegAiResolution(probed[^1], extras, probed);
            }
        }

        return new NativeSegAiResolution(null, extras, probed);
    }
}
