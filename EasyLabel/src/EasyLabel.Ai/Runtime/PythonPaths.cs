namespace EasyLabel.Ai.Runtime;

public sealed class PythonPaths
{
    private PythonPaths(string appBaseDirectory, string pythonExePath, string scriptsDirectory, string requirementsPath, string wheelsDirectory, string logDirectory)
    {
        AppBaseDirectory = appBaseDirectory;
        PythonExePath = pythonExePath;
        ScriptsDirectory = scriptsDirectory;
        RequirementsPath = requirementsPath;
        WheelsDirectory = wheelsDirectory;
        LogDirectory = logDirectory;
    }

    public string AppBaseDirectory { get; }

    public string PythonExePath { get; }

    public string ScriptsDirectory { get; }

    public string RequirementsPath { get; }

    public string WheelsDirectory { get; }

    public string LogDirectory { get; }

    public static PythonPaths Resolve(string? appBaseDirectory = null)
    {
        string baseDirectory = Path.GetFullPath(appBaseDirectory ?? AppContext.BaseDirectory);
        string root = FindEasyLabelRoot(baseDirectory) ?? baseDirectory;
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string logDirectory = Path.Combine(appData, "EasyLabel", "logs");

        return new PythonPaths(
            baseDirectory,
            Path.Combine(root, "runtime", "python", "python.exe"),
            Path.Combine(root, "python"),
            Path.Combine(root, "python", "requirements.lock"),
            Path.Combine(root, "Lib"),
            logDirectory);
    }

    private static string? FindEasyLabelRoot(string startDirectory)
    {
        DirectoryInfo? directory = new(startDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "runtime", "python"))
                && Directory.Exists(Path.Combine(directory.FullName, "Lib"))
                && File.Exists(Path.Combine(directory.FullName, "python", "requirements.lock")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return null;
    }
}
