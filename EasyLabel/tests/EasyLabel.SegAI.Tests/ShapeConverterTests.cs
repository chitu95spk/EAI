using EasyLabel.Core.Editing;
using EasyLabel.Core.Model;
using Newtonsoft.Json.Linq;

namespace EasyLabel.SegAI.Tests;

public sealed class ShapeConverterTests
{
    private static readonly string[] Targets = { "polygon", "rectangle", "oriented_rectangle", "circle", "point" };

    private static LabelShape Make(string type)
    {
        var s = new LabelShape { Label = "a", GroupId = 3, ShapeType = type };
        s.Flags["f"] = true;
        switch (type)
        {
            case "rectangle": s.Points = new List<double[]> { new[] { 10.0, 20.0 }, new[] { 110.0, 70.0 } }; break;
            case "oriented_rectangle":
                s.Points = new List<double[]> { new[] { 10.0, 20.0 }, new[] { 110.0, 20.0 }, new[] { 110.0, 70.0 }, new[] { 10.0, 70.0 } };
                s.Direction = 0; break;
            case "polygon": s.Points = new List<double[]> { new[] { 0.0, 0.0 }, new[] { 100.0, 0.0 }, new[] { 100.0, 50.0 }, new[] { 0.0, 50.0 } }; break;
            case "circle": s.Points = new List<double[]> { new[] { 50.0, 50.0 }, new[] { 80.0, 50.0 } }; break;
            case "point": s.Points = new List<double[]> { new[] { 5.0, 5.0 } }; break;
            case "line": s.Points = new List<double[]> { new[] { 0.0, 0.0 }, new[] { 40.0, 30.0 } }; break;
            case "linestrip": s.Points = new List<double[]> { new[] { 0.0, 0.0 }, new[] { 40.0, 0.0 }, new[] { 40.0, 30.0 } }; break;
        }
        return s;
    }

    private static string Snapshot(LabelShape s) =>
        s.ShapeType + "|" + string.Join(";", s.Points.Select(p => p[0] + "," + p[1])) + "|" + s.Direction + "|" + s.Label;

    [Theory]
    [InlineData("polygon"), InlineData("rectangle"), InlineData("oriented_rectangle"), InlineData("circle"),
     InlineData("point"), InlineData("line"), InlineData("linestrip"), InlineData("oriented_rectangle")]
    public void CanConvert_matches_Convert_for_every_pair(string from)
    {
        foreach (string to in Targets)
        {
            bool can = ShapeConverter.CanConvert(from, to);
            string norm = from == "rotation" ? "oriented_rectangle" : from;
            if (norm == to || norm == "point") Assert.False(can);
            else if (norm == "line") Assert.Equal(to == "rectangle" || to == "circle" || to == "point", can);
            else Assert.True(can);

            LabelShape src = Make(norm);
            src.ShapeType = from;
            ShapeConversionResult r = ShapeConverter.Convert(src, to);
            Assert.Equal(can, r.Shape != null);
            if (r.Shape != null)
            {
                Assert.Equal(to, r.Shape.ShapeType);
                Assert.Equal("a", r.Shape.Label);
                Assert.Equal(3, r.Shape.GroupId);
                Assert.True(r.Shape.Flags["f"]);
                Assert.Equal(r.Lossy, r.WarningKey != null);
            }
        }
    }

    [Fact]
    public void Original_is_never_mutated()
    {
        foreach (string from in new[] { "polygon", "rectangle", "oriented_rectangle", "circle", "linestrip" })
            foreach (string to in Targets)
            {
                LabelShape src = Make(from);
                string before = Snapshot(src);
                ShapeConversionResult r = ShapeConverter.Convert(src, to);
                Assert.Equal(before, Snapshot(src));
                if (r.Shape != null) Assert.NotSame(src, r.Shape);
                if (r.Shape != null) Assert.NotSame(src.Points[0], r.Shape.Points[0]);
            }
    }

    [Fact]
    public void Rectangle_to_rotation_and_back_keeps_bounds()
    {
        LabelShape rect = Make("rectangle");
        LabelShape rot = ShapeConverter.Convert(rect, "oriented_rectangle").Shape!;
        Assert.Equal(4, rot.Points.Count);
        Assert.Equal(0.0, rot.Direction!.Value, 6);
        LabelShape back = ShapeConverter.Convert(rot, "rectangle").Shape!;
        Assert.Equal(2, back.Points.Count);
        Assert.InRange(Math.Min(back.Points[0][0], back.Points[1][0]), 9, 11);
        Assert.InRange(Math.Min(back.Points[0][1], back.Points[1][1]), 19, 21);
        Assert.InRange(Math.Max(back.Points[0][0], back.Points[1][0]), 109, 111);
        Assert.InRange(Math.Max(back.Points[0][1], back.Points[1][1]), 69, 71);
    }

    [Fact]
    public void Rotated_input_recovers_angle_and_size()
    {
        double a = 30 * Math.PI / 180, c = Math.Cos(a), s = Math.Sin(a);
        double[] P(double x, double y) => new[] { 200 + x * c - y * s, 150 + x * s + y * c };
        var src = new LabelShape
        {
            ShapeType = "polygon",
            Points = new List<double[]> { P(-60, -20), P(60, -20), P(60, 20), P(-60, 20) }
        };
        LabelShape rot = ShapeConverter.Convert(src, "oriented_rectangle").Shape!;
        Assert.Equal(30.0, rot.Direction!.Value * 180 / Math.PI, 4);
        double w = Math.Sqrt(Math.Pow(rot.Points[1][0] - rot.Points[0][0], 2) + Math.Pow(rot.Points[1][1] - rot.Points[0][1], 2));
        double h = Math.Sqrt(Math.Pow(rot.Points[3][0] - rot.Points[0][0], 2) + Math.Pow(rot.Points[3][1] - rot.Points[0][1], 2));
        Assert.Equal(120, w, 4);
        Assert.Equal(40, h, 4);
    }

    [Fact]
    public void Oriented_rectangle_is_written_back_as_rotation()
    {
        LabelShape src = Make("oriented_rectangle");
        src.ShapeType = "oriented_rectangle";
        Assert.Equal("polygon", ShapeConverter.Convert(src, "polygon").Shape!.ShapeType);
        Assert.False(ShapeConverter.CanConvert("oriented_rectangle", "oriented_rectangle"));
        Assert.True(ShapeConverter.CanConvert("rectangle", "oriented_rectangle"));
    }

    [Fact]
    public void Circle_and_point_layouts()
    {
        LabelShape poly = Make("polygon");
        LabelShape circle = ShapeConverter.Convert(poly, "circle").Shape!;
        Assert.Equal(50, circle.Points[0][0], 6);
        Assert.Equal(25, circle.Points[0][1], 6);
        Assert.Equal(Math.Sqrt(50 * 50 + 25 * 25), circle.Points[1][0] - circle.Points[0][0], 6);
        LabelShape pt = ShapeConverter.Convert(poly, "point").Shape!;
        Assert.Equal(new[] { 50.0, 25.0 }, pt.Points[0]);
        Assert.Equal(32, ShapeConverter.Convert(Make("circle"), "polygon").Shape!.Points.Count);
    }

    [Fact]
    public void Lossy_flags()
    {
        Assert.False(ShapeConverter.Convert(Make("rectangle"), "polygon").Lossy);
        Assert.False(ShapeConverter.Convert(Make("rectangle"), "oriented_rectangle").Lossy);
        Assert.False(ShapeConverter.Convert(Make("oriented_rectangle"), "rectangle").Lossy);
        Assert.False(ShapeConverter.Convert(Make("polygon"), "rectangle").Lossy);
        Assert.True(ShapeConverter.Convert(Make("rectangle"), "point").Lossy);
        Assert.True(ShapeConverter.Convert(Make("circle"), "polygon").Lossy);
        Assert.True(ShapeConverter.Convert(Make("circle"), "rectangle").Lossy);
        Assert.True(ShapeConverter.Convert(Make("circle"), "oriented_rectangle").Lossy);
        Assert.Equal(ShapeConverter.LossyCircleKey, ShapeConverter.Convert(Make("circle"), "rectangle").WarningKey);
        Assert.True(ShapeConverter.Convert(Make("rectangle"), "circle").Lossy);
        Assert.True(ShapeConverter.Convert(Make("oriented_rectangle"), "circle").Lossy);
        Assert.True(ShapeConverter.Convert(Make("line"), "rectangle").Lossy);
        Assert.True(ShapeConverter.Convert(Make("linestrip"), "polygon").Lossy);

        LabelShape hex = Make("polygon");
        hex.Points = Enumerable.Range(0, 6).Select(i => new[] { 50 + 40 * Math.Cos(i * Math.PI / 3), 50 + 40 * Math.Sin(i * Math.PI / 3) }).ToList();
        foreach (string to in new[] { "rectangle", "oriented_rectangle", "circle", "point" })
            Assert.True(ShapeConverter.Convert(hex, to).Lossy);
        Assert.False(ShapeConverter.Convert(hex, "polygon").Lossy);

        LabelShape holed = Make("rectangle");
        holed.EasyAi = new JObject { ["holes"] = new JArray(new JArray(new JArray(1, 1), new JArray(2, 1), new JArray(2, 2))) };
        Assert.False(ShapeConverter.Convert(holed, "polygon").Lossy);
        Assert.NotNull(ShapeConverter.Convert(holed, "polygon").Shape!.EasyAi);
        Assert.Null(ShapeConverter.Convert(holed, "oriented_rectangle").Shape!.EasyAi);

        LabelShape holedPoly = Make("polygon");
        holedPoly.EasyAi = new JObject { ["holes"] = new JArray(new JArray(new JArray(1, 1), new JArray(2, 1), new JArray(2, 2))) };
        Assert.False(ShapeConverter.Convert(holedPoly, "polygon").Lossy);
        Assert.True(ShapeConverter.Convert(holedPoly, "rectangle").Lossy);
    }
}
