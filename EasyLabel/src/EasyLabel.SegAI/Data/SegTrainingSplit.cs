// Cloned from BeeCore/Unit/SegmentAI.Dataset.cs@d1201fa3d (GetTrainingModeSetting/ResolveTrainingMode :72-81, IsTrainableLabelled/
// GetTrainingAndValidationSamples :82-97, HasTrainingLabels :119-123). Brush strokes and legacy polygons do not exist in EasyLabel:
// the annotation list stands in for them.
using OpenCvSharp;

namespace EasyLabel.SegAI.Data;

/// <summary>Which samples reach training and which side of the Train/Val split they fall on.</summary>
public static class SegTrainingSplit
{
    /// <summary>The raw per-image setting (Auto/Train/Val) as stored on the sample.</summary>
    public static SegTrainingMode GetTrainingModeSetting(SegSample? sample)
    {
        return sample == null ? SegTrainingMode.Auto : (SegTrainingMode)SegSample.NormalizeMode(sample.TrainValMode);
    }

    /// <summary>Effective mode: Ignored without labels, otherwise Val for a Val pin and Train for Auto/Train. Never Auto.</summary>
    public static SegTrainingMode ResolveTrainingMode(SegSample? sample)
    {
        byte mode = SegSample.ResolveMode(sample);
        return mode == SegSample.ModeVal ? SegTrainingMode.Val : mode == SegSample.ModeTrain ? SegTrainingMode.Train : SegTrainingMode.Ignored;
    }

    /// <summary>Training-level label test (stricter than <see cref="SegSample.HasLabels"/>: a stored mask must actually contain pixels).</summary>
    public static bool IsTrainableLabelled(SegSample? sample)
    {
        if (sample == null) return false;
        if (sample.Annotations != null && sample.Annotations.Count == 0) return false;
        bool hasMask = !string.IsNullOrEmpty(sample.MaskPath) && File.Exists(sample.MaskPath);
        bool hasAnnotations = sample.Annotations != null && sample.Annotations.Count > 0;
        return (hasMask || hasAnnotations) && HasTrainingLabels(sample);
    }

    /// <summary>Samples that reach frame preparation: trainable-labelled samples that resolve to Train or Val.</summary>
    public static List<SegSample> GetTrainingAndValidationSamples(IEnumerable<SegSample?> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        return samples.Where(s => s != null && IsTrainableLabelled(s) && SegSample.ResolveMode(s) != SegSample.ModeIgnored).Select(s => s!).ToList();
    }

    /// <summary>Share of the live images that <see cref="AutoAssign"/> puts in Val (at least one, never all).</summary>
    public const double AutoValFraction = 0.2;

    /// <summary>
    /// Deterministic Train/Val split for images that have no explicit Val choice. Pure: the same items and seed always give the same
    /// answer. About <see cref="AutoValFraction"/> of the non-ignored items go to Val (at least 1 when there are 2 or more, never all).
    /// Classes are stratified: an item only goes to Val while every class it holds keeps another Train image, and Val is filled with
    /// items that bring a class not yet in Val first. Items pinned Val stay Val; items pinned Train are chosen last. An Ignored item
    /// is returned as <see cref="SegSplitSide.Ignored"/>. Result[i] is the side of items[i].
    /// </summary>
    public static SegSplitSide[] AutoAssign(IReadOnlyList<SegSplitItem> items, int seed)
    {
        ArgumentNullException.ThrowIfNull(items);
        SegSplitSide[] sides = new SegSplitSide[items.Count];
        List<int> live = new List<int>();
        HashSet<int>[] classes = new HashSet<int>[items.Count];
        ulong[] keys = new ulong[items.Count];
        for (int i = 0; i < items.Count; i++)
        {
            SegSplitItem item = items[i];
            if (item.Mode == SegTrainingMode.Ignored)
            {
                sides[i] = SegSplitSide.Ignored;
                continue;
            }

            sides[i] = item.Mode == SegTrainingMode.Val ? SegSplitSide.Val : SegSplitSide.Train;
            classes[i] = new HashSet<int>(item.ClassIds ?? Array.Empty<int>());
            keys[i] = StableKey(item.Rel, seed);
            live.Add(i);
        }

        int n = live.Count;
        if (n < 2)
        {
            if (n == 1) sides[live[0]] = SegSplitSide.Train;
            return sides;
        }

        live.Sort((a, b) =>
        {
            int c = keys[a].CompareTo(keys[b]);
            if (c != 0) return c;
            c = string.CompareOrdinal(items[a].Rel, items[b].Rel);
            return c != 0 ? c : a.CompareTo(b);
        });

        int target = Math.Min(n - 1, Math.Max(1, (int)Math.Round(n * AutoValFraction, MidpointRounding.AwayFromZero)));
        int valCount = 0;
        foreach (int i in live) if (sides[i] == SegSplitSide.Val) valCount++;
        if (valCount >= n)
        {
            sides[live[n - 1]] = SegSplitSide.Train; // never Val == everything
            valCount--;
        }

        Dictionary<int, int> trainCount = new Dictionary<int, int>();
        HashSet<int> valCovered = new HashSet<int>();
        foreach (int i in live)
        {
            if (sides[i] == SegSplitSide.Val)
            {
                valCovered.UnionWith(classes[i]);
                continue;
            }

            foreach (int c in classes[i]) trainCount[c] = trainCount.TryGetValue(c, out int have) ? have + 1 : 1;
        }

        bool Eligible(int i)
        {
            foreach (int c in classes[i]) if (trainCount[c] < 2) return false;
            return true;
        }

        void MoveToVal(int i)
        {
            sides[i] = SegSplitSide.Val;
            valCount++;
            foreach (int c in classes[i]) trainCount[c]--;
            valCovered.UnionWith(classes[i]);
        }

        // A: bring classes that are not in Val yet (at most one round per class).
        while (valCount < target)
        {
            int best = -1, bestGain = 0;
            bool bestPinned = false;
            foreach (int i in live)
            {
                if (sides[i] != SegSplitSide.Train || !Eligible(i)) continue;
                int gain = 0;
                foreach (int c in classes[i]) if (!valCovered.Contains(c)) gain++;
                bool pinned = items[i].Mode == SegTrainingMode.Train;
                if (gain > bestGain || (gain == bestGain && gain > 0 && bestPinned && !pinned))
                {
                    best = i;
                    bestGain = gain;
                    bestPinned = pinned;
                }
            }

            if (best < 0) break;
            MoveToVal(best);
        }

        // B: fill up in hash order, items that were not pinned Train first.
        for (int pass = 0; pass < 2 && valCount < target; pass++)
        {
            foreach (int i in live)
            {
                if (valCount >= target) break;
                if (sides[i] != SegSplitSide.Train || !Eligible(i)) continue;
                if (pass == 0 && items[i].Mode == SegTrainingMode.Train) continue;
                MoveToVal(i);
            }
        }

        // C: Val must not be empty; take the item that costs the fewest sole-Train classes.
        if (valCount == 0)
        {
            int best = -1, bestLoss = int.MaxValue;
            foreach (int i in live)
            {
                int loss = 0;
                foreach (int c in classes[i]) if (trainCount[c] < 2) loss++;
                if (loss < bestLoss)
                {
                    best = i;
                    bestLoss = loss;
                }
            }

            MoveToVal(best);
        }

        return sides;
    }

    // FNV-1a over the normalized rel with the seed mixed in, then a finalizer. Not string.GetHashCode (randomized per process).
    private static ulong StableKey(string? rel, int seed)
    {
        unchecked
        {
            ulong hash = 14695981039346656037UL ^ (ulong)(uint)seed;
            foreach (char ch in (rel ?? string.Empty).Replace('\\', '/').ToLowerInvariant())
            {
                hash ^= ch;
                hash *= 1099511628211UL;
            }

            hash ^= hash >> 33;
            hash *= 0xff51afd7ed558ccdUL;
            hash ^= hash >> 33;
            return hash;
        }
    }

    private static bool HasTrainingLabels(SegSample sample)
    {
        if (string.IsNullOrEmpty(sample.MaskPath) || !File.Exists(sample.MaskPath)) return true;
        using Mat mask = Cv2.ImRead(sample.MaskPath, ImreadModes.Grayscale);
        return !mask.Empty() && Cv2.CountNonZero(mask) > 0;
    }
}
