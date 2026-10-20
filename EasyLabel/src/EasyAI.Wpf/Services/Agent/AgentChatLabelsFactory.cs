using EasyAI.Desktop.Resources;

namespace EasyAI.Desktop.Services.Agent;

/// <summary>Builds the chat labels from the localized resources (kept apart from <see cref="AgentChatEntryViewModel"/> so that file needs no resources).</summary>
public static class AgentChatLabelsFactory
{
    public static AgentChatLabels FromStrings()
    {
        return new AgentChatLabels
        {
            ModeAll = Strings.ChatModeAll,
            ModeCurrent = Strings.ChatModeCurrent,
            ModeRemaining = Strings.ChatModeRemaining,
            EffortEconomy = Strings.ChatEffortEconomy,
            EffortNormal = Strings.ChatEffortNormal,
            EffortBest = Strings.ChatEffortBest,
            RoleUser = Strings.ChatRoleUser,
            RoleAgent = Strings.ChatRoleAgent,
            RoleSystem = Strings.ChatRoleSystem,
            SummaryFormat = Strings.ChatSummaryFormat,
            OutcomeOk = Strings.ChatOutcomeOk,
            OutcomeCancelled = Strings.ChatOutcomeCancelled,
            OutcomeCached = Strings.ChatOutcomeCached,
            OutcomeError = Strings.ChatOutcomeError,
            OutcomeErrorFormat = Strings.ChatOutcomeErrorFormat,
        };
    }
}
