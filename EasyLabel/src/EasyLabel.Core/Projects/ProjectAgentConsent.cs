using System.Linq;

namespace EasyLabel.Core.Projects;

/// <summary>
/// The user's consent for one project to send its images and prompts to an AI agent provider (the <c>agentConsent</c> key of the manifest).
/// Absent = never asked. Revoking removes the key again, so a revoked project is the same as a new one.
/// </summary>
public sealed class ProjectAgentConsent
{
    /// <summary>Version of the consent text the user saw; a later version of the text asks again.</summary>
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;

    /// <summary>True when the user accepted. A stored false is not written by the app (revoke removes the key).</summary>
    public bool Granted { get; set; }

    /// <summary>When the user answered (UTC).</summary>
    public DateTime Utc { get; set; }

    /// <summary>The provider names the user accepted (for example "claude", "codex").</summary>
    public string[] Tools { get; set; } = Array.Empty<string>();

    /// <summary>A consent given now for the given providers.</summary>
    public static ProjectAgentConsent Grant(IEnumerable<string> tools, DateTime utcNow)
    {
        return new ProjectAgentConsent
        {
            Version = CurrentVersion,
            Granted = true,
            Utc = DateTime.SpecifyKind(utcNow, DateTimeKind.Utc),
            Tools = tools.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim()).Distinct(StringComparer.Ordinal).ToArray(),
        };
    }

    /// <summary>True when this consent was given for the current consent text and covers the provider.</summary>
    public bool Covers(string tool)
    {
        return Granted && Version >= CurrentVersion && Tools.Contains(tool, StringComparer.Ordinal);
    }
}
