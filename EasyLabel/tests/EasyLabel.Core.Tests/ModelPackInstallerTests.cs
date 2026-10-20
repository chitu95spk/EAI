using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using EasyLabel.Core.Models;
using Newtonsoft.Json;

namespace EasyLabel.Core.Tests;

public sealed class ModelPackInstallerTests : IDisposable
{
    private readonly string _work;

    public ModelPackInstallerTests()
    {
        _work = Path.Combine(AppContext.BaseDirectory, "pack-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_work);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_work))
            {
                Directory.Delete(_work, true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public void ReadManifest_ReturnsLicenceWithoutExtracting()
    {
        byte[] model = Bytes("model-bytes");
        string pack = BuildPack("read.zip", Manifest("classify-base", Entry("model.onnx", "classify-backbone", model)), ("model.onnx", model));

        ModelPackReadResult read = ModelPackInstaller.ReadManifest(pack);

        Assert.True(read.Success, read.Message);
        Assert.Equal("Test licence", read.Manifest!.Files[0].Licence);
        Assert.False(Directory.Exists(Path.Combine(_work, "classify-backbone")));
    }

    [Fact]
    public void ReadManifest_NotAZip_ReportsBadArchive()
    {
        string path = Path.Combine(_work, "plain.zip");
        File.WriteAllText(path, "not a zip");

        Assert.Equal(ModelPackError.BadArchive, ModelPackInstaller.ReadManifest(path).Error);
    }

    [Fact]
    public void Install_ValidPack_PlacesFileUnderTargetAndLeavesNoStaging()
    {
        byte[] model = Bytes("model-bytes");
        string pack = BuildPack("ok.zip", Manifest("classify-base", Entry("model.onnx", "classify-backbone", model)), ("model.onnx", model));
        string root = Path.Combine(_work, "models");

        ModelPackInstallResult result = ModelPackInstaller.Install(pack, root);

        Assert.True(result.Success, result.Message);
        Assert.Equal(model, File.ReadAllBytes(Path.Combine(root, "classify-backbone", "model.onnx")));
        Assert.Empty(StagingFolders(root));
    }

    [Fact]
    public void Install_ZipSlipEntry_IsRejectedAndWritesNothing()
    {
        byte[] model = Bytes("m");
        string pack = BuildPack(
            "slip.zip",
            Manifest("x", Entry("model.onnx", "classify-backbone", model)),
            ("model.onnx", model),
            ("../evil.onnx", Bytes("evil")));
        string root = Path.Combine(_work, "models");

        ModelPackInstallResult result = ModelPackInstaller.Install(pack, root);

        Assert.Equal(ModelPackError.UnsafeEntry, result.Error);
        Assert.False(File.Exists(Path.Combine(_work, "evil.onnx")));
        Assert.False(Directory.Exists(Path.Combine(root, "classify-backbone")));
    }

    [Fact]
    public void Install_UnsafeTargetFolder_IsRejected()
    {
        byte[] model = Bytes("m");
        string pack = BuildPack("target.zip", Manifest("x", Entry("model.onnx", "..", model)), ("model.onnx", model));
        string root = Path.Combine(_work, "models");

        ModelPackInstallResult result = ModelPackInstaller.Install(pack, root);

        Assert.Equal(ModelPackError.UnsafeEntry, result.Error);
        Assert.False(File.Exists(Path.Combine(_work, "model.onnx")));
    }

    [Fact]
    public void Install_EntryNotListedInManifest_IsRejected()
    {
        byte[] model = Bytes("m");
        string pack = BuildPack("extra.zip", Manifest("x", Entry("model.onnx", "classify-backbone", model)), ("model.onnx", model), ("extra.bin", Bytes("x")));

        ModelPackInstallResult result = ModelPackInstaller.Install(pack, Path.Combine(_work, "models"));

        Assert.Equal(ModelPackError.UnlistedEntry, result.Error);
    }

    [Fact]
    public void Install_BadHash_NamesFileAndLeavesModelsRootUntouched()
    {
        byte[] model = Bytes("model-bytes");
        string pack = BuildPack("bad.zip", Manifest("x", ("model.onnx", "classify-backbone", new string('0', 64), (long)model.Length)), ("model.onnx", model));
        string root = Path.Combine(_work, "models");

        ModelPackInstallResult result = ModelPackInstaller.Install(pack, root);

        Assert.Equal(ModelPackError.BadHash, result.Error);
        Assert.Equal("model.onnx", result.BadFile);
        Assert.False(Directory.Exists(Path.Combine(root, "classify-backbone")));
        Assert.Empty(StagingFolders(root));
    }

    [Fact]
    public void Install_PackOverSizeCap_IsRefusedBeforeExtraction()
    {
        byte[] model = Bytes("model-bytes");
        string pack = BuildPack("big.zip", Manifest("x", Entry("model.onnx", "classify-backbone", model)), ("model.onnx", model));
        string root = Path.Combine(_work, "models");

        ModelPackInstallResult result = ModelPackInstaller.Install(pack, root, new ModelPackInstallOptions { MaxTotalBytes = 4 });

        Assert.Equal(ModelPackError.TooLarge, result.Error);
        Assert.False(Directory.Exists(Path.Combine(root, "classify-backbone")));
    }

    [Fact]
    public void Install_ExistingTarget_IsKeptAsBackup()
    {
        byte[] model = Bytes("new-model");
        string pack = BuildPack("replace.zip", Manifest("x", Entry("model.onnx", "classify-backbone", model)), ("model.onnx", model));
        string root = Path.Combine(_work, "models");
        Directory.CreateDirectory(Path.Combine(root, "classify-backbone"));
        File.WriteAllText(Path.Combine(root, "classify-backbone", "old.onnx"), "old");

        ModelPackInstallResult result = ModelPackInstaller.Install(pack, root);

        Assert.True(result.Success, result.Message);
        Assert.Single(result.BackupFolders);
        Assert.StartsWith("classify-backbone.bak-", result.BackupFolders[0], StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(root, result.BackupFolders[0], "old.onnx")));
        Assert.Equal(model, File.ReadAllBytes(Path.Combine(root, "classify-backbone", "model.onnx")));
    }

    [Fact]
    public void Install_LaterMoveFails_RollsBackEarlierTargets()
    {
        // Windows refuses to rename a folder while a file inside it is open, so the second target's backup move fails.
        byte[] alpha = Bytes("new-alpha");
        byte[] beta = Bytes("new-beta");
        string pack = BuildPack(
            "two.zip",
            Manifest("two", Entry("a.onnx", "alpha", alpha), Entry("b.onnx", "beta", beta)),
            ("a.onnx", alpha),
            ("b.onnx", beta));
        string root = Path.Combine(_work, "models");
        Directory.CreateDirectory(Path.Combine(root, "alpha"));
        File.WriteAllText(Path.Combine(root, "alpha", "old.txt"), "old-alpha");
        Directory.CreateDirectory(Path.Combine(root, "beta"));
        File.WriteAllText(Path.Combine(root, "beta", "old.txt"), "old-beta");

        ModelPackInstallResult result;
        using (FileStream held = new FileStream(Path.Combine(root, "beta", "old.txt"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            result = ModelPackInstaller.Install(pack, root);
        }

        Assert.Equal(ModelPackError.Io, result.Error);
        Assert.Equal("old-alpha", File.ReadAllText(Path.Combine(root, "alpha", "old.txt")));
        Assert.False(File.Exists(Path.Combine(root, "alpha", "a.onnx")));
        Assert.Equal("old-beta", File.ReadAllText(Path.Combine(root, "beta", "old.txt")));
        Assert.Empty(Directory.GetDirectories(root, "alpha.bak-*"));
        Assert.Empty(StagingFolders(root));
    }

    private static byte[] Bytes(string text)
    {
        return Encoding.UTF8.GetBytes(text);
    }

    private static string Sha256(byte[] data)
    {
        return System.Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
    }

    private static (string File, string Target, string Sha, long Size) Entry(string file, string target, byte[] data)
    {
        return (file, target, Sha256(data), data.Length);
    }

    private static string Manifest(string id, params (string File, string Target, string Sha, long Size)[] files)
    {
        object[] items = files.Select(f => (object)new { file = f.File, size = f.Size, sha256 = f.Sha, licence = "Test licence", target = f.Target }).ToArray();
        return JsonConvert.SerializeObject(new { schema = 1, id, files = items });
    }

    private string BuildPack(string name, string manifest, params (string Name, byte[] Data)[] entries)
    {
        string path = Path.Combine(_work, name);
        using (ZipArchive zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            WriteEntry(zip, "manifest.json", Bytes(manifest));
            foreach ((string entryName, byte[] data) in entries)
            {
                WriteEntry(zip, entryName, data);
            }
        }

        return path;
    }

    private static void WriteEntry(ZipArchive zip, string name, byte[] data)
    {
        using Stream stream = zip.CreateEntry(name).Open();
        stream.Write(data, 0, data.Length);
    }

    private string[] StagingFolders(string root)
    {
        return Directory.Exists(root) ? Directory.GetDirectories(root, ".staging-*") : Array.Empty<string>();
    }
}
