namespace EasyAI.Desktop.Services.Agent;

/// <summary>
/// The slice of the machine the agent code looks at (environment variables, files, folders). A small interface so the Claude Code search and
/// the agent-tool search are unit tested with a fake file system.
/// </summary>
public interface IAgentEnvironment
{
    /// <summary>Value of an environment variable, or null when it is not set.</summary>
    string? GetVariable(string name);

    /// <summary>The user profile folder (<c>%USERPROFILE%</c>), or an empty string when unknown.</summary>
    string UserProfile { get; }

    bool FileExists(string path);

    /// <summary>Full paths of the sub-folders of <paramref name="parent"/> whose name matches the pattern (empty when the folder is missing).</summary>
    IReadOnlyList<string> GetDirectories(string parent, string searchPattern);

    /// <summary>Full paths of the files of <paramref name="folder"/> matching the pattern (empty when the folder is missing).</summary>
    IReadOnlyList<string> GetFiles(string folder, string searchPattern);

    string ReadAllText(string path);
}

/// <summary>The real machine.</summary>
public sealed class SystemAgentEnvironment : IAgentEnvironment
{
    public string? GetVariable(string name)
    {
        return Environment.GetEnvironmentVariable(name);
    }

    public string UserProfile => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public bool FileExists(string path)
    {
        try
        {
            return File.Exists(path);
        }
        catch (Exception)
        {
            return false;
        }
    }

    public IReadOnlyList<string> GetDirectories(string parent, string searchPattern)
    {
        try
        {
            return Directory.Exists(parent) ? Directory.GetDirectories(parent, searchPattern) : Array.Empty<string>();
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }

    public IReadOnlyList<string> GetFiles(string folder, string searchPattern)
    {
        try
        {
            return Directory.Exists(folder) ? Directory.GetFiles(folder, searchPattern) : Array.Empty<string>();
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }

    public string ReadAllText(string path)
    {
        return File.ReadAllText(path);
    }
}
