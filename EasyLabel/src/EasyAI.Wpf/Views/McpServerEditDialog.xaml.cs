using System.Windows;
using EasyAI.Desktop.Resources;
using EasyAI.Desktop.Services.Agent;

namespace EasyAI.Desktop.Views;

/// <summary>Add / edit dialog of an external MCP server entry. Save validates the entry; the caller stores it (and the store clears the approval).</summary>
public partial class McpServerEditDialog
{
    public McpServerEditDialog(string title, McpServerEntry? initial)
    {
        InitializeComponent();
        Title = title;
        DialogTitleBar.Title = title;
        if (initial != null)
        {
            NameBox.Text = initial.Name;
            SourceBox.Text = initial.SourceUrl;
            VersionBox.Text = initial.Version;
            CommandBox.Text = initial.Command;
            ArgsBox.Text = string.Join("\n", initial.Args);
            AccessBox.Text = initial.AccessSummary;
            ToolsBox.Text = string.Join("\n", initial.Tools);
        }
    }

    /// <summary>The confirmed entry (never approved), or null when the dialog was cancelled.</summary>
    public McpServerEntry? Result { get; private set; }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        NameBox.Focus();
    }

    private static List<string> Lines(string text, bool trim)
    {
        List<string> lines = new List<string>();
        foreach (string line in (text ?? string.Empty).Replace("\r\n", "\n").Split('\n'))
        {
            string value = trim ? line.Trim() : line.TrimEnd('\r');
            if (value.Length > 0)
            {
                lines.Add(value);
            }
        }

        return lines;
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        McpServerEntry entry = new McpServerEntry
        {
            Name = NameBox.Text.Trim(),
            SourceUrl = SourceBox.Text.Trim(),
            Version = VersionBox.Text.Trim(),
            Command = CommandBox.Text.Trim(),
            Args = Lines(ArgsBox.Text, false),
            AccessSummary = AccessBox.Text.Trim(),
            Tools = Lines(ToolsBox.Text, true),
        };

        if (!McpServerRegistry.IsValidName(entry.Name))
        {
            Fail(Strings.McpNameInvalid, NameBox);
            return;
        }

        if (!McpServerRegistry.IsComplete(entry))
        {
            Fail(Strings.McpEntryInvalid, CommandBox);
            return;
        }

        Result = entry;
        DialogResult = true;
    }

    private void Fail(string message, System.Windows.Controls.Control focus)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
        focus.Focus();
    }
}
