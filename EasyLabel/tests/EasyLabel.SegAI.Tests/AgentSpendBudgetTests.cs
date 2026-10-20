namespace EasyLabel.SegAI.Tests;

public sealed class AgentSpendBudgetTests
{
    [Fact]
    public void SingleWorkerGetsTheWholeRemainderUpToItsCeiling()
    {
        EasyAI.Desktop.Services.Agent.AgentSpendBudget budget = new EasyAI.Desktop.Services.Agent.AgentSpendBudget(1.0, double.PositiveInfinity, 1);

        Assert.Equal(EasyAI.Desktop.Services.Agent.AgentReserveResult.Granted, budget.TryReserve(0.9, out EasyAI.Desktop.Services.Agent.AgentBudgetReservation? first));
        Assert.Equal(0.9, first!.Usd, 6);
        Assert.Equal(0.1, budget.RemainingUsd, 6);
    }

    [Fact]
    public void FreeSlotsShareTheRemainderFairly()
    {
        EasyAI.Desktop.Services.Agent.AgentSpendBudget budget = new EasyAI.Desktop.Services.Agent.AgentSpendBudget(1.0, double.PositiveInfinity, 4);

        budget.TryReserve(5.0, out EasyAI.Desktop.Services.Agent.AgentBudgetReservation? first);
        budget.TryReserve(5.0, out EasyAI.Desktop.Services.Agent.AgentBudgetReservation? second);

        Assert.Equal(0.25, first!.Usd, 6);
        Assert.Equal(0.25, second!.Usd, 6);
        Assert.Equal(2, budget.ActiveReservations);
    }

    [Fact]
    public void CeilingLimitsTheGrant()
    {
        EasyAI.Desktop.Services.Agent.AgentSpendBudget budget = new EasyAI.Desktop.Services.Agent.AgentSpendBudget(1.0, double.PositiveInfinity, 1);

        budget.TryReserve(EasyAI.Desktop.Services.Agent.AgentSpendBudget.SingleCallCeilingUsd, out EasyAI.Desktop.Services.Agent.AgentBudgetReservation? reservation);

        Assert.Equal(0.15, reservation!.Usd, 6);
    }

    [Fact]
    public void ShareBelowTheMinimumIsRefused()
    {
        EasyAI.Desktop.Services.Agent.AgentSpendBudget budget = new EasyAI.Desktop.Services.Agent.AgentSpendBudget(0.01, double.PositiveInfinity, 1);

        Assert.Equal(EasyAI.Desktop.Services.Agent.AgentReserveResult.Exhausted, budget.TryReserve(0.15, out EasyAI.Desktop.Services.Agent.AgentBudgetReservation? reservation));
        Assert.Null(reservation);
    }

    [Fact]
    public void ZeroDayRemainingRefusesEverything()
    {
        EasyAI.Desktop.Services.Agent.AgentSpendBudget budget = new EasyAI.Desktop.Services.Agent.AgentSpendBudget(1.0, 0, 1);

        Assert.Equal(EasyAI.Desktop.Services.Agent.AgentReserveResult.Exhausted, budget.TryReserve(0.15, out _));
    }

    [Fact]
    public void SettleReturnsTheUnusedPartAndOnlyOnce()
    {
        EasyAI.Desktop.Services.Agent.AgentSpendBudget budget = new EasyAI.Desktop.Services.Agent.AgentSpendBudget(1.0, double.PositiveInfinity, 1);
        budget.TryReserve(0.5, out EasyAI.Desktop.Services.Agent.AgentBudgetReservation? reservation);

        budget.Settle(reservation!, 0.2);
        budget.Settle(reservation!, 0.5);

        Assert.Equal(0.2, budget.SettledUsd, 6);
        Assert.Equal(0.0, budget.ReservedUsd, 6);
        Assert.Equal(0.8, budget.RemainingUsd, 6);
        Assert.True(reservation!.IsSettled);
    }

    [Fact]
    public void NaNActualCountsTheWholeReservation()
    {
        EasyAI.Desktop.Services.Agent.AgentSpendBudget budget = new EasyAI.Desktop.Services.Agent.AgentSpendBudget(1.0, double.PositiveInfinity, 1);
        budget.TryReserve(0.5, out EasyAI.Desktop.Services.Agent.AgentBudgetReservation? reservation);

        budget.Settle(reservation!, double.NaN);

        Assert.Equal(0.5, budget.SettledUsd, 6);
    }

    [Fact]
    public void ClosedBudgetRefusesLaterReservations()
    {
        EasyAI.Desktop.Services.Agent.AgentSpendBudget budget = new EasyAI.Desktop.Services.Agent.AgentSpendBudget(1.0, double.PositiveInfinity, 1);

        budget.Close();

        Assert.True(budget.IsClosed);
        Assert.Equal(EasyAI.Desktop.Services.Agent.AgentReserveResult.Closed, budget.TryReserve(0.15, out _));
    }

    [Fact]
    public void NonFiniteOrZeroCeilingIsRejected()
    {
        EasyAI.Desktop.Services.Agent.AgentSpendBudget budget = new EasyAI.Desktop.Services.Agent.AgentSpendBudget(1.0, double.PositiveInfinity, 1);

        Assert.Throws<ArgumentOutOfRangeException>(() => budget.TryReserve(double.NaN, out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => budget.TryReserve(0, out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => budget.TryReserve(double.PositiveInfinity, out _));
    }

    [Fact]
    public void EightWorkersNeverGrantOrSpendMoreThanTheBatchCap()
    {
        const double cap = 1.0;
        EasyAI.Desktop.Services.Agent.AgentSpendBudget budget = new EasyAI.Desktop.Services.Agent.AgentSpendBudget(cap, double.PositiveInfinity, 8);
        // The test keeps its own ledger of what is held (granted and not yet settled) and what was spent. Both are updated under one test lock
        // together with each budget call, so the check below sees the same state the budget saw. A settled run hands back its unused part,
        // so the sum of all grants over time may exceed the cap; what must stay within the cap is held plus spent.
        object sync = new object();
        double held = 0;
        double spent = 0;
        int grants = 0;
        Task[] workers = new Task[8];
        for (int w = 0; w < workers.Length; w++)
        {
            int seed = w + 1;
            workers[w] = Task.Run(() =>
            {
                Random random = new Random(seed);
                for (int i = 0; i < 400; i++)
                {
                    EasyAI.Desktop.Services.Agent.AgentBudgetReservation? reservation = null;
                    lock (sync)
                    {
                        EasyAI.Desktop.Services.Agent.AgentReserveResult result = budget.TryReserve(
                            EasyAI.Desktop.Services.Agent.AgentSpendBudget.SingleCallCeilingUsd,
                            out reservation);
                        if (result == EasyAI.Desktop.Services.Agent.AgentReserveResult.Closed)
                        {
                            break;
                        }

                        if (result != EasyAI.Desktop.Services.Agent.AgentReserveResult.Granted || reservation == null)
                        {
                            continue;
                        }

                        held += reservation.Usd;
                        grants++;
                        Assert.True(held + spent <= cap + 1e-9, "held+spent exceeded the cap after a grant");
                    }

                    // Spends between nothing and the whole reservation; never more than it was granted.
                    double actual = reservation.Usd * random.NextDouble();
                    lock (sync)
                    {
                        budget.Settle(reservation, actual);
                        held -= reservation.Usd;
                        spent += actual;
                    }
                }
            });
        }

        Task.WaitAll(workers);

        Assert.True(grants > 0);
        Assert.True(spent <= cap + 1e-9);
        Assert.True(budget.SettledUsd <= cap + 1e-9);
        Assert.Equal(0, budget.ActiveReservations);
        Assert.Equal(0.0, budget.ReservedUsd, 9);
    }
}
