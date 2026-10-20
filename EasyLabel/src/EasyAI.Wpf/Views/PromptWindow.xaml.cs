using System.Windows;

namespace EasyAI.Desktop.Views;

public enum PromptResult
{
    Primary,
    Secondary,
    Cancel,
}

/// <summary>Small modal question with up to three buttons (Save / Discard / Cancel, or Delete / Cancel).</summary>
public partial class PromptWindow
{
    public PromptWindow(string title, string message, string? details, string primary, string? secondary, string cancel)
    {
        InitializeComponent();
        Title = title;
        PromptTitleBar.Title = title;
        MessageText.Text = message;
        DetailsText.Text = details ?? string.Empty;
        DetailsText.Visibility = string.IsNullOrEmpty(details) ? Visibility.Collapsed : Visibility.Visible;
        PrimaryButton.Content = primary;
        SecondaryButton.Content = secondary ?? string.Empty;
        SecondaryButton.Visibility = secondary == null ? Visibility.Collapsed : Visibility.Visible;
        CancelButton.Content = cancel;
        Result = PromptResult.Cancel;
    }

    /// <summary>
    /// Compact Yes / No question without title bar. It opens one row below <paramref name="row"/> (the selected list row, in device-independent
    /// screen units), or one row above it when it would leave <paramref name="bounds"/> (the main window), and stays inside the work area.
    /// </summary>
    public PromptWindow(string message, string yes, string no, Rect row, Rect bounds)
        : this(string.Empty, message, null, yes, null, no)
    {
        PromptTitleBar.Visibility = Visibility.Collapsed;
        ContentScroll.Visibility = Visibility.Collapsed;
        PromptIcon.Visibility = Visibility.Visible;
        System.Windows.Automation.AutomationProperties.SetName(this, message);
        ButtonPanel.Margin = new Thickness(10, 6, 10, 6);
        ButtonPanel.HorizontalAlignment = HorizontalAlignment.Left;
        MinWidth = 0;
        MinHeight = 0;
        Width = double.NaN;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = row.Left + 8;
        Top = row.Bottom;
        Loaded += (s, e) =>
        {
            // The hidden title bar still adds non-client height: shrink the window to the button row.
            SizeToContent = SizeToContent.Manual;
            ButtonPanel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            MaxHeight = double.PositiveInfinity;
            Height = ButtonPanel.DesiredSize.Height;
            UpdateLayout();
            Rect work = SystemParameters.WorkArea;
            double top = row.Bottom;
            if (top + ActualHeight > bounds.Bottom)
            {
                top = row.Top - ActualHeight;
            }

            Left = Math.Max(work.Left, Math.Min(row.Left + 8, work.Right - ActualWidth));
            Top = Math.Max(work.Top, Math.Min(top, work.Bottom - ActualHeight));
            PrimaryButton.Focus();
        };
    }

    public PromptResult Result { get; private set; }

    private void OnPrimary(object sender, RoutedEventArgs e)
    {
        Result = PromptResult.Primary;
        Close();
    }

    private void OnSecondary(object sender, RoutedEventArgs e)
    {
        Result = PromptResult.Secondary;
        Close();
    }
}
