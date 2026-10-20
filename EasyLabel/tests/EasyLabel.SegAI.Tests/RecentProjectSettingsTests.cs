using System.Text.Json;
using EasyAI.Desktop.Infrastructure;
using EasyLabel.Core.Projects;

namespace EasyLabel.SegAI.Tests;

public sealed class RecentProjectSettingsTests
{
    private static readonly DateTime Opened = new DateTime(2026, 10, 1, 8, 30, 15, 123, DateTimeKind.Utc);

    [Fact]
    public void NewSettingsHaveAnEmptyRecentList()
    {
        Assert.NotNull(new AppSettings().RecentProjects);
        Assert.Empty(new AppSettings().RecentProjects);
    }

    [Fact]
    public void OldSettingsFileWithoutRecentProjectsLoadsWithAnEmptyList()
    {
        AppSettings old = JsonSerializer.Deserialize<AppSettings>("{\"SchemaVersion\":1,\"Theme\":\"dark\"}")!;

        Assert.Equal("dark", old.Theme);
        Assert.Empty(old.RecentProjects);
        Assert.Empty(RecentProjectSetting.ToEntries(old.RecentProjects));
    }

    [Fact]
    public void NullRecentProjectsInTheFileIsTolerated()
    {
        AppSettings loaded = JsonSerializer.Deserialize<AppSettings>("{\"RecentProjects\":null}")!;

        Assert.Empty(RecentProjectSetting.ToEntries(loaded.RecentProjects));
    }

    [Fact]
    public void RoundTripKeepsPathNameTimeAndKind()
    {
        string manifest = Path.Combine(Path.GetTempPath(), "easyai-recent", "Line 1.easyai");
        string folder = Path.Combine(Path.GetTempPath(), "easyai-recent", "images");
        var entries = new List<RecentProject>
        {
            new RecentProject(manifest, "Line 1", Opened, RecentProjectKind.Named),
            new RecentProject(folder, "images", Opened.AddHours(-2), RecentProjectKind.Folder),
        };
        AppSettings settings = new AppSettings();
        settings.RecentProjects = RecentProjectSetting.FromEntries(entries);

        string json = JsonSerializer.Serialize(settings);
        AppSettings loaded = JsonSerializer.Deserialize<AppSettings>(json)!;
        IReadOnlyList<RecentProject> back = RecentProjectSetting.ToEntries(loaded.RecentProjects);

        Assert.Equal(entries, back);
        Assert.Contains("\"Kind\":\"named\"", json);
        Assert.Contains("\"Kind\":\"folder\"", json);
        Assert.Contains("2026-10-01T08:30:15.123", json);
    }

    [Fact]
    public void DamagedEntriesAreRepairedOrDroppedWithoutFailingTheLoad()
    {
        string json = "{\"RecentProjects\":["
            + "{\"Path\":\"\",\"Name\":\"no path\"},"
            + "{\"Path\":\"C:\\\\data\\\\a.easyai\",\"Name\":\"A\",\"LastOpenedUtc\":\"not a date\",\"Kind\":\"???\"},"
            + "{\"Path\":\"C:\\\\data\\\\imgs\",\"Kind\":\"FOLDER\"},"
            + "{\"Path\":\"c:\\\\DATA\\\\A.easyai\\\\\",\"Name\":\"duplicate\"}"
            + "]}";

        AppSettings loaded = JsonSerializer.Deserialize<AppSettings>(json)!;
        IReadOnlyList<RecentProject> entries = RecentProjectSetting.ToEntries(loaded.RecentProjects);

        Assert.Equal(2, entries.Count);
        Assert.Equal("A", entries[0].Name);
        Assert.Equal(DateTime.MinValue, entries[0].LastOpenedUtc);
        Assert.Equal(RecentProjectKind.Named, entries[0].Kind);
        Assert.Equal(RecentProjectKind.Folder, entries[1].Kind);
        Assert.Equal("imgs", entries[1].Name);
    }

    [Fact]
    public void FromEntriesWritesUtcTime()
    {
        var local = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc).ToLocalTime();

        RecentProjectSetting setting = RecentProjectSetting.FromEntry(new RecentProject("C:\\x.easyai", "x", local, RecentProjectKind.Named));

        Assert.Equal("2026-10-01T12:00:00.0000000Z", setting.LastOpenedUtc);
    }
}
