using EasyLabel.SegAI.Native;

namespace EasyLabel.SegAI.Tests;

/// <summary>Skips at discovery when BeeNativeSegAI.dll cannot be found in the default locations.</summary>
public sealed class NativeSegAiFactAttribute : FactAttribute
{
    public NativeSegAiFactAttribute()
    {
        NativeSegAiResolution resolution = NativeSegAiFolderResolver.Resolve(
            new NativeSegAiOptions(null, Array.Empty<string>()));
        if (resolution.Folder is null)
        {
            Skip = NativeSegAiFolderResolver.DllName + " not found. Probed: " + string.Join("; ", resolution.Probed);
        }
    }
}
