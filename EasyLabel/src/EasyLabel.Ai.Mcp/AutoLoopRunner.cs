using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using Newtonsoft.Json.Linq;

namespace EasyLabel.Ai.Mcp;

/// <summary>Starts / inspects / stops tools/autoloop/autoloop.py (synthetic data -> YOLO-OBB train -> harness eval, repeated until the target).
/// shortcut: python + script are located by env EASYAI_AUTOLOOP_SCRIPT / EASYAI_PYTHON or by walking up from the app folder; upgrade to a bundled runtime when the app is shipped.</summary>
/// <summary>Settings passed to autoloop.py on start. Defaults match the script defaults.</summary>
internal sealed record AutoLoopOptions(
    string? Model = null,
    int StartRung = 0,
    int Cycles = 1,
    int MaxRoundsPerCycle = 0,
    double TargetMap50 = 0.95,
    double TargetRecall = 0.98,
    double TargetStep = 0.0);

internal static class AutoLoopRunner
{
    private static readonly System.Text.RegularExpressions.Regex ModelName = new(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,63}\.pt$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    public static string Call(string referencePath, string action, AutoLoopOptions? options = null)
    {
        string? folder = Path.GetDirectoryName(referencePath);
        if (folder is null || !Directory.Exists(folder)) throw new ArgumentException("The reference folder does not exist.");
        // Output sits beside the labelled folder, never inside it (the script globs *.json in the labelled folder).
        string outDir = Path.Combine(Path.GetDirectoryName(folder) ?? folder, Path.GetFileName(folder) + ".autoloop");

        switch (action)
        {
            case "start": return Start(folder, outDir, options ?? new AutoLoopOptions());
            case "status": return Status(outDir);
            case "stop": return Stop(outDir);
            default: throw new ArgumentException("action must be start, status or stop.");
        }
    }

    private static string? LatestRun(string outDir) =>
        Directory.Exists(outDir) ? Directory.GetDirectories(outDir, "run-*").OrderBy(d => d, StringComparer.Ordinal).LastOrDefault() : null;

    private static string Start(string folder, string outDir, AutoLoopOptions options)
    {
        Validate(options);
        string? running = LatestRun(outDir);
        if (running is not null && IsRunning(running)) return Json("already_running", running);

        string script = FindScript();
        string python = Environment.GetEnvironmentVariable("EASYAI_PYTHON") ?? "python";
        // The run folder is created here so autoloop.log (stdout+stderr, including start-up errors) lives in it from the first line.
        // The trainer is started detached (own log, no inherited pipes, breakaway from the agent job) so it outlives this MCP call.
        string run = Path.Combine(outDir, "run-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(run);
        int pid = DetachedProcess.Start(python, BuildArguments(script, folder, outDir, run, options),
            Path.GetDirectoryName(script)!, Path.Combine(run, "autoloop.log"), out bool brokeAway);
        return new JObject
        {
            ["state"] = "started", ["folder"] = outDir, ["runFolder"] = run, ["log"] = Path.Combine(run, "autoloop.log"), ["pid"] = pid, ["brokeAwayFromJob"] = brokeAway,
            ["settings"] = new JObject
            {
                ["model"] = options.Model is null ? null : (JToken)options.Model, ["startRung"] = options.StartRung, ["cycles"] = options.Cycles,
                ["maxRoundsPerCycle"] = options.MaxRoundsPerCycle, ["targetMap50"] = options.TargetMap50, ["targetRecall"] = options.TargetRecall, ["targetStep"] = options.TargetStep,
            },
        }.ToString(Newtonsoft.Json.Formatting.None);
    }

    internal static string[] BuildArguments(string script, string folder, string outDir, string run, AutoLoopOptions o)
    {
        var args = new List<string> { "-I", "-u", script, "--dir", folder, "--out", outDir, "--run", run };
        if (o.Model is not null) args.AddRange(new[] { "--model", o.Model });
        args.AddRange(new[]
        {
            "--start-rung", o.StartRung.ToString(CultureInfo.InvariantCulture),
            "--cycles", o.Cycles.ToString(CultureInfo.InvariantCulture),
            "--max-rounds", o.MaxRoundsPerCycle.ToString(CultureInfo.InvariantCulture),
            "--target-map50", o.TargetMap50.ToString("0.###", CultureInfo.InvariantCulture),
            "--target-recall", o.TargetRecall.ToString("0.###", CultureInfo.InvariantCulture),
            "--target-step", o.TargetStep.ToString("0.###", CultureInfo.InvariantCulture),
        });
        return args.ToArray();
    }

    private static void Validate(AutoLoopOptions o)
    {
        if (o.Model is not null && !ModelName.IsMatch(o.Model)) throw new ArgumentException("model must be a weights file name such as yolo11s-obb.pt.");
        if (o.StartRung < 0 || o.StartRung > 10) throw new ArgumentException("startRung must be 0..10.");
        if (o.Cycles < 1 || o.Cycles > 10) throw new ArgumentException("cycles must be 1..10.");
        if (o.MaxRoundsPerCycle < 0 || o.MaxRoundsPerCycle > 20) throw new ArgumentException("maxRoundsPerCycle must be 0..20.");
        if (o.TargetMap50 < 0 || o.TargetMap50 > 1 || o.TargetRecall < 0 || o.TargetRecall > 1) throw new ArgumentException("targetMap50 and targetRecall must be 0..1.");
        if (o.TargetStep < 0 || o.TargetStep > 0.1) throw new ArgumentException("targetStep must be 0..0.1.");
    }

    private static string Status(string outDir)
    {
        string? run = LatestRun(outDir);
        if (run is null) return Json("none", outDir);
        string report = Path.Combine(run, "report.json");
        bool running = IsRunning(run);
        var result = new JObject { ["state"] = running ? "running" : "idle", ["runFolder"] = run };
        if (File.Exists(report))
        {
            // The trainer rewrites report.json after every round; a read can still hit a half-written file, so never let that fail the whole status call.
            try { result["report"] = JObject.Parse(ReadShared(report)); }
            catch (Newtonsoft.Json.JsonException) { result["reportNote"] = "report.json was being written; ask again in a few seconds."; }
        }
        // The agent only sees what is returned here: give it the last log lines (progress bars collapsed) so a long first round or a crash is visible.
        string log = Path.Combine(run, "autoloop.log");
        if (File.Exists(log))
        {
            string[] lines = LogTail(log, 6);
            result["logTail"] = new JArray(lines);
            if (!running && !File.Exists(report)) result["note"] = "The trainer is not running and wrote no report: it likely failed - see logTail / log.";
            else if (!running && lines.Any(l => l.Contains("done.", StringComparison.Ordinal))) result["state"] = "finished";
            result["log"] = log;
        }
        return result.ToString(Newtonsoft.Json.Formatting.None);
    }

    private static string ReadShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var sr = new StreamReader(fs);
        return sr.ReadToEnd();
    }

    // Last timestamped lines (HH:mm:ss prefix) of autoloop.log; tqdm progress bars carry no timestamp and would flood the answer.
    private static string[] LogTail(string path, int count)
    {
        var list = new List<string>();
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        const int window = 256 * 1024;
        fs.Seek(Math.Max(0, fs.Length - window), SeekOrigin.Begin);
        using var sr = new StreamReader(fs);
        foreach (string raw in sr.ReadToEnd().Split('\n', '\r'))
        {
            string l = raw.Trim();
            if (l.Length > 9 && char.IsDigit(l[0]) && l[2] == ':' && l[5] == ':') list.Add(l.Length > 240 ? l[..240] : l);
        }
        return list.Skip(Math.Max(0, list.Count - count)).ToArray();
    }

    // RUNNING holds the python pid; a leftover file from a crashed run does not count.
    private static bool IsRunning(string run)
    {
        string marker = Path.Combine(run, "RUNNING");
        if (!File.Exists(marker) || !int.TryParse(File.ReadAllText(marker).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int pid)) return false;
        // A crashed run leaves its pid behind and Windows reuses pids: only a live python process counts as the trainer.
        try { using Process p = Process.GetProcessById(pid); return !p.HasExited && p.ProcessName.StartsWith("python", StringComparison.OrdinalIgnoreCase); }
        catch (ArgumentException) { return false; }
    }

    private static string Stop(string outDir)
    {
        string? run = LatestRun(outDir);
        if (run is null) return Json("none", outDir);
        File.WriteAllText(Path.Combine(run, "STOP"), string.Empty);
        return Json("stop_requested", run);
    }

    private static string FindScript()
    {
        string? env = Environment.GetEnvironmentVariable("EASYAI_AUTOLOOP_SCRIPT");
        if (!string.IsNullOrEmpty(env) && File.Exists(env)) return env;
        for (DirectoryInfo? d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            string candidate = Path.Combine(d.FullName, "tools", "autoloop", "autoloop.py");
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException("autoloop.py not found; set EASYAI_AUTOLOOP_SCRIPT.");
    }

    private static string Json(string state, string path) =>
        new JObject { ["state"] = state, ["folder"] = path }.ToString(Newtonsoft.Json.Formatting.None);
}
