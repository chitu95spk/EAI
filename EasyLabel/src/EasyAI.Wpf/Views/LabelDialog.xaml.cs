using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using EasyAI.Desktop.Infrastructure;
using EasyAI.Desktop.Resources;
using EasyAI.Desktop.Services;
using EasyLabel.Core.Editing;

namespace EasyAI.Desktop.Views;

/// <summary>"Shape label" dialog: label text, group id, the label list and a description. Enter confirms, Esc cancels.</summary>
public partial class LabelDialog
{
    private readonly LabelDialogRequest _request;
    private bool _suppress;

    public LabelDialog(LabelDialogRequest request)
    {
        _request = request;
        InitializeComponent();
        Title = Strings.LblDlgTitle;
        DialogTitleBar.Title = Strings.LblDlgTitle;

        _suppress = true;
        LabelBox.Text = request.Initial.Label ?? string.Empty;
        GroupBox.Text = request.Initial.GroupId.HasValue ? request.Initial.GroupId.Value.ToString(CultureInfo.InvariantCulture) : string.Empty;
        DescriptionBox.Text = request.Initial.Description ?? string.Empty;
        LabelRow.Visibility = Visibility.Visible;
        LabelBox.Visibility = request.ShowTextField ? Visibility.Visible : Visibility.Collapsed;
        LabelList.ItemsSource = request.Labels.ToList();
        if (request.Initial.Label != null)
        {
            LabelList.SelectedItem = request.Labels.FirstOrDefault(l => string.Equals(l, request.Initial.Label, StringComparison.Ordinal));
        }

        _suppress = false;
    }

    /// <summary>The confirmed values, or null when the dialog was cancelled.</summary>
    public ShapeLabelInfo? Result { get; private set; }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_request.ShowTextField)
        {
            LabelBox.Focus();
            LabelBox.SelectAll();
        }
        else
        {
            LabelList.Focus();
        }
    }

    private void OnLabelTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppress)
        {
            return;
        }

        // Typing filters the list (starts with / contains, from the Settings).
        _suppress = true;
        try
        {
            LabelList.ItemsSource = LabelListRules.Filter(_request.Labels, LabelBox.Text, _request.AutoComplete);
        }
        finally
        {
            _suppress = false;
        }
    }

    private void OnListSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress || LabelList.SelectedItem is not string label)
        {
            return;
        }

        _suppress = true;
        try
        {
            LabelBox.Text = label;
        }
        finally
        {
            _suppress = false;
        }
    }

    private void OnListDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (LabelList.SelectedItem is string)
        {
            Confirm();
        }
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        Confirm();
    }

    /// <summary>Enter anywhere (except the multi-line description) approves the label for the shape, like the OK button.</summary>
    private void OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if ((e.Key == System.Windows.Input.Key.Enter || e.Key == System.Windows.Input.Key.Return)
            && !ReferenceEquals(e.OriginalSource, DescriptionBox)
            && !(e.OriginalSource is System.Windows.Controls.Button))
        {
            e.Handled = true;
            Confirm();
        }
    }

    private void Confirm()
    {
        string typed = _request.ShowTextField ? LabelBox.Text.Trim() : string.Empty;
        string label = typed.Length > 0
            ? typed
            : (LabelList.SelectedItem as string ?? (LabelList.Items.Count == 1 ? LabelList.Items[0] as string : null) ?? LabelBox.Text).Trim();
        if (label.Length == 0 && _request.Initial.Label != null)
        {
            // A single shape always needs a label; a mixed selection (Initial.Label == null) may keep its labels.
            return;
        }

        int? group = null;
        if (int.TryParse(GroupBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
        {
            group = parsed;
        }

        Result = new ShapeLabelInfo { Label = label, GroupId = group, Description = DescriptionBox.Text };
        DialogResult = true;
    }
}
