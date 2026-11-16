using System.Globalization;
using System.Text;

namespace EasyAI.Desktop.Infrastructure;

public enum SettingKind
{
    Toggle,
    Choice,
    Text,
    Info,
    Path,
    /// <summary>A "#RRGGBB" colour: hex text, swatch and a button that opens the colour dialog.</summary>
    Color,
    /// <summary>A row that only holds a button (its caption is the row caption).</summary>
    Button,
}

public sealed class SettingOption
{
    public SettingOption(string value, string caption)
    {
        Value = value;
        Caption = caption;
    }

    public string Value { get; }

    public string Caption { get; }
}

/// <summary>One row of the Settings dialog. A row that is not enabled shows <see cref="DisabledReason"/> as its tooltip.</summary>
public sealed class SettingRowModel
{
    public SettingRowModel(string id, string pageId, string section, string caption, SettingKind kind)
    {
        Id = id;
        PageId = pageId;
        Section = section;
        Caption = caption;
        Kind = kind;
    }

    public string Id { get; }

    public string PageId { get; }

    public string Section { get; }

    public string Caption { get; }

    public SettingKind Kind { get; }

    public string? Description { get; set; }

    public bool IsEnabled { get; set; }

    public string? DisabledReason { get; set; }

    /// <summary>Value shown by a disabled row (its default), or the status text of an Info row.</summary>
    public string? ValueText { get; set; }

    public IReadOnlyList<SettingOption> Options { get; set; } = Array.Empty<SettingOption>();
}

public sealed class SettingPageModel
{
    public SettingPageModel(string id, string title)
    {
        Id = id;
        Title = title;
    }

    public string Id { get; }

    public string Title { get; }

    public List<SettingRowModel> Rows { get; } = new List<SettingRowModel>();
}

/// <summary>Availability of one AI model detected from files on disk (not from a flag).</summary>
public sealed class AiModelStatus
{
    public AiModelStatus(string id, string displayName, bool found, string location)
    {
        Id = id;
        DisplayName = displayName;
        Found = found;
        Location = location;
    }

    public string Id { get; }

    public string DisplayName { get; }

    public bool Found { get; }

    public string Location { get; }
}

/// <summary>
/// Looks for the AI model files the AI Model source needs (SAM2, YOLO-World). A found model is usable by the AI-Box / AI-Points
/// tools and the Auto-label Run of the AI Model source.
/// </summary>
public static class AiModelCatalog
{
    public const string Sam2Id = "sam2";
    public const string YoloWorldId = "yolo-world";

    /// <summary>A YOLO detect model trained in the Train step and installed by "Use this model" (folder models\custom-detect).</summary>
    public const string CustomDetectId = "custom-detect";

    /// <summary>SAM3 box masks re-cut by SAM2 (hybrid). Same folder as sam2; found only when models\sam3 also exists.</summary>
    public const string Sam3Id = "sam3";

    public static IReadOnlyList<AiModelStatus> Detect(string modelsRoot)
    {
        AiModelStatus sam2 = Probe(modelsRoot, Sam2Id, "Segment Anything 2");
        bool sam3 = false;
        try
        {
            sam3 = sam2.Found && EasyLabel.Ai.Onnx.Sam3Backend.Exists(System.IO.Path.Combine(modelsRoot, "sam3"));
        }
        catch (Exception)
        {
            sam3 = false;
        }

        return new[]
        {
            sam2,
            new AiModelStatus(Sam3Id, "SAM3 + SAM2", sam3, sam2.Location),
            Probe(modelsRoot, YoloWorldId, "YOLO-World"),
            Probe(modelsRoot, CustomDetectId, "Custom YOLO"),
        };
    }

    private static AiModelStatus Probe(string modelsRoot, string id, string displayName)
    {
        string folder = string.IsNullOrWhiteSpace(modelsRoot) ? id : System.IO.Path.Combine(modelsRoot, id);
        bool found = false;
        try
        {
            // Found means the backend could load it: sam2 = one *encoder*.onnx + one *decoder*.onnx, yolo-world = exactly one .onnx.
            found = !string.IsNullOrWhiteSpace(modelsRoot)
                && EasyLabel.Ai.Onnx.AiModelFiles.Resolve(id, folder).Found;
        }
        catch (Exception)
        {
            found = false;
        }

        return new AiModelStatus(id, displayName, found, folder);
    }
}

/// <summary>Builds the pages and rows of the Settings dialog from caption keys; captions are resolved by <c>text</c> (localized in the app).</summary>
public static class SettingsCatalog
{
    public const string PageAppearance = "appearance";
    public const string PageFiles = "files";
    public const string PageCanvas = "canvas";
    public const string PageContinue = "continue";
    public const string PageLabelSources = "labelSources";
    public const string PageLabelBehavior = "labelBehavior";
    public const string PageAi = "ai";
    public const string PageAiModels = "aiModels";
    public const string PageAgents = "agents";
    public const string PageCache = "cache";
    public const string PageAgentRun = "agentRun";
    public const string PageCamera = "camera";

    public const string RowTheme = "theme";
    public const string RowLanguage = "language";
    public const string RowAutoSave = "autoSave";
    public const string RowEmbedImage = "embedImage";
    public const string RowOutputDirectory = "outputDirectory";

    public static IReadOnlyList<SettingPageModel> Build(Func<string, string> text, IReadOnlyList<AiModelStatus> ai)
    {
        string later = text("SetNotYet");
        var pages = new List<SettingPageModel>();

        SettingPageModel appearance = Page(pages, PageAppearance, text("SetPageAppearance"));
        SettingRowModel theme = Row(appearance, RowTheme, text("SetSectionTheme"), text("SetTheme"), SettingKind.Choice, true, null);
        theme.Options = new[]
        {
            new SettingOption("system", text("SetThemeSystem")),
            new SettingOption("light", text("SetThemeLight")),
            new SettingOption("dark", text("SetThemeDark")),
        };
        SettingRowModel language = Row(appearance, RowLanguage, text("SetSectionLanguage"), text("SetLanguage"), SettingKind.Choice, true, null);
        language.Description = text("SetLanguageRestart");
        language.Options = new[]
        {
            new SettingOption("vi", text("SetLanguageVi")),
            new SettingOption("en", text("SetLanguageEn")),
        };
        // Logo colour theme: four colours applied live, plus a button that puts the logo colours back.
        string palette = text("SetSectionPalette");
        Row(appearance, SettingsAccessor.RowThemeAccent, palette, text("SetThemeAccent"), SettingKind.Color, true, null);
        Row(appearance, SettingsAccessor.RowThemeGradientStart, palette, text("SetThemeGradientStart"), SettingKind.Color, true, null);
        Row(appearance, SettingsAccessor.RowThemeGradientEnd, palette, text("SetThemeGradientEnd"), SettingKind.Color, true, null);
        Row(appearance, SettingsAccessor.RowThemeBackground, palette, text("SetThemeBackground"), SettingKind.Color, true, null);
        Row(appearance, SettingsAccessor.RowThemeReset, palette, text("SetThemeResetCaption"), SettingKind.Button, true, null);

        SettingPageModel files = Page(pages, PageFiles, text("SetPageFiles"));
        Row(files, RowAutoSave, text("SetSectionSave"), text("SetAutoSave"), SettingKind.Toggle, true, null);
        Row(files, RowEmbedImage, text("SetSectionSave"), text("SetEmbedImage"), SettingKind.Toggle, true, null);
        Row(files, RowOutputDirectory, text("SetSectionSave"), text("SetOutputDirectory"), SettingKind.Path, true, null);
        // Send to EasyVision (T4): the folder EasyVision loads .segai models from, chosen once.
        string easyVisionSection = Localized(text, "SetSectionEasyVision", "EasyVision");
        Row(files, SettingsAccessor.RowEasyVisionFolder, easyVisionSection, Localized(text, "SetEasyVisionFolder", "EasyVision model folder"), SettingKind.Text, true, null);
        Row(files, SettingsAccessor.RowEasyVisionFolderBrowse, easyVisionSection, Localized(text, "SetEasyVisionFolderBrowse", "Browse for the EasyVision model folder"), SettingKind.Button, true, null);

        SettingPageModel canvas = Page(pages, PageCanvas, text("SetPageCanvas"));
        string drawing = text("SetSectionDrawing");
        Row(canvas, SettingsAccessor.RowShowLabelDialog, drawing, text("SetShowLabelDialog"), SettingKind.Toggle, true, null);
        SettingRowModel colorMode = Row(canvas, SettingsAccessor.RowShapeColorMode, drawing, text("SetShapeColorMode"), SettingKind.Choice, true, null);
        colorMode.Options = new[]
        {
            new SettingOption(ShapeColorRules.ModeAuto, text("SetColorModeAuto")),
            new SettingOption(ShapeColorRules.ModeUniform, text("SetColorModeUniform")),
            new SettingOption(ShapeColorRules.ModeLabel, text("SetColorModeLabel")),
        };
        Row(canvas, SettingsAccessor.RowShiftAutoColor, drawing, text("SetShiftAutoColor"), SettingKind.Toggle, true, null);
        Row(canvas, SettingsAccessor.RowUniformColor, drawing, text("SetUniformColor"), SettingKind.Text, true, null);
        Row(canvas, SettingsAccessor.RowFallbackColor, drawing, text("SetFallbackColor"), SettingKind.Text, true, null);
        Row(canvas, SettingsAccessor.RowFillWhileDrawing, drawing, text("SetFillWhileDrawing"), SettingKind.Toggle, true, null);
        Row(canvas, SettingsAccessor.RowAllowOutOfBounds, drawing, text("SetAllowOutOfBounds"), SettingKind.Toggle, true, null);
        Row(canvas, SettingsAccessor.RowShowLabelOnCanvas, drawing, text("SetShowLabelOnCanvas"), SettingKind.Toggle, true, null);

        SettingPageModel cont = Page(pages, PageContinue, text("SetPageContinue"));
        string between = text("SetSectionContinue");
        Row(cont, SettingsAccessor.RowKeepAnnotation, between, text("SetKeepAnnotation"), SettingKind.Toggle, true, null);
        Row(cont, SettingsAccessor.RowKeepZoom, between, text("SetKeepZoom"), SettingKind.Toggle, true, null);
        Row(cont, SettingsAccessor.RowKeepBrightness, between, text("SetKeepBrightness"), SettingKind.Toggle, true, null);

        SettingPageModel sources = Page(pages, PageLabelSources, text("SetPageLabelSources"));
        string sourcesSection = text("SetSectionSources");
        Row(sources, SettingsAccessor.RowPredefinedLabels, sourcesSection, text("SetPredefinedLabels"), SettingKind.Text, true, null);
        // The predefined image flags row is hidden until a Flags UI exists (the Core flag code and the stored value stay).
        // The predefined shape flags row is hidden for the same reason (no shape-flag editor yet).

        SettingPageModel behavior = Page(pages, PageLabelBehavior, text("SetPageLabelBehavior"));
        string behaviorSection = text("SetSectionBehavior");
        Disabled(behavior, "validateLabels", behaviorSection, text("SetValidateLabels"), SettingKind.Choice, later, text("SetOff"));
        Row(behavior, SettingsAccessor.RowSortLabels, behaviorSection, text("SetSortLabels"), SettingKind.Toggle, true, null);
        Row(behavior, SettingsAccessor.RowShowLabelTextField, behaviorSection, text("SetShowLabelTextField"), SettingKind.Toggle, true, null);
        SettingRowModel autoComplete = Row(behavior, SettingsAccessor.RowAutoComplete, behaviorSection, text("SetAutoComplete"), SettingKind.Choice, true, null);
        autoComplete.Options = new[]
        {
            new SettingOption("startsWith", text("SetStartsWith")),
            new SettingOption("contains", text("SetContains")),
        };

        SettingPageModel aiPage = Page(pages, PageAi, text("SetPageAi"));
        string aiSection = text("SetSectionAi");
        foreach (AiModelStatus model in ai)
        {
            SettingRowModel status = Row(aiPage, "aiStatus." + model.Id, aiSection, model.DisplayName, SettingKind.Info, false, null);
            status.ValueText = AiStatusText(text, model);
        }

        string aiReason = text("SetAiUnavailable");
        Disabled(aiPage, "aiDefaultModel", aiSection, text("SetAiDefaultModel"), SettingKind.Choice, aiReason, text("SetUnavailable"));
        Disabled(aiPage, "aiTextModel", aiSection, text("SetAiTextModel"), SettingKind.Choice, aiReason, text("SetUnavailable"));
        Disabled(aiPage, "aiPolygonDetail", aiSection, text("SetAiPolygonDetail"), SettingKind.Text, aiReason, "60");
        Disabled(aiPage, "aiBlockDuplicates", aiSection, text("SetAiBlockDuplicates"), SettingKind.Toggle, aiReason, text("SetOff"));

        SettingPageModel modelsPage = Page(pages, PageAiModels, text("SetPageAiModels"));
        foreach (AiModelStatus model in ai)
        {
            SettingRowModel row = Row(modelsPage, "aiModel." + model.Id, text("SetSectionAiModels"), model.DisplayName, SettingKind.Info, false, null);
            row.ValueText = AiStatusText(text, model);
            row.Description = model.Location;
        }

        // Installs a model pack (zip) into the models folder; the licence is shown and confirmed before anything is copied.
        Row(modelsPage, SettingsAccessor.RowInstallModelPack, text("SetSectionAiModels"), Localized(text, "SetInstallModelPackCaption", "OK/NG base model pack"), SettingKind.Button, true, null);
        // Advanced opt-in for YOLO training without an NVIDIA GPU (the Train tab applies it in Advanced mode only). The key is new.
        Row(modelsPage, SettingsAccessor.RowUltraAllowCpu, text("SetSectionAiModels"), Localized(text, "SetUltraAllowCpu", "Allow YOLO training on CPU (Advanced, slow)"), SettingKind.Toggle, true, null);
        // Segment AI device (inspection / evaluation only; training stays on Intel/CPU): CPU is the default and what an old settings file means.
        SettingRowModel segDevice = Row(modelsPage, SettingsAccessor.RowSegAiDevice, text("SetSectionAiModels"), Localized(text, "SetSegAiDevice", "Segment AI device"), SettingKind.Choice, true, null);
        segDevice.Description = Localized(text, "SetSegAiDeviceHint", "NVIDIA CUDA needs an NVIDIA GPU and the CUDA runtime DLLs next to BeeNativeSegAI.dll; otherwise it falls back to CPU and says so. Training is not affected.");
        segDevice.Options = new[]
        {
            new SettingOption(SegAiDeviceRules.Cpu, Localized(text, "SetSegAiDeviceCpu", "CPU")),
            new SettingOption(SegAiDeviceRules.Intel, Localized(text, "SetSegAiDeviceIntel", "Intel GPU (OpenVINO)")),
            new SettingOption(SegAiDeviceRules.Nvidia, Localized(text, "SetSegAiDeviceNvidia", "NVIDIA GPU / CUDA (ONNX Runtime)")),
        };

        SettingPageModel cachePage = Page(pages, PageCache, text("SetPageCache"));
        string cacheSection = text("SetSectionCache");
        SettingRowModel cacheSize = Row(cachePage, SettingsAccessor.RowCacheSize, cacheSection, text("SetCacheSize"), SettingKind.Info, false, null);
        cacheSize.ValueText = "0 B";
        Row(cachePage, SettingsAccessor.RowCacheMax, cacheSection, text("SetCacheMax"), SettingKind.Text, true, null);
        Row(cachePage, SettingsAccessor.RowCacheClear, cacheSection, text("SetCacheClear"), SettingKind.Button, true, null);

        SettingPageModel agentsPage = Page(pages, PageAgents, text("SetPageAgents"));
        SettingRowModel offline = Row(agentsPage, SettingsAccessor.RowAgentOfflineOnly, text("SetPageAgents"), text("AgentOfflineOnly"), SettingKind.Toggle, true, null);
        offline.Description = text("AgentOfflineExplanation");

        // Caps of an agent batch (EAI-AGENT S7).
        SettingPageModel agentRunPage = Page(pages, PageAgentRun, text("SetPageAgentRun"));
        string agentRunSection = text("SetSectionAgentRun");
        Row(agentRunPage, SettingsAccessor.RowAgentMaxImages, agentRunSection, text("SetAgentMaxImages"), SettingKind.Text, true, null);
        Row(agentRunPage, SettingsAccessor.RowAgentBatchWorkers, agentRunSection, text("SetAgentBatchWorkers"), SettingKind.Text, true, null);
        Row(agentRunPage, SettingsAccessor.RowAgentMaxCost, agentRunSection, text("SetAgentMaxCost"), SettingKind.Text, true, null);
        // The label key is new: until the resource has it, the English text shows (ByName answers the key itself when it is missing).
        string perDayLabel = text("SetAgentMaxUsdPerDay");
        Row(agentRunPage, SettingsAccessor.RowAgentMaxUsdPerDay, agentRunSection, perDayLabel == "SetAgentMaxUsdPerDay" ? "Daily cost cap (USD, 0 = off)" : perDayLabel, SettingKind.Text, true, null);
        // Remaining mode routing (T7). The key is new: until the resource has it, the English text shows.
        string routeLabel = text("SetAgentRouteLocalFirst");
        Row(agentRunPage, SettingsAccessor.RowAgentRouteLocalFirst, agentRunSection, routeLabel == "SetAgentRouteLocalFirst" ? "Local results first in Remaining mode" : routeLabel, SettingKind.Toggle, true, null);
        Row(agentRunPage, SettingsAccessor.RowAgentBatchRetryNormal, agentRunSection, text("SetAgentBatchRetryNormal"), SettingKind.Toggle, true, null);
        Row(agentRunPage, SettingsAccessor.RowAgentBatchSam2Refine, agentRunSection, text("SetAgentBatchSam2Refine"), SettingKind.Toggle, true, null);

        // Image source and the shared camera connection (EAI-CAM K3). The device options and the status texts are filled in by the dialog.
        SettingPageModel cameraPage = Page(pages, PageCamera, text("SetPageCamera"));
        string cameraSourceSection = text("SetSectionCameraSource");
        SettingRowModel cameraSource = Row(cameraPage, SettingsAccessor.RowCameraSource, cameraSourceSection, text("SetCameraSource"), SettingKind.Choice, true, null);
        cameraSource.Options = new[]
        {
            new SettingOption(AppSettings.ImageSourceFiles, text("SetCameraSourceFiles")),
            new SettingOption(AppSettings.ImageSourceCameraMvs, text("SetCameraSourceMvs")),
        };
        string cameraSection = text("SetSectionCamera");
        Row(cameraPage, SettingsAccessor.RowCameraDevice, cameraSection, text("SetCameraDevice"), SettingKind.Choice, true, null);
        Row(cameraPage, SettingsAccessor.RowCameraRefresh, cameraSection, text("SetCameraRefresh"), SettingKind.Button, true, null);
        Row(cameraPage, SettingsAccessor.RowCameraConnect, cameraSection, text("SetCameraConnect"), SettingKind.Button, true, null);
        Row(cameraPage, SettingsAccessor.RowCameraDisconnect, cameraSection, text("SetCameraDisconnect"), SettingKind.Button, true, null);
        SettingRowModel cameraStatus = Row(cameraPage, SettingsAccessor.RowCameraStatus, cameraSection, text("SetCameraStatus"), SettingKind.Info, false, null);
        cameraStatus.ValueText = text("CameraStatusDisconnected");
        Row(cameraPage, SettingsAccessor.RowCameraError, cameraSection, text("SetCameraError"), SettingKind.Info, false, text("SetCameraError"));

        return pages;
    }

    /// <summary>The resource text, or <paramref name="english"/> while the key is not in the resource file (the lookup then returns the key itself).</summary>
    public static string Localized(Func<string, string> text, string key, string english)
    {
        string value = text(key);
        return string.IsNullOrEmpty(value) || string.Equals(value, key, StringComparison.Ordinal) ? english : value;
    }

    /// <summary>"Không khả dụng: name" when the model files are absent, otherwise a "model files found" text.</summary>
    public static string AiStatusText(Func<string, string> text, AiModelStatus model)
    {
        // The custom model is not downloaded by hand: the hint says where it comes from.
        string key = model.Found
            ? "AiDetectedFormat"
            : model.Id == AiModelCatalog.CustomDetectId ? "AiCustomUnavailableFormat" : "AiUnavailableFormat";
        return string.Format(CultureInfo.CurrentCulture, text(key), model.DisplayName);
    }

    private static SettingPageModel Page(List<SettingPageModel> pages, string id, string title)
    {
        var page = new SettingPageModel(id, title);
        pages.Add(page);
        return page;
    }

    private static SettingRowModel Row(SettingPageModel page, string id, string section, string caption, SettingKind kind, bool enabled, string? reason)
    {
        var row = new SettingRowModel(id, page.Id, section, caption, kind) { IsEnabled = enabled, DisabledReason = reason };
        page.Rows.Add(row);
        return row;
    }

    private static void Disabled(SettingPageModel page, string id, string section, string caption, SettingKind kind, string reason, string valueText)
    {
        SettingRowModel row = Row(page, id, section, caption, kind, false, reason);
        row.ValueText = valueText;
    }
}

/// <summary>Caption filter of the Settings dialog search box (case and diacritic insensitive, all words must match).</summary>
public static class SettingsSearch
{
    public static string Normalize(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        string decomposed = value.Trim().ToLowerInvariant().Replace('đ', 'd').Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (char c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }

    public static bool Matches(string caption, string query)
    {
        string normalizedCaption = Normalize(caption);
        foreach (string word in Normalize(query).Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (!normalizedCaption.Contains(word, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Rows of all pages whose caption matches; an empty query returns every row.</summary>
    public static IReadOnlyList<SettingRowModel> Filter(IEnumerable<SettingPageModel> pages, string? query)
    {
        var result = new List<SettingRowModel>();
        foreach (SettingPageModel page in pages)
        {
            foreach (SettingRowModel row in page.Rows)
            {
                if (string.IsNullOrWhiteSpace(query) || Matches(row.Caption, query))
                {
                    result.Add(row);
                }
            }
        }

        return result;
    }
}

public enum GuideBlockKind
{
    Heading1,
    Heading2,
    Heading3,
    Paragraph,
    Bullet,
}

public sealed class GuideBlock
{
    public GuideBlock(GuideBlockKind kind, string text)
    {
        Kind = kind;
        Text = text;
    }

    public GuideBlockKind Kind { get; }

    public string Text { get; }
}

/// <summary>Reads the small markdown subset of docs/easyai/guide.md: #, ##, ### headings, "- " bullets and paragraphs.</summary>
public static class GuideParser
{
    public static IReadOnlyList<GuideBlock> Parse(string markdown)
    {
        var blocks = new List<GuideBlock>();
        var paragraph = new StringBuilder();

        void FlushParagraph()
        {
            if (paragraph.Length > 0)
            {
                blocks.Add(new GuideBlock(GuideBlockKind.Paragraph, Clean(paragraph.ToString())));
                paragraph.Clear();
            }
        }

        foreach (string raw in (markdown ?? string.Empty).Replace("\r\n", "\n").Split('\n'))
        {
            string line = raw.TrimEnd();
            if (line.Length == 0)
            {
                FlushParagraph();
            }
            else if (line.StartsWith("### ", StringComparison.Ordinal))
            {
                FlushParagraph();
                blocks.Add(new GuideBlock(GuideBlockKind.Heading3, Clean(line.Substring(4))));
            }
            else if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                FlushParagraph();
                blocks.Add(new GuideBlock(GuideBlockKind.Heading2, Clean(line.Substring(3))));
            }
            else if (line.StartsWith("# ", StringComparison.Ordinal))
            {
                FlushParagraph();
                blocks.Add(new GuideBlock(GuideBlockKind.Heading1, Clean(line.Substring(2))));
            }
            else if (line.StartsWith("- ", StringComparison.Ordinal) || line.StartsWith("* ", StringComparison.Ordinal))
            {
                FlushParagraph();
                blocks.Add(new GuideBlock(GuideBlockKind.Bullet, Clean(line.Substring(2))));
            }
            else
            {
                if (paragraph.Length > 0)
                {
                    paragraph.Append(' ');
                }

                paragraph.Append(line.Trim());
            }
        }

        FlushParagraph();
        return blocks;
    }

    private static string Clean(string text)
    {
        return text.Replace("**", string.Empty).Replace("`", string.Empty).Trim();
    }
}
