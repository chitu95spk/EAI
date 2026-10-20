using EasyLabel.Core.Projects;

namespace EasyLabel.Core.Tests;

public sealed class RecentProjectsTests
{
    private static readonly DateTime T0 = new DateTime(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc);

    private static string P(string name) => Path.Combine(Path.GetTempPath(), "easyai-recent", name);

    private static RecentProject Entry(string path, string name = "n", int minutes = 0, RecentProjectKind kind = RecentProjectKind.Named)
        => new RecentProject(path, name, T0.AddMinutes(minutes), kind);

    private static IReadOnlyList<RecentProject> Empty => Array.Empty<RecentProject>();

    [Fact]
    public void Add_PutsNewestFirst()
    {
        var list = RecentProjectList.Add(RecentProjectList.Add(Empty, Entry(P("a.easyai"), "A")), Entry(P("b.easyai"), "B"));

        Assert.Equal(new[] { "B", "A" }, list.Select(e => e.Name).ToArray());
    }

    [Fact]
    public void Add_DoesNotChangeTheInputList()
    {
        IReadOnlyList<RecentProject> original = RecentProjectList.Add(Empty, Entry(P("a.easyai"), "A"));

        RecentProjectList.Add(original, Entry(P("b.easyai"), "B"));

        Assert.Single(original);
    }

    [Fact]
    public void Add_SamePathMovesToTopAndUpdatesNameAndTime()
    {
        var list = RecentProjectList.Add(Empty, Entry(P("a.easyai"), "A", 0));
        list = RecentProjectList.Add(list, Entry(P("b.easyai"), "B", 1));

        list = RecentProjectList.Add(list, Entry(P("a.easyai"), "A renamed", 5));

        Assert.Equal(2, list.Count);
        Assert.Equal("A renamed", list[0].Name);
        Assert.Equal(T0.AddMinutes(5), list[0].LastOpenedUtc);
        Assert.Equal("B", list[1].Name);
    }

    [Fact]
    public void Add_IsCaseInsensitive()
    {
        var list = RecentProjectList.Add(Empty, Entry(P("Line1.easyai"), "x"));

        list = RecentProjectList.Add(list, Entry(P("LINE1.EASYAI"), "y"));

        Assert.Single(list);
        Assert.Equal("y", list[0].Name);
    }

    [Fact]
    public void Add_IgnoresTrailingSeparators()
    {
        string folder = P("images");
        var list = RecentProjectList.Add(Empty, Entry(folder, "x", 0, RecentProjectKind.Folder));

        list = RecentProjectList.Add(list, Entry(folder + Path.DirectorySeparatorChar, "y", 1, RecentProjectKind.Folder));
        list = RecentProjectList.Add(list, Entry(folder + Path.AltDirectorySeparatorChar, "z", 2, RecentProjectKind.Folder));

        Assert.Single(list);
        Assert.Equal("z", list[0].Name);
        Assert.Equal(folder, list[0].Path);
    }

    [Fact]
    public void Add_CapsAtMaxAndDropsTheOldest()
    {
        IReadOnlyList<RecentProject> list = Empty;
        for (int i = 0; i < 14; i++)
        {
            list = RecentProjectList.Add(list, Entry(P("p" + i + ".easyai"), "P" + i, i));
        }

        Assert.Equal(RecentProjectList.Max, list.Count);
        Assert.Equal(10, RecentProjectList.Max);
        Assert.Equal("P13", list[0].Name);
        Assert.Equal("P4", list[9].Name);
    }

    [Fact]
    public void Add_ExistingEntryWhenFullKeepsTheOthers()
    {
        IReadOnlyList<RecentProject> list = Empty;
        for (int i = 0; i < 10; i++)
        {
            list = RecentProjectList.Add(list, Entry(P("p" + i + ".easyai"), "P" + i, i));
        }

        list = RecentProjectList.Add(list, Entry(P("p0.easyai"), "P0", 99));

        Assert.Equal(10, list.Count);
        Assert.Equal("P0", list[0].Name);
        Assert.Equal("P1", list[9].Name);
    }

    [Fact]
    public void Add_BlankNameIsDerivedFromThePath()
    {
        var named = RecentProjectList.Add(Empty, Entry(P("Line 1.easyai"), "  ", 0, RecentProjectKind.Named));
        var folder = RecentProjectList.Add(Empty, Entry(P("shots.v2"), "", 0, RecentProjectKind.Folder));

        Assert.Equal("Line 1", named[0].Name);
        Assert.Equal("shots.v2", folder[0].Name);
    }

    [Fact]
    public void Add_EmptyPathIsIgnored()
    {
        var list = RecentProjectList.Add(Empty, Entry(P("a.easyai"), "A"));

        Assert.Single(RecentProjectList.Add(list, Entry("  ", "B")));
        Assert.Single(RecentProjectList.Add(list, Entry("", "B")));
    }

    [Fact]
    public void Add_ToNullListWorks()
    {
        var list = RecentProjectList.Add(null, Entry(P("a.easyai"), "A"));

        Assert.Single(list);
    }

    [Fact]
    public void Remove_DropsOnlyThatPathAndIgnoresCaseAndSeparator()
    {
        var list = RecentProjectList.Add(Empty, Entry(P("a.easyai"), "A"));
        list = RecentProjectList.Add(list, Entry(P("images"), "I", 1, RecentProjectKind.Folder));

        list = RecentProjectList.Remove(list, P("IMAGES") + Path.DirectorySeparatorChar);

        Assert.Equal(new[] { "A" }, list.Select(e => e.Name).ToArray());
        Assert.Single(RecentProjectList.Remove(list, P("missing.easyai")));
        Assert.Single(RecentProjectList.Remove(list, null));
        Assert.Empty(RecentProjectList.Remove(null, P("a.easyai")));
    }

    [Fact]
    public void Clear_GivesAnEmptyList()
    {
        Assert.Empty(RecentProjectList.Clear());
    }

    [Fact]
    public void MarkMissing_PairsEveryEntryWithItsExistence_AndKeepsOrder()
    {
        var list = RecentProjectList.Add(Empty, Entry(P("a.easyai"), "A"));
        list = RecentProjectList.Add(list, Entry(P("b.easyai"), "B", 1));
        list = RecentProjectList.Add(list, Entry(P("c.easyai"), "C", 2));
        string missing = RecentProjectList.NormalizePath(P("b.easyai"));

        var statuses = RecentProjectList.MarkMissing(list, path => !string.Equals(path, missing, StringComparison.OrdinalIgnoreCase));

        Assert.Equal(new[] { "C", "B", "A" }, statuses.Select(s => s.Project.Name).ToArray());
        Assert.Equal(new[] { true, false, true }, statuses.Select(s => s.Exists).ToArray());
        Assert.Equal(3, list.Count);
    }

    [Fact]
    public void MarkMissing_ThrowingExistsTestCountsAsMissing()
    {
        var list = RecentProjectList.Add(Empty, Entry(P("a.easyai"), "A"));

        var statuses = RecentProjectList.MarkMissing(list, _ => throw new IOException("offline share"));

        Assert.False(statuses[0].Exists);
    }

    [Fact]
    public void MarkMissing_NullListIsEmpty()
    {
        Assert.Empty(RecentProjectList.MarkMissing(null, _ => true));
    }

    [Fact]
    public void Normalize_ToleratesNullAndEmpty()
    {
        Assert.Empty(RecentProjectList.Normalize(null));
        Assert.Empty(RecentProjectList.Normalize(Empty));
    }

    [Fact]
    public void Normalize_DropsNullEntriesAndEmptyPaths()
    {
        var raw = new RecentProject?[] { null, Entry("", "x"), Entry("   ", "y"), Entry(P("a.easyai"), "A") };

        var list = RecentProjectList.Normalize(raw!);

        Assert.Equal(new[] { "A" }, list.Select(e => e.Name).ToArray());
    }

    [Fact]
    public void Normalize_KeepsTheFirstOfDuplicates()
    {
        var raw = new[]
        {
            Entry(P("a.easyai"), "first", 5),
            Entry(P("b.easyai"), "B", 4),
            Entry(P("A.EASYAI"), "second", 3),
            Entry(P("b.easyai") + Path.DirectorySeparatorChar, "third", 2),
        };

        var list = RecentProjectList.Normalize(raw);

        Assert.Equal(new[] { "first", "B" }, list.Select(e => e.Name).ToArray());
    }

    [Fact]
    public void Normalize_CapsAtMax()
    {
        var raw = Enumerable.Range(0, 25).Select(i => Entry(P("p" + i + ".easyai"), "P" + i, -i)).ToArray();

        var list = RecentProjectList.Normalize(raw);

        Assert.Equal(RecentProjectList.Max, list.Count);
        Assert.Equal("P0", list[0].Name);
        Assert.Equal("P9", list[9].Name);
    }

    [Fact]
    public void Normalize_IsIdempotent()
    {
        var raw = new[] { Entry(P("a.easyai"), "A"), Entry(P("a.easyai"), "A2"), Entry(P("b.easyai") + Path.DirectorySeparatorChar, "") };

        var once = RecentProjectList.Normalize(raw);
        var twice = RecentProjectList.Normalize(once);

        Assert.Equal(once, twice);
    }
}
