using EasyLabel.Core.Model;
using EasyLabel.SegAI.Convert;
using EasyLabel.SegAI.Data;
using EasyLabel.SegAI.Engine;

namespace EasyLabel.SegAI.Tests;

// EAI-POS P5: managed side of the per-instance head (converter, Detect/Position split, probe fallback). No native DLL is needed.
public sealed class SegInstanceTests
{
    [Fact]
    public void Converter_RotToInstance_Direction()
    {
        const double theta = Math.PI / 6;
        double c = Math.Cos(theta), s = Math.Sin(theta);
        // Local corners TL, TR, BR, BL of a 40 x 20 box centred at (50, 40), rotated by theta in image coordinates (y down).
        double[][] local = { new[] { -20.0, -10.0 }, new[] { 20.0, -10.0 }, new[] { 20.0, 10.0 }, new[] { -20.0, 10.0 } };
        var shape = new LabelShape { Label = "NG", ShapeType = "oriented_rectangle" };
        foreach (double[] p in local) shape.Points.Add(new[] { 50 + p[0] * c - p[1] * s, 40 + p[0] * s + p[1] * c });

        SegInstance box = Assert.Single(SegLabelMeConverter.ToInstances(new[] { shape }, new SegClassMap()));
        Assert.Equal(SegInstance.KindBox, box.Kind);
        Assert.Equal(0, box.ClassIndex);
        Assert.True(Math.Abs(box.Cx - 50) < 1e-3, "centre x");
        Assert.True(Math.Abs(box.Cy - 40) < 1e-3, "centre y");
        Assert.True(Math.Abs(box.W - 40) < 1e-3, "w along TL->TR");
        Assert.True(Math.Abs(box.H - 20) < 1e-3, "h along TL->BL");
        Assert.True(Math.Abs(box.Theta - theta) < 1e-4, "theta = atan2(TR - TL)");
    }

    [Fact]
    public void ToInstances_ImageSize_SkipsCentresOutsideAndKeepsPartlyOutsideBoxes()
    {
        var map = new SegClassMap();
        LabelShape Box(double cx, double cy)
        {
            var shape = new LabelShape { Label = "NG", ShapeType = "oriented_rectangle" };
            shape.Points.AddRange(new[] { new[] { cx - 20, cy - 10 }, new[] { cx + 20, cy - 10 }, new[] { cx + 20, cy + 10 }, new[] { cx - 20, cy + 10 } });
            return shape;
        }

        LabelShape[] shapes = { Box(50, 40), Box(5, 40), Box(-5, 40), Box(50, 95) }; // inside, centre inside but cut, centre outside left, centre outside below
        Assert.Equal(4, SegLabelMeConverter.ToInstances(shapes, map).Count); // no size: unchanged behaviour
        List<SegInstance> guarded = SegLabelMeConverter.ToInstances(shapes, map, false, 100, 80);
        Assert.Equal(new[] { 50f, 5f }, guarded.Select(i => i.Cx).ToArray());
    }

    [Fact]
    public void Converter_CircleSkippedInDetect()
    {
        var map = new SegClassMap();
        var circle = new LabelShape { Label = "NG", ShapeType = "circle" };
        circle.Points.Add(new[] { 50.0, 50.0 });
        circle.Points.Add(new[] { 60.0, 50.0 });
        var rect = new LabelShape { Label = "NG", ShapeType = "rectangle" };
        rect.Points.Add(new[] { 10.0, 10.0 });
        rect.Points.Add(new[] { 30.0, 30.0 });

        // Detect path (FromLabelMe): the circle is skipped exactly as before, the rectangle still converts.
        List<SegAnnotation> regions = SegLabelMeConverter.FromLabelMe(new[] { circle, rect }, null, map, out _, out int skipped);
        SegAnnotation only = Assert.Single(regions);
        Assert.Equal("Rect", only.Kind);
        Assert.Equal(1, skipped);
        Assert.DoesNotContain(regions, r => r.Kind == SegAnnotation.CircleKind);

        // Position-only path: the same circle becomes a disc of radius 10 (centre and edge point).
        SegAnnotation disc = Assert.Single(SegLabelMeConverter.ToCircleRegions(new[] { circle }, map));
        Assert.Equal(SegAnnotation.CircleKind, disc.Kind);
        Assert.True(Math.Abs(disc.Points[1].X - disc.Points[0].X - 10) < 1e-3, "edge point at radius 10");
    }

    [Fact]
    public void Inferer_ProbeAbsent_Fallback()
    {
        // A DLL without the instance exports throws EntryPointNotFoundException on the first call: the probe reads 0 (masks only).
        Assert.Equal(0, SegAiInferer.ProbeInstanceApi(() => throw new EntryPointNotFoundException("SEGAI_GetInstanceApiVersion")));
        Assert.Equal(1, SegAiInferer.ProbeInstanceApi(() => 1));
    }
}
