using System.Windows;
using EasyAI.Desktop.Resources;
using EasyAI.Desktop.Services.Agent;

namespace EasyAI.Desktop.Views;

/// <summary>
/// Shows everything that will be started and called for an external MCP server (name, source, version, command, arguments, working folder,
/// access summary, tools) and the statement that the app does not install or verify it. Only a click on Approve returns true; Cancel is the default.
/// </summary>
public partial class McpApprovalDialog
{
    public McpApprovalDialog(McpServerEntry entry)
    {
        InitializeComponent();
        string title = string.Format(Strings.McpApprovalTitleFormat, entry.Name);
        Title = title;
        DialogTitleBar.Title = title;
        StatementText.Text = Strings.McpApprovalStatement;
        NameText.Text = entry.Name;
        SourceText.Text = entry.SourceUrl;
        VersionText.Text = entry.Version;
        CommandText.Text = entry.Command;
        ArgsText.Text = string.Join("\n", entry.Args);
        WorkingFolderText.Text = Strings.McpWorkingFolderValue;
        AccessText.Text = entry.AccessSummary;
        ToolsText.Text = string.Join("\n", entry.Tools);
    }

    private void OnApprove(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }
}
