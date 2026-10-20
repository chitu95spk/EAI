using System;
using EasyLabel.Commands.Discovery;

namespace EasyLabel.Commands
{
    /// <summary>Shared slash metadata; AGT.CHECK remains on the existing CC-2 handler.</summary>
    public abstract class AgentSlashCommandDescriptors
    {
        private readonly Func<string, ICommandEndpoint> _bind;

        protected AgentSlashCommandDescriptors(Func<string, ICommandEndpoint> bind)
        {
            _bind = bind ?? throw new ArgumentNullException(nameof(bind));
        }

        [Command("AGT.CLEAR", Risk = CommandRisk.R2, Aliases = new[] { "clear" })]
        public ICommandEndpoint ClearChat { get { return _bind("AGT.CLEAR"); } }

        [Command("AGT.SUGGEST", Risk = CommandRisk.R3, Cost = CommandCost.C3, Aliases = new[] { "suggest" })]
        public ICommandEndpoint SlashSuggest { get { return _bind("AGT.SUGGEST"); } }

        [Command("AGT.CANCEL", Risk = CommandRisk.R1, Aliases = new[] { "cancel" })]
        public ICommandEndpoint SlashCancel { get { return _bind("AGT.CANCEL"); } }

        [Command("AGT.MODEL.SET", Risk = CommandRisk.R1, Aliases = new[] { "model" },
            Args = "{\"type\":\"object\",\"properties\":{\"name\":{\"type\":\"string\",\"default\":\"\"}},\"additionalProperties\":false}")]
        public ICommandEndpoint SlashModel { get { return _bind("AGT.MODEL.SET"); } }

        // Keep the free-form argument: the VM supplies the existing localized invalid-mode notice.
        [Command("AGT.MODE.SET", Risk = CommandRisk.R1, Aliases = new[] { "mode" },
            Args = "{\"type\":\"object\",\"properties\":{\"mode\":{\"type\":\"string\",\"default\":\"\"}},\"additionalProperties\":false}")]
        public ICommandEndpoint SlashMode { get { return _bind("AGT.MODE.SET"); } }

        [Command("AGT.FORGET", Risk = CommandRisk.R2, Aliases = new[] { "forget" })]
        public ICommandEndpoint ForgetVocabulary { get { return _bind("AGT.FORGET"); } }

        [Command("AGT.HELP", Risk = CommandRisk.R0, Aliases = new[] { "help" })]
        public ICommandEndpoint SlashHelp { get { return _bind("AGT.HELP"); } }
    }
}
