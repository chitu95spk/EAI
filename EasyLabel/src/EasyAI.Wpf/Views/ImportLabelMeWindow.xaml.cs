using System.ComponentModel;
using EasyAI.Desktop.ViewModels;

namespace EasyAI.Desktop.Views;

/// <summary>Import LabelMe JSON dialog (File &gt; Import LabelMe JSON). It cannot be closed while a scan or import runs; closing then asks the run to cancel.</summary>
public partial class ImportLabelMeWindow
{
    private readonly ImportLabelMeViewModel _viewModel;

    public ImportLabelMeWindow(ImportLabelMeViewModel viewModel)
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
