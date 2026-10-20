namespace EasyLabel.Core.Model;

public sealed class ProjectSettings
{
    public string Version { get; set; } = "1.0";

    public DateTime LastOpenedUtc { get; set; } = DateTime.UtcNow;
}
