using EasyLabel.Ai.Onnx;

namespace EasyLabel.Ai.Onnx.Tests;

public class YoloAndMaskTests
{
    [Fact]
    public void LetterboxCentresAWideImage()
    {
        var box = new Letterbox(1280, 720, 640);

        Assert.Equal(0.5f, box.Scale);
        Assert.Equal(640, box.NewWidth);
        Assert.Equal(360, box.NewHeight);
        Assert.Equal(0, box.PadX);
        Assert.Equal(140, box.PadY);
    }

    [Fact]
    public void PreprocessPadsWith114AndPlacesTheImage()
    {
        // 2x1 white image into a 4x4 canvas: scale 2, content 4x2, pad rows 0 and 3.
        var bgr = new byte[] { 255, 255, 255, 255, 255, 255 };
        var box = new Letterbox(2, 1, 4);

        float[] data = YoloDecoder.Preprocess(bgr, 2, 1, box);

        Assert.Equal(3 * 16, data.Length);
        Assert.Equal(114f / 255f, data[0], 5);
        Assert.Equal(1f, data[1 * 4 + 0], 5);
        Assert.Equal(1f, data[16 + 2 * 4 + 3], 5);
        Assert.Equal(114f / 255f, data[2 * 16 + 3 * 4], 5);
    }

    private static float[] Tensor(int nc, int n, params (int Anchor, float Cx, float Cy, float W, float H, int Class, float Score)[] items)
    {
        var data = new float[(4 + nc) * n];
        foreach (var it in items)
        {
            data[0 * n + it.Anchor] = it.Cx;
            data[1 * n + it.Anchor] = it.Cy;
            data[2 * n + it.Anchor] = it.W;
            data[3 * n + it.Anchor] = it.H;
            data[(4 + it.Class) * n + it.Anchor] = it.Score;
        }

        return data;
    }

    [Fact]
    public void DecodeUndoesLetterboxAndNmsIsClassAware()
    {
        var box = new Letterbox(1280, 720, 640);
        float[] data = Tensor(2, 4,
            (0, 320, 320, 100, 80, 1, 0.9f),
            (1, 322, 320, 100, 80, 1, 0.8f),   // same class, overlaps: suppressed
            (2, 320, 320, 100, 80, 0, 0.6f),   // other class: kept
            (3, 320, 320, 100, 80, 0, 0.1f));  // below confidence

        List<BoxDetection> raw = YoloDecoder.Decode(data, 6, 4, 2, box, 1280, 720, 0.25f, null);
        List<BoxDetection> kept = Nms.Apply(raw, 0.5f);

        Assert.Equal(3, raw.Count);
        Assert.Equal(2, kept.Count);
        Assert.Equal(1, kept[0].ClassId);
        Assert.Equal(0.9f, kept[0].Score);
        Assert.Equal(540f, kept[0].X1, 3);
        Assert.Equal(740f, kept[0].X2, 3);
        Assert.Equal(280f, kept[0].Y1, 3);
        Assert.Equal(440f, kept[0].Y2, 3);
        Assert.Equal(0, kept[1].ClassId);
    }

    [Fact]
    public void DecodeClampsToTheImageAndFiltersClasses()
    {
        var box = new Letterbox(640, 640, 640);
        float[] data = Tensor(2, 2,
            (0, 10, 10, 100, 100, 0, 0.9f),
            (1, 300, 300, 50, 50, 1, 0.9f));

        List<BoxDetection> onlyClass0 = YoloDecoder.Decode(data, 6, 2, 2, box, 640, 640, 0.25f, new HashSet<int> { 0 });

        BoxDetection d = Assert.Single(onlyClass0);
        Assert.Equal(0f, d.X1);
        Assert.Equal(0f, d.Y1);
        Assert.Equal(60f, d.X2, 3);
    }

    [Fact]
    public void DecodeRejectsAWrongAttributeCountWithTheActualShape()
    {
        var box = new Letterbox(640, 640, 640);

        var ex = Assert.Throws<AiModelException>(() => YoloDecoder.Decode(new float[5 * 4], 5, 4, 2, box, 640, 640, 0.25f, null));

        Assert.Contains("[1,5,4]", ex.Message);
    }

    [Fact]
    public void NmsKeepsDisjointBoxesOfTheSameClass()
    {
        var list = new[]
        {
            new BoxDetection(0, 0, 10, 10, 0.9f, 0),
            new BoxDetection(20, 20, 30, 30, 0.8f, 0),
            new BoxDetection(1, 1, 10, 10, 0.7f, 0),
        };

        List<BoxDetection> kept = Nms.Apply(list, 0.5f);

        Assert.Equal(2, kept.Count);
        Assert.Equal(0.9f, kept[0].Score);
        Assert.Equal(0.8f, kept[1].Score);
    }

    [Fact]
    public void NamesParseUltralyticsDictRepr()
    {
        string[]? names = YoloNames.ParseUltralytics("{0: 'person', 1: 'traffic light', 2: \"it's\"}");

        Assert.NotNull(names);
        Assert.Equal(new[] { "person", "traffic light", "it's" }, names);
        Assert.Null(YoloNames.ParseUltralytics("{0: 'a', 2: 'c'}"));
        Assert.Null(YoloNames.ParseUltralytics(null));
        Assert.Equal(new[] { "a", "b" }, YoloNames.ParseLines(new[] { " a ", "", "b" }));
    }

    private static double Area(System.Drawing.PointF[] p)
    {
        double sum = 0;
        for (int i = 0; i < p.Length; i++)
        {
            System.Drawing.PointF a = p[i];
            System.Drawing.PointF b = p[(i + 1) % p.Length];
            sum += (double)a.X * b.Y - (double)b.X * a.Y;
        }

        return Math.Abs(sum) / 2.0;
    }

    [Fact]
    public void PolygonizeSquareWithHoleGivesOuterAndOneHole()
    {
        const int size = 100;
        var mask = new byte[size * size];
        for (int y = 20; y < 80; y++)
            for (int x = 20; x < 80; x++)
                mask[y * size + x] = 255;
        for (int y = 40; y < 60; y++)
            for (int x = 40; x < 60; x++)
                mask[y * size + x] = 0;

        MaskPolygon? polygon = MaskPolygonizer.Polygonize(mask, size, size);

        Assert.NotNull(polygon);
        Assert.InRange(polygon!.Outer.Length, 4, 8);
        Assert.Single(polygon.Holes);
        Assert.Equal(60 * 60 - 20 * 20, polygon.PixelCount);
        Assert.InRange(Area(polygon.Outer), 3400, 3700);
        Assert.InRange(Area(polygon.Holes[0]), 300, 450);
        Assert.InRange(polygon.Bounds.Left, 19f, 21f);
        Assert.InRange(polygon.Bounds.Right, 78f, 80f);
    }

    [Fact]
    public void PolygonizeKeepsOnlyTheLargestComponentAndNullForEmpty()
    {
        const int size = 50;
        var mask = new byte[size * size];
        for (int y = 2; y < 6; y++)
            for (int x = 2; x < 6; x++)
                mask[y * size + x] = 255;
        for (int y = 20; y < 40; y++)
            for (int x = 20; x < 40; x++)
                mask[y * size + x] = 255;

        MaskPolygon? polygon = MaskPolygonizer.Polygonize(mask, size, size);

        Assert.NotNull(polygon);
        Assert.Equal(400, polygon!.PixelCount);
        Assert.True(polygon.Bounds.Left >= 19f);
        Assert.Empty(polygon.Holes);
        Assert.Null(MaskPolygonizer.Polygonize(new byte[size * size], size, size));
    }

    [Fact]
    public void PolygonizeCapsThePointCount()
    {
        const int size = 200;
        var mask = new byte[size * size];
        // A comb has many corners; the cap must hold.
        for (int y = 10; y < 190; y++)
            for (int x = 10; x < 190; x++)
                if (x < 20 || y < 20 || (x / 4) % 2 == 0) mask[y * size + x] = 255;

        MaskPolygon? polygon = MaskPolygonizer.Polygonize(mask, size, size, 30);

        Assert.NotNull(polygon);
        Assert.True(polygon!.Outer.Length <= 30);
    }
}
