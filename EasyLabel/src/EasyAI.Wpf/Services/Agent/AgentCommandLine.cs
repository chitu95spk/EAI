namespace EasyAI.Desktop.Services.Agent;

/// <summary>
/// The command line of one agent run. The flag set is the one proven in the S3 spike (it cut the cost per run about 5 times: no built-in
/// tools, no slash commands, no settings, no session file, own system prompt). Values go through <c>ProcessStartInfo.ArgumentList</c>, so a
/// path with spaces or a prompt with quotes and new lines needs no manual quoting.
/// </summary>
public static class AgentCommandLine
{
    public const string Model = "sonnet";
    public const int MaxTurns = 14;

    /// <summary>The only tools the agent may call (the MCP tools of the easyai server).</summary>
    public const string AllowedTools =
        "mcp__easyai__get_image_info,mcp__easyai__get_crop,mcp__easyai__get_reference,mcp__easyai__list_shapes,mcp__easyai__sam2_segment,mcp__easyai__detect_points,mcp__easyai__pick_candidates,mcp__easyai__propose_shapes,mcp__easyai__memory_read,mcp__easyai__memory_add,mcp__easyai__grid_crop,mcp__easyai__render_overlay,mcp__easyai__measure,mcp__easyai__propagate_labels,mcp__easyai__capture_frames,mcp__easyai__propagate_to_frames,mcp__easyai__synthesize_samples,mcp__easyai__auto_train_loop";

    /// <summary>The longest prompt (after trimming) a run accepts.</summary>
    public const int MaxPromptLength = 4000;

    /// <summary>The prompt as sent: trimmed, and prefixed with "Task: " when it starts with '-' so it cannot be parsed as a flag.</summary>
    public static string SafePrompt(string? prompt)
    {
        string text = (prompt ?? string.Empty).Trim();
        return text.StartsWith('-') ? "Task: " + text : text;
    }

    /// <summary>The only tools a "Suggest prompt" run may call: it looks at the image and never proposes shapes.</summary>
    public const string SuggestAllowedTools = "mcp__easyai__get_image_info,mcp__easyai__get_crop,mcp__easyai__memory_read";

    public const int SuggestMaxTurns = 3;

    /// <summary>The longest a "Suggest prompt" run may take.</summary>
    public static readonly System.TimeSpan SuggestTimeout = System.TimeSpan.FromSeconds(90);

    /// <summary>The command line of a "Suggest prompt" run (same flag set as a label run, fewer tools and turns).</summary>
    public static IReadOnlyList<string> BuildSuggestArguments(string prompt, string mcpConfigPath, string systemPromptPath, AgentRunOptions? options = null)
    {
        List<string> args = new List<string>
        {
            "-p", SafePrompt(prompt),
            "--mcp-config", mcpConfigPath,
            "--strict-mcp-config",
            "--tools", string.Empty,
            "--allowedTools", SuggestAllowedTools,
            "--system-prompt-file", systemPromptPath,
            "--disable-slash-commands",
            "--setting-sources", string.Empty,
            "--no-session-persistence",
            "--output-format", "json",
            "--max-turns", SuggestMaxTurns.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--model", (options ?? AgentRunOptions.Default).ClaudeModel,
        };
        AddClaudeEffort(args, options);
        return args;
    }

    // The effort is always sent explicitly (no options = the default options, medium); it is left out only when the chosen model does not list it.
    private static void AddClaudeEffort(List<string> args, AgentRunOptions? options)
    {
        string? effort = (options ?? AgentRunOptions.Default).ClaudeEffort;
        if (effort != null)
        {
            args.Add("--effort");
            args.Add(effort);
        }
    }

    /// <summary>Name of the Codex model in a result and in the cache key (the Codex default model is used, so no version is pinned).</summary>
    public const string CodexModel = "codex";

    /// <summary>The MCP tools a Codex run may use (the same set as the Claude run; Codex has no allow list on the command line, so the server is told).</summary>
    public static readonly string[] CodexTools = { "get_image_info", "get_crop", "get_reference", "list_shapes", "sam2_segment", "detect_points", "pick_candidates", "propose_shapes", "memory_read", "memory_add", "grid_crop", "render_overlay", "measure", "propagate_labels", "capture_frames", "propagate_to_frames", "synthesize_samples", "auto_train_loop" };

    public static readonly string[] CodexSuggestTools = { "get_image_info", "get_crop", "memory_read" };

    /// <summary>
    /// The command line of a Codex run (<c>codex exec</c>): read-only sandbox, no user config, no saved session, the easyai MCP server given
    /// with <c>-c</c> overrides, the closing message written to <paramref name="lastMessagePath"/>. Codex has no system-prompt flag, so the
    /// system text and the task are one prompt.
    /// </summary>
    public static IReadOnlyList<string> BuildCodexArguments(string systemText, string prompt, string mcpExePath, string runFolder, string lastMessagePath, bool suggest, AgentRunOptions? options = null, IReadOnlyList<McpServerEntry>? approvedServers = null, string? previewImagePath = null)
    {
        string[] tools = suggest ? CodexSuggestTools : CodexTools;
        string toolList = "[" + string.Join(",", tools.Select(TomlString)) + "]";
        string full = "Use only the easyai MCP tools (never shell commands or files).\n\n" + systemText.Trim() + "\n\nTask:\n" + SafePrompt(prompt);
        List<string> args = new List<string>
        {
            "exec",
            "--skip-git-repo-check",
            "--ephemeral",
            "--ignore-user-config",
            "--ignore-rules",
            "-s", "read-only",
            "--color", "never",
            "-C", runFolder,
        };
        if (!string.IsNullOrEmpty(previewImagePath))
        {
            // The preview is the first thing the model sees: no get_crop turn is needed just to look at the image.
            args.Add("-i");
            args.Add(previewImagePath!);
        }

        args.AddRange(new[]
        {
            "-c", "mcp_servers.easyai.command=" + TomlString(mcpExePath),
            "-c", "mcp_servers.easyai.args=[" + TomlString("--run") + "," + TomlString(runFolder) + "]",
            "-c", "mcp_servers.easyai.enabled_tools=" + toolList,
            "-c", "mcp_servers.easyai.default_tools_approval_mode=" + TomlString("approve"),
            "-c", "approval_policy=" + TomlString("never"),
            "-o", lastMessagePath,
        });
        foreach (McpServerEntry server in McpServerRegistry.OnlyApproved(approvedServers))
        {
            string prefix = "mcp_servers." + server.Name + ".";
            args.Add("-c");
            args.Add(prefix + "command=" + TomlString(server.Command));
            args.Add("-c");
            args.Add(prefix + "args=[" + string.Join(",", AgentRunFiles.ReplaceRun(server.Args, runFolder).Select(TomlString)) + "]");
            args.Add("-c");
            args.Add(prefix + "enabled_tools=[" + string.Join(",", server.Tools.Select(TomlString)) + "]");
            args.Add("-c");
            args.Add(prefix + "default_tools_approval_mode=" + TomlString("approve"));
        }

        string? model = options?.CodexModel;
        if (model != null)
        {
            args.Add("-m");
            args.Add(model);
        }

        string? effort = options?.CodexEffort;
        if (effort != null)
        {
            args.Add("-c");
            args.Add("model_reasoning_effort=" + TomlString(effort));
        }

        args.Add(full);
        return args;
    }

    /// <summary>A TOML basic string: quotes and back slashes escaped.</summary>
    public static string TomlString(string value)
    {
        return "\"" + (value ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }

    /// <summary>
    /// The allow list of a label run: the built-in tools plus, for each approved external server, every tool of its list as
    /// <c>mcp__&lt;server&gt;__&lt;tool&gt;</c> one by one (never a wildcard). Without approved servers it is exactly <see cref="AllowedTools"/>.
    /// </summary>
    public static string BuildAllowedTools(IReadOnlyList<McpServerEntry>? approvedServers)
    {
        List<string> tools = new List<string> { AllowedTools };
        foreach (McpServerEntry server in McpServerRegistry.OnlyApproved(approvedServers))
        {
            foreach (string tool in server.Tools)
            {
                tools.Add("mcp__" + server.Name + "__" + tool);
            }
        }

        return string.Join(",", tools);
    }

    /// <param name="maxBudgetUsd">When set (and above 0), passed as <c>--max-budget-usd</c>; null = no flag (the watchdog alone bounds the run).</param>
    public static IReadOnlyList<string> BuildArguments(string prompt, string mcpConfigPath, string systemPromptPath, AgentRunOptions? options = null, IReadOnlyList<McpServerEntry>? approvedServers = null, double? maxBudgetUsd = null)
    {
        AgentRunOptions chosen = options ?? AgentRunOptions.Default;
        List<string> args = new List<string>
        {
            "-p", SafePrompt(prompt),
            "--mcp-config", mcpConfigPath,
            "--strict-mcp-config",
            "--tools", string.Empty,
            "--allowedTools", BuildAllowedTools(approvedServers),
            "--system-prompt-file", systemPromptPath,
            "--disable-slash-commands",
            "--setting-sources", string.Empty,
            "--no-session-persistence",
            "--output-format", "stream-json", "--verbose",
            "--max-turns", chosen.MaxTurns.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--model", chosen.ClaudeModel,
        };
        if (maxBudgetUsd.HasValue && !double.IsNaN(maxBudgetUsd.Value) && !double.IsInfinity(maxBudgetUsd.Value) && maxBudgetUsd.Value > 0)
        {
            args.Add("--max-budget-usd");
            args.Add(maxBudgetUsd.Value.ToString("0.0000", System.Globalization.CultureInfo.InvariantCulture));
        }

        AddClaudeEffort(args, chosen);
        return args;
    }
}
