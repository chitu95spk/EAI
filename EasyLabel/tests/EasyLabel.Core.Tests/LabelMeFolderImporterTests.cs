using EasyLabel.Core.Import;
using EasyLabel.Core.Io;
using EasyLabel.Core.Model;

namespace EasyLabel.Core.Tests;

public sealed class LabelMeFolderImporterTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "EasyLabel", "ImportLabelMe", Guid.NewGuid().ToString("N"));

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

    private string Project => Path.Combine(_root, "proj");

    private string Source => Path.Combine(_root, "src");

    private void Put(string baseDir, string rel, string text)
    {
        string path = Path.Combine(baseDir, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private void Build()
    {
        Put(Project, "classes.txt", "NG\nOK\n");
        Put(Project, "p1.jpg", "OLD");
        Put(Source, "p1.jpg", "IMG1");
        Put(Source, "p1.json", """
        {"version":"5.5.0","flags":{},"customKey":7,"shapes":[
          {"label":"NG","points":[[1,1],[5,5]],"group_id":null,"shape_type":"rectangle","flags":{},"extraShapeKey":"keep"},
          {"label":"Scratch","points":[[3,3],[4,3]],"group_id":null,"shape_type":"circle","flags":{}},
          {"label":"Scratch","points":[[3,3]],"group_id":null,"shape_type":"point","flags":{}}],
         "imagePath":"p1.jpg","imageData":null,"imageHeight":10,"imageWidth":10}
        """);
        Put(Source, "sub/p2.png", "IMG2");
        Put(Source, "sub/p2.json", """
        {"version":"5.5.0","flags":{},"shapes":[{"label":"OK","points":[[1,1],[5,1],[5,5]],"group_id":null,"shape_type":"polygon","flags":{}}],
         "imagePath":"p2.png","imageData":null,"imageHeight":10,"imageWidth":10}
        """);
        Put(Source, "miss.json", """{"version":"5.5.0","flags":{},"shapes":[],"imagePath":"missing.jpg","imageData":null,"imageHeight":10,"imageWidth":10}""");
        Put(Source, "bad.json", "{ not json");
    }

    [Fact]
    public void PreviewFindsImagesInSiblingImagesFolderAndExplicitFolder()
    {
        Put(_root, "data/images/a.png", "IMG");
        Put(_root, "data/json/a.json", """{"version":"5.5.0","flags":{},"shapes":[],"imagePath":"..\\images\\a.png","imageData":null,"imageHeight":10,"imageWidth":10}""");
        Put(_root, "data/json/escape.json", """{"version":"5.5.0","flags":{},"shapes":[],"imagePath":"..\\..\\secret.png","imageData":null,"imageHeight":10,"imageWidth":10}""");
        Put(_root, "secret.png", "X");
        string json = Path.Combine(_root, "data", "json");

        LabelMeImportPreview auto = new LabelMeFolderImporter(Project).Preview(json);
        Assert.Equal(1, auto.ImagesFound);
        Assert.Equal(1, auto.ImagesMissing);
        Assert.Equal("a.png", auto.Items.First(i => i.CanImport).TargetRel);

        Put(_root, "other/a.png", "IMG");
        LabelMeImportPreview explicitFolder = new LabelMeFolderImporter(Project).Preview(json, Path.Combine(_root, "other"));
        Assert.Equal(1, explicitFolder.ImagesFound);
    }

    [Fact]
    public void PreviewReportsCountsLabelsAndConflictsWithoutWriting()
    {
        Build();
        LabelMeImportPreview preview = new LabelMeFolderImporter(Project).Preview(Source);

        Assert.Equal(4, preview.JsonFiles);
        Assert.Equal(1, preview.InvalidJson);
        Assert.Equal(2, preview.ImagesFound);
        Assert.Equal(1, preview.ImagesMissing);
        Assert.Equal(1, preview.Conflicts);
        Assert.Equal(4, preview.TotalShapes);
        Assert.Equal(1, preview.ShapesByKind["rectangle"]);
        Assert.Equal(1, preview.ShapesByKind["circle"]);
        Assert.Equal(1, preview.ShapesByKind["point"]);
        Assert.Equal(1, preview.ShapesByKind["polygon"]);
        Assert.Equal(new[] { "Scratch" }, preview.NewLabels.ToArray());
        Assert.Contains("NG", preview.ExistingLabels);
        Assert.Contains("OK", preview.ExistingLabels);
        Assert.Contains(preview.Items, i => i.SourceRel == "p1.json" && i.TargetRel == "p1.jpg" && i.Conflict);
        Assert.Contains(preview.Items, i => i.SourceRel == "sub/p2.json" && i.TargetRel == "sub/p2.png" && !i.Conflict);
        Assert.Contains(preview.Items, i => i.SourceRel == "miss.json" && i.ImageMissing);

        Assert.Equal("NG\nOK\n", File.ReadAllText(Path.Combine(Project, "classes.txt")));
        Assert.False(Directory.Exists(Path.Combine(Project, "sub")));
        Assert.False(File.Exists(Path.Combine(Project, "p1.json")));
    }

    [Fact]
    public void SkipIsTheSafeModeAndOnlyImportedFilesExtendClasses()
    {
        Build();
        LabelMeFolderImporter importer = new LabelMeFolderImporter(Project);
        LabelMeImportResult result = importer.Apply(importer.Preview(Source), LabelMeImportConflictMode.Skip);

        Assert.Equal(1, result.Imported);
        Assert.Equal(new[] { "sub/p2.png" }, result.ImportedImages.ToArray());
        Assert.Equal(2, result.Skipped);
        Assert.Equal(1, result.Failed);
        Assert.Equal("OLD", File.ReadAllText(Path.Combine(Project, "p1.jpg")));
        Assert.False(File.Exists(Path.Combine(Project, "p1.json")));
        Assert.Equal("IMG2", File.ReadAllText(Path.Combine(Project, "sub", "p2.png")));
        Assert.Equal("p2.png", new LabelMeIO().Read(Path.Combine(Project, "sub", "p2.json")).ImagePath);
        Assert.Empty(result.ClassesAdded);
        Assert.Equal("NG\nOK\n", File.ReadAllText(Path.Combine(Project, "classes.txt")));
        Assert.Empty(Directory.GetFiles(Path.Combine(Project, "sub"), ".*"));
    }

    [Fact]
    public void ReplaceOverwritesKeepsUnknownKeysAndAppendsNewLabels()
    {
        Build();
        LabelMeFolderImporter importer = new LabelMeFolderImporter(Project);
        LabelMeImportResult result = importer.Apply(importer.Preview(Source), LabelMeImportConflictMode.Replace);

        Assert.Equal(2, result.Imported);
        Assert.Equal(1, result.Replaced);
        Assert.Equal("IMG1", File.ReadAllText(Path.Combine(Project, "p1.jpg")));
        string json = File.ReadAllText(Path.Combine(Project, "p1.json"));
        Assert.Contains("\"customKey\"", json);
        Assert.Contains("\"extraShapeKey\"", json);
        LabelMeDocument document = new LabelMeIO().Read(Path.Combine(Project, "p1.json"));
        Assert.Equal("p1.jpg", document.ImagePath);
        Assert.Equal(3, document.Shapes.Count);
        Assert.Equal(new[] { "Scratch" }, result.ClassesAdded.ToArray());
        Assert.Equal("NG\nOK\nScratch\n", File.ReadAllText(Path.Combine(Project, "classes.txt")).Replace("\r\n", "\n"));
    }

    [Fact]
    public void RenameImportsUnderAFreeNameAndLeavesTheOriginal()
    {
        Build();
        LabelMeFolderImporter importer = new LabelMeFolderImporter(Project);
        LabelMeImportResult result = importer.Apply(importer.Preview(Source), LabelMeImportConflictMode.Rename);

        Assert.Equal(2, result.Imported);
        Assert.Equal(1, result.Renamed);
        Assert.Equal("OLD", File.ReadAllText(Path.Combine(Project, "p1.jpg")));
        Assert.Equal("IMG1", File.ReadAllText(Path.Combine(Project, "p1_1.jpg")));
        Assert.Equal("p1_1.jpg", new LabelMeIO().Read(Path.Combine(Project, "p1_1.json")).ImagePath);
        Assert.Contains("p1_1.jpg", result.ImportedImages);
    }

    [Fact]
    public void ConflictsAreCheckedAgainTheMomentOfTheWrite()
    {
        Build();
        LabelMeFolderImporter importer = new LabelMeFolderImporter(Project);
        LabelMeImportPreview preview = importer.Preview(Source);
        Put(Project, "sub/p2.png", "LATE");

        LabelMeImportResult result = importer.Apply(preview, LabelMeImportConflictMode.Skip);

        Assert.Equal(0, result.Imported);
        Assert.Equal("LATE", File.ReadAllText(Path.Combine(Project, "sub", "p2.png")));
        Assert.False(File.Exists(Path.Combine(Project, "sub", "p2.json")));
    }

    [Fact]
    public void ReplaceRefusesAnImageWithTheSameNameAndAnotherExtension()
    {
        Build();
        File.Delete(Path.Combine(Project, "p1.jpg"));
        Put(Project, "p1.png", "OTHER");
        LabelMeFolderImporter importer = new LabelMeFolderImporter(Project);
        LabelMeImportResult result = importer.Apply(importer.Preview(Source), LabelMeImportConflictMode.Replace);

        Assert.Equal(1, result.Imported);
        Assert.Equal("OTHER", File.ReadAllText(Path.Combine(Project, "p1.png")));
        Assert.False(File.Exists(Path.Combine(Project, "p1.jpg")));
        Assert.Contains(result.Reasons, r => r.StartsWith("p1.json:"));
    }

    [Fact]
    public void SourceFolderInsideTheProjectIsRefused()
    {
        Put(Project, "classes.txt", "NG\n");
        Put(Project, "inner/a.json", "{\"shapes\":[],\"imageWidth\":10,\"imageHeight\":10}");
        Assert.Throws<InvalidOperationException>(() => new LabelMeFolderImporter(Project).Preview(Path.Combine(Project, "inner")));
        Assert.Throws<InvalidOperationException>(() => new LabelMeFolderImporter(Project).Preview(Project));
    }

    [Fact]
    public void ImagePathPointingOutsideTheSourceFolderIsNotImported()
    {
        Put(Project, "classes.txt", "NG\n");
        Put(_root, "outside/secret.png", "SECRET");
        Put(Source, "a.json", "{\"imagePath\":\"../outside/secret.png\",\"imageWidth\":10,\"imageHeight\":10,\"shapes\":[{\"label\":\"NG\",\"points\":[[1,1],[5,5]],\"shape_type\":\"rectangle\",\"flags\":{}}]}");
        LabelMeImportPreview preview = new LabelMeFolderImporter(Project).Preview(Source);
        LabelMeImportItem item = Assert.Single(preview.Items);
        Assert.Null(item.SourceImage);
        LabelMeImportResult result = new LabelMeFolderImporter(Project).Apply(preview, LabelMeImportConflictMode.Skip);
        Assert.Equal(0, result.Imported);
        Assert.False(File.Exists(Path.Combine(Project, "secret.png")));
    }
}
