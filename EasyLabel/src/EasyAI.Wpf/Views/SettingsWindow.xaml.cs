using System.Windows;
using System.Windows.Threading;
using EasyAI.Desktop.ViewModels;

namespace EasyAI.Desktop.Views;

/// <summary>Settings dialog: every page in one scrolling list with a category list and a search box; only rows that change real behaviour are enabled.</summary>
public partial class SettingsWindow
{
    private readonly SettingsViewModel _viewModel;

    public SettingsWindow(SettingsViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        _viewModel.ScrollToRowRequested -= OnScrollToRowRequested;
        _viewModel.ScrollToRowRequested += OnScrollToRowRequested;
    }

    private void OnScrollToRowRequested(object? sender, SettingRowViewModel row)
    {
        // Row containers are generated in a layout pass; scroll once it ran.
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (RowsList.ItemContainerGenerator.ContainerFromItem(row) is FrameworkElement container)
            {
                double offset = container.TranslatePoint(new Point(0, 0), RowsList).Y;
                RowsScroll.ScrollToVerticalOffset(Math.Max(0, offset));
            }
        }));
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _viewModel.ScrollToRowRequested -= OnScrollToRowRequested;
        _viewModel.Detach();
    }
}
