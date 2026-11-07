using EasyAI.Desktop.ViewModels;

namespace EasyAI.Desktop.Views;

/// <summary>Train &gt; Self-learn window (non-modal): auto-loop monitor and OBB detection on a test folder. Closing it ends polling and any running detection.</summary>
public partial class SelfLearnWindow
{
    private readonly SelfLearnViewModel _viewModel;

    public SelfLearnWindow(SelfLearnViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
    }

    protected override void OnClosed(EventArgs e)
    {
        _viewModel.Dispose();
        base.OnClosed(e);
    }
}
