namespace EasyLabel.SegAI.Training.Ultralytics;

/// <summary>Whether the Ultralytics trainer folder and its Python environment are usable.</summary>
public enum UltraTrainerStatus
{
    Ready,
    NoTrainerFolder,
    NoVenv,
}

/// <summary>Where the trainer is. <see cref="MessageKey"/> is a localisation key for a non-Ready status (empty when Ready).</summary>
public sealed record UltraTrainerLocation(
    UltraTrainerStatus Status,
    string TrainerDirectory,
    string ScriptPath,
    string PythonPath,
    string MessageKey);

/// <summary>Finds <c>tools\easyai-train</c> (train_ultra.py) and its <c>.venv\Scripts\python.exe</c>.</summary>
public sealed class UltraTrainerLocator
{
    public const string EnvironmentVariable = "EASYAI_ULTRA_DIR";

    public const string ScriptName = "train_ultra.py";

    public const string MessageNoTrainerFolder = "train.ultra.noTrainerFolder";

    public const string MessageNoVenv = "train.ultra.noVenv";

    private const int MaxAncestors = 8;

    private readonly Func<string, bool> _fileExists;
    private readonly Func<string, bool> _directoryExists;
    private readonly Func<string, string?> _getEnvironment;
    private readonly string _baseDirectory;

    /// <param name="fileExists">File existence check (a fake in tests); null = <see cref="File.Exists(string?)"/>.</param>
    /// <param name="getEnvironment">Environment variable reader (a fake in tests); null = the process environment.</param>
    /// <param name="baseDirectory">Start of the upward probe; null = <see cref="AppContext.BaseDirectory"/>.</param>
    /// <param name="directoryExists">Directory existence check (a fake in tests); null = <see cref="Directory.Exists(string?)"/>.</param>
    public UltraTrainerLocator(Func<string, bool>? fileExists = null, Func<string, string?>? getEnvironment = null, string? baseDirectory = null, Func<string, bool>? directoryExists = null)
    {
        _fileExists = fileExists ?? File.Exists;
        _directoryExists = directoryExists ?? Directory.Exists;
        _getEnvironment = getEnvironment ?? Environment.GetEnvironmentVariable;
        _baseDirectory = string.IsNullOrWhiteSpace(baseDirectory) ? AppContext.BaseDirectory : baseDirectory;
    }

    private bool HasRepoMarker(string directory)
    {
        return _fileExists(Path.Combine(directory, ".git"))
            || _directoryExists(Path.Combine(directory, ".git"))
            || _fileExists(Path.Combine(directory, "EasyLabel.sln"))
            || _directoryExists(Path.Combine(directory, "EasyLabel"));
    }

    /// <summary>Probes the environment variable first, then the base folder, then up to 8 ancestors (an ancestor only when it carries a repo marker).</summary>
    public UltraTrainerLocation Locate()
    {
        string? trainerDir = null;
        string? fromEnv = _getEnvironment(EnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(fromEnv) && _fileExists(Path.Combine(fromEnv.Trim(), ScriptName)))
        {
            trainerDir = fromEnv.Trim();
        }

        string? current = _baseDirectory;
        for (int i = 0; trainerDir == null && i <= MaxAncestors && !string.IsNullOrEmpty(current); i++)
        {
            string candidate = Path.Combine(current, "tools", "easyai-train");
            if (_fileExists(Path.Combine(candidate, ScriptName)) && (i == 0 || HasRepoMarker(current)))
            {
                trainerDir = candidate;
            }

            current = Path.GetDirectoryName(current.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        }

        if (trainerDir == null)
        {
            return new UltraTrainerLocation(UltraTrainerStatus.NoTrainerFolder, string.Empty, string.Empty, string.Empty, MessageNoTrainerFolder);
        }

        string script = Path.Combine(trainerDir, ScriptName);
        string python = Path.Combine(trainerDir, ".venv", "Scripts", "python.exe");
        if (!_fileExists(python))
        {
            return new UltraTrainerLocation(UltraTrainerStatus.NoVenv, trainerDir, script, python, MessageNoVenv);
        }

        return new UltraTrainerLocation(UltraTrainerStatus.Ready, trainerDir, script, python, string.Empty);
    }
}
