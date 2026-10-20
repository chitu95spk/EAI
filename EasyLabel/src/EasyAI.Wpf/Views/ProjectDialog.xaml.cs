using System.ComponentModel;
using System.Windows;
using EasyAI.Desktop.Resources;
using EasyAI.Desktop.ViewModels;

namespace EasyAI.Desktop.Views;

/// <summary>
/// New Project and Save Project As dialog. OK is disabled while the input is invalid. In Save As mode OK runs the copy with progress;
/// Cancel, Esc and the close button then cancel the copy instead of closing the window.
/// </summary>
public partial class ProjectDialog
{
    private readonly ProjectDialogViewModel _viewModel;

    public ProjectDialog(ProjectDialogViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        string title = viewModel.IsNewMode ? Strings.ProjNewTitle : Strings.ProjSaveAsTitle;
        Title = title;
        DialogTitleBar.Title = title;
        System.Windows.Automation.AutomationProperties.SetAutomationId(this, viewModel.IsNewMode ? "NewProjectDialog" : "SaveProjectAsDialog");
        viewModel.CloseRequested -= OnCloseRequested;
        viewModel.CloseRequested += OnCloseRequested;
    }

    private void OnCloseRequested(object? sender, bool result)
    {
        DialogResult = result;
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        _viewModel.RequestCancel();
    }

    private void OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Escape)
        {
            _viewModel.RequestCancel();
            e.Handled = true;
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_viewModel.IsRunning)
        {
            e.Cancel = true;
            _viewModel.RequestCancel();
        }

        base.OnClosing(e);
    }
}
