using System;
using System.Collections.Generic;
using System.Linq;
using EasyLabel.SegAI.Inspection;

namespace EasyAI.Desktop.ViewModels;

/// <summary>Row order of the Label tab image list ("Most doubtful first"). Pure: no WPF types, so it is unit tested.</summary>
public static class ImageListSortRules
{
    /// <summary>
    /// The rows in list order. Off = the given order, unchanged. On = descending local uncertainty; a row without a result comes last;
    /// rows with equal scores keep their list order (the sort is stable).
    /// </summary>
    public static List<T> Order<T>(IEnumerable<T> rows, bool mostDoubtfulFirst, Func<T, ImageUncertainty?> uncertaintyOf)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(uncertaintyOf);
        if (!mostDoubtfulFirst)
        {
            return new List<T>(rows);
        }

        return UncertaintyOrdering.MostDoubtfulFirst(rows, uncertaintyOf);
    }

    /// <summary>
    /// The rows in list order for the "Unlabelled first" toggle. Off = by name (natural order: "img (2)" before "img (10)"). On = the rows
    /// without a label first, then the labelled ones, each group by name. Stable; equal names keep their given order.
    /// </summary>
    public static List<T> OrderByLabelState<T>(IEnumerable<T> rows, bool unlabelledFirst, Func<T, bool> isUnlabelled, Func<T, string> nameOf)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(isUnlabelled);
        ArgumentNullException.ThrowIfNull(nameOf);
        IEnumerable<T> ordered = rows;
        if (unlabelledFirst)
        {
            return ordered.OrderBy(r => isUnlabelled(r) ? 0 : 1).ThenBy(nameOf, Comparer<string>.Create(NaturalCompare)).ToList();
        }

        return ordered.OrderBy(nameOf, Comparer<string>.Create(NaturalCompare)).ToList();
    }

    /// <summary>
    /// The rows in list order with the Train / Val checkboxes: rows without a label first (when asked), then Train rows (when asked), then Val rows
    /// (when asked), then the rest; every group by name (natural order).
    /// </summary>
    public static List<T> OrderRows<T>(
        IEnumerable<T> rows,
        bool unlabelledFirst,
        bool trainFirst,
        bool valFirst,
        Func<T, bool> isUnlabelled,
        Func<T, bool> isTrain,
        Func<T, bool> isVal,
        Func<T, string> nameOf)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(isUnlabelled);
        ArgumentNullException.ThrowIfNull(isTrain);
        ArgumentNullException.ThrowIfNull(isVal);
        ArgumentNullException.ThrowIfNull(nameOf);
        int ModeRank(T row)
        {
            if (trainFirst && isTrain(row)) return 0;
            if (valFirst && isVal(row)) return trainFirst ? 1 : 0;
            return 2;
        }

        return rows
            .OrderBy(r => unlabelledFirst && isUnlabelled(r) ? 0 : 1)
            .ThenBy(ModeRank)
            .ThenBy(nameOf, Comparer<string>.Create(NaturalCompare))
            .ToList();
    }

    /// <summary>Case-insensitive name comparison where digit runs compare by value ("a (2)" &lt; "a (10)").</summary>
    public static int NaturalCompare(string? a, string? b)
    {
        a ??= string.Empty;
        b ??= string.Empty;
        int i = 0, j = 0;
        while (i < a.Length && j < b.Length)
        {
            if (char.IsDigit(a[i]) && char.IsDigit(b[j]))
            {
                int si = i, sj = j;
                while (i < a.Length && char.IsDigit(a[i])) i++;
                while (j < b.Length && char.IsDigit(b[j])) j++;
                string da = a.Substring(si, i - si).TrimStart('0'), db = b.Substring(sj, j - sj).TrimStart('0');
                if (da.Length != db.Length) return da.Length < db.Length ? -1 : 1;
                int c = string.CompareOrdinal(da, db);
                if (c != 0) return c;
            }
            else
            {
                int c = char.ToUpperInvariant(a[i]).CompareTo(char.ToUpperInvariant(b[j]));
                if (c != 0) return c;
                i++;
                j++;
            }
        }

        return (a.Length - i).CompareTo(b.Length - j);
    }

    /// <summary>
    /// The uncertainty an image is sorted by: the larger score of the segmentation result and the OK/NG classifier. A missing value
    /// (unknown) does not count; both missing = null. A tie keeps the segmentation value.
    /// </summary>
    public static ImageUncertainty? Merge(ImageUncertainty? segmentation, ImageUncertainty? classifier)
    {
        if (segmentation == null)
        {
            return classifier;
        }

        if (classifier == null)
        {
            return segmentation;
        }

        return classifier.Value.Score > segmentation.Value.Score ? classifier : segmentation;
    }
}
