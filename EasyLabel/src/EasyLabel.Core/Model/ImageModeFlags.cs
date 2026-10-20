namespace EasyLabel.Core.Model;

/// <summary>
/// Segment AI Train/Val choice of an image, stored as the image-level LabelMe flags <c>train</c> and <c>val</c>
/// (neither set = Auto; both set is treated as Auto).
/// </summary>
public static class ImageModeFlags
{
    public const string TrainFlag = "train";
    public const string ValFlag = "val";

    public const byte Auto = 0, Train = 1, Val = 2;

    public static byte Read(IReadOnlyDictionary<string, bool>? flags)
    {
        if (flags == null) return Auto;
        bool train = Lookup(flags, TrainFlag);
        bool val = Lookup(flags, ValFlag);
        return train == val ? Auto : train ? Train : Val;
    }

    /// <summary>Auto removes both keys; Train/Val set one key to true and drop the other. Other flags are untouched.</summary>
    public static void Apply(IDictionary<string, bool> flags, byte mode)
    {
        ArgumentNullException.ThrowIfNull(flags);
        Remove(flags, TrainFlag);
        Remove(flags, ValFlag);
        if (mode == Train) flags[TrainFlag] = true;
        else if (mode == Val) flags[ValFlag] = true;
    }

    /// <summary>True when the flags already carry a train or val key (even a false one), so a migration must not override it.</summary>
    public static bool HasKey(IReadOnlyDictionary<string, bool>? flags)
    {
        if (flags == null) return false;
        foreach (string key in flags.Keys)
            if (IsKey(key)) return true;
        return false;
    }

    private static bool IsKey(string key)
    {
        return string.Equals(key, TrainFlag, StringComparison.OrdinalIgnoreCase) || string.Equals(key, ValFlag, StringComparison.OrdinalIgnoreCase);
    }

    private static bool Lookup(IReadOnlyDictionary<string, bool> flags, string name)
    {
        foreach (KeyValuePair<string, bool> pair in flags)
            if (pair.Value && string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static void Remove(IDictionary<string, bool> flags, string name)
    {
        foreach (string key in flags.Keys.Where(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase)).ToList())
            flags.Remove(key);
    }
}
