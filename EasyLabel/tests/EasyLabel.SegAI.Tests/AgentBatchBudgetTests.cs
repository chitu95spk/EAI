namespace EasyLabel.SegAI.Tests;

public sealed class AgentBatchBudgetTests
{
    [Xunit.Fact]
    public void BudgetLimitClassifiesAsBatchStop()
    {
        Xunit.Assert.Equal(
            EasyAI.Desktop.Services.Agent.AgentBatchStep.Stop,
            EasyAI.Desktop.Services.Agent.AgentBatchPlanner.Classify(EasyAI.Desktop.Services.Agent.AgentOutcomeKind.BudgetLimit, string.Empty));
    }

    [Xunit.Fact]
    public async System.Threading.Tasks.Task CancelledResultAddsItsRecordedCost()
    {
        EasyAI.Desktop.Services.Agent.AgentBatchRunner runner = new EasyAI.Desktop.Services.Agent.AgentBatchRunner(
            (input, ct) => System.Threading.Tasks.Task.FromResult(new EasyAI.Desktop.Services.Agent.AgentRunResult(EasyAI.Desktop.Services.Agent.AgentOutcomeKind.Cancelled)
            {
                CostUsd = 0.125,
            }));
        EasyAI.Desktop.Services.Agent.AgentBatchReport report = await runner.RunAsync(
            new[] { "a.png" },
            new EasyAI.Desktop.Services.Agent.AgentBatchCaps(1, 1.0),
            rel => System.Threading.Tasks.Task.FromResult<EasyAI.Desktop.Services.Agent.AgentRunInput?>(Input(rel)),
            (rel, input, result) => 0,
            null,
            System.Threading.CancellationToken.None);

        Xunit.Assert.Equal(EasyAI.Desktop.Services.Agent.AgentBatchStop.Cancelled, report.Stop);
        Xunit.Assert.Equal(0.125, report.Progress.CostUsd, 6);
        Xunit.Assert.Equal(0, report.Progress.Completed);
    }

    [Xunit.Fact]
    public void BudgetLimitIsNotRetried()
    {
        Xunit.Assert.False(EasyAI.Desktop.Services.Agent.AgentBatchRunner.ShouldRetry(
            new EasyAI.Desktop.Services.Agent.AgentRunResult(EasyAI.Desktop.Services.Agent.AgentOutcomeKind.BudgetLimit)));
    }

    [Xunit.Fact]
    public async System.Threading.Tasks.Task SerialBudgetLimitKeepsTheInterruptedImageForResumeAndCountsItsCostOnce()
    {
        EasyAI.Desktop.Services.Agent.AgentBatchRunner runner = new EasyAI.Desktop.Services.Agent.AgentBatchRunner(
            (input, ct) => System.Threading.Tasks.Task.FromResult(new EasyAI.Desktop.Services.Agent.AgentRunResult(EasyAI.Desktop.Services.Agent.AgentOutcomeKind.BudgetLimit)
            {
                CostUsd = 0.07,
            }));
        EasyAI.Desktop.Services.Agent.AgentBatchReport report = await runner.RunAsync(
            new[] { "a.png", "b.png" },
            new EasyAI.Desktop.Services.Agent.AgentBatchCaps(10, 1.0, 5.0),
            rel => System.Threading.Tasks.Task.FromResult<EasyAI.Desktop.Services.Agent.AgentRunInput?>(Input(rel)),
            (rel, input, result) => 0,
            null,
            System.Threading.CancellationToken.None);

        Xunit.Assert.Equal(EasyAI.Desktop.Services.Agent.AgentBatchStop.CostCap, report.Stop);
        Xunit.Assert.Equal(0.07, report.Progress.CostUsd, 6);
        Xunit.Assert.Equal(0, report.Progress.Completed);
        Xunit.Assert.Equal(new[] { "a.png", "b.png" }, report.NotProcessed);
    }

    [Xunit.Fact]
    public async System.Threading.Tasks.Task ParallelBudgetLimitReplacesThePreSetImageCapWithTheCostCap()
    {
        System.Collections.Generic.IReadOnlyList<System.Func<EasyAI.Desktop.Services.Agent.AgentRunInput, System.Threading.CancellationToken, System.IProgress<EasyAI.Desktop.Services.Agent.AgentStreamEvent>?, System.Threading.Tasks.Task<EasyAI.Desktop.Services.Agent.AgentRunResult>>> workers =
            new System.Func<EasyAI.Desktop.Services.Agent.AgentRunInput, System.Threading.CancellationToken, System.IProgress<EasyAI.Desktop.Services.Agent.AgentStreamEvent>?, System.Threading.Tasks.Task<EasyAI.Desktop.Services.Agent.AgentRunResult>>[]
            {
                (input, ct, progress) => System.Threading.Tasks.Task.FromResult(new EasyAI.Desktop.Services.Agent.AgentRunResult(EasyAI.Desktop.Services.Agent.AgentOutcomeKind.BudgetLimit) { CostUsd = 0.04 }),
                (input, ct, progress) => System.Threading.Tasks.Task.FromResult(new EasyAI.Desktop.Services.Agent.AgentRunResult(EasyAI.Desktop.Services.Agent.AgentOutcomeKind.Success)),
            };
        EasyAI.Desktop.Services.Agent.AgentBatchRunner runner = new EasyAI.Desktop.Services.Agent.AgentBatchRunner(workers);
        EasyAI.Desktop.Services.Agent.AgentBatchReport report = await runner.RunAsync(
            new[] { "a.png", "b.png" },
            new EasyAI.Desktop.Services.Agent.AgentBatchCaps(1, 1.0, 5.0),
            rel => System.Threading.Tasks.Task.FromResult<EasyAI.Desktop.Services.Agent.AgentRunInput?>(Input(rel)),
            (rel, input, result) => 0,
            null,
            System.Threading.CancellationToken.None);

        Xunit.Assert.Equal(EasyAI.Desktop.Services.Agent.AgentBatchStop.CostCap, report.Stop);
        Xunit.Assert.Equal(0.04, report.Progress.CostUsd, 6);
        Xunit.Assert.Equal(new[] { "a.png", "b.png" }, report.NotProcessed);
    }

    [Xunit.Fact]
    public async System.Threading.Tasks.Task TimedOutResultCountsItsCostOnce()
    {
        int calls = 0;
        EasyAI.Desktop.Services.Agent.AgentBatchRunner runner = new EasyAI.Desktop.Services.Agent.AgentBatchRunner(
            (input, ct) =>
            {
                calls++;
                return System.Threading.Tasks.Task.FromResult(calls == 1
                    ? new EasyAI.Desktop.Services.Agent.AgentRunResult(EasyAI.Desktop.Services.Agent.AgentOutcomeKind.TimedOut) { CostUsd = 0.03 }
                    : new EasyAI.Desktop.Services.Agent.AgentRunResult(EasyAI.Desktop.Services.Agent.AgentOutcomeKind.Success));
            });
        EasyAI.Desktop.Services.Agent.AgentBatchReport report = await runner.RunAsync(
            new[] { "a.png", "b.png" },
            new EasyAI.Desktop.Services.Agent.AgentBatchCaps(10, 1.0),
            rel => System.Threading.Tasks.Task.FromResult<EasyAI.Desktop.Services.Agent.AgentRunInput?>(Input(rel)),
            (rel, input, result) => 0,
            null,
            System.Threading.CancellationToken.None);

        Xunit.Assert.Equal(2, report.Progress.Completed);
        Xunit.Assert.Equal(1, report.Progress.Failed);
        Xunit.Assert.Equal(0.03, report.Progress.CostUsd, 6);
    }

    private static EasyAI.Desktop.Services.Agent.AgentRunInput Input(string rel)
    {
        return new EasyAI.Desktop.Services.Agent.AgentRunInput(
            rel,
            rel,
            100,
            100,
            new[] { "NG" },
            System.Array.Empty<EasyAI.Desktop.Services.Agent.AgentShapeInfo>(),
            "prompt",
            null,
            0,
            false,
            null,
            "digest");
    }
}
