using System.ComponentModel;
using EasyAI.Desktop.ViewModels;

namespace EasyAI.Desktop.Views;

/// <summary>LabelMe folder to YOLO dialog (Tools menu). It cannot be closed while a conversion runs; closing then asks the run to cancel.</summary>
public partial class ConvertYoloWindow
{
    private readonly ConvertYoloViewModel _viewModel;

    public ConvertYoloWindow(ConvertYoloViewModel viewModel)
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
