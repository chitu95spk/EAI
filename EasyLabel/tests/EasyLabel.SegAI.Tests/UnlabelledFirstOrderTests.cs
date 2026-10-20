using EasyAI.Desktop.ViewModels;

namespace EasyLabel.SegAI.Tests;

/// <summary>EAI-LOGO: the image list toggle "Unlabelled first" (off = by name, natural order).</summary>
public sealed class UnlabelledFirstOrderTests
{
    private sealed record Row(string Name, bool Unlabelled);

    private static readonly Row[] Rows =
    {
        new Row("img (10)", false), new Row("img (2)", true), new Row("img (1)", false), new Row("img (11)", true),
    };

    [Fact]
    public void OffSortsByNameNaturally()
    {
        var order = ImageListSortRules.OrderByLabelState(Rows, false, r => r.Unlabelled, r => r.Name).Select(r => r.Name).ToArray();
        Assert.Equal(new[] { "img (1)", "img (2)", "img (10)", "img (11)" }, order);
    }

    [Fact]
    public void OnPutsUnlabelledFirstThenByName()
    {
        var order = ImageListSortRules.OrderByLabelState(Rows, true, r => r.Unlabelled, r => r.Name).Select(r => r.Name).ToArray();
        Assert.Equal(new[] { "img (2)", "img (11)", "img (1)", "img (10)" }, order);
    }

    [Theory]
    [InlineData("a (2)", "a (10)", -1)]
    [InlineData("A", "a", 0)]
    [InlineData("a (007)", "a (7)", 0)]
    [InlineData("b", "a (1)", 1)]
    public void NaturalCompareReadsDigitRunsAsNumbers(string a, string b, int sign)
    {
        Assert.Equal(sign, Math.Sign(ImageListSortRules.NaturalCompare(a, b)));
    }
}

/// <summary>EAI-LOGO: the Train first / Val first boxes of the image list.</summary>
public sealed class TrainValFirstOrderTests
{
    private sealed record Row(string Name, bool Unlabelled, bool Train, bool Val);

    private static readonly Row[] Rows =
    {
        new Row("a (3)", false, false, true), new Row("a (1)", true, false, false), new Row("a (4)", false, true, false),
        new Row("a (2)", false, true, false), new Row("a (5)", false, false, true),
    };

    private static string[] Order(bool unlabelled, bool train, bool val)
    {
        return ImageListSortRules.OrderRows(Rows, unlabelled, train, val, r => r.Unlabelled, r => r.Train, r => r.Val, r => r.Name).Select(r => r.Name).ToArray();
    }

    [Fact]
    public void AllOffIsByName() => Assert.Equal(new[] { "a (1)", "a (2)", "a (3)", "a (4)", "a (5)" }, Order(false, false, false));

    [Fact]
    public void TrainFirstPutsTrainRowsOnTop() => Assert.Equal(new[] { "a (2)", "a (4)", "a (1)", "a (3)", "a (5)" }, Order(false, true, false));

    [Fact]
    public void ValFirstPutsValRowsOnTop() => Assert.Equal(new[] { "a (3)", "a (5)", "a (1)", "a (2)", "a (4)" }, Order(false, false, true));

    [Fact]
    public void BothBoxesPutTrainThenVal() => Assert.Equal(new[] { "a (2)", "a (4)", "a (3)", "a (5)", "a (1)" }, Order(false, true, true));

    [Fact]
    public void UnlabelledComesBeforeTheModeGroups() => Assert.Equal(new[] { "a (1)", "a (2)", "a (4)", "a (3)", "a (5)" }, Order(true, true, true));
}
