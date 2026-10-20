using System.Windows;
using EasyAI.Desktop.Infrastructure;
using EasyAI.Desktop.Resources;
using Wpf.Ui.Controls;
using TextBlock = Wpf.Ui.Controls.TextBlock;

namespace EasyAI.Desktop.Views;

/// <summary>Shows docs/easyai/guide.md (copied next to the executable as docs\guide.md) as headings, paragraphs and bullets.</summary>
public partial class GuideWindow
{
    public static string GuidePath => System.IO.Path.Combine(AppContext.BaseDirectory, "docs", "guide.md");

    public GuideWindow()
    {
        InitializeComponent();
        Load();
    }

    private void Load()
    {
        string path = GuidePath;
        string markdown;
        try
        {
            markdown = File.ReadAllText(path);
        }
        catch (Exception ex)
        {
            GuideBody.Children.Add(new TextBlock
            {
                Text = string.Format(Strings.GuideMissingFormat, path, ex.Message),
                TextWrapping = TextWrapping.Wrap,
            });
            return;
        }

        foreach (GuideBlock block in GuideParser.Parse(markdown))
        {
            TextBlock text = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Text = block.Kind == GuideBlockKind.Bullet ? "•  " + block.Text : block.Text,
            };
            switch (block.Kind)
            {
                case GuideBlockKind.Heading1:
                    text.FontTypography = FontTypography.Title;
                    text.Margin = new Thickness(0, 8, 0, 8);
                    break;
                case GuideBlockKind.Heading2:
                    text.FontTypography = FontTypography.Subtitle;
                    text.Margin = new Thickness(0, 16, 0, 6);
                    break;
                case GuideBlockKind.Heading3:
                    text.FontTypography = FontTypography.BodyStrong;
                    text.Margin = new Thickness(0, 10, 0, 4);
                    break;
                case GuideBlockKind.Bullet:
                    text.Margin = new Thickness(16, 0, 0, 3);
                    break;
                default:
                    text.Margin = new Thickness(0, 0, 0, 8);
                    break;
            }

            GuideBody.Children.Add(text);
        }
    }
}
