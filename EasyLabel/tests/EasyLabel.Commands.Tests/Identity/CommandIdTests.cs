namespace EasyLabel.Commands.Tests.Identity;

public sealed class CommandIdTests
{
    [Theory]
    [InlineData("LBL.RUN.AUTO")]
    [InlineData("TRN.START")]
    [InlineData("CAN.TOOL.RECT")]
    [InlineData("SET.AGENT.CAP_DAY")]
    [InlineData("TOL.IMG.CROP")]
    [InlineData("UI.RUNMODE.SET")]
    [InlineData("A.B.C.D.E")]
    [InlineData("A.AAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("CAN.SET.ZOOM_2")]
    public void AcceptsDocumentedAndBoundaryIds(string value)
    {
        var id = new CommandId(value);
        Assert.Equal(value, id.Value);
        Assert.Equal(value, id.ToString());
        Assert.True(CommandId.TryParse(value, out CommandId parsed));
        Assert.Equal(id, parsed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("TRN")]
    [InlineData("trn.START")]
    [InlineData("TRN.Start")]
    [InlineData("TRN..START")]
    [InlineData(".TRN.START")]
    [InlineData("TRN.START.")]
    [InlineData("TRN.START\n")]
    [InlineData("TRN.START ")]
    [InlineData("TRN.ĐỌC")]
    [InlineData("TRN.RUN-NOW")]
    [InlineData("A.B.C.D.E.F")]
    [InlineData("A.AAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("1A.RUN")]
    [InlineData("A._RUN")]
    public void RejectsInvalidIds(string? value)
    {
        Assert.False(CommandId.IsValid(value));
        Assert.False(CommandId.TryParse(value, out _));
        Assert.Throws<ArgumentException>(() => new CommandId(value!));
    }

    [Fact]
    public void DefaultStructCannotBecomeARegisteredId() =>
        Assert.Throws<ArgumentException>(() => new CommandDescriptor(default));
}
