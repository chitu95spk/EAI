using System.Security.Cryptography;
using System.Text;

namespace EasyLabel.Core.Classify;

/// <summary>One image of an OK/NG training set: its project-relative path (unique, '/' separated) and whether it is NG.</summary>
public sealed record ClassifySample(string RelativePath, bool IsNg);

/// <summary>One train/validation split. Both lists are sorted by path (ordinal).</summary>
public sealed record ClassifySplit(IReadOnlyList<ClassifySample> Train, IReadOnlyList<ClassifySample> Validation);

/// <summary>
/// The held-out scheme of a training set: one stratified split (<see cref="LeaveOneOut"/> false), or one split per image
/// (leave-one-out, each validation part is a single image) below <see cref="ClassifyDataset.LeaveOneOutBelow"/> images.
/// </summary>
public sealed record ClassifyPlan(bool LeaveOneOut, IReadOnlyList<ClassifySplit> Splits);

/// <summary>
/// Deterministic train/validation splitting for the OK/NG classifier. The same set of paths always gives the same split, whatever
/// the input order: validation images are chosen by the SHA-256 of their path, not by a random generator.
/// </summary>
public static class ClassifyDataset
{
    /// <summary>Share of each class held out for validation (stratified).</summary>
    public const double ValidationFraction = 0.2;

    /// <summary>Fewer images than this use leave-one-out instead of one 20% split.</summary>
    public const int LeaveOneOutBelow = 20;

    /// <summary>Each class needs at least this many images (so that both train and validation hold one of it).</summary>
    public const int MinPerClass = 2;

    /// <summary>Chooses the held-out scheme: leave-one-out below <see cref="LeaveOneOutBelow"/> images, else one stratified split.</summary>
    /// <exception cref="ArgumentException">Duplicate paths, or a class with fewer than <see cref="MinPerClass"/> images.</exception>
    public static ClassifyPlan Plan(IEnumerable<ClassifySample> samples)
    {
        List<ClassifySample> all = Normalize(samples);
        if (all.Count < LeaveOneOutBelow) return new ClassifyPlan(true, LeaveOneOutSorted(all));
        return new ClassifyPlan(false, new[] { SplitSorted(all) });
    }

    /// <summary>
    /// Stratified split: per class, 20% (rounded, at least one, at most all but one) of the hash-ordered images is validation;
    /// the rest is training.
    /// </summary>
    /// <exception cref="ArgumentException">Duplicate paths, or a class with fewer than <see cref="MinPerClass"/> images.</exception>
    public static ClassifySplit Split(IEnumerable<ClassifySample> samples)
    {
        return SplitSorted(Normalize(samples));
    }

    /// <summary>One split per image: the image alone is validation, every other image is training.</summary>
    /// <exception cref="ArgumentException">Duplicate paths, or a class with fewer than <see cref="MinPerClass"/> images.</exception>
    public static IReadOnlyList<ClassifySplit> LeaveOneOut(IEnumerable<ClassifySample> samples)
    {
        return LeaveOneOutSorted(Normalize(samples));
    }

    private static ClassifySplit SplitSorted(List<ClassifySample> all)
    {
        var validation = new HashSet<string>(StringComparer.Ordinal);
        foreach (bool ng in new[] { false, true })
        {
            List<ClassifySample> cls = all.Where(s => s.IsNg == ng).OrderBy(s => HashOf(s.RelativePath), StringComparer.Ordinal).ToList();
            int take = Math.Clamp((int)Math.Round(cls.Count * ValidationFraction, MidpointRounding.AwayFromZero), 1, cls.Count - 1);
            for (int i = 0; i < take; i++) validation.Add(cls[i].RelativePath);
        }

        return Build(all, validation);
    }

    private static IReadOnlyList<ClassifySplit> LeaveOneOutSorted(List<ClassifySample> all)
    {
        var splits = new List<ClassifySplit>(all.Count);
        foreach (ClassifySample held in all)
        {
            splits.Add(Build(all, new HashSet<string>(new[] { held.RelativePath }, StringComparer.Ordinal)));
        }

        return splits;
    }

    private static ClassifySplit Build(List<ClassifySample> all, HashSet<string> validationPaths)
    {
        var train = new List<ClassifySample>();
        var validation = new List<ClassifySample>();
        foreach (ClassifySample s in all)
        {
            if (validationPaths.Contains(s.RelativePath)) validation.Add(s);
            else train.Add(s);
        }

        return new ClassifySplit(train, validation);
    }

    private static List<ClassifySample> Normalize(IEnumerable<ClassifySample> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        var list = new List<ClassifySample>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (ClassifySample s in samples)
        {
            if (string.IsNullOrWhiteSpace(s.RelativePath)) throw new ArgumentException("Every sample needs a relative path.", nameof(samples));
            if (!seen.Add(s.RelativePath)) throw new ArgumentException("Duplicate sample path: " + s.RelativePath, nameof(samples));
            list.Add(s);
        }

        int ok = list.Count(s => !s.IsNg);
        int ng = list.Count - ok;
        if (ok < MinPerClass || ng < MinPerClass)
            throw new ArgumentException($"Each class needs at least {MinPerClass} images (OK {ok}, NG {ng}).", nameof(samples));

        list.Sort((a, b) => string.CompareOrdinal(a.RelativePath, b.RelativePath));
        return list;
    }

    private static string HashOf(string relativePath)
    {
        return System.Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(relativePath)));
    }
}
