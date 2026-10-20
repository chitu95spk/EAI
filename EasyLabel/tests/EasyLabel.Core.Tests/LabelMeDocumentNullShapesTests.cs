using EasyLabel.Core.Model;
using Newtonsoft.Json;

namespace EasyLabel.Core.Tests;

public sealed class LabelMeDocumentNullShapesTests
{
    [Fact]
    public void ShapesNullInJsonLoadsAsEmptyList()
    {
        const string json = "{\"version\":\"5.5.0\",\"flags\":{},\"shapes\":null,\"imagePath\":\"a.png\",\"imageHeight\":3,\"imageWidth\":2}";

        LabelMeDocument? document = JsonConvert.DeserializeObject<LabelMeDocument>(json);

        Assert.NotNull(document);
        Assert.NotNull(document!.Shapes);
        Assert.Empty(document.Shapes);
    }

    [Fact]
    public void MissingShapesKeyLoadsAsEmptyList()
    {
        const string json = "{\"version\":\"5.5.0\",\"flags\":{},\"imagePath\":\"a.png\",\"imageHeight\":3,\"imageWidth\":2}";

        LabelMeDocument? document = JsonConvert.DeserializeObject<LabelMeDocument>(json);

        Assert.NotNull(document);
        Assert.NotNull(document!.Shapes);
        Assert.Empty(document.Shapes);
    }

    [Fact]
    public void AssigningNullShapesStoresEmptyList()
    {
        LabelMeDocument document = new();

        document.Shapes = null!;

        Assert.NotNull(document.Shapes);
        Assert.Empty(document.Shapes);
    }

    [Fact]
    public void NormalDocumentRoundTripKeepsShapes()
    {
        LabelMeDocument original = new()
        {
            ImagePath = "a.png",
            ImageWidth = 2,
            ImageHeight = 3
        };
        LabelShape shape = new() { Label = "gear", GroupId = 7 };
        shape.Points.Add(new[] { 1.0, 2.0 });
        shape.Points.Add(new[] { 3.5, 4.5 });
        original.Shapes.Add(shape);

        string json = JsonConvert.SerializeObject(original);
        LabelMeDocument? roundTripped = JsonConvert.DeserializeObject<LabelMeDocument>(json);

        Assert.NotNull(roundTripped);
        Assert.Equal(original.Version, roundTripped!.Version);
        Assert.Equal(original.ImagePath, roundTripped.ImagePath);
        Assert.Equal(original.ImageWidth, roundTripped.ImageWidth);
        Assert.Equal(original.ImageHeight, roundTripped.ImageHeight);
        Assert.Single(roundTripped.Shapes);
        Assert.Equal("gear", roundTripped.Shapes[0].Label);
        Assert.Equal(7, roundTripped.Shapes[0].GroupId);
        Assert.Equal(2, roundTripped.Shapes[0].Points.Count);
        Assert.Equal(new[] { 3.5, 4.5 }, roundTripped.Shapes[0].Points[1]);
    }
}
