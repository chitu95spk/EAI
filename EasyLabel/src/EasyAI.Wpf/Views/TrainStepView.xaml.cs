using System.Windows.Controls;

namespace EasyAI.Desktop.Views;

/// <summary>Train tab controls (counts header, options, Start/Stop, progress, candidates, comparison). The charts are in <see cref="TrainChartsView"/>.</summary>
public partial class TrainStepView : UserControl
{
    public TrainStepView()
    {
        InitializeComponent();
    }
}
