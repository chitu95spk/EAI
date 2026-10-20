using EasyLabel.Core.Projects;

namespace EasyAI.Desktop.Services.Agent;

/// <summary>
/// The per-project permission to send images and prompts to an agent provider (S9). The question is asked on the UI thread through the
/// caller's delegate, only when the project has no permission for that provider yet. Declining changes nothing and sends nothing. An
/// accepted answer is stored in the project's manifest, written on a worker thread. A project without a manifest is asked again on every
/// use (nothing to store it in).
/// </summary>
public static class AgentConsentGate
{
    /// <summary>The provider key stored in the consent ("claude" or "codex") for a tool.</summary>
    public static string ProviderKey(AgentTool tool)
    {
        return tool == AgentTool.Codex ? "codex" : "claude";
    }

    /// <summary>True when the stored permission covers the provider (no question needed).</summary>
    public static bool IsCovered(ProjectManifest? manifest, string provider)
    {
        return manifest?.AgentConsent?.Covers(provider) == true;
    }

    /// <summary>
    /// True when the agent may run now. <paramref name="ask"/> is called on the calling (UI) thread only when the permission is missing; it
    /// returns true when the user accepted.
    /// </summary>
    public static async Task<bool> EnsureAsync(ProjectManifest? manifest, string provider, Func<bool> ask, DateTime utcNow)
    {
        if (IsCovered(manifest, provider))
        {
            return true;
        }

        if (!ask())
        {
            return false;
        }

        if (manifest == null)
        {
            return true;
        }

        // Keep the providers accepted for the current text; a stale or withdrawn permission starts from nothing.
        ProjectAgentConsent? current = manifest.AgentConsent;
        IEnumerable<string> previous = current != null && current.Granted && current.Version == ProjectAgentConsent.CurrentVersion
            ? current.Tools
            : Array.Empty<string>();
        manifest.AgentConsent = ProjectAgentConsent.Grant(previous.Append(provider), utcNow);
        await SaveAsync(manifest).ConfigureAwait(true);
        return true;
    }

    /// <summary>Withdraws the permission of the project (every provider). The manifest is written on a worker thread.</summary>
    public static async Task RevokeAsync(ProjectManifest? manifest)
    {
        if (manifest == null || manifest.AgentConsent == null)
        {
            return;
        }

        manifest.AgentConsent = null;
        await SaveAsync(manifest).ConfigureAwait(true);
    }

    // A write that fails leaves the permission in memory for this session; the next session asks again.
    private static async Task SaveAsync(ProjectManifest manifest)
    {
        try
        {
            await Task.Run(() => ProjectManifestStore.Write(manifest)).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
        }
    }
}
