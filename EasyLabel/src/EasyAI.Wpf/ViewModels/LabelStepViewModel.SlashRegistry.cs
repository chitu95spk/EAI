using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using EasyAI.Desktop.Commands;
using EasyAI.Desktop.Resources;
using EasyAI.Desktop.Services.Agent;
using EasyLabel.Commands;
using EasyLabel.Core.Io;

namespace EasyAI.Desktop.ViewModels
{
    public sealed partial class LabelStepViewModel
    {
        private async Task DispatchSlashCommandAsync(AgentSlashCommand command)
        {
            var host = _host.SlashCommandHost;
            var descriptor = host == null ? null :
                AgentSlashCatalog.Resolve(host.Catalog.Select(item => item.Descriptor), command.Name);
            if (host == null || descriptor == null)
            {
                AddChatNotice(string.Format(System.Globalization.CultureInfo.CurrentCulture,
                    Strings.ByName("AgentSlashUnknownFormat"), command.Name));
                return;
            }

            JsonElement args = AgentSlashCatalog.Arguments(descriptor, command.Args);
            if (!host.CanExecute(descriptor.Id, args))
            {
                AddChatNotice(Strings.ByName("AgentSlashUnavailable"));
                return;
            }

            var result = await host.ExecuteAsync(descriptor.Id, args);
            if (!result.Ok) AddChatNotice(Strings.ByName("AgentSlashUnavailable"));
        }

        internal ViewModelCommandTarget ResolveSlashCommandTarget(string id, JsonElement args)
        {
            if (id == "AGT.SUGGEST") return SlashTarget(SuggestPromptCommand);
            if (id == "AGT.CANCEL") return SlashTarget(CancelRunCommand);
            Action action;
            if (id == "AGT.CLEAR") action = ClearSlashHistory;
            else if (id == "AGT.FORGET") action = ForgetSlashVocabulary;
            else if (id == "AGT.MODEL.SET") action = () => HandleModelCommand(args.GetProperty("name").GetString() ?? string.Empty);
            else if (id == "AGT.MODE.SET") action = () => HandleModeCommand(args.GetProperty("mode").GetString() ?? string.Empty);
            else if (id == "AGT.HELP") action = () => AddChatNotice(SlashHelpText());
            else throw new ArgumentException("Unsupported chat command: " + id, nameof(id));
            return SlashTarget(new RelayCommand(action));
        }

        private string SlashHelpText()
        {
            var host = _host.SlashCommandHost;
            return AgentSlashCatalog.Help(host == null ? Enumerable.Empty<CommandDescriptor>() :
                host.Catalog.Select(item => item.Descriptor), Strings.ByName("AgentSlashHelp"));
        }

        private static ViewModelCommandTarget SlashTarget(System.Windows.Input.ICommand command)
        {
#pragma warning disable CS8625
            return ViewModelCommandTarget.FromCommand(command, null);
#pragma warning restore CS8625
        }

        private void ClearSlashHistory()
        {
            if (!AgentSlashCommands.CanClearHistory(IsRunning, IsBatchRunning))
            {
                AddChatNotice(Strings.ByName("AgentSlashClearBusy"));
                return;
            }

            AgentChatLog.Clear(_host.Project?.Paths.AgentChatFile);
            _chatNotices.Clear();
            ReloadChat();
        }

        private void ForgetSlashVocabulary()
        {
            try
            {
                EasyLabel.Core.Io.ProjectVocabulary.Clear(_host.Project?.Paths.VocabularyFile);
                AddChatNotice(Strings.ByName("AgentSlashForgetDone"));
            }
            catch (Exception ex) when (ex is System.IO.IOException || ex is UnauthorizedAccessException)
            {
                AddChatNotice(Strings.ByName("AgentSlashUnavailable"));
            }
        }
    }
}
