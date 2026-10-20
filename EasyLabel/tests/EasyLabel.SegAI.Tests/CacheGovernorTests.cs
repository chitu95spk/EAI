using System.IO;
using EasyAI.Desktop.Infrastructure;

namespace EasyLabel.SegAI.Tests;

public sealed class CacheGovernorTests : IDisposable
{
    private readonly string _base;
    private readonly string _cache;

    public CacheGovernorTests()
    {
        _base = Path.Combine(AppContext.BaseDirectory, "cache-governor-" + Guid.NewGuid().ToString("N"));
        _cache = Path.Combine(_base, "cache");
        Directory.CreateDirectory(Path.Combine(_cache, "results", "autolabel"));
        Directory.CreateDirectory(Path.Combine(_cache, "results", "inspect"));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_base, true);
        }
        catch (IOException)
        {
        }
    }

    private string Write(string relative, int bytes, DateTime? utc = null)
    {
        string path = Path.Combine(_cache, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
        if (utc.HasValue) File.SetLastWriteTimeUtc(path, utc.Value);
        return path;
    }

    [Fact]
    public void MeasureCountsOnlyOwnedFiles()
    {
        Write("image-meta.json", 10);
        Write(Path.Combine("results", "autolabel", "a.json"), 100);
        Write(Path.Combine("results", "inspect", "b.json"), 1000);
        Write("foreign.dat", 5000);
        Write(Path.Combine("other", "x.bin"), 7000);

        Assert.Equal(1110, EasyLabel.Core.CacheGovernor.Measure(_cache));
    }

    [Fact]
    public void ClearDeletesOnlyOwnedFilesAndReportsBytes()
    {
        string meta = Write("image-meta.json", 10);
        string a = Write(Path.Combine("results", "autolabel", "a.json"), 100);
        string foreign = Write("foreign.dat", 5000);
        string foreignNested = Write(Path.Combine("other", "x.bin"), 7000);

        long freed = EasyLabel.Core.CacheGovernor.Clear(_cache);

        Assert.Equal(110, freed);
        Assert.False(File.Exists(meta));
        Assert.False(File.Exists(a));
        Assert.True(File.Exists(foreign));
        Assert.True(File.Exists(foreignNested));
        Assert.Equal(0, EasyLabel.Core.CacheGovernor.Measure(_cache));
    }

    [Fact]
    public void ClearOnMissingFilesFreesNothing()
    {
        Assert.Equal(0, EasyLabel.Core.CacheGovernor.Clear(_cache));
    }

    [Fact]
    public void TrimDeletesOldestFirstUntilUnderLimit()
    {
        DateTime now = DateTime.UtcNow;
        string meta = Write("image-meta.json", 10);
        string oldest = Write(Path.Combine("results", "autolabel", "1.json"), 100, now.AddHours(-3));
        string middle = Write(Path.Combine("results", "inspect", "2.json"), 100, now.AddHours(-2));
        string newest = Write(Path.Combine("results", "autolabel", "3.json"), 100, now.AddHours(-1));
        string foreign = Write("foreign.dat", 5000, now.AddDays(-9));

        long freed = EasyLabel.Core.CacheGovernor.Trim(_cache, 215);

        Assert.Equal(100, freed);
        Assert.False(File.Exists(oldest));
        Assert.True(File.Exists(middle));
        Assert.True(File.Exists(newest));
        Assert.True(File.Exists(meta));
        Assert.True(File.Exists(foreign));

        freed = EasyLabel.Core.CacheGovernor.Trim(_cache, 0);
        Assert.Equal(200, freed);
        Assert.True(File.Exists(meta));
        Assert.True(File.Exists(foreign));
    }

    [Fact]
    public void TrimUnderLimitDeletesNothing()
    {
        string a = Write(Path.Combine("results", "autolabel", "a.json"), 100);

        Assert.Equal(0, EasyLabel.Core.CacheGovernor.Trim(_cache, 100));
        Assert.True(File.Exists(a));
    }

    [Fact]
    public void GuardRefusesAFolderNotNamedCache()
    {
        string notCache = Path.Combine(_base, "store");
        string file = Path.Combine(notCache, "results", "autolabel", "a.json");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllBytes(file, new byte[100]);

        Assert.Equal(0, EasyLabel.Core.CacheGovernor.Measure(notCache));
        Assert.Equal(0, EasyLabel.Core.CacheGovernor.Clear(notCache));
        Assert.Equal(0, EasyLabel.Core.CacheGovernor.Trim(notCache, 0));
        Assert.True(File.Exists(file));
    }

    [Fact]
    public void GuardRefusesMissingOrEmptyFolder()
    {
        string missing = Path.Combine(_base, "none", "cache");

        Assert.Equal(0, EasyLabel.Core.CacheGovernor.Measure(missing));
        Assert.Equal(0, EasyLabel.Core.CacheGovernor.Clear(missing));
        Assert.Equal(0, EasyLabel.Core.CacheGovernor.Trim(missing, 0));
        Assert.Equal(0, EasyLabel.Core.CacheGovernor.Clear(null));
        Assert.Equal(0, EasyLabel.Core.CacheGovernor.Clear(string.Empty));
    }

    [Fact]
    public void FilesOutsideTheCacheFolderAreNeverTouched()
    {
        string sibling = Path.Combine(_base, "results", "autolabel", "a.json");
        Directory.CreateDirectory(Path.GetDirectoryName(sibling)!);
        File.WriteAllBytes(sibling, new byte[100]);
        string siblingMeta = Path.Combine(_base, "image-meta.json");
        File.WriteAllBytes(siblingMeta, new byte[10]);
        Write(Path.Combine("results", "autolabel", "in.json"), 50);

        EasyLabel.Core.CacheGovernor.Clear(_cache);
        EasyLabel.Core.CacheGovernor.Trim(_cache, 0);

        Assert.True(File.Exists(sibling));
        Assert.True(File.Exists(siblingMeta));
    }

    [Fact]
    public void LockedFileIsToleratedAndNotCountedAsFreed()
    {
        string locked = Write(Path.Combine("results", "autolabel", "locked.json"), 100);
        string free = Write(Path.Combine("results", "autolabel", "free.json"), 50);

        long freed;
        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            freed = EasyLabel.Core.CacheGovernor.Clear(_cache);
        }

        Assert.Equal(50, freed);
        Assert.True(File.Exists(locked));
        Assert.False(File.Exists(free));
    }
}

public sealed class CacheMaxSettingTests
{
    [Fact]
    public void DefaultIs2048()
    {
        Assert.Equal(2048, new AppSettings().CacheMaxMegabytes);
    }

    [Theory]
    [InlineData(0, 64)]
    [InlineData(-5, 64)]
    [InlineData(63, 64)]
    [InlineData(64, 64)]
    [InlineData(500, 500)]
    [InlineData(65536, 65536)]
    [InlineData(65537, 65536)]
    [InlineData(int.MaxValue, 65536)]
    public void ReadIsClamped(int stored, int expected)
    {
        AppSettings settings = new AppSettings { CacheMaxMegabytes = stored };

        Assert.Equal(expected, settings.CacheMaxMegabytes);
    }

    [Fact]
    public void RowTextRoundTripsAndClamps()
    {
        AppSettings settings = new AppSettings();

        Assert.True(SettingsAccessor.TrySetString(settings, SettingsAccessor.RowCacheMax, "4096"));
        Assert.True(SettingsAccessor.TryGetString(settings, SettingsAccessor.RowCacheMax, out string text));
        Assert.Equal("4096", text);

        Assert.True(SettingsAccessor.TrySetString(settings, SettingsAccessor.RowCacheMax, "1"));
        Assert.Equal(64, settings.CacheMaxMegabytes);
        Assert.True(SettingsAccessor.TrySetString(settings, SettingsAccessor.RowCacheMax, "999999"));
        Assert.Equal(65536, settings.CacheMaxMegabytes);

        Assert.False(SettingsAccessor.TrySetString(settings, SettingsAccessor.RowCacheMax, "abc"));
        Assert.Equal(65536, settings.CacheMaxMegabytes);
    }
}
