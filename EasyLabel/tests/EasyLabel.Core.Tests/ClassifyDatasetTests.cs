using EasyLabel.Core.Classify;

namespace EasyLabel.Core.Tests;

public sealed class ClassifyDatasetTests
{
    private static List<ClassifySample> Set(int ok, int ng)
    {
        var list = new List<ClassifySample>();
        for (int i = 0; i < ok; i++) list.Add(new ClassifySample($"ok/{i:D3}.png", false));
        for (int i = 0; i < ng; i++) list.Add(new ClassifySample($"ng/{i:D3}.png", true));
        return list;
    }

    private static string[] Paths(IEnumerable<ClassifySample> samples) => samples.Select(s => s.RelativePath).ToArray();

    [Fact]
    public void Twenty_or_more_images_use_one_stratified_split_of_about_twenty_percent()
    {
        ClassifyPlan plan = ClassifyDataset.Plan(Set(15, 15));
        Assert.False(plan.LeaveOneOut);
        Assert.Single(plan.Splits);
        ClassifySplit split = plan.Splits[0];
        Assert.Equal(6, split.Validation.Count);
        Assert.Equal(3, split.Validation.Count(s => s.IsNg));
        Assert.Equal(3, split.Validation.Count(s => !s.IsNg));
        Assert.Equal(24, split.Train.Count);
    }

    [Fact]
    public void Each_class_keeps_at_least_one_validation_and_one_training_image()
    {
        ClassifySplit split = ClassifyDataset.Split(Set(2, 18));
        Assert.Equal(1, split.Validation.Count(s => !s.IsNg));
        Assert.Equal(1, split.Train.Count(s => !s.IsNg));
        Assert.Equal(4, split.Validation.Count(s => s.IsNg));
        Assert.Equal(14, split.Train.Count(s => s.IsNg));
    }

    [Fact]
    public void Split_is_deterministic_and_independent_of_input_order()
    {
        List<ClassifySample> forward = Set(12, 12);
        List<ClassifySample> reversed = forward.AsEnumerable().Reverse().ToList();

        ClassifySplit a = ClassifyDataset.Split(forward);
        ClassifySplit b = ClassifyDataset.Split(reversed);
        ClassifySplit again = ClassifyDataset.Split(forward);

        Assert.Equal(Paths(a.Validation), Paths(b.Validation));
        Assert.Equal(Paths(a.Train), Paths(b.Train));
        Assert.Equal(Paths(a.Validation), Paths(again.Validation));
    }

    [Fact]
    public void Fewer_than_twenty_images_use_leave_one_out()
    {
        ClassifyPlan plan = ClassifyDataset.Plan(Set(8, 8));
        Assert.True(plan.LeaveOneOut);
        Assert.Equal(16, plan.Splits.Count);
        foreach (ClassifySplit split in plan.Splits)
        {
            Assert.Single(split.Validation);
            Assert.Equal(15, split.Train.Count);
        }

        Assert.Equal(16, plan.Splits.Select(s => s.Validation[0].RelativePath).Distinct().Count());
    }

    [Fact]
    public void Twenty_images_are_not_leave_one_out()
    {
        Assert.False(ClassifyDataset.Plan(Set(10, 10)).LeaveOneOut);
        Assert.True(ClassifyDataset.Plan(Set(9, 10)).LeaveOneOut);
    }

    [Fact]
    public void Each_class_needs_at_least_two_images()
    {
        Assert.Throws<ArgumentException>(() => ClassifyDataset.Plan(Set(1, 20)));
        Assert.Throws<ArgumentException>(() => ClassifyDataset.Plan(Set(20, 0)));
        Assert.Throws<ArgumentException>(() => ClassifyDataset.LeaveOneOut(Set(2, 1)));
    }

    [Fact]
    public void Duplicate_paths_are_refused()
    {
        List<ClassifySample> list = Set(3, 3);
        list.Add(new ClassifySample("ok/000.png", false));
        Assert.Throws<ArgumentException>(() => ClassifyDataset.Plan(list));
    }
}
