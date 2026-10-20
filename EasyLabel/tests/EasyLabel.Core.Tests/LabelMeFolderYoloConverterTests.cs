using EasyLabel.Core.Convert;

namespace EasyLabel.Core.Tests;

/// <summary>
/// Golden tests of the JSON2YOLO.py / JsonToYoloObb.py rules. Every expected line was computed by hand from the scripts' formulas
/// (normalise by imageWidth/imageHeight, six decimals, clamp), not by running the converter.
/// </summary>
public sealed class LabelMeFolderYoloConverterTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "EasyLabel", "YoloFolder", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, true);
        }
        catch (IOException)
        {
        }
    }

    private string Input => Path.Combine(_root, "in");

    private string Output => Path.Combine(_root, "out");

    private void Put(string rel, string json)
    {
        string path = Path.Combine(Input, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, json);
    }

    private void BuildFixture()
    {
        Put("a.json", """
        {"imageWidth":200,"imageHeight":100,"shapes":[
          {"label":"OK","shape_type":"rectangle","points":[[10,20],[50,60]]},
          {"label":"NG","shape_type":"rectangle","points":[[190,90],[250,120]]},
          {"label":"NG","shape_type":"rectangle","points":[[300,10],[320,50]]},
          {"label":"OK","shape_type":"circle","points":[[10,10],[20,10]]},
          {"label":"XX","shape_type":"rectangle","points":[[1,1],[5,5]]},
          {"label":" OK ","shape_type":"polygon","points":[[20,10],[60,10],[60,50],[20,50]]},
          {"label":"NG","shape_type":"polygon","points":[[1,1],[5,5]]},
          {"label":"OK","shape_type":"oriented_rectangle","points":[[100,10],[120,30],[100,50],[80,30]]}
        ]}
        """);
        Put("sub/b.json", """{"imageWidth":100,"imageHeight":50,"shapes":[{"label":"NG","shape_type":"rectangle","points":[[0,0],[100,50]]}]}""");
        Put("sub/deep/empty.json", """{"imageWidth":10,"imageHeight":10,"shapes":[]}""");
        Put("noSize.json", """{"shapes":[{"label":"OK","shape_type":"rectangle","points":[[1,1],[2,2]]}]}""");
        Put("bad.json", "{ not json");
        Put(".hidden/x.json", """{"imageWidth":10,"imageHeight":10,"shapes":[]}""");
    }

    private LabelMeYoloResult Run(LabelMeYoloFormat format, Action<LabelMeFolderYoloRequest>? tweak = null)
    {
        LabelMeFolderYoloRequest request = new LabelMeFolderYoloRequest
        {
            InputDirectory = Input,
            OutputDirectory = Output,
            Format = format,
            ClassNames = new[] { "OK", "NG" },
        };
        tweak?.Invoke(request);
        return new LabelMeFolderYoloConverter().Run(request);
    }

    private string Read(string rel)
    {
        return File.ReadAllText(Path.Combine(Output, rel.Replace('/', Path.DirectorySeparatorChar)));
    }

    [Fact]
    public void DetectWritesClampedNormalisedBoxesAndCounters()
    {
        BuildFixture();
        LabelMeYoloResult result = Run(LabelMeYoloFormat.Detect);

        Assert.Equal("0 0.150000 0.400000 0.200000 0.400000\n1 0.975000 0.950000 0.050000 0.100000\n", Read("a.txt"));
        Assert.Equal("1 0.500000 0.500000 1.000000 1.000000\n", Read("sub/b.txt"));
        Assert.Equal(string.Empty, Read("sub/deep/empty.txt"));
        Assert.False(File.Exists(Path.Combine(Output, "noSize.txt")));
        Assert.False(File.Exists(Path.Combine(Output, "bad.txt")));
        Assert.False(Directory.Exists(Path.Combine(Output, ".hidden")));
        Assert.Equal("OK\nNG\n", Read("classes.txt"));

        Assert.Equal(5, result.FilesFound);
        Assert.Equal(3, result.FilesConverted);
        Assert.Equal(2, result.FilesFailed);
        Assert.Equal(3, result.ObjectsConverted);
        Assert.Equal(6, result.ObjectsSkipped);
        Assert.Equal(new[] { 1, 2 }, result.PerClassCounts.Select(p => p.Value).ToArray());
        Assert.True(result.ClassesFileWritten);
        Assert.Contains(result.Skips, s => s.File == "a.json" && s.ShapeNumber == 3 && s.Reason.Contains("no area"));
        Assert.Contains(result.Skips, s => s.ShapeNumber == 4 && s.ShapeType == "circle");
        Assert.Contains(result.Skips, s => s.ShapeNumber == 5 && s.Label == "XX" && s.Reason.Contains("class list"));
        Assert.Contains(result.FileErrors, e => e.StartsWith("noSize.json:"));
        Assert.Contains(result.FileErrors, e => e.StartsWith("bad.json:"));
    }

    [Fact]
    public void ObbWritesCornersMinAreaRectanglesAndKeepsZeroAreaRectangles()
    {
        BuildFixture();
        LabelMeYoloResult result = Run(LabelMeYoloFormat.Obb, r => r.WriteClassesFile = false);

        string expected =
            "0 0.050000 0.200000 0.250000 0.200000 0.250000 0.600000 0.050000 0.600000\n" +
            "1 0.950000 0.900000 1.000000 0.900000 1.000000 1.000000 0.950000 1.000000\n" +
            "1 1.000000 0.100000 1.000000 0.100000 1.000000 0.500000 1.000000 0.500000\n" +
            "0 0.100000 0.100000 0.300000 0.100000 0.300000 0.500000 0.100000 0.500000\n" +
            "0 0.500000 0.100000 0.600000 0.300000 0.500000 0.500000 0.400000 0.300000\n";
        Assert.Equal(expected, Read("a.txt"));
        Assert.Equal("1 0.000000 0.000000 1.000000 0.000000 1.000000 1.000000 0.000000 1.000000\n", Read("sub/b.txt"));
        Assert.Equal(string.Empty, Read("sub/deep/empty.txt"));
        Assert.False(File.Exists(Path.Combine(Output, "classes.txt")));

        Assert.Equal(3, result.FilesConverted);
        Assert.Equal(2, result.FilesFailed);
        Assert.Equal(6, result.ObjectsConverted);
        Assert.Equal(3, result.ObjectsSkipped);
        Assert.Equal(new[] { 3, 3 }, result.PerClassCounts.Select(p => p.Value).ToArray());
        Assert.False(result.ClassesFileWritten);
        Assert.Contains(result.Skips, s => s.ShapeNumber == 7 && s.Reason.Contains("fewer than 3"));
        Assert.Contains(result.Skips, s => s.ShapeNumber == 4 && s.Reason.Contains("unsupported"));
    }

    [Fact]
    public void ExpectedObjectsPerClassProducesOneWarningPerMismatchingFile()
    {
        BuildFixture();
        LabelMeYoloResult result = Run(LabelMeYoloFormat.Detect, r => r.ExpectedObjectsPerClass = new Dictionary<string, int> { ["OK"] = 1 });

        Assert.Equal(
            new[] { "sub/b.json: expected 1 'OK' objects, found 0", "sub/deep/empty.json: expected 1 'OK' objects, found 0" },
            result.Warnings.ToArray());
    }

    [Fact]
    public void RefusesNonEmptyOutputAndInputFolderUnlessOverwriteIsAllowed()
    {
        BuildFixture();
        Directory.CreateDirectory(Output);
        File.WriteAllText(Path.Combine(Output, "keep.txt"), "keep");

        Assert.Throws<LabelMeYoloException>(() => Run(LabelMeYoloFormat.Detect));
        Assert.Throws<LabelMeYoloException>(() => Run(LabelMeYoloFormat.Detect, r => r.OutputDirectory = Input));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(Output, "keep.txt")));

        LabelMeYoloResult result = Run(LabelMeYoloFormat.Detect, r => r.OverwriteExisting = true);
        Assert.Equal(3, result.FilesConverted);
        Assert.Equal("keep", File.ReadAllText(Path.Combine(Output, "keep.txt")));
    }

    [Fact]
    public void RequiresAClassList()
    {
        BuildFixture();
        Assert.Throws<LabelMeYoloException>(() => Run(LabelMeYoloFormat.Detect, r => r.ClassNames = new[] { " ", string.Empty }));
    }

    [Fact]
    public void ReadClassesFileTrimsAndSkipsEmptyLines()
    {
        Directory.CreateDirectory(Input);
        string path = Path.Combine(Input, "classes.txt");
        File.WriteAllText(path, "OK\r\n\r\n  NG \r\n");
        Assert.Equal(new[] { "OK", "NG" }, LabelMeFolderYoloConverter.ReadClassesFile(path).ToArray());
        Assert.Empty(LabelMeFolderYoloConverter.ReadClassesFile(Path.Combine(Input, "none.txt")));
    }
}
