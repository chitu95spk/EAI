namespace EasyLabel.SegAI.Tests;

public sealed class ImageMetaCacheTests : System.IDisposable
{
    private readonly string _dir;

    public ImageMetaCacheTests()
    {
        string root = System.IO.Path.Combine(System.AppContext.BaseDirectory, "imc-" + System.Guid.NewGuid().ToString("N"));
        _dir = System.IO.Path.Combine(root, "cache");
    }

    public void Dispose()
    {
        string? root = System.IO.Path.GetDirectoryName(_dir);
        if (root != null && System.IO.Directory.Exists(root)) System.IO.Directory.Delete(root, true);
    }

    private static int _decodes;

    private static System.Func<(int Width, int Height)?> Decoder(int w, int h)
    {
        return () =>
        {
            _decodes++;
            return (w, h);
        };
    }

    private string Cache => System.IO.Path.Combine(_dir, EasyLabel.SegAI.Project.ImageMetaCache.FileName);

    [Fact]
    public void Hit_DoesNotDecodeAgain()
    {
        _decodes = 0;
        using var cache = new EasyLabel.SegAI.Project.ImageMetaCache(_dir, 0);
        Assert.True(cache.GetOrDecode("a.png", 100, 5, Decoder(640, 480), out int w, out int h));
        Assert.True(cache.GetOrDecode("A.PNG", 100, 5, Decoder(1, 1), out w, out h));
        Assert.Equal(1, _decodes);
        Assert.Equal(640, w);
        Assert.Equal(480, h);
    }

    [Fact]
    public void StaleByLength_Redecodes()
    {
        _decodes = 0;
        using var cache = new EasyLabel.SegAI.Project.ImageMetaCache(_dir, 0);
        cache.GetOrDecode("a.png", 100, 5, Decoder(640, 480), out _, out _);
        Assert.True(cache.GetOrDecode("a.png", 101, 5, Decoder(800, 600), out int w, out int h));
        Assert.Equal(2, _decodes);
        Assert.Equal(800, w);
        Assert.Equal(600, h);
    }

    [Fact]
    public void StaleByTicks_Redecodes()
    {
        _decodes = 0;
        using var cache = new EasyLabel.SegAI.Project.ImageMetaCache(_dir, 0);
        cache.GetOrDecode("a.png", 100, 5, Decoder(640, 480), out _, out _);
        Assert.True(cache.GetOrDecode("a.png", 100, 6, Decoder(320, 240), out int w, out int h));
        Assert.Equal(2, _decodes);
        Assert.Equal(320, w);
        Assert.Equal(240, h);
    }

    [Fact]
    public void FailedDecode_IsNotStored()
    {
        using var cache = new EasyLabel.SegAI.Project.ImageMetaCache(_dir, 0);
        Assert.False(cache.GetOrDecode("bad.png", 1, 1, () => null, out int w, out int h));
        Assert.Equal(0, w);
        Assert.Equal(0, h);
        Assert.False(cache.TryGet("bad.png", 1, 1, out _, out _));
    }

    [Fact]
    public void CorruptFile_IsIgnored()
    {
        System.IO.Directory.CreateDirectory(_dir);
        System.IO.File.WriteAllText(Cache, "{ not json <<<");
        using var cache = new EasyLabel.SegAI.Project.ImageMetaCache(_dir, 0);
        Assert.False(cache.TryGet("a.png", 100, 5, out _, out _));
        Assert.True(cache.GetOrDecode("a.png", 100, 5, Decoder(10, 20), out int w, out int h));
        Assert.Equal(10, w);
        Assert.Equal(20, h);
    }

    [Fact]
    public void WrongVersion_IsIgnored()
    {
        System.IO.Directory.CreateDirectory(_dir);
        System.IO.File.WriteAllText(Cache,
            "{\"Version\":999,\"Entries\":{\"a.png\":{\"Length\":100,\"WriteTicks\":5,\"Width\":640,\"Height\":480}}}");
        using var cache = new EasyLabel.SegAI.Project.ImageMetaCache(_dir, 0);
        Assert.False(cache.TryGet("a.png", 100, 5, out _, out _));
    }

    [Fact]
    public void Flush_WritesAtomically_LeavesNoTempFile()
    {
        using (var cache = new EasyLabel.SegAI.Project.ImageMetaCache(_dir, 60000))
        {
            cache.Put("a.png", 100, 5, 640, 480);
            cache.Put("b.png", 200, 6, 320, 240);
            cache.Flush();
            Assert.True(System.IO.File.Exists(Cache));
        }

        string[] files = System.IO.Directory.GetFiles(_dir);
        Assert.Single(files);
        Assert.Equal(Cache, files[0]);
    }

    [Fact]
    public void Dispose_FlushesPendingEntries()
    {
        var cache = new EasyLabel.SegAI.Project.ImageMetaCache(_dir, 60000);
        cache.Put("a.png", 100, 5, 640, 480);
        Assert.False(System.IO.File.Exists(Cache));
        cache.Dispose();
        Assert.True(System.IO.File.Exists(Cache));
        Assert.Single(System.IO.Directory.GetFiles(_dir));
    }

    [Fact]
    public void SecondSession_ReadsPersistedFile_WithoutDecoding()
    {
        using (var first = new EasyLabel.SegAI.Project.ImageMetaCache(_dir, 60000))
        {
            first.GetOrDecode("sub/a.png", 100, 5, Decoder(640, 480), out _, out _);
        }

        _decodes = 0;
        using var second = new EasyLabel.SegAI.Project.ImageMetaCache(_dir, 60000);
        Assert.True(second.GetOrDecode("sub/a.png", 100, 5, Decoder(1, 1), out int w, out int h));
        Assert.Equal(0, _decodes);
        Assert.Equal(640, w);
        Assert.Equal(480, h);
    }

    [Fact]
    public void SecondSession_StaleEntry_Redecodes()
    {
        using (var first = new EasyLabel.SegAI.Project.ImageMetaCache(_dir, 60000))
        {
            first.GetOrDecode("a.png", 100, 5, Decoder(640, 480), out _, out _);
        }

        _decodes = 0;
        using var second = new EasyLabel.SegAI.Project.ImageMetaCache(_dir, 60000);
        Assert.True(second.GetOrDecode("a.png", 999, 5, Decoder(50, 60), out int w, out int h));
        Assert.Equal(1, _decodes);
        Assert.Equal(50, w);
        Assert.Equal(60, h);
    }
}
