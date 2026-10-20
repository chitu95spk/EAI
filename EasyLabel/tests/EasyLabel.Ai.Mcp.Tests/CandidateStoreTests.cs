using EasyLabel.Ai.Mcp;
using Xunit;

namespace EasyLabel.Ai.Mcp.Tests;

public class CandidateStoreTests
{
    private static string Folder()
    {
        string dir = Path.Combine(Path.GetTempPath(), "cand-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, CandidateStore.FileName),
            "{\"candidates\":[{\"id\":1,\"score\":0.9,\"points\":[[10,10],[60,10],[60,30],[10,30]]},{\"id\":2,\"score\":0.8,\"points\":[[55,10],[120,10],[120,30],[55,30]]},{\"id\":3,\"score\":0.9,\"points\":[[10,80],[40,80],[40,100],[10,100]]}]}");
        return dir;
    }

    [Fact]
    public void Union_JoinsTouchingOutlinesIntoOnePolygonWithTheLowestScore()
    {
        var store = new CandidateStore(Folder());
        var (outer, _, score, error) = store.Union(new[] { 1, 2 }, 200, 200);
        Assert.Null(error);
        Assert.Equal(0.8, score, 3);
        Assert.Equal(10, outer!.Min(p => p[0]), 0);
        Assert.Equal(120, outer!.Max(p => p[0]), 0);
    }

    [Fact]
    public void Union_ReportsAnUnknownNumberAndAnEmptyList()
    {
        var store = new CandidateStore(Folder());
        Assert.NotNull(store.Union(new[] { 9 }, 200, 200).Error);
        Assert.NotNull(store.Union(Array.Empty<int>(), 200, 200).Error);
    }

    [Fact]
    public void Exists_IsFalseWithoutTheFile()
    {
        Assert.False(new CandidateStore(Path.Combine(Path.GetTempPath(), "none-" + Guid.NewGuid().ToString("N"))).Exists);
    }
}
