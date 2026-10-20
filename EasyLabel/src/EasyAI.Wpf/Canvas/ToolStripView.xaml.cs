using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace EasyAI.Desktop.Views;

/// <summary>Left tool column of the main window (DataContext: the canvas view model).</summary>
public partial class ToolStripView : UserControl
{
    /// <summary>The round chat button's command; the main window binds it to the Label step's open-chat command (this control's own DataContext is the canvas).</summary>
    public static readonly DependencyProperty AgentChatCommandProperty = DependencyProperty.Register(
        nameof(AgentChatCommand),
        typeof(ICommand),
        typeof(ToolStripView),
        new PropertyMetadata(null));

    public ToolStripView()
    {
        InitializeComponent();
    }

    /// <summary>The round chat button; the chat window anchors itself next to it.</summary>
    public FrameworkElement AgentChatAnchor => AgentChatRoundButton;

    public ICommand? AgentChatCommand
    {
        get => (ICommand?)GetValue(AgentChatCommandProperty);
        set => SetValue(AgentChatCommandProperty, value);
    }
}
