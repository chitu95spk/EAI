using EasyLabel.Core.Model;
using EasyLabel.Core.Projects;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace EasyLabel.Core.Tests;

/// <summary>Named projects: Create, OpenFile, SaveManifest, Reload and SaveAs (including the conversion of a legacy image folder).</summary>
public sealed class ProjectManagerTests
{
    private readonly ProjectManager _manager = new ProjectManager();

    private static JObject ReadJson(string path)
    {
        return JsonConvert.DeserializeObject<JObject>(File.ReadAllText(path), new JsonSerializerSettings { DateParseHandling = DateParseHandling.None })!;
    }

    private static string[] Keys(SortedDictionary<string, string> snapshot)
    {
        return snapshot.Keys.ToArray();
    }

    /// <summary>A named project with its own images folder holding a.png (+json), sub/b.png (+json), c.jpg and files that must never be copied.</summary>
    private ProjectOpenResult BuildSource(string name, out string location)
    {
        location = ProjectTestSupport.NewFolder(name + "-src");
        ProjectOpenResult created = _manager.Create("Src", location, null);
        string images = created.Paths.ImageRoot;
        ProjectTestSupport.WriteImage(images, "a.png", true);
        ProjectTestSupport.WriteImage(images, "sub/b.png", true);
        ProjectTestSupport.WriteImage(images, "c.jpg", false);
        ProjectTestSupport.Write(Path.Combine(images, "readme.txt"), "not an image");
        ProjectTestSupport.Write(Path.Combine(images, "orphan.json"), "{}");
        ProjectTestSupport.WriteImage(images, ".hidden/z.png", true);

        string data = created.Paths.MetaDir;
        ProjectTestSupport.Write(Path.Combine(data, "meta", "a.json"), "{\"reviewStatus\":\"reviewed\"}");
        ProjectTestSupport.Write(Path.Combine(data, "cache", "sub", "b.seg.json"), "{\"Schema\":1}");
        ProjectTestSupport.Write(Path.Combine(data, "runs", "r1", "status.json"), "{\"status\":\"success\"}");
        ProjectTestSupport.Write(Path.Combine(data, "models", "registry.json"), "{\"schema\":1}");
        ProjectTestSupport.Write(Path.Combine(data, "agent-memory.md"), "remember");
        ProjectTestSupport.Write(Path.Combine(data, "partial.tmp"), "half written");
        ProjectTestSupport.Write(Path.Combine(data, "cache", ".x.json.0123.tmp"), "half written");
        ProjectTestSupport.Write(created.Paths.ClassesFile, "NG\nOK\n");
        return new ProjectOpenResult(_manager.Reload(created.Project), created.Manifest, false, Array.Empty<string>());
    }

    // ---- create ----

    [Fact]
    public void Create_WithoutAnImageFolder_BuildsManifestDataFolderAndAnEmptyImagesFolder()
    {
        string location = ProjectTestSupport.NewFolder("create-own");

        ProjectOpenResult result = _manager.Create("  Line 1 ", location, null);

        string manifestPath = Path.Combine(location, "Line 1.easyai");
        string data = Path.Combine(location, "Line 1.easyai.data");
        string images = Path.Combine(location, "Line 1.images");
        Assert.Equal(new[] { "Line 1.easyai", "Line 1.easyai.data", "Line 1.images" }, ProjectTestSupport.Names(location));
        Assert.Equal(new[] { "classes.txt", "project.json" }, ProjectTestSupport.Names(data));
        Assert.Empty(Directory.GetFileSystemEntries(images));
        Assert.Equal(2, ReadJson(Path.Combine(data, "project.json")).Value<int>("schema"));

        JObject manifest = ReadJson(manifestPath);
        Assert.Equal(3, manifest.Value<int>("schema"));
        Assert.Equal("Line 1", manifest.Value<string>("name"));
        Assert.Equal("Line 1.images", manifest.Value<string>("imageRoot"));
        Assert.Equal("Line 1.easyai.data", manifest.Value<string>("dataDir"));
        Assert.NotNull(manifest["createdUtc"]);
        Assert.NotNull(manifest["lastOpenedUtc"]);

        Assert.Equal(images, result.Paths.ImageRoot);
        Assert.Equal(data, result.Paths.MetaDir);
        Assert.False(result.Paths.IsLegacyPlacement);
        Assert.Same(result.Project.Manifest, result.Manifest);
        Assert.Equal(manifestPath, result.Manifest.ManifestPath);
        Assert.Empty(result.Project.Images);
        Assert.False(result.DataFolderRecreated);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void Create_WithAnImageFolder_ReferencesItAndNeverWritesIntoIt()
    {
        string images = ProjectTestSupport.NewFolder("create-ref-images");
        ProjectTestSupport.WriteImage(images, "a.png", true);
        ProjectTestSupport.WriteImage(images, "sub/b.jpg", false);
        SortedDictionary<string, string> before = ProjectTestSupport.Snapshot(images);
        string location = ProjectTestSupport.NewFolder("create-ref");

        ProjectOpenResult result = _manager.Create("Ref", location, images);

        Assert.Equal(before, ProjectTestSupport.Snapshot(images));
        Assert.Equal(new[] { "Ref.easyai", "Ref.easyai.data" }, ProjectTestSupport.Names(location));
        Assert.Equal(2, result.Project.Images.Count);
        Assert.Equal(images, result.Paths.ImageRoot);
        Assert.Equal(images, result.Manifest.ImageRootFullPath);
        Assert.StartsWith("..", ReadJson(Path.Combine(location, "Ref.easyai")).Value<string>("imageRoot"));
        Assert.True(File.Exists(Path.Combine(location, "Ref.easyai.data", "classes.txt")));
    }

    [Fact]
    public void Create_WithTheDataFolderInsideTheImageFolder_IsAllowedAndScannersSkipIt()
    {
        string images = ProjectTestSupport.NewFolder("create-inside");
        ProjectTestSupport.WriteImage(images, "a.png", true);

        ProjectOpenResult result = _manager.Create("In", images, images);

        Assert.Equal(new[] { "In.easyai", "In.easyai.data", "a.json", "a.png" }, ProjectTestSupport.Names(images));
        Assert.Single(result.Project.Images);
        Assert.Equal(images, result.Manifest.ImageRootFullPath);
        Assert.Equal(".", ReadJson(Path.Combine(images, "In.easyai")).Value<string>("imageRoot"));
        Assert.Single(_manager.OpenFile(Path.Combine(images, "In.easyai")).Project.Images);
    }

    [Fact]
    public void Create_RefusesAnInvalidName_AMissingImageFolderAndABadLocation_WithoutTouchingTheDisk()
    {
        string location = ProjectTestSupport.NewFolder("create-refuse");
        string fresh = Path.Combine(location, "not-created-yet");

        ProjectException name = Assert.Throws<ProjectException>(() => _manager.Create("CON", location, null));
        Assert.Equal(ProjectErrorCode.InvalidName, name.Code);
        Assert.Equal(ProjectNameError.ReservedName, name.NameError);

        ProjectException missing = Assert.Throws<ProjectException>(() => _manager.Create("P", fresh, Path.Combine(location, "no-such-images")));
        Assert.Equal(ProjectErrorCode.ImageRootMissing, missing.Code);
        Assert.False(Directory.Exists(fresh));

        string file = ProjectTestSupport.Write(Path.Combine(location, "a-file"), "x");
        Assert.Equal(ProjectErrorCode.InvalidLocation, Assert.Throws<ProjectException>(() => _manager.Create("P", file, null)).Code);
        Assert.Equal(ProjectErrorCode.InvalidLocation, Assert.Throws<ProjectException>(() => _manager.Create("P", "  ", null)).Code);

        Assert.Equal(new[] { "a-file" }, ProjectTestSupport.Names(location));
    }

    [Fact]
    public void Create_NeverOverwritesAnExistingManifestDataFolderOrImagesFolder()
    {
        string location = ProjectTestSupport.NewFolder("create-exists");
        ProjectTestSupport.Write(Path.Combine(location, "A.easyai"), "keep-manifest");
        ProjectTestSupport.Write(Path.Combine(location, "B.easyai.data", "keep.txt"), "keep-data");
        ProjectTestSupport.Write(Path.Combine(location, "C.images", "keep.png"), "keep-images");
        SortedDictionary<string, string> before = ProjectTestSupport.Snapshot(location);

        foreach (string name in new[] { "A", "B", "C" })
        {
            ProjectException ex = Assert.Throws<ProjectException>(() => _manager.Create(name, location, null));
            Assert.Equal(ProjectErrorCode.TargetExists, ex.Code);
            Assert.Equal(before, ProjectTestSupport.Snapshot(location));
        }
    }

    // ---- open ----

    [Fact]
    public void OpenFile_ReadsTheProject_AndTouchesLastOpened()
    {
        string location = ProjectTestSupport.NewFolder("open");
        ProjectOpenResult created = _manager.Create("Open", location, null);
        ProjectTestSupport.WriteImage(created.Paths.ImageRoot, "a.png", true);
        string manifestPath = created.Manifest.ManifestPath;
        ProjectManifest before = ProjectManifestStore.Read(manifestPath);
        Thread.Sleep(25);

        ProjectOpenResult opened = _manager.OpenFile(manifestPath);

        Assert.Single(opened.Project.Images);
        Assert.False(opened.DataFolderRecreated);
        Assert.Empty(opened.Warnings);
        Assert.Same(opened.Manifest, opened.Project.Manifest);
        ProjectManifest after = ProjectManifestStore.Read(manifestPath);
        Assert.True(after.LastOpenedUtc > before.LastOpenedUtc);
        Assert.Equal(before.CreatedUtc, after.CreatedUtc);
        Assert.Equal(before.Name, after.Name);
        Assert.Equal(before.ImageRoot, after.ImageRoot);
    }

    [Fact]
    public void OpenFile_AfterMovingTheWholeLocation_StillFindsTheImages()
    {
        string parent = ProjectTestSupport.NewFolder("open-move");
        string location = Path.Combine(parent, "first");
        ProjectOpenResult created = _manager.Create("Move", location, null);
        ProjectTestSupport.WriteImage(created.Paths.ImageRoot, "a.png", false);
        string moved = Path.Combine(parent, "second");
        Directory.Move(location, moved);

        ProjectOpenResult opened = _manager.OpenFile(Path.Combine(moved, "Move.easyai"));

        Assert.Equal(Path.Combine(moved, "Move.images"), opened.Paths.ImageRoot);
        Assert.Single(opened.Project.Images);
    }

    [Fact]
    public void OpenFile_RecreatesAMissingDataFolderAndReportsIt()
    {
        string location = ProjectTestSupport.NewFolder("open-nodata");
        ProjectOpenResult created = _manager.Create("NoData", location, null);
        ProjectTestSupport.WriteImage(created.Paths.ImageRoot, "a.png", false);
        Directory.Delete(created.Paths.MetaDir, true);

        ProjectOpenResult opened = _manager.OpenFile(created.Manifest.ManifestPath);

        Assert.True(opened.DataFolderRecreated);
        Assert.Contains(opened.Warnings, w => w.Contains("NoData.easyai.data", StringComparison.Ordinal));
        Assert.Equal(new[] { "classes.txt", "project.json" }, ProjectTestSupport.Names(created.Paths.MetaDir));
        Assert.Single(opened.Project.Images);
    }

    [Fact]
    public void OpenFile_WithAMissingImageFolder_ThrowsAClearErrorAndCreatesNothing()
    {
        string location = ProjectTestSupport.NewFolder("open-noimages");
        ProjectOpenResult created = _manager.Create("NoImg", location, null);
        Directory.Delete(created.Paths.MetaDir, true);
        Directory.Delete(created.Paths.ImageRoot, true);

        ProjectException ex = Assert.Throws<ProjectException>(() => _manager.OpenFile(created.Manifest.ManifestPath));

        Assert.Equal(ProjectErrorCode.ImageRootMissing, ex.Code);
        Assert.Contains(Path.Combine(location, "NoImg.images"), ex.Message, StringComparison.Ordinal);
        Assert.Contains("NoImg", ex.Message, StringComparison.Ordinal);
        Assert.Equal(new[] { "NoImg.easyai" }, ProjectTestSupport.Names(location));
    }

    [Fact]
    public void OpenFile_RefusesAMissingOrInvalidManifest()
    {
        string location = ProjectTestSupport.NewFolder("open-bad");
        string bad = ProjectTestSupport.Write(Path.Combine(location, "Bad.easyai"), "garbage");

        Assert.Equal(ProjectErrorCode.ManifestMissing, Assert.Throws<ProjectException>(() => _manager.OpenFile(Path.Combine(location, "None.easyai"))).Code);
        Assert.Equal(ProjectErrorCode.ManifestInvalid, Assert.Throws<ProjectException>(() => _manager.OpenFile(bad)).Code);
    }

    // ---- save ----

    [Fact]
    public void SaveManifest_TouchesLastOpenedAndKeepsUnknownFields()
    {
        string location = ProjectTestSupport.NewFolder("save-manifest");
        ProjectOpenResult created = _manager.Create("Save", location, null);
        string path = created.Manifest.ManifestPath;
        JObject json = ReadJson(path);
        json["futureField"] = "keep-me";
        File.WriteAllText(path, json.ToString());
        ProjectOpenResult opened = _manager.OpenFile(path);
        DateTime before = opened.Manifest.LastOpenedUtc;
        Thread.Sleep(25);

        ProjectManifest saved = _manager.SaveManifest(opened.Project);

        Assert.True(saved.LastOpenedUtc > before);
        JObject after = ReadJson(path);
        Assert.Equal("keep-me", after.Value<string>("futureField"));
        Assert.Equal("Save", after.Value<string>("name"));
        Assert.Equal(new[] { "Save.easyai", "Save.easyai.data", "Save.images" }, ProjectTestSupport.Names(location));
    }

    [Fact]
    public void SaveManifest_OfALegacyProject_SaysToUseSaveAs()
    {
        string root = ProjectTestSupport.NewFolder("save-legacy");
        Project legacy = new ProjectService().Open(root);

        ProjectException ex = Assert.Throws<ProjectException>(() => _manager.SaveManifest(legacy));

        Assert.Equal(ProjectErrorCode.NotANamedProject, ex.Code);
    }

    [Fact]
    public void Reload_KeepsTheManifest_AndSeesNewImages()
    {
        string location = ProjectTestSupport.NewFolder("reload");
        ProjectOpenResult created = _manager.Create("Reload", location, null);
        ProjectTestSupport.WriteImage(created.Paths.ImageRoot, "late.png", false);

        Project reloaded = _manager.Reload(created.Project);

        Assert.Same(created.Manifest, reloaded.Manifest);
        Assert.Single(reloaded.Images);
        Assert.Equal(created.Paths.MetaDir, reloaded.Paths.MetaDir);
    }

    // ---- save as ----

    [Fact]
    public void SaveAs_WithoutCopyingImages_DuplicatesTheDataFolderAndReferencesTheSameImages()
    {
        ProjectOpenResult source = BuildSource("saveas-ref", out string sourceLocation);
        SortedDictionary<string, string> sourceBefore = ProjectTestSupport.Snapshot(sourceLocation);
        string destination = ProjectTestSupport.NewFolder("saveas-ref-dest");
        SyncProgress<ProjectCopyProgress> progress = new SyncProgress<ProjectCopyProgress>();

        ProjectOpenResult copy = _manager.SaveAs(source.Project, "Copy", destination, false, progress, CancellationToken.None);

        Assert.Equal(sourceBefore, ProjectTestSupport.Snapshot(sourceLocation));
        Assert.Equal(new[] { "Copy.easyai", "Copy.easyai.data" }, ProjectTestSupport.Names(destination));
        Assert.Equal(source.Paths.ImageRoot, copy.Paths.ImageRoot);
        Assert.Equal(source.Paths.ImageRoot, copy.Manifest.ImageRootFullPath);
        Assert.Equal(Path.Combine(destination, "Copy.easyai.data"), copy.Paths.MetaDir);
        Assert.Equal("Copy", copy.Manifest.Name);
        Assert.Same(copy.Manifest, copy.Project.Manifest);
        Assert.Equal(3, copy.Project.Images.Count);
        Assert.Equal(new[] { "NG", "OK" }, copy.Project.Classes);

        // Every source data file is in the copy, byte for byte (project.json is rewritten by opening the copy); temp files are not.
        SortedDictionary<string, string> sourceData = ProjectTestSupport.Snapshot(source.Paths.MetaDir);
        SortedDictionary<string, string> copyData = ProjectTestSupport.Snapshot(copy.Paths.MetaDir);
        foreach (KeyValuePair<string, string> entry in sourceData)
        {
            if (entry.Key.EndsWith(".tmp", StringComparison.Ordinal))
            {
                Assert.DoesNotContain(entry.Key, copyData.Keys);
            }
            else if (entry.Key != "project.json")
            {
                Assert.True(copyData.TryGetValue(entry.Key, out string? hash), "missing in the copy: " + entry.Key);
                Assert.Equal(entry.Value, hash);
            }
        }

        Assert.Equal(source.Manifest.CreatedUtc, ProjectManifestStore.Read(source.Manifest.ManifestPath).CreatedUtc);
        Assert.NotEqual(source.Manifest.ManifestPath, copy.Manifest.ManifestPath);

        Assert.Equal(ProjectCopyPhase.Scanning, progress.Items.First().Phase);
        ProjectCopyProgress last = progress.Items.Last();
        Assert.Equal(ProjectCopyPhase.Done, last.Phase);
        Assert.True(last.FilesTotal > 0);
        Assert.Equal(last.FilesTotal, last.FilesDone);
        Assert.Equal(last.BytesTotal, last.BytesDone);
        Assert.DoesNotContain(progress.Items, p => p.Phase == ProjectCopyPhase.CopyingImages);
        int previous = 0;
        foreach (ProjectCopyProgress item in progress.Items)
        {
            Assert.True(item.FilesDone >= previous);
            previous = item.FilesDone;
        }
    }

    [Fact]
    public void SaveAs_WithCopyingImages_CopiesImagesAndTheirJsonOnly_KeepingSubFolders()
    {
        ProjectOpenResult source = BuildSource("saveas-copy", out string sourceLocation);
        SortedDictionary<string, string> sourceBefore = ProjectTestSupport.Snapshot(sourceLocation);
        string destination = ProjectTestSupport.NewFolder("saveas-copy-dest");

        ProjectOpenResult copy = _manager.SaveAs(source.Project, "Copy", destination, true, null, CancellationToken.None);

        Assert.Equal(sourceBefore, ProjectTestSupport.Snapshot(sourceLocation));
        string images = Path.Combine(destination, "Copy.images");
        Assert.Equal(new[] { "Copy.easyai", "Copy.easyai.data", "Copy.images" }, ProjectTestSupport.Names(destination));
        Assert.Equal(images, copy.Paths.ImageRoot);
        Assert.Equal(images, copy.Manifest.ImageRootFullPath);
        Assert.Equal("Copy.images", ReadJson(copy.Manifest.ManifestPath).Value<string>("imageRoot"));
        Assert.Equal(new[] { "a.json", "a.png", "c.jpg", "sub/", "sub/b.json", "sub/b.png" }, Keys(ProjectTestSupport.Snapshot(images)));
        Assert.Equal(3, copy.Project.Images.Count);

        SortedDictionary<string, string> sourceImages = ProjectTestSupport.Snapshot(source.Paths.ImageRoot);
        SortedDictionary<string, string> copiedImages = ProjectTestSupport.Snapshot(images);
        foreach (KeyValuePair<string, string> entry in copiedImages)
        {
            Assert.Equal(sourceImages[entry.Key], entry.Value);
        }
    }

    [Fact]
    public void SaveAs_NeverOverwrites_TheManifestTheDataFolderOrTheImagesFolder()
    {
        ProjectOpenResult source = BuildSource("saveas-exists", out string sourceLocation);
        SortedDictionary<string, string> sourceBefore = ProjectTestSupport.Snapshot(sourceLocation);
        string destination = ProjectTestSupport.NewFolder("saveas-exists-dest");
        ProjectTestSupport.Write(Path.Combine(destination, "M.easyai"), "keep-manifest");
        ProjectTestSupport.Write(Path.Combine(destination, "D.easyai.data", "keep.txt"), "keep-data");
        ProjectTestSupport.Write(Path.Combine(destination, "I.images", "keep.png"), "keep-images");
        SortedDictionary<string, string> destinationBefore = ProjectTestSupport.Snapshot(destination);

        foreach ((string name, bool copyImages) in new[] { ("M", false), ("M", true), ("D", false), ("D", true), ("I", true) })
        {
            ProjectException ex = Assert.Throws<ProjectException>(() => _manager.SaveAs(source.Project, name, destination, copyImages, null, CancellationToken.None));
            Assert.Equal(ProjectErrorCode.TargetExists, ex.Code);
            Assert.Equal(destinationBefore, ProjectTestSupport.Snapshot(destination));
            Assert.Equal(sourceBefore, ProjectTestSupport.Snapshot(sourceLocation));
        }

        // Saving onto itself is the same refusal.
        Assert.Equal(
            ProjectErrorCode.TargetExists,
            Assert.Throws<ProjectException>(() => _manager.SaveAs(source.Project, "Src", sourceLocation, false, null, CancellationToken.None)).Code);
        Assert.Equal(sourceBefore, ProjectTestSupport.Snapshot(sourceLocation));
    }

    [Fact]
    public void SaveAs_WhenCancelled_LeavesNoManifestAndNoPartialCopy()
    {
        ProjectOpenResult source = BuildSource("saveas-cancel", out string sourceLocation);
        SortedDictionary<string, string> sourceBefore = ProjectTestSupport.Snapshot(sourceLocation);

        // Cancelled in the middle of the data copy, into an existing folder that holds something else.
        string existing = ProjectTestSupport.NewFolder("saveas-cancel-existing");
        ProjectTestSupport.Write(Path.Combine(existing, "keep.txt"), "k");
        using (CancellationTokenSource first = new CancellationTokenSource())
        {
            SyncProgress<ProjectCopyProgress> progress = new SyncProgress<ProjectCopyProgress>(p =>
            {
                if (p.Phase == ProjectCopyPhase.CopyingData && p.FilesDone >= 2) first.Cancel();
            });
            Assert.Throws<OperationCanceledException>(() => _manager.SaveAs(source.Project, "Copy", existing, true, progress, first.Token));
        }

        Assert.Equal(new[] { "keep.txt" }, ProjectTestSupport.Names(existing));

        // Cancelled while copying images, into a location that did not exist.
        string fresh = Path.Combine(ProjectTestSupport.NewFolder("saveas-cancel-fresh"), "new-location");
        using (CancellationTokenSource second = new CancellationTokenSource())
        {
            SyncProgress<ProjectCopyProgress> progress = new SyncProgress<ProjectCopyProgress>(p =>
            {
                if (p.Phase == ProjectCopyPhase.CopyingImages && p.FilesDone >= 1) second.Cancel();
            });
            Assert.Throws<OperationCanceledException>(() => _manager.SaveAs(source.Project, "Copy", fresh, true, progress, second.Token));
        }

        Assert.False(Directory.Exists(fresh));

        // Cancelled before anything started.
        using (CancellationTokenSource third = new CancellationTokenSource())
        {
            third.Cancel();
            Assert.Throws<OperationCanceledException>(() => _manager.SaveAs(source.Project, "Copy", existing, false, null, third.Token));
        }

        Assert.Equal(new[] { "keep.txt" }, ProjectTestSupport.Names(existing));
        Assert.Equal(sourceBefore, ProjectTestSupport.Snapshot(sourceLocation));
    }

    [Fact]
    public void SaveAs_RefusesALocationInsideWhatItCopiesFrom_AndAnInvalidName()
    {
        ProjectOpenResult source = BuildSource("saveas-inside", out string sourceLocation);
        SortedDictionary<string, string> sourceBefore = ProjectTestSupport.Snapshot(sourceLocation);

        string insideData = Path.Combine(source.Paths.MetaDir, "zzz");
        Assert.Equal(ProjectErrorCode.InvalidLocation, Assert.Throws<ProjectException>(() => _manager.SaveAs(source.Project, "X", insideData, false, null, CancellationToken.None)).Code);
        string insideImages = Path.Combine(source.Paths.ImageRoot, "zzz");
        Assert.Equal(ProjectErrorCode.InvalidLocation, Assert.Throws<ProjectException>(() => _manager.SaveAs(source.Project, "X", insideImages, true, null, CancellationToken.None)).Code);

        ProjectException name = Assert.Throws<ProjectException>(() => _manager.SaveAs(source.Project, "a:b", ProjectTestSupport.NewFolder("saveas-inside-dest"), false, null, CancellationToken.None));
        Assert.Equal(ProjectErrorCode.InvalidName, name.Code);
        Assert.Equal(ProjectNameError.InvalidCharacter, name.NameError);

        Assert.Equal(sourceBefore, ProjectTestSupport.Snapshot(sourceLocation));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SaveAs_FromALegacyProject_ConvertsItAndLeavesTheOriginalFolderByteIdentical(bool copyImages)
    {
        string root = ProjectTestSupport.NewFolder("saveas-legacy");
        ProjectTestSupport.WriteImage(root, "a.png", true);
        ProjectTestSupport.WriteImage(root, "sub/b.jpg", true);
        ProjectTestSupport.Write(Path.Combine(root, "classes.txt"), "NG\nOK\nScratch\n");
        Project legacy = new ProjectService().Open(root);
        ProjectTestSupport.Write(Path.Combine(root, ".easyai", "cache", "a.seg.json"), "{\"Schema\":1}");
        ProjectTestSupport.Write(Path.Combine(root, ".easyai", "agent-memory.md"), "legacy memory");
        Assert.True(legacy.Paths.IsLegacyPlacement);
        Assert.Null(legacy.Manifest);
        SortedDictionary<string, string> before = ProjectTestSupport.Snapshot(root);
        string destination = ProjectTestSupport.NewFolder("saveas-legacy-dest");

        ProjectOpenResult converted = _manager.SaveAs(legacy, "Converted", destination, copyImages, null, CancellationToken.None);

        Assert.Equal(before, ProjectTestSupport.Snapshot(root));
        Assert.False(converted.Paths.IsLegacyPlacement);
        Assert.NotNull(converted.Project.Manifest);
        Assert.Equal(Path.Combine(destination, "Converted.easyai.data"), converted.Paths.MetaDir);
        Assert.Equal("NG\nOK\nScratch\n", File.ReadAllText(converted.Paths.ClassesFile));
        Assert.Equal(new[] { "NG", "OK", "Scratch" }, converted.Project.Classes);
        Assert.Equal("legacy memory", File.ReadAllText(converted.Paths.AgentMemoryFile));
        Assert.True(File.Exists(Path.Combine(converted.Paths.CacheDir, "a.seg.json")));
        Assert.False(Directory.Exists(Path.Combine(destination, ".easyai")));
        Assert.Equal(2, converted.Project.Images.Count);

        if (copyImages)
        {
            string images = Path.Combine(destination, "Converted.images");
            Assert.Equal(images, converted.Paths.ImageRoot);
            Assert.Equal(new[] { "a.json", "a.png", "sub/", "sub/b.jpg", "sub/b.json" }, Keys(ProjectTestSupport.Snapshot(images)));
        }
        else
        {
            Assert.Equal(root, converted.Paths.ImageRoot);
            Assert.Equal(new[] { "Converted.easyai", "Converted.easyai.data" }, ProjectTestSupport.Names(destination));
        }
    }
}
