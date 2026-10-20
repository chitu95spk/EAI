using EasyLabel.SegAI.Model;

namespace EasyLabel.SegAI.Tests;

public sealed class SegAiImportModelTests
{
    private static SegAiSession NewSession(out string root, out string source, FakeTrainBackend? backend = null)
    {
        root = TrainFixtures.Folder("import");
        source = Path.Combine(root, "outside", "external.segai");
        SegAiSession session = SegAiSession.Create(Path.Combine(root, "project"));
        session.TrainBackend = backend ?? new FakeTrainBackend();
        return session;
    }

    private static string ModelsDir(SegAiSession session) => Path.Combine(session.ProjectRoot, ".easyai", "models");

    [Fact]
    public void Import_RegistersNextVersion_CopiesFile_AndDoesNotActivate()
    {
        using SegAiSession session = NewSession(out _, out string source);
        ModelStub.Write(source, "one");

        SegAiModelImportResult first = session.ImportModel(source);
        SegAiModelImportResult second = session.ImportModel(source);

        Assert.Null(first.Problem);
        Assert.Equal("v001.segai", first.Entry!.File);
        Assert.Equal(SegAiRegistryEntry.StateImported, first.Entry.State);
        Assert.Equal("v002.segai", second.Entry!.File);
        Assert.True(File.Exists(source));
        Assert.True(File.Exists(Path.Combine(ModelsDir(session), "v001.segai")));
        Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(Path.Combine(ModelsDir(session), "v002.segai")));
        Assert.Equal(new[] { "v001.segai", "v002.segai" }, session.Models.Select(m => m.File).ToArray());
        Assert.Null(session.ActiveModel);
        Assert.Null(session.LoadedModel);
    }

    [Fact]
    public void Import_NeverOverwritesAnExistingFile()
    {
        using SegAiSession session = NewSession(out _, out string source);
        ModelStub.Write(source);
        Directory.CreateDirectory(ModelsDir(session));
        string existing = Path.Combine(ModelsDir(session), "v007.segai");
        File.WriteAllText(existing, "precious");

        SegAiModelImportResult result = session.ImportModel(source);

        Assert.Equal("v008.segai", result.Entry!.File);
        Assert.Equal("precious", File.ReadAllText(existing));
    }

    [Fact]
    public void Import_ExistingRegistryEntriesAndActiveSurvive()
    {
        using SegAiSession session = NewSession(out string root, out string source);
        ModelStub.Write(source);
        var registry = new SegAiRegistry(ModelsDir(session));
        string produced = Path.Combine(root, "produced.segai");
        ModelStub.Write(produced, "trained");
        registry.StageCandidate(produced, "trained", "fp", _ => null);

        SegAiModelImportResult result = session.ImportModel(source);

        Assert.Equal("v002.segai", result.Entry!.File);
        Assert.Equal(new[] { "v001.segai", "v002.segai" }, session.Models.Select(m => m.File).ToArray());
        Assert.Equal(SegAiRegistryEntry.StateTrained, session.Models[0].State);
        Assert.Null(session.ActiveModel);
    }

    [Fact]
    public void Import_InvalidFile_ReturnsProblem_AndLeavesStateUnchanged()
    {
        using SegAiSession session = NewSession(out _, out string source);
        ModelStub.WriteInvalid(source);

        SegAiModelImportResult result = session.ImportModel(source);

        Assert.Null(result.Entry);
        Assert.NotNull(result.Problem);
        Assert.Empty(session.Models);
        AssertNothingLeft(session);
    }

    [Fact]
    public void Import_MissingFile_ReturnsProblem()
    {
        using SegAiSession session = NewSession(out string root, out _);

        SegAiModelImportResult result = session.ImportModel(Path.Combine(root, "nope.segai"));

        Assert.Null(result.Entry);
        Assert.NotNull(result.Problem);
        AssertNothingLeft(session);
    }

    [Fact]
    public void Import_FileThatDoesNotLoad_ReturnsProblem_AndLeavesStateUnchanged()
    {
        using SegAiSession session = NewSession(out _, out string source, new FakeTrainBackend { LoadError = "boom" });
        ModelStub.Write(source);

        SegAiModelImportResult result = session.ImportModel(source);

        Assert.Null(result.Entry);
        Assert.Contains("boom", result.Problem!.En);
        Assert.Empty(session.Models);
        AssertNothingLeft(session);
    }

    [Fact]
    public void Import_AfterDispose_Throws()
    {
        SegAiSession session = NewSession(out _, out string source);
        ModelStub.Write(source);
        session.Dispose();

        Assert.Throws<ObjectDisposedException>(() => session.ImportModel(source));
    }

    private static void AssertNothingLeft(SegAiSession session)
    {
        string dir = ModelsDir(session);
        Assert.False(File.Exists(Path.Combine(dir, SegAiRegistry.RegistryFileName)));
        if (!Directory.Exists(dir)) return;
        Assert.Empty(Directory.EnumerateFiles(dir, "*.segai", SearchOption.AllDirectories));
    }
}
