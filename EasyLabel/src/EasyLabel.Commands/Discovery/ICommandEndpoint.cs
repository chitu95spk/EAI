using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using EasyLabel.Commands.Results;

namespace EasyLabel.Commands.Discovery
{
    public interface ICommandEndpoint
    {
        bool CanExecute(ICommandContext context, JsonElement args);
        Task<CommandResult> ExecuteAsync(ICommandContext context, JsonElement args, CancellationToken cancellationToken);
    }

    public sealed class DiscoveredCommand
    {
        internal DiscoveredCommand(CommandDescriptor descriptor, ICommandEndpoint endpoint, bool headless)
        {
            Descriptor = descriptor;
            Endpoint = endpoint;
            Headless = headless;
        }

        public CommandDescriptor Descriptor { get; private set; }
        public ICommandEndpoint Endpoint { get; private set; }
        public bool Headless { get; private set; }
    }
}
