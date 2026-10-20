using EasyLabel.Core.Io;

namespace EasyAI.Desktop.Services.Agent;

/// <summary>
/// When the "Remaining images" mode of the agent chat may be used: only after the open image was scanned by the agent (in this session or
/// according to the chat history), or when the open image already has pending suggestions. No I/O, no UI.
/// </summary>
public static class RemainingGateRules
{
    /// <summary>True for a finished run that produced a usable answer: outcome "ok" or "cached" with a proposed count of zero or more.</summary>
    public static bool IsScanSuccess(string? outcome, int proposed)
    {
        string value = (outcome ?? string.Empty).Trim();
        return proposed >= 0
            && (string.Equals(value, "ok", StringComparison.OrdinalIgnoreCase) || string.Equals(value, "cached", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The rel paths of the images that a successful agent entry of the history covers (user entries, errors and cancels count for nothing).</summary>
    public static ISet<string> ScannedRels(IReadOnlyList<AgentChatEntry>? history)
    {
        HashSet<string> rels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (history == null)
        {
            return rels;
        }

        foreach (AgentChatEntry entry in history)
        {
            if (entry == null || !string.Equals(entry.Role, "agent", StringComparison.OrdinalIgnoreCase) || !IsScanSuccess(entry.Outcome, entry.Proposed)
                || entry.Images == null)
            {
                continue;
            }

            foreach (string image in entry.Images)
            {
                if (!string.IsNullOrWhiteSpace(image))
                {
                    rels.Add(image);
                }
            }
        }

        return rels;
    }

    /// <summary>True when <paramref name="rel"/> is in the in-session set or covered by a successful history entry.</summary>
    public static bool IsScanned(string? rel, IEnumerable<string>? sessionScanned, IReadOnlyList<AgentChatEntry>? history)
    {
        if (string.IsNullOrWhiteSpace(rel))
        {
            return false;
        }

        if (sessionScanned != null)
        {
            foreach (string scanned in sessionScanned)
            {
                if (string.Equals(scanned, rel, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return ScannedRels(history).Contains(rel!);
    }

    /// <summary>
    /// True when the Remaining mode is available: the open image (<paramref name="currentRel"/>) was scanned by the agent in this session
    /// (<paramref name="sessionScanned"/>, which /clear does not reset) or in the history, or it has pending suggestions. No open image: false.
    /// </summary>
    public static bool Decide(IEnumerable<string>? sessionScanned, string? currentRel, IReadOnlyList<AgentChatEntry>? history, bool currentHasPendingSuggestions = false)
    {
        if (string.IsNullOrWhiteSpace(currentRel))
        {
            return false;
        }

        return currentHasPendingSuggestions || IsScanned(currentRel, sessionScanned, history);
    }
}
