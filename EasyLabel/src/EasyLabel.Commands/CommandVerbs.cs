using System;
using System.Collections.Generic;

namespace EasyLabel.Commands
{
    /// <summary>
    /// Optional catalog vocabulary for command descriptors. CommandId validates only the
    /// lexical ID grammar; catalog owners may choose the semantic segment to validate.
    /// TOOL, AGENT, and RUNMODE remain here for legacy IDs such as CAN.TOOL.RECT,
    /// SET.AGENT.CAP_DAY, and UI.RUNMODE.SET. CAN is a domain token, not a verb.
    /// </summary>
    public static class CommandVerbs
    {
        private static readonly HashSet<string> Known = new HashSet<string>(StringComparer.Ordinal)
        {
            "RUN", "START", "STOP", "CANCEL", "OPEN", "CLOSE", "SAVE", "SHOW", "HIDE", "TOGGLE",
            "SET", "GET", "ADD", "REMOVE", "DELETE", "NEXT", "PREV", "ZOOM", "FIT", "EXPORT",
            "IMPORT", "ACCEPT", "REJECT", "SELECT", "CLEAR", "UNDO", "REDO", "TOOL", "AGENT",
            "RUNMODE", "RESET"
        };

        public static bool IsKnown(string token)
        {
            return !string.IsNullOrEmpty(token) && Known.Contains(token);
        }
    }
}
