using EasyAI.Desktop.Infrastructure;

namespace EasyLabel.SegAI.Tests;

public sealed class ShortcutTableTests
{
    [Fact]
    public void TableEqualsTheApprovedLabelmeSetPlusShapeAndEasyAiKeys()
    {
        string[] expected =
        {
            "OpenImages=Ctrl+O",
            "NextImage=D",
            "PreviousImage=A",
            "NextImageDown=Down",
            "PreviousImageUp=Up",
            "OpenFolder=Ctrl+U",
            "Save=Ctrl+S",
            "SaveProjectAs=Ctrl+Shift+S",
            "NewProject=Ctrl+Alt+N",
            "OpenProject=Ctrl+Shift+O",
            "CloseImage=Ctrl+W",
            "DeleteFile=Ctrl+Del",
            "Settings=Ctrl+Shift+,",
            "Exit=Ctrl+Q",
            "ToolPolygon=Ctrl+N",
            "ToolRect=Ctrl+R",
            "ToolRot=Ctrl+Shift+R",
            "ToolCircle=Ctrl+Shift+C",
            "ToolPoint=Ctrl+Shift+P",
            "ToolLine=Ctrl+L",
            "ToolLineStrip=Ctrl+Shift+L",
            "ToolAiPoints=Ctrl+Shift+A",
            "ToolAiBox=Ctrl+B",
            "ToolHand=H",
            "EditMode=Ctrl+J",
            "Undo=Ctrl+Z",
            "Copy=Ctrl+C",
            "Paste=Ctrl+V",
            "Duplicate=Ctrl+D",
            "EditLabel=Ctrl+E",
            "DeleteShape=Del",
            "DeleteVertex=Meta+H",
            "SelectUp=Ctrl+Up",
            "SelectDown=Ctrl+Down",
            "SelectLeft=Ctrl+Left",
            "SelectRight=Ctrl+Right",
            "KeepPrevious=Ctrl+P",
            "ToggleShapes=T",
            "ZoomIn=Ctrl++",
            "ZoomOut=Ctrl+-",
            "ActualSize=Ctrl+0",
            "Fit=Ctrl+F",
            "FitWidth=Ctrl+Shift+F",
            "FitSpace=Space",
            "FinishDraft=Enter",
            "CancelDraft=Esc",
            "RemoveDraftVertex=Backspace",
            "AutoLabel=Ctrl+Alt+L",
            "Train=Ctrl+Alt+T",
            "ImportDataset=Ctrl+Alt+I",
            "TagOk=G",
            "TagNg=B",
        };

        string[] actual = ShortcutTable.All.Select(e => e.Id + "=" + e.DisplayText).ToArray();

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void NoGestureAndNoIdIsUsedTwice()
    {
        Assert.Equal(ShortcutTable.All.Count, ShortcutTable.All.Select(e => e.Id).Distinct().Count());
        Assert.Equal(ShortcutTable.All.Count, ShortcutTable.All.Select(e => e.Key + "|" + (int)e.Modifiers).Distinct().Count());
        Assert.Equal(ShortcutTable.All.Count, ShortcutTable.All.Select(e => e.DisplayText).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void EveryShortcutIdHasAnEntry()
    {
        foreach (ShortcutId id in Enum.GetValues<ShortcutId>())
        {
            Assert.Equal(id, ShortcutTable.Get(id).Id);
        }
    }

    [Theory]
    [InlineData("V", ShortcutModifiers.None)]
    [InlineData("R", ShortcutModifiers.None)]
    [InlineData("O", ShortcutModifiers.None)]
    [InlineData("P", ShortcutModifiers.None)]
    [InlineData("F", ShortcutModifiers.None)]
    [InlineData("D1", ShortcutModifiers.None)]
    [InlineData("D2", ShortcutModifiers.None)]
    [InlineData("Tab", ShortcutModifiers.None)]
    [InlineData("Tab", ShortcutModifiers.Shift)]
    [InlineData("Left", ShortcutModifiers.None)]
    [InlineData("A", ShortcutModifiers.Ctrl)]
    [InlineData("Y", ShortcutModifiers.Ctrl)]
    [InlineData("Z", ShortcutModifiers.Ctrl | ShortcutModifiers.Shift)]
    [InlineData("D1", ShortcutModifiers.Ctrl)]
    [InlineData("S", ShortcutModifiers.None)]
    public void RemovedShortcutsAreGone(string key, ShortcutModifiers modifiers)
    {
        Assert.Null(ShortcutTable.Find(key, modifiers));
    }

    [Theory]
    [InlineData("O", ShortcutModifiers.Ctrl, ShortcutId.OpenImages)]
    [InlineData("D", ShortcutModifiers.None, ShortcutId.NextImage)]
    [InlineData("D", ShortcutModifiers.Ctrl, ShortcutId.Duplicate)]
    [InlineData("R", ShortcutModifiers.Ctrl | ShortcutModifiers.Shift, ShortcutId.ToolRot)]
    [InlineData("L", ShortcutModifiers.Ctrl | ShortcutModifiers.Alt, ShortcutId.AutoLabel)]
    [InlineData("H", ShortcutModifiers.None, ShortcutId.ToolHand)]
    [InlineData("H", ShortcutModifiers.Meta, ShortcutId.DeleteVertex)]
    [InlineData("Add", ShortcutModifiers.Ctrl, ShortcutId.ZoomIn)]
    [InlineData("OemPlus", ShortcutModifiers.Ctrl | ShortcutModifiers.Shift, ShortcutId.ZoomIn)]
    [InlineData("Subtract", ShortcutModifiers.Ctrl, ShortcutId.ZoomOut)]
    [InlineData("NumPad0", ShortcutModifiers.Ctrl, ShortcutId.ActualSize)]
    [InlineData("Enter", ShortcutModifiers.None, ShortcutId.FinishDraft)]
    [InlineData("Return", ShortcutModifiers.None, ShortcutId.FinishDraft)]
    [InlineData("Escape", ShortcutModifiers.None, ShortcutId.CancelDraft)]
    [InlineData("Back", ShortcutModifiers.None, ShortcutId.RemoveDraftVertex)]
    [InlineData("OemComma", ShortcutModifiers.Ctrl | ShortcutModifiers.Shift, ShortcutId.Settings)]
    public void FindMapsKeyAndModifiersToTheCommand(string key, ShortcutModifiers modifiers, ShortcutId expected)
    {
        ShortcutEntry? entry = ShortcutTable.Find(key, modifiers);

        Assert.NotNull(entry);
        Assert.Equal(expected, entry!.Id);
    }

    [Fact]
    public void TooltipTextAppendsTheShortcutInParentheses()
    {
        Assert.Equal("Open images (Ctrl+O)", ShortcutTable.WithShortcut("Open images", ShortcutId.OpenImages));
        Assert.Equal("Ctrl+Shift+S", ShortcutTable.TextOf(ShortcutId.SaveProjectAs));
    }

    [Theory]
    [InlineData("N", ShortcutModifiers.Ctrl, ShortcutId.ToolPolygon)]
    [InlineData("N", ShortcutModifiers.Ctrl | ShortcutModifiers.Alt, ShortcutId.NewProject)]
    [InlineData("O", ShortcutModifiers.Ctrl | ShortcutModifiers.Shift, ShortcutId.OpenProject)]
    [InlineData("S", ShortcutModifiers.Ctrl | ShortcutModifiers.Shift, ShortcutId.SaveProjectAs)]
    [InlineData("S", ShortcutModifiers.Ctrl, ShortcutId.Save)]
    public void ProjectShortcutsDoNotTakeOverTheLabelmeToolKeys(string key, ShortcutModifiers modifiers, ShortcutId expected)
    {
        ShortcutEntry? entry = ShortcutTable.Find(key, modifiers);

        Assert.NotNull(entry);
        Assert.Equal(expected, entry!.Id);
    }
}
