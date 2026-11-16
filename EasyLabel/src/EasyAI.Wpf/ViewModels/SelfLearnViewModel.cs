using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EasyAI.Desktop.Infrastructure;
using EasyAI.Desktop.Resources;
using EasyAI.Desktop.Services;

namespace EasyAI.Desktop.ViewModels;

/// <summary>One finished round of the auto loop, as written to report.json by tools/autoloop/autoloop.py.</summary>
public sealed record SelfLearnRoundRow(string Round, string Model, string Synth, string Epochs, string Map50, string Recall, string Precision);

/// <summary>One image of a detect run: what the model found, what was labelled (when a label file exists) and the overlay written by detect_obb.py.</summary>
public sealed record SelfLearnDetectRow(string Name, int Count, string Labelled, string Ms, string OverlayPath);

/// <summary>
/// Train &gt; Self-learn window. Tab 1 watches an auto-loop run folder (RUNNING pid, report.json, autoloop.log) and can ask it to stop (STOP file);
/// tab 2 runs tools/autoloop/detect_obb.py (YOLO-OBB) on a folder of test images and lists the real counts with the overlay of the selected image.
/// It only reads the run folder, so it does not depend on the MCP server process that started the run.
/// </summary>
public sealed partial class SelfLearnViewModel : ObservableObject, IDisposable
{
    private const int LogLines = 8;

    private readonly IDialogService _dialogs;
    private readonly SettingsStore _store;
    private readonly DispatcherTimer _timer;
    private Process? _detectProcess;
    private bool _detectCancelled;

    public SelfLearnViewModel(string? projectRoot, IDialogService dialogs, SettingsStore store)
    {
        _dialogs = dialogs;
        _store = store;
        StateText = string.Format(Strings.SlStateFormat, Strings.SlStateNone);
        if (!string.IsNullOrWhiteSpace(projectRoot))
        {
            // The agent writes its runs beside the labelled folder: <folder>.autoloop\run-*.
            string guess = Path.TrimEndingDirectorySeparator(projectRoot) + ".autoloop";
            if (Directory.Exists(guess))
            {
                RunFolder = guess;
            }
        }

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _timer.Tick += (_, _) => RefreshRun();
        _timer.Start();
        RefreshRun();
    }

    public ObservableCollection<SelfLearnRoundRow> Rounds { get; } = new ObservableCollection<SelfLearnRoundRow>();

    public ObservableCollection<SelfLearnDetectRow> DetectRows { get; } = new ObservableCollection<SelfLearnDetectRow>();

    [ObservableProperty]
    private string _runFolder = string.Empty;

    [ObservableProperty]
    private string _stateText = string.Empty;

    [ObservableProperty]
    private string _targetText = string.Empty;

    [ObservableProperty]
    private string _bestText = string.Empty;

    [ObservableProperty]
    private string _logText = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    private bool _canStop;

    // Synthesis progress read from events.jsonl (gen_image events written by tools/autoloop/synthgen.py).
    [ObservableProperty]
    private string _genText = string.Empty;

    [ObservableProperty]
    private double _genValue;

    [ObservableProperty]
    private double _genMaximum = 1;

    [ObservableProperty]
    private ImageSource? _genPreview;

    [ObservableProperty]
    private bool _hasGen;

    private string _genPreviewPath = string.Empty;

    // ---- detect tab ----
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DetectCommand))]
    private string _weightsPath = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DetectCommand))]
    private string _imagesDirectory = string.Empty;

    [ObservableProperty]
    private string _confidenceText = "0.25";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DetectCommand), nameof(CancelDetectCommand), nameof(BrowseImagesCommand))]
    [NotifyPropertyChangedFor(nameof(IsDetectIdle))]
    private bool _isDetecting;

    [ObservableProperty]
    private double _detectValue;

    [ObservableProperty]
    private double _detectMaximum = 1;

    [ObservableProperty]
    private string _detectProgressText = string.Empty;

    [ObservableProperty]
    private string _detectSummary = string.Empty;

    [ObservableProperty]
    private SelfLearnDetectRow? _selectedRow;

    [ObservableProperty]
    private ImageSource? _previewImage;

    public bool IsDetectIdle => !IsDetecting;

    partial void OnRunFolderChanged(string value)
    {
        RefreshRun();
        string best = Path.Combine(ResolveRun(value) ?? string.Empty, "best.pt");
        if (File.Exists(best))
        {
            WeightsPath = best;
        }
    }

    partial void OnSelectedRowChanged(SelfLearnDetectRow? value)
    {
        PreviewImage = value == null ? null : LoadImage(value.OverlayPath);
    }

    // ================= monitor =================

    /// <summary>A run folder as typed: either the run-... folder itself or the .autoloop folder that holds them (then the newest run).</summary>
    internal static string? ResolveRun(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return null;
        }

        string? latest = Directory.GetDirectories(path, "run-*").OrderBy(d => d, StringComparer.Ordinal).LastOrDefault();
        return latest ?? path;
    }

    [RelayCommand]
    private void RefreshRun()
    {
        string? run = ResolveRun(RunFolder);
        Rounds.Clear();
        if (run == null)
        {
            StateText = string.Format(Strings.SlStateFormat, Strings.SlStateNone);
            TargetText = string.Empty;
            BestText = string.Empty;
            LogText = string.Empty;
            CanStop = false;
            ReadGeneration(null);
            return;
        }

        ReadGeneration(run);
        bool running = IsTrainerRunning(run);
        string report = Path.Combine(run, "report.json");
        string[] tail = File.Exists(Path.Combine(run, "autoloop.log")) ? LogTail(Path.Combine(run, "autoloop.log"), LogLines) : Array.Empty<string>();
        bool stopRequested = File.Exists(Path.Combine(run, "STOP"));
        bool hasReport = ReadReport(report);
        if (!hasReport)
        {
            TargetText = string.Empty;
            BestText = Strings.SlNoReport;
        }

        string state;
        if (running)
        {
            state = stopRequested ? Strings.SlStateStopping : Strings.SlStateRunning;
        }
        else if (tail.Any(l => l.Contains("done.", StringComparison.Ordinal)))
        {
            state = Strings.SlStateFinished;
        }
        else
        {
            state = hasReport ? Strings.SlStateIdle : Strings.SlStateFailed;
        }

        StateText = string.Format(Strings.SlStateFormat, state);
        LogText = string.Join(Environment.NewLine, tail);
        CanStop = running && !stopRequested;
    }

    /// <summary>Latest gen_image event of the run: "train 76/300 · stripes · 19 objects" plus a thumbnail of that image. Hidden when the run has no events.</summary>
    private void ReadGeneration(string? run)
    {
        string events = run == null ? string.Empty : Path.Combine(run, "events.jsonl");
        string? last = null;
        bool done = false;
        if (events.Length > 0 && File.Exists(events))
        {
            try
            {
                using FileStream fs = new FileStream(events, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                fs.Seek(Math.Max(0, fs.Length - 64 * 1024), SeekOrigin.Begin);
                using StreamReader sr = new StreamReader(fs);
                foreach (string line in sr.ReadToEnd().Split('\n'))
                {
                    if (line.Contains("\"gen_image\"", StringComparison.Ordinal))
                    {
                        last = line;
                        done = false;
                    }
                    else if (line.Contains("\"gen_done\"", StringComparison.Ordinal))
                    {
                        done = true;
                    }
                }
            }
            catch (IOException)
            {
            }
        }

        if (last == null)
        {
            HasGen = false;
            GenText = string.Empty;
            GenPreview = null;
            _genPreviewPath = string.Empty;
            return;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(last);
            JsonElement e = doc.RootElement;
            int index = e.TryGetProperty("index", out JsonElement i) && i.TryGetInt32(out int iv) ? iv : 0;
            int total = e.TryGetProperty("total", out JsonElement t) && t.TryGetInt32(out int tv) ? tv : 0;
            int count = e.TryGetProperty("count", out JsonElement c) && c.TryGetInt32(out int cv) ? cv : 0;
            string path = Text(e, "path");
            GenMaximum = Math.Max(1, total);
            GenValue = done ? GenMaximum : index;
            GenText = string.Format(CultureInfo.CurrentCulture, Strings.SlGenFormat, Text(e, "split"), index, total, Text(e, "bg"), count);
            HasGen = true;
            if (!string.Equals(path, _genPreviewPath, StringComparison.Ordinal))
            {
                _genPreviewPath = path;
                GenPreview = LoadImage(path);
            }
        }
        catch (JsonException)
        {
            // a half-written last line: the next tick shows it.
        }
    }

    private bool ReadReport(string path)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(ReadShared(path));
            JsonElement root = doc.RootElement;
            if (root.TryGetProperty("target", out JsonElement target))
            {
                TargetText = string.Format(Strings.SlTargetFormat, Num(target, "map50"), Num(target, "recall"));
            }

            if (root.TryGetProperty("history", out JsonElement history) && history.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement h in history.EnumerateArray())
                {
                    Rounds.Add(new SelfLearnRoundRow(Num(h, "round"), Text(h, "model"), Num(h, "synth"), Num(h, "epochs"), Num(h, "map50"), Num(h, "recall"), Num(h, "precision")));
                }
            }

            if (root.TryGetProperty("best", out JsonElement best) && best.ValueKind == JsonValueKind.Object)
            {
                BestText = string.Format(Strings.SlBestFormat, Num(best, "round"), Num(best, "map50"), Num(best, "recall"));
            }

            return true;
        }
        catch (JsonException)
        {
            // report.json is replaced atomically, but never fail the refresh on a half-read: the next tick shows it.
            return true;
        }
        catch (IOException)
        {
            return true;
        }
    }

    private static string Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : string.Empty;

    private static string Num(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out JsonElement v) || v.ValueKind != JsonValueKind.Number)
        {
            return string.Empty;
        }

        return v.TryGetInt64(out long i) ? i.ToString(CultureInfo.CurrentCulture) : v.GetDouble().ToString("0.###", CultureInfo.CurrentCulture);
    }

    private static string ReadShared(string path)
    {
        using FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using StreamReader sr = new StreamReader(fs);
        return sr.ReadToEnd();
    }

    // Timestamped lines only (HH:mm:ss prefix): the tqdm progress bars have none and would flood the box.
    private static string[] LogTail(string path, int count)
    {
        // A long training run buries the timestamped lines under megabytes of progress bars: widen the window until enough are found.
        long[] windows = { 256L * 1024, 4L * 1024 * 1024, 64L * 1024 * 1024 };
        List<string> list = new List<string>();
        try
        {
            using FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            foreach (long window in windows)
            {
                list.Clear();
                fs.Seek(Math.Max(0, fs.Length - window), SeekOrigin.Begin);
                using StreamReader sr = new StreamReader(fs, System.Text.Encoding.UTF8, true, 4096, true);
                foreach (string raw in sr.ReadToEnd().Split('\n', '\r'))
                {
                    string l = raw.Trim();
                    if (l.Length > 9 && char.IsDigit(l[0]) && l[2] == ':' && l[5] == ':')
                    {
                        list.Add(l.Length > 240 ? l[..240] : l);
                    }
                }

                if (list.Count >= count || fs.Length <= window)
                {
                    break;
                }
            }
        }
        catch (IOException)
        {
        }

        return list.Skip(Math.Max(0, list.Count - count)).ToArray();
    }

    // RUNNING holds the python pid; a leftover file from a crashed run does not count (pids are reused, so the process must be python).
    private static bool IsTrainerRunning(string run)
    {
        string marker = Path.Combine(run, "RUNNING");
        try
        {
            if (!File.Exists(marker) || !int.TryParse(File.ReadAllText(marker).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int pid))
            {
                return false;
            }

            using Process p = Process.GetProcessById(pid);
            return !p.HasExited && p.ProcessName.StartsWith("python", StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }

    [RelayCommand]
    private void BrowseRun()
    {
        string? folder = _dialogs.PickFolder(Strings.SlRunFolderTitle, Directory.Exists(RunFolder) ? RunFolder : null);
        if (folder != null)
        {
            RunFolder = folder;
        }
    }

    [RelayCommand(CanExecute = nameof(CanStop))]
    private void Stop()
    {
        string? run = ResolveRun(RunFolder);
        if (run == null)
        {
            return;
        }

        File.WriteAllText(Path.Combine(run, "STOP"), string.Empty);
        RefreshRun();
    }

    // ================= detect =================

    private bool CanBrowseImages() => !IsDetecting;

    private bool CanDetect() => !IsDetecting && !string.IsNullOrWhiteSpace(WeightsPath) && !string.IsNullOrWhiteSpace(ImagesDirectory);

    private bool CanCancelDetect() => IsDetecting;

    [RelayCommand(CanExecute = nameof(CanBrowseImages))]
    private void BrowseImages()
    {
        string? folder = _dialogs.PickFolder(Strings.SlImagesTitle, Directory.Exists(ImagesDirectory) ? ImagesDirectory : null);
        if (folder != null)
        {
            ImagesDirectory = folder;
        }
    }

    internal static string? FindScript()
    {
        string? env = Environment.GetEnvironmentVariable("EASYAI_DETECT_SCRIPT");
        if (!string.IsNullOrEmpty(env) && File.Exists(env))
        {
            return env;
        }

        for (DirectoryInfo? d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            string candidate = Path.Combine(d.FullName, "tools", "autoloop", "detect_obb.py");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    [RelayCommand(CanExecute = nameof(CanDetect))]
    private async Task DetectAsync()
    {
        if (!File.Exists(WeightsPath))
        {
            DetectSummary = Strings.SlNeedWeights;
            return;
        }

        if (!Directory.Exists(ImagesDirectory))
        {
            DetectSummary = Strings.SlNeedImages;
            return;
        }

        string? script = FindScript();
        if (script == null)
        {
            DetectSummary = Strings.SlNoScript;
            return;
        }

        if (!double.TryParse(ConfidenceText.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double conf) || conf <= 0 || conf >= 1)
        {
            conf = 0.25;
        }

        string outDir = Path.Combine(Path.GetTempPath(), "EasyAI", "detect-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(outDir);
        ProcessStartInfo psi = new ProcessStartInfo(Environment.GetEnvironmentVariable("EASYAI_PYTHON") ?? "python")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(script)!,
        };
        foreach (string arg in new[] { "-I", "-u", script, "--weights", WeightsPath, "--dir", ImagesDirectory, "--out", outDir, "--conf", conf.ToString("0.###", CultureInfo.InvariantCulture) })
        {
            psi.ArgumentList.Add(arg);
        }

        psi.Environment["PYTHONIOENCODING"] = "utf-8";

        IsDetecting = true;
        _detectCancelled = false;
        DetectRows.Clear();
        SelectedRow = null;
        DetectSummary = string.Empty;
        DetectValue = 0;
        DetectMaximum = 1;
        DetectProgressText = string.Empty;
        Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
        string? error = null;
        string? summary = null;
        StringBuilder stderr = new StringBuilder();
        try
        {
            await Task.Run(() =>
            {
                using Process p = Process.Start(psi) ?? throw new InvalidOperationException("python did not start");
                _detectProcess = p;
                p.ErrorDataReceived += (_, e) =>
                {
                    if (e.Data != null && stderr.Length < 4000)
                    {
                        stderr.AppendLine(e.Data);
                    }
                };
                p.BeginErrorReadLine();
                string? line;
                int total = 1;
                while ((line = p.StandardOutput.ReadLine()) != null)
                {
                    if (!line.StartsWith('{'))
                    {
                        continue;
                    }

                    using JsonDocument doc = JsonDocument.Parse(line);
                    JsonElement e = doc.RootElement;
                    switch (Text(e, "type"))
                    {
                        case "start":
                            total = Math.Max(1, e.GetProperty("total").GetInt32());
                            dispatcher.Invoke(() => DetectMaximum = total);
                            break;
                        case "image":
                            int index = e.GetProperty("index").GetInt32();
                            int count = e.GetProperty("count").GetInt32();
                            string labelled = e.TryGetProperty("gt", out JsonElement gt) && gt.ValueKind == JsonValueKind.Number ? gt.GetInt32().ToString(CultureInfo.CurrentCulture) : "-";
                            SelfLearnDetectRow row = new SelfLearnDetectRow(Path.GetFileName(Text(e, "image")), count, labelled,
                                e.GetProperty("ms").GetDouble().ToString("0", CultureInfo.CurrentCulture), Text(e, "overlay"));
                            dispatcher.Invoke(() =>
                            {
                                DetectRows.Add(row);
                                DetectValue = index + 1;
                                DetectProgressText = string.Format(Strings.SlDetectProgressFormat, index + 1, total);
                                if (SelectedRow == null)
                                {
                                    SelectedRow = row;
                                }
                            });
                            break;
                        case "done":
                            summary = string.Format(Strings.SlDetectSummaryFormat, e.GetProperty("total").GetInt32(), e.GetProperty("detections").GetInt32(),
                                e.GetProperty("ms_mean").GetDouble().ToString("0", CultureInfo.CurrentCulture));
                            if (e.TryGetProperty("gt", out JsonElement g) && g.ValueKind == JsonValueKind.Number)
                            {
                                summary += string.Format(Strings.SlDetectGtFormat, g.GetInt32(), e.GetProperty("images_exact").GetInt32());
                            }

                            break;
                        case "error":
                            error = Text(e, "message");
                            break;
                    }
                }

                p.WaitForExit();
                if (error == null && summary == null && !_detectCancelled)
                {
                    error = stderr.Length > 0 ? stderr.ToString().Trim() : "exit code " + p.ExitCode.ToString(CultureInfo.InvariantCulture);
                }
            });
        }
        catch (Exception ex)
        {
            _store.Log("Self-learn detect failed: " + ex);
            error = ex.Message;
        }
        finally
        {
            _detectProcess = null;
            IsDetecting = false;
        }

        DetectSummary = _detectCancelled ? Strings.ExpCancelled : error != null ? string.Format(Strings.SlDetectFailedFormat, error) : summary ?? string.Empty;
        DetectProgressText = string.Empty;
    }

    [RelayCommand(CanExecute = nameof(CanCancelDetect))]
    private void CancelDetect()
    {
        _detectCancelled = true;
        KillDetect();
    }

    private void KillDetect()
    {
        try
        {
            _detectProcess?.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
    }

    private static ImageSource? LoadImage(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        BitmapImage image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad; // read fully now, so the overlay file is never locked
        image.UriSource = new Uri(path);
        image.EndInit();
        image.Freeze();
        return image;
    }

    /// <summary>Window close: stop polling and end a running detection.</summary>
    public void Dispose()
    {
        _timer.Stop();
        _detectCancelled = true;
        KillDetect();
    }
}
