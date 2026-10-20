using EasyLabel.Core.Classify;

namespace EasyLabel.Core.Tests;

public sealed class ClassifyHeadTests
{
    private const int Dim = 8;

    // Deterministic pseudo-random noise (no System.Random, so the fixture is the same on every runtime).
    private static float[] Row(int seed, bool ng)
    {
        var row = new float[Dim];
        uint state = unchecked((uint)seed * 2654435761u + 12345u);
        for (int j = 0; j < Dim; j++)
        {
            state = unchecked(state * 1664525u + 1013904223u);
            float noise = ((state >> 8) / 16777216f - 0.5f) * 0.6f;
            float signal = !ng ? 0f : j == 0 ? 3f : j == 1 ? 1.5f : 0f;
            row[j] = noise + signal;
        }

        return row;
    }

    private static void Labelled(int count, int firstSeed, List<float[]> x, List<bool> y)
    {
        for (int i = 0; i < count; i++)
        {
            x.Add(Row(firstSeed + i, false));
            y.Add(false);
            x.Add(Row(firstSeed + 1000 + i, true));
            y.Add(true);
        }
    }

    [Fact]
    public void Separable_data_is_learned_and_generalises()
    {
        var x = new List<float[]>();
        var y = new List<bool>();
        Labelled(40, 1, x, y);
        ClassifyHead head = ClassifyHead.Train(x, y);

        for (int i = 0; i < x.Count; i++)
            Assert.Equal(y[i], ClassifyMetrics.IsNg(head.PredictNgProbability(x[i])));

        int correct = 0;
        for (int i = 0; i < 30; i++)
        {
            if (!ClassifyMetrics.IsNg(head.PredictNgProbability(Row(5000 + i, false)))) correct++;
            if (ClassifyMetrics.IsNg(head.PredictNgProbability(Row(7000 + i, true)))) correct++;
        }

        Assert.True(correct >= 57, $"held-out correct {correct} of 60");
    }

    [Fact]
    public void Training_is_deterministic()
    {
        var x = new List<float[]>();
        var y = new List<bool>();
        Labelled(20, 9, x, y);
        ClassifyHead first = ClassifyHead.Train(x, y);
        ClassifyHead second = ClassifyHead.Train(x, y);

        Assert.Equal(first.Bias, second.Bias);
        for (int j = 0; j < Dim; j++) Assert.Equal(first.Weights[j], second.Weights[j]);
        float[] probe = Row(77, true);
        Assert.Equal(first.PredictNgProbability(probe), second.PredictNgProbability(probe));
    }

    [Fact]
    public void Probabilities_stay_in_the_unit_interval()
    {
        var x = new List<float[]>();
        var y = new List<bool>();
        Labelled(10, 3, x, y);
        ClassifyHead head = ClassifyHead.Train(x, y);
        float[] extreme = Enumerable.Repeat(1000f, Dim).ToArray();
        foreach (float[] row in x.Append(extreme))
        {
            double p = head.PredictNgProbability(row);
            Assert.InRange(p, 0.0, 1.0);
        }
    }

    [Fact]
    public void Stored_parts_predict_the_same_as_the_trained_head()
    {
        var x = new List<float[]>();
        var y = new List<bool>();
        Labelled(10, 4, x, y);
        ClassifyHead head = ClassifyHead.Train(x, y);
        var copy = new ClassifyHead(head.Mean.ToArray(), head.Scale.ToArray(), head.Weights.ToArray(), head.Bias);

        foreach (float[] row in x)
            Assert.Equal(head.PredictNgProbability(row), copy.PredictNgProbability(row));
    }

    [Fact]
    public void One_class_or_ragged_input_is_refused()
    {
        var x = new List<float[]> { Row(1, false), Row(2, false) };
        Assert.Throws<ArgumentException>(() => ClassifyHead.Train(x, new List<bool> { false, false }));
        Assert.Throws<ArgumentException>(() => ClassifyHead.Train(new List<float[]> { Row(1, false), new float[2] }, new List<bool> { false, true }));
        Assert.Throws<ArgumentException>(() => new ClassifyHead(new[] { 0f }, new[] { 1f, 1f }, new[] { 0f }, 0f));
        Assert.Throws<ArgumentException>(() => new ClassifyHead(new[] { 0f }, new[] { 0f }, new[] { 0f }, 0f));
    }

    [Fact]
    public void Wrong_feature_length_is_refused_at_prediction()
    {
        var x = new List<float[]>();
        var y = new List<bool>();
        Labelled(5, 2, x, y);
        ClassifyHead head = ClassifyHead.Train(x, y);
        Assert.Throws<ArgumentException>(() => head.PredictNgProbability(new float[Dim - 1]));
    }

    [Fact]
    public void Metrics_count_threshold_and_rates()
    {
        // p = 0.9 (NG, found), 0.4 (NG, missed), 0.49 (OK, correct), 0.5 (OK, rejected: the threshold is inclusive).
        ClassifyMetrics m = ClassifyMetrics.Compute(new[] { true, true, false, false }, new[] { 0.9, 0.4, 0.49, 0.5 });
        Assert.Equal(4, m.Count);
        Assert.Equal(2, m.NgCount);
        Assert.Equal(2, m.OkCount);
        Assert.Equal(2, m.Correct);
        Assert.Equal(1, m.NgFound);
        Assert.Equal(1, m.OkRejected);
        Assert.Equal(0.5, m.Accuracy);
        Assert.Equal(0.5, m.NgRecall);
        Assert.Equal(0.5, m.OkFalseRejectRate);
        Assert.True(ClassifyMetrics.IsNg(0.5));
        Assert.False(ClassifyMetrics.IsNg(0.4999));
    }

    [Fact]
    public void Absent_class_gives_zero_rate_and_mismatched_lists_are_refused()
    {
        ClassifyMetrics m = ClassifyMetrics.Compute(new[] { false, false }, new[] { 0.1, 0.2 });
        Assert.Equal(0, m.NgCount);
        Assert.Equal(0.0, m.NgRecall);
        Assert.Equal(1.0, m.Accuracy);
        Assert.Throws<ArgumentException>(() => ClassifyMetrics.Compute(new[] { true }, new[] { 0.1, 0.2 }));
    }
}
