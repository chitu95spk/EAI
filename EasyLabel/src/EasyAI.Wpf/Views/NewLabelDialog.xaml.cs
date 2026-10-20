using System.Windows;
using EasyAI.Desktop.Resources;

namespace EasyAI.Desktop.Views;

/// <summary>"New label" dialog: one name. Enter confirms, Esc cancels; the caller validates the name against the class table.</summary>
public partial class NewLabelDialog
{
    public NewLabelDialog()
    {
        InitializeComponent();
        Title = Strings.NewLabelTitle;
        DialogTitleBar.Title = Strings.NewLabelTitle;
    }

    /// <summary>The trimmed name, or null when the dialog was cancelled.</summary>
    public string? Result { get; private set; }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        NameBox.Focus();
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        string name = NameBox.Text.Trim();
        if (name.Length == 0)
        {
            NameBox.Focus();
            return;
        }

        Result = name;
        DialogResult = true;
    }
}
