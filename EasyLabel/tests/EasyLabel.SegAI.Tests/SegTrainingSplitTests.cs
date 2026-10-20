using EasyLabel.SegAI.Data;
using EasyLabel.SegAI.Training;

namespace EasyLabel.SegAI.Tests;

public sealed class SegTrainingSplitTests
{
    private static SegSample Labelled(string name, byte mode) => new()
    {
        ImagePath = name,
        OriginalName = name,
        TrainValMode = mode,
        Annotations = new List<SegAnnotation> { TrainFixtures.Square(1) },
    };

    private static SegSample Unlabelled(string name) => new() { ImagePath = name, OriginalName = name, Annotations = new List<SegAnnotation>() };

    [Fact]
    public void ResolveTrainingMode_FollowsLabelsAndPins()
    {
        Assert.Equal(SegTrainingMode.Train, SegTrainingSplit.ResolveTrainingMode(Labelled("a", SegSample.ModeAuto)));
        Assert.Equal(SegTrainingMode.Train, SegTrainingSplit.ResolveTrainingMode(Labelled("a", SegSample.ModeTrain)));
        Assert.Equal(SegTrainingMode.Val, SegTrainingSplit.ResolveTrainingMode(Labelled("a", SegSample.ModeVal)));
        Assert.Equal(SegTrainingMode.Ignored, SegTrainingSplit.ResolveTrainingMode(Unlabelled("a")));
        Assert.Equal(SegTrainingMode.Ignored, SegTrainingSplit.ResolveTrainingMode(null));
        Assert.Equal(SegTrainingMode.Auto, SegTrainingSplit.GetTrainingModeSetting(Labelled("a", SegSample.ModeAuto)));
        Assert.Equal(SegTrainingMode.Val, SegTrainingSplit.GetTrainingModeSetting(Labelled("a", SegSample.ModeVal)));
    }

    [Fact]
    public void GetTrainingAndValidationSamples_KeepsInputOrder_AndDropsUnlabelledAndNull()
    {
        var samples = new List<SegSample?>
        {
            Labelled("c", SegSample.ModeVal),
            Unlabelled("u1"),
            null,
            Labelled("a", SegSample.ModeAuto),
            Labelled("b", SegSample.ModeTrain),
            Unlabelled("u2"),
        };

        List<SegSample> first = SegTrainingSplit.GetTrainingAndValidationSamples(samples);
        List<SegSample> second = SegTrainingSplit.GetTrainingAndValidationSamples(samples);

        Assert.Equal(new[] { "c", "a", "b" }, first.Select(s => s.OriginalName).ToArray());
        Assert.Equal(first.Select(s => s.OriginalName).ToArray(), second.Select(s => s.OriginalName).ToArray());
        Assert.Equal(new[] { SegTrainingMode.Val, SegTrainingMode.Train, SegTrainingMode.Train },
            first.Select(SegTrainingSplit.ResolveTrainingMode).ToArray());
    }

    [Fact]
    public void IsTrainableLabelled_RequiresRealLabels()
    {
        Assert.False(SegTrainingSplit.IsTrainableLabelled(null));
        Assert.False(SegTrainingSplit.IsTrainableLabelled(Unlabelled("a")));
        Assert.False(SegTrainingSplit.IsTrainableLabelled(new SegSample { ImagePath = "a" }));
        Assert.True(SegTrainingSplit.IsTrainableLabelled(Labelled("a", SegSample.ModeAuto)));
    }

    [Fact]
    public void ResolveValidationFrames_IsDeterministic_AndMovesIdenticalGroupsWholeToVal()
    {
        // Frames 0 and 1 hold identical pixels (one group); only frame 1 is tagged Val, so both go to Val with a warning.
        List<SegAiTrainingFrame> Frames() => new()
        {
            new SegAiTrainingFrame { Index = 0, Name = "a", GroupKey = "g1", ClassBits = 1 },
            new SegAiTrainingFrame { Index = 1, Name = "b", GroupKey = "g1", ClassBits = 1, IsVal = true },
            new SegAiTrainingFrame { Index = 2, Name = "c", GroupKey = "g2", ClassBits = 2 },
            new SegAiTrainingFrame { Index = 3, Name = "d", GroupKey = "g3", ClassBits = 1, IsVal = true },
        };

        HashSet<int> first = SegAiTrainingPipeline.ResolveValidationFrames(Frames(), out string? warning);
        HashSet<int> second = SegAiTrainingPipeline.ResolveValidationFrames(Frames(), out string? warningAgain);

        Assert.Equal(new[] { 0, 1, 3 }, first.OrderBy(i => i).ToArray());
        Assert.Equal(first.OrderBy(i => i).ToArray(), second.OrderBy(i => i).ToArray());
        Assert.NotNull(warning);
        Assert.Contains("a = b", warning);
        Assert.Equal(warning, warningAgain);
    }

    [Fact]
    public void ResolveValidationFrames_NoValFrames_MeansNoValidation()
    {
        var frames = new List<SegAiTrainingFrame>
        {
            new() { Index = 0, GroupKey = "g1", ClassBits = 1 },
            new() { Index = 1, GroupKey = "g2", ClassBits = 2 },
        };

        HashSet<int> validation = SegAiTrainingPipeline.ResolveValidationFrames(frames, out string? warning);

        Assert.Empty(validation);
        Assert.Null(warning);
    }

    [Fact]
    public void ValidateValidationSplit_RejectsClassesThatOnlyValHas()
    {
        var frames = new List<SegAiTrainingFrame>
        {
            new() { Index = 0, ClassBits = 1 },
            new() { Index = 1, ClassBits = 2 },
        };

        Assert.Throws<InvalidOperationException>(() => SegAiTrainingPipeline.ValidateValidationSplit(frames, new HashSet<int> { 1 }));
        SegAiTrainingPipeline.ValidateValidationSplit(frames, new HashSet<int>());
    }

    [Fact]
    public void SelectRehearsal_IsDeterministic_AndCoversEveryPresentClass()
    {
        List<SegAiTrainingFrame> Pool() => Enumerable.Range(0, 10)
            .Select(i => new SegAiTrainingFrame { Index = i, GroupKey = "g" + i, ClassBits = i == 7 ? 2u : 1u }).ToList();

        HashSet<int> first = SegAiTrainingPipeline.SelectRehearsal(Pool(), 3, 4);
        HashSet<int> second = SegAiTrainingPipeline.SelectRehearsal(Pool(), 3, 4);

        Assert.Equal(first.OrderBy(i => i).ToArray(), second.OrderBy(i => i).ToArray());
        Assert.Contains(7, first); // the only OK frame must be rehearsed
        Assert.True(first.Count >= 2);
        Assert.True(first.Count <= 3);
    }

    [Fact]
    public void ComputeFingerprint_IgnoresInputOrder_AndChangesWithTheMode()
    {
        SegSample a = Labelled("a", SegSample.ModeTrain), b = Labelled("b", SegSample.ModeVal);

        string forward = SegAiTrainingPipeline.ComputeFingerprint(new[] { a, b });
        string backward = SegAiTrainingPipeline.ComputeFingerprint(new[] { b, a });
        string flipped = SegAiTrainingPipeline.ComputeFingerprint(new[] { a, Labelled("b", SegSample.ModeTrain) });

        Assert.Equal(forward, backward);
        Assert.NotEqual(forward, flipped);
        Assert.Equal(64, forward.Length);
    }

    [Fact]
    public void ClassBitsOf_reports_each_present_class_up_to_32()
    {
        using var labels = new OpenCvSharp.Mat(4, 4, OpenCvSharp.MatType.CV_8UC1, new OpenCvSharp.Scalar(0));
        labels.Set(0, 0, (byte)1);
        labels.Set(1, 1, (byte)3);
        labels.Set(2, 2, (byte)32);
        labels.Set(3, 3, (byte)33);
        Assert.Equal(1u | 4u | (1u << 31), SegAiTrainingPipeline.ClassBitsOf(labels, 32));
        Assert.Equal(1u | 4u, SegAiTrainingPipeline.ClassBitsOf(labels, 3));
        Assert.Equal(1u, SegAiTrainingPipeline.ClassBitsOf(labels, 2));
    }

    [Fact]
    public void ValidationSplit_names_the_missing_named_class()
    {
        var frames = new List<SegAiTrainingFrame>
        {
            new() { Index = 0, ClassBits = 1 },
            new() { Index = 1, ClassBits = 1 | 4, IsVal = true },
        };
        var ex = Assert.Throws<InvalidOperationException>(() =>
            SegAiTrainingPipeline.ValidateValidationSplit(frames, new HashSet<int> { 1 }, new[] { "scratch", "dent", "stain" }));
        Assert.Contains("stain", ex.Message);
        Assert.DoesNotContain("dent", ex.Message);
    }

    [Fact]
    public void Rehearsal_covers_a_class_above_two()
    {
        var pool = new List<SegAiTrainingFrame>();
        for (int i = 0; i < 10; i++) pool.Add(new SegAiTrainingFrame { Index = i, GroupKey = "g" + i, ClassBits = i == 6 ? 4u : 1u });
        HashSet<int> picked = SegAiTrainingPipeline.SelectRehearsal(pool, 1u | 4u, 3);
        Assert.Contains(6, picked);
    }
}
