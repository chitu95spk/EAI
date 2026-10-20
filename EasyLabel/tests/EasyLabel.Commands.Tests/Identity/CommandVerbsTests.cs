using System.Threading.Tasks;
using EasyLabel.Commands.Results;

namespace EasyLabel.Commands.Tests.Identity
{
    public sealed class CommandVerbsTests
    {
        [Theory]
        [InlineData("RUN")]
        [InlineData("START")]
        [InlineData("STOP")]
        [InlineData("CANCEL")]
        [InlineData("OPEN")]
        [InlineData("CLOSE")]
        [InlineData("SAVE")]
        [InlineData("SHOW")]
        [InlineData("HIDE")]
        [InlineData("TOGGLE")]
        [InlineData("SET")]
        [InlineData("GET")]
        [InlineData("ADD")]
        [InlineData("REMOVE")]
        [InlineData("DELETE")]
        [InlineData("NEXT")]
        [InlineData("PREV")]
        [InlineData("ZOOM")]
        [InlineData("FIT")]
        [InlineData("EXPORT")]
        [InlineData("IMPORT")]
        [InlineData("ACCEPT")]
        [InlineData("REJECT")]
        [InlineData("SELECT")]
        [InlineData("CLEAR")]
        [InlineData("UNDO")]
        [InlineData("REDO")]
        [InlineData("TOOL")]
        [InlineData("AGENT")]
        [InlineData("RUNMODE")]
        [InlineData("RESET")]
        public void RecognizesCatalogVocabulary(string token)
        {
            Assert.True(CommandVerbs.IsKnown(token));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("CAN")]
        [InlineData("WIDGET")]
        [InlineData("CAN.TOOL.RECT")]
        [InlineData("SET.AGENT.CAP_DAY")]
        [InlineData("UI.RUNMODE.SET")]
        [InlineData("run")]
        [InlineData(" RUN")]
        [InlineData("RUN ")]
        [InlineData("RUN NOW")]
        public void RejectsUnknownOrNonCanonicalTokens(string token)
        {
            Assert.False(CommandVerbs.IsKnown(token));
        }

        [Fact]
        public void LegacyCatalogTokensDoNotConstrainLexicalCommandIds()
        {
            Assert.True(CommandId.IsValid("CAN.TOOL.RECT"));
            Assert.True(CommandId.IsValid("SET.AGENT.CAP_DAY"));
            Assert.True(CommandId.IsValid("UI.RUNMODE.SET"));
            Assert.True(CommandId.IsValid("CAN.WIDGET.RECT"));
            Assert.False(CommandVerbs.IsKnown("WIDGET"));

            var descriptor = new CommandDescriptor(new CommandId("CAN.WIDGET.RECT"));
            var registry = new CommandRegistry();
            registry.Register(descriptor, (context, args, cancellationToken) =>
                Task.FromResult(CommandResult.Success()));

            CommandDescriptor registered = Assert.Single(registry.Query(new CommandContext(CtxFlags.None)));
            Assert.Equal("CAN.WIDGET.RECT", registered.Id.Value);
        }
    }
}
