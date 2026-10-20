using EasyAI.Desktop.Infrastructure;

namespace EasyLabel.SegAI.Tests;

public class ClassTableRulesTests
{
    private static readonly string[] TwoClasses = { "NG", "OK" };

    [Fact]
    public void IndexOf_TwoClassProject_KeepsNgOkAndOtherSentinel()
    {
        Assert.Equal(0, ClassTableRules.IndexOf("NG", TwoClasses));
        Assert.Equal(1, ClassTableRules.IndexOf("OK", TwoClasses));
        Assert.Equal(ClassTableRules.OtherIndex, ClassTableRules.IndexOf("dust", TwoClasses));
        Assert.Equal(ClassTableRules.OtherIndex, ClassTableRules.IndexOf("ok", TwoClasses));
        Assert.Equal(ClassTableRules.OtherIndex, ClassTableRules.IndexOf(null, TwoClasses));
    }

    [Fact]
    public void IndexOf_NamedClasses_UsesTablePosition()
    {
        string[] names = { "NG", "OK", "scratch", "dent" };
        Assert.Equal(2, ClassTableRules.IndexOf("scratch", names));
        Assert.Equal(3, ClassTableRules.IndexOf("dent", names));
        Assert.Equal(ClassTableRules.OtherIndex, ClassTableRules.IndexOf("burr", names));
    }

    [Fact]
    public void IsIgnored_NullEnabled_MatchesLegacyNgOkRule()
    {
        Assert.False(ShapeListRules.IsIgnoredBySegAi("NG", "NG", "OK"));
        Assert.False(ShapeListRules.IsIgnoredBySegAi("OK", "NG", "OK"));
        Assert.True(ShapeListRules.IsIgnoredBySegAi("dust", "NG", "OK"));
        Assert.True(ShapeListRules.IsIgnoredBySegAi(null, "NG", "OK"));
        Assert.False(ClassTableRules.IsIgnored("dust", new[] { "NG", "OK", "dust" }, null));
    }

    [Fact]
    public void IsIgnored_DisabledClassAndNonClassLabel_AreIgnored()
    {
        string[] names = { "NG", "OK", "scratch" };
        bool[] enabled = { true, false, true };
        Assert.False(ShapeListRules.IsIgnoredBySegAi("NG", names, enabled));
        Assert.True(ShapeListRules.IsIgnoredBySegAi("OK", names, enabled));
        Assert.False(ShapeListRules.IsIgnoredBySegAi("scratch", names, enabled));
        Assert.True(ShapeListRules.IsIgnoredBySegAi("dust", names, enabled));
    }

    [Fact]
    public void FirstEnabled_SkipsDisabledClasses_AndIsSelectableChecksRange()
    {
        Assert.Equal(1, ClassTableRules.FirstEnabled(new[] { false, true, true }));
        Assert.Equal(0, ClassTableRules.FirstEnabled(new[] { false, false }));
        Assert.True(ClassTableRules.IsSelectable(new[] { true, false }, 0));
        Assert.False(ClassTableRules.IsSelectable(new[] { true, false }, 1));
        Assert.False(ClassTableRules.IsSelectable(new[] { true, false }, 2));
        Assert.False(ClassTableRules.IsSelectable(new[] { true, false }, -1));
    }

    [Fact]
    public void ColorOf_TwoClassesKeepEasyAiColours_AndOtherSlotIsOtherColour()
    {
        Assert.Equal(ShapeColorRules.NgColor, ClassTableRules.ColorOf(0));
        Assert.Equal(ShapeColorRules.OkColor, ClassTableRules.ColorOf(1));
        Assert.Equal(ShapeColorRules.OtherColor, ClassTableRules.ColorOf(ClassTableRules.OtherIndex));
        Assert.Equal(ShapeColorRules.OtherColor, ClassTableRules.ColorOf(-1));
    }

    [Fact]
    public void ColorOf_ExtraClasses_AreStableOpaqueAndDistinct()
    {
        var seen = new HashSet<uint>();
        for (int i = 0; i < ClassTableRules.MaxClasses; i++)
        {
            uint colour = ClassTableRules.ColorOf(i);
            Assert.Equal(0xFF000000u, colour & 0xFF000000u);
            Assert.Equal(colour, ClassTableRules.ColorOf(i));
            if (i >= 2)
            {
                Assert.DoesNotContain(colour, new[] { ShapeColorRules.NgColor, ShapeColorRules.OkColor, ShapeColorRules.OtherColor });
            }

            Assert.True(seen.Add(colour), "class " + i + " repeats an earlier colour");
        }
    }

    [Fact]
    public void Build_ClassesFirstInTableOrder_ThenFreeLabels()
    {
        string[] classes = { "NG", "OK", "scratch" };
        List<string> list = LabelListRules.Build(classes, new[] { "dent", "scratch", "NG" }, "burr", false);
        Assert.Equal(new[] { "NG", "OK", "scratch", "burr", "dent" }, list.ToArray());
    }

    [Fact]
    public void Build_TwoClassOverload_MatchesClassListOverload()
    {
        List<string> legacy = LabelListRules.Build("NG", "OK", new[] { "dent", "NG", "scratch" }, "burr; scratch", false);
        List<string> table = LabelListRules.Build(TwoClasses, new[] { "dent", "NG", "scratch" }, "burr; scratch", false);
        Assert.Equal(legacy, table);
    }
}
