using EasyLabel.Ai.Onnx;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace EasyLabel.Ai.Onnx.Tests;

public class Sam2Tests
{
    [Fact]
    public void PreprocessIdentitySizeConvertsBgrToNormalizedRgb()
    {
        // 2 wide, 3 high, BGR. Pixel (x=1,y=2) = B10 G20 R30.
        var bgr = new byte[2 * 3 * 3];
        int at = (2 * 2 + 1) * 3;
        bgr[at] = 10; bgr[at + 1] = 20; bgr[at + 2] = 30;

        DenseTensor<float> t = Sam2Tensors.Preprocess(bgr, 2, 3, 2, 3);

        Assert.Equal(new[] { 1, 3, 3, 2 }, t.Dimensions.ToArray());
        Assert.Equal((30f / 255f - 0.485f) / 0.229f, t[0, 0, 2, 1], 4);
        Assert.Equal((20f / 255f - 0.456f) / 0.224f, t[0, 1, 2, 1], 4);
        Assert.Equal((10f / 255f - 0.406f) / 0.225f, t[0, 2, 2, 1], 4);
        Assert.Equal((0f - 0.485f) / 0.229f, t[0, 0, 0, 0], 4);
    }

    [Fact]
    public void PreprocessUpscaleKeepsCornersAndStaysInRange()
    {
        var bgr = new byte[2 * 3 * 3];
        for (int i = 0; i < bgr.Length; i++) bgr[i] = (byte)(i * 10);

        DenseTensor<float> t = Sam2Tensors.Preprocess(bgr, 2, 3, 4, 6);

        // Output corner (0,0) samples source pixel (0,0): R = bgr[2].
        Assert.Equal((bgr[2] / 255f - 0.485f) / 0.229f, t[0, 0, 0, 0], 4);
        // Last pixel samples the last source pixel: R = bgr[(2*3-1)*3+2].
        Assert.Equal((bgr[(2 * 3 - 1) * 3 + 2] / 255f - 0.485f) / 0.229f, t[0, 0, 5, 3], 4);
        foreach (float v in t.ToArray()) Assert.InRange(v, -2.2f, 2.7f);
    }

    [Fact]
    public void PromptPointsAreScaledAndPaddedWhenThereIsNoBox()
    {
        var prompt = new AiPrompt(new[] { new AiPoint(100, 50, true), new AiPoint(200, 20, false) });

        (float[] coords, float[] labels, int count) = Sam2Tensors.BuildPrompt(prompt, 2048, 512);

        Assert.Equal(3, count);
        Assert.Equal(new[] { 50f, 100f, 100f, 40f, 0f, 0f }, coords);
        Assert.Equal(new[] { 1f, 0f, -1f }, labels);
    }

    [Fact]
    public void PromptBoxBecomesLabels2And3WithoutPadding()
    {
        var prompt = new AiPrompt(new[] { new AiPoint(1024, 512, true) }, new AiBox(30, 60, 10, 20));

        (float[] coords, float[] labels, int count) = Sam2Tensors.BuildPrompt(prompt, 2048, 512);

        Assert.Equal(3, count);
        Assert.Equal(new[] { 2f, 3f, 1f }, labels);
        Assert.Equal(new[] { 5f, 40f, 15f, 120f, 512f, 1024f }, coords);
    }

    [Fact]
    public void BestMaskIsTheHighestIou()
    {
        Assert.Equal(2, Sam2Tensors.PickBest(new[] { 0.2f, 0.5f, 0.9f, 0.9f }));
    }

    [Fact]
    public void LogitsAreThresholdedAtZero()
    {
        byte[] mask = Sam2Tensors.LogitsToMask(new[] { 1f, -1f, 0f, 0.5f }, 0, 2, 2, 2, 2);

        Assert.Equal(new byte[] { 255, 0, 0, 255 }, mask);
    }

    [Fact]
    public void LogitsAreResizedToTheImageBeforeThresholding()
    {
        // Top row +5, bottom row -5, doubled to 4x4: rows are +, +(interpolated 2.5), -(interpolated -2.5), -.
        byte[] mask = Sam2Tensors.LogitsToMask(new[] { 5f, 5f, -5f, -5f }, 0, 2, 2, 4, 4);

        Assert.Equal(16, mask.Length);
        for (int x = 0; x < 4; x++)
        {
            Assert.Equal(255, mask[0 * 4 + x]);
            Assert.Equal(255, mask[1 * 4 + x]);
            Assert.Equal(0, mask[2 * 4 + x]);
            Assert.Equal(0, mask[3 * 4 + x]);
        }
    }

    [Fact]
    public void InterfaceMatchesTypicalSamExporterNames()
    {
        Sam2Interface i = Sam2Interface.Match(
            new[] { "image" },
            new[] { "high_res_feats_0", "high_res_feats_1", "image_embed" },
            new[] { "image_embed", "high_res_feats_0", "high_res_feats_1", "point_coords", "point_labels", "mask_input", "has_mask_input", "orig_im_size" },
            new[] { "masks", "iou_predictions", "low_res_masks" });

        Assert.Equal("image", i.EncoderImageInput);
        Assert.Equal("mask_input", i.MaskInput);
        Assert.Equal("has_mask_input", i.HasMaskInput);
        Assert.Equal("orig_im_size", i.OrigImSize);
        Assert.Equal("masks", i.Masks);
        Assert.Equal("iou_predictions", i.IouPredictions);
    }

    [Fact]
    public void InterfaceMatchesBySubstringAndOrderIndependently()
    {
        Sam2Interface i = Sam2Interface.Match(
            new[] { "pixel_values" },
            new[] { "enc_image_embeddings", "enc_high_res_feats_1", "enc_high_res_feats_0" },
            new[] { "has_mask_input_flag", "mask_input_lowres", "labels_point_labels", "coords_point_coords", "dec_image_embed", "dec_high_res_feats_0", "dec_high_res_feats_1" },
            new[] { "iou_scores", "out_masks" });

        Assert.Equal("enc_high_res_feats_0", i.EncoderHighRes0);
        Assert.Equal("mask_input_lowres", i.MaskInput);
        Assert.Equal("has_mask_input_flag", i.HasMaskInput);
        Assert.Null(i.OrigImSize);
        Assert.Equal("out_masks", i.Masks);
    }

    [Fact]
    public void InterfaceMismatchNamesTheProblem()
    {
        var ex = Assert.Throws<AiModelException>(() => Sam2Interface.Match(
            new[] { "image", "extra" },
            new[] { "high_res_feats_0", "image_embed" },
            new[] { "image_embed", "high_res_feats_0", "high_res_feats_1", "point_coords", "point_labels", "mask_input", "has_mask_input", "weird_input" },
            new[] { "masks", "iou_predictions" }));

        Assert.StartsWith("Unsupported SAM2 ONNX interface:", ex.Message);
        Assert.Contains("high_res_feats_1", ex.Message);
        Assert.Contains("weird_input", ex.Message);
        Assert.Contains("exactly one image input", ex.Message);
    }

    [Fact]
    public void EmbeddingCacheEvictsTheLeastRecentlyUsed()
    {
        var cache = new EmbeddingCache(2);
        cache.Add("a", Embedding());
        cache.Add("b", Embedding());
        Assert.True(cache.TryGet("a", out _)); // a becomes most recent
        cache.Add("c", Embedding());

        Assert.Equal(2, cache.Count);
        Assert.True(cache.TryGet("a", out _));
        Assert.True(cache.TryGet("c", out _));
        Assert.False(cache.TryGet("b", out _));
        Assert.Equal("img|enc.onnx|42", EmbeddingCache.MakeKey("img", "enc.onnx", 42));
    }

    [Fact]
    public void EmbeddingCacheDefaultsToFourAndSurvivesParallelUse()
    {
        var cache = new EmbeddingCache();
        Parallel.For(0, 200, i =>
        {
            cache.Add("k" + (i % 9), Embedding());
            cache.TryGet("k" + (i % 5), out _);
        });

        Assert.Equal(EmbeddingCache.DefaultCapacity, cache.Count);
    }

    private static Sam2Embedding Embedding()
        => new Sam2Embedding(new DenseTensor<float>(new[] { 1 }), new DenseTensor<float>(new[] { 1 }), new DenseTensor<float>(new[] { 1 }));
}
