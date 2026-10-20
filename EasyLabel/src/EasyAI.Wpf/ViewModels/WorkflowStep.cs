namespace EasyAI.Desktop.ViewModels;

/// <summary>The tab of the step strip under the toolbar: Inspect, Label (default), Train, or Live (only while the camera is the image source). The other values are reserved.</summary>
public enum WorkflowStep
{
    Data = 1,
    Label = 2,
    Train = 3,
    Evaluate = 4,
    Export = 5,
    Inspect = 6,
    Live = 7,
}
