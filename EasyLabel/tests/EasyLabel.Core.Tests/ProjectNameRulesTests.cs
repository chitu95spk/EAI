using EasyLabel.Core.Projects;

namespace EasyLabel.Core.Tests;

public sealed class ProjectNameRulesTests
{
    [Theory]
    [InlineData("a")]
    [InlineData("Line 1")]
    [InlineData("Prod-2026_A")]
    [InlineData("Proj.v2")]
    [InlineData("  padded  ")]
    [InlineData("Chữ tiếng Việt")]
    [InlineData("COM0")]
    [InlineData("COM10")]
    [InlineData("CONSOLE")]
    [InlineData("easyai")]
    [InlineData("x.easyai.dat")]
    public void Validate_AcceptsUsableNames(string name)
    {
        Assert.Equal(ProjectNameError.None, ProjectNameRules.Validate(name));
        Assert.True(ProjectNameRules.IsValid(name));
    }

    [Fact]
    public void Validate_AcceptsExactlyTheMaximumLength_AndCountsAfterTrim()
    {
        Assert.Equal(ProjectNameError.None, ProjectNameRules.Validate(new string('x', ProjectNameRules.MaxLength)));
        Assert.Equal(ProjectNameError.None, ProjectNameRules.Validate("  " + new string('x', ProjectNameRules.MaxLength) + "  "));
        Assert.Equal(ProjectNameError.TooLong, ProjectNameRules.Validate(new string('x', ProjectNameRules.MaxLength + 1)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t ")]
    public void Validate_RejectsEmpty(string? name)
    {
        Assert.Equal(ProjectNameError.Empty, ProjectNameRules.Validate(name));
    }

    [Theory]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("a:b")]
    [InlineData("a*b")]
    [InlineData("a?b")]
    [InlineData("a\"b")]
    [InlineData("a<b")]
    [InlineData("a>b")]
    [InlineData("a|b")]
    [InlineData("a\tb")]
    [InlineData("a\u0001b")]
    public void Validate_RejectsInvalidFileNameCharacters(string name)
    {
        Assert.Equal(ProjectNameError.InvalidCharacter, ProjectNameRules.Validate(name));
    }

    [Theory]
    [InlineData("name.")]
    [InlineData("name. ")]
    [InlineData("..")]
    [InlineData(".")]
    public void Validate_RejectsTrailingDot(string name)
    {
        Assert.Equal(ProjectNameError.TrailingDotOrSpace, ProjectNameRules.Validate(name));
    }

    [Theory]
    [InlineData("CON")]
    [InlineData("con")]
    [InlineData("Aux")]
    [InlineData("NUL")]
    [InlineData("prn")]
    [InlineData("COM1")]
    [InlineData("com9")]
    [InlineData("LPT1")]
    [InlineData("lpt9")]
    [InlineData("CON.txt")]
    [InlineData("nul.v2")]
    [InlineData(" CON ")]
    public void Validate_RejectsReservedDeviceNames(string name)
    {
        Assert.Equal(ProjectNameError.ReservedName, ProjectNameRules.Validate(name));
    }

    [Theory]
    [InlineData("x.easyai")]
    [InlineData("X.EASYAI")]
    [InlineData("x.easyai.data")]
    [InlineData("X.EasyAi.Data")]
    [InlineData(".easyai")]
    public void Validate_RejectsTheSuffixesTheProjectAddsItself(string name)
    {
        Assert.Equal(ProjectNameError.ReservedSuffix, ProjectNameRules.Validate(name));
    }

    [Fact]
    public void Normalize_TrimsAndTreatsNullAsEmpty()
    {
        Assert.Equal("a b", ProjectNameRules.Normalize("  a b \t"));
        Assert.Equal(string.Empty, ProjectNameRules.Normalize(null));
    }
}
