using System.Windows.Media;
using EasyAI.Desktop.Infrastructure;

namespace EasyAI.Desktop.Annotation;

/// <summary>
/// The one place that declares annotation colours (class colours and the NG/OK tokens). Everything else on the canvas uses the
/// WPF-UI theme brushes. Index 0 = NG, 1 = OK, 2..31 = further project classes (<see cref="ClassTableRules.ColorOf"/>),
/// <see cref="ClassTableRules.OtherIndex"/> = any other label. All brushes and pens are frozen.
/// </summary>
public static class AnnotationPalette
{
    public static readonly Color NgColor = Color.FromRgb(0xE5, 0x48, 0x4D);
    public static readonly Color OkColor = Color.FromRgb(0x30, 0xA4, 0x6C);
    public static readonly Color OtherColor = Color.FromRgb(0xF5, 0xA5, 0x24);

    public const double OutlineThickness = 2.0;
    public const double SelectedThickness = 3.0;

    private const int Slots = ClassTableRules.OtherIndex + 1;

    private static readonly Color[] Colors = BuildColors();
    private static readonly Brush[] Fills = new Brush[Slots];
    private static readonly Brush[] Solids = new Brush[Slots];
    private static readonly Pen[] Outlines = new Pen[Slots];
    private static readonly Pen[] Selected = new Pen[Slots];
    private static readonly Pen[] Dashed = new Pen[Slots];
    private static readonly Pen[] DashedSelected = new Pen[Slots];

    private static Color[] BuildColors()
    {
        var colors = new Color[Slots];
        for (int i = 0; i < ClassTableRules.MaxClasses; i++)
        {
            uint argb = ClassTableRules.ColorOf(i);
            colors[i] = Color.FromRgb((byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
        }

        colors[ClassTableRules.OtherIndex] = OtherColor;
        return colors;
    }

    static AnnotationPalette()
    {
        for (int i = 0; i < Slots; i++)
        {
            Color c = Colors[i];
            Brush fill = new SolidColorBrush(Color.FromArgb(0x38, c.R, c.G, c.B));
            fill.Freeze();
            Fills[i] = fill;

            Brush solid = new SolidColorBrush(c);
            solid.Freeze();
            Solids[i] = solid;

            Pen outline = new Pen(solid, OutlineThickness) { LineJoin = PenLineJoin.Round };
            outline.Freeze();
            Outlines[i] = outline;

            Pen selected = new Pen(solid, SelectedThickness) { LineJoin = PenLineJoin.Round };
            selected.Freeze();
            Selected[i] = selected;

            Pen dashed = new Pen(solid, OutlineThickness) { DashStyle = DashStyles.Dash, LineJoin = PenLineJoin.Round };
            dashed.Freeze();
            Dashed[i] = dashed;

            Pen dashedSelected = new Pen(solid, SelectedThickness) { DashStyle = DashStyles.Dash, LineJoin = PenLineJoin.Round };
            dashedSelected.Freeze();
            DashedSelected[i] = dashedSelected;
        }
    }

    /// <summary>Frozen fill / outline pens of one arbitrary shape colour (settings colour modes).</summary>
    public sealed class Swatch
    {
        public Swatch(Color c)
        {
            Fill = Freeze(new SolidColorBrush(Color.FromArgb(0x38, c.R, c.G, c.B)));
            Solid = Freeze(new SolidColorBrush(Color.FromRgb(c.R, c.G, c.B)));
            Outline = Freeze(new Pen(Solid, OutlineThickness) { LineJoin = PenLineJoin.Round });
            Selected = Freeze(new Pen(Solid, SelectedThickness) { LineJoin = PenLineJoin.Round });
            Dashed = Freeze(new Pen(Solid, OutlineThickness) { DashStyle = DashStyles.Dash, LineJoin = PenLineJoin.Round });
            DashedSelected = Freeze(new Pen(Solid, SelectedThickness) { DashStyle = DashStyles.Dash, LineJoin = PenLineJoin.Round });
        }

        public Brush Fill { get; }

        public Brush Solid { get; }

        public Pen Outline { get; }

        public Pen Selected { get; }

        public Pen Dashed { get; }

        public Pen DashedSelected { get; }

        private static T Freeze<T>(T value) where T : System.Windows.Freezable
        {
            value.Freeze();
            return value;
        }
    }

    private static readonly Dictionary<uint, Swatch> Swatches = new Dictionary<uint, Swatch>();

    /// <summary>Cached frozen brushes and pens of a colour (0xAARRGGBB; alpha is ignored). UI thread only.</summary>
    public static Swatch For(uint argb)
    {
        uint key = argb | 0xFF000000u;
        if (!Swatches.TryGetValue(key, out Swatch? swatch))
        {
            swatch = new Swatch(Color.FromRgb((byte)(key >> 16), (byte)(key >> 8), (byte)key));
            Swatches[key] = swatch;
        }

        return swatch;
    }

    private static int Clamp(int classIndex) => classIndex < 0 || classIndex >= ClassTableRules.MaxClasses ? ClassTableRules.OtherIndex : classIndex;

    public static Brush Fill(int classIndex) => Fills[Clamp(classIndex)];

    public static Brush Solid(int classIndex) => Solids[Clamp(classIndex)];

    public static Pen Outline(int classIndex) => Outlines[Clamp(classIndex)];

    public static Pen SelectedOutline(int classIndex) => Selected[Clamp(classIndex)];

    public static Pen DashedOutline(int classIndex) => Dashed[Clamp(classIndex)];

    public static Pen DashedSelectedOutline(int classIndex) => DashedSelected[Clamp(classIndex)];
}
