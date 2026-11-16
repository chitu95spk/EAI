using EasyAI.Desktop.Infrastructure;

namespace EasyLabel.SegAI.Tests;

public sealed class EditorShellRulesTests
{
    [Theory]
    [InlineData(true, true, false, false)]
    [InlineData(true, false, false, false)]
    [InlineData(false, true, true, true)]
    [InlineData(false, false, false, false)]
    public void SavePolicyMatchesLabelmeModel(bool autoSave, bool dirty, bool canSave, bool needsPrompt)
    {
        Assert.Equal(canSave, SavePolicy.CanSave(autoSave, dirty));
        Assert.Equal(needsPrompt, SavePolicy.NeedsPrompt(autoSave, dirty));
        Assert.Equal(autoSave, SavePolicy.WritesImmediately(autoSave));
    }

    [Fact]
    public void TitleMarksDirtyAndSuggestedNameUsesImageStem()
    {
        Assert.Equal("EasyAI", SavePolicy.FormatTitle("EasyAI", false));
        Assert.Equal("EasyAI *", SavePolicy.FormatTitle("EasyAI", true));
        Assert.Equal("img01.json", SavePolicy.SuggestedFileName("sub/dir/img01.png"));
        Assert.Equal("a.b.json", SavePolicy.SuggestedFileName("a.b.bmp"));
        Assert.Null(SavePolicy.MirrorDirectory("  "));
        Assert.Equal("D:/out", SavePolicy.MirrorDirectory("D:/out"));
    }

    [Fact]
    public void DirtyTrackerFollowsSignatureAndUndoReturnsToClean()
    {
        DirtyTracker tracker = new DirtyTracker();
        tracker.MarkSaved("A");
        Assert.False(tracker.IsDirty);

        Assert.True(tracker.Update("B"));
        Assert.True(tracker.IsDirty);
        Assert.False(tracker.Update("C"));
        Assert.True(tracker.Update("A"));
        Assert.False(tracker.IsDirty);

        tracker.MarkUnsaved();
        Assert.True(tracker.IsDirty);
        Assert.False(tracker.Update("A"));
        tracker.MarkSaved("A");
        Assert.False(tracker.IsDirty);
    }

    [Fact]
    public void StatusTextShowsModeAndOneDecimalPosition()
    {
        Assert.Equal("EDIT", StatusTextFormatter.ModeText(true, "polygon"));
        Assert.Equal("CREATE(polygon)", StatusTextFormatter.ModeText(false, "polygon"));
        Assert.Equal("mode=EDIT | x=12.3, y=4.0", StatusTextFormatter.Pointer("EDIT", true, 12.34f, 4f));
        Assert.Equal("mode=EDIT", StatusTextFormatter.Pointer("EDIT", false, 0, 0));
    }

    [Fact]
    public void SearchIgnoresCaseAndDiacriticsAndNeedsAllWords()
    {
        Assert.Equal("tu dong luu", SettingsSearch.Normalize("Tự động lưu"));
        Assert.True(SettingsSearch.Matches("Tự động lưu", "dong LUU"));
        Assert.True(SettingsSearch.Matches("Tự động lưu", "tự lưu"));
        Assert.False(SettingsSearch.Matches("Tự động lưu", "nhúng"));
        Assert.True(SettingsSearch.Matches("anything", "  "));
    }

    [Fact]
    public void CatalogHasTenPagesAndOnlyRealRowsAreEnabled()
    {
        IReadOnlyList<AiModelStatus> ai = new[] { new AiModelStatus("sam2", "Segment Anything 2", false, "x") };
        IReadOnlyList<SettingPageModel> pages = SettingsCatalog.Build(key => key, ai);

        // EAI-AGENT S7 added the Agent runs page (batch caps); EAI-CAM K3 added the Camera page; EAI-COST S8 moved the Agents page (Offline-only row) into the catalog.
        Assert.Equal(12, pages.Count);
        HashSet<string> real = new HashSet<string>
        {
            SettingsCatalog.RowTheme,
            SettingsCatalog.RowLanguage,
            SettingsCatalog.RowAutoSave,
            SettingsCatalog.RowEmbedImage,
            SettingsCatalog.RowOutputDirectory,
            // EAI-UI commit C: drawing, continue-between-images, label sources and label behaviour rows are real.
            SettingsAccessor.RowShowLabelDialog,
            SettingsAccessor.RowShapeColorMode,
            SettingsAccessor.RowShiftAutoColor,
            SettingsAccessor.RowUniformColor,
            SettingsAccessor.RowFallbackColor,
            SettingsAccessor.RowFillWhileDrawing,
            SettingsAccessor.RowShowLabelOnCanvas,
            SettingsAccessor.RowKeepAnnotation,
            SettingsAccessor.RowKeepZoom,
            SettingsAccessor.RowKeepBrightness,
            SettingsAccessor.RowPredefinedLabels,
            SettingsAccessor.RowSortLabels,
            SettingsAccessor.RowShowLabelTextField,
            SettingsAccessor.RowAutoComplete,
            // EAI-UI commit G: the logo colour theme rows and the reset button are real.
            SettingsAccessor.RowThemeAccent,
            SettingsAccessor.RowThemeGradientStart,
            SettingsAccessor.RowThemeGradientEnd,
            SettingsAccessor.RowThemeBackground,
            SettingsAccessor.RowThemeReset,
            // EAI-CACHE C5: the cache limit and the clear button are real (the size row is a read-only Info row).
            SettingsAccessor.RowCacheMax,
            SettingsAccessor.RowCacheClear,
            // EAI-AGENT S7: the batch caps are real.
            SettingsAccessor.RowAgentMaxImages,
            SettingsAccessor.RowAgentBatchWorkers,
            SettingsAccessor.RowAgentMaxCost,
            SettingsAccessor.RowAgentMaxUsdPerDay,
            // EAI-ROADMAP T7: the Remaining-mode routing switch is real.
            SettingsAccessor.RowAgentRouteLocalFirst,
            SettingsAccessor.RowAgentBatchRetryNormal,
            SettingsAccessor.RowAgentBatchSam2Refine,
            // EAI-COST S8: the Offline-only switch is real.
            SettingsAccessor.RowAgentOfflineOnly,
            // EAI-CAM K3: the source and device choices and the three buttons are real (status and error are read-only Info rows).
            SettingsAccessor.RowCameraSource,
            SettingsAccessor.RowCameraDevice,
            // EAI-LABEL: boxes may leave the image (default on), the row is real.
            SettingsAccessor.RowAllowOutOfBounds,
            SettingsAccessor.RowCameraRefresh,
            SettingsAccessor.RowCameraConnect,
            SettingsAccessor.RowCameraDisconnect,
            // EAI-ROADMAP T4: the EasyVision model folder and its Browse button are real.
            SettingsAccessor.RowEasyVisionFolder,
            SettingsAccessor.RowEasyVisionFolderBrowse,
            // EAI-OKNG: the model pack install button is real.
            SettingsAccessor.RowInstallModelPack,
            // EAI-OKNG: the Advanced YOLO CPU opt-in toggle is real.
            SettingsAccessor.RowUltraAllowCpu,
            // EAI-OKNG: the Segment AI device choice (CPU / Intel GPU / NVIDIA CUDA) is real.
            SettingsAccessor.RowSegAiDevice,
        };
        foreach (SettingRowModel row in pages.SelectMany(p => p.Rows))
        {
            Assert.Equal(real.Contains(row.Id), row.IsEnabled);
            if (!row.IsEnabled)
            {
                Assert.False(string.IsNullOrEmpty(row.DisabledReason) && string.IsNullOrEmpty(row.ValueText));
            }
        }

        Assert.Equal(2, SettingsSearch.Filter(pages, "SetAutoSave").Count + SettingsSearch.Filter(pages, "SetEmbedImage").Count);
        Assert.Equal(pages.Sum(p => p.Rows.Count), SettingsSearch.Filter(pages, null).Count);
    }

    [Fact]
    public void AiModelsAreDetectedFromFilesOnDisk()
    {
        string root = Path.Combine(AppContext.BaseDirectory, "ai-detect-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "sam2"));
        File.WriteAllText(Path.Combine(root, "sam2", "encoder.onnx"), "x");

        // An encoder alone is not a usable SAM2 model: Found means the backend can load it (encoder + decoder pair).
        Assert.False(AiModelCatalog.Detect(root).Single(m => m.Id == AiModelCatalog.Sam2Id).Found);
        File.WriteAllText(Path.Combine(root, "sam2", "decoder.onnx"), "x");

        IReadOnlyList<AiModelStatus> found = AiModelCatalog.Detect(root);

        Assert.True(found.Single(m => m.Id == AiModelCatalog.Sam2Id).Found);
        Assert.False(found.Single(m => m.Id == AiModelCatalog.YoloWorldId).Found);
        Assert.False(AiModelCatalog.Detect(Path.Combine(root, "missing")).Any(m => m.Found));
    }

    [Fact]
    public void CustomDetectModelNeedsExactlyOneOnnxInItsOwnFolder()
    {
        string root = Path.Combine(AppContext.BaseDirectory, "ai-custom-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        // Missing folder: listed, not found, and the YOLO-World model is not affected.
        AiModelStatus missing = AiModelCatalog.Detect(root).Single(m => m.Id == AiModelCatalog.CustomDetectId);
        Assert.False(missing.Found);
        Assert.Equal(Path.Combine(root, "custom-detect"), missing.Location);

        string folder = Path.Combine(root, "custom-detect");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "model.onnx"), "x");
        File.WriteAllText(Path.Combine(folder, "model.classes.txt"), "FAN3");
        IReadOnlyList<AiModelStatus> found = AiModelCatalog.Detect(root);
        Assert.True(found.Single(m => m.Id == AiModelCatalog.CustomDetectId).Found);
        Assert.False(found.Single(m => m.Id == AiModelCatalog.YoloWorldId).Found);

        File.WriteAllText(Path.Combine(folder, "other.onnx"), "x");
        Assert.False(AiModelCatalog.Detect(root).Single(m => m.Id == AiModelCatalog.CustomDetectId).Found);
    }

    [Fact]
    public void GuideParserReadsHeadingsBulletsAndParagraphs()
    {
        IReadOnlyList<GuideBlock> blocks = GuideParser.Parse("# Title\n\nFirst line\nsecond **bold** line\n\n## Part\n- one\n- `two`\n### Sub\n");

        Assert.Equal(
            new[] { GuideBlockKind.Heading1, GuideBlockKind.Paragraph, GuideBlockKind.Heading2, GuideBlockKind.Bullet, GuideBlockKind.Bullet, GuideBlockKind.Heading3 },
            blocks.Select(b => b.Kind).ToArray());
        Assert.Equal("First line second bold line", blocks[1].Text);
        Assert.Equal("two", blocks[4].Text);
    }
}
