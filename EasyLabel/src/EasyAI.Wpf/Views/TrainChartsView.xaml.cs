using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using EasyAI.Desktop.Resources;
using EasyAI.Desktop.ViewModels;
using ScottPlot;
using WpfColor = System.Windows.Media.Color;

namespace EasyAI.Desktop.Views;

/// <summary>
/// Large central charts of the Train tab: two ScottPlot plots (loss, validation IoU) redrawn from the epoch series at most
/// five times per second, with colours read from the current WPF-UI theme so light and dark both stay readable.
/// </summary>
public partial class TrainChartsView : UserControl
{
    private static readonly TimeSpan RedrawInterval = TimeSpan.FromMilliseconds(200);

    private readonly DispatcherTimer _redrawTimer;
    private TrainStepViewModel? _train;
    private bool _dirty = true;
    private WpfColor _themeText;
    private WpfColor _themeBackground;

    public TrainChartsView()
    {
        InitializeComponent();

        LossPlot.UserInputProcessor.Disable();
        IouPlot.UserInputProcessor.Disable();
        LossPlot.Plot.Axes.Bottom.Label.Text = Strings.ChartEpoch;
        LossPlot.Plot.Axes.Left.Label.Text = Strings.ChartLoss;
        IouPlot.Plot.Axes.Bottom.Label.Text = Strings.ChartEpoch;
        IouPlot.Plot.Axes.Left.Label.Text = Strings.ChartIou;

        _redrawTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = RedrawInterval };
        _redrawTimer.Tick -= OnRedrawTick;
        _redrawTimer.Tick += OnRedrawTick;

        DataContextChanged -= OnDataContextChanged;
        DataContextChanged += OnDataContextChanged;
        IsVisibleChanged -= OnIsVisibleChanged;
        IsVisibleChanged += OnIsVisibleChanged;
        Unloaded -= OnUnloaded;
        Unloaded += OnUnloaded;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_train != null)
        {
            _train.ChartInvalidated -= OnChartInvalidated;
        }

        _train = (DataContext as MainViewModel)?.Train;
        if (_train != null)
        {
            _train.ChartInvalidated -= OnChartInvalidated;
            _train.ChartInvalidated += OnChartInvalidated;
        }

        _dirty = true;
    }

    private void OnChartInvalidated(object? sender, EventArgs e)
    {
        _dirty = true;
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible)
        {
            _dirty = true;
            _redrawTimer.Start();
        }
        else
        {
            _redrawTimer.Stop();
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _redrawTimer.Stop();
    }

    private void OnRedrawTick(object? sender, EventArgs e)
    {
        if (!IsVisible || _train == null)
        {
            return;
        }

        if (!_train.HasChart)
        {
            // After a project change the series is emptied: clear the plots once so the old curves do not stay on screen.
            if (_dirty)
            {
                LossPlot.Plot.Clear();
                IouPlot.Plot.Clear();
                LossPlot.Refresh();
                IouPlot.Refresh();
                _dirty = false;
            }

            return;
        }

        WpfColor text = ThemeColor("TextFillColorPrimaryBrush", System.Windows.Media.Colors.Gray);
        WpfColor background = ThemeColor("ApplicationBackgroundBrush", System.Windows.Media.Colors.Black);
        bool themeChanged = text != _themeText || background != _themeBackground;
        if (!_dirty && !themeChanged)
        {
            return;
        }

        _dirty = false;
        _themeText = text;
        _themeBackground = background;
        Redraw(text, background);
    }

    private void Redraw(WpfColor textColor, WpfColor backgroundColor)
    {
        TrainStepViewModel? train = _train;
        if (train == null)
        {
            return;
        }

        TrainChartData data = TrainStepRules.BuildChart(train.EpochSnapshot());
        double xMax = Math.Max(1, train.ChartTotalEpochs) + 1;

        ScottPlot.Color text = ToPlot(textColor);
        ScottPlot.Color background = ToPlot(backgroundColor);
        ScottPlot.Color grid = new ScottPlot.Color(textColor.R, textColor.G, textColor.B, 48);
        ScottPlot.Color trainLoss = ToPlot(ThemeColor("AccentFillColorDefaultBrush", System.Windows.Media.Colors.SteelBlue));
        ScottPlot.Color validationLoss = ToPlot(ThemeColor("SystemFillColorCautionBrush", System.Windows.Media.Colors.Goldenrod));
        ScottPlot.Color validationIou = ToPlot(ThemeColor("SystemFillColorSuccessBrush", System.Windows.Media.Colors.SeaGreen));

        Plot loss = LossPlot.Plot;
        ApplyTheme(loss, text, background, grid);
        loss.Clear();
        AddSeries(loss, data.TrainLossX, data.TrainLossY, trainLoss, Strings.ChartTrainLoss);
        AddSeries(loss, data.ValidationLossX, data.ValidationLossY, validationLoss, Strings.ChartValidationLoss);
        loss.Axes.SetLimits(0, xMax, 0, TrainStepRules.YMax(data.TrainLossY, data.ValidationLossY));
        LossPlot.Refresh();

        Plot iou = IouPlot.Plot;
        ApplyTheme(iou, text, background, grid);
        iou.Clear();
        AddSeries(iou, data.ValidationIouX, data.ValidationIouY, validationIou, Strings.ChartValidationIou);
        iou.Axes.SetLimits(0, xMax, 0, 1.05);
        IouPlot.Refresh();
    }

    private static void AddSeries(Plot plot, double[] xs, double[] ys, ScottPlot.Color color, string legend)
    {
        if (xs.Length == 0)
        {
            return;
        }

        var series = plot.Add.Scatter(xs, ys);
        series.Color = color;
        series.LineWidth = 2;
        series.MarkerSize = 4;
        series.LegendText = legend;
    }

    private static void ApplyTheme(Plot plot, ScottPlot.Color text, ScottPlot.Color background, ScottPlot.Color grid)
    {
        plot.FigureBackground.Color = background;
        plot.DataBackground.Color = background;
        plot.Axes.Color(text);
        plot.Grid.MajorLineColor = grid;
        plot.Legend.BackgroundColor = background;
        plot.Legend.FontColor = text;
        plot.Legend.OutlineColor = grid;
        plot.ShowLegend();
    }

    private static ScottPlot.Color ToPlot(WpfColor color)
    {
        return new ScottPlot.Color(color.R, color.G, color.B);
    }

    /// <summary>Colour of a theme brush (opaque); the fallback is used when the resource is missing.</summary>
    private static WpfColor ThemeColor(string key, WpfColor fallback)
    {
        if (Application.Current?.TryFindResource(key) is System.Windows.Media.SolidColorBrush brush)
        {
            WpfColor color = brush.Color;
            return WpfColor.FromRgb(color.R, color.G, color.B);
        }

        return fallback;
    }
}
