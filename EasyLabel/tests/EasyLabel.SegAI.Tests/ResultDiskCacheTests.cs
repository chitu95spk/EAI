namespace EasyLabel.SegAI.Tests;

public sealed class ResultDiskCacheTests : System.IDisposable
{
    private readonly string _root;
    private readonly string _cacheDir;

    public ResultDiskCacheTests()
    {
        _root = System.IO.Path.Combine(System.AppContext.BaseDirectory, "rdc-" + System.Guid.NewGuid().ToString("N"));
        _cacheDir = System.IO.Path.Combine(_root, "cache");
    }

    public void Dispose()
    {
        // A queued background write may still hold a temp file: retry briefly, then give up (scratch folder only).
        for (int attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                if (System.IO.Directory.Exists(_root)) System.IO.Directory.Delete(_root, true);
                return;
            }
            catch (System.IO.IOException)
            {
                System.Threading.Thread.Sleep(50);
            }
            catch (System.UnauthorizedAccessException)
            {
                System.Threading.Thread.Sleep(50);
            }
        }
    }

    // ---- store ----

    private static EasyLabel.SegAI.AutoLabel.SegAiSuggestion Suggestion(float score)
    {
        return new EasyLabel.SegAI.AutoLabel.SegAiSuggestion(
            "a.png",
            1,
            new[] { new System.Drawing.PointF(1.5f, 2.25f), new System.Drawing.PointF(10f, 2f), new System.Drawing.PointF(10f, 9f) },
            new[] { new[] { new System.Drawing.PointF(3f, 3f), new System.Drawing.PointF(4f, 3f), new System.Drawing.PointF(4f, 4f) } },
            score,
            "NG",
            "polygon",
            "m1");
    }

    private static EasyLabel.SegAI.Project.AutoLabelDiskPayload Payload(float score)
    {
        return EasyLabel.SegAI.Project.AutoLabelDiskPayload.From(true, new[] { Suggestion(score) });
    }

    [Fact]
    public void Store_RoundTripsPayload_UnderKindFolderWithHashName()
    {
        var disk = new EasyLabel.SegAI.Project.ResultDiskCache(_cacheDir);
        const string key = "autolabel|a.png|m1|s1";

        Assert.True(disk.Put(EasyLabel.SegAI.Project.ResultDiskCache.AutoLabelKind, key, Payload(0.7f)));

        string path = disk.FilePathFor(EasyLabel.SegAI.Project.ResultDiskCache.AutoLabelKind, key)!;
        Assert.Equal(System.IO.Path.Combine(_cacheDir, "results", "autolabel", EasyLabel.SegAI.Project.ResultDiskCache.HashOf(key) + ".json"), path);
        Assert.Equal(32, EasyLabel.SegAI.Project.ResultDiskCache.HashOf(key).Length);
        Assert.True(disk.TryGet(EasyLabel.SegAI.Project.ResultDiskCache.AutoLabelKind, key, out EasyLabel.SegAI.Project.AutoLabelDiskPayload? loaded));
        Assert.NotNull(loaded);
        Assert.True(loaded!.HasScores);
        System.Collections.Generic.IReadOnlyList<EasyLabel.SegAI.AutoLabel.SegAiSuggestion>? list = loaded.TryRestore();
        Assert.NotNull(list);
        EasyLabel.SegAI.AutoLabel.SegAiSuggestion s = Assert.Single(list!);
        Assert.Equal(0.7f, s.Score);
        Assert.Equal(1, s.ClassIndex);
        Assert.Equal("NG", s.Label);
        Assert.Equal("m1", s.Model);
        Assert.Equal(3, s.Outer.Length);
        Assert.Equal(1.5f, s.Outer[0].X);
        Assert.Equal(2.25f, s.Outer[0].Y);
        Assert.Single(s.Holes);
        Assert.Equal(4f, s.Holes[0][2].Y);
    }

    [Fact]
    public void Store_MissingFile_IsAMiss()
    {
        var disk = new EasyLabel.SegAI.Project.ResultDiskCache(_cacheDir);

        Assert.False(disk.TryGet(EasyLabel.SegAI.Project.ResultDiskCache.AutoLabelKind, "nothing", out EasyLabel.SegAI.Project.AutoLabelDiskPayload? loaded));
        Assert.Null(loaded);
    }

    [Fact]
    public void Store_CorruptFile_IsAMissAndDoesNotThrow()
    {
        var disk = new EasyLabel.SegAI.Project.ResultDiskCache(_cacheDir);
        const string key = "k";
        disk.Put(EasyLabel.SegAI.Project.ResultDiskCache.AutoLabelKind, key, Payload(0.5f));
        string path = disk.FilePathFor(EasyLabel.SegAI.Project.ResultDiskCache.AutoLabelKind, key)!;

        System.IO.File.WriteAllText(path, "{ this is not json");
        Assert.False(disk.TryGet(EasyLabel.SegAI.Project.ResultDiskCache.AutoLabelKind, key, out EasyLabel.SegAI.Project.AutoLabelDiskPayload? a));
        Assert.Null(a);

        System.IO.File.WriteAllBytes(path, new byte[0]);
        Assert.False(disk.TryGet(EasyLabel.SegAI.Project.ResultDiskCache.AutoLabelKind, key, out EasyLabel.SegAI.Project.AutoLabelDiskPayload? b));
        Assert.Null(b);
    }

    [Fact]
    public void Store_WrongVersion_IsAMiss()
    {
        var disk = new EasyLabel.SegAI.Project.ResultDiskCache(_cacheDir);
        const string key = "k";
        disk.Put(EasyLabel.SegAI.Project.ResultDiskCache.AutoLabelKind, key, Payload(0.5f));
        string path = disk.FilePathFor(EasyLabel.SegAI.Project.ResultDiskCache.AutoLabelKind, key)!;
        string text = System.IO.File.ReadAllText(path);
        Assert.Contains("\"Version\":1", text);

        System.IO.File.WriteAllText(path, text.Replace("\"Version\":1", "\"Version\":99"));

        Assert.False(disk.TryGet(EasyLabel.SegAI.Project.ResultDiskCache.AutoLabelKind, key, out EasyLabel.SegAI.Project.AutoLabelDiskPayload? loaded));
        Assert.Null(loaded);
    }

    [Fact]
    public void Store_KeyStringMismatch_IsAMiss_CollisionGuard()
    {
        var disk = new EasyLabel.SegAI.Project.ResultDiskCache(_cacheDir);
        disk.Put(EasyLabel.SegAI.Project.ResultDiskCache.AutoLabelKind, "key-A", Payload(0.5f));
        string pathA = disk.FilePathFor(EasyLabel.SegAI.Project.ResultDiskCache.AutoLabelKind, "key-A")!;
        string pathB = disk.FilePathFor(EasyLabel.SegAI.Project.ResultDiskCache.AutoLabelKind, "key-B")!;

        // Pretend key-B hashes to the same file as key-A.
        System.IO.File.Copy(pathA, pathB);

        Assert.True(disk.TryGet(EasyLabel.SegAI.Project.ResultDiskCache.AutoLabelKind, "key-A", out EasyLabel.SegAI.Project.AutoLabelDiskPayload? a));
        Assert.NotNull(a);
        Assert.False(disk.TryGet(EasyLabel.SegAI.Project.ResultDiskCache.AutoLabelKind, "key-B", out EasyLabel.SegAI.Project.AutoLabelDiskPayload? b));
        Assert.Null(b);
    }

    [Fact]
    public void Store_AsyncWrite_LeavesNoTempFile_AndStaysInsideResults()
    {
        var disk = new EasyLabel.SegAI.Project.ResultDiskCache(_cacheDir);
        for (int i = 0; i < 20; i++) disk.PutAsync(EasyLabel.SegAI.Project.ResultDiskCache.AutoLabelKind, "key-" + i, Payload(i / 20f));
        disk.PutAsync(EasyLabel.SegAI.Project.ResultDiskCache.AutoLabelKind, "key-3", Payload(0.99f)); // replace
        disk.Flush();

        string[] files = System.IO.Directory.GetFiles(_cacheDir, "*", System.IO.SearchOption.AllDirectories);
        Assert.Equal(20, files.Length);
        Assert.All(files, f => Assert.EndsWith(".json", f));
        string results = System.IO.Path.Combine(_cacheDir, "results") + System.IO.Path.DirectorySeparatorChar;
        Assert.All(files, f => Assert.StartsWith(results, f));
        Assert.True(disk.TryGet(EasyLabel.SegAI.Project.ResultDiskCache.AutoLabelKind, "key-3", out EasyLabel.SegAI.Project.AutoLabelDiskPayload? loaded));
        Assert.Equal(0.99f, loaded!.TryRestore()![0].Score);
    }

    [Fact]
    public void Store_UnknownKind_WritesNothing()
    {
        var disk = new EasyLabel.SegAI.Project.ResultDiskCache(_cacheDir);

        Assert.Null(disk.FilePathFor("..", "k"));
        Assert.False(disk.Put("..", "k", Payload(0.5f)));
        Assert.False(System.IO.Directory.Exists(_cacheDir));
    }

    [Fact]
    public void Store_WriteFailure_IsSwallowed()
    {
        System.IO.Directory.CreateDirectory(_root);
        string blocker = System.IO.Path.Combine(_root, "blocker");
        System.IO.File.WriteAllText(blocker, "x"); // a file where the cache folder should be
        var disk = new EasyLabel.SegAI.Project.ResultDiskCache(System.IO.Path.Combine(blocker, "cache"));

        Assert.False(disk.Put(EasyLabel.SegAI.Project.ResultDiskCache.AutoLabelKind, "k", Payload(0.5f)));
        disk.PutAsync(EasyLabel.SegAI.Project.ResultDiskCache.AutoLabelKind, "k", Payload(0.5f));
        disk.Flush();
        Assert.False(disk.TryGet(EasyLabel.SegAI.Project.ResultDiskCache.AutoLabelKind, "k", out EasyLabel.SegAI.Project.AutoLabelDiskPayload? loaded));
        Assert.Null(loaded);
    }

    // ---- InspectService ----

    private sealed class InspectFake
    {
        public int Calls;
        public string Model = "v001.segai";
        public string FileStamp = "100:1";
        public string ModelFile = "5000:77";
        public float Threshold = 0.5f;
        public int MinArea = 50;

        public EasyLabel.SegAI.Inspection.InspectService Service(string cacheDir)
        {
            var service = new EasyLabel.SegAI.Inspection.InspectService(
                () => false,
                path => (Model, FileStamp),
                path =>
                {
                    Calls++;
                    return (InspectServiceTests.Blobs(2, false, new[] { 0.3f, 0.7f }), 64, 64);
                },
                () => (Threshold, MinArea),
                () => new System.DateTime(2026, 10, 8, 1, 2, 3, System.DateTimeKind.Utc));
            service.ConfigureDiskCache(new EasyLabel.SegAI.Project.ResultDiskCache(cacheDir), () => ModelFile);
            return service;
        }
    }

    [Fact]
    public void Inspect_SecondInstanceReadsWhatTheFirstWrote_WithoutInference()
    {
        string path = System.IO.Path.GetFullPath("disk-a.png");
        var fake = new InspectFake();
        EasyLabel.SegAI.Inspection.InspectService first = fake.Service(_cacheDir);
        EasyLabel.SegAI.Inspection.InspectResult fresh = first.Run(path);
        first.FlushDiskWrites();
        Assert.False(fresh.FromCache);
        Assert.Equal(1, fake.Calls);

        var fake2 = new InspectFake();
        EasyLabel.SegAI.Inspection.InspectService second = fake2.Service(_cacheDir);
        EasyLabel.SegAI.Inspection.InspectResult loaded = second.Run(path);

        Assert.Equal(0, fake2.Calls);
        Assert.True(loaded.FromCache);
        Assert.Equal(fresh.Verdict, loaded.Verdict);
        Assert.Equal(fresh.Regions, loaded.Regions);
        Assert.Equal(fresh.Score, loaded.Score);
        Assert.Equal(fresh.ElapsedMs, loaded.ElapsedMs);
        Assert.Equal(fresh.Model, loaded.Model);
        Assert.Equal(fresh.TimestampUtc, loaded.TimestampUtc);
        Assert.Equal(fresh.Width, loaded.Width);
        Assert.Equal(fresh.Height, loaded.Height);
        Assert.Equal(path, loaded.Path);
        Assert.Equal(fresh.Suggestions.Count, loaded.Suggestions.Count);
        Assert.Equal(fresh.Suggestions[0].Outer, loaded.Suggestions[0].Outer);
        Assert.Equal(1, second.CachedImages);

        // now in memory: a third run on the same instance does not touch the disk or the model
        Assert.True(second.Run(path).FromCache);
        Assert.Equal(0, fake2.Calls);
    }

    [Fact]
    public void Inspect_EachKeyPartChange_IsAMiss()
    {
        string path = System.IO.Path.GetFullPath("disk-b.png");
        var seed = new InspectFake();
        EasyLabel.SegAI.Inspection.InspectService writer = seed.Service(_cacheDir);
        writer.Run(path);
        writer.FlushDiskWrites();

        var changes = new System.Collections.Generic.List<(string Name, System.Action<InspectFake> Change)>
        {
            ("image stamp", f => f.FileStamp = "100:2"),
            ("class suffix", f => f.FileStamp = "100:1|Scratch:0:1"),
            ("model name", f => f.Model = "v002.segai"),
            ("model file stamp", f => f.ModelFile = "5000:78"),
            ("threshold", f => f.Threshold = 0.6f),
            ("min area", f => f.MinArea = 51),
        };
        foreach ((string name, System.Action<InspectFake> change) in changes)
        {
            var fake = new InspectFake();
            change(fake);
            EasyLabel.SegAI.Inspection.InspectResult result = fake.Service(_cacheDir).Run(path);
            Assert.False(result.FromCache, name);
            Assert.Equal(1, fake.Calls);
        }

        // a different image path is a miss as well
        var other = new InspectFake();
        Assert.False(other.Service(_cacheDir).Run(System.IO.Path.GetFullPath("disk-c.png")).FromCache);
        Assert.Equal(1, other.Calls);

        // the unchanged key still hits
        var same = new InspectFake();
        Assert.True(same.Service(_cacheDir).Run(path).FromCache);
        Assert.Equal(0, same.Calls);
    }

    [Fact]
    public void Inspect_EmptyModelStamp_SkipsTheDisk()
    {
        string path = System.IO.Path.GetFullPath("disk-d.png");
        var fake = new InspectFake { ModelFile = "" };
        EasyLabel.SegAI.Inspection.InspectService service = fake.Service(_cacheDir);

        service.Run(path);
        service.FlushDiskWrites();

        Assert.False(System.IO.Directory.Exists(_cacheDir));
    }

    [Fact]
    public void Inspect_CorruptFile_FallsBackToInference()
    {
        string path = System.IO.Path.GetFullPath("disk-e.png");
        var fake = new InspectFake();
        EasyLabel.SegAI.Inspection.InspectService writer = fake.Service(_cacheDir);
        writer.Run(path);
        writer.FlushDiskWrites();
        foreach (string file in System.IO.Directory.GetFiles(_cacheDir, "*.json", System.IO.SearchOption.AllDirectories)) System.IO.File.WriteAllText(file, "garbage");

        var fake2 = new InspectFake();
        EasyLabel.SegAI.Inspection.InspectResult result = fake2.Service(_cacheDir).Run(path);

        Assert.False(result.FromCache);
        Assert.Equal(1, fake2.Calls);
        Assert.Equal(EasyLabel.SegAI.Inspection.InspectVerdict.NG, result.Verdict);
    }

    [Fact]
    public void Inspect_DiskOff_WritesNothing()
    {
        string path = System.IO.Path.GetFullPath("disk-f.png");
        var fake = new InspectFake();
        EasyLabel.SegAI.Inspection.InspectService service = fake.Service(_cacheDir);
        service.ConfigureDiskCache(null, null);

        service.Run(path);
        service.FlushDiskWrites();

        Assert.False(System.IO.Directory.Exists(_cacheDir));
    }

    // ---- AutoLabelService ----

    private sealed class AutoFake
    {
        public int Calls;
        public string Model = "m1";
        public string Stamp = "s1";
        public string ModelFile = "5000:77";

        public EasyLabel.SegAI.AutoLabel.AutoLabelService Service(EasyLabel.SegAI.SegAiSession session, string cacheDir)
        {
            var service = new EasyLabel.SegAI.AutoLabel.AutoLabelService(
                session,
                rel => (Model, Stamp),
                (rel, threshold) =>
                {
                    Calls++;
                    return (InspectServiceTests.Blobs(2, false, new[] { 0.3f, 0.7f }), 64, 64);
                });
            service.ConfigureDiskCache(new EasyLabel.SegAI.Project.ResultDiskCache(cacheDir), () => ModelFile);
            return service;
        }
    }

    [Fact]
    public void AutoLabel_SecondInstanceReadsWhatTheFirstWrote_WithoutInference()
    {
        using EasyLabel.SegAI.SegAiSession session = EasyLabel.SegAI.SegAiSession.Create(TrainFixtures.Folder("rdcal1"));
        var fake = new AutoFake();
        EasyLabel.SegAI.AutoLabel.AutoLabelService first = fake.Service(session, _cacheDir);
        EasyLabel.SegAI.AutoLabel.AutoLabelImageResult fresh = first.Run("a.png");
        first.FlushDiskWrites();
        Assert.False(fresh.FromCache);
        Assert.Equal(1, fake.Calls);

        var fake2 = new AutoFake();
        EasyLabel.SegAI.AutoLabel.AutoLabelService second = fake2.Service(session, _cacheDir);
        EasyLabel.SegAI.AutoLabel.AutoLabelImageResult loaded = second.Run("a.png");

        Assert.Equal(0, fake2.Calls);
        Assert.True(loaded.FromCache);
        Assert.Null(loaded.Error);
        Assert.Equal(fresh.HasScores, loaded.HasScores);
        Assert.Equal(fresh.Suggestions.Count, loaded.Suggestions.Count);
        Assert.Equal(fresh.Suggestions[1].Score, loaded.Suggestions[1].Score);
        Assert.Equal(fresh.Suggestions[1].Outer, loaded.Suggestions[1].Outer);
        Assert.Equal(1, second.CachedImages);
        Assert.Equal(1, second.Filter("a.png", 0.5f).Count);
        Assert.Equal(0, fake2.Calls);
    }

    [Fact]
    public void AutoLabel_EachKeyPartChange_IsAMiss()
    {
        using EasyLabel.SegAI.SegAiSession session = EasyLabel.SegAI.SegAiSession.Create(TrainFixtures.Folder("rdcal2"));
        var seed = new AutoFake();
        EasyLabel.SegAI.AutoLabel.AutoLabelService writer = seed.Service(session, _cacheDir);
        writer.Run("a.png");
        writer.FlushDiskWrites();

        var changes = new System.Collections.Generic.List<(string Name, System.Action<AutoFake> Change)>
        {
            ("image stamp", f => f.Stamp = "s2"),
            ("class suffix", f => f.Stamp = "s1|Scratch:0:1"),
            ("model name", f => f.Model = "m2"),
            ("model file stamp", f => f.ModelFile = "5000:78"),
        };
        foreach ((string name, System.Action<AutoFake> change) in changes)
        {
            var fake = new AutoFake();
            change(fake);
            Assert.False(fake.Service(session, _cacheDir).Run("a.png").FromCache, name);
            Assert.Equal(1, fake.Calls);
        }

        var other = new AutoFake();
        Assert.False(other.Service(session, _cacheDir).Run("b.png").FromCache);
        Assert.Equal(1, other.Calls);

        // min defect area is part of the disk key
        int saved = session.Inference.MinDefectArea;
        session.Inference.MinDefectArea = saved + 1;
        var area = new AutoFake();
        Assert.False(area.Service(session, _cacheDir).Run("a.png").FromCache);
        Assert.Equal(1, area.Calls);
        session.Inference.MinDefectArea = saved;

        var same = new AutoFake();
        Assert.True(same.Service(session, _cacheDir).Run("a.png").FromCache);
        Assert.Equal(0, same.Calls);
    }

    [Fact]
    public void AutoLabel_Clear_DropsMemoryOnly()
    {
        using EasyLabel.SegAI.SegAiSession session = EasyLabel.SegAI.SegAiSession.Create(TrainFixtures.Folder("rdcal3"));
        var fake = new AutoFake();
        EasyLabel.SegAI.AutoLabel.AutoLabelService service = fake.Service(session, _cacheDir);
        service.Run("a.png");
        service.FlushDiskWrites();

        service.Clear();

        Assert.Equal(0, service.CachedImages);
        Assert.True(service.Run("a.png").FromCache); // restored from disk
        Assert.Equal(1, fake.Calls);
    }

    [Fact]
    public void AutoLabel_WarmFromDisk_RestoresUncertaintyWithoutInference()
    {
        using EasyLabel.SegAI.SegAiSession session = EasyLabel.SegAI.SegAiSession.Create(TrainFixtures.Folder("rdcal4"));
        var fake = new AutoFake();
        EasyLabel.SegAI.AutoLabel.AutoLabelService first = fake.Service(session, _cacheDir);
        EasyLabel.SegAI.AutoLabel.AutoLabelImageResult fresh = first.Run("a.png");
        first.FlushDiskWrites();
        Assert.NotNull(fresh.Uncertainty);

        // A new session: memory is empty, the disk entry of the same model and image is not.
        var fake2 = new AutoFake();
        EasyLabel.SegAI.AutoLabel.AutoLabelService second = fake2.Service(session, _cacheDir);
        Assert.Null(second.CachedUncertainty("a.png"));

        // "missing.png" has no disk entry and must be skipped silently.
        Assert.Equal(1, second.WarmFromDisk(new[] { "a.png", "missing.png" }));
        Assert.Equal(0, fake2.Calls);
        Assert.Equal(fresh.Uncertainty, second.CachedUncertainty("a.png"));
        Assert.Null(second.CachedUncertainty("missing.png"));
        Assert.Equal(1, second.CachedImages);
    }
}
