using System.Globalization;
using System.Text.Json;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EasyAI.Desktop.Resources;
using EasyAI.Desktop.Services;
using InfoBarSeverity = Wpf.Ui.Controls.InfoBarSeverity;

namespace EasyAI.Desktop.ViewModels;

/// <summary>
/// The CounterAI (FastNet point counter) kind of the Train tab: train / fine-tune the weights from the <c>*_points.csv</c> files of the
/// project and convert them to OpenVINO. The heavy work runs in <see cref="CounterAiRunner"/> (python subprocess); progress and log use the
/// tab's own progress bar and the data log.
/// </summary>
public sealed partial class TrainStepViewModel
{
    private readonly CounterAiRunner _counterAiRunner = new CounterAiRunner();

    /// <summary>The FastNet trainer is chosen (not persisted).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSegmentKind))]
    [NotifyPropertyChangedFor(nameof(ShowSegmentOptions))]
    private bool _isCounterAiKind;

    /// <summary>Training iterations of the FastNet run.</summary>
    [ObservableProperty]
    private string _counterAiItersText = "1200";

    /// <summary>Device passed to the service: auto, cpu, cuda or xpu.</summary>
    [ObservableProperty]
    private string _counterAiDevice = "auto";

    /// <summary>Convert to OpenVINO in FP32 instead of FP16.</summary>
    [ObservableProperty]
    private bool _counterAiFp32;

    /// <summary>Last status line of the FastNet run (check result, errors, converted file).</summary>
    [ObservableProperty]
    private string _counterAiStatus = string.Empty;

    /// <summary>OpenVINO xml written by the last Convert (empty before).</summary>
    [ObservableProperty]
    private string _counterAiConvertedXml = string.Empty;

    /// <summary>The segmentation options (epochs, early stop, input size) are hidden while the FastNet kind is chosen.</summary>
    public bool ShowSegmentOptions => !IsCounterAiKind;

    /// <summary>Weights file shared with the Label step (FastNet source) and the Inspect step.</summary>
    public string CounterAiWeights => _host.CounterAiBar.ResolvedWeights;

    partial void OnIsCounterAiKindChanged(bool value)
    {
        RefreshState();
        ConvertCounterAiCommand.NotifyCanExecuteChanged();
    }

    partial void OnCounterAiConvertedXmlChanged(string value)
    {
        UseCounterAiConvertedCommand.NotifyCanExecuteChanged();
    }

    private string? CounterAiRoot() => _host.Session?.ProjectRoot;

    /// <summary>Block text of the FastNet run; null when it can start.</summary>
    private string? CounterAiBlockText()
    {
        if (!_host.HasSession) return Strings.ByName("TrainBlockNoSession");
        if (_host.IsBusy || IsRunning) return null;
        string? root = CounterAiRoot();
        if (string.IsNullOrEmpty(root) || !HasPointsCsv(root)) return Strings.TrainCounterAiNoPoints;
        return null;
    }

    private static bool HasPointsCsv(string root)
    {
        try
        {
            return Directory.EnumerateFiles(root, "*_points.csv", SearchOption.AllDirectories).Any();
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            return false;
        }
    }

    private bool CanStartCounterAi() => _host.HasSession && !_host.IsBusy && !IsRunning && CounterAiBlockText() == null;

    private async Task StartCounterAiAsync()
    {
        string? root = CounterAiRoot();
        if (root == null || !CanStartCounterAi()) return;
        string weights = CounterAiWeights;
        if (string.IsNullOrWhiteSpace(weights)) return;
        if (!int.TryParse(CounterAiItersText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int iters) || iters < 10) iters = 1200;
        string? dir = Path.GetDirectoryName(weights);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        List<string> args = new List<string>
        {
            "train", "--images", root, "--labels", root, "--out", weights,
            "--iters", iters.ToString(CultureInfo.InvariantCulture), "--device", CounterAiDevice,
        };
        // Continue from the existing weights (fine-tune) instead of starting from scratch.
        if (File.Exists(weights))
        {
            args.Add("--init");
            args.Add(weights);
        }

        await RunCounterAiAsync(args, true);
    }

    /// <summary>The CounterAI panel is the one to use: a CounterAI project, or the Count tool is in use (no Advanced mode needed).</summary>
    internal bool ShouldUseCounterAi => _host.Canvas.IsStampKind || _host.Canvas.IsCountTool;

    /// <summary>
    /// Selects the CounterAI trainer when <see cref="ShouldUseCounterAi"/> (and nothing runs) and fills the Weights box with the project's
    /// default weights when that file exists; the user can still edit or browse.
    /// </summary>
    internal void AutoSelectCounterAi()
    {
        if (!_host.HasSession) return;
        _host.CounterAiBar.RefreshDefaultWeights();
        OnPropertyChanged(nameof(CounterAiWeights));
        if (IsRunning || !ShouldUseCounterAi || IsCounterAiKind) return;
        IsUltraKind = false;
        IsClassifyKind = false;
        IsCounterAiKind = true;
    }

    private void OnCounterAiBarChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CounterAiBarViewModel.WeightsPath))
        {
            OnPropertyChanged(nameof(CounterAiWeights));
            ConvertCounterAiCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>Convert needs the CounterAI trainer, no run and an existing .pt weights file (an .xml is already converted).</summary>
    internal bool CanConvertCounterAi()
    {
        if (!_host.HasSession || IsRunning || !IsCounterAiKind) return false;
        string w = CounterAiWeights;
        return !string.IsNullOrWhiteSpace(w)
            && string.Equals(Path.GetExtension(w), ".pt", StringComparison.OrdinalIgnoreCase)
            && File.Exists(w);
    }

    /// <summary>Convert the FastNet .pt weights to OpenVINO next to them: openvino\&lt;name&gt;_fp16.xml (or _fp32).</summary>
    [RelayCommand(CanExecute = nameof(CanConvertCounterAi))]
    private async Task ConvertCounterAiAsync()
    {
        string weights = CounterAiWeights;
        if (!File.Exists(weights))
        {
            CounterAiStatus = Strings.CaNeedWeights;
            return;
        }

        if (string.Equals(Path.GetExtension(weights), ".xml", StringComparison.OrdinalIgnoreCase))
        {
            CounterAiStatus = Strings.CaConvertNeedPt;
            return;
        }

        CounterAiConvertedXml = string.Empty;
        string outXml = Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(weights)) ?? string.Empty,
            "openvino",
            Path.GetFileNameWithoutExtension(weights) + (CounterAiFp32 ? "_fp32" : "_fp16") + ".xml");
        List<string> args = new List<string> { "convert", "--weights", weights, "--out", outXml };
        if (CounterAiFp32) args.Add("--fp32");
        await RunCounterAiAsync(args, false);
    }

    private bool CanUseCounterAiConverted() => IsIdle && File.Exists(CounterAiConvertedXml);

    [RelayCommand(CanExecute = nameof(CanUseCounterAiConverted))]
    private void UseCounterAiConverted()
    {
        _host.CounterAiBar.WeightsPath = CounterAiConvertedXml;
        OnPropertyChanged(nameof(CounterAiWeights));
        CounterAiStatus = Strings.CaUsingConverted;
    }

    private async Task RunCounterAiAsync(IEnumerable<string> args, bool isTrain)
    {
        _stopRequested = false;
        IsRunning = true;
        ProgressValue = 0;
        ProgressText = Strings.CaRunning;
        CounterAiStatus = string.Empty;
        RefreshState();
        Dispatcher ui = _dispatcher;
        try
        {
            CounterAiRunResult r = await _counterAiRunner.RunAsync(args, ev => ui.Invoke(() => OnCounterAiEvent(ev)));
            if (r.Cancelled)
            {
                ProgressText = Strings.CaStopped;
            }
            else if (r.ExitCode != 0)
            {
                string line = LastLine(r.StdErr);
                CounterAiStatus = Strings.CaFailed + " " + line;
                _host.LogData("CounterAI failed: " + line);
                _host.ShowDataNotice(Strings.TrainFailedTitle, CounterAiStatus, InfoBarSeverity.Error);
            }
            else if (isTrain)
            {
                ProgressValue = 100;
                ProgressText = Strings.CaDone;
                _host.ShowDataNotice(Strings.TrainNoticeTitle, Strings.CaDone + " " + CounterAiWeights, InfoBarSeverity.Success);
            }
        }
        finally
        {
            IsRunning = false;
            _host.CounterAiBar.RefreshDefaultWeights();
            RefreshState();
            ConvertCounterAiCommand.NotifyCanExecuteChanged();
            UseCounterAiConvertedCommand.NotifyCanExecuteChanged();
        }
    }

    internal static string LastLine(string s)
    {
        string t = (s ?? string.Empty).Trim();
        int i = t.LastIndexOf('\n');
        return i >= 0 ? t.Substring(i + 1).Trim() : t;
    }

    /// <summary>Applies one JSON event of counterai_service.py on the UI thread (progress, log, done, error). Internal for tests.</summary>
    internal void OnCounterAiEvent(JsonElement ev)
    {
        string kind = ev.TryGetProperty("event", out JsonElement k) ? k.GetString() ?? string.Empty : string.Empty;
        switch (kind)
        {
            case "progress":
                double total = Math.Max(1, ev.GetProperty("total").GetDouble());
                double it = ev.GetProperty("it").GetDouble();
                ProgressValue = Math.Min(100, 100.0 * it / total);
                ProgressText = string.Format(CultureInfo.InvariantCulture, "{0}/{1}  loss {2:0.####}", (int)it, (int)total, ev.GetProperty("loss").GetDouble());
                break;
            case "log":
                _host.LogData("CounterAI: " + (ev.GetProperty("text").GetString() ?? string.Empty));
                break;
            case "done":
                if (ev.TryGetProperty("xml", out JsonElement xml)) CounterAiConvertedXml = xml.GetString() ?? string.Empty;
                if (!string.IsNullOrEmpty(CounterAiConvertedXml)) CounterAiStatus = CounterAiConvertedXml;
                break;
            case "error":
                CounterAiStatus = Strings.CaFailed + " " + (ev.TryGetProperty("message", out JsonElement m) ? m.GetString() : string.Empty);
                break;
        }
    }

    [RelayCommand(CanExecute = nameof(CanSetKind))]
    private async Task CheckCounterAiAsync()
    {
        string? root = CounterAiRoot();
        if (root == null)
        {
            CounterAiStatus = Strings.CaNeedData;
            return;
        }

        await RunCounterAiAsync(new[] { "check", "--images", root, "--labels", root }, false);
    }
}
