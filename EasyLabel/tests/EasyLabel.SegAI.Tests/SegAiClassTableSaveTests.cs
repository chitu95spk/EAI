using EasyLabel.SegAI.Convert;
using EasyLabel.SegAI.Data;
using EasyLabel.SegAI.Model;
using EasyLabel.SegAI.Project;
using OpenCvSharp;
using PointF = System.Drawing.PointF;

namespace EasyLabel.SegAI.Tests;

public sealed class SegAiClassTableSaveTests
{
    // "dent" is disabled so the round trip also covers the enabled flag.
    private static SegAiClassTable Three() => new(new[]
    {
        new SegAiClassEntry("scratch", SegAiClassKind.Defect, true),
        new SegAiClassEntry("dent", SegAiClassKind.Defect, false),
        new SegAiClassEntry("good", SegAiClassKind.Good, true),
    });

    private static SegAiClassTable WithPit() => new(new[]
    {
        new SegAiClassEntry("scratch", SegAiClassKind.Defect, true),
        new SegAiClassEntry("pit", SegAiClassKind.Defect, true),
        new SegAiClassEntry("good", SegAiClassKind.Good, true),
    });

    private static string NewFolder(string name)
    {
        string dir = Path.Combine(SegAiFixtures.ScratchFolder("classtable"), name + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string AdvancedJson(string root) => Path.Combine(new SegProjectStore(root).EasyAiDir, "advanced.json");

    private static PointF[] Box(float x0, float y0, float x1, float y1)
    {
        return new[] { new PointF(x0, y0), new PointF(x1, y0), new PointF(x1, y1), new PointF(x0, y1) };
    }

    private static byte[] TinyPng()
    {
        using var image = new Mat(64, 64, MatType.CV_8UC3, new Scalar(30, 60, 90));
        Assert.True(Cv2.ImEncode(".png", image, out byte[] png));
        return png;
    }

    // One image labelled with class id 1; the bytes are PNG, as in SegAiSessionTests.
    private static EvDatasetEntry OneEntry() => new("img0.bmp", null, null, TinyPng(), null,
        new List<SegAnnotation> { new SegAnnotation { Label = 1, Points = Box(4, 4, 30, 30) } }, (byte)0);

    [Fact]
    public void SaveThenReopen_RoundTripsNamesKindsEnabledAndIds()
    {
        string root = NewFolder("roundtrip");
        using (var session = SegAiSession.Create(root))
        {
            session.SaveClassTable(Three());
            Assert.Equal(new[] { "scratch", "dent", "good" }, session.ClassNames);
        }

        using var reopened = SegAiSession.Open(root);
        Assert.Equal(new[] { "scratch", "dent", "good" }, reopened.ClassNames);
        Assert.Equal(Three().Classes, reopened.ClassTable.Classes);
        // classes.txt is append-only: the project started as NG/OK, so the new names follow those lines.
        string[] lines = File.ReadAllLines(Path.Combine(root, "classes.txt"));
        Assert.All(new[] { "scratch", "dent", "good" }, n => Assert.Contains(n, lines));

        SegClassMap map = new SegProjectStore(root).LoadClassMap();
        Assert.True(map.HasNamedTable);
        Assert.Equal((byte)1, map.LabelOf("scratch"));
        Assert.Equal((byte)2, map.LabelOf("dent"));
        Assert.Equal((byte)3, map.LabelOf("good"));
    }

    [Fact]
    public void InvalidTable_IsRejected_AndNothingIsWritten()
    {
        string root = NewFolder("invalid");
        using var session = SegAiSession.Create(root);
        session.SaveClassTable(Three());
        string before = File.ReadAllText(AdvancedJson(root));

        var invalid = new List<SegAiClassTable>
        {
            new SegAiClassTable(Array.Empty<SegAiClassEntry>()),
            new SegAiClassTable(Enumerable.Range(0, SegAiClassTable.MaxClasses + 1).Select(i => new SegAiClassEntry("c" + i, SegAiClassKind.Defect, true)).ToArray()),
            new SegAiClassTable(new[] { new SegAiClassEntry("a", SegAiClassKind.Defect, true), new SegAiClassEntry("a", SegAiClassKind.Defect, true) }),
            new SegAiClassTable(new[] { new SegAiClassEntry("g1", SegAiClassKind.Good, true), new SegAiClassEntry("g2", SegAiClassKind.Good, true) }),
            new SegAiClassTable(new[] { new SegAiClassEntry("a", SegAiClassKind.Defect, false) }),
            new SegAiClassTable(new[] { new SegAiClassEntry(new string('x', SegAiClassTable.MaxNameBytes + 1), SegAiClassKind.Defect, true) }),
            new SegAiClassTable(new[] { new SegAiClassEntry("", SegAiClassKind.Defect, true) }),
            new SegAiClassTable(new[] { new SegAiClassEntry("Dent", SegAiClassKind.Defect, true), new SegAiClassEntry("dent", SegAiClassKind.Defect, true) }),
            new SegAiClassTable(new[] { new SegAiClassEntry(" dent", SegAiClassKind.Defect, true) }),
        };
        foreach (SegAiClassTable table in invalid)
            Assert.Throws<ArgumentException>(() => session.SaveClassTable(table));

        Assert.Equal(before, File.ReadAllText(AdvancedJson(root)));
        Assert.Equal(new[] { "scratch", "dent", "good" }, session.ClassNames);

        // A fresh project with a rejected table gets no advanced.json and keeps the NG/OK map.
        string fresh = NewFolder("invalid-fresh");
        using var freshSession = SegAiSession.Create(fresh);
        Assert.Throws<ArgumentException>(() => freshSession.SaveClassTable(invalid[0]));
        Assert.False(File.Exists(AdvancedJson(fresh)));
        Assert.Equal(new[] { "NG", "OK" }, freshSession.ClassNames);
    }

    [Fact]
    public void DefaultTwoClassProject_StaysUntouchedUntilATableIsSaved()
    {
        string root = NewFolder("default");
        using (var session = SegAiSession.Create(root))
        {
            Assert.False(File.Exists(AdvancedJson(root)));
            Assert.Equal(new[] { "NG", "OK" }, session.ClassNames);
            Assert.Equal(new[] { "NG", "OK" }, File.ReadAllLines(Path.Combine(root, "classes.txt")));
        }

        // Without a table the dataset carries no class table, and the NG/OK project still accepts it.
        string work = NewFolder("default-work");
        string zip = Path.Combine(work, "plain.zip");
        EasyVisionDataset.WriteZip(zip, new[] { OneEntry() });
        using (var session = SegAiSession.Open(root))
            Assert.Equal(1, session.ImportEasyVisionDataset(zip).Imported);
        Assert.False(File.Exists(AdvancedJson(root)));

        using (var session = SegAiSession.Open(root))
        {
            session.SaveClassTable(Three());
            Assert.True(File.Exists(AdvancedJson(root)));
            Assert.Equal(3, session.ClassNames.Count);
        }
    }

    [Fact]
    public void ChangingTheTableAfterExport_MakesTheDatasetMismatch()
    {
        string root = NewFolder("mismatch");
        string work = NewFolder("mismatch-work");
        string oldZip = Path.Combine(work, "old.zip");
        string plainZip = Path.Combine(work, "plain.zip");
        using var session = SegAiSession.Create(root);
        session.SaveClassTable(Three());
        EasyVisionDataset.WriteZip(oldZip, new[] { OneEntry() }, session.ClassTable);
        EasyVisionDataset.WriteZip(plainZip, new[] { OneEntry() }, null);

        // The same table is accepted.
        Assert.Equal(1, session.ImportEasyVisionDataset(oldZip).Imported);

        // After the table changes, the dataset written with the old table is rejected, and so is one without a table.
        session.SaveClassTable(WithPit());
        Assert.Throws<InvalidDataException>(() => session.ImportEasyVisionDataset(oldZip));
        Assert.Throws<InvalidDataException>(() => session.ImportEasyVisionDataset(plainZip));
    }
}
