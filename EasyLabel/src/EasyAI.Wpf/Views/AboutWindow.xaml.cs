using System.Reflection;
using EasyAI.Desktop.Resources;

namespace EasyAI.Desktop.Views;

/// <summary>Small modal dialog: application version and the engine status block (SegAI, MVS).</summary>
public partial class AboutWindow
{
    public AboutWindow()
    {
        InitializeComponent();
        VersionText.Text = string.Format(Strings.AboutVersionFormat, ReadVersion());
    }

    private static string ReadVersion()
    {
        Assembly assembly = Assembly.GetEntryAssembly() ?? typeof(AboutWindow).Assembly;
        string? informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            int plus = informational.IndexOf('+');
            return plus > 0 ? informational.Substring(0, plus) : informational;
        }

        return assembly.GetName().Version?.ToString() ?? string.Empty;
    }
}
