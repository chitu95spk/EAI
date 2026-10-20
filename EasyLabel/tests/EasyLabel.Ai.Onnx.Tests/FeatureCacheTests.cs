using EasyLabel.Ai.Onnx.Classify;

namespace EasyLabel.Ai.Onnx.Tests;

public sealed class FeatureCacheTests : IDisposable
{
    private const string Sha = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private readonly string _root = Path.Combine(AppContext.BaseDirectory, "feature-cache-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private static float[] Features()
    {
        var values = new float[ClassifyBackbone.FeatureCount];
        for (int i = 0; i < values.Length; i++) values[i] = i * 0.25f - 100f;
        return values;
    }

    [Fact]
    public void Put_then_get_returns_the_same_values()
    {
        var cache = new FeatureCache(_root);
        string key = FeatureCache.MakeKey("C:/img/a.png", 1234, new DateTime(2026, 10, 12, 0, 0, 0, DateTimeKind.Utc), Sha, 448);
        float[] written = Features();

        cache.Put(key, written);

        Assert.True(cache.TryGet(key, out float[]? read));
        Assert.Equal(written, read);
    }

    [Fact]
    public void Edited_file_backbone_or_long_side_are_misses()
    {
        var cache = new FeatureCache(_root);
        DateTime time = new DateTime(2026, 10, 12, 0, 0, 0, DateTimeKind.Utc);
        string key = FeatureCache.MakeKey("C:/img/a.png", 1234, time, Sha, 448);
        cache.Put(key, Features());

        Assert.False(cache.TryGet(FeatureCache.MakeKey("C:/img/a.png", 1235, time, Sha, 448), out _));
        Assert.False(cache.TryGet(FeatureCache.MakeKey("C:/img/a.png", 1234, time.AddSeconds(1), Sha, 448), out _));
        Assert.False(cache.TryGet(FeatureCache.MakeKey("C:/img/a.png", 1234, time, new string('f', 64), 448), out _));
        Assert.False(cache.TryGet(FeatureCache.MakeKey("C:/img/a.png", 1234, time, Sha, 640), out _));
    }

    [Fact]
    public void Wrong_length_features_are_refused_on_write()
    {
        var cache = new FeatureCache(_root);
        Assert.Throws<ArgumentException>(() => cache.Put("k", new float[3]));
    }

    [Fact]
    public void Null_folder_is_a_disabled_cache()
    {
        var cache = new FeatureCache(null);
        Assert.False(cache.IsEnabled);
        cache.Put("k", Features());
        Assert.False(cache.TryGet("k", out _));
    }
}
