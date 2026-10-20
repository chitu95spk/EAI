using System.Text.Json;
using EasyAI.Desktop.Infrastructure;
using EasyAI.Desktop.ViewModels;

namespace EasyLabel.SegAI.Tests;

public sealed class EasyModeRulesTests
{
    private static EasyInputs Project(int labelled = 0, bool hostBusy = false)
    {
        return new EasyInputs
        {
            HasProject = true,
            TotalImages = 20,
            Labelled = labelled,
            HostBusy = hostBusy,
        };
    }

    [Fact]
    public void ResolveMode_KeepsStoredValues()
    {
        Assert.Equal("simple", EasyModeRules.ResolveMode("simple", true));
        Assert.Equal("simple", EasyModeRules.ResolveMode(" SIMPLE ", false));
        Assert.Equal("advanced", EasyModeRules.ResolveMode("advanced", false));
    }

    [Fact]
    public void ResolveMode_MissingValueDependsOnExistingFile()
    {
        Assert.Equal("advanced", EasyModeRules.ResolveMode(null, true));
        Assert.Equal("simple", EasyModeRules.ResolveMode(null, false));
        Assert.Equal("simple", EasyModeRules.ResolveMode("  ", false));
    }

    [Fact]
    public void ResolveMode_GarbageIsAdvanced()
    {
        Assert.Equal("advanced", EasyModeRules.ResolveMode("expert", true));
        Assert.Equal("advanced", EasyModeRules.ResolveMode("expert", false));
    }

    [Fact]
    public void AppSettings_WithoutUiMode_LoadsNull()
    {
        AppSettings settings = JsonSerializer.Deserialize<AppSettings>("{\"SchemaVersion\":1,\"UiCulture\":\"en\"}")!;

        Assert.Null(settings.UiMode);
    }

    [Theory]
    [InlineData(0, 10, false)]
    [InlineData(1, 9, false)]
    [InlineData(9, 1, false)]
    [InlineData(10, 0, true)]
    [InlineData(50, 0, true)]
    public void Step2_RemainingAndDone(int labelled, int remaining, bool done)
    {
        EasyStepState[] states = EasyModeRules.Evaluate(Project(labelled));

        Assert.Equal(remaining, states[1].Remaining);
        Assert.Equal(done ? EasyStepStatus.Done : EasyStepStatus.Ready, states[1].Status);
        Assert.True(states[1].IsEnabled);
    }

    [Fact]
    public void Step2_LockedWithoutProject()
    {
        EasyStepState[] states = EasyModeRules.Evaluate(new EasyInputs());

        Assert.False(states[1].IsEnabled);
        Assert.Equal(EasyStepStatus.Locked, states[1].Status);
    }

    [Fact]
    public void Step3_LockedBelowTwoLabelledImages()
    {
        EasyStepState[] states = EasyModeRules.Evaluate(Project(labelled: 1));

        Assert.False(states[2].IsEnabled);
        Assert.Equal(EasyStepStatus.Locked, states[2].Status);
    }

    [Fact]
    public void Step3_LockedWhileHostBusy()
    {
        EasyStepState[] states = EasyModeRules.Evaluate(Project(labelled: 5, hostBusy: true));

        Assert.False(states[2].IsEnabled);
        Assert.NotEqual(EasyStepStatus.Ready, states[2].Status);
    }

    [Fact]
    public void Step3_ReadyWithEnoughLabelledImages()
    {
        EasyStepState[] states = EasyModeRules.Evaluate(Project(labelled: 2));

        Assert.True(states[2].IsEnabled);
        Assert.Equal(EasyStepStatus.Ready, states[2].Status);
    }

    [Fact]
    public void Step3_RunningWhileTraining()
    {
        EasyInputs input = Project(labelled: 5, hostBusy: true);
        input.TrainRunning = true;

        EasyStepState[] states = EasyModeRules.Evaluate(input);

        Assert.Equal(EasyStepStatus.Running, states[2].Status);
    }

    [Fact]
    public void Steps4And5_LockedWithoutModel()
    {
        EasyStepState[] states = EasyModeRules.Evaluate(Project(labelled: 10));

        Assert.False(states[3].IsEnabled);
        Assert.Equal(EasyStepStatus.Locked, states[3].Status);
        Assert.False(states[4].IsEnabled);
        Assert.Equal(EasyStepStatus.Locked, states[4].Status);
    }

    [Fact]
    public void CurrentIndex_FollowsFirstNotDone()
    {
        EasyInputs input = new EasyInputs();
        Assert.Equal(0, EasyModeRules.CurrentIndex(EasyModeRules.Evaluate(input)));

        input.HasProject = true;
        input.TotalImages = 5;
        Assert.Equal(1, EasyModeRules.CurrentIndex(EasyModeRules.Evaluate(input)));

        input.Labelled = 10;
        Assert.Equal(2, EasyModeRules.CurrentIndex(EasyModeRules.Evaluate(input)));

        input.HasActiveModel = true;
        Assert.Equal(3, EasyModeRules.CurrentIndex(EasyModeRules.Evaluate(input)));

        input.InspectRun = 1;
        Assert.Equal(4, EasyModeRules.CurrentIndex(EasyModeRules.Evaluate(input)));

        input.SentThisSession = true;
        Assert.Equal(-1, EasyModeRules.CurrentIndex(EasyModeRules.Evaluate(input)));
    }
}
