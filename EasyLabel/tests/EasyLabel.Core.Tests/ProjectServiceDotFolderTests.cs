namespace EasyLabel.Core.Tests;

public sealed class ProjectServiceDotFolderTests
{
    [Fact]
    public void OpenSkipsDotDirectories()
    {
        string root = Path.Combine(AppContext.BaseDirectory, "scratch", "dotdirs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "a"));
        Directory.CreateDirectory(Path.Combine(root, ".hidden"));
        Directory.CreateDirectory(Path.Combine(root, ".easyai", "cache"));
        File.WriteAllBytes(Path.Combine(root, "a", "b.png"), new byte[] { 1 });
        File.WriteAllBytes(Path.Combine(root, ".hidden", "c.png"), new byte[] { 1 });
        File.WriteAllBytes(Path.Combine(root, ".easyai", "cache", "d.png"), new byte[] { 1 });

        var project = new ProjectService().Open(root);

        string only = Assert.Single(project.Images).ImagePath;
        Assert.Equal(Path.Combine(Path.GetFullPath(root), "a", "b.png"), only);
    }
}
