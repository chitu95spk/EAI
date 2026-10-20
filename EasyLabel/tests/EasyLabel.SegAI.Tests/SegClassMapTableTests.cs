using EasyLabel.SegAI.Convert;
using EasyLabel.SegAI.Model;
using EasyLabel.SegAI.Project;

namespace EasyLabel.SegAI.Tests;

public sealed class SegClassMapTableTests
{
    private static SegAiClassTable Three() => new(new[]
    {
        new SegAiClassEntry("scratch", SegAiClassKind.Defect, true),
        new SegAiClassEntry("dent", SegAiClassKind.Defect, true),
        new SegAiClassEntry("good", SegAiClassKind.Good, true),
    });

    [Fact]
    public void Default_map_is_two_class_ng_ok()
    {
        var map = new SegClassMap();
        Assert.Equal(2, map.Count);
        Assert.Equal("NG", map.NameOf(1));
        Assert.Equal("OK", map.NameOf(2));
        Assert.Equal("", map.NameOf(3));
        Assert.Equal(SegAiClassKind.Defect, map.KindOf(1));
        Assert.Equal(SegAiClassKind.Good, map.KindOf(2));
        Assert.Equal(new[] { "NG", "OK" }, map.Table.Classes.Select(c => c.Name).ToArray());
    }

    [Fact]
    public void Table_map_uses_positions_as_ids()
    {
        var map = new SegClassMap(Three());
        Assert.Equal(3, map.Count);
        Assert.Equal((byte)2, map.LabelOf("Dent"));
        Assert.Null(map.LabelOf("NG"));
        Assert.Equal("good", map.NameOf(3));
        Assert.Equal(SegAiClassKind.Good, map.KindOf(3));
        Assert.Null(map.KindOf(4));
    }

    [Fact]
    public void EnsureClassesFile_writes_every_class_name()
    {
        string dir = Path.Combine(AppContext.BaseDirectory, "segmap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, "classes.txt");
            new SegClassMap(Three()).EnsureClassesFile(path);
            Assert.Equal(new[] { "scratch", "dent", "good" }, File.ReadAllLines(path));
        }
        finally { Directory.Delete(dir, true); }
    }
}
