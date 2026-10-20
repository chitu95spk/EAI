using EasyLabel.Core.Model;

namespace EasyAI.Desktop.ViewModels;

/// <summary>Why the OK/NG model cannot be trained yet. <see cref="None"/> means it can.</summary>
public enum ClassifyBlockReason
{
    None,
    NoSession,
    Busy,
    OneClassOnly,
    TooFewTagged,
    BackboneMissing,
}

/// <summary>The fix button offered with a block (only the backbone has one).</summary>
public enum ClassifyFixKind
{
    None,
    InstallModelPack,
}

/// <summary>The first failing rule and its fix; <see cref="Reason"/> None means training may start.</summary>
public sealed class ClassifyBlock
{
    public ClassifyBlock(ClassifyBlockReason reason, ClassifyFixKind fix)
    {
        Reason = reason;
        Fix = fix;
    }

    public ClassifyBlockReason Reason { get; }

    public ClassifyFixKind Fix { get; }

    public bool CanTrain => Reason == ClassifyBlockReason.None;
}

/// <summary>Counts of the OK/NG marks of the images of a project.</summary>
public sealed class ClassifyCounts
{
    public ClassifyCounts(int ok, int ng, int unmarked)
    {
        Ok = ok;
        Ng = ng;
        Unmarked = unmarked;
    }

    public int Ok { get; }

    public int Ng { get; }

    public int Unmarked { get; }
}

/// <summary>
/// OK/NG classification rules of the Label, Train, Simple mode and Inspect steps. Pure rules, no WPF, so the test project links
/// this file. Resource text is looked up by the view model with English fallback (<see cref="ResourceKeyOf"/>).
/// </summary>
public static class ClassifyRules
{
    /// <summary>Marks each class needs before training (leaders decision: Simple minimum per class = 2).</summary>
    public const int MinPerClass = 2;

    public const string KindSegment = "segment";

    public const string KindClassify = "classify";

    /// <summary>
    /// The first failing rule, in this order: no session, busy, only one kind marked, fewer than the minimum per class, backbone missing.
    /// Zero marks of both kinds are reported as too few, not as one kind only.
    /// </summary>
    public static ClassifyBlock Check(bool hasSession, bool busy, int okCount, int ngCount, bool backboneInstalled, int minPerClass = MinPerClass)
    {
        if (!hasSession)
        {
            return new ClassifyBlock(ClassifyBlockReason.NoSession, ClassifyFixKind.None);
        }

        if (busy)
        {
            return new ClassifyBlock(ClassifyBlockReason.Busy, ClassifyFixKind.None);
        }

        if ((okCount > 0) != (ngCount > 0))
        {
            return new ClassifyBlock(ClassifyBlockReason.OneClassOnly, ClassifyFixKind.None);
        }

        if (okCount < minPerClass || ngCount < minPerClass)
        {
            return new ClassifyBlock(ClassifyBlockReason.TooFewTagged, ClassifyFixKind.None);
        }

        if (!backboneInstalled)
        {
            return new ClassifyBlock(ClassifyBlockReason.BackboneMissing, ClassifyFixKind.InstallModelPack);
        }

        return new ClassifyBlock(ClassifyBlockReason.None, ClassifyFixKind.None);
    }

    /// <summary>Resource key of the block text; null for NoSession and Busy (the Train step already has their text).</summary>
    public static string? ResourceKeyOf(ClassifyBlockReason reason)
    {
        switch (reason)
        {
            case ClassifyBlockReason.OneClassOnly: return "ClassifyBlockOneClass";
            case ClassifyBlockReason.TooFewTagged: return "ClassifyBlockTooFew";
            case ClassifyBlockReason.BackboneMissing: return "ClassifyBlockBackboneMissing";
            default: return null;
        }
    }

    /// <summary>Resource key of a fix button caption; null for <see cref="ClassifyFixKind.None"/>.</summary>
    public static string? FixResourceKeyOf(ClassifyFixKind fix)
    {
        return fix == ClassifyFixKind.InstallModelPack ? "FixInstallModelPack" : null;
    }

    /// <summary>Counts the marks from the stored class of each image (<see cref="ImageClassFlags.Read"/> values).</summary>
    public static ClassifyCounts Count(IEnumerable<byte> classes)
    {
        int ok = 0;
        int ng = 0;
        int unmarked = 0;
        foreach (byte value in classes)
        {
            if (value == ImageClassFlags.Ok)
            {
                ok++;
            }
            else if (value == ImageClassFlags.Ng)
            {
                ng++;
            }
            else
            {
                unmarked++;
            }
        }

        return new ClassifyCounts(ok, ng, unmarked);
    }

    /// <summary>The one-click action of a Train block: go to the Label step, or open the missing backbone's folder.</summary>
    public enum TrainAction
    {
        None,
        GoToLabel,
        OpenModelFolder,
    }

    /// <summary>The fix action of a classify block. The backbone comes from the model pack, so its fix opens the model folder for now.</summary>
    public static TrainAction TrainActionOf(ClassifyBlockReason reason)
    {
        switch (reason)
        {
            case ClassifyBlockReason.TooFewTagged:
            case ClassifyBlockReason.OneClassOnly:
                return TrainAction.GoToLabel;
            case ClassifyBlockReason.BackboneMissing:
                return TrainAction.OpenModelFolder;
            default:
                return TrainAction.None;
        }
    }

    /// <summary>Done text key: Simple mode shows only that the model is ready; Advanced also shows the held-out accuracy.</summary>
    public static string DoneKey(bool simpleMode)
    {
        return simpleMode ? "ClassifyReady" : "ClassifyDone";
    }

    /// <summary>
    /// G/B advance: the index of the first image after <paramref name="current"/> whose class is unmarked, searching to the end of the list
    /// only; -1 when there is none. Images with a class (OK or NG) are skipped.
    /// </summary>
    public static int NextUntaggedIndex(IReadOnlyList<byte> classes, int current)
    {
        for (int i = Math.Max(0, current + 1); i < classes.Count; i++)
        {
            if (classes[i] == ImageClassFlags.Unmarked)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// Simple mode: the classify kind is used when some image is OK/NG-tagged and no image has shapes. Labelled means tagged for this kind.
    /// </summary>
    public static string KindFor(int taggedImages, int shapeImages)
    {
        return taggedImages > 0 && shapeImages == 0 ? KindClassify : KindSegment;
    }
}
