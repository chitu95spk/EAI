using EasyLabel.SegAI.Native;

namespace EasyLabel.SegAI.Tests;

internal static class SegAiFixtures
{
    internal const string Synthetic = "tmp/ToolUpgrade/SegAI-fixtures/synthetic.segai";
    internal const string V2Both = "artifacts/V133/v2-both/model.segai";
    /// <summary>V1 model that really detects a region on artifacts/V133/fixtures/0.png (v2-both returns no regions, so it cannot prove parity).</summary>
    internal const string NgOnly = "artifacts/V133/native-checks/ng-only/model.segai";
    internal const string Scan = "artifacts/V135/baseline/scan/scan-1.segai";

    /// <summary>Nearest ancestor of the test output folder that contains both 'EasyLabel' and 'BeeNativeSegAI'.</summary>
    internal static string? RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "EasyLabel"))
                && Directory.Exists(Path.Combine(dir.FullName, "BeeNativeSegAI")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        return null;
    }

    internal static string? Resolve(string relative)
    {
        string? root = RepoRoot();
        if (root is null)
        {
            return null;
        }

        string full = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        return File.Exists(full) ? full : null;
    }

    /// <summary>First existing fixture of the candidates, or null.</summary>
    internal static string? ResolveAny(params string[] relatives)
    {
        foreach (string relative in relatives)
        {
            string? full = Resolve(relative);
            if (full is not null)
            {
                return full;
            }
        }

        return null;
    }

    /// <summary>Writable scratch folder under the test output folder (never %TEMP%).</summary>
    internal static string ScratchFolder(string name)
    {
        string folder = Path.Combine(AppContext.BaseDirectory, "scratch", name);
        Directory.CreateDirectory(folder);
        return folder;
    }
}

/// <summary>Skips at discovery when none of the fixtures exists, and optionally when BeeNativeSegAI.dll is missing.</summary>
public sealed class SegAiFixtureFactAttribute : FactAttribute
{
    public SegAiFixtureFactAttribute(bool requiresNative, params string[] relativeCandidates)
    {
        if (SegAiFixtures.ResolveAny(relativeCandidates) is null)
        {
            Skip = "Fixture missing: " + string.Join(", ", relativeCandidates);
            return;
        }

        if (requiresNative)
        {
            NativeSegAiResolution resolution = NativeSegAiFolderResolver.Resolve(
                new NativeSegAiOptions(null, Array.Empty<string>()));
            if (resolution.Folder is null)
            {
                Skip = NativeSegAiFolderResolver.DllName + " not found. Probed: " + string.Join("; ", resolution.Probed);
            }
        }
    }
}
