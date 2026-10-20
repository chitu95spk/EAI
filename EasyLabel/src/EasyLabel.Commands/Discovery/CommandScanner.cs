using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace EasyLabel.Commands.Discovery
{
    public sealed class CommandScanner
    {
        public IReadOnlyList<DiscoveredCommand> Scan(params object[] handlers)
        {
            if (handlers == null) throw new ArgumentNullException(nameof(handlers));
            var commands = new List<DiscoveredCommand>();
            var ids = new HashSet<CommandId>();
            foreach (object handler in handlers)
            {
                if (handler == null) throw new ArgumentException("Null handler.", nameof(handlers));
                foreach (PropertyInfo property in handler.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    var attribute = property.GetCustomAttribute<CommandAttribute>();
                    if (attribute == null) continue;
                    if (property.GetIndexParameters().Length != 0 || property.GetMethod == null ||
                        !property.GetMethod.IsPublic || !typeof(ICommandEndpoint).IsAssignableFrom(property.PropertyType))
                        throw new InvalidOperationException("Command properties must expose an ICommandEndpoint: " + property.Name);
                    CommandDescriptor descriptor = attribute.CreateDescriptor();
                    if (!ids.Add(descriptor.Id))
                        throw new InvalidOperationException("Duplicate command ID: " + descriptor.Id);
                    var endpoint = property.GetValue(handler) as ICommandEndpoint;
                    if (endpoint == null) throw new InvalidOperationException("Null command endpoint: " + property.Name);
                    commands.Add(new DiscoveredCommand(descriptor, endpoint, attribute.Headless));
                }
            }
            return Array.AsReadOnly(commands.OrderBy(c => c.Descriptor.Id.Value, StringComparer.Ordinal).ToArray());
        }

        public void Register(CommandRegistry registry, IReadOnlyList<DiscoveredCommand> commands)
        {
            if (registry == null) throw new ArgumentNullException(nameof(registry));
            if (commands == null) throw new ArgumentNullException(nameof(commands));
            // Validate every ID before mutating the registry, including collisions with CC-1 registrations.
            var ids = new HashSet<CommandId>();
            foreach (DiscoveredCommand command in commands)
            {
                if (command == null) throw new ArgumentException("Null command.", nameof(commands));
                if (!ids.Add(command.Descriptor.Id) || registry.TryGet(command.Descriptor.Id, out _))
                    throw new InvalidOperationException("Duplicate command ID: " + command.Descriptor.Id);
            }
            foreach (DiscoveredCommand command in commands)
                registry.Register(command.Descriptor, command.Endpoint.ExecuteAsync);
        }
    }
}
