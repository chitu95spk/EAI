namespace EasyLabel.SegAI.Tests;

public sealed class EasyVisionPublisherTests
{
    private static readonly System.DateTime Stamp = new System.DateTime(2026, 10, 12, 3, 4, 5, System.DateTimeKind.Utc);

    private static readonly EasyLabel.SegAI.Export.PublishMetadata Meta =
        new EasyLabel.SegAI.Export.PublishMetadata("Line1", "v002.segai", "mAP50=0.91 epochs=40", "abc123", "1.2.3.4");

    private static string NewModel(string root, string note = "one")
    {
        string path = System.IO.Path.Combine(root, "source", "v002.segai");
        ModelStub.Write(path, note);
        return path;
    }

    private static string NewTarget(string root)
    {
        string target = System.IO.Path.Combine(root, "easyvision");
        System.IO.Directory.CreateDirectory(target);
        return target;
    }

    private static EasyLabel.SegAI.Export.PublishResult Publish(string model, string? target, EasyLabel.SegAI.Export.EasyVisionPublisher? publisher = null)
    {
        return (publisher ?? new EasyLabel.SegAI.Export.EasyVisionPublisher()).Publish(model, target, "Line1", Meta, () => Stamp);
    }

    private static string[] Names(string folder)
    {
        return System.IO.Directory.GetFiles(folder).Select(System.IO.Path.GetFileName).OrderBy(n => n, System.StringComparer.Ordinal).ToArray()!;
    }

    [Fact]
    public void FirstPublish_IsVersion1_WithSidecar_AndNoTempLeft()
    {
        string root = TrainFixtures.Folder("evpub");
        string model = NewModel(root);
        string target = NewTarget(root);

        EasyLabel.SegAI.Export.PublishResult result = Publish(model, target);

        Assert.True(result.Success);
        Assert.Equal(1, result.Version);
        Assert.Equal(System.IO.Path.Combine(target, "Line1_v1.segai"), result.ModelPath);
        Assert.Equal(System.IO.Path.Combine(target, "Line1_v1.json"), result.SidecarPath);
        Assert.Equal(new[] { "Line1_v1.json", "Line1_v1.segai" }, Names(target));
        Assert.Equal(System.IO.File.ReadAllBytes(model), System.IO.File.ReadAllBytes(result.ModelPath));
    }

    [Fact]
    public void Sidecar_HoldsTheRecordedFacts()
    {
        string root = TrainFixtures.Folder("evpub");
        string model = NewModel(root);
        string target = NewTarget(root);

        EasyLabel.SegAI.Export.PublishResult result = Publish(model, target);

        Newtonsoft.Json.Linq.JObject json = Newtonsoft.Json.JsonConvert.DeserializeObject<Newtonsoft.Json.Linq.JObject>(
            System.IO.File.ReadAllText(result.SidecarPath),
            new Newtonsoft.Json.JsonSerializerSettings { DateParseHandling = Newtonsoft.Json.DateParseHandling.None })!;
        Assert.Equal("Line1", (string?)json["projectName"]);
        Assert.Equal("v002.segai", (string?)json["runId"]);
        Assert.Equal("mAP50=0.91 epochs=40", (string?)json["metrics"]);
        Assert.Equal("abc123", (string?)json["datasetHash"]);
        Assert.Equal("1.2.3.4", (string?)json["easyLabelVersion"]);
        Assert.Equal(1, (int?)json["modelVersion"]);
        Assert.Equal(3, (int?)json["segaiFormatVersion"]);
        Assert.Equal("Line1_v1.segai", (string?)json["fileName"]);
        Assert.Equal("2026-10-12T03:04:05.000Z", (string?)json["publishedUtc"]);
        string expected = System.Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.IO.File.ReadAllBytes(model))).ToLowerInvariant();
        Assert.Equal(expected, (string?)json["sourceSha256"]);
    }

    [Fact]
    public void SameModelTwice_IsVersion2_AndVersion1IsKept()
    {
        string root = TrainFixtures.Folder("evpub");
        string model = NewModel(root);
        string target = NewTarget(root);

        EasyLabel.SegAI.Export.PublishResult first = Publish(model, target);
        byte[] firstBytes = System.IO.File.ReadAllBytes(first.ModelPath);
        EasyLabel.SegAI.Export.PublishResult second = Publish(model, target);

        Assert.Equal(2, second.Version);
        Assert.True(System.IO.File.Exists(first.ModelPath));
        Assert.Equal(firstBytes, System.IO.File.ReadAllBytes(first.ModelPath));
        Assert.Equal(4, Names(target).Length);
    }

    [Fact]
    public void Numbering_UsesHighestPlusOne_AndIgnoresOtherNames()
    {
        string root = TrainFixtures.Folder("evpub");
        string model = NewModel(root);
        string target = NewTarget(root);
        System.IO.File.WriteAllText(System.IO.Path.Combine(target, "Line1_v1.segai"), "old1");
        System.IO.File.WriteAllText(System.IO.Path.Combine(target, "Line1_v3.segai"), "old3");
        System.IO.File.WriteAllText(System.IO.Path.Combine(target, "Other_v9.segai"), "other");
        System.IO.File.WriteAllText(System.IO.Path.Combine(target, "Line1_v7.json"), "{}");

        EasyLabel.SegAI.Export.PublishResult result = Publish(model, target);

        Assert.Equal(4, result.Version);
        Assert.Equal("old1", System.IO.File.ReadAllText(System.IO.Path.Combine(target, "Line1_v1.segai")));
        Assert.Equal("old3", System.IO.File.ReadAllText(System.IO.Path.Combine(target, "Line1_v3.segai")));
    }

    [Fact]
    public void NextVersion_CountsFromOneAndSkipsGaps()
    {
        string target = NewTarget(TrainFixtures.Folder("evpub"));
        Assert.Equal(1, EasyLabel.SegAI.Export.EasyVisionPublisher.NextVersion(target, "M"));
        System.IO.File.WriteAllText(System.IO.Path.Combine(target, "M_v2.segai"), "x");
        System.IO.File.WriteAllText(System.IO.Path.Combine(target, "m_v10.SEGAI"), "x");
        Assert.Equal(11, EasyLabel.SegAI.Export.EasyVisionPublisher.NextVersion(target, "M"));
    }

    [Fact]
    public void ForeignSidecar_IsNeverOverwritten_AndNothingIsLeft()
    {
        string root = TrainFixtures.Folder("evpub");
        string model = NewModel(root);
        string target = NewTarget(root);
        string foreign = System.IO.Path.Combine(target, "Line1_v1.json");
        System.IO.File.WriteAllText(foreign, "precious");

        EasyLabel.SegAI.Export.PublishResult result = Publish(model, target);

        Assert.Equal(EasyLabel.SegAI.Export.PublishError.Io, result.Error);
        Assert.Equal("precious", System.IO.File.ReadAllText(foreign));
        Assert.Equal(new[] { "Line1_v1.json" }, Names(target));
    }

    [Theory]
    [InlineData("copy")]
    [InlineData("sidecar")]
    public void FailureAtAnyStep_LeavesNoTempAndNoHalfPublishedModel(string stage)
    {
        string root = TrainFixtures.Folder("evpub");
        string model = NewModel(root);
        string target = NewTarget(root);
        System.IO.File.WriteAllText(System.IO.Path.Combine(target, "Line1_v1.segai"), "keep");
        var publisher = new EasyLabel.SegAI.Export.EasyVisionPublisher
        {
            FaultHook = s => { if (s == stage) throw new System.IO.IOException("injected"); },
        };

        EasyLabel.SegAI.Export.PublishResult result = Publish(model, target, publisher);

        Assert.Equal(EasyLabel.SegAI.Export.PublishError.Io, result.Error);
        Assert.Equal(new[] { "Line1_v1.segai" }, Names(target));
        Assert.Equal("keep", System.IO.File.ReadAllText(System.IO.Path.Combine(target, "Line1_v1.segai")));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void UnsetTarget_IsRefused(string? target)
    {
        string model = NewModel(TrainFixtures.Folder("evpub"));
        Assert.Equal(EasyLabel.SegAI.Export.PublishError.FolderNotSet, Publish(model, target).Error);
    }

    [Fact]
    public void MissingTarget_IsRefused_AndNotCreated()
    {
        string root = TrainFixtures.Folder("evpub");
        string model = NewModel(root);
        string target = System.IO.Path.Combine(root, "nope");

        Assert.Equal(EasyLabel.SegAI.Export.PublishError.FolderMissing, Publish(model, target).Error);
        Assert.False(System.IO.Directory.Exists(target));
    }

    [Fact]
    public void ReadOnlyTarget_IsRefused_AndLeavesNoTemp()
    {
        if (!System.OperatingSystem.IsWindows()) return;
        string root = TrainFixtures.Folder("evpub");
        string model = NewModel(root);
        string target = NewTarget(root);
        var info = new System.IO.DirectoryInfo(target);
        System.Security.Principal.SecurityIdentifier me = System.Security.Principal.WindowsIdentity.GetCurrent().User!;
        var rule = new System.Security.AccessControl.FileSystemAccessRule(
            me,
            System.Security.AccessControl.FileSystemRights.CreateFiles | System.Security.AccessControl.FileSystemRights.WriteData,
            System.Security.AccessControl.InheritanceFlags.None,
            System.Security.AccessControl.PropagationFlags.None,
            System.Security.AccessControl.AccessControlType.Deny);
        System.Security.AccessControl.DirectorySecurity security = info.GetAccessControl();
        security.AddAccessRule(rule);
        info.SetAccessControl(security);
        try
        {
            EasyLabel.SegAI.Export.PublishResult result = Publish(model, target);
            Assert.Equal(EasyLabel.SegAI.Export.PublishError.FolderNotWritable, result.Error);
        }
        finally
        {
            security = info.GetAccessControl();
            security.RemoveAccessRule(rule);
            info.SetAccessControl(security);
        }

        Assert.Empty(Names(target));
    }

    [Fact]
    public void MissingModel_IsRefused()
    {
        string root = TrainFixtures.Folder("evpub");
        string target = NewTarget(root);

        Assert.Equal(EasyLabel.SegAI.Export.PublishError.ModelMissing, Publish(System.IO.Path.Combine(root, "gone.segai"), target).Error);
        Assert.Empty(Names(target));
    }

    [Fact]
    public void CorruptModel_IsRefused()
    {
        string root = TrainFixtures.Folder("evpub");
        string model = System.IO.Path.Combine(root, "bad.segai");
        ModelStub.WriteInvalid(model);
        string target = NewTarget(root);

        Assert.Equal(EasyLabel.SegAI.Export.PublishError.ModelInvalid, Publish(model, target).Error);
        Assert.Empty(Names(target));
    }

    [Fact]
    public void TruncatedModel_IsRefused()
    {
        string root = TrainFixtures.Folder("evpub");
        string model = NewModel(root);
        byte[] all = System.IO.File.ReadAllBytes(model);
        System.IO.File.WriteAllBytes(model, all.Take(all.Length - 5).ToArray());
        string target = NewTarget(root);

        Assert.Equal(EasyLabel.SegAI.Export.PublishError.ModelInvalid, Publish(model, target).Error);
        Assert.Empty(Names(target));
    }

    [Fact]
    public void FormatVersionNewerThanEasyVision_IsRefused()
    {
        string root = TrainFixtures.Folder("evpub");
        string model = NewModel(root);
        byte[] all = System.IO.File.ReadAllBytes(model);
        System.BitConverter.GetBytes(5u).CopyTo(all, 8);
        System.IO.File.WriteAllBytes(model, all);
        string target = NewTarget(root);

        EasyLabel.SegAI.Export.PublishResult result = Publish(model, target);

        Assert.Equal(EasyLabel.SegAI.Export.PublishError.UnsupportedVersion, result.Error);
        Assert.Empty(Names(target));
    }

    [Fact]
    public void SafeName_ReplacesInvalidCharacters()
    {
        Assert.Equal("a_b", EasyLabel.SegAI.Export.EasyVisionPublisher.SafeName("a/b"));
        Assert.Equal("model", EasyLabel.SegAI.Export.EasyVisionPublisher.SafeName("  . "));
        Assert.Equal("Line 1", EasyLabel.SegAI.Export.EasyVisionPublisher.SafeName(" Line 1 "));
    }
}
