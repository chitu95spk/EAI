namespace EasyLabel.SegAI.Tests;

public sealed class PreviewCacheLruTests
{
    private sealed class Item
    {
    }

    [Xunit.Fact]
    public void HitAndMiss()
    {
        EasyAI.Desktop.Services.PreviewCacheLru<Item> lru = new EasyAI.Desktop.Services.PreviewCacheLru<Item>(1000, 10);
        Item a = new Item();
        Xunit.Assert.False(lru.TryGet("a", out Item? miss));
        Xunit.Assert.Null(miss);
        lru.Put("a", a, 100);
        Xunit.Assert.True(lru.TryGet("a", out Item? hit));
        Xunit.Assert.Same(a, hit);
        Xunit.Assert.Equal(1, lru.Count);
        Xunit.Assert.Equal(100, lru.TotalBytes);
    }

    [Xunit.Fact]
    public void TryGetRefreshesLruOrder()
    {
        EasyAI.Desktop.Services.PreviewCacheLru<Item> lru = new EasyAI.Desktop.Services.PreviewCacheLru<Item>(1000, 2);
        lru.Put("a", new Item(), 10);
        lru.Put("b", new Item(), 10);
        Xunit.Assert.True(lru.TryGet("a", out Item? _)); // a is now most recent; b is the LRU.
        lru.Put("c", new Item(), 10);
        Xunit.Assert.True(lru.TryGet("a", out Item? _));
        Xunit.Assert.False(lru.TryGet("b", out Item? _));
        Xunit.Assert.True(lru.TryGet("c", out Item? _));
    }

    [Xunit.Fact]
    public void EvictsByBytes()
    {
        EasyAI.Desktop.Services.PreviewCacheLru<Item> lru = new EasyAI.Desktop.Services.PreviewCacheLru<Item>(100, 10);
        lru.Put("a", new Item(), 40);
        lru.Put("b", new Item(), 40);
        lru.Put("c", new Item(), 40); // 120 > 100: a evicted.
        Xunit.Assert.False(lru.TryGet("a", out Item? _));
        Xunit.Assert.True(lru.TryGet("b", out Item? _));
        Xunit.Assert.True(lru.TryGet("c", out Item? _));
        Xunit.Assert.Equal(80, lru.TotalBytes);
        Xunit.Assert.Equal(2, lru.Count);
    }

    [Xunit.Fact]
    public void EvictsByCount()
    {
        EasyAI.Desktop.Services.PreviewCacheLru<Item> lru = new EasyAI.Desktop.Services.PreviewCacheLru<Item>(1000000, 3);
        for (int i = 0; i < 5; i++)
        {
            lru.Put("k" + i, new Item(), 1);
        }

        Xunit.Assert.Equal(3, lru.Count);
        Xunit.Assert.Equal(3, lru.TotalBytes);
        Xunit.Assert.False(lru.TryGet("k0", out Item? _));
        Xunit.Assert.False(lru.TryGet("k1", out Item? _));
        Xunit.Assert.True(lru.TryGet("k4", out Item? _));
    }

    [Xunit.Fact]
    public void OversizedItemIsNotStored()
    {
        EasyAI.Desktop.Services.PreviewCacheLru<Item> lru = new EasyAI.Desktop.Services.PreviewCacheLru<Item>(100, 10);
        lru.Put("small", new Item(), 50);
        lru.Put("big", new Item(), 101);
        Xunit.Assert.False(lru.TryGet("big", out Item? _));
        Xunit.Assert.True(lru.TryGet("small", out Item? _));
        Xunit.Assert.Equal(50, lru.TotalBytes);
    }

    [Xunit.Fact]
    public void PutSameKeyReplacesAndAdjustsBytes()
    {
        EasyAI.Desktop.Services.PreviewCacheLru<Item> lru = new EasyAI.Desktop.Services.PreviewCacheLru<Item>(100, 10);
        lru.Put("a", new Item(), 30);
        Item b = new Item();
        lru.Put("a", b, 20);
        Xunit.Assert.Equal(1, lru.Count);
        Xunit.Assert.Equal(20, lru.TotalBytes);
        Xunit.Assert.True(lru.TryGet("a", out Item? got));
        Xunit.Assert.Same(b, got);
    }

    [Xunit.Fact]
    public void ClearEmptiesCache()
    {
        EasyAI.Desktop.Services.PreviewCacheLru<Item> lru = new EasyAI.Desktop.Services.PreviewCacheLru<Item>(100, 10);
        lru.Put("a", new Item(), 10);
        lru.Put("b", new Item(), 10);
        lru.Clear();
        Xunit.Assert.Equal(0, lru.Count);
        Xunit.Assert.Equal(0, lru.TotalBytes);
        Xunit.Assert.False(lru.TryGet("a", out Item? _));
    }

    [Xunit.Fact]
    public void ParallelPutsKeepTotalsConsistent()
    {
        EasyAI.Desktop.Services.PreviewCacheLru<Item> lru = new EasyAI.Desktop.Services.PreviewCacheLru<Item>(500, 20);
        System.Threading.Tasks.Parallel.For(0, 2000, i =>
        {
            lru.Put("k" + (i % 50), new Item(), 10 + (i % 7));
            lru.TryGet("k" + ((i + 3) % 50), out Item? _);
        });

        Xunit.Assert.True(lru.Count <= 20);
        Xunit.Assert.True(lru.TotalBytes <= 500);
        int present = 0;
        for (int i = 0; i < 50; i++)
        {
            if (lru.TryGet("k" + i, out Item? _))
            {
                present++;
            }
        }

        Xunit.Assert.Equal(lru.Count, present);
        Xunit.Assert.True(lru.TotalBytes >= 10L * lru.Count);
        Xunit.Assert.True(lru.TotalBytes <= 16L * lru.Count);
    }
}
