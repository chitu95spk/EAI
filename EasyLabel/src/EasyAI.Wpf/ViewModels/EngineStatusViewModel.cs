using CommunityToolkit.Mvvm.ComponentModel;
using EasyAI.Desktop.Resources;

namespace EasyAI.Desktop.ViewModels;

public enum EngineState
{
    Loading,
    Loaded,
    Unavailable,
    RequiresRestart,
}

public sealed partial class EngineStatusViewModel : ObservableObject
{
    [ObservableProperty]
    private EngineState _state = EngineState.Loading;

    [ObservableProperty]
    private string _version = string.Empty;

    [ObservableProperty]
    private string _path = string.Empty;

    [ObservableProperty]
    private string _message = string.Empty;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private string _toolTipText = string.Empty;

    public EngineStatusViewModel(string name, bool canChooseFolder)
    {
        Name = name;
        CanChooseFolder = canChooseFolder;
        Update(EngineState.Loading, string.Empty, string.Empty, string.Empty, string.Empty);
    }

    public string Name { get; }

    /// <summary>True when the engine location can be chosen by the user.</summary>
    public bool CanChooseFolder { get; }

    public bool RequiresRestart => State == EngineState.RequiresRestart;

    /// <summary>The library message is shown only when the engine is not loaded.</summary>
    public bool ShowMessage => State != EngineState.Loaded && Message.Length > 0;

    public void Update(EngineState state, string version, string path, string message, string detail)
    {
        State = state;
        Version = version;
        Path = path;
        Message = message;
        string stateText;
        switch (state)
        {
            case EngineState.Loaded:
                stateText = Strings.StatusLoaded;
                break;
            case EngineState.Unavailable:
                stateText = Strings.StatusUnavailable;
                break;
            case EngineState.RequiresRestart:
                stateText = Strings.StatusRestart;
                break;
            default:
                stateText = Strings.StatusLoading;
                break;
        }

        StatusText = Name + ": " + stateText + (version.Length > 0 ? " " + version : string.Empty);

        List<string> lines = new List<string>();
        if (version.Length > 0)
        {
            lines.Add(Strings.Version + ": " + version);
        }

        if (path.Length > 0)
        {
            lines.Add(Strings.Location + ": " + path);
        }

        if (detail.Length > 0)
        {
            lines.Add(detail);
        }

        if (message.Length > 0)
        {
            lines.Add(message);
        }

        ToolTipText = lines.Count > 0 ? string.Join(Environment.NewLine, lines) : StatusText;
        OnPropertyChanged(nameof(RequiresRestart));
        OnPropertyChanged(nameof(ShowMessage));
    }
}
