p='SegInstanceTests.cs'
s=open(p,encoding='utf-8',newline='').read()
crlf='\r\n' in s
s=s.replace('\r\n','\n')
new='''    [Fact]
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
    public void Converter_CircleSkippedInDetect()'''
a='    [Fact]\n    public void Converter_CircleSkippedInDetect()'
assert a in s
s=s.replace(a,new,1)
if crlf: s=s.replace('\n','\r\n')
open(p,'w',encoding='utf-8',newline='').write(s)

p='SegLabelRasterTests.cs'
s=open(p,encoding='utf-8',newline='').read()
crlf='\r\n' in s
s=s.replace('\r\n','\n')
a='    [Fact]\n    public void ClipToImage_drops_regions_and_holes'
new='''    [Fact]
    public void ClipToImage_drops_a_zero_area_sliver_on_the_border()
    {
        // The square touches the image only along the line x = 0.
        var regions = new List<SegAnnotation> { Square(1, -30, 10, 0, 40) };
        Assert.Empty(SegLabelRaster.ClipToImage(regions, 64, 64));
    }

'''+a
assert a in s
s=s.replace(a,new,1)
if crlf: s=s.replace('\n','\r\n')
open(p,'w',encoding='utf-8',newline='').write(s)
