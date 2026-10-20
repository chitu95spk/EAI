using EasyLabel.Core;
using EasyLabel.SegAI.Inspection;

namespace EasyLabel.SegAI.Tests;

public sealed class InspectListStoreTests
{
    private static string NewProject(string name) => TrainFixtures.Folder(name);

    private static string Touch(string folder, string relative)
    {
        string path = Path.Combine(folder, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[] { 1 });
        return path;
    }

    [Fact]
    public void FilePath_IsInsideTheMetadataFolder()
    {
        string root = NewProject("inspstorepath");

        Assert.Equal(Path.Combine(root, ".easyai", "inspect.json"), new InspectListStore(root).FilePath);
        Assert.Equal(ProjectLayout.InspectFile(root), new InspectListStore(root).FilePath);
    }

    [Fact]
    public void SaveThenLoad_RoundTripsInOrder_InsideAsRelativeOutsideAsAbsolute()
    {
        string root = NewProject("insplist");
        string outside = NewProject("inspoutside");
        string inside = Touch(root, "sub/a.png");
        string far = Touch(outside, "b.jpg");
        var store = new InspectListStore(root);

        store.Save(new[] { inside, far });
        IReadOnlyList<string> loaded = new InspectListStore(root).Load();

        Assert.Equal(new[] { Path.GetFullPath(inside), Path.GetFullPath(far) }, loaded);
        string json = File.ReadAllText(store.FilePath);
        Assert.Contains("\"sub/a.png\"", json);
        Assert.DoesNotContain(root.Replace("\\", "\\\\"), json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(far.Replace("\\", "\\\\"), json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"schema\": " + InspectListStore.Schema, json);
    }

    [Fact]
    public void Load_MissingFileIsEmpty_AndMissingImagesStayInTheList()
    {
        string root = NewProject("inspmissing");
        var store = new InspectListStore(root);
        Assert.Empty(store.Load());

        string gone = Path.Combine(root, "gone.png");
        store.Save(new[] { gone });

        Assert.Equal(new[] { Path.GetFullPath(gone) }, store.Load());
    }

    [Fact]
    public void Load_CorruptFileThrowsInvalidData()
    {
        string root = NewProject("inspcorrupt");
        var store = new InspectListStore(root);
        Directory.CreateDirectory(Path.GetDirectoryName(store.FilePath)!);

        File.WriteAllText(store.FilePath, "{ not json");
        Assert.Throws<InvalidDataException>(() => store.Load());

        File.WriteAllText(store.FilePath, "{\"schema\":1}");
        Assert.Throws<InvalidDataException>(() => store.Load());
    }

    [Fact]
    public void Merge_DropsDuplicatesCaseInsensitively_AndNonImages_KeepsOrder()
    {
        string root = NewProject("inspmerge");
        string a = Path.Combine(root, "a.png");
        string b = Path.Combine(root, "b.bmp");

        IReadOnlyList<string> merged = InspectListStore.Merge(
            new[] { a },
            new[] { b, a.ToUpperInvariant(), Path.Combine(root, "notes.txt"), "", Path.Combine(root, "c.JPG") });

        Assert.Equal(new[] { Path.GetFullPath(a), Path.GetFullPath(b), Path.GetFullPath(Path.Combine(root, "c.JPG")) }, merged);
    }

    [Fact]
    public void ScanFolder_IsRecursive_SkipsDotFoldersAndOtherFiles_Sorted()
    {
        string root = NewProject("inspscan");
        Touch(root, "b.png");
        Touch(root, "a.jpg");
        Touch(root, "deep/er/c.bmp");
        Touch(root, ".easyai/cache/x.png");
        Touch(root, ".hidden/y.png");
        Touch(root, "readme.txt");
        Touch(root, "labels.json");

        IReadOnlyList<string> found = InspectListStore.ScanFolder(root);

        Assert.Equal(
            new[] { "a.jpg", "b.png", Path.Combine("deep", "er", "c.bmp") }.Select(r => Path.Combine(root, r)).ToArray(),
            found.ToArray());
        Assert.Empty(InspectListStore.ScanFolder(Path.Combine(root, "nope")));
        Assert.Empty(InspectListStore.ScanFolder(""));
    }

    [Fact]
    public void IsImage_AcceptsTheFourProjectImageTypes()
    {
        Assert.True(InspectListStore.IsImage("a.PNG"));
        Assert.True(InspectListStore.IsImage("a.jpeg"));
        Assert.True(InspectListStore.IsImage("a.bmp"));
        Assert.True(InspectListStore.IsImage("a.jpg"));
        Assert.False(InspectListStore.IsImage("a.tif"));
        Assert.False(InspectListStore.IsImage("a"));
        Assert.False(InspectListStore.IsImage(""));
    }

    [Fact]
    public void ListIsIndependentOfTheTrainingImages()
    {
        using SegAiSession session = SegAiSession.Create(NewProject("inspindependent"));
        string outside = Touch(NewProject("inspindependent2"), "z.png");
        var store = new InspectListStore(session.ProjectRoot);

        store.Save(new[] { outside });

        Assert.Empty(session.Samples);
        Assert.Equal(new[] { Path.GetFullPath(outside) }, store.Load());
    }
}
