using System.Text.RegularExpressions;
using EasyAI.Desktop.Services.Agent;

namespace EasyLabel.SegAI.Tests;

/// <summary>An in-memory machine: a set of files (with optional text), environment variables and a user profile.</summary>
internal sealed class FakeAgentEnvironment : IAgentEnvironment
{
    public Dictionary<string, string> Variables { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public HashSet<string> Files { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, string> Texts { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public string UserProfile { get; set; } = @"C:\Users\tester";

    public string? GetVariable(string name)
    {
        return Variables.TryGetValue(name, out string? value) ? value : null;
    }

    public bool FileExists(string path)
    {
        return Files.Contains(path);
    }

    public IReadOnlyList<string> GetDirectories(string parent, string searchPattern)
    {
        string prefix = parent.TrimEnd('\\') + "\\";
        HashSet<string> found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string file in Files)
        {
            if (!file.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string rest = file.Substring(prefix.Length);
            int slash = rest.IndexOf('\\');
            if (slash > 0 && Matches(rest.Substring(0, slash), searchPattern))
            {
                found.Add(prefix + rest.Substring(0, slash));
            }
        }

        return found.ToList();
    }

    public IReadOnlyList<string> GetFiles(string folder, string searchPattern)
    {
        string prefix = folder.TrimEnd('\\') + "\\";
        return Files
            .Where(f => f.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                && f.IndexOf('\\', prefix.Length) < 0
                && Matches(f.Substring(prefix.Length), searchPattern))
            .ToList();
    }

    public string ReadAllText(string path)
    {
        return Texts[path];
    }

    public void AddFile(string path, string? text = null)
    {
        Files.Add(path);
        if (text != null)
        {
            Texts[path] = text;
        }
    }

    private static bool Matches(string name, string pattern)
    {
        string regex = "^" + Regex.Escape(pattern).Replace("\\*", ".*") + "$";
        return Regex.IsMatch(name, regex, RegexOptions.IgnoreCase);
    }
}

/// <summary>A scratch folder below the test output folder (inside the project tree, never %TEMP%); deleted on dispose.</summary>
internal sealed class AgentScratch : IDisposable
{
    public AgentScratch(string name)
    {
        Path = System.IO.Path.Combine(AppContext.BaseDirectory, "agent-scratch", name + "-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Combine(params string[] parts)
    {
        return System.IO.Path.Combine(new[] { Path }.Concat(parts).ToArray());
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
