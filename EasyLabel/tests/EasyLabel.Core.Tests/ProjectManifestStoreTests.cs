using EasyLabel.Core.Projects;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace EasyLabel.Core.Tests;

public sealed class ProjectManifestStoreTests
{
    [Fact]
    public void CreateNew_WriteAndRead_RoundTripEveryField()
    {
        string folder = ProjectTestSupport.NewFolder("manifest-roundtrip");
        string images = Path.Combine(folder, "Line1.images");
        string path = Path.Combine(folder, "Line1.easyai");

        ProjectManifest created = ProjectManifestStore.CreateNew(path, "  Line1 ", images);
        ProjectManifestStore.Write(created);
        ProjectManifest read = ProjectManifestStore.Read(path);

        Assert.Equal(3, read.Schema);
        Assert.Equal("Line1", read.Name);
        Assert.Equal(created.CreatedUtc, read.CreatedUtc);
        Assert.Equal(created.LastOpenedUtc, read.LastOpenedUtc);
        Assert.Equal(DateTimeKind.Utc, read.CreatedUtc.Kind);
        Assert.Equal("Line1.images", read.ImageRoot);
        Assert.Equal("Line1.easyai.data", read.DataDir);
        Assert.Equal(path, read.ManifestPath);
        Assert.Equal(images, read.ImageRootFullPath);
        Assert.Equal(Path.Combine(folder, "Line1.easyai.data"), read.DataDirFullPath);
        Assert.Equal(images, read.ToPaths().ImageRoot);
        Assert.Equal(read.DataDirFullPath, read.ToPaths().MetaDir);
    }

    [Fact]
    public void Write_IsUtf8WithoutBom_LeavesNoTemporaryFile_AndReplacesAnExistingFile()
    {
        string folder = ProjectTestSupport.NewFolder("manifest-atomic");
        string path = Path.Combine(folder, "P.easyai");
        ProjectManifest manifest = ProjectManifestStore.CreateNew(path, "Tên Việt", Path.Combine(folder, "img"));

        ProjectManifestStore.Write(manifest);
        manifest.Name = "Second";
        ProjectManifestStore.Write(manifest);

        byte[] bytes = File.ReadAllBytes(path);
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);
        Assert.Equal("Second", JObject.Parse(File.ReadAllText(path)).Value<string>("name"));
        Assert.Equal(new[] { "P.easyai" }, ProjectTestSupport.Names(folder));
    }

    [Fact]
    public void Write_KeepsFieldsThisVersionDoesNotKnow()
    {
        string folder = ProjectTestSupport.NewFolder("manifest-unknown");
        string path = Path.Combine(folder, "P.easyai");
        ProjectTestSupport.Write(path, "{\"schema\":3,\"name\":\"P\",\"createdUtc\":\"2026-01-02T03:04:05.0000000Z\",\"lastOpenedUtc\":\"2026-01-02T03:04:05.0000000Z\","
            + "\"imageRoot\":\"img\",\"dataDir\":\"P.easyai.data\",\"futureFlag\":true,\"futureObject\":{\"a\":[1,2,3],\"b\":\"x\"}}");

        ProjectManifest manifest = ProjectManifestStore.Read(path);
        manifest.LastOpenedUtc = new DateTime(2026, 5, 6, 7, 8, 9, DateTimeKind.Utc);
        ProjectManifestStore.Write(manifest);

        JObject json = JsonConvert.DeserializeObject<JObject>(File.ReadAllText(path), new JsonSerializerSettings { DateParseHandling = DateParseHandling.None })!;
        Assert.True(json.Value<bool>("futureFlag"));
        Assert.Equal(3, json["futureObject"]!["a"]!.Count());
        Assert.Equal("x", json["futureObject"]!.Value<string>("b"));
        Assert.Equal("2026-05-06T07:08:09.0000000Z", json.Value<string>("lastOpenedUtc"));
        Assert.Equal("2026-01-02T03:04:05.0000000Z", json.Value<string>("createdUtc"));

        // And once more from the rewritten file.
        ProjectManifest again = ProjectManifestStore.Read(path);
        ProjectManifestStore.Write(again);
        Assert.True(JObject.Parse(File.ReadAllText(path)).Value<bool>("futureFlag"));
    }

    [Fact]
    public void ImageRoot_IsStoredRelativeOnTheSameDrive_AndFollowsTheManifestWhenBothMove()
    {
        string root = ProjectTestSupport.NewFolder("manifest-relative");
        string projects = Path.Combine(root, "projects");
        string images = Path.Combine(root, "shared", "images");
        Directory.CreateDirectory(projects);
        Directory.CreateDirectory(images);
        string path = Path.Combine(projects, "P.easyai");

        ProjectManifestStore.Write(ProjectManifestStore.CreateNew(path, "P", images));

        ProjectManifest read = ProjectManifestStore.Read(path);
        Assert.Equal("../shared/images", read.ImageRoot);
        Assert.Equal(images, read.ImageRootFullPath);

        // The same relative text resolves against wherever the manifest is now.
        string moved = Path.Combine(root, "elsewhere", "deeper");
        Directory.CreateDirectory(moved);
        string movedPath = Path.Combine(moved, "P.easyai");
        File.Copy(path, movedPath);
        Assert.Equal(Path.GetFullPath(Path.Combine(moved, "..", "shared", "images")), ProjectManifestStore.Read(movedPath).ImageRootFullPath);
    }

    [Fact]
    public void ImageRoot_OnAnotherDriveIsStoredAbsolute()
    {
        // Pure path arithmetic: neither drive has to exist.
        string stored = ProjectManifest.FormatImageRoot(@"C:\Projects\One", @"D:\Pictures\Set");
        Assert.Equal(@"D:\Pictures\Set", stored);
        Assert.Equal(@"D:\Pictures\Set", ProjectManifest.ResolveImageRoot(@"C:\Projects\One", stored));
        Assert.Equal(@"C:\Projects\Pictures", ProjectManifest.ResolveImageRoot(@"C:\Projects\One", "../Pictures"));
        Assert.Equal(".", ProjectManifest.FormatImageRoot(@"C:\Projects\One", @"C:\Projects\One"));
        Assert.Equal("img/a", ProjectManifest.FormatImageRoot(@"C:\Projects\One", @"C:\Projects\One\img\a"));
    }

    [Fact]
    public void Read_ResolvesAnAbsoluteImageRootAsIs_AndFillsMissingOptionalFields()
    {
        string folder = ProjectTestSupport.NewFolder("manifest-absolute");
        string absolute = Path.Combine(ProjectTestSupport.NewFolder("manifest-absolute-img"), "photos");
        string path = ProjectTestSupport.Write(Path.Combine(folder, "Named.easyai"),
            new JObject { ["schema"] = 3, ["imageRoot"] = absolute }.ToString());

        ProjectManifest manifest = ProjectManifestStore.Read(path);

        Assert.Equal(absolute, manifest.ImageRootFullPath);
        Assert.Equal("Named", manifest.Name);
        Assert.Equal("Named.easyai.data", manifest.DataDir);
        Assert.Equal(manifest.CreatedUtc, manifest.LastOpenedUtc);
    }

    [Fact]
    public void Read_RefusesAMissingFile_AFolder_AndInvalidContent()
    {
        string folder = ProjectTestSupport.NewFolder("manifest-invalid");

        Assert.Equal(ProjectErrorCode.ManifestMissing, Assert.Throws<ProjectException>(() => ProjectManifestStore.Read(Path.Combine(folder, "none.easyai"))).Code);

        Directory.CreateDirectory(Path.Combine(folder, ".easyai"));
        Assert.Equal(ProjectErrorCode.ManifestInvalid, Assert.Throws<ProjectException>(() => ProjectManifestStore.Read(Path.Combine(folder, ".easyai"))).Code);

        string[] bad =
        {
            "not json at all",
            "",
            "[1,2]",
            "{\"schema\":2,\"imageRoot\":\"img\"}",
            "{\"imageRoot\":\"img\"}",
            "{\"schema\":3}",
            "{\"schema\":3,\"imageRoot\":\"  \"}",
            "{\"schema\":3,\"imageRoot\":\"img\",\"dataDir\":\"..\\\\x\"}",
            "{\"schema\":3,\"imageRoot\":\"img\",\"dataDir\":\"a/b\"}",
            "{\"schema\":3,\"imageRoot\":\"img\",\"dataDir\":\"..\"}",
        };
        for (int i = 0; i < bad.Length; i++)
        {
            string path = ProjectTestSupport.Write(Path.Combine(folder, "bad" + i + ".easyai"), bad[i]);
            ProjectException ex = Assert.Throws<ProjectException>(() => ProjectManifestStore.Read(path));
            Assert.Equal(ProjectErrorCode.ManifestInvalid, ex.Code);
        }
    }
}
