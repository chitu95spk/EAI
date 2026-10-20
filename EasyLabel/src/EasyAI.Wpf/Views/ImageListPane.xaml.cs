using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using EasyAI.Desktop.ViewModels;
using EasyLabel.SegAI.Data;

namespace EasyAI.Desktop.Views;

public partial class ImageListPane : UserControl
{
    public ImageListPane()
    {
        InitializeComponent();
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ImageList.SelectedItem != null)
        {
            ImageList.ScrollIntoView(ImageList.SelectedItem);
        }

        if (DataContext is FilesPanelViewModel panel)
        {
            panel.MultiSelection = Selected();
        }
    }

    /// <summary>The rows selected with Ctrl / Shift (a list copy, so the command can change the list while it runs).</summary>
    private List<ImageItemViewModel> Selected()
    {
        return ImageList.SelectedItems.OfType<ImageItemViewModel>().ToList();
    }

    /// <summary>A right click on a row that is not in the selection selects just that row first (Explorer behaviour); a click inside the selection keeps it.</summary>
    private void OnRowRightClick(object sender, MouseButtonEventArgs e)
    {
        DependencyObject? node = e.OriginalSource as DependencyObject;
        while (node != null && node is not ListViewItem)
        {
            node = node is Visual || node is System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
        }

        if (node is ListViewItem item && item.DataContext is ImageItemViewModel row && !ImageList.SelectedItems.Contains(row))
        {
            ImageList.SelectedItems.Clear();
            ImageList.SelectedItems.Add(row);
        }
    }

    private async void OnMenuDelete(object sender, RoutedEventArgs e)
    {
        if (DataContext is FilesPanelViewModel panel && Selected() is { Count: > 0 } rows)
        {
            await panel.DeleteSelectionAsync(rows);
        }
    }

    private async void OnMenuTrain(object sender, RoutedEventArgs e)
    {
        if (DataContext is FilesPanelViewModel panel && Selected() is { Count: > 0 } rows)
        {
            await panel.SetModeForSelectionAsync(rows, SegTrainingMode.Train);
        }
    }

    private async void OnMenuVal(object sender, RoutedEventArgs e)
    {
        if (DataContext is FilesPanelViewModel panel && Selected() is { Count: > 0 } rows)
        {
            await panel.SetModeForSelectionAsync(rows, SegTrainingMode.Val);
        }
    }

    private async void OnMenuRemoveLabels(object sender, RoutedEventArgs e)
    {
        if (DataContext is FilesPanelViewModel panel && Selected() is { Count: > 0 } rows)
        {
            await panel.RemoveLabelsForSelectionAsync(rows);
        }
    }
}
