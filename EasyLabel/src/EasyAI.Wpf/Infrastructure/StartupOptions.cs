using EasyAI.Desktop.Resources;

namespace EasyAI.Desktop.Infrastructure;

/// <summary>Command line: --settings-dir &lt;dir&gt;, --culture en|vi, --project &lt;folder or .easyai file&gt;.</summary>
public sealed class StartupOptions
{
    public string? SettingsDir { get; private set; }

    public string? Culture { get; private set; }

    public string? ProjectFolder { get; private set; }

    /// <summary>A <c>--project &lt;Name&gt;.easyai</c> project file (opened instead of a folder).</summary>
    public string? ProjectFile { get; private set; }

    /// <summary>Hidden test flag <c>--fake-camera</c>: the camera service uses the simulated camera instead of the MVS runtime.</summary>
    public bool FakeCamera { get; private set; }

    /// <summary>Hidden test flag <c>--fake-agent &lt;script.ps1&gt;</c>: agent runs start this PowerShell script instead of claude/codex (see <c>AgentRunner.FakeAgentScript</c>).</summary>
    public string? FakeAgent { get; private set; }

    /// <summary>Hidden test flag <c>--selflearn-signal &lt;file&gt;</c>: when that file appears the app deletes it and opens the Self-learn window (for UI automation where popup menus do not open).</summary>
    public string? SelfLearnSignal { get; private set; }

    /// <summary>Startup problems to show once in the main window.</summary>
    public List<string> Warnings { get; } = new List<string>();

    public static StartupOptions Parse(string[] args)
    {
        StartupOptions options = new();
        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            bool hasValue = i + 1 < args.Length;
            if (string.Equals(arg, "--settings-dir", StringComparison.OrdinalIgnoreCase) && hasValue)
            {
                string dir = args[++i].Trim().Trim('"').Trim();
                if (IsUsableRooted(dir))
                {
                    options.SettingsDir = dir;
                }
                else
                {
                    options.Warnings.Add(string.Format(Strings.StartupSettingsDirIgnoredFormat, dir));
                }
            }
            else if (string.Equals(arg, "--fake-camera", StringComparison.OrdinalIgnoreCase))
            {
                options.FakeCamera = true;
            }
            else if (string.Equals(arg, "--fake-agent", StringComparison.OrdinalIgnoreCase) && hasValue)
            {
                string script = args[++i].Trim().Trim('"').Trim();
                if (script.Length > 0)
                {
                    options.FakeAgent = script;
                }
            }
            else if (string.Equals(arg, "--selflearn-signal", StringComparison.OrdinalIgnoreCase) && hasValue)
            {
                string signal = args[++i].Trim().Trim('"').Trim();
                if (IsUsableRooted(signal))
                {
                    options.SelfLearnSignal = signal;
                }
            }
            else if (string.Equals(arg, "--culture", StringComparison.OrdinalIgnoreCase) && hasValue)
            {
                string value = args[++i].Trim();
                if (string.Equals(value, "en", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(value, "vi", StringComparison.OrdinalIgnoreCase))
                {
                    options.Culture = value.ToLowerInvariant();
                }
            }
            else if (string.Equals(arg, "--project", StringComparison.OrdinalIgnoreCase) && hasValue)
            {
                string project = args[++i].Trim().Trim('"').Trim();
                if (Directory.Exists(project))
                {
                    options.ProjectFolder = project;
                }
                else if (File.Exists(project) && project.EndsWith(EasyLabel.Core.Projects.ProjectManifest.FileExtension, StringComparison.OrdinalIgnoreCase))
                {
                    options.ProjectFile = project;
                }
                else
                {
                    options.Warnings.Add(string.Format(Strings.StartupProjectIgnoredFormat, project));
                }
            }
        }

        return options;
    }

    private static bool IsUsableRooted(string path)
    {
        try
        {
            return path.Length > 0 && Path.IsPathRooted(path) && Path.GetFullPath(path).Length > 0;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
