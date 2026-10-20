using EasyLabel.SegAI.Convert;
using EasyLabel.SegAI.Model;
using Newtonsoft.Json.Linq;

namespace EasyLabel.SegAI.Tests;

/// <summary>
/// Renaming a class relabels its shapes by table position, and the class prediction decision for named and NG/OK tables.
/// The decision tests need no native library.
/// </summary>
public sealed class ClassRenameTests
{
    private const string Document =
        "{\"version\":\"5.0.5\",\"flags\":{\"train\":true},\"shapes\":[" +
        "{\"label\":\"NG\",\"points\":[[1.0,1.0],[10.0,10.0]],\"group_id\":null,\"shape_type\":\"polygon\",\"flags\":{},\"custom\":\"kept\"," +
        "\"easyai\":{\"holes\":[]}}," +
        "{\"label\":\"OK\",\"points\":[[20.0,20.0],[30.0,30.0]],\"group_id\":null,\"shape_type\":\"polygon\",\"flags\":{}}," +
        "{\"label\":\"Scratch\",\"points\":[[40.0,40.0],[50.0,50.0]],\"group_id\":null,\"shape_type\":\"polygon\",\"flags\":{}}" +
        "],\"imagePath\":\"a.png\",\"imageData\":\"QUJD\",\"imageHeight\":64,\"imageWidth\":64,\"unknownTop\":{\"x\":1}}";

    private static SegAiClassTable Table(params (string Name, SegAiClassKind Kind)[] classes) =>
        new(classes.Select(c => new SegAiClassEntry(c.Name, c.Kind, true)).ToList());

    private static SegAiClassTable NgOkOnly => SegAiClassTable.Legacy();

    [Fact]
    public void RenameOneClass_RelabelsOnlyItsShapes_AndKeepsOtherKeysAndClasses()
    {
        var renames = SegClassRename.RenamesBetween(new[] { "NG", "OK", "Scratch" }, new[] { "Dent", "OK", "Scratch" });

        string? relabelled = SegClassRename.RelabelJson(Document, renames);

        Assert.NotNull(relabelled);
        JObject root = JObject.Parse(relabelled!);
        JArray shapes = (JArray)root["shapes"]!;
        Assert.Equal("Dent", (string?)shapes[0]["label"]);
        Assert.Equal("OK", (string?)shapes[1]["label"]);
        Assert.Equal("Scratch", (string?)shapes[2]["label"]);
        Assert.Equal("kept", (string?)shapes[0]["custom"]);
        Assert.Equal("[]", shapes[0]["easyai"]!["holes"]!.ToString(Newtonsoft.Json.Formatting.None));
        Assert.Equal("QUJD", (string?)root["imageData"]);
        Assert.Equal(1, (int?)root["unknownTop"]!["x"]);
        Assert.Equal(true, (bool?)root["flags"]!["train"]);
        Assert.Equal(3, shapes.Count);
    }

    [Fact]
    public void SwapOfTwoNames_IsOnePassAndDoesNotChain()
    {
        var renames = SegClassRename.RenamesBetween(new[] { "NG", "OK" }, new[] { "OK", "NG" });

        JObject root = JObject.Parse(SegClassRename.RelabelJson(Document, renames)!);
        JArray shapes = (JArray)root["shapes"]!;

        Assert.Equal("OK", (string?)shapes[0]["label"]);
        Assert.Equal("NG", (string?)shapes[1]["label"]);
        Assert.Equal("Scratch", (string?)shapes[2]["label"]);
    }

    [Fact]
    public void NoRename_ReturnsNullAndLeavesTheTextAlone()
    {
        var renames = SegClassRename.RenamesBetween(new[] { "NG", "OK" }, new[] { "NG", "OK" });

        Assert.Empty(renames);
        Assert.Null(SegClassRename.RelabelJson(Document, renames));
    }

    [Fact]
    public void SaveClassTable_RenamedClass_RewritesTheLabelMeJsonOnDisk()
    {
        using SegAiSession session = SegAiSession.Create(TrainFixtures.Folder("eai-cls-rename"));
        TrainFixtures.WriteImage(session.ProjectRoot, "a.png", 1, 64, 64);
        string jsonPath = Path.Combine(session.ProjectRoot, "a.json");
        File.WriteAllText(jsonPath, Document);

        session.SaveClassTable(Table(("Dent", SegAiClassKind.Defect), ("OK", SegAiClassKind.Good)));

        JObject root = JObject.Parse(File.ReadAllText(jsonPath));
        JArray shapes = (JArray)root["shapes"]!;
        Assert.Equal("Dent", (string?)shapes[0]["label"]);
        Assert.Equal("OK", (string?)shapes[1]["label"]);
        Assert.Equal("Scratch", (string?)shapes[2]["label"]);
        Assert.Equal("QUJD", (string?)root["imageData"]);
        Assert.Equal(1, (int?)root["unknownTop"]!["x"]);
        Assert.Equal(new[] { "Dent", "OK" }, session.ClassTable.Classes.Select(c => c.Name).ToArray());
    }

    [Fact]
    public void DecideClassPrediction_NgOkDefault_KeepsTwoClassPath_WithoutAModelTable()
    {
        Assert.False(SegAiSession.DecideClassPrediction(NgOkOnly, null));
        Assert.False(SegAiSession.DecideClassPrediction(NgOkOnly, SegAiClassTable.Legacy()));
    }

    [Fact]
    public void DecideClassPrediction_NgOkDefault_RejectsAModelWithMoreThanTwoClasses()
    {
        SegAiClassTable model = Table(("Scratch", SegAiClassKind.Defect), ("Dent", SegAiClassKind.Defect), ("Good", SegAiClassKind.Good));

        var error = Assert.Throws<InvalidOperationException>(() => { _ = SegAiSession.DecideClassPrediction(NgOkOnly, model); });

        Assert.Contains("3 classes", error.Message);
    }

    [Fact]
    public void DecideClassPrediction_OneNamedClass_UsesClassPrediction_AndChecksTheModel()
    {
        SegAiClassTable one = Table(("Scratch", SegAiClassKind.Defect));

        Assert.True(SegAiSession.DecideClassPrediction(one, one));
        Assert.Throws<InvalidOperationException>(() => { _ = SegAiSession.DecideClassPrediction(one, null); });
        Assert.Throws<InvalidOperationException>(() => { _ = SegAiSession.DecideClassPrediction(one, Table(("Dent", SegAiClassKind.Defect))); });
    }

    [Fact]
    public void DecideClassPrediction_TwoNamedClasses_WithRenamedOk_UsesClassPrediction_AndChecksTheModel()
    {
        SegAiClassTable renamed = Table(("NG", SegAiClassKind.Defect), ("Dent", SegAiClassKind.Good));

        Assert.False(SegAiSession.IsNgOkTable(renamed));
        Assert.True(SegAiSession.DecideClassPrediction(renamed, renamed));
        Assert.Throws<InvalidOperationException>(() => { _ = SegAiSession.DecideClassPrediction(renamed, NgOkOnly); });
        Assert.Throws<InvalidOperationException>(() => { _ = SegAiSession.DecideClassPrediction(renamed, null); });
    }

    [Fact]
    public void DecideClassPrediction_NgOkWithDisabledClass_IsNotTheDefault()
    {
        var disabledOk = new SegAiClassTable(new[]
        {
            new SegAiClassEntry("NG", SegAiClassKind.Defect, true),
            new SegAiClassEntry("OK", SegAiClassKind.Good, false),
        });

        Assert.False(SegAiSession.IsNgOkTable(disabledOk));
        Assert.True(SegAiSession.DecideClassPrediction(disabledOk, disabledOk));
    }
}
