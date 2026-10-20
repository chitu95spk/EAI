namespace EasyLabel.SegAI.Tests
{
    public sealed class AgentSlashCommandsTests
    {
        [Xunit.Theory]
        [Xunit.InlineData("/clear", EasyAI.Desktop.Services.Agent.AgentSlashKind.Clear, "clear", "")]
        [Xunit.InlineData("/check", EasyAI.Desktop.Services.Agent.AgentSlashKind.Check, "check", "")]
        [Xunit.InlineData("/suggest", EasyAI.Desktop.Services.Agent.AgentSlashKind.Suggest, "suggest", "")]
        [Xunit.InlineData("/cancel", EasyAI.Desktop.Services.Agent.AgentSlashKind.Cancel, "cancel", "")]
        [Xunit.InlineData("/help", EasyAI.Desktop.Services.Agent.AgentSlashKind.Help, "help", "")]
        [Xunit.InlineData("/model", EasyAI.Desktop.Services.Agent.AgentSlashKind.Model, "model", "")]
        [Xunit.InlineData("/model sonnet", EasyAI.Desktop.Services.Agent.AgentSlashKind.Model, "model", "sonnet")]
        [Xunit.InlineData("/mode all", EasyAI.Desktop.Services.Agent.AgentSlashKind.Mode, "mode", "all")]
        [Xunit.InlineData("/CLEAR", EasyAI.Desktop.Services.Agent.AgentSlashKind.Clear, "clear", "")]
        [Xunit.InlineData("  /Check  ", EasyAI.Desktop.Services.Agent.AgentSlashKind.Check, "check", "")]
        [Xunit.InlineData("  /Model   Opus 4.1  ", EasyAI.Desktop.Services.Agent.AgentSlashKind.Model, "model", "Opus 4.1")]
        [Xunit.InlineData("/clear now please", EasyAI.Desktop.Services.Agent.AgentSlashKind.Clear, "clear", "now please")]
        [Xunit.InlineData("/foo", EasyAI.Desktop.Services.Agent.AgentSlashKind.Unknown, "foo", "")]
        [Xunit.InlineData("/Foo bar", EasyAI.Desktop.Services.Agent.AgentSlashKind.Unknown, "foo", "bar")]
        [Xunit.InlineData("/", EasyAI.Desktop.Services.Agent.AgentSlashKind.Unknown, "", "")]
        [Xunit.InlineData("/clearx", EasyAI.Desktop.Services.Agent.AgentSlashKind.Unknown, "clearx", "")]
        public void CommandsAreParsed(string input, EasyAI.Desktop.Services.Agent.AgentSlashKind kind, string name, string args)
        {
            EasyAI.Desktop.Services.Agent.AgentSlashCommand c = EasyAI.Desktop.Services.Agent.AgentSlashCommands.Parse(input);
            Xunit.Assert.Equal(kind, c.Kind);
            Xunit.Assert.Equal(name, c.Name);
            Xunit.Assert.Equal(args, c.Args);
            Xunit.Assert.True(c.IsCommand);
        }

        [Xunit.Theory]
        [Xunit.InlineData("find the pads")]
        [Xunit.InlineData("  find the pads  ")]
        [Xunit.InlineData("clear")]
        [Xunit.InlineData("please /clear")]
        [Xunit.InlineData("/data/x.png please")]
        [Xunit.InlineData("/data/x.png")]
        [Xunit.InlineData("  /home/me/img 1.png  ")]
        public void OtherTextIsANormalPrompt(string input)
        {
            EasyAI.Desktop.Services.Agent.AgentSlashCommand c = EasyAI.Desktop.Services.Agent.AgentSlashCommands.Parse(input);
            Xunit.Assert.Equal(EasyAI.Desktop.Services.Agent.AgentSlashKind.Prompt, c.Kind);
            Xunit.Assert.False(c.IsCommand);
            Xunit.Assert.Equal(input.Trim(), c.Args);
        }

        [Xunit.Theory]
        [Xunit.InlineData("")]
        [Xunit.InlineData("   ")]
        [Xunit.InlineData(null)]
        public void EmptyInputIsNothing(string input)
        {
            EasyAI.Desktop.Services.Agent.AgentSlashCommand c = EasyAI.Desktop.Services.Agent.AgentSlashCommands.Parse(input);
            Xunit.Assert.Equal(EasyAI.Desktop.Services.Agent.AgentSlashKind.Empty, c.Kind);
            Xunit.Assert.False(c.IsCommand);
        }

        [Xunit.Theory]
        [Xunit.InlineData("current", true, "current")]
        [Xunit.InlineData(" ALL ", true, "all")]
        [Xunit.InlineData("Remaining", true, "remaining")]
        [Xunit.InlineData("", false, "")]
        [Xunit.InlineData("everything", false, "")]
        public void ModeArgumentIsValidated(string args, bool ok, string key)
        {
            Xunit.Assert.Equal(ok, EasyAI.Desktop.Services.Agent.AgentSlashCommands.TryParseMode(args, out string parsed));
            Xunit.Assert.Equal(key, parsed);
        }

        [Xunit.Theory]
        [Xunit.InlineData(false, false, true)]
        [Xunit.InlineData(true, false, false)]
        [Xunit.InlineData(false, true, false)]
        [Xunit.InlineData(true, true, false)]
        public void ClearIsBlockedWhileARunIsActive(bool running, bool batch, bool allowed)
        {
            Xunit.Assert.Equal(allowed, EasyAI.Desktop.Services.Agent.AgentSlashCommands.CanClearHistory(running, batch));
        }

        [Xunit.Fact]
        public void ANoticeRowShowsItsTextAndNoRunSummary()
        {
            EasyAI.Desktop.Services.Agent.AgentChatEntryViewModel row = EasyAI.Desktop.Services.Agent.AgentChatEntryViewModel.CreateNotice("Unknown command: /foo", "System");
            Xunit.Assert.True(row.IsNotice);
            Xunit.Assert.False(row.IsUser);
            Xunit.Assert.False(row.HasSummary);
            Xunit.Assert.Equal("Unknown command: /foo", row.Prompt);
            Xunit.Assert.Equal("System", row.RoleText);
        }
    }
}
