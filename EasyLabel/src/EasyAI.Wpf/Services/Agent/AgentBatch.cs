using System.Globalization;
using System.Text;
using EasyLabel.SegAI.Inspection;

namespace EasyAI.Desktop.Services.Agent;

/// <summary>Which images one agent batch covers.</summary>
public enum AgentBatchMode
{
    /// <summary>Only the open image (the default; one run like the Run button).</summary>
    Current = 0,

    /// <summary>Every image from the open one to the end of the list.</summary>
    All,

    /// <summary>The images that have no shapes, no pending suggestions and no earlier agent scan; a done open image is skipped and the plan starts after it.</summary>
    Remaining,
}

/// <summary>Why a batch ended.</summary>
public enum AgentBatchStop
{
    /// <summary>Every planned image was handled.</summary>
    Done = 0,

    /// <summary>The image cap was reached before the plan ended.</summary>
    ImageCap,

    /// <summary>The cumulative cost reached the cost cap.</summary>
    CostCap,

    /// <summary>The user cancelled.</summary>
    Cancelled,

    /// <summary>An error every following image would repeat (agent missing, not signed in, usage limit).</summary>
    Error,

    /// <summary>Today's agent cost (earlier runs plus this batch) reached the daily cap.</summary>
    DayCap,
}

/// <summary>What the batch does after one result.</summary>
public enum AgentBatchStep
{
    Continue = 0,
    Stop,
    Cancelled,
}

/// <summary>One image of the project list as the planner sees it.</summary>
public sealed class AgentBatchImage
{
    public AgentBatchImage(string rel, bool hasShapes, bool hasPendingSuggestions, bool isCurrent, bool scannedByAgent = false, UncertaintyBucket? localUncertainty = null)
    {
        Rel = rel;
        HasShapes = hasShapes;
        HasPendingSuggestions = hasPendingSuggestions;
        IsCurrent = isCurrent;
        ScannedByAgent = scannedByAgent;
        LocalUncertainty = localUncertainty;
    }

    /// <summary>How doubtful the latest local model result for this image is; null = no local result (unscanned, or an older cached one).</summary>
    public UncertaintyBucket? LocalUncertainty { get; }

    /// <summary>The agent already scanned this image successfully (this session or according to the chat history).</summary>
    public bool ScannedByAgent { get; }

    public string Rel { get; }

    public bool HasShapes { get; }

    public bool HasPendingSuggestions { get; }

    public bool IsCurrent { get; }
}

/// <summary>The two caps of a batch. A cost cap that is null or not above zero is off; the image cap is always on.</summary>
public sealed class AgentBatchCaps
{
    public const int DefaultMaxImages = 50;
    public const int MinMaxImages = 1;
    public const int MaxMaxImages = 1000;
    public const double DefaultMaxCostUsd = 1.0;
    public const double MaxMaxCostUsd = 1000.0;

    /// <param name="maxImages">Most images of the batch.</param>
    /// <param name="maxCostUsd">Cost cap of the batch; null or not above zero = off.</param>
    /// <param name="maxUsdPerDay">Daily cost cap; null or not above zero = off.</param>
    /// <param name="spentTodayUsd">What the agent already cost today, before this batch (read once when the batch starts).</param>
    public AgentBatchCaps(int maxImages, double? maxCostUsd, double? maxUsdPerDay = null, double spentTodayUsd = 0)
    {
        MaxImages = AgentBatchPlanner.ClampMaxImages(maxImages);
        MaxCostUsd = maxCostUsd.HasValue && !double.IsNaN(maxCostUsd.Value) && maxCostUsd.Value > 0 ? maxCostUsd : null;
        MaxUsdPerDay = maxUsdPerDay.HasValue && !double.IsNaN(maxUsdPerDay.Value) && maxUsdPerDay.Value > 0 ? maxUsdPerDay : null;
        SpentTodayUsd = double.IsNaN(spentTodayUsd) || double.IsInfinity(spentTodayUsd) || spentTodayUsd < 0 ? 0 : spentTodayUsd;
    }

    /// <summary>Null = no daily cost cap.</summary>
    public double? MaxUsdPerDay { get; }

    /// <summary>Cost of today's earlier runs (not this batch).</summary>
    public double SpentTodayUsd { get; }

    public bool DayCapOn => MaxUsdPerDay.HasValue;

    public static AgentBatchCaps Default => new AgentBatchCaps(DefaultMaxImages, DefaultMaxCostUsd);

    public int MaxImages { get; }

    /// <summary>Null = no cost cap.</summary>
    public double? MaxCostUsd { get; }

    public bool CostCapOn => MaxCostUsd.HasValue;
}

/// <summary>Counts, cost and stop reason of a running or finished batch. UI-free; the runner updates it, the view model shows it.</summary>
public sealed class AgentBatchProgress
{
    private readonly AgentBatchCaps _caps;

    public AgentBatchProgress(AgentBatchCaps caps, int planned)
    {
        _caps = caps;
        Planned = Math.Max(0, planned);
    }

    /// <summary>Images in the plan (before the image cap).</summary>
    public int Planned { get; }

    /// <summary>Images this batch will handle at most: the plan cut at the image cap.</summary>
    public int Total => Math.Min(Planned, _caps.MaxImages);

    /// <summary>Images finished, succeeded or failed.</summary>
    public int Completed { get; private set; }

    public int Succeeded { get; private set; }

    public int Failed { get; private set; }

    /// <summary>Succeeded images whose answer came from the result cache.</summary>
    public int Cached { get; private set; }

    public int ShapesProposed { get; private set; }

    /// <summary>Cumulative cost; a cached result costs 0.</summary>
    public double CostUsd { get; private set; }

    /// <summary>The image being processed now (null between images and after the end).</summary>
    public string? CurrentRel { get; set; }

    /// <summary>Set when the batch ended.</summary>
    public AgentBatchStop? Stop { get; private set; }

    /// <summary>
    /// The decision before the next image: null = go on, otherwise the reason to stop. Done wins over a cap that is reached at the same
    /// moment; an agent that reports no cost (always 0) never trips the cost cap.
    /// </summary>
    public AgentBatchStop? NextStop()
    {
        if (Completed >= Planned)
        {
            return AgentBatchStop.Done;
        }

        if (Completed >= _caps.MaxImages)
        {
            return AgentBatchStop.ImageCap;
        }

        if (_caps.DayCapOn && _caps.SpentTodayUsd + CostUsd >= _caps.MaxUsdPerDay!.Value)
        {
            return AgentBatchStop.DayCap;
        }

        if (_caps.CostCapOn && CostUsd >= _caps.MaxCostUsd!.Value)
        {
            return AgentBatchStop.CostCap;
        }

        return null;
    }

    public void RecordSuccess(double costUsd, bool fromCache, int shapes)
    {
        Completed++;
        Succeeded++;
        if (fromCache)
        {
            Cached++;
        }
        else
        {
            AddCost(costUsd);
        }

        ShapesProposed += Math.Max(0, shapes);
    }

    public void RecordFailure(double costUsd)
    {
        Completed++;
        Failed++;
        AddSpend(costUsd);
    }

    /// <summary>Adds a recorded cost for a cancelled run, which does not count as a failed image.</summary>
    public void AddSpend(double costUsd)
    {
        AddCost(costUsd);
    }

    public void Finish(AgentBatchStop stop)
    {
        Stop = stop;
        CurrentRel = null;
    }

    private void AddCost(double costUsd)
    {
        if (!double.IsNaN(costUsd) && !double.IsInfinity(costUsd) && costUsd > 0)
        {
            CostUsd += costUsd;
        }
    }
}

/// <summary>Modes, caps clamping and the stop rules of an agent batch. No I/O, no UI.</summary>
public static class AgentBatchPlanner
{
    public static string ModeKey(AgentBatchMode mode)
    {
        switch (mode)
        {
            case AgentBatchMode.All: return "all";
            case AgentBatchMode.Remaining: return "remaining";
            default: return "current";
        }
    }

    /// <summary>"all", "remaining" or "current" (any case); anything else is <paramref name="fallback"/>.</summary>
    public static AgentBatchMode ParseMode(string? text, AgentBatchMode fallback)
    {
        string value = (text ?? string.Empty).Trim();
        if (string.Equals(value, "all", StringComparison.OrdinalIgnoreCase))
        {
            return AgentBatchMode.All;
        }

        if (string.Equals(value, "remaining", StringComparison.OrdinalIgnoreCase))
        {
            return AgentBatchMode.Remaining;
        }

        if (string.Equals(value, "current", StringComparison.OrdinalIgnoreCase))
        {
            return AgentBatchMode.Current;
        }

        return fallback;
    }

    public static int ClampMaxImages(int value)
    {
        return Math.Min(AgentBatchCaps.MaxMaxImages, Math.Max(AgentBatchCaps.MinMaxImages, value));
    }

    /// <summary>0 = off (a value not above zero); NaN is the default; the upper bound is 1000.</summary>
    public static double ClampMaxCostUsd(double value)
    {
        if (double.IsNaN(value))
        {
            return AgentBatchCaps.DefaultMaxCostUsd;
        }

        return Math.Min(AgentBatchCaps.MaxMaxCostUsd, Math.Max(0.0, value));
    }

    /// <summary>
    /// The rel paths the batch visits, in order, without duplicates. The caps are applied while running, not here.
    /// <paramref name="routeLocalFirst"/> (Remaining mode only): an image whose latest local result is Sure is left to the local suggestions
    /// and not sent to the agent; an image that is Doubtful or Unsure is sent even when it has pending local suggestions (they are what is
    /// in doubt); an image without a local result follows the old rule. Off = the plan of earlier versions, unchanged.
    /// </summary>
    public static IReadOnlyList<string> Plan(IReadOnlyList<AgentBatchImage> images, AgentBatchMode mode, bool routeLocalFirst = false)
    {
        List<string> rels = new List<string>();
        HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int currentIndex = -1;
        for (int i = 0; i < images.Count; i++)
        {
            if (images[i].IsCurrent)
            {
                currentIndex = i;
                break;
            }
        }

        switch (mode)
        {
            case AgentBatchMode.Current:
                if (currentIndex >= 0)
                {
                    Add(rels, seen, images[currentIndex].Rel);
                }

                break;
            case AgentBatchMode.All:
                for (int i = currentIndex >= 0 ? currentIndex : 0; i < images.Count; i++)
                {
                    Add(rels, seen, images[i].Rel);
                }

                break;
            default:
                bool currentRemaining = currentIndex >= 0 && IsRemaining(images[currentIndex], routeLocalFirst);
                if (currentRemaining)
                {
                    Add(rels, seen, images[currentIndex].Rel);
                }

                // An open image that is already done (labelled, pending or scanned by the agent) is not visited again: the batch starts at the next one.
                int start = currentIndex >= 0 && !currentRemaining ? currentIndex + 1 : 0;
                for (int k = 0; k < images.Count; k++)
                {
                    int i = (start + k) % images.Count;
                    if (i != currentIndex && IsRemaining(images[i], routeLocalFirst))
                    {
                        Add(rels, seen, images[i].Rel);
                    }
                }

                break;
        }

        return rels;
    }

    private static bool IsRemaining(AgentBatchImage image, bool routeLocalFirst)
    {
        if (routeLocalFirst && image.LocalUncertainty.HasValue)
        {
            // A local result exists: Sure stays local; Doubtful / Unsure go to the agent even with pending local suggestions.
            return image.LocalUncertainty.Value != UncertaintyBucket.Sure && !image.HasShapes && !image.ScannedByAgent;
        }

        return !image.HasShapes && !image.HasPendingSuggestions && !image.ScannedByAgent;
    }

    private static void Add(List<string> rels, HashSet<string> seen, string rel)
    {
        if (!string.IsNullOrWhiteSpace(rel) && seen.Add(rel))
        {
            rels.Add(rel);
        }
    }

    /// <summary>
    /// What to do after a result: a cancel ends the batch as Cancelled; a missing agent, a sign-in problem or a usage / rate limit would
    /// repeat on every following image, so they stop it (Error); any other failure (time-out, one bad answer) is reported and the batch goes on.
    /// </summary>
    public static AgentBatchStep Classify(AgentOutcomeKind kind, string? message)
    {
        switch (kind)
        {
            case AgentOutcomeKind.Success:
                return AgentBatchStep.Continue;
            case AgentOutcomeKind.Cancelled:
                return AgentBatchStep.Cancelled;
            case AgentOutcomeKind.BudgetLimit:
                return AgentBatchStep.Stop;
            case AgentOutcomeKind.NotInstalled:
            case AgentOutcomeKind.NotSignedIn:
                return AgentBatchStep.Stop;
            case AgentOutcomeKind.Failed:
                return LooksLikeLimit(message) ? AgentBatchStep.Stop : AgentBatchStep.Continue;
            default:
                return AgentBatchStep.Continue;
        }
    }

    private static readonly string[] LimitPhrases =
    {
        "usage limit",
        "session limit",
        "hit your limit",
        "rate limit",
        "limit reached",
        "limit exceeded",
        "quota",
        "credit balance",
        "too many requests",
        "429",
    };

    /// <summary>True when the failure text names a usage / rate limit or an exhausted quota.</summary>
    public static bool LooksLikeLimit(string? message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return false;
        }

        foreach (string phrase in LimitPhrases)
        {
            if (message.Contains(phrase, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>
/// The experience block: a short text (at most 1500 characters, no image) describing the shapes the agent proposed for the first labelled
/// image of a batch. It goes into the system prompt of the following images so they follow the same style.
/// </summary>
public static class AgentBatchExperience
{
    public const int MaxChars = 1500;
    public const int MaxShapes = 8;
    public const int MaxLabelChars = 40;

    public const string Header = "Experience from the first labelled image of this batch (same project and task; follow its style, it is not the answer for this image):";

    /// <summary>The block for <paramref name="proposals"/> (new shapes only), or an empty string when there is no usable shape or the image size is unknown.</summary>
    public static string Build(IReadOnlyList<AgentProposal>? proposals, int imageWidth, int imageHeight)
    {
        if (proposals == null || proposals.Count == 0 || imageWidth <= 0 || imageHeight <= 0)
        {
            return string.Empty;
        }

        StringBuilder text = new StringBuilder(Header);
        int shapes = 0;
        foreach (AgentProposal proposal in proposals)
        {
            if (shapes >= MaxShapes)
            {
                break;
            }

            string? line = Line(proposal, imageWidth, imageHeight);
            if (line == null)
            {
                continue;
            }

            if (text.Length + 1 + line.Length > MaxChars)
            {
                break;
            }

            text.Append('\n').Append(line);
            shapes++;
        }

        return shapes == 0 ? string.Empty : text.ToString();
    }

    private static string? Line(AgentProposal proposal, int imageWidth, int imageHeight)
    {
        if (proposal.Action != null || proposal.Points == null || proposal.Points.Count == 0)
        {
            return null;
        }

        double minX = double.MaxValue;
        double minY = double.MaxValue;
        double maxX = double.MinValue;
        double maxY = double.MinValue;
        foreach (double[] p in proposal.Points)
        {
            if (p == null || p.Length < 2 || double.IsNaN(p[0]) || double.IsNaN(p[1]) || double.IsInfinity(p[0]) || double.IsInfinity(p[1]))
            {
                return null;
            }

            minX = Math.Min(minX, p[0]);
            maxX = Math.Max(maxX, p[0]);
            minY = Math.Min(minY, p[1]);
            maxY = Math.Max(maxY, p[1]);
        }

        double widthPercent = 100.0 * (maxX - minX) / imageWidth;
        double heightPercent = 100.0 * (maxY - minY) / imageHeight;
        StringBuilder line = new StringBuilder("- ");
        line.Append(Clean(proposal.Label)).Append(", ").Append(Clean(proposal.ShapeType)).Append(", box ")
            .Append(widthPercent.ToString("0.#", CultureInfo.InvariantCulture)).Append("% x ")
            .Append(heightPercent.ToString("0.#", CultureInfo.InvariantCulture)).Append("% of the image");

        // No absolute coordinates: they belong to the first image only and made the next ones copy its positions.
        return line.ToString();
    }

    private static string Clean(string? value)
    {
        StringBuilder clean = new StringBuilder();
        foreach (char c in value ?? string.Empty)
        {
            clean.Append(char.IsControl(c) ? ' ' : c);
        }

        string text = clean.ToString().Trim();
        return text.Length <= MaxLabelChars ? text : text.Substring(0, MaxLabelChars);
    }
}
