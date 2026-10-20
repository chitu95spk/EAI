using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using Newtonsoft.Json;

namespace EasyAI.Desktop.Services.Agent;

/// <summary>Price of one model in USD per million tokens, as the vendor's pricing page lists it (standard tier, short context).</summary>
public sealed class AgentPriceEntry
{
    /// <summary>"claude" or "openai".</summary>
    public string Provider { get; set; } = string.Empty;

    public string Model { get; set; } = string.Empty;

    public double InputPerMTok { get; set; }

    public double OutputPerMTok { get; set; }

    /// <summary>Cache read price; null when the page has no such column.</summary>
    public double? CachedInputPerMTok { get; set; }
}

/// <summary>The prices read from the two pricing pages and the time they were read.</summary>
public sealed class AgentPriceTable
{
    public const string ClaudeProvider = "claude";
    public const string OpenAiProvider = "openai";

    // A run of the agent on one image: the image, the prompt, the tool definitions and the conversation. These are rough, only used for an
    // estimate before any run of the model is in the log (a logged run always wins).
    public const long NominalInputTokensPerImage = 20000;
    public const long NominalOutputTokensPerImage = 2000;

    public DateTime FetchedUtc { get; set; }

    public List<AgentPriceEntry> Entries { get; set; } = new List<AgentPriceEntry>();

    public bool IsEmpty => Entries.Count == 0;

    public int Count(string provider) => Entries.Count(e => e.Provider == provider);

    private static string Normalize(string? text)
    {
        return new string((text ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
    }

    /// <summary>
    /// The price of a model name as the agent programs report it ("claude-sonnet-5-5", "sonnet", "Claude Sonnet 5.5", "gpt-6.1-sol").
    /// An exact match (ignoring case and punctuation) wins; otherwise the first listed model of the same family (the pages list the newest first).
    /// </summary>
    public AgentPriceEntry? Find(string? provider, string? model)
    {
        string wanted = Normalize(model);
        if (wanted.Length == 0)
        {
            return null;
        }

        IEnumerable<AgentPriceEntry> pool = string.IsNullOrEmpty(provider) ? Entries : Entries.Where(e => e.Provider == provider);
        List<AgentPriceEntry> list = pool.ToList();
        foreach (AgentPriceEntry entry in list)
        {
            string key = Normalize(entry.Model);
            if (key == wanted || key == "claude" + wanted || wanted == "claude" + key)
            {
                return entry;
            }
        }

        foreach (AgentPriceEntry entry in list)
        {
            string key = Normalize(entry.Model);
            if (key.Contains(wanted, StringComparison.Ordinal) || (key.Length >= 4 && wanted.Contains(key, StringComparison.Ordinal)))
            {
                return entry;
            }
        }

        return null;
    }

    /// <summary>USD for the given tokens at the model's price; null when the model is not in the table.</summary>
    public double? CostOf(string? provider, string? model, long inputTokens, long cachedInputTokens, long outputTokens)
    {
        AgentPriceEntry? price = Find(provider, model);
        if (price == null)
        {
            return null;
        }

        double cachedRate = price.CachedInputPerMTok ?? price.InputPerMTok;
        return (Math.Max(0, inputTokens) * price.InputPerMTok + Math.Max(0, cachedInputTokens) * cachedRate + Math.Max(0, outputTokens) * price.OutputPerMTok) / 1_000_000d;
    }

    /// <summary>USD of one nominal agent run on one image; null when the model has no price.</summary>
    public double? NominalCostPerImage(string? model)
    {
        string text = (model ?? string.Empty).Trim();
        string provider = text.StartsWith("gpt", StringComparison.OrdinalIgnoreCase) || text.StartsWith("codex", StringComparison.OrdinalIgnoreCase) || text.StartsWith("o", StringComparison.OrdinalIgnoreCase) && text.Length > 1 && char.IsDigit(text[1])
            ? OpenAiProvider
            : ClaudeProvider;
        AgentPriceEntry? price = Find(provider, text);
        if (price == null && string.Equals(text, AgentCommandLine.CodexModel, StringComparison.OrdinalIgnoreCase))
        {
            // The Codex program picks its own default model; the first (newest) model of the OpenAI page is the nearest price.
            price = Entries.FirstOrDefault(e => e.Provider == OpenAiProvider);
        }

        return price == null ? null : CostOf(price.Provider, price.Model, NominalInputTokensPerImage, 0, NominalOutputTokensPerImage);
    }
}

/// <summary>Reads the model price tables of the two vendors' pricing pages (HTML tables). No I/O, so it is unit tested.</summary>
public static class AgentPriceParser
{
    private static readonly Regex Tables = new Regex("<table.*?</table>", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Rows = new Regex("<tr[ >].*?</tr>", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Cells = new Regex("<t[hd][ >].*?</t[hd]>", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Anchor = new Regex(@"<a(?:\s[^>]*)?>(.*?)</a>", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Tags = new Regex("<[^>]+>", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex Money = new Regex(@"\$\s*([0-9]*\.?[0-9]+)", RegexOptions.Compiled);

    public static List<AgentPriceEntry> ParseClaude(string? html) => Parse(html, AgentPriceTable.ClaudeProvider);

    public static List<AgentPriceEntry> ParseOpenAi(string? html) => Parse(html, AgentPriceTable.OpenAiProvider);

    /// <summary>
    /// The first table that has a header row with "Input" and "Output" columns (the standard tier of both pages). Only rows with as many cells
    /// as the header count (a sub-row under a rowspan cell, e.g. a long-prompt surcharge, is not a model); a cell without a "$" price skips the row.
    /// </summary>
    internal static List<AgentPriceEntry> Parse(string? html, string provider)
    {
        List<AgentPriceEntry> result = new List<AgentPriceEntry>();
        if (string.IsNullOrEmpty(html))
        {
            return result;
        }

        foreach (Match table in Tables.Matches(html))
        {
            List<string> rows = Rows.Matches(table.Value).Select(m => m.Value).ToList();
            for (int headerAt = 0; headerAt < rows.Count; headerAt++)
            {
                List<string> header = Cells.Matches(rows[headerAt]).Select(m => Text(m.Value, false).ToLowerInvariant()).ToList();
                int input = header.IndexOf("input");
                int output = header.IndexOf("output");
                if (input < 0 || output < 0)
                {
                    continue;
                }

                int cached = header.FindIndex(h => h == "cached input" || h.StartsWith("hits", StringComparison.Ordinal));
                for (int r = headerAt + 1; r < rows.Count; r++)
                {
                    List<string> cells = Cells.Matches(rows[r]).Select(m => m.Value).ToList();
                    if (cells.Count != header.Count)
                    {
                        continue;
                    }

                    string name = Text(cells[0], true);
                    double? inputPrice = Price(Text(cells[input], false));
                    double? outputPrice = Price(Text(cells[output], false));
                    if (name.Length == 0 || !inputPrice.HasValue || !outputPrice.HasValue)
                    {
                        continue;
                    }

                    result.Add(new AgentPriceEntry
                    {
                        Provider = provider,
                        Model = name,
                        InputPerMTok = inputPrice.Value,
                        OutputPerMTok = outputPrice.Value,
                        CachedInputPerMTok = cached >= 0 ? Price(Text(cells[cached], false)) : null,
                    });
                }

                if (result.Count > 0)
                {
                    return result;
                }
            }
        }

        return result;
    }

    private static string Text(string cell, bool preferAnchor)
    {
        string source = cell;
        if (preferAnchor)
        {
            Match anchor = Anchor.Match(cell);
            if (anchor.Success)
            {
                source = anchor.Groups[1].Value;
            }
        }

        return Regex.Replace(WebUtility.HtmlDecode(Tags.Replace(source, " ")), @"\s+", " ").Trim();
    }

    private static double? Price(string text)
    {
        Match m = Money.Match(text);
        return m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ? value : null;
    }
}

/// <summary>What one refresh did.</summary>
public sealed record AgentPriceRefreshResult(int ClaudeCount, int OpenAiCount, string? Error)
{
    public bool Updated => ClaudeCount > 0 || OpenAiCount > 0;
}

/// <summary>Stores the price table as a JSON file and refreshes it from the vendors' pricing pages (neither vendor has a price API).</summary>
public static class AgentPriceStore
{
    public const string FileName = "agent-prices.json";
    public const string ClaudeUrl = "https://platform.claude.com/docs/en/about-claude/pricing";
    public const string OpenAiUrl = "https://developers.openai.com/api/docs/pricing";

    public static string DefaultPath() => System.IO.Path.Combine(AgentRunFolder.DefaultRoot(), FileName);

    /// <summary>The stored table; an empty table when there is no file or it cannot be read.</summary>
    public static AgentPriceTable Load(string? path = null)
    {
        try
        {
            string file = path ?? DefaultPath();
            if (!File.Exists(file))
            {
                return new AgentPriceTable();
            }

            return JsonConvert.DeserializeObject<AgentPriceTable>(File.ReadAllText(file)) ?? new AgentPriceTable();
        }
        catch (Exception ex) when (ex is IOException || ex is JsonException || ex is UnauthorizedAccessException)
        {
            return new AgentPriceTable();
        }
    }

    public static void Save(AgentPriceTable table, string? path = null)
    {
        string file = path ?? DefaultPath();
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file)!);
        string temp = file + ".tmp";
        File.WriteAllText(temp, JsonConvert.SerializeObject(table, Formatting.Indented));
        File.Move(temp, file, true);
    }

    /// <summary>
    /// Downloads both pages and replaces the prices of the provider that was read. A page that cannot be read or has no price table keeps the
    /// old prices of that provider (the error says which); nothing is written when neither page gave prices.
    /// </summary>
    public static async Task<AgentPriceRefreshResult> RefreshAsync(HttpClient http, string? path, CancellationToken ct, Func<bool>? isOffline = null)
    {
        if (isOffline?.Invoke() == true)
        {
            return new AgentPriceRefreshResult(0, 0, EasyAI.Desktop.Services.AgentRunner.OfflineMessage);
        }

        AgentPriceTable table = Load(path);
        List<string> errors = new List<string>();
        int claude = 0;
        int openAi = 0;

        List<AgentPriceEntry> claudeEntries = await FetchAsync(http, ClaudeUrl, AgentPriceParser.ParseClaude, "Claude", errors, ct);
        if (claudeEntries.Count > 0)
        {
            table.Entries.RemoveAll(e => e.Provider == AgentPriceTable.ClaudeProvider);
            table.Entries.AddRange(claudeEntries);
            claude = claudeEntries.Count;
        }

        if (isOffline?.Invoke() == true)
        {
            return new AgentPriceRefreshResult(0, 0, EasyAI.Desktop.Services.AgentRunner.OfflineMessage);
        }

        List<AgentPriceEntry> openAiEntries = await FetchAsync(http, OpenAiUrl, AgentPriceParser.ParseOpenAi, "OpenAI", errors, ct);
        if (openAiEntries.Count > 0)
        {
            table.Entries.RemoveAll(e => e.Provider == AgentPriceTable.OpenAiProvider);
            table.Entries.AddRange(openAiEntries);
            openAi = openAiEntries.Count;
        }

        if (claude + openAi > 0)
        {
            table.FetchedUtc = DateTime.UtcNow;
            Save(table, path);
        }

        return new AgentPriceRefreshResult(claude, openAi, errors.Count == 0 ? null : string.Join("; ", errors));
    }

    private static async Task<List<AgentPriceEntry>> FetchAsync(
        HttpClient http,
        string url,
        Func<string?, List<AgentPriceEntry>> parse,
        string name,
        List<string> errors,
        CancellationToken ct)
    {
        try
        {
            string html = await http.GetStringAsync(url, ct).ConfigureAwait(false);
            List<AgentPriceEntry> entries = parse(html);
            if (entries.Count == 0)
            {
                errors.Add(name + ": no price table on the page");
            }

            return entries;
        }
        catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException || ex is IOException)
        {
            if (ct.IsCancellationRequested)
            {
                throw;
            }

            errors.Add(name + ": " + ex.Message);
            return new List<AgentPriceEntry>();
        }
    }
}
