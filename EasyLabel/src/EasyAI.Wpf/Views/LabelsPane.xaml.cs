using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using EasyAI.Desktop.ViewModels;

namespace EasyAI.Desktop.Views;

/// <summary>Merged Label / Shape list. Clicks go to <see cref="LabelsPanelViewModel"/>; there is no selection logic here.</summary>
public partial class LabelsPane : UserControl
{
    public LabelsPane()
    {
        InitializeComponent();
    }

    private void OnGroupClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: LabelGroupViewModel group } && DataContext is LabelsPanelViewModel model)
        {
            model.ActivateGroup(group);
        }
    }

    private void OnShapeClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ShapeRowViewModel row } && DataContext is LabelsPanelViewModel model)
        {
            model.SelectShape(row, (Keyboard.Modifiers & ModifierKeys.Control) != 0);
        }
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        // Delete removes the selected shapes through the same command as the toolbar and the canvas (one undo unit).
        if (e.Key == Key.Delete && Keyboard.Modifiers == ModifierKeys.None && DataContext is LabelsPanelViewModel model)
        {
            model.DeleteSelected();
            e.Handled = true;
        }
    }
}
