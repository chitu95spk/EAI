using System.Text.Json;
using EasyAI.Desktop.Infrastructure;
using EasyAI.Desktop.ViewModels;

namespace EasyLabel.SegAI.Tests;

public sealed class AutoLabelSourceRulesTests
{
    private static readonly AutoLabelSource[] Sources =
    {
        AutoLabelSource.SegmentModel,
        AutoLabelSource.AiModel,
        AutoLabelSource.AgentPrompt,
    };

    private static readonly string?[] Models = { AiModelCatalog.Sam2Id, AiModelCatalog.YoloWorldId, "other", null };

    [Fact]
    public void AiPointsIsEnabledOnlyForSam2OfTheAiModelSourceWithABackend()
    {
        foreach (AutoLabelSource source in Sources)
        {
            foreach (string? model in Models)
            {
                foreach (bool backend in new[] { false, true })
                {
                    bool expected = backend && source == AutoLabelSource.AiModel && model == AiModelCatalog.Sam2Id;
                    Assert.Equal(expected, AutoLabelSourceRules.IsAiPointsEnabled(source, model, backend));
                }
            }
        }
    }

    [Fact]
    public void AiBoxIsEnabledOnlyForAiModelsWithABackendNeverForTheAgent()
    {
        foreach (AutoLabelSource source in Sources)
        {
            foreach (string? model in Models)
            {
                foreach (bool backend in new[] { false, true })
                {
                    bool aiModel = source == AutoLabelSource.AiModel
                        && (model == AiModelCatalog.Sam2Id || model == AiModelCatalog.YoloWorldId);
                    bool expected = backend && aiModel;
                    Assert.Equal(expected, AutoLabelSourceRules.IsAiBoxEnabled(source, model, backend));
                }
            }
        }
    }

    [Fact]
    public void TheAiModelAndAgentSourcesHaveABackendAndNeedTheirFiles()
    {
        foreach (AutoLabelSource source in Sources)
        {
            // The Segment source has no AI backend; the Agent source is implemented since the agent bridge exists.
            bool implemented = source != AutoLabelSource.SegmentModel;
            Assert.Equal(implemented, AutoLabelSourceRules.IsBackendImplemented(source));
            foreach (bool files in new[] { false, true })
            {
                bool expectedAvailable = implemented && files;
                Assert.Equal(expectedAvailable, AutoLabelSourceRules.IsBackendAvailable(source, files));
                foreach (string? model in Models)
                {
                    bool backend = AutoLabelSourceRules.IsBackendAvailable(source, files);
                    bool aiModelSource = source == AutoLabelSource.AiModel;
                    Assert.Equal(aiModelSource && expectedAvailable && model == AiModelCatalog.Sam2Id, AutoLabelSourceRules.IsAiPointsEnabled(source, model, backend));
                    bool box = aiModelSource && expectedAvailable && (model == AiModelCatalog.Sam2Id || model == AiModelCatalog.YoloWorldId);
                    Assert.Equal(box, AutoLabelSourceRules.IsAiBoxEnabled(source, model, backend));
                }
            }
        }
    }

    [Fact]
    public void AgentRunReasonNamesTheFirstThingTheUserCanFix()
    {
        Assert.Equal(AutoLabelReason.ClaudeNotFound, AutoLabelSourceRules.AgentRunReason(false, false, "x"));
        Assert.Equal(AutoLabelReason.ClaudeNotFound, AutoLabelSourceRules.AgentRunReason(false, true, ""));
        Assert.Equal(AutoLabelReason.AgentToolsMissing, AutoLabelSourceRules.AgentRunReason(true, false, "x"));
        Assert.Equal(AutoLabelReason.AgentToolsMissing, AutoLabelSourceRules.AgentRunReason(true, false, null));
        Assert.Equal(AutoLabelReason.NeedAgentPrompt, AutoLabelSourceRules.AgentRunReason(true, true, null));
        Assert.Equal(AutoLabelReason.NeedAgentPrompt, AutoLabelSourceRules.AgentRunReason(true, true, "  \r\n "));
        Assert.Equal(AutoLabelReason.None, AutoLabelSourceRules.AgentRunReason(true, true, "mark every scratch"));
    }

    [Fact]
    public void AgentToolReasonsAndAgentPromptSetting()
    {
        // AI-Points and AI-Box stay disabled for the Agent source, each with its own reason.
        Assert.Equal(AutoLabelReason.NeedSam2, AutoLabelSourceRules.PointsReason(AutoLabelSource.AgentPrompt, null, true, true));
        Assert.Equal(AutoLabelReason.AgentUsesPrompt, AutoLabelSourceRules.BoxReason(AutoLabelSource.AgentPrompt, null, true, true));
        Assert.False(AutoLabelSourceRules.IsAiPointsEnabled(AutoLabelSource.AgentPrompt, AiModelCatalog.Sam2Id, true));
        Assert.False(AutoLabelSourceRules.IsAiBoxEnabled(AutoLabelSource.AgentPrompt, AiModelCatalog.Sam2Id, true));

        AppSettings settings = new AppSettings();
        Assert.Equal(string.Empty, AutoLabelSourceRules.ReadAgentPrompt(settings));
        settings.AgentPrompt = "mark every scratch";
        AppSettings loaded = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))!;
        Assert.Equal("mark every scratch", AutoLabelSourceRules.ReadAgentPrompt(loaded));
        Assert.Equal(string.Empty, AutoLabelSourceRules.ReadAgentPrompt(JsonSerializer.Deserialize<AppSettings>("{\"AgentPrompt\":null}")!));
        Assert.Equal(string.Empty, AutoLabelSourceRules.ReadAgentPrompt(JsonSerializer.Deserialize<AppSettings>("{\"SchemaVersion\":1}")!));
    }

    [Fact]
    public void ToolsAndRunReasonAgreeWithTheFlippedBackendForTheAiModelSource()
    {
        bool implemented = AutoLabelSourceRules.IsBackendImplemented(AutoLabelSource.AiModel);
        // With files: SAM2 gets both tools, YOLO-World only the box; without files nothing is enabled and the reason is the missing files.
        Assert.Equal(AutoLabelReason.None, AutoLabelSourceRules.PointsReason(AutoLabelSource.AiModel, AiModelCatalog.Sam2Id, true, implemented));
        Assert.Equal(AutoLabelReason.NeedSam2, AutoLabelSourceRules.PointsReason(AutoLabelSource.AiModel, AiModelCatalog.YoloWorldId, true, implemented));
        Assert.Equal(AutoLabelReason.None, AutoLabelSourceRules.BoxReason(AutoLabelSource.AiModel, AiModelCatalog.YoloWorldId, true, implemented));
        Assert.Equal(AutoLabelReason.ModelFilesMissing, AutoLabelSourceRules.BoxReason(AutoLabelSource.AiModel, AiModelCatalog.Sam2Id, false, implemented));
        Assert.Equal(AutoLabelReason.AgentUsesPrompt, AutoLabelSourceRules.BoxReason(AutoLabelSource.AgentPrompt, AiModelCatalog.Sam2Id, true, AutoLabelSourceRules.IsBackendImplemented(AutoLabelSource.AgentPrompt)));
    }

    [Fact]
    public void Sam2RunNeedsAPromptButYoloWorldRunsFromTheButton()
    {
        Assert.Equal(AutoLabelReason.NeedPrompt, AutoLabelSourceRules.RunReason(AutoLabelSource.AiModel, AiModelCatalog.Sam2Id, true, true));
        Assert.Equal(AutoLabelReason.None, AutoLabelSourceRules.RunReason(AutoLabelSource.AiModel, AiModelCatalog.YoloWorldId, true, true));
        Assert.Equal(AutoLabelReason.ModelFilesMissing, AutoLabelSourceRules.RunReason(AutoLabelSource.AiModel, AiModelCatalog.Sam2Id, false, true));
        Assert.Equal(AutoLabelReason.ModelFilesMissing, AutoLabelSourceRules.RunReason(AutoLabelSource.AiModel, AiModelCatalog.YoloWorldId, false, true));
        Assert.Equal(AutoLabelReason.NoBackend, AutoLabelSourceRules.RunReason(AutoLabelSource.AiModel, AiModelCatalog.YoloWorldId, true, false));
    }

    [Fact]
    public void BackendAvailabilityNeedsFilesOnlyForTheAiModelSource()
    {
        // The rule with a backend present (the S2/S3 case), written against the explicit combinations.
        Assert.Equal(AutoLabelReason.None, AutoLabelSourceRules.PointsReason(AutoLabelSource.AiModel, AiModelCatalog.Sam2Id, true, true));
        Assert.Equal(AutoLabelReason.None, AutoLabelSourceRules.BoxReason(AutoLabelSource.AiModel, AiModelCatalog.YoloWorldId, true, true));
        Assert.Equal(AutoLabelReason.AgentUsesPrompt, AutoLabelSourceRules.BoxReason(AutoLabelSource.AgentPrompt, AiModelCatalog.Sam2Id, false, true));
    }

    [Fact]
    public void PointsReasonFollowsTheSourceModelFilesAndBackend()
    {
        foreach (AutoLabelSource source in Sources)
        {
            foreach (string? model in Models)
            {
                foreach (bool files in new[] { false, true })
                {
                    foreach (bool impl in new[] { false, true })
                    {
                        AutoLabelReason actual = AutoLabelSourceRules.PointsReason(source, model, files, impl);
                        AutoLabelReason expected;
                        if (source != AutoLabelSource.AiModel || model != AiModelCatalog.Sam2Id)
                        {
                            expected = AutoLabelReason.NeedSam2;
                        }
                        else if (!files)
                        {
                            expected = AutoLabelReason.ModelFilesMissing;
                        }
                        else
                        {
                            expected = impl ? AutoLabelReason.None : AutoLabelReason.NoBackend;
                        }

                        Assert.Equal(expected, actual);
                        bool backend = files && impl;
                        Assert.Equal(actual == AutoLabelReason.None, AutoLabelSourceRules.IsAiPointsEnabled(source, model, backend && source == AutoLabelSource.AiModel));
                    }
                }
            }
        }
    }

    [Fact]
    public void BoxAndRunReasonFollowTheSourceModelFilesAndBackend()
    {
        Assert.Equal(AutoLabelReason.NeedAiOrAgent, AutoLabelSourceRules.BoxReason(AutoLabelSource.SegmentModel, AiModelCatalog.Sam2Id, true, true));
        Assert.Equal(AutoLabelReason.NeedAiOrAgent, AutoLabelSourceRules.BoxReason(AutoLabelSource.AiModel, "other", true, true));
        Assert.Equal(AutoLabelReason.NeedAiOrAgent, AutoLabelSourceRules.BoxReason(AutoLabelSource.AiModel, null, true, true));
        Assert.Equal(AutoLabelReason.ModelFilesMissing, AutoLabelSourceRules.BoxReason(AutoLabelSource.AiModel, AiModelCatalog.Sam2Id, false, true));
        Assert.Equal(AutoLabelReason.ModelFilesMissing, AutoLabelSourceRules.BoxReason(AutoLabelSource.AiModel, AiModelCatalog.YoloWorldId, false, false));
        Assert.Equal(AutoLabelReason.NoBackend, AutoLabelSourceRules.BoxReason(AutoLabelSource.AiModel, AiModelCatalog.YoloWorldId, true, false));
        Assert.Equal(AutoLabelReason.AgentUsesPrompt, AutoLabelSourceRules.BoxReason(AutoLabelSource.AgentPrompt, AiModelCatalog.Sam2Id, true, false));
        Assert.Equal(AutoLabelReason.None, AutoLabelSourceRules.RunReason(AutoLabelSource.SegmentModel, null, false, false));
        Assert.Equal(AutoLabelReason.AgentNotConnected, AutoLabelSourceRules.RunReason(AutoLabelSource.AgentPrompt, null, false, false));
        Assert.Equal(AutoLabelReason.NoBackend, AutoLabelSourceRules.RunReason(AutoLabelSource.AiModel, AiModelCatalog.Sam2Id, true, false));
        Assert.Equal(AutoLabelReason.NeedPrompt, AutoLabelSourceRules.RunReason(AutoLabelSource.AiModel, AiModelCatalog.Sam2Id, true, true));
        Assert.Equal(AutoLabelReason.None, AutoLabelSourceRules.RunReason(AutoLabelSource.AiModel, AiModelCatalog.YoloWorldId, true, true));
    }

    [Fact]
    public void BoxEnabledMatchesTheBoxReason()
    {
        foreach (AutoLabelSource source in Sources)
        {
            foreach (string? model in Models)
            {
                foreach (bool files in new[] { false, true })
                {
                    foreach (bool impl in new[] { false, true })
                    {
                        bool backend = source == AutoLabelSource.AgentPrompt ? impl : source == AutoLabelSource.AiModel && files && impl;
                        bool enabled = AutoLabelSourceRules.IsAiBoxEnabled(source, model, backend);
                        Assert.Equal(AutoLabelSourceRules.BoxReason(source, model, files, impl) == AutoLabelReason.None, enabled);
                    }
                }
            }
        }
    }

    [Fact]
    public void SourceTextRoundTripsAndUnknownTextFallsBackToSegmentModel()
    {
        foreach (AutoLabelSource source in Sources)
        {
            Assert.Equal(source, AutoLabelSourceRules.ParseSource(AutoLabelSourceRules.ToSetting(source)));
        }

        Assert.Equal(AutoLabelSource.AiModel, AutoLabelSourceRules.ParseSource(" AI "));
        Assert.Equal(AutoLabelSource.SegmentModel, AutoLabelSourceRules.ParseSource(null));
        Assert.Equal(AutoLabelSource.SegmentModel, AutoLabelSourceRules.ParseSource(string.Empty));
        Assert.Equal(AutoLabelSource.SegmentModel, AutoLabelSourceRules.ParseSource("bogus"));
        Assert.Equal(AutoLabelSource.SegmentModel, AutoLabelSourceRules.ParseSource("2"));
    }

    [Fact]
    public void AiModelTextFallsBackToSam2()
    {
        Assert.Equal(AiModelCatalog.Sam2Id, AutoLabelSourceRules.NormalizeAiModel(null));
        Assert.Equal(AiModelCatalog.Sam2Id, AutoLabelSourceRules.NormalizeAiModel("bogus"));
        Assert.Equal(AiModelCatalog.Sam2Id, AutoLabelSourceRules.NormalizeAiModel("SAM2"));
        Assert.Equal(AiModelCatalog.YoloWorldId, AutoLabelSourceRules.NormalizeAiModel("yolo-world"));
        Assert.Equal(AiModelCatalog.YoloWorldId, AutoLabelSourceRules.NormalizeAiModel(" YOLO-World "));
    }

    [Fact]
    public void CustomDetectIsAnAiModelAndSurvivesNormalizing()
    {
        Assert.True(AutoLabelSourceRules.IsAiModelId(AiModelCatalog.CustomDetectId));
        Assert.False(AutoLabelSourceRules.IsAiModelId("custom"));
        Assert.Equal(AiModelCatalog.CustomDetectId, AutoLabelSourceRules.NormalizeAiModel("custom-detect"));
        Assert.Equal(AiModelCatalog.CustomDetectId, AutoLabelSourceRules.NormalizeAiModel(" Custom-Detect "));
        Assert.Equal(AiModelCatalog.Sam2Id, AutoLabelSourceRules.NormalizeAiModel("custom"));

        // Like YOLO-World: AI-Box works, AI-Points does not, Run needs no prompt.
        Assert.Equal(AutoLabelReason.None, AutoLabelSourceRules.BoxReason(AutoLabelSource.AiModel, AiModelCatalog.CustomDetectId, true, true));
        Assert.Equal(AutoLabelReason.NeedSam2, AutoLabelSourceRules.PointsReason(AutoLabelSource.AiModel, AiModelCatalog.CustomDetectId, true, true));
        Assert.Equal(AutoLabelReason.None, AutoLabelSourceRules.RunReason(AutoLabelSource.AiModel, AiModelCatalog.CustomDetectId, true, true));
        Assert.Equal(AutoLabelReason.ModelFilesMissing, AutoLabelSourceRules.RunReason(AutoLabelSource.AiModel, AiModelCatalog.CustomDetectId, false, true));

        AppSettings settings = new AppSettings();
        AutoLabelSourceRules.Write(settings, AutoLabelSource.AiModel, AiModelCatalog.CustomDetectId);
        Assert.Equal(AiModelCatalog.CustomDetectId, AutoLabelSourceRules.ReadAiModel(settings));
    }

    [Fact]
    public void DefaultSettingsAreTheSegmentModel()
    {
        AppSettings settings = new AppSettings();
        Assert.Equal(AutoLabelSource.SegmentModel, AutoLabelSourceRules.ReadSource(settings));
        Assert.Equal(AiModelCatalog.Sam2Id, AutoLabelSourceRules.ReadAiModel(settings));
    }

    [Fact]
    public void WrittenChoiceSurvivesAJsonRoundTrip()
    {
        AppSettings settings = new AppSettings();
        AutoLabelSourceRules.Write(settings, AutoLabelSource.AgentPrompt, AiModelCatalog.YoloWorldId);

        AppSettings loaded = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))!;

        Assert.Equal(AutoLabelSource.AgentPrompt, AutoLabelSourceRules.ReadSource(loaded));
        Assert.Equal(AiModelCatalog.YoloWorldId, AutoLabelSourceRules.ReadAiModel(loaded));
    }

    [Fact]
    public void InvalidOrMissingStoredValuesFallBack()
    {
        AppSettings invalid = JsonSerializer.Deserialize<AppSettings>("{\"AutoLabelSource\":\"bogus\",\"AutoLabelAiModel\":\"nope\"}")!;
        Assert.Equal(AutoLabelSource.SegmentModel, AutoLabelSourceRules.ReadSource(invalid));
        Assert.Equal(AiModelCatalog.Sam2Id, AutoLabelSourceRules.ReadAiModel(invalid));

        AppSettings nulls = JsonSerializer.Deserialize<AppSettings>("{\"AutoLabelSource\":null,\"AutoLabelAiModel\":null}")!;
        Assert.Equal(AutoLabelSource.SegmentModel, AutoLabelSourceRules.ReadSource(nulls));
        Assert.Equal(AiModelCatalog.Sam2Id, AutoLabelSourceRules.ReadAiModel(nulls));

        // A settings file written before this feature has neither field: the defaults apply.
        AppSettings old = JsonSerializer.Deserialize<AppSettings>("{\"SchemaVersion\":1}")!;
        Assert.Equal(AutoLabelSource.SegmentModel, AutoLabelSourceRules.ReadSource(old));
        Assert.Equal(AiModelCatalog.Sam2Id, AutoLabelSourceRules.ReadAiModel(old));
    }

    [Fact]
    public void WriteStoresANormalizedModelId()
    {
        AppSettings settings = new AppSettings();
        AutoLabelSourceRules.Write(settings, AutoLabelSource.AiModel, "bogus");
        Assert.Equal("ai", settings.AutoLabelSource);
        Assert.Equal(AiModelCatalog.Sam2Id, settings.AutoLabelAiModel);
    }
}
