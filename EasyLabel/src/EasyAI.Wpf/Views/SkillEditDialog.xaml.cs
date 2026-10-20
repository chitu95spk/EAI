using System.Windows;
using EasyAI.Desktop.Resources;
using EasyAI.Desktop.Services;
using EasyAI.Desktop.Services.Agent;

namespace EasyAI.Desktop.Views;

/// <summary>Skill editor: the name (only when a skill is added) and the SKILL.md text. Save validates the name; the caller writes the files.</summary>
public partial class SkillEditDialog
{
    private readonly bool _nameEditable;

    public SkillEditDialog(string title, string name, string text, bool nameEditable)
    {
        _nameEditable = nameEditable;
        InitializeComponent();
        Title = title;
        DialogTitleBar.Title = title;
        NameBox.Text = name;
        NameBox.IsEnabled = nameEditable;
        TextBoxBody.Text = text;
    }

    /// <summary>The confirmed name and text, or null when the dialog was cancelled.</summary>
    public SkillEditResult? Result { get; private set; }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_nameEditable)
        {
            NameBox.Focus();
        }
        else
        {
            TextBoxBody.Focus();
        }
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        string name = NameBox.Text.Trim();
        if (_nameEditable && !AgentSkillStore.IsValidName(name))
        {
            ErrorText.Text = Strings.AgentsSkillNameInvalid;
            ErrorText.Visibility = Visibility.Visible;
            NameBox.Focus();
            return;
        }

        Result = new SkillEditResult(name, TextBoxBody.Text);
        DialogResult = true;
    }
}
