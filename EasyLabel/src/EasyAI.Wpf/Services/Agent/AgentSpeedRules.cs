namespace EasyAI.Desktop.Services.Agent;

/// <summary>Pure rules of the Speed slider of the agent chat: how many agent runs may go at the same time on this machine.</summary>
public static class AgentSpeedRules
{
    /// <summary>The detent stops of the Speed slider (agent runs at the same time).</summary>
    public static readonly IReadOnlyList<int> Stops = new[] { 1, 2, 4, 8, 16, 32 };

    /// <summary>Memory one agent run is assumed to need, in GB.</summary>
    public const double GbPerWorker = 1.5;

    /// <summary>The largest stop that is at most <paramref name="logicalCores"/> and at most <paramref name="ramGb"/> / 1.5; 1 at least.</summary>
    public static int MaxWorkers(int logicalCores, double ramGb)
    {
        double limit = Math.Min(logicalCores, double.IsNaN(ramGb) ? 0 : ramGb / GbPerWorker);
        return StopAtMost(limit);
    }

    /// <summary>The stops this machine offers: <see cref="Stops"/> up to and including <paramref name="maxWorkers"/> (always at least x1).</summary>
    public static IReadOnlyList<int> AvailableStops(int maxWorkers)
    {
        List<int> result = Stops.Where(s => s <= maxWorkers).ToList();
        if (result.Count == 0)
        {
            result.Add(1);
        }

        return result;
    }

    /// <summary>A stored worker count as it is used: not above the machine cap and snapped down to a stop (3 reads as x2, 100 on a x8 machine as x8).</summary>
    public static int ClampToCap(int stored, int maxWorkers)
    {
        return StopAtMost(Math.Min(stored, Math.Max(1, maxWorkers)));
    }

    /// <summary>Position of a (clamped) worker count among the available stops.</summary>
    public static int IndexOf(int workers, IReadOnlyList<int> available)
    {
        int index = 0;
        for (int i = 0; i < available.Count; i++)
        {
            if (available[i] <= workers)
            {
                index = i;
            }
        }

        return index;
    }

    /// <summary>The text of a stop, for example <c>x4</c>.</summary>
    public static string StopText(int workers)
    {
        return "x" + workers.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private static int StopAtMost(double limit)
    {
        int best = 1;
        foreach (int stop in Stops)
        {
            if (stop <= limit)
            {
                best = stop;
            }
        }

        return best;
    }

    private static int? _machineMax;

    /// <summary>The cap of this PC from its logical cores and its physical memory (read once).</summary>
    public static int MachineMaxWorkers()
    {
        if (_machineMax == null)
        {
            double ramGb = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024.0 * 1024.0 * 1024.0);
            _machineMax = MaxWorkers(Environment.ProcessorCount, ramGb);
        }

        return _machineMax.Value;
    }
}
