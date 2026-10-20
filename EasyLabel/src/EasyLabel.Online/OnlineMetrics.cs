using System;
using System.Collections.Generic;

namespace EasyLabel.Online
{
    public sealed class StageStats
    {
        public StageStats(string stage, int count, double meanMs, double p50Ms, double p95Ms, double p99Ms, double maxMs)
        {
            Stage = stage; Count = count; MeanMs = meanMs; P50Ms = p50Ms; P95Ms = p95Ms; P99Ms = p99Ms; MaxMs = maxMs;
        }
        public string Stage { get; private set; }
        public int Count { get; private set; }
        public double MeanMs { get; private set; }
        public double P50Ms { get; private set; }
        public double P95Ms { get; private set; }
        public double P99Ms { get; private set; }
        public double MaxMs { get; private set; }
    }

    /// <summary>Thread-safe per-stage timing and counters for the Online runner.</summary>
    public sealed class OnlineMetrics
    {
        public const string TotalStage = "total";
        public const string QueueStage = "queue";

        private readonly object gate = new object();
        private readonly Dictionary<string, List<double>> samples = new Dictionary<string, List<double>>();
        private long accepted, dropped, completed, failed;

        public long Accepted { get { lock (gate) return accepted; } }
        public long Dropped { get { lock (gate) return dropped; } }
        public long Completed { get { lock (gate) return completed; } }
        public long Failed { get { lock (gate) return failed; } }

        internal void CountAccepted() { lock (gate) accepted++; }
        internal void CountDropped() { lock (gate) dropped++; }
        internal void CountFailed() { lock (gate) failed++; }

        internal void Record(IDictionary<string, double> stageMs)
        {
            lock (gate)
            {
                completed++;
                foreach (var kv in stageMs)
                {
                    List<double> list;
                    if (!samples.TryGetValue(kv.Key, out list)) { list = new List<double>(); samples[kv.Key] = list; }
                    list.Add(kv.Value);
                }
            }
        }

        public IReadOnlyList<StageStats> Snapshot()
        {
            var result = new List<StageStats>();
            lock (gate)
            {
                foreach (var kv in samples)
                {
                    var sorted = kv.Value.ToArray();
                    Array.Sort(sorted);
                    double sum = 0;
                    foreach (var v in sorted) sum += v;
                    result.Add(new StageStats(kv.Key, sorted.Length, sum / sorted.Length,
                        Percentile(sorted, 0.50), Percentile(sorted, 0.95), Percentile(sorted, 0.99), sorted[sorted.Length - 1]));
                }
            }
            result.Sort((a, b) => string.CompareOrdinal(a.Stage, b.Stage));
            return result;
        }

        // Nearest-rank percentile on a sorted array.
        public static double Percentile(double[] sorted, double p)
        {
            if (sorted.Length == 0) return 0;
            int rank = (int)Math.Ceiling(p * sorted.Length);
            if (rank < 1) rank = 1;
            if (rank > sorted.Length) rank = sorted.Length;
            return sorted[rank - 1];
        }
    }
}
