using EasyLabel.Core.Io;

namespace EasyLabel.Core.Tests;

public sealed class AtomicFileTests
{
    [Fact]
    public void WriteAllTextReplacesExistingFile()
    {
        string directory = CreateTempDirectory();
        string path = Path.Combine(directory, "sample.json");
        File.WriteAllText(path, "old");

        AtomicFile.WriteAllText(path, "new");

        Assert.Equal("new", File.ReadAllText(path));
        Assert.Empty(Directory.EnumerateFiles(directory, "*.tmp"));
    }

    [Fact]
    public void FailedReplaceLeavesOriginalFileIntact()
    {
        string directory = CreateTempDirectory();
        string path = Path.Combine(directory, "sample.json");
        File.WriteAllText(path, "original");

        using FileStream locked = new(path, FileMode.Open, FileAccess.Read, FileShare.None);

        Exception? failure = Record.Exception(() => AtomicFile.WriteAllText(path, "new"));
        Assert.NotNull(failure);
        Assert.True(
            failure is IOException or UnauthorizedAccessException,
            failure?.GetType().FullName ?? "No exception was thrown.");

        locked.Dispose();
        Assert.Equal("original", File.ReadAllText(path));
    }

    private static string CreateTempDirectory()
    {
        string directory = Path.Combine(Path.GetTempPath(), "EasyLabel", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }
}
