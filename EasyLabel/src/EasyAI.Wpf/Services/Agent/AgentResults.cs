using System.Drawing;
using System.Globalization;
using System.Text.RegularExpressions;
using EasyAI.Desktop.ViewModels;
using EasyLabel.Ai.Onnx;
using EasyLabel.SegAI.AutoLabel;
using Newtonsoft.Json.Linq;

namespace EasyAI.Desktop.Services.Agent;

/// <summary>How an agent run ended.</summary>
public enum AgentOutcomeKind
{
    /// <summary>The agent finished; the proposals (possibly none) are in the result.</summary>
    Success = 0,

    /// <summary>Claude Code was not found or could not be started.</summary>
    NotInstalled,

    /// <summary>Claude Code reported that nobody is signed in.</summary>
    NotSignedIn,

    /// <summary>Any other failure; <see cref="AgentRunResult.Message"/> holds the raw text.</summary>
    Failed,

    /// <summary>The caller cancelled; the agent process tree was killed.</summary>
    Cancelled,

    /// <summary>The agent did not finish in time; the process tree was killed.</summary>
    TimedOut,

    /// <summary>
    /// The run was stopped (or refused before it started) because its spend budget was used up: the batch cap, the day cap, or the ledger could not
    /// record the reservation. Appended last; every switch on the kind must handle it. Its cost is an estimate.
    /// </summary>
    BudgetLimit,
}

/// <summary>Token counts the Claude Code result reports.</summary>
public sealed class AgentUsage
{
    public AgentUsage(long inputTokens, long cacheCreationTokens, long cacheReadTokens, long outputTokens)
    {
        InputTokens = inputTokens;
        CacheCreationTokens = cacheCreationTokens;
        CacheReadTokens = cacheReadTokens;
        OutputTokens = outputTokens;
    }

    public static AgentUsage Empty { get; } = new AgentUsage(0, 0, 0, 0);

    public long InputTokens { get; }

    public long CacheCreationTokens { get; }

    public long CacheReadTokens { get; }

    public long OutputTokens { get; }
}

/// <summary>One shape the agent proposed (<c>proposals.json</c>); points are image pixels.</summary>
public sealed class AgentProposal
{
    public AgentProposal(string label, string shapeType, IReadOnlyList<double[]> points, IReadOnlyList<IReadOnlyList<double[]>> holes, double? score, string note, string? action = null, int? targetIndex = null, string? targetHash = null)
    {
        Label = label;
        ShapeType = shapeType;
        Points = points;
        Holes = holes;
        Score = score;
        Note = note;
        Action = action;
        TargetIndex = targetIndex;
        TargetHash = targetHash;
    }

    /// <summary>"replace" or "remove" for a review of an existing shape (then <see cref="TargetIndex"/> is set); null = a new shape.</summary>
    public string? Action { get; }

    /// <summary>Index of the reviewed existing shape (the agent's list_shapes id).</summary>
    public int? TargetIndex { get; }

    /// <summary>Fingerprint of the reviewed shape as the run saw it.</summary>
    public string? TargetHash { get; }

    public string Label { get; }

    /// <summary>"polygon", "rectangle" or "oriented_rectangle" (four corners in order).</summary>
    public string ShapeType { get; }

    public IReadOnlyList<double[]> Points { get; }

    public IReadOnlyList<IReadOnlyList<double[]>> Holes { get; }

    public double? Score { get; }

    public string Note { get; }

    /// <summary>"sam2" when the outline already comes from SAM2 (sam2_segment / detect_points): the local SAM2 refinement has nothing to improve. "agent" otherwise.</summary>
    public string Source { get; init; } = "agent";
}

/// <summary>What <see cref="AgentRunner"/> returns. Never null, never thrown for an expected failure.</summary>
public sealed record AgentRunResult(AgentOutcomeKind Kind)
{
    public IReadOnlyList<AgentProposal> Proposals { get; init; } = Array.Empty<AgentProposal>();

    public double CostUsd { get; init; }

    public int Turns { get; init; }

    public AgentUsage Usage { get; init; } = AgentUsage.Empty;

    public long DurationMs { get; init; }

    /// <summary>The agent's closing message.</summary>
    public string ResultText { get; init; } = string.Empty;

    /// <summary>Raw failure text for <see cref="AgentOutcomeKind.Failed"/> (and the reason of the other failures).</summary>
    public string Message { get; init; } = string.Empty;

    public string Model { get; init; } = AgentCommandLine.Model;

    /// <summary>Exit code of the agent process when it ended by itself (null when no process result is behind this result).</summary>
    public int? ExitCode { get; init; }

    /// <summary>Length of the process output (stdout plus stderr); the persistent log records this instead of the text.</summary>
    public int OutputLength { get; init; }

    /// <summary>The proposals came from the result cache: nothing was started and nothing was spent.</summary>
    public bool FromCache { get; init; }

    /// <summary>
    /// <see cref="CostUsd"/> is an estimate (the run was cancelled, timed out, stopped at its budget, crashed, or Codex, which reports no cost).
    /// False for a cost the agent reported itself, and for cached answers.
    /// </summary>
    public bool IsCostEstimated { get; init; }
}

/// <summary>The parsed JSON of <c>claude -p --output-format json</c>.</summary>
public sealed class AgentCliOutput
{
    public bool IsError { get; init; }

    public string Result { get; init; } = string.Empty;

    public double CostUsd { get; init; }

    public int Turns { get; init; }

    public long DurationMs { get; init; }

    public AgentUsage Usage { get; init; } = AgentUsage.Empty;

    /// <summary>
    /// Parses the standard output of a run. Returns null when it is not the JSON result (empty, text, broken JSON, no object); a JSON array
    /// (some versions print every event) is searched for its last "result" object.
    /// </summary>
    public static AgentCliOutput? Parse(string? stdout)
    {
        string text = (stdout ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            return null;
        }

        JToken token;
        try
        {
            token = JToken.Parse(text);
        }
        catch (Newtonsoft.Json.JsonException)
        {
            return ParseJsonLines(text);
        }

        JObject? root = null;
        if (token is JObject single)
        {
            root = single;
        }
        else if (token is JArray array)
        {
            for (int i = array.Count - 1; i >= 0; i--)
            {
                if (array[i] is JObject item && string.Equals(item.Value<string>("type"), "result", StringComparison.Ordinal))
                {
                    root = item;
                    break;
                }
            }
        }

        return FromRoot(root);
    }

    /// <summary>Stream-json output: one JSON object per line; the last line whose type is "result" wins. Other and broken lines are skipped.</summary>
    private static AgentCliOutput? ParseJsonLines(string text)
    {
        string[] lines = text.Split('\n');
        for (int i = lines.Length - 1; i >= 0; i--)
        {
            string line = lines[i].Trim();
            if (line.Length == 0 || line[0] != '{')
            {
                continue;
            }

            try
            {
                if (JToken.Parse(line) is JObject item && string.Equals(item.Value<string>("type"), "result", StringComparison.Ordinal))
                {
                    return FromRoot(item);
                }
            }
            catch (Newtonsoft.Json.JsonException)
            {
            }
        }

        return null;
    }

    private static AgentCliOutput? FromRoot(JObject? root)
    {
        if (root == null || (root["result"] == null && root["is_error"] == null && root["total_cost_usd"] == null))
        {
            return null;
        }

        JObject? usage = root["usage"] as JObject;
        return new AgentCliOutput
        {
            IsError = ReadBool(root["is_error"]),
            Result = root["result"]?.Type == JTokenType.String ? root.Value<string>("result") ?? string.Empty : string.Empty,
            CostUsd = ReadDouble(root["total_cost_usd"]),
            Turns = (int)Math.Min(int.MaxValue, ReadLong(root["num_turns"])),
            DurationMs = ReadLong(root["duration_ms"]),
            Usage = new AgentUsage(
                ReadLong(usage?["input_tokens"]),
                ReadLong(usage?["cache_creation_input_tokens"]),
                ReadLong(usage?["cache_read_input_tokens"]),
                ReadLong(usage?["output_tokens"])),
        };
    }

    private static bool ReadBool(JToken? token)
    {
        return token != null && token.Type == JTokenType.Boolean && token.Value<bool>();
    }

    private static double ReadDouble(JToken? token)
    {
        return token != null && (token.Type == JTokenType.Float || token.Type == JTokenType.Integer) ? token.Value<double>() : 0d;
    }

    private static long ReadLong(JToken? token)
    {
        return token != null && (token.Type == JTokenType.Integer || token.Type == JTokenType.Float) ? token.Value<long>() : 0L;
    }
}

/// <summary>Recognises the messages Claude Code prints when nobody is signed in.</summary>
public static class AgentLoginDetector
{
    /// <summary>A text this long is an answer, not a login message.</summary>
    public const int MaxMessageLength = 300;

    private static readonly string[] Phrases =
    {
        "not logged in",
        "not signed in",
        "please run /login",
        "run /login",
        "authentication_error",
        "authentication failed",
        "failed to authenticate",
        "invalid api key",
        "missing bearer",
    };

    /// <summary>The HTTP status 401 as a whole number ("HTTP 401", "API Error: 401"), not part of a longer number or word.</summary>
    private static readonly Regex Unauthorized = new Regex(@"(?<![0-9A-Za-z])401(?![0-9A-Za-z])", RegexOptions.CultureInvariant);

    /// <summary>True when the text holds one of the login phrases. The caller decides which texts may be asked (see the classifier).</summary>
    public static bool LooksSignedOut(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        foreach (string phrase in Phrases)
        {
            if (text.Contains(phrase, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return Unauthorized.IsMatch(text);
    }

    /// <summary><see cref="LooksSignedOut"/> for a text shorter than <see cref="MaxMessageLength"/> only.</summary>
    public static bool LooksSignedOutShort(string? text)
    {
        string trimmed = (text ?? string.Empty).Trim();
        return trimmed.Length < MaxMessageLength && LooksSignedOut(trimmed);
    }
}

/// <summary>Turns the end state of a process (exit code, both streams) into a result; no I/O, so it is unit tested.</summary>
public static class AgentOutcomeClassifier
{
    public const int RawMessageLimit = 300;

    /// <summary>The result without proposals; the runner adds them from <c>proposals.json</c> when the kind is Success.</summary>
    public static AgentRunResult Classify(int exitCode, string? stdout, string? stderr)
    {
        int outputLength = (stdout ?? string.Empty).Length + (stderr ?? string.Empty).Length;
        AgentCliOutput? parsed = AgentCliOutput.Parse(stdout);
        if (parsed != null)
        {
            if (!parsed.IsError)
            {
                return new AgentRunResult(AgentOutcomeKind.Success)
                {
                    CostUsd = parsed.CostUsd,
                    Turns = parsed.Turns,
                    Usage = parsed.Usage,
                    DurationMs = parsed.DurationMs,
                    ResultText = parsed.Result,
                    ExitCode = exitCode,
                    OutputLength = outputLength,
                };
            }

            // Only the short error text of the result can be a login message; a long answer that mentions a phrase is not one.
            bool signedOut = AgentLoginDetector.LooksSignedOutShort(parsed.Result);
            return new AgentRunResult(signedOut ? AgentOutcomeKind.NotSignedIn : AgentOutcomeKind.Failed)
            {
                CostUsd = parsed.CostUsd,
                Turns = parsed.Turns,
                Usage = parsed.Usage,
                DurationMs = parsed.DurationMs,
                ResultText = parsed.Result,
                Message = Limit(parsed.Result.Length > 0 ? parsed.Result : (stderr ?? string.Empty)),
                ExitCode = exitCode,
                OutputLength = outputLength,
            };
        }

        string raw = (stdout ?? string.Empty).Trim();
        string error = (stderr ?? string.Empty).Trim();
        if (AgentLoginDetector.LooksSignedOutShort(error) || AgentLoginDetector.LooksSignedOutShort(raw))
        {
            return new AgentRunResult(AgentOutcomeKind.NotSignedIn)
            {
                Message = Limit(raw.Length > 0 ? raw : error),
                ExitCode = exitCode,
                OutputLength = outputLength,
            };
        }

        // An empty message means "no output" (for example --max-turns was hit): the view model shows the localized exit-code text.
        string detail = raw.Length > 0 ? raw : error;
        return new AgentRunResult(AgentOutcomeKind.Failed)
        {
            Message = detail.Length > 0 ? Limit(detail) : string.Empty,
            ExitCode = exitCode,
            OutputLength = outputLength,
        };
    }

    /// <summary>
    /// The result of a <c>codex exec</c> run: exit 0 is a success whose closing message is the text of the <c>-o</c> file; anything else is a
    /// sign-in problem (a short login message) or a failure with the first characters of the output.
    /// </summary>
    public static AgentRunResult ClassifyCodex(int exitCode, string? stdout, string? stderr, string? lastMessage)
    {
        int outputLength = (stdout ?? string.Empty).Length + (stderr ?? string.Empty).Length;
        string message = (lastMessage ?? string.Empty).Trim();
        if (exitCode == 0)
        {
            return new AgentRunResult(AgentOutcomeKind.Success)
            {
                ResultText = message,
                Model = AgentCommandLine.CodexModel,
                ExitCode = exitCode,
                OutputLength = outputLength,
            };
        }

        string error = StripCodexBanner(stderr);
        string raw = (stdout ?? string.Empty).Trim();
        // The Codex stderr is a log, never an answer, so its length does not matter: a 401 after five reconnect lines is still a sign-in problem.
        bool signedOut = AgentLoginDetector.LooksSignedOut(error) || AgentLoginDetector.LooksSignedOutShort(raw) || AgentLoginDetector.LooksSignedOutShort(message);
        string detail = error.Length > 0 ? error : (message.Length > 0 ? message : raw);
        return new AgentRunResult(signedOut ? AgentOutcomeKind.NotSignedIn : AgentOutcomeKind.Failed)
        {
            Message = detail.Length > 0 ? Limit(detail) : string.Empty,
            Model = AgentCommandLine.CodexModel,
            ExitCode = exitCode,
            OutputLength = outputLength,
        };
    }

    /// <summary>
    /// The stderr of a failed Codex run without its start banner ("Reading additional input from stdin...", version, workdir, model, session id) and
    /// without the echoed prompt, so the 300 character message shows the real error. An ERROR line wins when there is one.
    /// </summary>
    public static string StripCodexBanner(string? stderr)
    {
        string text = (stderr ?? string.Empty).Replace("\r\n", "\n").Trim();
        if (text.Length == 0)
        {
            return string.Empty;
        }

        string[] lines = text.Split('\n');
        List<string> errors = new List<string>();
        foreach (string line in lines)
        {
            string trimmed = line.Trim();
            bool isError = trimmed.StartsWith("ERROR", StringComparison.OrdinalIgnoreCase) || trimmed.StartsWith("stream error", StringComparison.OrdinalIgnoreCase);
            // "ERROR: Reconnecting... 2/5" repeats the same failure; the final error line says why.
            if (isError && !trimmed.Contains("Reconnecting...", StringComparison.OrdinalIgnoreCase) && !errors.Contains(trimmed))
            {
                errors.Add(trimmed);
            }
        }

        if (errors.Count > 0)
        {
            return string.Join(" ", errors);
        }

        int lastSession = -1;
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].TrimStart().StartsWith("session id:", StringComparison.OrdinalIgnoreCase))
            {
                lastSession = i;
            }
        }

        if (lastSession < 0)
        {
            return text;
        }

        // After "session id:" come the closing "--------" and, in exec mode, "user" and the prompt; keep only what follows the prompt echo's section.
        int start = lastSession + 1;
        while (start < lines.Length && (lines[start].Trim().Length == 0 || lines[start].Trim().All(c => c == '-')))
        {
            start++;
        }

        string rest = string.Join("\n", lines.Skip(start)).Trim();
        return rest.Length > 0 ? rest : text;
    }

    /// <summary>The first <see cref="RawMessageLimit"/> characters, white space collapsed to single spaces.</summary>
    public static string Limit(string text)
    {
        string collapsed = Regex.Replace(text ?? string.Empty, @"\s+", " ").Trim();
        return collapsed.Length <= RawMessageLimit ? collapsed : collapsed.Substring(0, RawMessageLimit);
    }
}

/// <summary>Reads <c>proposals.json</c> (written by the MCP server) and turns the proposals into suggestions.</summary>
public static class AgentProposals
{
    /// <summary>Provenance backend name of every suggestion that comes from the agent.</summary>
    public const string BackendName = "Agent";

    /// <summary>At most this many proposals are read from one file.</summary>
    public const int MaxProposals = 200;

    /// <summary>A proposals.json larger than this is refused unread.</summary>
    public const long MaxFileBytes = 8L * 1024 * 1024;

    /// <summary>The proposals of a <c>proposals.json</c> text; broken text or a broken item gives fewer (never an exception).</summary>
    public static IReadOnlyList<AgentProposal> Parse(string? json)
    {
        List<AgentProposal> list = new List<AgentProposal>();
        if (string.IsNullOrWhiteSpace(json))
        {
            return list;
        }

        JObject root;
        try
        {
            root = JObject.Parse(json);
        }
        catch (Newtonsoft.Json.JsonException)
        {
            return list;
        }

        if (root["proposals"] is not JArray items)
        {
            return list;
        }

        foreach (JToken item in items)
        {
            if (list.Count >= MaxProposals)
            {
                break;
            }

            AgentProposal? proposal = ParseItem(item as JObject);
            if (proposal != null)
            {
                list.Add(proposal);
            }
        }

        return list;
    }

    private static AgentProposal? ParseItem(JObject? item)
    {
        if (item == null)
        {
            return null;
        }

        // Review fields (optional; a file without them is a plain list of new shapes). A target without a valid action is ignored.
        string? action = null;
        int? targetIndex = null;
        string? targetHash = null;
        string actionText = (item["action"]?.Type == JTokenType.String ? item.Value<string>("action") ?? string.Empty : string.Empty).Trim().ToLowerInvariant();
        JToken? targetToken = item["target"];
        if ((actionText == SegAiSuggestion.ActionReplace || actionText == SegAiSuggestion.ActionRemove)
            && targetToken != null && targetToken.Type == JTokenType.Integer && targetToken.Value<long>() >= 0 && targetToken.Value<long>() <= int.MaxValue)
        {
            action = actionText;
            targetIndex = (int)targetToken.Value<long>();
            targetHash = item["targetHash"]?.Type == JTokenType.String ? item.Value<string>("targetHash") : null;
        }

        string shapeType = (item.Value<string>("shapeType") ?? "polygon").Trim().ToLowerInvariant();
        List<double[]>? points = ParsePoints(item["points"]);
        if (action == SegAiSuggestion.ActionRemove)
        {
            // A remove needs no outline.
            return new AgentProposal(
                (item.Value<string>("label") ?? string.Empty).Trim(),
                AiSuggestionMapper.PolygonShape,
                Array.Empty<double[]>(),
                new List<IReadOnlyList<double[]>>(),
                null,
                (item.Value<string>("note") ?? string.Empty).Trim(),
                action,
                targetIndex,
                targetHash);
        }

        if (shapeType != AiSuggestionMapper.PolygonShape && shapeType != AiSuggestionMapper.RectangleShape && shapeType != AiSuggestionMapper.OrientedRectangleShape)
        {
            return null;
        }

        if (points == null || points.Count == 0)
        {
            return null;
        }

        List<IReadOnlyList<double[]>> holes = new List<IReadOnlyList<double[]>>();
        if (item["holes"] is JArray holeArray)
        {
            foreach (JToken hole in holeArray)
            {
                List<double[]>? ring = ParsePoints(hole);
                if (ring != null && ring.Count >= 3)
                {
                    holes.Add(ring);
                }
            }
        }

        double? score = item["score"] != null && (item["score"]!.Type == JTokenType.Float || item["score"]!.Type == JTokenType.Integer)
            ? item["score"]!.Value<double>()
            : null;
        return new AgentProposal(
            (item.Value<string>("label") ?? string.Empty).Trim(),
            shapeType,
            points,
            holes,
            score,
            (item.Value<string>("note") ?? string.Empty).Trim(),
            action,
            targetIndex,
            targetHash)
        {
            Source = string.Equals(item.Value<string>("source"), "sam2", StringComparison.OrdinalIgnoreCase) ? "sam2" : "agent",
        };
    }

    private static List<double[]>? ParsePoints(JToken? token)
    {
        if (token is not JArray array)
        {
            return null;
        }

        List<double[]> points = new List<double[]>();
        foreach (JToken p in array)
        {
            if (p is not JArray pair || pair.Count != 2
                || (pair[0].Type != JTokenType.Float && pair[0].Type != JTokenType.Integer)
                || (pair[1].Type != JTokenType.Float && pair[1].Type != JTokenType.Integer))
            {
                return null;
            }

            double x = pair[0].Value<double>();
            double y = pair[1].Value<double>();
            if (!double.IsFinite(x) || !double.IsFinite(y))
            {
                return null;
            }

            points.Add(new[] { x, y });
        }

        return points;
    }

    /// <summary>The proposals as <c>proposals.json</c> text (the result cache keeps this form).</summary>
    public static string ToJson(IReadOnlyList<AgentProposal> proposals)
    {
        JArray items = new JArray();
        foreach (AgentProposal p in proposals)
        {
            JObject entry = new JObject
            {
                ["label"] = p.Label,
                ["shapeType"] = p.ShapeType,
                ["points"] = new JArray(p.Points.Select(pt => (object)new JArray(pt[0], pt[1]))),
                ["holes"] = new JArray(p.Holes.Select(h => (object)new JArray(h.Select(pt => (object)new JArray(pt[0], pt[1]))))),
                ["score"] = p.Score.HasValue ? new JValue(p.Score.Value) : JValue.CreateNull(),
                ["note"] = p.Note,
            };
            if (p.Action != null && p.TargetIndex.HasValue)
            {
                entry["action"] = p.Action;
                entry["target"] = p.TargetIndex.Value;
                entry["targetHash"] = p.TargetHash ?? string.Empty;
            }

            items.Add(entry);
        }

        return new JObject { ["version"] = 1, ["proposals"] = items }.ToString(Newtonsoft.Json.Formatting.Indented);
    }

    /// <summary>
    /// The class of a proposal label among <paramref name="classNames"/> (case-insensitive: 0 = NG, 1 = OK), or
    /// <paramref name="fallbackClassIndex"/> when the project has no such class.
    /// </summary>
    public static int ClassIndexOf(string? label, IReadOnlyList<string> classNames, int fallbackClassIndex)
    {
        string wanted = (label ?? string.Empty).Trim();
        for (int i = 0; i < classNames.Count; i++)
        {
            if (string.Equals((classNames[i] ?? string.Empty).Trim(), wanted, StringComparison.OrdinalIgnoreCase) && wanted.Length > 0)
            {
                return i;
            }
        }

        return fallbackClassIndex;
    }

    /// <summary>
    /// The proposals as suggestions of <paramref name="rel"/> (the dashed layer). A polygon keeps its holes; a rectangle becomes a "rectangle"
    /// suggestion; a degenerate proposal is dropped. Provenance is <c>Agent | model | note</c>, the note being the prompt start when the
    /// agent wrote none. A proposal without a score gets 0 (the Segment score slider does not filter AI results).
    /// </summary>
    public static IReadOnlyList<SegAiSuggestion> ToSuggestions(
        IReadOnlyList<AgentProposal> proposals,
        string rel,
        IReadOnlyList<string> classNames,
        int fallbackClassIndex,
        string model,
        string prompt)
    {
        string snippet = PromptSnippet(prompt);
        List<SegAiSuggestion> made = new List<SegAiSuggestion>();
        foreach (AgentProposal proposal in proposals)
        {
            int classIndex = ClassIndexOf(proposal.Label, classNames, fallbackClassIndex);
            string note = proposal.Note.Length > 0 ? AgentOutcomeClassifier.Limit(proposal.Note) : snippet;
            float score = proposal.Score.HasValue ? (float)proposal.Score.Value : 0f;
            if (proposal.Action == SegAiSuggestion.ActionRemove && proposal.TargetIndex.HasValue)
            {
                // A remove has no outline: the suggestion only names the shape to delete (S4 draws and applies it).
                string removeLabel = proposal.Label.Trim();
                made.Add(new SegAiSuggestion(
                    rel,
                    classIndex,
                    Array.Empty<PointF>(),
                    Array.Empty<PointF[]>(),
                    score,
                    removeLabel.Length > 0 ? SuggestionLabel(removeLabel, classIndex, classNames) : null,
                    AiSuggestionMapper.PolygonShape,
                    AiSuggestionMapper.Provenance(BackendName, model, note),
                    proposal.Action,
                    proposal.TargetIndex,
                    proposal.TargetHash,
                    proposal.Note.Length > 0 ? AgentOutcomeClassifier.Limit(proposal.Note) : null));
                continue;
            }

            if (proposal.ShapeType == AiSuggestionMapper.OrientedRectangleShape && proposal.Points.Count != 4)
            {
                continue;
            }

            AiProposal ai;
            if (proposal.ShapeType == AiSuggestionMapper.RectangleShape)
            {
                if (proposal.Points.Count < 2)
                {
                    continue;
                }

                float left = (float)proposal.Points.Min(p => p[0]);
                float top = (float)proposal.Points.Min(p => p[1]);
                float right = (float)proposal.Points.Max(p => p[0]);
                float bottom = (float)proposal.Points.Max(p => p[1]);
                ai = new AiProposal(Array.Empty<PointF>(), Array.Empty<IReadOnlyList<PointF>>(), score, note, true, new RectangleF(left, top, right - left, bottom - top));
            }
            else
            {
                PointF[] outer = proposal.Points.Select(p => new PointF((float)p[0], (float)p[1])).ToArray();
                List<IReadOnlyList<PointF>> holes = proposal.Holes
                    .Select(h => (IReadOnlyList<PointF>)h.Select(p => new PointF((float)p[0], (float)p[1])).ToArray())
                    .ToList();
                ai = new AiProposal(outer, holes, score, note, false, RectangleF.Empty);
            }

            SegAiSuggestion? suggestion = AiSuggestionMapper.ToSuggestion(ai, rel, classIndex, BackendName, model);
            if (suggestion != null && proposal.ShapeType == AiSuggestionMapper.OrientedRectangleShape)
            {
                // The four corners are the Outer ring already; only the shape type differs from a polygon.
                suggestion = suggestion with { ShapeType = AiSuggestionMapper.OrientedRectangleShape };
            }

            if (suggestion != null)
            {
                // The proposal's own label is pasted when the suggestion is accepted: an existing class in its project spelling, a new label as typed.
                string labelText = proposal.Label.Trim();
                if (labelText.Length > 0)
                {
                    suggestion = suggestion with { Label = SuggestionLabel(labelText, classIndex, classNames) };
                }

                if (proposal.Action != null && proposal.TargetIndex.HasValue)
                {
                    suggestion = suggestion with
                    {
                        Action = proposal.Action,
                        TargetIndex = proposal.TargetIndex,
                        TargetHash = proposal.TargetHash,
                        Note = proposal.Note.Length > 0 ? AgentOutcomeClassifier.Limit(proposal.Note) : null,
                    };
                }

                made.Add(suggestion);
            }
        }

        return made;
    }

    private static string SuggestionLabel(string labelText, int classIndex, IReadOnlyList<string> classNames)
    {
        return classIndex >= 0 && classIndex < classNames.Count && string.Equals((classNames[classIndex] ?? string.Empty).Trim(), labelText, StringComparison.OrdinalIgnoreCase)
            ? classNames[classIndex].Trim()
            : labelText;
    }

    /// <summary>The first 60 characters of the prompt on one line (provenance text when the agent wrote no note).</summary>
    public static string PromptSnippet(string? prompt)
    {
        string text = AgentOutcomeClassifier.Limit(prompt ?? string.Empty);
        return text.Length <= 60 ? text : text.Substring(0, 60);
    }
}
