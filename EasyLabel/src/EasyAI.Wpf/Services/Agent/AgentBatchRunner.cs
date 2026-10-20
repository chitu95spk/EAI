namespace EasyAI.Desktop.Services.Agent;

/// <summary>One image of a batch that failed (the batch went on) or whose answer could not be used.</summary>
public sealed class AgentBatchFailure
{
    public AgentBatchFailure(string rel, string message)
    {
        Rel = rel;
        Message = message;
    }

    public string Rel { get; }

    public string Message { get; }
}

/// <summary>How a batch ended.</summary>
public sealed class AgentBatchReport
{
    public AgentBatchReport(AgentBatchProgress progress, IReadOnlyList<AgentBatchFailure> failures, AgentOutcomeKind? fatalKind, string experienceBlock, IReadOnlyList<string>? notProcessed = null)
    {
        Progress = progress;
        Failures = failures;
        FatalKind = fatalKind;
        ExperienceBlock = experienceBlock;
        NotProcessed = notProcessed ?? Array.Empty<string>();
    }

    /// <summary>The planned images that were not handled because a cap (images, batch cost, daily cost) stopped the batch; empty for any other end.</summary>
    public IReadOnlyList<string> NotProcessed { get; }

    public AgentBatchProgress Progress { get; }

    public IReadOnlyList<AgentBatchFailure> Failures { get; }

    /// <summary>The outcome kind that stopped the batch with <see cref="AgentBatchStop.Error"/> (agent missing, not signed in, failed on a limit); null otherwise.</summary>
    public AgentOutcomeKind? FatalKind { get; }

    /// <summary>The experience block built from the first labelled image (empty when no image gave shapes).</summary>
    public string ExperienceBlock { get; }

    public AgentBatchStop Stop => Progress.Stop ?? AgentBatchStop.Done;
}

/// <summary>
/// Runs the agent over a list of images, one after the other (one run delegate) or with several runs at the same time (one delegate per worker,
/// each worker its own <c>AgentRunner</c>: a runner holds a single live process). The agent call, the input loading and the storing of a
/// result are delegates, so the loop is unit tested without a process and the view model keeps ownership of the suggestion store. The
/// delegates <c>loadInput</c>, <c>apply</c> and <c>report</c> are always called on the caller's context (the UI thread when started from it), also
/// with several workers: only the agent calls overlap. With several workers the experience block is not built (it comes from the previous image).
/// </summary>
public sealed class AgentBatchRunner
{
    /// <summary>Cost reserved for an image in flight before any image gave a real cost, so a cost cap is not overshot by the whole worker pool.</summary>
    private const double FirstReserveUsd = 0.10;

    private IReadOnlyList<Func<AgentRunInput, CancellationToken, IProgress<AgentStreamEvent>?, Task<AgentRunResult>>> _runs;

    /// <param name="run">One agent run (<c>AgentRunner.RunAsync</c>).</param>
    public AgentBatchRunner(Func<AgentRunInput, CancellationToken, Task<AgentRunResult>> run)
    {
        _runs = new[] { (Func<AgentRunInput, CancellationToken, IProgress<AgentStreamEvent>?, Task<AgentRunResult>>)((input, ct, progress) => run(input, ct)) };
    }

    /// <param name="run">One agent run that also reports its live lines (<c>AgentRunner.RunAsync</c> with the progress overload); the progress may be null.</param>
    public AgentBatchRunner(Func<AgentRunInput, CancellationToken, IProgress<AgentStreamEvent>?, Task<AgentRunResult>> run)
    {
        _runs = new[] { run };
    }

    /// <param name="workers">One agent run per worker (1..n); with more than one, images are labelled at the same time and each worker has its own runner.</param>
    public AgentBatchRunner(IReadOnlyList<Func<AgentRunInput, CancellationToken, IProgress<AgentStreamEvent>?, Task<AgentRunResult>>> workers)
    {
        if (workers == null || workers.Count == 0)
        {
            throw new ArgumentException("At least one worker is required.", nameof(workers));
        }

        _runs = workers;
    }

    /// <param name="workers">One agent run per worker.</param>
    /// <param name="retryWorkers">
    /// One stronger run per worker (same order). An image whose run failed for a reason that is not a limit or a sign-in problem (for example the
    /// turn limit of Economy) is run once more on the same worker with it, inside the same slot; the cost of both runs is added. Null or empty = no retry.
    /// </param>
    public AgentBatchRunner(
        IReadOnlyList<Func<AgentRunInput, CancellationToken, IProgress<AgentStreamEvent>?, Task<AgentRunResult>>> workers,
        IReadOnlyList<Func<AgentRunInput, CancellationToken, IProgress<AgentStreamEvent>?, Task<AgentRunResult>>>? retryWorkers)
        : this(workers)
    {
        if (retryWorkers == null || retryWorkers.Count == 0)
        {
            return;
        }

        if (retryWorkers.Count != workers.Count)
        {
            throw new ArgumentException("One retry run per worker is required.", nameof(retryWorkers));
        }

        _runs = workers.Select((run, i) => WithRetry(run, retryWorkers[i])).ToList();
    }

    /// <summary>True when a failed answer is worth one more run with a stronger setting: not a cancel, not a missing / signed-out agent, not a limit.</summary>
    public static bool ShouldRetry(AgentRunResult result)
    {
        if (result.Kind == AgentOutcomeKind.BudgetLimit)
        {
            return false;
        }

        if (result.Kind == AgentOutcomeKind.TimedOut)
        {
            return true;
        }

        if (result.Kind != AgentOutcomeKind.Failed)
        {
            return false;
        }

        string message = result.Message.Length > 0 ? result.Message : result.ResultText;
        return AgentBatchPlanner.Classify(result.Kind, message) == AgentBatchStep.Continue;
    }

    private static Func<AgentRunInput, CancellationToken, IProgress<AgentStreamEvent>?, Task<AgentRunResult>> WithRetry(
        Func<AgentRunInput, CancellationToken, IProgress<AgentStreamEvent>?, Task<AgentRunResult>> run,
        Func<AgentRunInput, CancellationToken, IProgress<AgentStreamEvent>?, Task<AgentRunResult>> retry)
    {
        return async (input, ct, progress) =>
        {
            AgentRunResult first = await run(input, ct, progress);
            if (ct.IsCancellationRequested || !ShouldRetry(first))
            {
                return first;
            }

            AgentRunResult second = await retry(input, ct, progress);
            return second with
            {
                CostUsd = first.CostUsd + second.CostUsd,
                IsCostEstimated = first.IsCostEstimated || second.IsCostEstimated,
            };
        };
    }

    private static AgentBatchStop BudgetLimitStop(AgentBatchCaps caps, AgentBatchProgress progress)
    {
        double costRemaining = caps.CostCapOn ? caps.MaxCostUsd!.Value - progress.CostUsd : double.PositiveInfinity;
        double dayRemaining = caps.DayCapOn ? caps.MaxUsdPerDay!.Value - caps.SpentTodayUsd - progress.CostUsd : double.PositiveInfinity;
        return dayRemaining < costRemaining ? AgentBatchStop.DayCap : AgentBatchStop.CostCap;
    }

    /// <param name="rels">The planned images in order.</param>
    /// <param name="caps">Image and cost caps.</param>
    /// <param name="loadInput">The run input of an image (digest already frozen in it, experience not yet); null = it could not be read.</param>
    /// <param name="apply">Stores a successful result for the image and returns how many shapes it proposed.</param>
    /// <param name="report">Called before each image and at the end; may be null.</param>
    /// <param name="ct">Cancels the batch.</param>
    /// <param name="liveProgress">Receives the live lines of every image's run (all images, in order); may be null.</param>
    public async Task<AgentBatchReport> RunAsync(
        IReadOnlyList<string> rels,
        AgentBatchCaps caps,
        Func<string, Task<AgentRunInput?>> loadInput,
        Func<string, AgentRunInput, AgentRunResult, int> apply,
        Action<AgentBatchProgress>? report,
        CancellationToken ct,
        IProgress<AgentStreamEvent>? liveProgress = null)
    {
        if (_runs.Count > 1)
        {
            return await RunParallelAsync(rels, caps, loadInput, apply, report, ct, liveProgress);
        }

        AgentBatchProgress progress = new AgentBatchProgress(caps, rels.Count);
        List<AgentBatchFailure> failures = new List<AgentBatchFailure>();
        AgentOutcomeKind? fatal = null;
        string experience = string.Empty;
        List<string> interrupted = new List<string>();
        int index = 0;
        while (true)
        {
            if (ct.IsCancellationRequested)
            {
                progress.Finish(AgentBatchStop.Cancelled);
                break;
            }

            AgentBatchStop? stop = progress.NextStop();
            if (stop.HasValue)
            {
                progress.Finish(stop.Value);
                break;
            }

            string rel = rels[index++];
            progress.CurrentRel = rel;
            report?.Invoke(progress);

            AgentRunInput? loaded;
            try
            {
                loaded = await loadInput(rel);
            }
            catch (OperationCanceledException)
            {
                progress.Finish(AgentBatchStop.Cancelled);
                break;
            }
            catch (Exception ex)
            {
                loaded = null;
                failures.Add(new AgentBatchFailure(rel, ex.Message));
                progress.RecordFailure(0);
                continue;
            }

            if (loaded == null)
            {
                failures.Add(new AgentBatchFailure(rel, "The image or its shapes could not be read."));
                progress.RecordFailure(0);
                continue;
            }

            AgentRunInput input = loaded.WithExperienceBlock(experience);
            AgentRunResult result;
            try
            {
                result = await _runs[0](input, ct, liveProgress);
            }
            catch (OperationCanceledException)
            {
                progress.Finish(AgentBatchStop.Cancelled);
                break;
            }
            catch (Exception ex)
            {
                failures.Add(new AgentBatchFailure(rel, ex.Message));
                progress.RecordFailure(0);
                continue;
            }

            string message = result.Message.Length > 0 ? result.Message : result.ResultText;
            if (result.Kind == AgentOutcomeKind.BudgetLimit)
            {
                AgentBatchStop budgetStop = BudgetLimitStop(caps, progress);
                progress.AddSpend(result.CostUsd);
                interrupted.Add(rel);
                progress.Finish(budgetStop);
                break;
            }

            AgentBatchStep step = AgentBatchPlanner.Classify(result.Kind, message);
            if (step == AgentBatchStep.Cancelled)
            {
                progress.AddSpend(result.CostUsd);
                progress.Finish(AgentBatchStop.Cancelled);
                break;
            }

            if (result.Kind == AgentOutcomeKind.Success)
            {
                int shapes = apply(rel, input, result);
                progress.RecordSuccess(result.CostUsd, result.FromCache, shapes);
                if (experience.Length == 0)
                {
                    // Built at once from the first image that gave shapes: no Accept wait, no Continue.
                    experience = AgentBatchExperience.Build(result.Proposals, input.ImageWidth, input.ImageHeight);
                }

                continue;
            }

            failures.Add(new AgentBatchFailure(rel, message.Length > 0 ? message : result.Kind.ToString()));
            progress.RecordFailure(result.CostUsd);
            if (step == AgentBatchStep.Stop)
            {
                fatal = result.Kind;
                progress.Finish(AgentBatchStop.Error);
                break;
            }
        }

        List<string> notProcessed = new List<string>();
        if (progress.Stop == AgentBatchStop.ImageCap || progress.Stop == AgentBatchStop.CostCap || progress.Stop == AgentBatchStop.DayCap)
        {
            foreach (string rel in interrupted)
            {
                notProcessed.Add(rel);
            }

            for (int i = index; i < rels.Count; i++)
            {
                notProcessed.Add(rels[i]);
            }
        }

        report?.Invoke(progress);
        return new AgentBatchReport(progress, failures, fatal, experience, notProcessed);
    }

    private async Task<AgentBatchReport> RunParallelAsync(
        IReadOnlyList<string> rels,
        AgentBatchCaps caps,
        Func<string, Task<AgentRunInput?>> loadInput,
        Func<string, AgentRunInput, AgentRunResult, int> apply,
        Action<AgentBatchProgress>? report,
        CancellationToken ct,
        IProgress<AgentStreamEvent>? liveProgress)
    {
        AgentBatchProgress progress = new AgentBatchProgress(caps, rels.Count);
        List<AgentBatchFailure> failures = new List<AgentBatchFailure>();
        AgentOutcomeKind? fatal = null;
        AgentBatchStop? stopReason = null;
        List<string> interrupted = new List<string>();
        int index = 0;
        Stack<int> free = new Stack<int>();
        for (int w = _runs.Count - 1; w >= 0; w--)
        {
            free.Push(w);
        }

        Dictionary<Task<AgentRunResult>, (string Rel, AgentRunInput Input, int Worker)> running = new Dictionary<Task<AgentRunResult>, (string, AgentRunInput, int)>();
        // A fatal answer (not signed in, limit) stops the workers still running; the user's token is linked so Cancel does too.
        using CancellationTokenSource stopAll = CancellationTokenSource.CreateLinkedTokenSource(ct);

        while (true)
        {
            while (stopReason == null && free.Count > 0 && index < rels.Count)
            {
                if (ct.IsCancellationRequested)
                {
                    stopReason = AgentBatchStop.Cancelled;
                    break;
                }

                if (index >= caps.MaxImages)
                {
                    stopReason = AgentBatchStop.ImageCap;
                    break;
                }

                AgentBatchStop? stop = progress.NextStop();
                if (stop.HasValue)
                {
                    stopReason = stop.Value;
                    break;
                }

                if (running.Count > 0 && caps.CostCapOn)
                {
                    double reserve = progress.Completed > 0 && progress.CostUsd > 0 ? progress.CostUsd / progress.Completed : FirstReserveUsd;
                    if (progress.CostUsd + running.Count * reserve >= caps.MaxCostUsd!.Value)
                    {
                        break;
                    }
                }

                string rel = rels[index++];
                progress.CurrentRel = rel;
                report?.Invoke(progress);

                AgentRunInput? loaded;
                try
                {
                    loaded = await loadInput(rel);
                }
                catch (OperationCanceledException)
                {
                    stopReason = AgentBatchStop.Cancelled;
                    break;
                }
                catch (Exception ex)
                {
                    failures.Add(new AgentBatchFailure(rel, ex.Message));
                    progress.RecordFailure(0);
                    continue;
                }

                if (loaded == null)
                {
                    failures.Add(new AgentBatchFailure(rel, "The image or its shapes could not be read."));
                    progress.RecordFailure(0);
                    continue;
                }

                AgentRunInput input = loaded.WithExperienceBlock(string.Empty);
                int worker = free.Pop();
                Func<AgentRunInput, CancellationToken, IProgress<AgentStreamEvent>?, Task<AgentRunResult>> run = _runs[worker];
                CancellationToken token = stopAll.Token;
                Task<AgentRunResult> task = Task.Run(() => run(input, token, liveProgress));
                running[task] = (rel, input, worker);
            }

            if (running.Count == 0)
            {
                break;
            }

            Task<AgentRunResult> finishedTask = await Task.WhenAny(running.Keys);
            (string doneRel, AgentRunInput doneInput, int doneWorker) = running[finishedTask];
            running.Remove(finishedTask);
            free.Push(doneWorker);

            AgentRunResult result;
            try
            {
                result = await finishedTask;
            }
            catch (OperationCanceledException)
            {
                if (ct.IsCancellationRequested)
                {
                    stopReason ??= AgentBatchStop.Cancelled;
                }

                continue;
            }
            catch (Exception ex)
            {
                failures.Add(new AgentBatchFailure(doneRel, ex.Message));
                progress.RecordFailure(0);
                report?.Invoke(progress);
                continue;
            }

            string message = result.Message.Length > 0 ? result.Message : result.ResultText;
            if (result.Kind == AgentOutcomeKind.BudgetLimit)
            {
                AgentBatchStop stop = BudgetLimitStop(caps, progress);
                progress.AddSpend(result.CostUsd);
                interrupted.Add(doneRel);
                if (!stopReason.HasValue || stopReason == AgentBatchStop.ImageCap || stopReason == AgentBatchStop.Done)
                {
                    stopReason = stop;
                }

                stopAll.Cancel();
                report?.Invoke(progress);
                continue;
            }

            AgentBatchStep step = AgentBatchPlanner.Classify(result.Kind, message);
            if (step == AgentBatchStep.Cancelled)
            {
                // The user's Cancel or the stop after a fatal answer: the result can still contain estimated cost.
                progress.AddSpend(result.CostUsd);
                if (stopReason == AgentBatchStop.CostCap || stopReason == AgentBatchStop.DayCap)
                {
                    interrupted.Add(doneRel);
                }
                else if (ct.IsCancellationRequested)
                {
                    stopReason ??= AgentBatchStop.Cancelled;
                }

                report?.Invoke(progress);
                continue;
            }

            if (result.Kind == AgentOutcomeKind.Success)
            {
                int shapes = apply(doneRel, doneInput, result);
                progress.RecordSuccess(result.CostUsd, result.FromCache, shapes);
            }
            else
            {
                failures.Add(new AgentBatchFailure(doneRel, message.Length > 0 ? message : result.Kind.ToString()));
                progress.RecordFailure(result.CostUsd);
                if (step == AgentBatchStep.Stop && fatal == null)
                {
                    fatal = result.Kind;
                    stopReason = AgentBatchStop.Error;
                    stopAll.Cancel();
                }
            }

            report?.Invoke(progress);
        }

        if (stopReason == null && ct.IsCancellationRequested)
        {
            stopReason = AgentBatchStop.Cancelled;
        }

        progress.Finish(stopReason ?? AgentBatchStop.Done);
        List<string> notProcessed = new List<string>();
        if (progress.Stop == AgentBatchStop.ImageCap || progress.Stop == AgentBatchStop.CostCap || progress.Stop == AgentBatchStop.DayCap)
        {
            foreach (string rel in interrupted)
            {
                notProcessed.Add(rel);
            }

            for (int i = index; i < rels.Count; i++)
            {
                notProcessed.Add(rels[i]);
            }
        }

        report?.Invoke(progress);
        return new AgentBatchReport(progress, failures, fatal, string.Empty, notProcessed);
    }
}
