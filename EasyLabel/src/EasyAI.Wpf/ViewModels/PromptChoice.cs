using CommunityToolkit.Mvvm.ComponentModel;

namespace EasyAI.Desktop.ViewModels;

/// <summary>One toggle chip of the Agent Prompt panel: a shape type or a label that is added to the prompt request while it is on.</summary>
public sealed partial class PromptChoice : ObservableObject
{
    [ObservableProperty]
    private bool _isSelected;

    public PromptChoice(string key, string display)
    {
        Key = key;
        Display = display;
    }

    /// <summary>LabelMe shape type (<c>polygon</c>, <c>oriented_rectangle</c>, ...) or the label text.</summary>
    public string Key { get; }

    /// <summary>Text on the chip.</summary>
    public string Display { get; }
}
