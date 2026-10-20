using System.Diagnostics;
using EasyLabel.SegAI.Data;
using EasyLabel.SegAI.Options;

namespace EasyLabel.SegAI.Training;

/// <summary>Why training cannot start now; the UI maps it to a disabled button and a tooltip.</summary>
public enum SegAiTrainBlock
{
    None,

    /// <summary>No Train image with regions.</summary>
    NoTrainImages,

    /// <summary>No image is explicitly set to Val (Val is never derived automatically).</summary>
    NoValImages,

    /// <summary>V2 training needs BeeNativeSegAI.dll and it is not loaded or found.</summary>
    NativeMissing,

    /// <summary>The native library is there but the V2 backbone model file is not.</summary>
    BackboneMissing,

    /// <summary>A training run is already active for this project.</summary>
    Busy,

    /// <summary>Position project: no oriented box or circle label among the Train and Val images (see <see cref="SegAiTrainingService.InstanceBlockOf"/>).</summary>
    NoInstanceLabels,
}

/// <summary>Progress of a run: overall percent (0..100, never decreases), the newest epoch metric when this report carries one, time since start.</summary>
public sealed record SegAiTrainProgress(int Percent, SegEpochMetric? Epoch, TimeSpan Elapsed);

/// <summary>
/// One training run at a time per project: validates the prerequisites, runs <see cref="SegAiSession.Train"/> on a background task and
/// reports progress. The options are passed through unchanged (the service never edits defaults). Reports reach the
/// <see cref="IProgress{T}"/> on the training thread; pass a <c>Progress&lt;T&gt;</c> created on the UI thread to marshal them.
/// </summary>
public sealed class SegAiTrainingService
{
    private readonly SegAiSession _session;

    public SegAiTrainingService(SegAiSession session)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
    }

    /// <summary>Raised (on the calling thread of Start, then on the training thread) when <see cref="IsRunning"/> changes.</summary>
    public event EventHandler? Changed;

    /// <summary>A run is active or about to start for the session (also true for a run started outside this service).</summary>
    public bool IsRunning => _session.IsTrainingActive;

    /// <summary>
    /// Position projects train from oriented boxes and circles only: with no such label among the Train and Val images the run is blocked
    /// (<see cref="SegAiTrainBlock.NoInstanceLabels"/>). Detect projects are never blocked by this rule.
    /// </summary>
    public static SegAiTrainBlock InstanceBlockOf(bool isPosition, int instanceLabels)
    {
        return isPosition && instanceLabels <= 0 ? SegAiTrainBlock.NoInstanceLabels : SegAiTrainBlock.None;
    }

    /// <summary>First missing prerequisite from the current sample states, else <see cref="SegAiTrainBlock.None"/>. V2 options also check the backbone.</summary>
    public static SegAiTrainBlock Check(SegAiSession session, SegTrainingOptions? options = null)
        => Check(session, options, null, false);

    /// <summary>Instance form of <see cref="Check(SegAiSession, SegTrainingOptions?)"/> for the session of this service.</summary>
    public SegAiTrainBlock Check(SegTrainingOptions? options = null) => Check(_session, options, null, false);

    internal static SegAiTrainBlock Check(SegAiSession session, SegTrainingOptions? options, string? backbonePathOverride, bool ignoreBusy)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!ignoreBusy && session.IsTrainingActive) return SegAiTrainBlock.Busy;

        int train = 0, val = 0, instances = 0;
        foreach (SegAiSampleInfo sample in session.Samples)
        {
            if (sample.AnnotationCount <= 0) continue; // Ignored
            if (sample.Mode == SegTrainingMode.Train) train++;
            else if (sample.Mode == SegTrainingMode.Val) val++;
            else continue;
            instances += sample.InstanceCount;
        }

        if (train == 0) return SegAiTrainBlock.NoTrainImages;
        if (val == 0) return SegAiTrainBlock.NoValImages;
        if (InstanceBlockOf(session.IsInstanceHead, instances) != SegAiTrainBlock.None) return SegAiTrainBlock.NoInstanceLabels;

        if ((options ?? session.Options).Version == 2)
        {
            string? backbone = backbonePathOverride;
            if (string.IsNullOrWhiteSpace(backbone))
            {
                try { backbone = session.TrainBackend.DefaultBackbonePath(); }
                catch (Exception) { backbone = null; }
                if (string.IsNullOrWhiteSpace(backbone)) return SegAiTrainBlock.NativeMissing;
            }

            if (!File.Exists(backbone)) return SegAiTrainBlock.BackboneMissing;
        }

        return SegAiTrainBlock.None;
    }

    /// <summary>
    /// Starts one run on a background task. A second call while a run is active returns a result with <see cref="SegAiTrainResult.Busy"/>
    /// (no exception); an unmet prerequisite returns a failed result naming the <see cref="SegAiTrainBlock"/>. Cancelling the token ends
    /// the run with <see cref="SegAiTrainResult.Cancelled"/> and no registered candidate.
    /// </summary>
    /// <param name="backbonePathOverride">Optional V2 backbone path (otherwise derived from the native folder).</param>
    public Task<SegAiTrainResult> StartAsync(SegTrainingOptions options, IProgress<SegAiTrainProgress>? progress, CancellationToken ct,
        string? backbonePathOverride = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!_session.TryReserveTraining()) return Task.FromResult(SegAiTrainResult.BusyResult());
        var clock = Stopwatch.StartNew();
        bool started = false;
        try
        {
            SegAiTrainBlock block = Check(_session, options, backbonePathOverride, true);
            if (block != SegAiTrainBlock.None)
                return Task.FromResult(SegAiTrainResult.Failed("Training cannot start: " + block + ".", TimeSpan.Zero));

            RaiseChanged();
            started = true;
            int percent = 0;
            var request = new SegAiTrainRequest { Options = options, BackbonePathOverride = backbonePathOverride };
            var percentSink = new DelegateProgress<int>(value =>
            {
                int next = Math.Max(Volatile.Read(ref percent), Math.Clamp(value, 0, 100));
                Volatile.Write(ref percent, next);
                Report(progress, new SegAiTrainProgress(next, null, clock.Elapsed));
            });
            var epochSink = new DelegateProgress<SegEpochMetric>(metric =>
                Report(progress, new SegAiTrainProgress(Volatile.Read(ref percent), metric, clock.Elapsed)));

            // The token is deliberately not given to Task.Run: a pre-cancelled token must still reach the pipeline, which returns the
            // Cancelled result, and the finally below must always run to free the slot.
            return Task.Run(() =>
            {
                try
                {
                    return _session.Train(request, percentSink, epochSink, ct);
                }
                finally
                {
                    _session.ReleaseTraining();
                    RaiseChanged();
                }
            });
        }
        finally
        {
            if (!started) _session.ReleaseTraining();
        }
    }

    private static void Report(IProgress<SegAiTrainProgress>? progress, SegAiTrainProgress value)
    {
        if (progress == null) return;
        try { progress.Report(value); }
        catch (Exception) { /* an observer must not turn a running training into a failure */ }
    }

    private void RaiseChanged()
    {
        try { Changed?.Invoke(this, EventArgs.Empty); }
        catch (Exception) { /* observer failure must not leave the slot reserved */ }
    }

    private sealed class DelegateProgress<T> : IProgress<T>
    {
        private readonly Action<T> _action;

        public DelegateProgress(Action<T> action)
        {
            _action = action;
        }

        public void Report(T value) => _action(value);
    }
}
