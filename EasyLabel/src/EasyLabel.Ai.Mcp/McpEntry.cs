using System.Globalization;

namespace EasyLabel.Ai.Mcp;

/// <summary>
/// Command-line entry that returns an exit code: 0 clean end of input, 2 for a bad command line or run folder.
/// stdout carries protocol messages only; logs go to stderr and to <c>&lt;run&gt;/mcp.log</c>.
/// </summary>
public static class McpEntry
{
    public const int ExitOk = 0;
    public const int ExitBadRun = 2;
    public const string LogFileName = "mcp.log";

    public static int Run(string[] args, TextReader stdin, TextWriter stdout, TextWriter stderr, CancellationToken ct = default)
    {
        string? run = null;
        string? models = null;
        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (arg == "--run" && i + 1 < args.Length) run = args[++i];
            else if (arg == "--models" && i + 1 < args.Length) models = args[++i];
            else
            {
                stderr.WriteLine("Unknown or incomplete argument: " + arg + ". Usage: EasyLabel.Ai.Mcp --run <dir> [--models <dir>]");
                return ExitBadRun;
            }
        }

        if (string.IsNullOrWhiteSpace(run))
        {
            stderr.WriteLine("Missing --run <dir>. Usage: EasyLabel.Ai.Mcp --run <dir> [--models <dir>]");
            return ExitBadRun;
        }

        string runDirectory = Path.GetFullPath(run);
        if (!Directory.Exists(runDirectory))
        {
            stderr.WriteLine("Run folder not found: " + runDirectory);
            return ExitBadRun;
        }

        string requestPath = Path.Combine(runDirectory, RunRequest.FileName);
        if (!File.Exists(requestPath))
        {
            stderr.WriteLine("Run request not found: " + requestPath);
            return ExitBadRun;
        }

        RunRequest request;
        try
        {
            request = RunRequest.Load(requestPath);
        }
        catch (InvalidDataException ex)
        {
            stderr.WriteLine("Run request is invalid: " + ex.Message);
            return ExitBadRun;
        }

        string logPath = Path.Combine(runDirectory, LogFileName);
        void Log(string message)
        {
            string line = DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) + " " + message;
            try
            {
                stderr.WriteLine(line);
            }
            catch (IOException)
            {
            }

            try
            {
                File.AppendAllText(logPath, line + Environment.NewLine);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        Log("EasyLabel.Ai.Mcp started. run=" + runDirectory + " models=" + (models ?? request.ModelsFolder ?? "(none)"));
        using var tools = new EasyAiTools(runDirectory, request, models);
        // The model is still reading the picture while SAM2 loads and embeds it, so detect_points / sam2_segment find it ready.
        tools.WarmUpSam2();
        new McpServer(tools, Log).Run(stdin, stdout, ct);
        return ExitOk;
    }
}
