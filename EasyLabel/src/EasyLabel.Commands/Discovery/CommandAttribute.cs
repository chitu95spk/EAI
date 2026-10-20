using System;
using EasyLabel.Commands.Schema;

namespace EasyLabel.Commands.Discovery
{
    /// <summary>Metadata for a property exposing an existing command endpoint.</summary>
    [AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
    public sealed class CommandAttribute : Attribute
    {
        public CommandAttribute(string id) { Id = id; }
        public string Id { get; private set; }
        public CommandKind Kind { get; set; }
        public string Args { get; set; } = "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}";
        public CtxFlags When { get; set; }
        public CommandCost Cost { get; set; }
        public CommandRisk Risk { get; set; }
        public bool Undo { get; set; }
        public bool Headless { get; set; } = true;
        public string[] Modes { get; set; } = new[] { CommandModes.Simple, CommandModes.Advanced };
        public string[] Aliases { get; set; } = new string[0];

        internal CommandDescriptor CreateDescriptor()
        {
            return new CommandDescriptor(new CommandId(Id), Kind, CommandArgsSchema.Parse(Args),
                When, Cost, Risk, Modes, Aliases, Undo);
        }
    }
}
