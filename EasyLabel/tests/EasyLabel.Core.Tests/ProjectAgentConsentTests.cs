using EasyLabel.Core.Projects;
using Newtonsoft.Json.Linq;

namespace EasyLabel.Core.Tests;

public sealed class ProjectAgentConsentTests
{
    private const string OldManifest = "{\"schema\":3,\"name\":\"P\",\"createdUtc\":\"2026-01-02T03:04:05.0000000Z\",\"lastOpenedUtc\":\"2026-01-02T03:04:05.0000000Z\","
        + "\"imageRoot\":\"img\",\"dataDir\":\"P.easyai.data\",\"futureFlag\":true}";

    [Fact]
    public void ManifestWithoutTheKeyOpensWithNoConsentDecided()
    {
        string folder = ProjectTestSupport.NewFolder("consent-old");
        string path = Path.Combine(folder, "P.easyai");
        ProjectTestSupport.Write(path, OldManifest);

        ProjectManifest manifest = ProjectManifestStore.Read(path);

        Assert.Null(manifest.AgentConsent);
    }

    [Fact]
    public void ConsentRoundTripsWithItsProvidersAndTime()
    {
        string folder = ProjectTestSupport.NewFolder("consent-roundtrip");
        string path = Path.Combine(folder, "P.easyai");
        ProjectManifest manifest = ProjectManifestStore.CreateNew(path, "P", Path.Combine(folder, "img"));
        DateTime when = new DateTime(2026, 10, 17, 8, 30, 0, DateTimeKind.Utc);
        manifest.AgentConsent = ProjectAgentConsent.Grant(new[] { "claude", " codex ", "claude", "" }, when);

        ProjectManifestStore.Write(manifest);
        ProjectManifest read = ProjectManifestStore.Read(path);

        Assert.NotNull(read.AgentConsent);
        Assert.True(read.AgentConsent!.Granted);
        Assert.Equal(ProjectAgentConsent.CurrentVersion, read.AgentConsent.Version);
        Assert.Equal(when, read.AgentConsent.Utc);
        Assert.Equal(new[] { "claude", "codex" }, read.AgentConsent.Tools);
        Assert.True(read.AgentConsent.Covers("claude"));
        Assert.False(read.AgentConsent.Covers("openai"));
    }

    [Fact]
    public void ConsentForAnOlderTextDoesNotCover()
    {
        ProjectAgentConsent old = new ProjectAgentConsent { Version = 0, Granted = true, Tools = new[] { "claude" } };

        Assert.False(old.Covers("claude"));
    }

    [Fact]
    public void SavingKeepsUnknownKeysWhileConsentIsSetAndRevoked()
    {
        string folder = ProjectTestSupport.NewFolder("consent-unknown");
        string path = Path.Combine(folder, "P.easyai");
        ProjectTestSupport.Write(path, OldManifest);
        ProjectManifest manifest = ProjectManifestStore.Read(path);

        manifest.AgentConsent = ProjectAgentConsent.Grant(new[] { "claude" }, DateTime.UtcNow);
        ProjectManifestStore.Write(manifest);
        JObject granted = JObject.Parse(File.ReadAllText(path));
        Assert.True(granted.Value<bool>("futureFlag"));
        Assert.NotNull(granted["agentConsent"]);

        manifest.AgentConsent = null;
        ProjectManifestStore.Write(manifest);
        JObject revoked = JObject.Parse(File.ReadAllText(path));
        Assert.True(revoked.Value<bool>("futureFlag"));
        Assert.Null(revoked["agentConsent"]);
        Assert.Null(ProjectManifestStore.Read(path).AgentConsent);
    }

    [Fact]
    public void DamagedConsentObjectReadsAsNotDecided()
    {
        string folder = ProjectTestSupport.NewFolder("consent-damaged");
        string path = Path.Combine(folder, "P.easyai");
        ProjectTestSupport.Write(path, OldManifest.Insert(OldManifest.Length - 1, ",\"agentConsent\":{\"granted\":\"yes\"}"));

        ProjectManifest manifest = ProjectManifestStore.Read(path);

        Assert.Equal("P", manifest.Name);
        Assert.Null(manifest.AgentConsent);
    }
}
