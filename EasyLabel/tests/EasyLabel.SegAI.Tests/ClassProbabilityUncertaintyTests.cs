using EasyLabel.SegAI.Inspection;

namespace EasyLabel.SegAI.Tests;

public sealed class ClassProbabilityUncertaintyTests
{
    [Theory]
    [InlineData(0.20, UncertaintyBucket.Sure)]
    [InlineData(0.23, UncertaintyBucket.Doubtful)]
    [InlineData(0.35, UncertaintyBucket.Unsure)]
    [InlineData(0.50, UncertaintyBucket.Unsure)]
    [InlineData(0.65, UncertaintyBucket.Unsure)]
    [InlineData(0.77, UncertaintyBucket.Doubtful)]
    [InlineData(0.80, UncertaintyBucket.Sure)]
    public void Band_edges_follow_the_documented_probabilities(double p, UncertaintyBucket expected)
    {
        ImageUncertainty? u = ImageUncertaintyCalculator.FromClassProbability(p);
        Assert.NotNull(u);
        Assert.Equal(expected, u!.Value.Bucket);
        Assert.Equal(UncertaintySignal.ClassProbability, u.Value.Signal);
    }

    [Fact]
    public void Score_is_one_at_the_midpoint_and_one_tenth_at_the_doubtful_edge()
    {
        Assert.Equal(1.0, (double)FromOrFail(0.5).Score, 6);
        Assert.Equal(0.1, (double)FromOrFail(0.23).Score, 6);
        Assert.Equal(0.5, (double)FromOrFail(0.35).Score, 6);
    }

    [Fact]
    public void Nan_is_unknown_and_out_of_range_values_are_clamped()
    {
        Assert.Null(ImageUncertaintyCalculator.FromClassProbability(double.NaN));
        Assert.Equal(UncertaintyBucket.Sure, FromOrFail(-3.0).Bucket);
        Assert.Equal(0.0, (double)FromOrFail(1.5).Score, 6);
    }

    [Fact]
    public void Signal_value_is_appended_as_four()
    {
        Assert.Equal(4, (int)UncertaintySignal.ClassProbability);
        Assert.Equal(3, (int)UncertaintySignal.BoxConfidence);
    }

    private static ImageUncertainty FromOrFail(double p)
    {
        ImageUncertainty? u = ImageUncertaintyCalculator.FromClassProbability(p);
        Assert.NotNull(u);
        return u!.Value;
    }
}
