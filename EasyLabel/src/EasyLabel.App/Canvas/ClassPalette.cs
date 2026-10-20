namespace EasyLabel.App.Canvas;

public sealed class ClassPalette
{
    private static readonly Color[] Colors =
    {
        Color.FromArgb(255, 99, 132),
        Color.FromArgb(54, 162, 235),
        Color.FromArgb(255, 206, 86),
        Color.FromArgb(75, 192, 192),
        Color.FromArgb(153, 102, 255),
        Color.FromArgb(255, 159, 64),
        Color.FromArgb(46, 204, 113),
        Color.FromArgb(236, 112, 99)
    };

    public Color ForLabel(string? label)
    {
        if (string.IsNullOrWhiteSpace(label))
        {
            return Colors[0];
        }

        int index = Math.Abs(StringComparer.OrdinalIgnoreCase.GetHashCode(label)) % Colors.Length;
        return Colors[index];
    }
}
