using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace EasyAI.Desktop.Services.Agent;

/// <summary>
/// Counts what one agent run has spent while it is still running, from the usage that the stream-json lines carry. Pure: no I/O, no
/// clock, so it is unit tested. A model turn is one API message: its usage is kept once per <c>message.id</c> (the last line of the
/// message wins), so the several content-block lines of one message are never counted twice.
/// Cost while running = max(priced tokens, turns x floor). The floor is the conservative minimum per turn and covers turns whose model
/// has no price or whose usage did not arrive. When the final <c>result</c> line carries <c>total_cost_usd</c> that figure is used and
/// the cost is no longer an estimate.
/// </summary>
public sealed class AgentUsageMeter
{
    /// <summary>Minimum USD counted for every model turn (used when the tokens cannot be priced). A guess until measured.</summary>
    public const double DefaultFloorUsdPerTurn = 0.05;

    /// <summary>Cache writes are billed above the input price; the meter bills them at this multiple of the input price.</summary>
    public const double CacheWriteMultiplier = 1.25;

    private readonly AgentPriceTable? _prices;
    private readonly double _floorUsdPerTurn;
    private readonly Dictionary<string, MessageUsage> _messages = new Dictionary<string, MessageUsage>(StringComparer.Ordinal);
    private int _anonymousTurns;
    private double _tokenCostUsd;
    private long _inputTokens;
    private long _cacheWriteTokens;
    private long _cachedInputTokens;
    private long _outputTokens;
    private double _resultCostUsd;
    private bool _hasResultCost;
    private string _model;

    /// <param name="prices">The price table; null = no prices, so only the turn floor counts.</param>
    /// <param name="model">The model the run was started with (used until a line names another one).</param>
    /// <param name="floorUsdPerTurn">Minimum USD per model turn; negative or NaN counts as 0.</param>
    public AgentUsageMeter(AgentPriceTable? prices, string? model = null, double floorUsdPerTurn = DefaultFloorUsdPerTurn)
    {
        _prices = prices;
        _floorUsdPerTurn = double.IsNaN(floorUsdPerTurn) || floorUsdPerTurn < 0 ? 0 : floorUsdPerTurn;
        _model = model ?? string.Empty;
    }

    /// <summary>The model name last seen (a line's model, else the one given to the constructor).</summary>
    public string Model => _model;

    /// <summary>Distinct model turns seen (one per message id; a call without an id counts as one turn each time).</summary>
    public int Turns => _messages.Count + _anonymousTurns;

    /// <summary>Input tokens of all turns (cache reads and writes excluded).</summary>
    public long InputTokens => _inputTokens;

    /// <summary>Cache write tokens of all turns.</summary>
    public long CacheWriteTokens => _cacheWriteTokens;

    /// <summary>Cache read tokens of all turns.</summary>
    public long CachedInputTokens => _cachedInputTokens;

    /// <summary>Output tokens of all turns.</summary>
    public long OutputTokens => _outputTokens;

    /// <summary>Priced tokens of all turns, without the floor.</summary>
    public double TokenCostUsd => _tokenCostUsd;

    /// <summary>True once the final result line gave <c>total_cost_usd</c>.</summary>
    public bool HasResultCost => _hasResultCost;

    /// <summary>The best cost known now: the result line's figure, else max(priced tokens, turns x floor).</summary>
    public double CostUsd => _hasResultCost ? _resultCostUsd : Math.Max(_tokenCostUsd, Turns * _floorUsdPerTurn);

    /// <summary>True until the result line arrives: the cost is then an estimate, and the caller must flag it.</summary>
    public bool IsEstimated => !_hasResultCost;

    /// <summary>
    /// Reads one stream-json line. Only lines with usage, a model name or a total cost are parsed; a broken line changes nothing.
    /// Returns true when the line changed the meter.
    /// </summary>
    public bool ObserveLine(string? line)
    {
        if (string.IsNullOrEmpty(line))
        {
            return false;
        }

        bool hasUsage = line!.IndexOf("\"usage\"", StringComparison.Ordinal) >= 0;
        bool hasCost = line.IndexOf("total_cost_usd", StringComparison.Ordinal) >= 0;
        bool hasInit = line.IndexOf("\"init\"", StringComparison.Ordinal) >= 0;
        if (!hasUsage && !hasCost && !hasInit)
        {
            return false;
        }

        JObject root;
        try
        {
            root = JObject.Parse(line.Trim());
        }
        catch (JsonException)
        {
            return false;
        }

        string? type = Str(root["type"]);
        if (type == "system")
        {
            string? model = Str(root["model"]);
            if (hasInit && !string.IsNullOrEmpty(model))
            {
                _model = model!;
            }

            return false;
        }

        if (type == "assistant")
        {
            JObject? message = root["message"] as JObject;
            JObject? usage = message?["usage"] as JObject;
            if (message == null || usage == null)
            {
                return false;
            }

            AddUsage(
                Str(message["id"]),
                Str(message["model"]),
                Long(usage["input_tokens"]),
                Long(usage["cache_creation_input_tokens"]),
                Long(usage["cache_read_input_tokens"]),
                Long(usage["output_tokens"]));
            return true;
        }

        if (type == "result" && root["total_cost_usd"] is JToken cost && (cost.Type == JTokenType.Float || cost.Type == JTokenType.Integer))
        {
            SetResultCost(cost.Value<double>());
            return true;
        }

        return false;
    }

    /// <summary>
    /// Adds the usage of one model turn. The same <paramref name="messageId"/> replaces the earlier usage of that turn (no double count).
    /// A null or empty id counts as a new turn each time. Negative token counts count as 0.
    /// </summary>
    public void AddUsage(string? messageId, string? model, long inputTokens, long cacheCreationTokens, long cacheReadTokens, long outputTokens)
    {
        if (!string.IsNullOrEmpty(model))
        {
            _model = model!;
        }

        long input = Math.Max(0, inputTokens);
        long cacheWrite = Math.Max(0, cacheCreationTokens);
        long cacheRead = Math.Max(0, cacheReadTokens);
        long output = Math.Max(0, outputTokens);
        MessageUsage usage = new MessageUsage(input, cacheWrite, cacheRead, output, CostOf(_model, input, cacheWrite, cacheRead, output));

        if (string.IsNullOrEmpty(messageId))
        {
            _anonymousTurns++;
            Apply(usage, 1);
            return;
        }

        // A repeated id replaces the turn's earlier usage; Turns counts the dictionary entries, so a repeat is not a second turn.
        if (_messages.TryGetValue(messageId!, out MessageUsage previous))
        {
            Apply(previous, -1);
        }

        _messages[messageId!] = usage;
        Apply(usage, 1);
    }

    /// <summary>Stores the final cost from the result line. Negative, NaN or infinite values are ignored.</summary>
    public void SetResultCost(double usd)
    {
        if (double.IsNaN(usd) || double.IsInfinity(usd) || usd < 0)
        {
            return;
        }

        _resultCostUsd = usd;
        _hasResultCost = true;
    }

    // Priced tokens of one turn; 0 when there is no price table or the model is not in it (the floor still counts the turn).
    private double CostOf(string? model, long input, long cacheWrite, long cacheRead, long output)
    {
        if (_prices == null || string.IsNullOrEmpty(model))
        {
            return 0;
        }

        long billedInput = input + (long)Math.Ceiling(cacheWrite * CacheWriteMultiplier);
        return _prices.CostOf(null, model, billedInput, cacheRead, output) ?? 0;
    }

    private void Apply(MessageUsage usage, int sign)
    {
        _inputTokens += sign * usage.Input;
        _cacheWriteTokens += sign * usage.CacheWrite;
        _cachedInputTokens += sign * usage.CacheRead;
        _outputTokens += sign * usage.Output;
        _tokenCostUsd += sign * usage.CostUsd;
    }

    private static string? Str(JToken? token)
    {
        return token != null && token.Type == JTokenType.String ? token.Value<string>() : null;
    }

    private static long Long(JToken? token)
    {
        if (token == null)
        {
            return 0;
        }

        if (token.Type == JTokenType.Integer)
        {
            return token.Value<long>();
        }

        if (token.Type == JTokenType.Float)
        {
            return (long)Math.Min(long.MaxValue / 2, Math.Max(0d, token.Value<double>()));
        }

        return 0;
    }

    /// <summary>One model turn's usage and its priced cost.</summary>
    private readonly struct MessageUsage
    {
        public MessageUsage(long input, long cacheWrite, long cacheRead, long output, double costUsd)
        {
            Input = input;
            CacheWrite = cacheWrite;
            CacheRead = cacheRead;
            Output = output;
            CostUsd = costUsd;
        }

        public long Input { get; }

        public long CacheWrite { get; }

        public long CacheRead { get; }

        public long Output { get; }

        public double CostUsd { get; }
    }
}
