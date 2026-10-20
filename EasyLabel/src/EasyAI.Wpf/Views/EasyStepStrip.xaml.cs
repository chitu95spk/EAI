using System.Windows.Controls;

namespace EasyAI.Desktop.Views;

/// <summary>
/// The Easy-mode step strip: five buttons (Locked, Ready, Running, Done) that read MainViewModel.EasyModeSteps through the window's DataContext.
/// It has no state of its own; the window shows it only in Simple mode.
/// </summary>
public partial class EasyStepStrip : UserControl
{
    public EasyStepStrip()
    {
        InitializeComponent();
    }
}
