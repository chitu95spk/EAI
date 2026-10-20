using System.Drawing;
using EasyAI.Desktop.ViewModels;
using EasyLabel.Ai.Onnx;
using EasyLabel.Core.Model;
using EasyLabel.SegAI.AutoLabel;

namespace EasyLabel.SegAI.Tests;

public sealed class AiSuggestionMapperTests
{
    private static AiProposal Polygon(float score = 0.9f, bool withHole = true)
    {
        PointF[] outer = { new PointF(0f, 0f), new PointF(40f, 0f), new PointF(40f, 40f), new PointF(0f, 40f) };
        IReadOnlyList<IReadOnlyList<PointF>> holes = withHole
            ? new IReadOnlyList<PointF>[] { new[] { new PointF(10f, 10f), new PointF(20f, 10f), new PointF(20f, 20f) } }
            : Array.Empty<IReadOnlyList<PointF>>();
        return new AiProposal(outer, holes, score, null, false, new RectangleF(0f, 0f, 40f, 40f));
    }

    private static AiProposal Detection(string label = "scratch")
    {
        RectangleF box = new RectangleF(10f, 20f, 30f, 15f);
        PointF[] corners = { new PointF(10f, 20f), new PointF(40f, 20f), new PointF(40f, 35f), new PointF(10f, 35f) };
        return new AiProposal(corners, Array.Empty<IReadOnlyList<PointF>>(), 0.7f, label, true, box);
    }

    [Fact]
    public void PolygonWithAHoleKeepsOuterHolesScoreAndClass()
    {
        SegAiSuggestion s = AiSuggestionMapper.ToSuggestion(Polygon(), "a.png", 1, "SAM2", "enc.onnx")!;

        Assert.Equal("a.png", s.Rel);
        Assert.Equal(1, s.ClassIndex);
        Assert.Equal(4, s.Outer.Length);
        Assert.Single(s.Holes);
        Assert.Equal(3, s.Holes[0].Length);
        Assert.Equal(0.9f, s.Score);
        Assert.Equal("polygon", s.ShapeType);
        Assert.Null(s.Label);
        Assert.Equal("SAM2 | enc.onnx", s.Model);

        LabelShape shape = LabelStepRules.ToShape(s, "OK");
        Assert.Equal("polygon", shape.ShapeType);
        Assert.Equal(4, shape.Points.Count);
        Assert.Equal("OK", shape.Label);
    }

    [Fact]
    public void BoxBecomesARectangleOfTwoPoints()
    {
        SegAiSuggestion s = AiSuggestionMapper.ToSuggestion(Detection(), "a.png", 0, "YOLO-World", "w.onnx")!;

        Assert.Equal("rectangle", s.ShapeType);
        Assert.Equal(4, s.Outer.Length);
        Assert.Empty(s.Holes);

        LabelShape shape = LabelStepRules.ToShape(s, "NG");
        Assert.Equal("rectangle", shape.ShapeType);
        Assert.Equal(2, shape.Points.Count);
        Assert.Equal(new double[] { 10, 20 }, shape.Points[0]);
        Assert.Equal(new double[] { 40, 35 }, shape.Points[1]);
    }

    [Fact]
    public void ModelClassGoesToTheProvenanceNotTheLabel()
    {
        SegAiSuggestion s = AiSuggestionMapper.ToSuggestion(Detection("dent"), "a.png", 0, "YOLO-World", "w.onnx")!;

        Assert.Null(s.Label);
        Assert.Equal("YOLO-World | w.onnx | dent", s.Model);
        Assert.Equal("NG", LabelStepRules.ToShape(s, "NG").Label);
    }

    [Fact]
    public void LabelWinsWhenGivenOtherwiseTheClassMappingApplies()
    {
        PointF[] outer = { new PointF(0f, 0f), new PointF(5f, 0f), new PointF(5f, 5f) };
        SegAiSuggestion plain = new SegAiSuggestion("a.png", 0, outer, Array.Empty<PointF[]>(), 0.5f);
        SegAiSuggestion named = plain with { Label = "scratch" };
        SegAiSuggestion blank = plain with { Label = "  " };

        Assert.Equal("NG", LabelStepRules.ToShape(plain, "NG").Label);
        Assert.Equal("scratch", LabelStepRules.ToShape(named, "NG").Label);
        Assert.Equal("NG", LabelStepRules.ToShape(blank, "NG").Label);
    }

    [Fact]
    public void ExistingFiveArgumentSuggestionsKeepTheirPolygonBehaviour()
    {
        PointF[] outer = { new PointF(0f, 0f), new PointF(5f, 0f), new PointF(5f, 5f) };
        SegAiSuggestion s = new SegAiSuggestion("a.png", 1, outer, Array.Empty<PointF[]>(), null);

        Assert.Equal("polygon", s.ShapeType);
        Assert.Null(s.Label);
        Assert.Null(s.Model);
        Assert.Equal("polygon", LabelStepRules.ToShape(s, "OK").ShapeType);
    }

    [Fact]
    public void ProvenanceUsesTheAiTextOrTheSegmentModel()
    {
        SegAiSuggestion ai = AiSuggestionMapper.ToSuggestion(Polygon(), "a.png", 0, "SAM2", "enc.onnx")!;
        PointF[] outer = { new PointF(0f, 0f), new PointF(5f, 0f), new PointF(5f, 5f) };
        SegAiSuggestion segment = new SegAiSuggestion("a.png", 0, outer, Array.Empty<PointF[]>(), 0.4f);

        ShapeProvenance aiProvenance = LabelStepRules.ToProvenance(ai, "seg.bin");
        Assert.Equal("model", aiProvenance.Source);
        Assert.Equal("SAM2 | enc.onnx", aiProvenance.Model);
        Assert.Equal(0.9, aiProvenance.Confidence!.Value, 3);
        Assert.Equal("seg.bin", LabelStepRules.ToProvenance(segment, "seg.bin").Model);
    }

    [Fact]
    public void DegenerateResultsAreSkipped()
    {
        AiProposal twoPoints = new AiProposal(new[] { new PointF(0f, 0f), new PointF(1f, 1f) }, Array.Empty<IReadOnlyList<PointF>>(), 0.5f, null, false, RectangleF.Empty);
        AiProposal emptyBox = new AiProposal(Array.Empty<PointF>(), Array.Empty<IReadOnlyList<PointF>>(), 0.5f, "x", true, new RectangleF(5f, 5f, 0f, 3f));

        Assert.Null(AiSuggestionMapper.ToSuggestion(twoPoints, "a.png", 0, "SAM2", "e.onnx"));
        Assert.Null(AiSuggestionMapper.ToSuggestion(emptyBox, "a.png", 0, "YOLO-World", "w.onnx"));
    }

    [Fact]
    public void HolesWithFewerThanThreePointsAreDropped()
    {
        PointF[] outer = { new PointF(0f, 0f), new PointF(40f, 0f), new PointF(40f, 40f) };
        IReadOnlyList<IReadOnlyList<PointF>> holes = new IReadOnlyList<PointF>[] { new[] { new PointF(1f, 1f), new PointF(2f, 2f) } };
        AiProposal proposal = new AiProposal(outer, holes, 0.5f, null, false, new RectangleF(0f, 0f, 40f, 40f));

        Assert.Empty(AiSuggestionMapper.ToSuggestion(proposal, "a.png", 0, "SAM2", "e.onnx")!.Holes);
    }

    [Fact]
    public void ImageKeyAndProviderTextAreStable()
    {
        Assert.Equal(@"C:\p\a.png|123|456", AiSuggestionMapper.ImageKey(@"C:\p\a.png", 123L, 456L));
        Assert.NotEqual(AiSuggestionMapper.ImageKey("a", 1L, 2L), AiSuggestionMapper.ImageKey("a", 2L, 2L));
        Assert.NotEqual(AiSuggestionMapper.ImageKey("a", 1L, 2L), AiSuggestionMapper.ImageKey("a", 1L, 3L));

        Assert.Equal("DirectML", AiSuggestionMapper.ProviderText("DirectML", null, "GPU unavailable, using CPU: {0}"));
        Assert.Equal("DirectML", AiSuggestionMapper.ProviderText("DirectML", " ", "GPU unavailable, using CPU: {0}"));
        Assert.Equal("GPU unavailable, using CPU: no adapter", AiSuggestionMapper.ProviderText("CPU", "no adapter", "GPU unavailable, using CPU: {0}"));
    }

    [Fact]
    public void CropCopiesTheBoxPixelsAndRemembersTheOffset()
    {
        // 4 x 3 image, pixel (x, y) = bytes (10*y + x) repeated for B, G, R.
        byte[] pixels = new byte[4 * 3 * 3];
        for (int y = 0; y < 3; y++)
        {
            for (int x = 0; x < 4; x++)
            {
                for (int c = 0; c < 3; c++) pixels[(y * 4 + x) * 3 + c] = (byte)(10 * y + x);
            }
        }

        AiImage image = new AiImage(pixels, 4, 3, "k");
        AiCrop crop = AiSuggestionMapper.Crop(image, new AiBox(1.2f, 1f, 2.5f, 2.9f));

        Assert.Equal(1, crop.OffsetX);
        Assert.Equal(1, crop.OffsetY);
        Assert.Equal(2, crop.Image.Width);
        Assert.Equal(2, crop.Image.Height);
        Assert.Equal(new byte[] { 11, 11, 11, 12, 12, 12, 21, 21, 21, 22, 22, 22 }, crop.Image.Pixels.Take(12).ToArray());
        Assert.StartsWith("k|crop:", crop.Image.CacheKey);
        Assert.NotEqual(image.CacheKey, crop.Image.CacheKey);
    }

    [Fact]
    public void CropStaysInsideTheImageAndIsNeverEmpty()
    {
        AiImage image = new AiImage(new byte[5 * 4 * 3], 5, 4, "k");

        AiCrop all = AiSuggestionMapper.Crop(image, new AiBox(-10f, -10f, 99f, 99f));
        Assert.Equal((0, 0, 5, 4), (all.OffsetX, all.OffsetY, all.Image.Width, all.Image.Height));

        AiCrop corner = AiSuggestionMapper.Crop(image, new AiBox(5f, 4f, 5f, 4f));
        Assert.Equal((4, 3, 1, 1), (corner.OffsetX, corner.OffsetY, corner.Image.Width, corner.Image.Height));
    }

    [Fact]
    public void OffsetMovesOuterHolesAndBoundsBackIntoTheFullImage()
    {
        AiProposal moved = AiSuggestionMapper.Offset(Polygon(), 100, 200);

        Assert.Equal(new PointF(100f, 200f), moved.Outer[0]);
        Assert.Equal(new PointF(110f, 210f), moved.Holes[0][0]);
        Assert.Equal(new RectangleF(100f, 200f, 40f, 40f), moved.Box);
        Assert.Equal(0.9f, moved.Score);

        AiProposal same = Detection();
        Assert.Same(same, AiSuggestionMapper.Offset(same, 0, 0));

        SegAiSuggestion s = AiSuggestionMapper.ToSuggestion(AiSuggestionMapper.Offset(Detection(), 5, 6), "a.png", 0, "YOLO-World", "w.onnx")!;
        Assert.Equal(new PointF(15f, 26f), s.Outer[0]);
    }
}
