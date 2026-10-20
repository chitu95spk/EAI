using EasyLabel.SegAI.Model;

namespace EasyLabel.SegAI.Tests;

public sealed class SegAiRegistryTests
{
    private static SegAiRegistry NewRegistry(out string folder)
    {
        folder = TrainFixtures.Folder("registry");
        return new SegAiRegistry(Path.Combine(folder, "models"));
    }

    private static string Produce(string folder, string name = "candidate.segai")
    {
        string path = Path.Combine(folder, name);
        ModelStub.Write(path, name);
        return path;
    }

    [Fact]
    public void EmptyRegistry_FirstNameIsV001_AndNothingIsActive()
    {
        SegAiRegistry registry = NewRegistry(out _);
        registry.Load();

        Assert.Empty(registry.Entries);
        Assert.Null(registry.Active);
        Assert.Equal("v001.segai", registry.NextFileName());
    }

    [Fact]
    public void StageCandidate_NumbersSequentially_Persists_AndNeverActivates()
    {
        SegAiRegistry registry = NewRegistry(out string folder);

        SegAiRegistryEntry first = registry.StageCandidate(Produce(folder, "a.segai"), "first", "fp1", _ => null);
        SegAiRegistryEntry second = registry.StageCandidate(Produce(folder, "b.segai"), "second", "fp2", _ => null);

        Assert.Equal("v001.segai", first.File);
        Assert.Equal("v002.segai", second.File);
        Assert.Equal(SegAiRegistryEntry.StateTrained, first.State);
        Assert.True(File.Exists(registry.PathOf("v001.segai")));
        Assert.True(File.Exists(registry.PathOf("v002.segai")));
        Assert.False(File.Exists(Path.Combine(folder, "a.segai")));
        Assert.Null(registry.Active);

        var reloaded = new SegAiRegistry(registry.ModelsDir);
        reloaded.Load();
        Assert.Equal(new[] { "v001.segai", "v002.segai" }, reloaded.Entries.Select(e => e.File).ToArray());
        Assert.Equal("fp2", reloaded.Entries[1].DatasetFingerprint);
        Assert.Equal("second", reloaded.Entries[1].ReportSummary);
        Assert.Null(reloaded.Active);
        Assert.Equal("v003.segai", reloaded.NextFileName());
    }

    [Fact]
    public void StageCandidate_NeverOverwritesAnExistingFile_NotEvenAnUnregisteredOne()
    {
        SegAiRegistry registry = NewRegistry(out string folder);
        Directory.CreateDirectory(registry.ModelsDir);
        string existing = registry.PathOf("v004.segai");
        File.WriteAllText(existing, "precious");

        SegAiRegistryEntry entry = registry.StageCandidate(Produce(folder), "s", "fp", _ => null);

        Assert.Equal("v005.segai", entry.File);
        Assert.Equal("precious", File.ReadAllText(existing));
    }

    [Fact]
    public void StageCandidate_LoadFailure_LeavesRegistryAndFolderUnchanged()
    {
        SegAiRegistry registry = NewRegistry(out string folder);
        string produced = Produce(folder);

        var error = Assert.Throws<InvalidDataException>(() => registry.StageCandidate(produced, "s", "fp", _ => "boom"));

        Assert.Contains("boom", error.Message);
        Assert.Empty(registry.Entries);
        Assert.False(File.Exists(registry.RegistryPath));
        Assert.Empty(Directory.GetFiles(registry.ModelsDir, "*.segai", SearchOption.AllDirectories));
    }

    [Fact]
    public void StageCandidate_RejectsAnInvalidFile_BeforeTheLoadCheck()
    {
        SegAiRegistry registry = NewRegistry(out string folder);
        string produced = Path.Combine(folder, "bad.segai");
        ModelStub.WriteInvalid(produced);
        bool loadCheckCalled = false;

        Assert.Throws<InvalidDataException>(() => registry.StageCandidate(produced, "s", "fp", _ => { loadCheckCalled = true; return null; }));

        Assert.False(loadCheckCalled);
        Assert.Empty(registry.Entries);
        Assert.Empty(Directory.GetFiles(registry.ModelsDir, "*.segai", SearchOption.AllDirectories));
    }

    [Fact]
    public void SetActive_RequiresARegisteredModel_AndPersists()
    {
        SegAiRegistry registry = NewRegistry(out string folder);
        registry.StageCandidate(Produce(folder), "s", "fp", _ => null);

        Assert.Throws<ArgumentException>(() => registry.SetActive("v009.segai"));
        registry.SetActive("v001.segai");

        var reloaded = new SegAiRegistry(registry.ModelsDir);
        reloaded.Load();
        Assert.Equal("v001.segai", reloaded.Active);
    }

    [Fact]
    public void Load_CorruptRegistry_ThrowsAndStageDoesNotMoveTheFile()
    {
        SegAiRegistry registry = NewRegistry(out string folder);
        Directory.CreateDirectory(registry.ModelsDir);
        File.WriteAllText(registry.RegistryPath, "{ not json");
        string produced = Produce(folder);

        Assert.Throws<InvalidDataException>(() => registry.StageCandidate(produced, "s", "fp", _ => null));

        Assert.True(File.Exists(produced));
    }
}
