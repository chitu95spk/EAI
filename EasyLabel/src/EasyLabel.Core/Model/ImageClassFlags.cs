namespace EasyLabel.Core.Model;

/// <summary>
/// OK/NG classification mark of an image, stored as the image-level LabelMe flags <c>ok</c> and <c>ng</c>. At most one of them is
/// meaningful: both set (or neither) reads as <see cref="Unmarked"/>. Keys are matched case-insensitively. Every other flag, including
/// the Segment AI <c>train</c>/<c>val</c> flags, is left untouched. An OK/NG mark never changes what "labelled" means.
/// </summary>
public static class ImageClassFlags
{
    public const string OkFlag = "ok";
    public const string NgFlag = "ng";

    public const byte Unmarked = 0, Ok = 1, Ng = 2;

    /// <summary>The mark of an image; null flags = <see cref="Unmarked"/>.</summary>
    public static byte Read(IReadOnlyDictionary<string, bool>? flags)
    {
        if (flags == null) return Unmarked;
        bool ok = Lookup(flags, OkFlag);
        bool ng = Lookup(flags, NgFlag);
        return ok == ng ? Unmarked : ok ? Ok : Ng;
    }

    /// <summary>
    /// Sets the mark: removes every <c>ok</c>/<c>ng</c> key (any case), then sets the one for <paramref name="value"/> to true.
    /// <see cref="Unmarked"/> removes both. Other flags are untouched.
    /// </summary>
    public static void Apply(IDictionary<string, bool> flags, byte value)
    {
        ArgumentNullException.ThrowIfNull(flags);
        if (value != Unmarked && value != Ok && value != Ng) throw new ArgumentOutOfRangeException(nameof(value));
        Remove(flags, OkFlag);
        Remove(flags, NgFlag);
        if (value == Ok) flags[OkFlag] = true;
        else if (value == Ng) flags[NgFlag] = true;
    }

    /// <summary>True for the keys this class owns (<c>ok</c>, <c>ng</c>, any case).</summary>
    public static bool IsFlagKey(string key)
    {
        return string.Equals(key, OkFlag, StringComparison.OrdinalIgnoreCase) || string.Equals(key, NgFlag, StringComparison.OrdinalIgnoreCase);
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
