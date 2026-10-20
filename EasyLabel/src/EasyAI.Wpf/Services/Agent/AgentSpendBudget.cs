namespace EasyAI.Desktop.Services.Agent;

/// <summary>The answer to a reservation request.</summary>
public enum AgentReserveResult
{
    /// <summary>A reservation was granted (see the reservation's amount).</summary>
    Granted = 0,

    /// <summary>What is left is below the minimum reservation (or nothing is left).</summary>
    Exhausted,

    /// <summary>The budget was closed (for example after a run hit its limit).</summary>
    Closed,
}

/// <summary>An amount of the budget held by one run until it is settled.</summary>
public sealed class AgentBudgetReservation
{
    internal AgentBudgetReservation(string id, double usd, AgentSpendBudget owner)
    {
        Id = id;
        Usd = usd;
        Owner = owner;
    }

    public string Id { get; }

    /// <summary>The amount held (USD).</summary>
    public double Usd { get; }

    /// <summary>True once <see cref="AgentSpendBudget.Settle"/> has been called for it.</summary>
    public bool IsSettled { get; internal set; }

    internal AgentSpendBudget Owner { get; }
}

/// <summary>
/// The spend budget of one batch, shared by every worker and retry of that batch. It is created with the batch cap and the day cap that is
/// left, and it never grants more than the total that is still unreserved. A request is granted a fair share (remaining / free worker slots),
/// limited by the caller's ceiling; a share below <see cref="MinimumReservationUsd"/> is refused. Settling a run returns what it did not spend.
/// All state is behind one lock, so the sum of the reservations held plus the settled spend never exceeds the total (except by the overshoot
/// that a run reports when it settles above its reservation). Pure: no I/O.
/// </summary>
public sealed class AgentSpendBudget
{
    /// <summary>The smallest reservation worth a model call (USD).</summary>
    public const double MinimumReservationUsd = 0.02;

    /// <summary>The ceiling of one suggest-prompt or prompt-refine call (USD).</summary>
    public const double SingleCallCeilingUsd = 0.15;

    private readonly object _gate = new object();
    private readonly double _totalUsd;
    private readonly int _workerSlots;
    private double _reservedUsd;
    private double _settledUsd;
    private int _activeReservations;
    private bool _closed;

    /// <param name="batchCapUsd">The batch cap; 0 or less = no batch cap.</param>
    /// <param name="dayRemainingUsd">What is left of the day cap (from the ledger); positive infinity = no day cap; NaN counts as 0.</param>
    /// <param name="workerSlots">How many runs may hold a reservation at once (at least 1).</param>
    public AgentSpendBudget(double batchCapUsd, double dayRemainingUsd, int workerSlots)
    {
        double total = double.PositiveInfinity;
        if (!double.IsNaN(batchCapUsd) && batchCapUsd > 0)
        {
            total = batchCapUsd;
        }

        double day = double.IsNaN(dayRemainingUsd) ? 0 : Math.Max(0, dayRemainingUsd);
        _totalUsd = Math.Min(total, day);
        _workerSlots = Math.Max(1, workerSlots);
    }

    /// <summary>The most that may be spent through this budget (USD); positive infinity when neither cap is on.</summary>
    public double TotalUsd => _totalUsd;

    /// <summary>USD held by reservations that are not settled yet.</summary>
    public double ReservedUsd
    {
        get
        {
            lock (_gate)
            {
                return _reservedUsd;
            }
        }
    }

    /// <summary>USD spent by settled runs.</summary>
    public double SettledUsd
    {
        get
        {
            lock (_gate)
            {
                return _settledUsd;
            }
        }
    }

    /// <summary>What may still be reserved or spent (USD), never below 0.</summary>
    public double RemainingUsd
    {
        get
        {
            lock (_gate)
            {
                return RemainingLocked();
            }
        }
    }

    /// <summary>How many reservations are held and not settled.</summary>
    public int ActiveReservations
    {
        get
        {
            lock (_gate)
            {
                return _activeReservations;
            }
        }
    }

    /// <summary>True after <see cref="Close"/>: no further reservation is granted.</summary>
    public bool IsClosed
    {
        get
        {
            lock (_gate)
            {
                return _closed;
            }
        }
    }

    /// <summary>
    /// Asks for a reservation of at most <paramref name="maxUsd"/> (finite, above 0). The grant is the smaller of that ceiling and the fair share
    /// (remaining divided by the free worker slots). Returns <see cref="AgentReserveResult.Exhausted"/> when the grant would be below
    /// <see cref="MinimumReservationUsd"/>.
    /// </summary>
    public AgentReserveResult TryReserve(double maxUsd, out AgentBudgetReservation? reservation)
    {
        if (double.IsNaN(maxUsd) || double.IsInfinity(maxUsd) || maxUsd <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxUsd), "A reservation ceiling must be a finite amount above 0.");
        }

        reservation = null;
        lock (_gate)
        {
            if (_closed)
            {
                return AgentReserveResult.Closed;
            }

            int free = Math.Max(1, _workerSlots - _activeReservations);
            double share = RemainingLocked() / free;
            double grant = Math.Min(share, maxUsd);
            if (double.IsNaN(grant) || grant < MinimumReservationUsd)
            {
                return AgentReserveResult.Exhausted;
            }

            _reservedUsd += grant;
            _activeReservations++;
            reservation = new AgentBudgetReservation(Guid.NewGuid().ToString("N"), grant, this);
            return AgentReserveResult.Granted;
        }
    }

    /// <summary>
    /// Ends a reservation with the amount the run actually spent; the rest of the reservation returns to the budget. A NaN or infinite amount
    /// counts as the whole reservation (conservative); a negative amount counts as 0. A second call, or a reservation of another budget, does
    /// nothing. Settle with 0 to release a reservation that was never used.
    /// </summary>
    public void Settle(AgentBudgetReservation reservation, double actualUsd)
    {
        if (reservation == null)
        {
            throw new ArgumentNullException(nameof(reservation));
        }

        double actual = double.IsNaN(actualUsd) || double.IsInfinity(actualUsd) ? reservation.Usd : Math.Max(0, actualUsd);
        lock (_gate)
        {
            if (!ReferenceEquals(reservation.Owner, this) || reservation.IsSettled)
            {
                return;
            }

            reservation.IsSettled = true;
            _activeReservations = Math.Max(0, _activeReservations - 1);
            _reservedUsd = Math.Max(0, _reservedUsd - reservation.Usd);
            _settledUsd += actual;
        }
    }

    /// <summary>Refuses every later reservation (the runs already holding one still settle normally).</summary>
    public void Close()
    {
        lock (_gate)
        {
            _closed = true;
        }
    }

    // Caller holds the lock.
    private double RemainingLocked()
    {
        return Math.Max(0, _totalUsd - (_reservedUsd + _settledUsd));
    }
}
