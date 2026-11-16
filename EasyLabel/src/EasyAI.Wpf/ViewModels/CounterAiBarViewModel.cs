using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EasyAI.Desktop.Resources;
using EasyAI.Desktop.Services;
using EasyLabel.Core.Editing;

namespace EasyAI.Desktop.ViewModels;

/// <summary>
/// CounterAI labelling bar of the main window (shown for CounterAI projects): shape choice (circle / rectangle), stamp size, automatic
/// labelling with the FastNet model and the <c>*_points.csv</c> export used for training. The stamping itself (left click paste, right click
/// delete) is done by the canvas edit session.
/// </summary>
public sealed partial class CounterAiBarViewModel : ObservableObject, IDisposable
{
    private readonly CanvasViewModel _canvas;
    private readonly Func<string?> _projectRoot;
    private CounterAiEngine? _engine;
    private string _engineKey = string.Empty;
    private string _lastWritten = string.Empty;

    public CounterAiBarViewModel(CanvasViewModel canvas, Func<string?> projectRoot)
    {
        _canvas = canvas;
        _projectRoot = projectRoot;
        _canvas.DocumentEdited += OnDocumentEdited;
    }

    [ObservableProperty]
    private bool _isCircle = true;

    [ObservableProperty]
    private string _widthText = "62";

    [ObservableProperty]
    private string _heightText = "62";

    [ObservableProperty]
    private string _weightsPath = string.Empty;

    [ObservableProperty]
    private string _thresholdText = "0.8";

    [ObservableProperty]
    private bool _autoSaveCsv = true;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AutoLabelCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private string _status = string.Empty;

    public bool IsRectangle
    {
        get => !IsCircle;
        set
        {
            if (value) IsCircle = false;
        }
    }

    public string ResolvedWeights
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(WeightsPath)) return WeightsPath;
            return CounterAiPaths.DefaultWeightsPath(_projectRoot());
        }
    }

    private string _autoWeights = string.Empty;

    /// <summary>
    /// Fills <see cref="WeightsPath"/> with the project's default weights (counterai\fastnet.pt) when that file exists. An empty box or a value
    /// this method set earlier (another project) is replaced; a path the user typed or browsed is never touched.
    /// </summary>
    public void RefreshDefaultWeights()
    {
        string def = CounterAiPaths.DefaultWeightsPath(_projectRoot());
        string next = !string.IsNullOrEmpty(def) && File.Exists(def) ? def : string.Empty;
        if (string.IsNullOrWhiteSpace(WeightsPath) || string.Equals(WeightsPath, _autoWeights, StringComparison.OrdinalIgnoreCase))
        {
            _autoWeights = next;
            WeightsPath = next;
        }
    }

    partial void OnIsCircleChanged(bool value)
    {
        OnPropertyChanged(nameof(IsRectangle));
        _canvas.StampCircle = value;
    }

    partial void OnWidthTextChanged(string value)
    {
        if (TryParse(value, out double v)) _canvas.StampWidth = v;
    }

    partial void OnHeightTextChanged(string value)
    {
        if (TryParse(value, out double v)) _canvas.StampHeight = v;
    }

    private static bool TryParse(string text, out double v)
        => double.TryParse((text ?? string.Empty).Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out v) && v >= 1 && v <= 4000;

    /// <summary>Pushes shape and size to the canvas and, for a CounterAI project, selects the Count tool (called when a CounterAI project opens).</summary>
    public void Activate()
    {
        _canvas.StampCircle = IsCircle;
        TryParse(WidthText, out double w);
        TryParse(HeightText, out double h);
        if (w >= 1) _canvas.StampWidth = w;
        if (h >= 1) _canvas.StampHeight = h;
        if (_canvas.IsStampKind) _canvas.Tool = CanvasTool.Count;
    }

    private void OnDocumentEdited(object? sender, EventArgs e)
    {
        if (!(_canvas.IsStampKind || _canvas.IsCountTool) || !AutoSaveCsv) return;
        WriteCsv(false);
    }

    private bool WriteCsv(bool report)
    {
        string? image = _canvas.CurrentImagePath;
        if (image == null) return false;
        IReadOnlyList<(int X, int Y)> pts = _canvas.StampCentres();
        string path = CounterAiPaths.PointsCsvPath(image);
        string signature = path + "|" + string.Join(";", pts.Select(p => p.X + "," + p.Y));
        if (!report && signature == _lastWritten) return true;
        try
        {
            CounterAiLabeler l = new CounterAiLabeler(1);
            l.Load(pts);
            l.WriteCsv(path);
            _lastWritten = signature;
            if (report) Status = string.Format(CultureInfo.CurrentCulture, Strings.CaBarCsvSaved, Path.GetFileName(path), pts.Count);
            return true;
        }
        catch (Exception ex)
        {
            Status = ex.Message;
            return false;
        }
    }

    [RelayCommand]
    private void ExportCsv()
    {
        if (_canvas.CurrentImagePath == null)
        {
            Status = Strings.CaBarNoImage;
            return;
        }

        WriteCsv(true);
    }

    [RelayCommand]
    private void BrowseWeights()
    {
        Microsoft.Win32.OpenFileDialog dlg = new Microsoft.Win32.OpenFileDialog { Filter = "FastNet (*.pt;*.xml)|*.pt;*.xml|*.*|*.*" };
        string? current = ResolvedWeights;
        string? dir = string.IsNullOrEmpty(current) ? null : Path.GetDirectoryName(current);
        if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) dlg.InitialDirectory = dir;
        if (dlg.ShowDialog() == true)
        {
            WeightsPath = dlg.FileName;
            Status = string.Empty;
        }
    }

    [RelayCommand(CanExecute = nameof(CanAutoLabel))]
    private async Task AutoLabelAsync()
    {
        string? image = _canvas.CurrentImagePath;
        if (image == null)
        {
            Status = Strings.CaBarNoImage;
            return;
        }

        string weights = ResolvedWeights;
        if (!File.Exists(weights))
        {
            Status = Strings.CaBarNoWeights;
            return;
        }

        double th = CounterAiPaths.ParseThreshold(ThresholdText);
        IsBusy = true;
        Status = Strings.CaBarRunning;
        try
        {
            string key = weights;
            if (_engine == null || !_engine.IsRunning || _engineKey != key)
            {
                _engine?.Dispose();
                _engine = await CounterAiEngine.StartAsync(weights, "AUTO", th);
                _engineKey = key;
            }

            CounterAiDetection det = await _engine.DetectAsync(image, th);
            // The user may have moved to another image while the model ran: never label the wrong picture.
            if (!string.Equals(_canvas.CurrentImagePath, image, StringComparison.OrdinalIgnoreCase))
            {
                Status = Strings.CaBarImageChanged;
                return;
            }

            int n = _canvas.ReplaceStamps(det.Points);
            Status = string.Format(CultureInfo.CurrentCulture, Strings.CaBarDone, n, det.Milliseconds);
        }
        catch (Exception ex)
        {
            Status = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanAutoLabel() => !IsBusy;

    public void Dispose()
    {
        _canvas.DocumentEdited -= OnDocumentEdited;
        _engine?.Dispose();
        _engine = null;
    }
}
