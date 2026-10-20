namespace EasyLabel.Core.Classify;

/// <summary>
/// Agreement of OK/NG predictions with the marks. A prediction is NG when P(NG) is at least <see cref="NgThreshold"/> (balanced
/// threshold). Rates use the class counts as denominators and are 0 when that class is absent (the counts say so).
/// </summary>
public sealed record ClassifyMetrics(
    int Count,
    int NgCount,
    int OkCount,
    int Correct,
    int NgFound,
    int OkRejected,
    double Accuracy,
    double NgRecall,
    double OkFalseRejectRate)
{
    /// <summary>P(NG) from which an image is called NG (balanced starting value).</summary>
    public const double NgThreshold = 0.5;

    /// <summary>True when a P(NG) value is called NG.</summary>
    public static bool IsNg(double pNg) => pNg >= NgThreshold;

    /// <summary>Scores predictions against marks. The two lists are aligned by index.</summary>
    /// <exception cref="ArgumentException">Lists differ in length, are empty, or hold a NaN probability.</exception>
    public static ClassifyMetrics Compute(IReadOnlyList<bool> isNg, IReadOnlyList<double> pNg)
    {
        ArgumentNullException.ThrowIfNull(isNg);
        ArgumentNullException.ThrowIfNull(pNg);
        if (isNg.Count == 0 || isNg.Count != pNg.Count) throw new ArgumentException("Marks and predictions must be non-empty and the same length.");

        int ngCount = 0;
        int okCount = 0;
        int correct = 0;
        int ngFound = 0;
        int okRejected = 0;
        for (int i = 0; i < isNg.Count; i++)
        {
            double p = pNg[i];
            if (double.IsNaN(p)) throw new ArgumentException("Probabilities must not be NaN.", nameof(pNg));
            bool truth = isNg[i];
            bool predicted = IsNg(p);
            if (truth) ngCount++;
            else okCount++;
            if (truth == predicted) correct++;
            if (truth && predicted) ngFound++;
            if (!truth && predicted) okRejected++;
        }

        return new ClassifyMetrics(
            isNg.Count,
            ngCount,
            okCount,
            correct,
            ngFound,
            okRejected,
            (double)correct / isNg.Count,
            ngCount == 0 ? 0.0 : (double)ngFound / ngCount,
            okCount == 0 ? 0.0 : (double)okRejected / okCount);
    }
}
