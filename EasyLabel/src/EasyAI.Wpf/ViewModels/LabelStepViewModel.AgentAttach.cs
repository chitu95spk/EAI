using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using EasyAI.Desktop.Resources;

namespace EasyAI.Desktop.ViewModels;

/// <summary>One file the user attached to the agent prompt as a hint (an example picture of the defect, a short note).</summary>
public sealed class AgentAttachment
{
    public AgentAttachment(string path, bool isImage)
    {
        Path = path;
        IsImage = isImage;
        Name = System.IO.Path.GetFileName(path);
    }

    public string Path { get; }

    public string Name { get; }

    public bool IsImage { get; }
}

/// <summary>Attached hint files and the number of agent runs a batch keeps going at the same time (both shown in the chat window).</summary>
public sealed partial class LabelStepViewModel
{
    /// <summary>The most files one prompt carries: every one is a picture or a note the agent may read, so it costs tokens.</summary>
    public const int MaxAttachments = 5;

    private const long MaxAttachmentBytes = 20L * 1024 * 1024;

    private static readonly string[] AttachmentImageExtensions = { ".png", ".jpg", ".jpeg", ".bmp", ".tif", ".tiff" };

    private static readonly string[] AttachmentTextExtensions = { ".txt", ".md", ".json", ".csv" };

    /// <summary>The attached files, in the order the agent sees them (index 0 first). They stay until removed and go with every image of a batch.</summary>
    public ObservableCollection<AgentAttachment> Attachments { get; } = new ObservableCollection<AgentAttachment>();

    public bool HasAttachments => Attachments.Count > 0;

    /// <summary>The attachment paths for <see cref="Services.Agent.AgentRunInput.References"/>.</summary>
    private IReadOnlyList<string> AttachmentPaths() => Attachments.Select(a => a.Path).ToList();

    /// <summary>Adds picture and note files (a picker, a drop); returns how many were added. A duplicate, a missing, a too large or another kind of file is skipped.</summary>
    public int AddAttachments(IEnumerable<string> paths)
    {
        int added = 0;
        foreach (string path in paths)
        {
            if (Attachments.Count >= MaxAttachments)
            {
                break;
            }

            string extension = System.IO.Path.GetExtension(path ?? string.Empty).ToLowerInvariant();
            bool image = Array.IndexOf(AttachmentImageExtensions, extension) >= 0;
            bool text = Array.IndexOf(AttachmentTextExtensions, extension) >= 0;
            if (string.IsNullOrWhiteSpace(path) || (!image && !text) || Attachments.Any(a => string.Equals(a.Path, path, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            try
            {
                System.IO.FileInfo file = new System.IO.FileInfo(path);
                if (!file.Exists || file.Length > MaxAttachmentBytes)
                {
                    continue;
                }
            }
            catch (Exception ex) when (ex is System.IO.IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException)
            {
                continue;
            }

            Attachments.Add(new AgentAttachment(path, image));
            added++;
        }

        if (added > 0)
        {
            OnPropertyChanged(nameof(HasAttachments));
        }

        return added;
    }

    [RelayCommand]
    private void AttachFiles()
    {
        Microsoft.Win32.OpenFileDialog dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = Strings.AgentAttachTitle,
            Filter = Strings.AgentAttachFilter,
            Multiselect = true,
            CheckFileExists = true,
        };
        if (dialog.ShowDialog() == true)
        {
            AddAttachments(dialog.FileNames);
        }
    }

    [RelayCommand]
    private void RemoveAttachment(AgentAttachment? attachment)
    {
        if (attachment != null && Attachments.Remove(attachment))
        {
            OnPropertyChanged(nameof(HasAttachments));
        }
    }

    // ----- agent runs at the same time (stored in the settings; 1 = one after the other, with the experience block) -----
    // The Speed slider offers only the stops this machine can do (cores and memory); a stored larger value is used as the cap.
    private System.Collections.Generic.IReadOnlyList<int> SpeedStops => EasyAI.Desktop.Services.Agent.AgentSpeedRules.AvailableStops(EasyAI.Desktop.Services.Agent.AgentSpeedRules.MachineMaxWorkers());

    /// <summary>Agent runs at the same time that a batch really uses: the stored value capped to this machine.</summary>
    public int EffectiveBatchWorkers => EasyAI.Desktop.Services.Agent.AgentSpeedRules.ClampToCap(_host.Settings.AgentBatchWorkers, EasyAI.Desktop.Services.Agent.AgentSpeedRules.MachineMaxWorkers());

    /// <summary>Last slider position (stops are 0-based); 0 when this machine only offers x1.</summary>
    public double SpeedSliderMax => SpeedStops.Count - 1;

    /// <summary>The Speed slider is shown only when the machine offers more than x1.</summary>
    public bool ShowSpeedSlider => SpeedStops.Count > 1;

    public double SpeedSliderValue
    {
        get => EasyAI.Desktop.Services.Agent.AgentSpeedRules.IndexOf(EffectiveBatchWorkers, SpeedStops);
        set
        {
            System.Collections.Generic.IReadOnlyList<int> stops = SpeedStops;
            int index = Math.Max(0, Math.Min(stops.Count - 1, (int)Math.Round(value)));
            SetWorkers(stops[index]);
        }
    }

    /// <summary>"Speed (x4)".</summary>
    public string SpeedText => Strings.AgentSpeedCaption + " (" + EasyAI.Desktop.Services.Agent.AgentSpeedRules.StopText(EffectiveBatchWorkers) + ")";

    private void SetWorkers(int workers)
    {
        if (_host.Settings.AgentBatchWorkers == workers)
        {
            return;
        }

        _host.Settings.AgentBatchWorkers = workers;
        _host.PersistSettings();
        OnPropertyChanged(nameof(SpeedSliderValue));
        OnPropertyChanged(nameof(SpeedText));
    }
}
