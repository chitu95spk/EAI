using System;

namespace EasyLabel.Commands
{
    public enum RunMode { Simple, Advanced, Online }

    public static class RunModeNames
    {
        public static string ToMode(RunMode mode)
        {
            switch (mode)
            {
                case RunMode.Simple: return CommandModes.Simple;
                case RunMode.Advanced: return CommandModes.Advanced;
                case RunMode.Online: return CommandModes.Online;
                default: throw new ArgumentOutOfRangeException(nameof(mode));
            }
        }

        public static RunMode Parse(string mode)
        {
            switch (mode)
            {
                case CommandModes.Simple: return RunMode.Simple;
                case CommandModes.Advanced: return RunMode.Advanced;
                // Preserve CC-1 callers; running is a context flag for new callers.
                case CommandModes.Online:
                case CommandModes.OnlineRunning: return RunMode.Online;
                default: throw new ArgumentException("Unknown run mode.", nameof(mode));
            }
        }
    }
}
