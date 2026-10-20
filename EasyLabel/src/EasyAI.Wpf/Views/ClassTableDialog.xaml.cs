using System.Windows;
using EasyAI.Desktop.ViewModels;

namespace EasyAI.Desktop.Views;

/// <summary>
/// Class table editor opened from the Class menu. OK validates and saves a changed table (the error stays inline when it is
/// invalid); Cancel closes without saving.
/// </summary>
public partial class ClassTableDialog
{
    private readonly ClassTableViewModel _viewModel;

    public ClassTableDialog(ClassTableViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (_viewModel.TrySave())
        {
            DialogResult = _viewModel.Saved;
        }
    }
}
