using EasyLabel.Core.Convert;
using EasyLabel.SegAI.Data;
using EasyLabel.SegAI.Training;
using EasyLabel.SegAI.Training.Ultralytics;
using EasyAI.Desktop.ViewModels;

namespace EasyLabel.SegAI.Tests;

/// <summary>T1: automatic Train/Val split, the YOLO dataset builder honouring the Val flags, and the guards of Step 3.</summary>
public sealed class TrainValSplitTests
{
    private static List<SegSplitItem> Items(int count, Func<int, int> classOf, SegTrainingMode mode = SegTrainingMode.Auto)
    {
        List<SegSplitItem> items = new List<SegSplitItem>();
        for (int i = 0; i < count; i++)
        {
            items.Add(new SegSplitItem("img" + i.ToString("D3") + ".png", new[] { classOf(i) }, mode));
        }

        return items;
    }

    private static int Count(SegSplitSide[] sides, SegSplitSide side) => sides.Count(s => s == side);

    [Theory]
    [InlineData(2, 1)]
    [InlineData(4, 1)]
    [InlineData(5, 1)]
    [InlineData(20, 4)]
    public void AutoAssign_ValCountIsAboutTwentyPercentAndAtLeastOne(int count, int expectedVal)
    {
        SegSplitSide[] sides = SegTrainingSplit.AutoAssign(Items(count, i => 1), 1337);
        Assert.Equal(count, sides.Length);
        Assert.Equal(expectedVal, Count(sides, SegSplitSide.Val));
        Assert.Equal(count - expectedVal, Count(sides, SegSplitSide.Train));
    }

    [Fact]
    public void AutoAssign_OneImageStaysTrain_NoneIsEmpty()
    {
        SegSplitSide[] one = SegTrainingSplit.AutoAssign(Items(1, i => 1), 1);
        Assert.Equal(SegSplitSide.Train, one[0]);
        Assert.Empty(SegTrainingSplit.AutoAssign(new List<SegSplitItem>(), 1));
    }

    [Fact]
    public void AutoAssign_ValAndTrainAreNeverEmptyOrEqualFor2To40Images()
    {
        for (int n = 2; n <= 40; n++)
        {
            SegSplitSide[] sides = SegTrainingSplit.AutoAssign(Items(n, i => i % 3), 99);
            Assert.True(Count(sides, SegSplitSide.Val) >= 1, "val empty for n=" + n);
            Assert.True(Count(sides, SegSplitSide.Train) >= 1, "train empty for n=" + n);
        }
    }

    [Fact]
    public void AutoAssign_RareClassWithTwoImagesIsInBothSides_SingleImageClassStaysInTrain()
    {
        // 30 images: class 1 x19, class 2 x8, class 3 x2 (rare), class 4 x1 (sole).
        List<SegSplitItem> items = Items(30, i => i < 19 ? 1 : i < 27 ? 2 : i < 29 ? 3 : 4);
        SegSplitSide[] sides = SegTrainingSplit.AutoAssign(items, 1337);

        HashSet<int> train = new HashSet<int>(), val = new HashSet<int>();
        for (int i = 0; i < items.Count; i++)
        {
            (sides[i] == SegSplitSide.Val ? val : train).UnionWith(items[i].ClassIds);
        }

        Assert.Equal(6, Count(sides, SegSplitSide.Val));
        foreach (int c in new[] { 1, 2, 3, 4 })
        {
            Assert.Contains(c, train);
        }

        Assert.Contains(1, val);
        Assert.Contains(2, val);
        Assert.Contains(3, val);
        Assert.DoesNotContain(4, val);
    }

    [Fact]
    public void AutoAssign_SameSeedSameResult_DifferentSeedDiffers()
    {
        List<SegSplitItem> items = Items(40, i => i % 2);
        SegSplitSide[] a = SegTrainingSplit.AutoAssign(items, 5);
        SegSplitSide[] b = SegTrainingSplit.AutoAssign(items, 5);
        Assert.Equal(a, b);

        bool anyDifferent = false;
        for (int seed = 6; seed < 16 && !anyDifferent; seed++)
        {
            anyDifferent = !a.SequenceEqual(SegTrainingSplit.AutoAssign(items, seed));
        }

        Assert.True(anyDifferent);
    }

    [Fact]
    public void AutoAssign_DoesNotDependOnItemOrder()
    {
        List<SegSplitItem> items = Items(25, i => i % 3);
        SegSplitSide[] forward = SegTrainingSplit.AutoAssign(items, 3);
        List<SegSplitItem> reversed = new List<SegSplitItem>(items);
        reversed.Reverse();
        SegSplitSide[] backward = SegTrainingSplit.AutoAssign(reversed, 3);
        for (int i = 0; i < items.Count; i++)
        {
            Assert.Equal(forward[i], backward[items.Count - 1 - i]);
        }
    }

    [Fact]
    public void AutoAssign_KeepsValPinsAndIgnoredAndPrefersAutoOverTrainPins()
    {
        List<SegSplitItem> items = Items(10, i => 1);
        items[2] = items[2] with { Mode = SegTrainingMode.Val };
        items[4] = items[4] with { Mode = SegTrainingMode.Ignored };
        SegSplitSide[] sides = SegTrainingSplit.AutoAssign(items, 1);
        Assert.Equal(SegSplitSide.Val, sides[2]);
        Assert.Equal(SegSplitSide.Ignored, sides[4]);
        Assert.Equal(2, Count(sides, SegSplitSide.Val));

        // All pinned Val: one image goes back to Train.
        SegSplitSide[] allVal = SegTrainingSplit.AutoAssign(Items(4, i => 1, SegTrainingMode.Val), 1);
        Assert.Equal(3, Count(allVal, SegSplitSide.Val));
        Assert.Equal(1, Count(allVal, SegSplitSide.Train));

        // Train pins are only used when needed: 5 images, 1 Val needed, 4 are Train-pinned, 1 Auto -> the Auto one.
        List<SegSplitItem> pinned = Items(5, i => 1, SegTrainingMode.Train);
        pinned[3] = pinned[3] with { Mode = SegTrainingMode.Auto };
        Assert.Equal(SegSplitSide.Val, SegTrainingSplit.AutoAssign(pinned, 1)[3]);
    }

    // ---------- UltraDatasetBuilder ----------

    /// <summary>A fresh base folder under the test output; the project goes in <c>proj</c>, the runs in <c>runs</c> (outside the project like the real layout).</summary>
    private static string NewDir()
    {
        string dir = Path.Combine(AppContext.BaseDirectory, "t1-split", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "proj"));
        return dir;
    }

    /// <summary>Creates root\img{i}.png + img{i}.json (flags val for the given indexes, train for others when withTrain) and returns a fake exporter that mimics the real naming.</summary>
    private static Func<UltralyticsExportRequest, UltralyticsExportResult> FakeExport(string root, int count, Func<int, int> classOf, ISet<int>? valFlag)
    {
        for (int i = 0; i < count; i++)
        {
            File.WriteAllBytes(Path.Combine(root, "img" + i + ".png"), new byte[] { 1 });
            string flags = valFlag != null && valFlag.Contains(i) ? "{\"val\": true}" : "{}";
            File.WriteAllText(Path.Combine(root, "img" + i + ".json"), "{\"version\":\"5.0\",\"flags\":" + flags + ",\"shapes\":[{\"label\":\"a\",\"points\":[[0,0],[1,1]],\"shape_type\":\"rectangle\"}],\"imagePath\":\"img" + i + ".png\"}");
        }

        return request =>
        {
            string[] patterns = { "*.jpg", "*.jpeg", "*.png", "*.bmp" };
            int index = 0;
            UltralyticsExportResult result = new UltralyticsExportResult { DataYamlPath = Path.Combine(request.OutputDirectory, "data.yaml") };
            foreach (string split in new[] { "train", "val", "test" })
            {
                Directory.CreateDirectory(Path.Combine(request.OutputDirectory, "images", split));
                Directory.CreateDirectory(Path.Combine(request.OutputDirectory, "labels", split));
                result.Splits.Add(new UltralyticsExportSplitResult { Name = split });
            }

            foreach (string pattern in patterns)
            {
                foreach (string file in Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories))
                {
                    string name = index.ToString("D6") + "_" + Path.GetFileName(file);
                    string stem = Path.GetFileNameWithoutExtension(file);
                    int imageNumber = int.Parse(stem.Substring(3));
                    string split = index == 0 ? "test" : "train"; // the old ratio split: Val stays empty
                    File.WriteAllBytes(Path.Combine(request.OutputDirectory, "images", split, name), new byte[] { 1 });
                    File.WriteAllText(Path.Combine(request.OutputDirectory, "labels", split, Path.ChangeExtension(name, ".txt")), classOf(imageNumber) + " 0.5 0.5 0.2 0.2\n");
                    result.Splits.First(s => s.Name == split).ImageCount++;
                    index++;
                }
            }

            return result;
        };
    }

    private static string[] Names(string outDir, string kind, string split)
    {
        string dir = Path.Combine(outDir, kind, split);
        return Directory.Exists(dir) ? Directory.GetFiles(dir).Select(p => Path.GetFileName(p)).OrderBy(n => n, StringComparer.Ordinal).ToArray() : new string[0];
    }

    [Fact]
    public void Builder_NoValFlag_AutoAssignsNonEmptyDisjointTrainAndVal()
    {
        string baseDir = NewDir();
        string root = Path.Combine(baseDir, "proj");
        try
        {
            string runs = Path.Combine(baseDir, "runs");
            UltraDatasetBuilder builder = new UltraDatasetBuilder(FakeExport(root, 8, i => i % 2, null));
            builder.Build(UltraTask.Detect, root, runs, "r1");

            string outDir = UltraDatasetBuilder.DatasetDirectory(runs, "r1");
            string[] train = Names(outDir, "images", "train"), val = Names(outDir, "images", "val");
            Assert.NotEmpty(val);
            Assert.NotEmpty(train);
            Assert.Empty(Names(outDir, "images", "test"));
            Assert.Empty(train.Intersect(val));
            Assert.Equal(8, train.Length + val.Length);
            Assert.Equal(val.Length, Names(outDir, "labels", "val").Length);
        }
        finally
        {
            Directory.Delete(baseDir, true);
        }
    }

    [Fact]
    public void Builder_HonoursExplicitValFlags()
    {
        string baseDir = NewDir();
        string root = Path.Combine(baseDir, "proj");
        try
        {
            string runs = Path.Combine(baseDir, "runs");
            UltraDatasetBuilder builder = new UltraDatasetBuilder(FakeExport(root, 8, i => i % 2, new HashSet<int> { 2, 5 }));
            builder.Build(UltraTask.Detect, root, runs, "r1");

            string outDir = UltraDatasetBuilder.DatasetDirectory(runs, "r1");
            string[] val = Names(outDir, "images", "val");
            Assert.Equal(2, val.Length);
            Assert.All(val, n => Assert.True(n.EndsWith("_img2.png") || n.EndsWith("_img5.png"), n));
            Assert.Equal(6, Names(outDir, "images", "train").Length);
        }
        finally
        {
            Directory.Delete(baseDir, true);
        }
    }

    [Fact]
    public void Builder_FewerThanFiveLabelledImages_ThrowsTooFewImages()
    {
        string baseDir = NewDir();
        string root = Path.Combine(baseDir, "proj");
        try
        {
            UltraDatasetBuilder builder = new UltraDatasetBuilder(FakeExport(root, 4, i => 0, null));
            UltraDatasetException ex = Assert.Throws<UltraDatasetException>(() => builder.Build(UltraTask.Detect, root, Path.Combine(baseDir, "runs"), "r1"));
            Assert.Equal(UltraDatasetProblem.TooFewImages, ex.Problem);
            Assert.Equal(4, ex.LabelledImages);
        }
        finally
        {
            Directory.Delete(baseDir, true);
        }
    }

    [Fact]
    public void Builder_ClassOnlyInValImage_ThrowsClassNotInTrain()
    {
        string baseDir = NewDir();
        string root = Path.Combine(baseDir, "proj");
        try
        {
            // Image 3 is the only one of class 1 and is flagged Val.
            UltraDatasetBuilder builder = new UltraDatasetBuilder(FakeExport(root, 6, i => i == 3 ? 1 : 0, new HashSet<int> { 3 }));
            UltraDatasetException ex = Assert.Throws<UltraDatasetException>(() => builder.Build(UltraTask.Detect, root, Path.Combine(baseDir, "runs"), "r1"));
            Assert.Equal(UltraDatasetProblem.ClassNotInTrain, ex.Problem);
        }
        finally
        {
            Directory.Delete(baseDir, true);
        }
    }

    // ---------- guards ----------

    [Fact]
    public void Guards_NoValWithTwoOrMoreImagesOffersAutoAssign()
    {
        Assert.Equal(TrainStepRules.TrainFixKind.AutoAssignVal, TrainStepRules.FixKindOf(SegAiTrainBlock.NoValImages, 2));
        Assert.Equal("TrainBlockNoValImages", TrainStepRules.SegmentBlockKey(SegAiTrainBlock.NoValImages, 2));
        // T2: with fewer than 2 labelled images the fix is to go and label more.
        Assert.Equal(TrainStepRules.TrainFixKind.GoToLabel, TrainStepRules.FixKindOf(SegAiTrainBlock.NoValImages, 1));
        Assert.Equal("TrainBlockNeedTwoImages", TrainStepRules.SegmentBlockKey(SegAiTrainBlock.NoValImages, 1));
        Assert.Equal(TrainStepRules.TrainFixKind.None, TrainStepRules.FixKindOf(SegAiTrainBlock.NativeMissing, 10));
        Assert.Equal("TrainBlockNativeMissing", TrainStepRules.SegmentBlockKey(SegAiTrainBlock.NativeMissing, 10));
        Assert.Null(TrainStepRules.SegmentBlockKey(SegAiTrainBlock.None, 10));
    }

    [Fact]
    public void Guards_YoloNeedsFiveLabelledImages()
    {
        TrainStepRules.UltraBlock few = TrainStepRules.UltraBlockOf(true, false, false, TrainStepRules.UltraEnvironment.Ready, true, true, 4);
        Assert.Equal(TrainStepRules.UltraBlock.TooFewImages, few);
        Assert.Equal("UltraBlockTooFewImages", TrainStepRules.UltraBlockKey(few));
        Assert.Equal(TrainStepRules.UltraBlock.None, TrainStepRules.UltraBlockOf(true, false, false, TrainStepRules.UltraEnvironment.Ready, true, true, 5));
        // The old call shape (no count) is unchanged.
        Assert.Equal(TrainStepRules.UltraBlock.None, TrainStepRules.UltraBlockOf(true, false, false, TrainStepRules.UltraEnvironment.Ready, true));
        Assert.Equal("UltraErrorClassNotInTrain", TrainStepRules.UltraDatasetProblemKey(UltraDatasetProblem.ClassNotInTrain));
        Assert.Equal("UltraErrorTooFewImages", TrainStepRules.UltraDatasetProblemKey(UltraDatasetProblem.TooFewImages));
    }
}
