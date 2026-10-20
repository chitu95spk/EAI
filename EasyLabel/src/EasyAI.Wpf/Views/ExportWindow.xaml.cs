using System.ComponentModel;
using EasyAI.Desktop.ViewModels;

namespace EasyAI.Desktop.Views;

/// <summary>Export dialog (File &gt; Export). It cannot be closed while an export runs; closing then asks the run to cancel.</summary>
public partial class ExportWindow
{
    private readonly ExportViewModel _viewModel;

    public ExportWindow(ExportViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_viewModel.TryClose())
        {
            e.Cancel = true;
        }

        base.OnClosing(e);
    }
}
