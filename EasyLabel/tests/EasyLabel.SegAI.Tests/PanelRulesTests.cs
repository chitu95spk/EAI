using EasyAI.Desktop.Infrastructure;
using EasyLabel.Core.Commands;
using EasyLabel.Core.Model;

namespace EasyLabel.SegAI.Tests;

public class PanelRulesTests
{
    [Fact]
    public void DefaultOrder_IsMergedLabelShapePanelThenFiles_WithUniqueIds()
    {
        Assert.Equal(new[] { "labels", "files" }, DockPanelIds.DefaultOrder);
        Assert.Equal(DockPanelIds.DefaultOrder.Count, DockPanelIds.DefaultOrder.Distinct().Count());
        foreach (string id in DockPanelIds.DefaultOrder)
        {
            Assert.True(DockPanelIds.IsPanel(id));
            Assert.True(DockPanelIds.DefaultHeightWeight(id) > 0);
            Assert.True(DockPanelIds.DefaultMinHeight(id) >= 44);
        }

        Assert.False(DockPanelIds.IsPanel("easyai"));
        Assert.False(DockPanelIds.IsPanel("flags"));
        Assert.False(DockPanelIds.IsPanel("shapes"));
        Assert.True(DockPanelIds.DefaultMinHeight(DockPanelIds.Labels) >= 120);
        Assert.True(DockPanelIds.DefaultMinHeight(DockPanelIds.Files) >= 96);
        // The merged panel is the tall one (about 60 % of the column).
        Assert.True(DockPanelIds.DefaultHeightWeight(DockPanelIds.Labels) > DockPanelIds.DefaultHeightWeight(DockPanelIds.Files));
        // The minimums must fit the right column of an 800 px high window (about 480 px) without overflow.
        double sum = 0;
        foreach (string panel in DockPanelIds.DefaultOrder) sum += DockPanelIds.DefaultMinHeight(panel);
        Assert.True(sum <= 480, "sum of default minimum heights: " + sum);
        Assert.Equal(3, LayoutSettings.CurrentSchema);
        Assert.Equal(LayoutSettings.CurrentSchema, LayoutSettings.CreateDefault().SchemaVersion);
        Assert.True(new LayoutSettings().SchemaVersion < LayoutSettings.CurrentSchema);

        Assert.False(DockPanelIds.IsPanel(DockPanelIds.Canvas));
        Assert.False(DockPanelIds.IsPanel(null));
    }

    [Fact]
    public void LegacyFlags_HideTheMatchingPanels()
    {
        Assert.Empty(DockLayoutRules.HiddenByLegacyFlags(true));
        Assert.Equal(new[] { "files" }, DockLayoutRules.HiddenByLegacyFlags(false));
    }

    [Fact]
    public void LayoutXml_MustMentionCanvasAndEveryPanel()
    {
        string full = "<LayoutRoot><LayoutDocument ContentId=\"canvas\"/>"
            + "<LayoutAnchorable ContentId=\"labels\"/><LayoutAnchorable ContentId=\"files\"/></LayoutRoot>";
        Assert.True(DockLayoutRules.LooksUsable(full));
        Assert.False(DockLayoutRules.LooksUsable(full.Replace("\"files\"", "\"other\"")));
        Assert.False(DockLayoutRules.LooksUsable(full.Replace("\"canvas\"", "\"x\"")));
        Assert.False(DockLayoutRules.LooksUsable(null));
        Assert.False(DockLayoutRules.LooksUsable("  "));
    }

    [Fact]
    public void OldLayoutFile_WithoutDockLayout_LoadsWithDefaults()
    {
        var layout = new LayoutSettings();
        Assert.Null(layout.DockLayout);
        LayoutSettings normalized = layout.Normalized(0, 0, 1920, 1080);
        Assert.Null(normalized.DockLayout);

        layout.DockLayout = "<x/>";
        Assert.Equal("<x/>", layout.Normalized(0, 0, 1920, 1080).DockLayout);
    }

    [Fact]
    public void FileFilter_IsCaseAndDiacriticInsensitive_AndKeepsOrder()
    {
        var names = new List<string> { "a/IMG_001.bmp", "a/img_002.bmp", "b/Ảnh_003.png", "b/other.png" };
        Assert.Equal(new[] { 0, 1, 2, 3 }, FileListRules.FilterIndices(names, null));
        Assert.Equal(new[] { 0, 1, 2, 3 }, FileListRules.FilterIndices(names, "  "));
        Assert.Equal(new[] { 0, 1 }, FileListRules.FilterIndices(names, "img"));
        Assert.Equal(new[] { 2 }, FileListRules.FilterIndices(names, "anh 003"));
        Assert.Empty(FileListRules.FilterIndices(names, "zzz"));
    }

    [Fact]
    public void ListName_ShowsUnsavedMarker()
    {
        Assert.Equal("a/x.bmp", FileListRules.ListName("a/x.bmp", false));
        Assert.Equal("* a/x.bmp", FileListRules.ListName("a/x.bmp", true));
    }

    [Fact]
    public void FlagRows_ListPredefinedFirst_ThenDocumentOnlyFlags()
    {
        Assert.Empty(FlagRules.RowNames(null, null));
        Assert.Equal(new[] { "blur", "dark" }, FlagRules.RowNames("blur\ndark", null));
        Assert.Equal(new[] { "blur", "dark", "zeta" }, FlagRules.RowNames("blur, dark", new[] { "zeta", "dark", "zeta" }));
        Assert.Equal(new[] { "blur", "alpha", "zeta" }, FlagRules.RowNames("blur", new[] { "zeta", "alpha" }));
    }

    [Fact]
    public void FlagRules_WouldChange_TreatsAbsentAsFalse()
    {
        var flags = new Dictionary<string, bool> { ["a"] = true, ["b"] = false };
        Assert.False(FlagRules.WouldChange(flags, "a", true));
        Assert.True(FlagRules.WouldChange(flags, "a", false));
        Assert.True(FlagRules.WouldChange(flags, "b", true));
        Assert.False(FlagRules.WouldChange(flags, "c", false));
        Assert.True(FlagRules.WouldChange(flags, "c", true));
        Assert.True(FlagRules.WouldChange(null, "c", true));
        Assert.True(FlagRules.IsChecked(flags, "a"));
        Assert.False(FlagRules.IsChecked(flags, "b"));
        Assert.False(FlagRules.IsChecked(null, "a"));
    }

    [Fact]
    public void FlagHash_IsOrderIndependentAndChangesWithValues()
    {
        var one = new Dictionary<string, bool> { ["a"] = true, ["b"] = false };
        var two = new Dictionary<string, bool> { ["b"] = false, ["a"] = true };
        var three = new Dictionary<string, bool> { ["a"] = false, ["b"] = false };
        Assert.Equal(FlagRules.Hash(one), FlagRules.Hash(two));
        Assert.NotEqual(FlagRules.Hash(one), FlagRules.Hash(three));
    }

    [Fact]
    public void ImageFlagCommand_IsOneUndoUnit_AndRestoresAbsence()
    {
        var document = new LabelMeDocument();
        var stack = new CommandStack();

        stack.Execute(new ImageFlagCommand(document, "blur", true));
        Assert.True(document.Flags["blur"]);
        Assert.True(stack.CanUndo);

        stack.Undo();
        Assert.False(document.Flags.ContainsKey("blur"));
        Assert.False(stack.CanUndo);

        stack.Redo();
        Assert.True(document.Flags["blur"]);

        // Unchecking an existing flag restores the stored value on undo.
        stack.Execute(new ImageFlagCommand(document, "blur", false));
        Assert.False(document.Flags["blur"]);
        stack.Undo();
        Assert.True(document.Flags["blur"]);
    }

    [Fact]
    public void ShapeRows_MarkOtherLabelsAsIgnored_AndKeepKinds()
    {
        Assert.False(ShapeListRules.IsIgnoredBySegAi("NG", "NG", "OK"));
        Assert.False(ShapeListRules.IsIgnoredBySegAi("OK", "NG", "OK"));
        Assert.True(ShapeListRules.IsIgnoredBySegAi("dust", "NG", "OK"));
        Assert.True(ShapeListRules.IsIgnoredBySegAi(null, "NG", "OK"));
        Assert.Equal("polygon", ShapeListRules.KindText(null));
        Assert.Equal("circle", ShapeListRules.KindText("circle"));
    }

    [Fact]
    public void ShapeHash_ChangesWithLabelTypeAndCount()
    {
        var a = new List<LabelShape> { new LabelShape { Label = "NG", ShapeType = "polygon" } };
        var b = new List<LabelShape> { new LabelShape { Label = "OK", ShapeType = "polygon" } };
        var c = new List<LabelShape> { new LabelShape { Label = "NG", ShapeType = "circle" } };
        var d = new List<LabelShape>(a) { new LabelShape { Label = "NG", ShapeType = "polygon" } };
        int hash = ShapeListRules.ShapeHash(a);
        Assert.Equal(hash, ShapeListRules.ShapeHash(new List<LabelShape> { new LabelShape { Label = "NG", ShapeType = "polygon" } }));
        Assert.NotEqual(hash, ShapeListRules.ShapeHash(b));
        Assert.NotEqual(hash, ShapeListRules.ShapeHash(c));
        Assert.NotEqual(hash, ShapeListRules.ShapeHash(d));
        Assert.Equal(0, ShapeListRules.ShapeHash(null));
    }

    [Fact]
    public void UsedLabels_AreDistinctInFirstSeenOrder()
    {
        var shapes = new List<LabelShape>
        {
            new LabelShape { Label = "dust" },
            new LabelShape { Label = "NG" },
            new LabelShape { Label = "dust" },
            new LabelShape { Label = null },
        };
        Assert.Equal(new[] { "dust", "NG" }, ShapeListRules.UsedLabels(shapes));
        Assert.Empty(ShapeListRules.UsedLabels(null));
    }

    [Fact]
    public void LabelGroups_KeepNgOkFirst_GroupShapesAndCollectUnlabelled()
    {
        var shapes = new List<LabelShape>
        {
            new LabelShape { Label = "dust" },
            new LabelShape { Label = "NG" },
            new LabelShape { Label = "dust" },
            new LabelShape { Label = null },
        };
        List<LabelGroupRow> rows = LabelGroupRules.Build(shapes, new[] { "NG", "OK", "dust" });
        Assert.Equal(new[] { "NG", "OK", "dust", "" }, rows.Select(r => r.Name).ToArray());
        Assert.Equal(new[] { 1 }, rows[0].ShapeIndices);
        Assert.Empty(rows[1].ShapeIndices);
        Assert.Equal(new[] { 0, 2 }, rows[2].ShapeIndices);
        Assert.Equal(new[] { 3 }, rows[3].ShapeIndices);

        // No document: the label rows stay (NG and OK are always present), there is no unlabelled row.
        Assert.Equal(2, LabelGroupRules.Build(null, new[] { "NG", "OK" }).Count);

        Assert.True(LabelGroupRules.AllVisible(new[] { 0, 2 }, i => true));
        Assert.False(LabelGroupRules.AllVisible(new[] { 0, 2 }, i => i != 2));
        Assert.True(LabelGroupRules.AllVisible(Array.Empty<int>(), i => false));
    }

    [Fact]
    public void ShapeClick_SelectsOne_CtrlToggles_HiddenKeepsSelection()
    {
        Assert.Equal(new[] { 2 }, ShapeSelectionRules.Next(new[] { 0, 1 }, 2, false, true));
        Assert.Equal(new[] { 0, 1, 2 }, ShapeSelectionRules.Next(new[] { 0, 1 }, 2, true, true));
        Assert.Equal(new[] { 1 }, ShapeSelectionRules.Next(new[] { 0, 1 }, 0, true, true));
        Assert.Equal(new[] { 0, 1 }, ShapeSelectionRules.Next(new[] { 0, 1 }, 2, false, false));
    }

    [Fact]
    public void FileModeButtons_NeedRegionsAndAnIdleEditor()
    {
        Assert.True(FileModeRules.CanSet(true, false, false, false, true, true));
        Assert.False(FileModeRules.CanSet(true, false, false, false, true, false));
        Assert.False(FileModeRules.CanSet(true, false, false, false, false, true));
        Assert.False(FileModeRules.CanSet(false, false, false, false, true, true));
        Assert.False(FileModeRules.CanSet(true, true, false, false, true, true));
        Assert.False(FileModeRules.CanSet(true, false, true, false, true, true));
        Assert.False(FileModeRules.CanSet(true, false, false, true, true, true));
    }

    [Fact]
    public void ShapeSelection_SkipsHiddenShapes_AndComparesAsSets()
    {
        Assert.Equal(new[] { 0, 3 }, ShapeListRules.DesiredSelection(
            new[] { true, false, true, true }, new[] { true, true, false, true }));
        Assert.True(ShapeListRules.SameSet(new[] { 2, 0 }, new[] { 0, 2 }));
        Assert.False(ShapeListRules.SameSet(new[] { 0 }, new[] { 0, 2 }));
        Assert.False(ShapeListRules.SameSet(new[] { 1 }, new[] { 2 }));
    }
}
