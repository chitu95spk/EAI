using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using EasyAI.Desktop.Services.Agent;

namespace EasyAI.Desktop.Services;

/// <summary>Builds the process start info of one agent run: program, arguments, working directory and TEMP folder.</summary>
public delegate ProcessStartInfo AgentStartInfoFactory(string claudePath, IReadOnlyList<string> arguments, string workingDirectory, string tempPath);

/// <summary>
/// Runs the Claude Code agent on one image: finds <c>claude</c> and the MCP tool executable, writes the run folder, starts
/// <c>claude -p</c> hidden with stdin closed, waits (5 minutes at most), kills the whole process tree on cancel or time-out, and turns the
/// outcome plus <c>proposals.json</c> into an <see cref="AgentRunResult"/>. A finished run is cached and logged. The started process is
/// also put into a kill-on-close Job Object and can be stopped synchronously with <see cref="CancelAndKill"/> (application exit).
/// </summary>
public sealed class AgentRunner
{
    /// <summary>The longest an agent run may take.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(5);

    private readonly IAgentEnvironment _env;
    private readonly string _applicationFolder;
    private readonly string _runsRoot;
    private readonly string _cacheRoot;
    private readonly AgentStartInfoFactory _startInfo;
    private readonly TimeSpan _timeout;
    private readonly AgentJob? _job;
    private readonly object _liveLock = new object();
    private Process? _live;
    private volatile bool _killed;

    /// <summary>The agent program this runner starts: Claude Code (default) or Codex. The Settings choice sets it before each run.</summary>
    public AgentTool Tool { get; set; } = AgentTool.Claude;

    /// <summary>Box accuracy, effort and model chosen in the Agent Prompt panel (the default reproduces the original run).</summary>
    public AgentRunOptions Options { get; set; } = AgentRunOptions.Default;

    /// <summary>Names of the run skills switched off in Settings; they are left out of the system prompt and the cache key. The Label step sets it before each run.</summary>
    public IReadOnlyCollection<string>? DisabledSkills { get; set; }

    /// <summary>The user skill folder (<c>%LOCALAPPDATA%\EasyAI\skills</c>); null = no user skills (tests).</summary>
    public string? UserSkillsRoot { get; set; } = RunSkillCatalog.DefaultUserRoot();

    /// <summary>The registry of external MCP servers (<c>%LOCALAPPDATA%\EasyAI\mcp-servers.json</c>); null = none (tests). Only approved entries are ever used.</summary>
    public string? McpServersPath { get; set; } = McpServerRegistry.DefaultPath();

    /// <summary>Offline-only: when true no agent program is started; every run is refused with <see cref="OfflineMessage"/> (cached answers still come back). Default off.</summary>
    public bool IsOffline { get; set; }

    /// <summary>When true, a run is refused unless <see cref="ConsentGranted"/> is set (the project's consent to send images). Default off.</summary>
    public bool RequireConsent { get; set; }

    /// <summary>The user's consent for this project; read only when <see cref="RequireConsent"/> is on.</summary>
    public bool ConsentGranted { get; set; }

    /// <summary>Pass <c>--max-budget-usd</c> (the run's reservation) to claude. Default off: the flag is unverified on the installed CLI; the watchdog bounds the run either way.</summary>
    public bool MaxBudgetFlagSupported { get; set; }

    /// <summary>The spend budget, shared by every worker of a batch. Null = no budget: runs are neither reserved, watched nor settled (the old behaviour).</summary>
    public AgentSpendBudget? Budget { get; set; }

    /// <summary>The ledger that records every reservation and settlement (read by the day cap); null = budget only.</summary>
    public AgentSpendLedger? Ledger { get; set; }

    /// <summary>Prices for the usage meter; null = only the per-turn floor counts.</summary>
    public AgentPriceTable? Prices { get; set; }

    /// <summary>Ceiling of one agent run's reservation (USD); the budget grants the smaller of this and the fair share. Default 1 USD.</summary>
    public double RunCeilingUsd { get; set; } = 1.0;

    /// <summary>Message of a run refused because <see cref="IsOffline"/> is on.</summary>
    public const string OfflineMessage = "Offline-only mode is on: the agent is not started.";

    /// <summary>Message of a run refused because the project has not given consent.</summary>
    public const string ConsentMessage = "This project has not allowed the agent to receive images yet.";

    /// <summary>Message of a run refused because the spend budget has no room left.</summary>
    public const string BudgetRefusedMessage = "The agent spend limit is used up; the run was not started.";

    /// <summary>Message of a run stopped by the watchdog at its reservation.</summary>
    public const string BudgetStoppedMessage = "The agent spend limit was reached; the run was stopped.";

    /// <summary>Message of a run refused because the ledger could not record its reservation.</summary>
    public const string LedgerMessage = "The agent spend record could not be written; the run was not started.";

    // Guard and reservation of one run. Offline and consent refuse first; then the budget grants a share (or refuses) and the ledger records
    // the reservation before any process starts. With no budget the run is only guarded.
    private async Task<(AgentRunSpend? Spend, AgentRunResult? Refusal)> ReserveAsync(double ceilingUsd, string purpose, string model)
    {
        if (IsOffline)
        {
            return (null, new AgentRunResult(AgentOutcomeKind.Failed) { Message = OfflineMessage });
        }

        if (RequireConsent && !ConsentGranted)
        {
            return (null, new AgentRunResult(AgentOutcomeKind.Failed) { Message = ConsentMessage });
        }

        AgentSpendBudget? budget = Budget;
        if (budget == null)
        {
            return (null, null);
        }

        string tool = (Tool == AgentTool.Codex ? "codex " : "claude ") + purpose;
        double ceiling = double.IsNaN(ceilingUsd) || double.IsInfinity(ceilingUsd) || ceilingUsd <= 0 ? 1.0 : ceilingUsd;
        AgentReserveResult granted = budget.TryReserve(ceiling, out AgentBudgetReservation? reservation);
        if (granted != AgentReserveResult.Granted || reservation == null)
        {
            return (null, new AgentRunResult(AgentOutcomeKind.BudgetLimit) { Message = BudgetRefusedMessage });
        }

        AgentSpendLedger? ledger = Ledger;
        if (ledger != null)
        {
            AgentBudgetReservation held = reservation;
            AgentLedgerRecord? record = await Task.Run(() => ledger.Reserve(held.Id, tool, model, held.Usd)).ConfigureAwait(false);
            if (record == null)
            {
                budget.Settle(reservation, 0);
                return (null, new AgentRunResult(AgentOutcomeKind.BudgetLimit) { Message = LedgerMessage });
            }
        }

        return (new AgentRunSpend(budget, reservation, ledger, new AgentUsageMeter(Prices, model), tool, model), null);
    }

    // Unexpected failure after a reservation: the run may have spent up to its reservation, so the whole of it is charged (estimate).
    private static async Task SettleOnFailureAsync(AgentRunSpend? spend)
    {
        if (spend != null)
        {
            await spend.SettleAsync(spend.Reservation.Usd, true).ConfigureAwait(false);
        }
    }

    private IReadOnlyList<McpServerEntry> ApprovedServers()
    {
        return string.IsNullOrWhiteSpace(McpServersPath) ? Array.Empty<McpServerEntry>() : McpServerRegistry.Load(McpServersPath).ApprovedEntries();
    }

    private string ModelName => Tool == AgentTool.Codex ? (Options.CodexModel ?? AgentCommandLine.CodexModel) : Options.ClaudeModel;

    /// <param name="env">Machine access (a fake in tests).</param>
    /// <param name="applicationFolder">Folder of EasyAI.exe; the MCP tools are in its <c>agent</c> sub-folder.</param>
    /// <param name="runsRoot">Run folders and <c>log.csv</c> (default <c>%LOCALAPPDATA%\EasyAI\agent-runs</c>).</param>
    /// <param name="cacheRoot">Result cache (default <c>%LOCALAPPDATA%\EasyAI\agent-cache</c>).</param>
    /// <param name="startInfo">Replaces the real <c>claude</c> command line (tests); null = <see cref="DefaultStartInfo"/>.</param>
    /// <param name="timeout">Replaces the 5 minute limit (tests).</param>
    public AgentRunner(
        IAgentEnvironment env,
        string applicationFolder,
        string? runsRoot = null,
        string? cacheRoot = null,
        AgentStartInfoFactory? startInfo = null,
        TimeSpan? timeout = null)
    {
        _env = env;
        _applicationFolder = applicationFolder;
        _runsRoot = string.IsNullOrWhiteSpace(runsRoot) ? AgentRunFolder.DefaultRoot() : runsRoot;
        _cacheRoot = string.IsNullOrWhiteSpace(cacheRoot) ? AgentCache.DefaultRoot() : cacheRoot;
        _startInfo = startInfo ?? DefaultStartInfo;
        _timeout = timeout ?? DefaultTimeout;
        _job = AgentJob.TryCreate();
    }

    /// <summary>Writes the downscaled picture a Codex run gets with its first message (set by the application; null = no preview, as before).</summary>
    public delegate string? PreviewWriterFunc(string imagePath, string runFolder, int imageWidth, int imageHeight, string? modelsFolder, out string note);

    public static PreviewWriterFunc? PreviewWriter { get; set; }

    /// <summary>A runner on the real machine with the application folder of this process.</summary>
    public static AgentRunner CreateDefault()
    {
        AgentRunner runner = new AgentRunner(new SystemAgentEnvironment(), AppContext.BaseDirectory);
        // The hidden --fake-agent <ps1> start flag (StartupOptions) replaces the real agent program by a PowerShell script.
        string[] args = Environment.GetCommandLineArgs();
        runner.FakeAgentScript = FakeAgentFromArgs(args);
        return runner;
    }

    /// <summary>The value of the hidden <c>--fake-agent &lt;ps1&gt;</c> argument (also parsed by StartupOptions), or null.</summary>
    public static string? FakeAgentFromArgs(string[] args)
    {
        for (int i = 0; i + 1 < args.Length; i++)
        {
            if (string.Equals(args[i], "--fake-agent", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(args[i + 1])) return args[i + 1];
        }

        return null;
    }

    /// <summary>
    /// Test hook (hidden <c>--fake-agent</c> flag): when set, every run starts <c>powershell -NoProfile -File &lt;script&gt; &lt;the usual arguments&gt;</c>
    /// instead of claude/codex, and its standard output is parsed like the real agent's. The MCP tool executable is then not required.
    /// </summary>
    public string? FakeAgentScript { get; set; }

    private const string FakeAgentProgram = "powershell.exe";

    private bool IsFake => !string.IsNullOrWhiteSpace(FakeAgentScript);

    private ProcessStartInfo CreateStartInfo(string program, IReadOnlyList<string> arguments, string workingDirectory, string tempPath)
    {
        if (!IsFake)
        {
            return _startInfo(program, arguments, workingDirectory, tempPath);
        }

        List<string> all = new List<string> { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", FakeAgentScript! };
        all.AddRange(arguments);
        return DefaultStartInfo(FakeAgentProgram, all, workingDirectory, tempPath);
    }

    /// <summary>The Claude Code executable, or null.</summary>
    public string? FindClaude()
    {
        return ClaudeLocator.Find(_env);
    }

    /// <summary>The executable of the selected agent (<see cref="Tool"/>), or null.</summary>
    public string? FindAgent()
    {
        if (IsFake)
        {
            return FakeAgentProgram;
        }

        return Tool == AgentTool.Codex ? CodexLocator.Find(_env) : ClaudeLocator.Find(_env);
    }

    private string? FindMcpOrFake()
    {
        return McpLocator.Find(_env, _applicationFolder) ?? (IsFake ? McpLocator.ExeName : null);
    }

    /// <summary>The MCP tool executable, or null.</summary>
    public string? FindMcp()
    {
        return McpLocator.Find(_env, _applicationFolder);
    }

    /// <summary>
    /// Kills the running agent process tree (claude and its MCP child) now and waits at most 2 seconds for it. Safe when nothing runs,
    /// safe to call twice. The run in flight ends as Cancelled.
    /// </summary>
    public void CancelAndKill()
    {
        Process? process;
        lock (_liveLock)
        {
            process = _live;
            if (process != null)
            {
                _killed = true;
            }
        }

        if (process == null)
        {
            return;
        }

        KillTree(process);
        try
        {
            process.WaitForExit(2000);
        }
        catch (Exception ex) when (ex is InvalidOperationException || ex is Win32Exception || ex is ObjectDisposedException)
        {
        }
    }

    /// <summary>The real command: the program itself, hidden, stdin/stdout/stderr redirected, TEMP and TMP inside the run folder.</summary>
    public static ProcessStartInfo DefaultStartInfo(string claudePath, IReadOnlyList<string> arguments, string workingDirectory, string tempPath)
    {
        ProcessStartInfo info = new ProcessStartInfo
        {
            FileName = claudePath,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        foreach (string argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        info.Environment["TEMP"] = tempPath;
        info.Environment["TMP"] = tempPath;
        return info;
    }

    /// <summary>
    /// Runs the agent on the image. Expected problems (missing program, not signed in, failure, time-out, cancel) come back as the result
    /// kind; only an unexpected bug throws.
    /// </summary>
    public Task<AgentRunResult> RunAsync(AgentRunInput input, CancellationToken ct)
    {
        return RunCoreAsync(input, ct, null);
    }

    /// <summary>
    /// <see cref="RunAsync(AgentRunInput, CancellationToken)"/> that also reports every live line of the agent (text, tool calls, tool results,
    /// the final result) to <paramref name="progress"/> while the process runs. A cached answer reports nothing.
    /// </summary>
    public Task<AgentRunResult> RunAsync(AgentRunInput input, CancellationToken ct, IProgress<AgentStreamEvent> progress)
    {
        return RunCoreAsync(input, ct, progress);
    }

    private async Task<AgentRunResult> RunCoreAsync(AgentRunInput input, CancellationToken ct, IProgress<AgentStreamEvent>? progress)
    {
        if (ct.IsCancellationRequested)
        {
            return new AgentRunResult(AgentOutcomeKind.Cancelled);
        }

        if (input.Prompt.Trim().Length > AgentCommandLine.MaxPromptLength)
        {
            return new AgentRunResult(AgentOutcomeKind.Failed)
            {
                Message = "The prompt is longer than " + AgentCommandLine.MaxPromptLength.ToString(System.Globalization.CultureInfo.InvariantCulture) + " characters.",
            };
        }

        string? claude = FindAgent();
        if (claude == null)
        {
            return new AgentRunResult(AgentOutcomeKind.NotInstalled) { Message = (Tool == AgentTool.Codex ? "Codex" : "Claude Code") + " was not found." };
        }

        string? mcp = FindMcpOrFake();
        if (mcp == null)
        {
            return new AgentRunResult(AgentOutcomeKind.Failed)
            {
                Message = McpLocator.ExeName + " or its Skills folder is missing in " + McpLocator.AgentFolder(_applicationFolder),
            };
        }

        long ticks;
        long length;
        IReadOnlyList<string> skills;
        try
        {
            FileInfo image = new FileInfo(input.ImagePath);
            if (!image.Exists)
            {
                return new AgentRunResult(AgentOutcomeKind.Failed) { Message = "Image file not found: " + input.ImagePath };
            }

            ticks = image.LastWriteTimeUtc.Ticks;
            length = image.Length;
            skills = AgentRunFiles.ReadSkillTexts(_env, McpLocator.AgentFolder(_applicationFolder), DisabledSkills, UserSkillsRoot, input.Kind);
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            return new AgentRunResult(AgentOutcomeKind.Failed) { Message = ex.Message };
        }

        string facts = AgentRunFiles.ImageFactsBlock(input);
        string referenceBlock = AgentRunFiles.ReferenceBlock(input.References);
        if (referenceBlock.Length > 0)
        {
            facts = facts.Length > 0 ? facts + "\n\n" + referenceBlock : referenceBlock;
        }

        string memory = AgentRunFiles.MemoryBlock(EasyLabel.Core.Io.AgentMemoryFile.Read(input.MemoryPath));
        string outcome = AgentRunFiles.OutcomeBlock(input.OutcomeDigest);
        string experience = AgentRunFiles.ExperienceTextBlock(input.ExperienceBlock);
        string vocabulary = AgentRunFiles.VocabularyTextBlock(input.VocabularyBlock);
        IReadOnlyList<McpServerEntry> servers = ApprovedServers();
        string key = AgentCache.Key(
            input.ImagePath, ticks, length, input.Prompt, ModelName, input.ClassNames, AgentRunFiles.SkillPackHash(AgentRunFiles.CacheHashTexts(skills, memory, outcome, Options.CacheTag, experience, McpServerRegistry.ApprovedSetHash(servers), input.PendingShapes.Count > 0 ? AgentRunFiles.ShapesHash(input.PendingShapes) : string.Empty, facts, vocabulary)), input.ExistingShapes, input.ModelsFolder);
        string logPath = Path.Combine(_runsRoot, AgentUsageLog.FileName);
        if (!input.BypassCache)
        {
            AgentRunResult? cached = AgentCache.TryLoad(_cacheRoot, key);
            if (cached != null)
            {
                AgentUsageLog.Append(logPath, AgentUsageLog.FormatLine(DateTime.UtcNow, input.Rel, cached, input.Prompt.Length));
                return cached;
            }
        }

        AgentRunFolder folder;
        try
        {
            AgentRunRetention.Cleanup(_runsRoot, DateTime.UtcNow);
            folder = AgentRunFolder.Create(_runsRoot, DateTime.Now, Guid.NewGuid(), input, mcp, AgentRunFiles.BuildSystemPrompt(AgentRunFiles.SystemPromptTexts(skills, memory, outcome, experience, facts, vocabulary)), servers);
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            return new AgentRunResult(AgentOutcomeKind.Failed) { Message = ex.Message };
        }

        bool codex = Tool == AgentTool.Codex && !IsFake;
        AgentRunOptions runOptions = Options.ForKind(input.Kind);
        (AgentRunSpend? spend, AgentRunResult? refused) = await ReserveAsync(RunCeilingUsd, "label", ModelName).ConfigureAwait(false);
        if (refused != null)
        {
            return refused;
        }

        AgentRunResult result;
        try
        {
            string previewNote = string.Empty;
            string? preview = codex && PreviewWriter != null ? PreviewWriter(input.ImagePath, folder.Path, input.ImageWidth, input.ImageHeight, input.ModelsFolder, out previewNote) : null;
            IReadOnlyList<string> arguments = codex
                ? AgentCommandLine.BuildCodexArguments(File.ReadAllText(folder.SystemPromptPath), preview == null ? input.Prompt : input.Prompt + "\n\n" + previewNote, mcp, folder.Path, folder.LastMessagePath, false, runOptions, servers, preview)
                : AgentCommandLine.BuildArguments(input.Prompt, folder.McpConfigPath, folder.SystemPromptPath, runOptions, servers, MaxBudgetFlagSupported ? spend?.Reservation.Usd : null);
            ProcessStartInfo startInfo = CreateStartInfo(claude, arguments, folder.Path, folder.TempPath);
            result = await RunProcessAsync(startInfo, folder, ct, _timeout, codex, progress, spend).ConfigureAwait(false);
        }
        catch
        {
            await SettleOnFailureAsync(spend).ConfigureAwait(false);
            throw;
        }
        // A forced run (cache bypass) replaces the stored answer of the same request.
        AgentCache.Store(_cacheRoot, key, result);

        AgentUsageLog.Append(logPath, AgentUsageLog.FormatLine(DateTime.UtcNow, input.Rel, result, input.Prompt.Length));
        return result;
    }

    /// <summary>
    /// "Suggest prompt": one short run (tools get_image_info and get_crop only, 3 turns, 90 seconds) whose closing message
    /// (<see cref="AgentRunResult.ResultText"/>) holds the prompt list; see <see cref="AgentSuggestions.Parse"/>. Not cached; reserved from the budget (single-call ceiling) and appended to log.csv like a run.
    /// Uses the same live-process slot as <see cref="RunAsync"/>, so <see cref="CancelAndKill"/> stops it too.
    /// </summary>
    public async Task<AgentRunResult> SuggestAsync(AgentRunInput input, string languageCode, CancellationToken ct, IReadOnlyList<string>? shapeTypes = null, IReadOnlyList<string>? labels = null)
    {
        if (ct.IsCancellationRequested)
        {
            return new AgentRunResult(AgentOutcomeKind.Cancelled);
        }

        string? claude = FindAgent();
        if (claude == null)
        {
            return new AgentRunResult(AgentOutcomeKind.NotInstalled) { Message = (Tool == AgentTool.Codex ? "Codex" : "Claude Code") + " was not found." };
        }

        string? mcp = FindMcpOrFake();
        if (mcp == null)
        {
            return new AgentRunResult(AgentOutcomeKind.Failed)
            {
                Message = McpLocator.ExeName + " or its Skills folder is missing in " + McpLocator.AgentFolder(_applicationFolder),
            };
        }

        AgentRunFolder folder;
        try
        {
            if (!File.Exists(input.ImagePath))
            {
                return new AgentRunResult(AgentOutcomeKind.Failed) { Message = "Image file not found: " + input.ImagePath };
            }

            AgentRunRetention.Cleanup(_runsRoot, DateTime.UtcNow);
            string suggestSkill = AgentRunFiles.ReadSuggestSkill(_env, McpLocator.AgentFolder(_applicationFolder));
            string suggestMemory = AgentRunFiles.MemoryBlock(EasyLabel.Core.Io.AgentMemoryFile.Read(input.MemoryPath));
            string suggestSystem = AgentSuggestions.SystemPrompt
                + (suggestSkill.Length > 0 ? "\n\n" + suggestSkill : string.Empty)
                + (suggestMemory.Length > 0 ? "\n\n" + suggestMemory : string.Empty)
                + "\n";
            folder = AgentRunFolder.Create(_runsRoot, DateTime.Now, Guid.NewGuid(), input, mcp, suggestSystem);
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            return new AgentRunResult(AgentOutcomeKind.Failed) { Message = ex.Message };
        }

        string userPrompt = AgentSuggestions.BuildUserPrompt(languageCode, input.ClassNames, shapeTypes, labels, Options.SuggestHint);
        bool codex = Tool == AgentTool.Codex && !IsFake;
        (AgentRunSpend? spend, AgentRunResult? refused) = await ReserveAsync(AgentSpendBudget.SingleCallCeilingUsd, "suggest", ModelName).ConfigureAwait(false);
        if (refused != null)
        {
            return refused;
        }

        AgentRunResult result;
        try
        {
            IReadOnlyList<string> arguments = codex
                ? AgentCommandLine.BuildCodexArguments(File.ReadAllText(folder.SystemPromptPath), userPrompt, mcp, folder.Path, folder.LastMessagePath, true, Options)
                : AgentCommandLine.BuildSuggestArguments(userPrompt, folder.McpConfigPath, folder.SystemPromptPath, Options);
            ProcessStartInfo startInfo = CreateStartInfo(claude, arguments, folder.Path, folder.TempPath);
            result = await RunProcessAsync(startInfo, folder, ct, AgentCommandLine.SuggestTimeout, codex, null, spend).ConfigureAwait(false);
        }
        catch
        {
            await SettleOnFailureAsync(spend).ConfigureAwait(false);
            throw;
        }

        string logPath = Path.Combine(_runsRoot, AgentUsageLog.FileName);
        await Task.Run(() => AgentUsageLog.Append(logPath, AgentUsageLog.FormatLine(DateTime.UtcNow, input.Rel, result, userPrompt.Length))).ConfigureAwait(false);
        return result;
    }

    /// <summary>
    /// "Prompt sub-agent": one short text-only run (no tool, <see cref="AgentCommandLine.SuggestTimeout"/>) whose closing message holds the
    /// rewritten command and its explanation; see <see cref="AgentPromptRefiner.Parse"/>. Not cached; reserved from the budget (single-call ceiling) and appended to log.csv like a run. Shares the live-process slot
    /// with <see cref="RunAsync"/>, so <see cref="CancelAndKill"/> stops it too.
    /// </summary>
    public async Task<AgentRunResult> RefinePromptAsync(AgentRunInput input, string prompt, string languageCode, string? hint, CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
        {
            return new AgentRunResult(AgentOutcomeKind.Cancelled);
        }

        string? claude = FindAgent();
        if (claude == null)
        {
            return new AgentRunResult(AgentOutcomeKind.NotInstalled) { Message = (Tool == AgentTool.Codex ? "Codex" : "Claude Code") + " was not found." };
        }

        string? mcp = FindMcpOrFake();
        if (mcp == null)
        {
            return new AgentRunResult(AgentOutcomeKind.Failed)
            {
                Message = McpLocator.ExeName + " or its Skills folder is missing in " + McpLocator.AgentFolder(_applicationFolder),
            };
        }

        AgentRunFolder folder;
        try
        {
            if (!File.Exists(input.ImagePath))
            {
                return new AgentRunResult(AgentOutcomeKind.Failed) { Message = "Image file not found: " + input.ImagePath };
            }

            AgentRunRetention.Cleanup(_runsRoot, DateTime.UtcNow);
            folder = AgentRunFolder.Create(_runsRoot, DateTime.Now, Guid.NewGuid(), input, mcp, AgentPromptRefiner.SystemPrompt + "\n");
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            return new AgentRunResult(AgentOutcomeKind.Failed) { Message = ex.Message };
        }

        string userPrompt = AgentPromptRefiner.BuildUserPrompt(prompt, languageCode, input.ClassNames, hint);
        bool codex = Tool == AgentTool.Codex && !IsFake;
        (AgentRunSpend? spend, AgentRunResult? refused) = await ReserveAsync(AgentSpendBudget.SingleCallCeilingUsd, "refine", ModelName).ConfigureAwait(false);
        if (refused != null)
        {
            return refused;
        }

        AgentRunResult result;
        try
        {
            IReadOnlyList<string> arguments = codex
                ? AgentCommandLine.BuildCodexArguments(File.ReadAllText(folder.SystemPromptPath), userPrompt, mcp, folder.Path, folder.LastMessagePath, true, Options)
                : AgentCommandLine.BuildSuggestArguments(userPrompt, folder.McpConfigPath, folder.SystemPromptPath, Options);
            ProcessStartInfo startInfo = CreateStartInfo(claude, arguments, folder.Path, folder.TempPath);
            result = await RunProcessAsync(startInfo, folder, ct, AgentCommandLine.SuggestTimeout, codex, null, spend).ConfigureAwait(false);
        }
        catch
        {
            await SettleOnFailureAsync(spend).ConfigureAwait(false);
            throw;
        }

        string logPath = Path.Combine(_runsRoot, AgentUsageLog.FileName);
        await Task.Run(() => AgentUsageLog.Append(logPath, AgentUsageLog.FormatLine(DateTime.UtcNow, input.Rel, result, userPrompt.Length))).ConfigureAwait(false);
        return result;
    }

    private async Task<AgentRunResult> RunProcessAsync(ProcessStartInfo startInfo, AgentRunFolder folder, CancellationToken ct, TimeSpan timeoutSpan, bool codex = false, IProgress<AgentStreamEvent>? progress = null, AgentRunSpend? spend = null)
    {
        using Process process = new Process { StartInfo = startInfo };
        lock (_liveLock)
        {
            _killed = false;
        }

        try
        {
            process.Start();
        }
        catch (Win32Exception ex)
        {
            // Only a program that is not there is "not installed"; a file that exists but cannot be started is a failure.
            // Nothing ran, so nothing is spent: the reservation goes back in full.
            await SettleNothingSpentAsync(spend).ConfigureAwait(false);
            return new AgentRunResult(File.Exists(startInfo.FileName) ? AgentOutcomeKind.Failed : AgentOutcomeKind.NotInstalled) { Message = ex.Message };
        }
        catch (Exception ex) when (ex is InvalidOperationException || ex is IOException)
        {
            await SettleNothingSpentAsync(spend).ConfigureAwait(false);
            return new AgentRunResult(AgentOutcomeKind.Failed) { Message = ex.Message };
        }

        lock (_liveLock)
        {
            _live = process;
        }

        try
        {
            _job?.TryAssign(process);
            AgentStreamTiming timing = new AgentStreamTiming();
            AgentRunResult collected;
            try
            {
                collected = await WaitAndCollectAsync(process, folder, ct, timeoutSpan, codex, progress, timing, spend).ConfigureAwait(false);
            }
            catch
            {
                await SettleOnFailureAsync(spend).ConfigureAwait(false);
                throw;
            }
            finally
            {
                // Names and milliseconds only (never the prompt); written also when the run was cancelled.
                timing.TryWrite(Path.Combine(folder.Path, AgentStreamTiming.FileName));
            }

            return await FinishSpendAsync(collected, spend, codex).ConfigureAwait(false);
        }
        finally
        {
            lock (_liveLock)
            {
                _live = null;
            }
        }
    }

    // The run's cost from the meter (or the whole reservation for codex), settled into the budget and the ledger; the result carries it.
    private static async Task<AgentRunResult> FinishSpendAsync(AgentRunResult result, AgentRunSpend? spend, bool codex)
    {
        if (spend == null)
        {
            return result;
        }

        (double usd, bool estimated) = spend.FinalCost(codex);
        await spend.SettleAsync(usd, estimated).ConfigureAwait(false);
        int turns = result.Turns > 0 ? result.Turns : spend.Meter.Turns;
        return result with { CostUsd = usd, IsCostEstimated = estimated, Turns = turns };
    }

    private static async Task SettleNothingSpentAsync(AgentRunSpend? spend)
    {
        if (spend != null)
        {
            await spend.SettleAsync(0, false).ConfigureAwait(false);
        }
    }

    private async Task<AgentRunResult> WaitAndCollectAsync(Process process, AgentRunFolder folder, CancellationToken ct, TimeSpan timeoutSpan, bool codex, IProgress<AgentStreamEvent>? progress, AgentStreamTiming timing, AgentRunSpend? spend)
    {
        try
        {
            process.StandardInput.Close();
        }
        catch (IOException)
        {
        }

        // Line by line with a bounded buffer: live events go to the progress, image data is dropped, and only result-relevant lines are kept.
        // The budget watchdog: every stdout line feeds the run's meter; at the reservation the tree is killed (once).
        Action<string>? onLine = null;
        if (spend != null)
        {
            onLine = line =>
            {
                if (spend.ObserveLine(line) && !spend.BudgetHit)
                {
                    spend.MarkBudgetHit();
                    KillTree(process);
                }
            };
        }

        Task<string> stdoutTask = ReadStdoutAsync(process.StandardOutput, progress, timing, onLine);
        Task<string> stderrTask = process.StandardError.ReadToEndAsync();
        using CancellationTokenSource timeout = new CancellationTokenSource(timeoutSpan);
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            KillTree(process);
            await WaitAfterKillAsync(process, stdoutTask, stderrTask).ConfigureAwait(false);
            return new AgentRunResult(ct.IsCancellationRequested || _killed ? AgentOutcomeKind.Cancelled : AgentOutcomeKind.TimedOut);
        }

        if (spend != null && spend.BudgetHit)
        {
            // The watchdog stopped the run: let the rest of its output arrive so the cost estimate sees it.
            await WaitAfterKillAsync(process, stdoutTask, stderrTask).ConfigureAwait(false);
            return new AgentRunResult(AgentOutcomeKind.BudgetLimit) { Message = BudgetStoppedMessage };
        }

        if (_killed)
        {
            return new AgentRunResult(AgentOutcomeKind.Cancelled);
        }

        // The process is gone; its pipes close with it. A stray grandchild holding a pipe must not hang the run.
        await Task.WhenAny(Task.WhenAll(stdoutTask, stderrTask), Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false);
        string stdout = stdoutTask.IsCompletedSuccessfully ? stdoutTask.Result : string.Empty;
        string stderr = stderrTask.IsCompletedSuccessfully ? stderrTask.Result : string.Empty;

        AgentRunResult result;
        if (codex)
        {
            string lastMessage = string.Empty;
            try
            {
                lastMessage = File.Exists(folder.LastMessagePath) ? File.ReadAllText(folder.LastMessagePath) : string.Empty;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
            }

            result = AgentOutcomeClassifier.ClassifyCodex(process.ExitCode, stdout, stderr, lastMessage);
        }
        else
        {
            result = AgentOutcomeClassifier.Classify(process.ExitCode, stdout, stderr);
        }

        if (result.Kind != AgentOutcomeKind.Success)
        {
            return result;
        }

        string proposalsJson = string.Empty;
        try
        {
            FileInfo file = new FileInfo(folder.ProposalsPath);
            if (file.Exists)
            {
                if (file.Length > AgentProposals.MaxFileBytes)
                {
                    return new AgentRunResult(AgentOutcomeKind.Failed) { Message = "proposals.json is larger than 8 MB and was not read." };
                }

                proposalsJson = File.ReadAllText(folder.ProposalsPath);
            }
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            return new AgentRunResult(AgentOutcomeKind.Failed) { Message = ex.Message };
        }

        return result with { Proposals = AgentProposals.Parse(proposalsJson) };
    }

    private static async Task<string> ReadStdoutAsync(StreamReader reader, IProgress<AgentStreamEvent>? progress, AgentStreamTiming timing, Action<string>? onLine = null)
    {
        AgentStreamRead read = await AgentStreamReader.ReadAsync(reader, item =>
        {
            AgentStreamEvent observed = timing.Observe(item);
            progress?.Report(observed);
        }, onLine).ConfigureAwait(false);
        return read.Kept;
    }

    private static void KillTree(Process process)
    {
        try
        {
            process.Kill(true);
        }
        catch (Exception ex) when (ex is InvalidOperationException || ex is Win32Exception || ex is NotSupportedException || ex is ObjectDisposedException)
        {
            // Already gone.
        }
    }

    private static async Task WaitAfterKillAsync(Process process, Task<string> stdoutTask, Task<string> stderrTask)
    {
        try
        {
            using CancellationTokenSource limit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException || ex is InvalidOperationException)
        {
        }

        await Task.WhenAny(Task.WhenAll(stdoutTask, stderrTask), Task.Delay(TimeSpan.FromSeconds(2))).ConfigureAwait(false);
    }
}
