using EasyAI.Desktop.Infrastructure;

namespace EasyLabel.SegAI.Tests;

public class ClassHotkeyMenuRulesTests
{
    [Fact]
    public void HotkeyIndex_TwoClassProject_SelectsNothing_KeepingExistingBindings()
    {
        Assert.Equal(-1, ClassTableRules.HotkeyIndex("D1", 2));
        Assert.Equal(-1, ClassTableRules.HotkeyIndex("D2", 2));
        Assert.Equal(-1, ClassTableRules.HotkeyIndex("NumPad1", 2));
    }

    [Fact]
    public void HotkeyIndex_MoreThanTwoClasses_MapsDigitsToTablePosition()
    {
        Assert.Equal(0, ClassTableRules.HotkeyIndex("D1", 3));
        Assert.Equal(1, ClassTableRules.HotkeyIndex("D2", 3));
        Assert.Equal(2, ClassTableRules.HotkeyIndex("D3", 3));
        Assert.Equal(-1, ClassTableRules.HotkeyIndex("D4", 3));
        Assert.Equal(8, ClassTableRules.HotkeyIndex("D9", 10));
        Assert.Equal(0, ClassTableRules.HotkeyIndex("NumPad1", 3));
        Assert.Equal(2, ClassTableRules.HotkeyIndex("NumPad3", 3));
    }

    [Fact]
    public void HotkeyIndex_NonDigitsAndZero_SelectNothing()
    {
        Assert.Equal(-1, ClassTableRules.HotkeyIndex("D0", 5));
        Assert.Equal(-1, ClassTableRules.HotkeyIndex("NumPad0", 5));
        Assert.Equal(-1, ClassTableRules.HotkeyIndex("A", 5));
        Assert.Equal(-1, ClassTableRules.HotkeyIndex("D10", 12));
        Assert.Equal(-1, ClassTableRules.HotkeyIndex("NumPad", 5));
        Assert.Equal(-1, ClassTableRules.HotkeyIndex("Delete", 5));
        Assert.Equal(-1, ClassTableRules.HotkeyIndex(null!, 5));
    }

    [Fact]
    public void MenuAutomationId_NgOkKeepTheirIds_OthersUseTheIndex()
    {
        Assert.Equal("MenuClassNg", ClassTableRules.MenuAutomationId(0));
        Assert.Equal("MenuClassOk", ClassTableRules.MenuAutomationId(1));
        Assert.Equal("MenuClass2", ClassTableRules.MenuAutomationId(2));
        Assert.Equal("MenuClass31", ClassTableRules.MenuAutomationId(31));
    }
}
