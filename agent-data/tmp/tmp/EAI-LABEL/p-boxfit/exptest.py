p='ExportServiceTests.cs'
s=open(p,encoding='utf-8',newline='').read()
crlf='\r\n' in s
s=s.replace('\r\n','\n')
new='''    private static string BuildCutProject()
    {
        string root = Scratch("cut");
        File.WriteAllText(Path.Combine(root, "classes.txt"), "NG\nOK\n");
        string a = Path.Combine(root, "a.png");
        WriteImage(a, 0);
        WriteJson(
            a,
            ImageModeFlags.Train,
            Rect("NG", 10, 10, 50, 50),          // inside
            Rect("NG", -20, 10, 30, 50),         // partly outside: visible x 0..30
            Rect("OK", -90, 10, 5, 50),          // mostly outside: visible x 0..5
            Rect("OK", -50, 10, -10, 50),        // fully outside
            new LabelShape
            {
                Label = "OK", ShapeType = "oriented_rectangle",
                Points = { new[] { 80.0, 60 }, new[] { 120.0, 60 }, new[] { 120.0, 70 }, new[] { 80.0, 70 } },
            });
        return root;
    }

    [Fact]
    public void YoloDetect_clips_boxes_cut_by_the_border_and_skips_the_ones_outside()
    {
        ExportResult result = Run(BuildCutProject(), ExportFormat.YoloDetect, ExportSet.All, out string output);

        string[] lines = Lines(Path.Combine(output, "labels", "train", "a.txt"));
        Assert.Equal(4, lines.Length);
        Assert.All(lines.SelectMany(Numbers).Skip(0), v => Assert.InRange(v, 0.0, 1.0));
        double[] inside = Numbers(lines[0]);
        AssertClose(0.3, inside[1]);                       // unchanged by the clipping
        double[] partly = Numbers(lines[1]);
        AssertClose(0.15, partly[1]);                      // x 0..30 -> centre 15 / 100
        AssertClose(0.3, partly[3]);                       // width 30 / 100
        double[] mostly = Numbers(lines[2]);
        AssertClose(0.025, mostly[1]);                     // x 0..5
        Assert.Contains(result.Skips, s => !s.IsWarning && s.Reason.Contains("outside the image"));
        Assert.Equal(1, result.ShapesSkipped);
    }

    [Fact]
    public void YoloSegment_writes_the_visible_polygon_of_an_oriented_box_cut_by_the_border()
    {
        Run(BuildCutProject(), ExportFormat.YoloSegment, ExportSet.All, out string output);

        string[] lines = Lines(Path.Combine(output, "labels", "train", "a.txt"));
        double[] last = Numbers(lines[^1]);
        Assert.All(last.Skip(1), v => Assert.InRange(v, 0.0, 1.0));
        Assert.Equal(1 + 8, last.Length); // class + 4 clipped corners
        AssertClose(1.0, last.Skip(1).Where((v, i) => i % 2 == 0).Max()); // reaches the right border
    }

    [Fact]
    public void Voc_and_Coco_stay_inside_the_image_for_boxes_cut_by_the_border()
    {
        string root = BuildCutProject();
        Run(root, ExportFormat.PascalVoc, ExportSet.All, out string vocOut);
        XDocument xml = XDocument.Load(Directory.GetFiles(Path.Combine(vocOut, "annotations"), "*.xml").Single());
        foreach (XElement box in xml.Descendants("bndbox"))
        {
            Assert.InRange(double.Parse(box.Element("xmin")!.Value, CultureInfo.InvariantCulture), 0, W);
            Assert.InRange(double.Parse(box.Element("xmax")!.Value, CultureInfo.InvariantCulture), 0, W);
        }

        Assert.Equal(4, xml.Descendants("object").Count());

        Run(root, ExportFormat.CocoInstances, ExportSet.All, out string cocoOut);
        string cocoFile = Directory.GetFiles(cocoOut, "*.json", SearchOption.AllDirectories).First(f => File.ReadAllText(f).Contains("\\"annotations\\""));
        JArray annotations = (JArray)JObject.Parse(File.ReadAllText(cocoFile))["annotations"]!;
        Assert.Equal(4, annotations.Count);
        foreach (JToken a in annotations)
        {
            double[] bbox = a["bbox"]!.Select(v => (double)v).ToArray();
            Assert.True(bbox[0] >= 0 && bbox[1] >= 0 && bbox[0] + bbox[2] <= W + 1e-6 && bbox[1] + bbox[3] <= H + 1e-6);
        }
    }

    private static ExportResult Run(string root, ExportFormat format, ExportSet set, out string output)
    {
        output = OutDir();
        return Run(root, format, set, output);
    }

    [Fact]
    public void YoloDetect_writes_split_files_normalised_lines_and_reports_skipped_shapes()'''
a='    [Fact]\n    public void YoloDetect_writes_split_files_normalised_lines_and_reports_skipped_shapes()'
assert a in s
s=s.replace(a,new,1)
if crlf: s=s.replace('\n','\r\n')
open(p,'w',encoding='utf-8',newline='').write(s)
