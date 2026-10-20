using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using EasyAI.Desktop.ViewModels;

namespace EasyAI.Desktop.Views;

public partial class InspectListPane : UserControl
{
    // Same order as the columns of the list.
    private static readonly InspectSortKey[] ColumnKeys =
    {
        InspectSortKey.File,
        InspectSortKey.Verdict,
        InspectSortKey.Regions,
        InspectSortKey.Score,
        InspectSortKey.Ms,
    };

    public InspectListPane()
    {
        InitializeComponent();
    }

    private InspectViewModel? Model => DataContext as InspectViewModel;

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (InspectList.SelectedItem != null)
        {
            InspectList.ScrollIntoView(InspectList.SelectedItem);
        }
    }

    private void OnHeaderClick(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is GridViewColumnHeader { Column: { } column }
            && InspectList.View is GridView grid)
        {
            int index = grid.Columns.IndexOf(column);
            if (index >= 0 && index < ColumnKeys.Length)
            {
                Model?.SortBy(ColumnKeys[index]);
            }
        }
    }

    private void OnListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete && Model is { } model && model.RemoveSelectedCommand.CanExecute(null))
        {
            model.RemoveSelectedCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (Model is { } model && e.Data.GetData(DataFormats.FileDrop) is string[] paths && paths.Length > 0)
        {
            _ = model.AddPathsAsync(paths);
        }

        e.Handled = true;
    }
}
