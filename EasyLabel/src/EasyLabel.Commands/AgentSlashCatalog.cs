using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace EasyLabel.Commands
{
    /// <summary>Alias lookup and localized help over registered chat commands.</summary>
    public static class AgentSlashCatalog
    {
        public static bool IsSlashCommand(CommandDescriptor descriptor)
        {
            return descriptor.Id.Value.StartsWith("AGT.", StringComparison.Ordinal) && descriptor.Aliases.Count > 0;
        }

        [return: System.Diagnostics.CodeAnalysis.MaybeNull]
        public static CommandDescriptor Resolve(IEnumerable<CommandDescriptor> catalog, string alias)
        {
            return catalog.Where(IsSlashCommand).SingleOrDefault(command =>
                command.Aliases.Contains(alias, StringComparer.OrdinalIgnoreCase));
        }

        public static JsonElement Arguments(CommandDescriptor descriptor, string text)
        {
            var args = new Dictionary<string, string>();
            foreach (JsonProperty property in descriptor.ArgsSchema.Definition.GetProperty("properties").EnumerateObject())
                args.Add(property.Name, text);
            return JsonSerializer.SerializeToElement(args);
        }

        public static string Help(IEnumerable<CommandDescriptor> catalog, string localizedTemplate)
        {
            // The existing resource supplies ordering and translated argument hints, never command availability.
            var slots = Regex.Matches(localizedTemplate, @"/(?<alias>[a-z]+)(?<hint>[^/]*)")
                .Cast<Match>().ToArray();
            return string.Join("  ", catalog.Where(IsSlashCommand).SelectMany(command => command.Aliases)
                .OrderBy(alias =>
                {
                    int index = Array.FindIndex(slots, slot => slot.Groups["alias"].Value == alias);
                    return index < 0 ? int.MaxValue : index;
                }).ThenBy(alias => alias, StringComparer.Ordinal)
                .Select(alias =>
                {
                    var slot = slots.FirstOrDefault(item => item.Groups["alias"].Value == alias);
                    return "/" + alias + (slot == null ? "" : slot.Groups["hint"].Value.TrimEnd());
                }));
        }
    }
}
