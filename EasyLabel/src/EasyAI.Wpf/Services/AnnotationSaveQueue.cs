namespace EasyAI.Desktop.Services;

/// <summary>One pending write of an edited image. <see cref="Run"/> executes on the queue worker thread.</summary>
public sealed class AnnotationSaveJob
{
    public AnnotationSaveJob(string rel, Action run)
    {
        Rel = rel;
        Run = run;
    }

    public string Rel { get; }

    public Action Run { get; }
}

/// <summary>
/// Single-writer background queue. Jobs run strictly one at a time in enqueue order; a not-yet-started job for the same
/// image is replaced by the newer snapshot. Failures are reported through <see cref="Failed"/> (raised on the worker
/// thread) and never lost silently.
/// </summary>
public sealed class AnnotationSaveQueue
{
    private readonly object _gate = new object();
    private readonly List<AnnotationSaveJob> _pending = new List<AnnotationSaveJob>();
    private bool _running;
    private TaskCompletionSource<bool>? _idle;

    /// <summary>Raised on the worker thread with the image path and the exception of a failed job.</summary>
    public event Action<string, Exception>? Failed;

    /// <summary>Raised on the worker thread with the image path after a job wrote it successfully.</summary>
    public event Action<string>? Saved;

    public void Enqueue(AnnotationSaveJob job)
    {
        lock (_gate)
        {
            int existing = _pending.FindIndex(p => string.Equals(p.Rel, job.Rel, StringComparison.OrdinalIgnoreCase));
            if (existing >= 0)
            {
                _pending[existing] = job;
            }
            else
            {
                _pending.Add(job);
            }

            if (!_running)
            {
                _running = true;
                Task.Run(Drain);
            }
        }
    }

    /// <summary>Completes when every job enqueued so far has finished (successfully or not).</summary>
    public Task FlushAsync()
    {
        lock (_gate)
        {
            if (!_running && _pending.Count == 0)
            {
                return Task.CompletedTask;
            }

            _idle ??= new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            return _idle.Task;
        }
    }

    /// <summary>Blocking flush for window close. Returns false when the timeout elapsed first.</summary>
    public bool Flush(TimeSpan timeout)
    {
        return FlushAsync().Wait(timeout);
    }

    private void Drain()
    {
        while (true)
        {
            AnnotationSaveJob job;
            lock (_gate)
            {
                if (_pending.Count == 0)
                {
                    _running = false;
                    _idle?.TrySetResult(true);
                    _idle = null;
                    return;
                }

                job = _pending[0];
                _pending.RemoveAt(0);
            }

            try
            {
                job.Run();
                try
                {
                    Saved?.Invoke(job.Rel);
                }
                catch (Exception)
                {
                    // A failing listener must not be reported as a failed save nor kill the writer.
                }
            }
            catch (Exception ex)
            {
                try
                {
                    Failed?.Invoke(job.Rel, ex);
                }
                catch (Exception)
                {
                    // A failing error sink must not kill the writer.
                }
            }
        }
    }
}
