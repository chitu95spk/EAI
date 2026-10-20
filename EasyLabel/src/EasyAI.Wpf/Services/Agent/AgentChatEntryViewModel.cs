using System.Globalization;
using EasyLabel.Core.Io;

namespace EasyAI.Desktop.Services.Agent;

/// <summary>
/// The user-visible words of the chat history. The defaults are English so the formatting is testable without resources;
/// <see cref="AgentChatLabelsFactory.FromStrings"/> fills them from the localized resources.
/// </summary>
public sealed class AgentChatLabels
{
    public string ModeAll { get; init; } = "All images";

    public string ModeCurrent { get; init; } = "Current image";

    public string ModeRemaining { get; init; } = "Remaining images";

    public string EffortEconomy { get; init; } = "Low";

    public string EffortNormal { get; init; } = "Normal";

    public string EffortBest { get; init; } = "High";

    public string RoleUser { get; init; } = "You";

    public string RoleAgent { get; init; } = "Agent";

    public string RoleSystem { get; init; } = "Review";

    /// <summary>Format with {0} proposed, {1} accepted, {2} rejected.</summary>
    public string SummaryFormat { get; init; } = "{0} proposed · {1} accepted · {2} rejected";

    public string OutcomeOk { get; init; } = "Done";

    public string OutcomeCancelled { get; init; } = "Cancelled";

    public string OutcomeCached { get; init; } = "From cache";

    public string OutcomeError { get; init; } = "Error";

    /// <summary>Format with {0} = the short error text.</summary>
    public string OutcomeErrorFormat { get; init; } = "Error: {0}";
}

/// <summary>
/// One row of the chat window (S9 binds it): a plain object holding an <see cref="AgentChatEntry"/> and its display strings. It uses no WPF type.
/// Unknown role / mode / effort / outcome values are shown as stored.
/// </summary>
public sealed class AgentChatEntryViewModel : System.ComponentModel.INotifyPropertyChanged
{
    private string _liveText = string.Empty;

    public AgentChatEntryViewModel(AgentChatEntry entry, AgentChatLabels? labels = null, TimeZoneInfo? zone = null)
    {
        Entry = entry ?? throw new ArgumentNullException(nameof(entry));
        AgentChatLabels words = labels ?? new AgentChatLabels();

        DateTime utc = DateTime.SpecifyKind(entry.Time, DateTimeKind.Utc);
        DateTime shown = TimeZoneInfo.ConvertTimeFromUtc(utc, zone ?? TimeZoneInfo.Local);
        TimeText = shown.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

        IsUser = string.Equals(entry.Role, "user", StringComparison.OrdinalIgnoreCase);
        IsAgent = string.Equals(entry.Role, "agent", StringComparison.OrdinalIgnoreCase);
        RoleText = IsUser ? words.RoleUser : IsAgent ? words.RoleAgent : string.Equals(entry.Role, "system", StringComparison.OrdinalIgnoreCase) ? words.RoleSystem : entry.Role;

        string mode = string.Equals(entry.Mode, "all", StringComparison.OrdinalIgnoreCase) ? words.ModeAll
            : string.Equals(entry.Mode, "current", StringComparison.OrdinalIgnoreCase) ? words.ModeCurrent
            : string.Equals(entry.Mode, "remaining", StringComparison.OrdinalIgnoreCase) ? words.ModeRemaining
            : entry.Mode;
        string effort = string.Equals(entry.Effort, "economy", StringComparison.OrdinalIgnoreCase) || string.Equals(entry.Effort, "low", StringComparison.OrdinalIgnoreCase) ? words.EffortEconomy
            : string.Equals(entry.Effort, "normal", StringComparison.OrdinalIgnoreCase) ? words.EffortNormal
            : string.Equals(entry.Effort, "best", StringComparison.OrdinalIgnoreCase) || string.Equals(entry.Effort, "high", StringComparison.OrdinalIgnoreCase) ? words.EffortBest
            : entry.Effort;
        HeaderText = mode.Length > 0 && effort.Length > 0 ? mode + " · " + effort : mode.Length > 0 ? mode : effort;

        SummaryText = string.Format(CultureInfo.InvariantCulture, words.SummaryFormat, entry.Proposed, entry.Accepted, entry.Rejected);
        IsNotice = string.Equals(entry.Role, NoticeRole, StringComparison.OrdinalIgnoreCase);
        IsLive = string.Equals(entry.Role, LiveRole, StringComparison.OrdinalIgnoreCase);
        HasSummary = !IsUser && !IsNotice && !IsLive;
        AgentText = IsAgent ? (entry.Prompt ?? string.Empty) : string.Empty;

        CostText = entry.Cost.HasValue ? "$" + FormatCost(entry.Cost.Value) : string.Empty;
        TurnsText = entry.Turns.HasValue ? entry.Turns.Value.ToString(CultureInfo.InvariantCulture) : string.Empty;

        string error = entry.Error ?? string.Empty;
        string outcome = (entry.Outcome ?? string.Empty).Trim();
        if (outcome.Length == 0)
        {
            OutcomeText = string.Empty;
        }
        else if (string.Equals(outcome, "ok", StringComparison.OrdinalIgnoreCase))
        {
            OutcomeText = words.OutcomeOk;
        }
        else if (string.Equals(outcome, "cancelled", StringComparison.OrdinalIgnoreCase))
        {
            OutcomeText = words.OutcomeCancelled;
        }
        else if (string.Equals(outcome, "cached", StringComparison.OrdinalIgnoreCase))
        {
            OutcomeText = words.OutcomeCached;
        }
        else if (string.Equals(outcome, "error", StringComparison.OrdinalIgnoreCase))
        {
            OutcomeText = error.Length > 0 ? string.Format(CultureInfo.InvariantCulture, words.OutcomeErrorFormat, error) : words.OutcomeError;
        }
        else
        {
            OutcomeText = outcome;
        }

        ImagesText = string.Join(", ", entry.Images ?? Array.Empty<string>());
        ImageLinks = (entry.Images ?? Array.Empty<string>()).Where(i => !string.IsNullOrWhiteSpace(i)).ToList();
    }

    /// <summary>The role of a chat-only system line (the result of a slash command): shown in the chat view, never written to the history file.</summary>
    public const string NoticeRole = "notice";

    /// <summary>A system line for the chat view (the answer of a slash command). <paramref name="roleText"/> is the localized caption.</summary>
    public static AgentChatEntryViewModel CreateNotice(string text, string roleText, DateTime? utcNow = null, TimeZoneInfo? zone = null)
    {
        AgentChatEntry entry = new AgentChatEntry(
            utcNow ?? DateTime.UtcNow, NoticeRole, string.Empty, string.Empty, text ?? string.Empty, Array.Empty<string>(), 0, 0, 0, null, null, string.Empty, string.Empty);
        AgentChatEntryViewModel row = new AgentChatEntryViewModel(entry, null, zone);
        row.RoleText = roleText ?? string.Empty;
        return row;
    }

    /// <summary>The role of the live row of a running agent: shown in the chat view only (never written to the history file); replaced by the final agent entry.</summary>
    public const string LiveRole = "live";

    /// <summary>The growing row of a running agent. <paramref name="roleText"/> is the localized caption.</summary>
    public static AgentChatEntryViewModel CreateLive(string roleText, DateTime? utcNow = null, TimeZoneInfo? zone = null)
    {
        AgentChatEntry entry = new AgentChatEntry(
            utcNow ?? DateTime.UtcNow, LiveRole, string.Empty, string.Empty, string.Empty, Array.Empty<string>(), 0, 0, 0, null, null, string.Empty, string.Empty);
        AgentChatEntryViewModel row = new AgentChatEntryViewModel(entry, null, zone);
        row.RoleText = roleText ?? string.Empty;
        return row;
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    /// <summary>The row is the live transcript of a running agent (shows <see cref="LiveText"/>, no summary / outcome).</summary>
    public bool IsLive { get; }

    /// <summary>The live lines joined by new lines (UI thread only; raises PropertyChanged).</summary>
    public string LiveText
    {
        get => _liveText;
        set
        {
            string text = value ?? string.Empty;
            if (!string.Equals(_liveText, text, StringComparison.Ordinal))
            {
                _liveText = text;
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(LiveText)));
            }
        }
    }

    /// <summary>The closing text the agent wrote (stored in the entry's text field, at most 500 characters); empty on other rows.</summary>
    public string AgentText { get; }

    public bool HasAgentText => AgentText.Length > 0;

    public AgentChatEntry Entry { get; }

    /// <summary>The prompt; a click in the chat window puts it back into the prompt box.</summary>
    public string Prompt => Entry.Prompt;

    public bool IsUser { get; }

    public bool IsAgent { get; }

    /// <summary>A chat-only system line (shows its text, no summary / outcome).</summary>
    public bool IsNotice { get; }

    public string RoleText { get; private set; }

    /// <summary>Local time, "yyyy-MM-dd HH:mm".</summary>
    public string TimeText { get; }

    /// <summary>"mode · effort" (one of them when the other is empty).</summary>
    public string HeaderText { get; }

    /// <summary>"N proposed · N accepted · N rejected" (shown only when <see cref="HasSummary"/>).</summary>
    public string SummaryText { get; }

    /// <summary>False for a user entry (nothing is proposed yet).</summary>
    public bool HasSummary { get; }

    /// <summary>"$0.12", or empty when the cost is unknown.</summary>
    public string CostText { get; }

    /// <summary>Number of agent turns, empty when unknown.</summary>
    public string TurnsText { get; }

    /// <summary>Localized outcome (with the short error text for an error); empty on a user entry.</summary>
    public string OutcomeText { get; }

    /// <summary>The images of the run, comma separated.</summary>
    public string ImagesText { get; }

    /// <summary>The same images one by one: each is a link that opens the image so its proposals can be reviewed.</summary>
    public IReadOnlyList<string> ImageLinks { get; }

    private static string FormatCost(decimal cost)
    {
        string text = cost.ToString("0.00", CultureInfo.InvariantCulture);
        return cost > 0 && text == "0.00" ? cost.ToString("0.000", CultureInfo.InvariantCulture) : text;
    }
}
