namespace EasyAI.Desktop.Services.Agent;

/// <summary>
/// The spend of one agent run: the reservation it holds, the usage meter fed by every stdout line, the watchdog verdict, and the settlement
/// into the batch budget and the ledger. <see cref="SettleAsync"/> is idempotent, so every exit path may call it. Thread-safe for the one
/// reader thread that feeds <see cref="ObserveLine"/> and the run thread that settles.
/// </summary>
public sealed class AgentRunSpend
{
    private readonly object _gate = new object();
    private readonly AgentSpendBudget _budget;
    private readonly AgentSpendLedger? _ledger;
    private volatile bool _budgetHit;
    private bool _settled;

    public AgentRunSpend(AgentSpendBudget budget, AgentBudgetReservation reservation, AgentSpendLedger? ledger, AgentUsageMeter meter, string tool, string model)
    {
        _budget = budget ?? throw new ArgumentNullException(nameof(budget));
        Reservation = reservation ?? throw new ArgumentNullException(nameof(reservation));
        _ledger = ledger;
        Meter = meter ?? throw new ArgumentNullException(nameof(meter));
        Tool = tool ?? string.Empty;
        Model = model ?? string.Empty;
    }

    /// <summary>The amount this run holds in the batch budget (USD); the watchdog stops the run when its estimate reaches it.</summary>
    public AgentBudgetReservation Reservation { get; }

    public AgentUsageMeter Meter { get; }

    /// <summary>What the run is for: "label", "suggest", "refine" (the ledger line's tool).</summary>
    public string Tool { get; }

    public string Model { get; }

    /// <summary>True once the watchdog has stopped the run for its budget.</summary>
    public bool BudgetHit => _budgetHit;

    /// <summary>
    /// Feeds one raw stdout line. Returns true when the run has no final cost yet and its estimate (max of priced tokens and turns x floor)
    /// has reached the reservation: the caller kills the process tree.
    /// </summary>
    public bool ObserveLine(string line)
    {
        lock (_gate)
        {
            Meter.ObserveLine(line);
            return !Meter.HasResultCost && Meter.CostUsd >= Reservation.Usd;
        }
    }

    /// <summary>Records that the watchdog stopped the run (the caller then kills the tree).</summary>
    public void MarkBudgetHit()
    {
        _budgetHit = true;
    }

    /// <summary>
    /// The cost to record. A codex run reports no cost and is always charged its whole reservation (it cannot be bounded mid-run). Otherwise the
    /// cost the agent reported; without one, max(estimate, floor), flagged as an estimate.
    /// </summary>
    public (double Usd, bool Estimated) FinalCost(bool wholeReservation)
    {
        lock (_gate)
        {
            if (wholeReservation)
            {
                return (Reservation.Usd, true);
            }

            if (Meter.HasResultCost)
            {
                return (Meter.CostUsd, false);
            }

            return (Math.Max(Meter.CostUsd, AgentUsageMeter.DefaultFloorUsdPerTurn), true);
        }
    }

    /// <summary>
    /// Ends the reservation with the spend: the batch budget gets the unused part back, the ledger gets the settlement line. A second call does
    /// nothing. A ledger failure leaves the reservation open in the ledger, which the day cap counts as fully spent (the safe side).
    /// </summary>
    public async Task SettleAsync(double usd, bool estimated)
    {
        lock (_gate)
        {
            if (_settled)
            {
                return;
            }

            _settled = true;
        }

        _budget.Settle(Reservation, usd);
        AgentSpendLedger? ledger = _ledger;
        if (ledger == null)
        {
            return;
        }

        long input;
        long output;
        lock (_gate)
        {
            input = Meter.InputTokens;
            output = Meter.OutputTokens;
        }

        // The ledger reads and writes a file: never on the calling (possibly UI) thread.
        await Task.Run(() => ledger.Settle(Reservation.Id, Tool, Model, usd, estimated, input, output)).ConfigureAwait(false);
    }
}
