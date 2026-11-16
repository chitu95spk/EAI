using System.Globalization;
using EasyLabel.Core.Projects;

namespace EasyAI.Desktop.Infrastructure;

public sealed class AppSettings
{
    public int SchemaVersion { get; set; } = 1;

    public string? NativeSegAiFolder { get; set; }

    /// <summary>"en", "vi" or null for the default (Vietnamese). Applied at start-up.</summary>
    public string? UiCulture { get; set; }

    /// <summary>"system" (follow Windows), "light" or "dark". Null means "system".</summary>
    public string? Theme { get; set; }

    // ---------- colour theme (defaults = the logo colours, see ThemePalette) ----------

    /// <summary>Selected states, primary buttons and panel title bars (#RRGGBB).</summary>
    public string ThemeAccentColor { get; set; } = ThemePalette.DefaultAccent;

    /// <summary>Left colour of the title / menu band (#RRGGBB).</summary>
    public string ThemeGradientStart { get; set; } = ThemePalette.DefaultGradientStart;

    /// <summary>Right colour of the title / menu band (#RRGGBB).</summary>
    public string ThemeGradientEnd { get; set; } = ThemePalette.DefaultGradientEnd;

    /// <summary>Toolbar background tint (#RRGGBB).</summary>
    public string ThemeBackgroundTint { get; set; } = ThemePalette.DefaultBackgroundTint;

    /// <summary>
    /// ON (default, the behaviour before this setting existed): every edit is written at once. OFF: edits are held in memory
    /// until Save, with a prompt before they would be lost.
    /// </summary>
    public bool AutoSave { get; set; } = true;

    /// <summary>Store the image base64-encoded in the LabelMe JSON (<c>imageData</c>) when annotations are written.</summary>
    // Off by default: with 2520x2600 images every autosave would otherwise re-encode the whole image into the JSON (Labelme defaults to on).
    public bool EmbedImageData { get; set; } = false;

    /// <summary>When set, every written LabelMe JSON is also written to this folder (same relative path); Segment AI files stay in the project.</summary>
    public string? OutputDirectory { get; set; }

    /// <summary>Folder EasyVision loads .segai models from; "Send to EasyVision" copies versioned models here. Null or blank = not chosen yet.</summary>
    public string? EasyVisionModelFolder { get; set; }

    /// <summary>
    /// "simple" (guided Easy mode) or "advanced" (full UI). Null = not stored yet; the start-up resolves and saves it (see <c>EasyModeRules.ResolveMode</c>).
    /// </summary>
    public string? UiMode { get; set; }

    // ---------- image source (EAI-CAM K3) ----------

    public const string ImageSourceFiles = "files";
    public const string ImageSourceCameraMvs = "cameraMvs";

    private string _imageSource = ImageSourceFiles;

    /// <summary>Where new images come from: <c>files</c> (default) or <c>cameraMvs</c>. Anything else reads back as <c>files</c>.</summary>
    public string ImageSource
    {
        get => string.Equals(_imageSource, ImageSourceCameraMvs, StringComparison.Ordinal) ? ImageSourceCameraMvs : ImageSourceFiles;
        set => _imageSource = (value ?? string.Empty).Trim();
    }

    /// <summary>Id of the camera chosen in Settings (the one Connect opens); null = none chosen yet.</summary>
    public string? LastCameraId { get; set; }

    // ---------- drawing and canvas (EAI-UI commit C) ----------

    /// <summary>Open the "Shape label" dialog after a new shape was drawn (Labelme default: on).</summary>
    public bool ShowLabelDialog { get; set; } = true;

    /// <summary>"auto" (default), "uniform" or "label". NG/OK keep their colours except in "uniform".</summary>
    public string ShapeColorMode { get; set; } = "auto";

    /// <summary>Auto colours start one palette slot later.</summary>
    public bool ShiftAutoColor { get; set; } = true;

    /// <summary>Colour of every shape in "uniform" mode (#RRGGBB).</summary>
    public string UniformColor { get; set; } = "#00FF00";

    /// <summary>Colour of labels other than NG/OK in "label" mode (#RRGGBB).</summary>
    public string FallbackColor { get; set; } = "#00FF00";

    public bool FillWhileDrawing { get; set; } = true;

    /// <summary>Vertices and the agent's boxes may leave the image, so an object cut by the border keeps its true box in the LabelMe JSON. Training masks cover only the visible part, and YOLO/VOC/COCO/EasyVision exports write the visible (clipped) shape; instances whose centre is outside the image are not trained.</summary>
    public bool AllowOutOfBounds { get; set; } = true;

    /// <summary>BETA: draw the label text near each shape.</summary>
    public bool ShowLabelOnCanvas { get; set; } = true;

    // ---------- auto-label source ----------

    /// <summary>"segment" (default), "ai" or "agent": where the Auto-label panel and the AI tools get suggestions. Unknown text means "segment".</summary>
    public string? AutoLabelSource { get; set; } = "segment";

    /// <summary>"sam2" (default) or "yolo-world": the AI model chosen when the source is "ai". Unknown text means "sam2".</summary>
    public string? AutoLabelAiModel { get; set; } = "sam2";

    /// <summary>The last prompt typed for the Agent Prompt source (empty by default).</summary>
    public string? AgentPrompt { get; set; } = string.Empty;

    /// <summary>The agent program Run and Suggest prompt use: <c>claude</c> (Claude Code, default) or <c>codex</c> (Codex). Only one is used.</summary>
    public string AgentBackend { get; set; } = "claude";

    /// <summary>Box accuracy of an agent run: <c>easy</c>, <c>normal</c> (default) or <c>hard</c>.</summary>
    public string AgentPrecision { get; set; } = "normal";

    /// <summary>
    /// Effort of an agent run: <c>low</c>, <c>medium</c> (default), <c>high</c>, <c>xhigh</c> or <c>max</c>. Older files hold
    /// <c>economy</c>, <c>normal</c> or <c>best</c>; they still load (<c>AgentRunOptions.ParseEffort</c> maps them to low, medium, high).
    /// </summary>
    public string AgentEffort { get; set; } = "medium";

    /// <summary>Model of Claude Code runs (alias such as <c>haiku</c>, <c>sonnet</c>, <c>opus</c>); empty = the default.</summary>
    public string AgentModelClaude { get; set; } = string.Empty;

    /// <summary>Model of Codex runs; empty = the account default.</summary>
    public string AgentModelCodex { get; set; } = string.Empty;

    public const int AgentMaxImagesDefault = 50;
    public const int AgentMaxImagesMin = 1;
    public const int AgentMaxImagesMax = 1000;
    public const double AgentMaxCostUsdDefault = 1.0;
    public const double AgentMaxCostUsdMax = 1000.0;
    public const double AgentMaxUsdPerDayDefault = 5.0;
    public const double AgentMaxUsdPerDayMax = 10000.0;

    public const int AgentBatchWorkersDefault = 1;
    public const int AgentBatchWorkersMin = 1;
    public const int AgentBatchWorkersMax = 32;

    private string _agentBatchMode = "current";
    private string _segAiDevice = SegAiDeviceRules.Cpu;
    private int _agentBatchWorkers = AgentBatchWorkersDefault;
    private int _agentMaxImages = AgentMaxImagesDefault;
    private double _agentMaxCostUsd = AgentMaxCostUsdDefault;
    private bool _agentRouteLocalFirst = true;

    /// <summary>
    /// Economy effort only: an image whose agent run failed (turn limit, time-out) is run once more at Normal effort in the same batch slot
    /// (default on; a retry costs about 0.12 USD per failed image and never happens at any other effort).
    /// </summary>
    public bool AgentBatchRetryNormal { get; set; } = true;

    /// <summary>
    /// After each successful agent run of a batch, every oriented_rectangle proposal is tightened locally with SAM2 (box prompt, best mask,
    /// minimum-area rectangle); no LLM and no cost. A proposal stays as it is when SAM2 is missing or gives no usable box (default on).
    /// </summary>
    public bool AgentBatchSam2Refine { get; set; } = true;

    /// <summary>Remaining mode of an agent batch: an image whose local result is Sure stays with the local suggestions; Doubtful or Unsure images go to the agent (default on).</summary>
    public bool AgentRouteLocalFirst
    {
        get => _agentRouteLocalFirst;
        set => _agentRouteLocalFirst = value;
    }

    /// <summary>The chat "Auto-edit prompt" button: a prompt sub-agent rewrites what the person typed (English command plus an explanation in their language) before the main agent gets it. Default off.</summary>
    public bool AgentAutoRefinePrompt { get; set; }

    /// <summary>Refuse agent processes and online agent services; local workflows remain available. Default off.</summary>
    public bool AgentOfflineOnly { get; set; }

    /// <summary>Extra instruction for the prompt sub-agent (Settings, Claude Code and Codex page); empty = none.</summary>
    public string AgentRefineHint { get; set; } = string.Empty;

    /// <summary>
    /// Advanced opt-in: a YOLO / RT-DETR run may train on the CPU when no NVIDIA GPU is found (n-size detect model only). Default off;
    /// the Train tab applies it only in Advanced mode.
    /// </summary>
    public bool UltraAllowCpu { get; set; }

    /// <summary>
    /// Device Segment AI inspection / evaluation runs on: <c>cpu</c> (default; a settings file written before this key existed reads as cpu),
    /// <c>intel</c> (Intel GPU, OpenVINO) or <c>nvidia</c> (NVIDIA CUDA, ONNX Runtime). Anything else reads back as <c>cpu</c>. Training is not affected.
    /// </summary>
    public string SegAiDevice
    {
        get => SegAiDeviceRules.Normalize(_segAiDevice);
        set => _segAiDevice = (value ?? string.Empty).Trim().ToLowerInvariant();
    }

    /// <summary>Which images an agent batch covers: <c>current</c> (default), <c>all</c> or <c>remaining</c>. Anything else reads back as <c>current</c>.</summary>
    public string AgentBatchMode
    {
        get => _agentBatchMode == "all" || _agentBatchMode == "remaining" ? _agentBatchMode : "current";
        set => _agentBatchMode = (value ?? string.Empty).Trim().ToLowerInvariant();
    }

    /// <summary>
    /// Agent runs a batch keeps going at the same time (default 1 = one after the other, with the experience block). 2 or more label several images at once
    /// without the experience block. Always read back inside 1..32; the batch uses it capped to what this machine can do (see AgentSpeedRules).
    /// </summary>
    public int AgentBatchWorkers
    {
        get => Math.Min(AgentBatchWorkersMax, Math.Max(AgentBatchWorkersMin, _agentBatchWorkers));
        set => _agentBatchWorkers = value;
    }

    /// <summary>Most images one agent batch handles (default 50). Always read back inside 1..1000.</summary>
    public int AgentMaxImages
    {
        get => Math.Min(AgentMaxImagesMax, Math.Max(AgentMaxImagesMin, _agentMaxImages));
        set => _agentMaxImages = value;
    }

    /// <summary>Cost at which an agent batch stops, in US dollars (default 2.00). 0 or less = no cost cap. Always read back inside 0..1000; NaN reads back as the default.</summary>
    public double AgentMaxCostUsd
    {
        get
        {
            if (double.IsNaN(_agentMaxCostUsd))
            {
                return AgentMaxCostUsdDefault;
            }

            return Math.Min(AgentMaxCostUsdMax, Math.Max(0.0, _agentMaxCostUsd));
        }

        set => _agentMaxCostUsd = value;
    }

    private double _agentMaxUsdPerDay = AgentMaxUsdPerDayDefault;

    /// <summary>Cost the agent may spend per local calendar day, in US dollars (default 5.00). 0 or less = no daily cap. Always read back inside 0..10000; NaN reads back as the default.</summary>
    public double AgentMaxUsdPerDay
    {
        get
        {
            if (double.IsNaN(_agentMaxUsdPerDay))
            {
                return AgentMaxUsdPerDayDefault;
            }

            return Math.Min(AgentMaxUsdPerDayMax, Math.Max(0.0, _agentMaxUsdPerDay));
        }

        set => _agentMaxUsdPerDay = value;
    }

    private double? _agentChatLeft;
    private double? _agentChatTop;
    private double _agentChatWidth = AgentChatWindowBounds.DefaultWidth;
    private double _agentChatHeight = AgentChatWindowBounds.DefaultHeight;

    /// <summary>Left edge of the agent chat window; null = never moved (centred on the main window). Not a sane number reads back as null.</summary>
    public double? AgentChatLeft
    {
        get => AgentChatWindowBounds.SanitizeCoordinate(_agentChatLeft);
        set => _agentChatLeft = value;
    }

    /// <summary>Top edge of the agent chat window; null = never moved. Not a sane number reads back as null.</summary>
    public double? AgentChatTop
    {
        get => AgentChatWindowBounds.SanitizeCoordinate(_agentChatTop);
        set => _agentChatTop = value;
    }

    /// <summary>Width of the agent chat window. Always read back inside the window minimum..10000; not a number reads back as the default.</summary>
    public double AgentChatWidth
    {
        get => AgentChatWindowBounds.ClampWidth(_agentChatWidth);
        set => _agentChatWidth = value;
    }

    /// <summary>Height of the agent chat window. Always read back inside the window minimum..10000; not a number reads back as the default.</summary>
    public double AgentChatHeight
    {
        get => AgentChatWindowBounds.ClampHeight(_agentChatHeight);
        set => _agentChatHeight = value;
    }

    /// <summary>The monitor layout the chat window position was saved for (see <see cref="AgentChatWindowBounds.LayoutKey"/>); empty = none.</summary>
    public string? AgentChatScreen { get; set; }

    /// <summary>Names of the EasyAI run skills switched off in Settings (built-in and user folder). Empty by default = every skill is on.</summary>
    public List<string> DisabledRunSkills { get; set; } = new List<string>();

    /// <summary>The Train tab already pre-filled its options from the hardware recommendation once (it never overwrites user-changed options again).</summary>
    public bool TrainRecommendedApplied { get; set; }

    // ---------- continue between images ----------

    public bool KeepPreviousAnnotation { get; set; }

    public bool KeepPreviousZoom { get; set; }

    public bool KeepPreviousBrightness { get; set; }

    // ---------- label sources and behaviour ----------

    /// <summary>Labels offered in the label dialog, separated by commas or new lines.</summary>
    public string PredefinedLabels { get; set; } = string.Empty;

    /// <summary>Predefined image flags (stored; shown by the Flags panel in a later commit).</summary>
    public string PredefinedImageFlags { get; set; } = string.Empty;

    /// <summary>Predefined shape flag rules (stored).</summary>
    public string PredefinedShapeFlags { get; set; } = string.Empty;

    public bool SortLabels { get; set; }

    public bool ShowLabelTextField { get; set; } = true;

    /// <summary>"startsWith" (default) or "contains".</summary>
    public string AutoComplete { get; set; } = "startsWith";

    // ---------- recent projects ----------

    /// <summary>Recently opened projects, newest first (at most <see cref="RecentProjectList.Max"/>). A settings file without this field loads as an empty list.</summary>
    public List<RecentProjectSetting> RecentProjects { get; set; } = new List<RecentProjectSetting>();

    /// <summary>Folder the New Project and Save Project As dialogs offer first (the location of the last project created or saved as). Null or blank means Documents\EasyAI Projects.</summary>
    public string? LastProjectLocation { get; set; }

    public const int CacheMaxMegabytesDefault = 2048;
    public const int CacheMaxMegabytesMin = 64;
    public const int CacheMaxMegabytesMax = 65536;

    private int _cacheMaxMegabytes = CacheMaxMegabytesDefault;

    /// <summary>Size limit of the project cache in megabytes (default 2048). Always read back inside 64..65536, whatever the settings file holds.</summary>
    public int CacheMaxMegabytes
    {
        get => Math.Min(CacheMaxMegabytesMax, Math.Max(CacheMaxMegabytesMin, _cacheMaxMegabytes));
        set => _cacheMaxMegabytes = value;
    }
}

/// <summary>One persisted recent-projects entry. Every field is plain text so a damaged value never stops the settings file from loading.</summary>
public sealed class RecentProjectSetting
{
    public string? Path { get; set; }

    public string? Name { get; set; }

    /// <summary>UTC time in ISO 8601 round-trip format ("O"); unreadable text loads as the minimum time.</summary>
    public string? LastOpenedUtc { get; set; }

    /// <summary>"named" (a .easyai manifest) or "folder" (a legacy image folder); anything else is inferred from the path.</summary>
    public string? Kind { get; set; }

    public static RecentProjectSetting FromEntry(RecentProject entry)
    {
        return new RecentProjectSetting
        {
            Path = entry.Path,
            Name = entry.Name,
            LastOpenedUtc = entry.LastOpenedUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            Kind = entry.Kind == RecentProjectKind.Named ? "named" : "folder",
        };
    }

    /// <summary>The entry, or null when the path is empty. Run the result through <see cref="RecentProjectList.Normalize"/>.</summary>
    public RecentProject? ToEntry()
    {
        if (string.IsNullOrWhiteSpace(Path))
        {
            return null;
        }

        DateTime opened = DateTime.MinValue;
        if (!string.IsNullOrWhiteSpace(LastOpenedUtc)
            && DateTime.TryParse(LastOpenedUtc, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTime parsed))
        {
            opened = parsed;
        }

        RecentProjectKind kind;
        string text = (Kind ?? string.Empty).Trim();
        if (string.Equals(text, "named", StringComparison.OrdinalIgnoreCase)) kind = RecentProjectKind.Named;
        else if (string.Equals(text, "folder", StringComparison.OrdinalIgnoreCase)) kind = RecentProjectKind.Folder;
        else kind = Path.TrimEnd('\\', '/').EndsWith(ProjectManifest.FileExtension, StringComparison.OrdinalIgnoreCase) ? RecentProjectKind.Named : RecentProjectKind.Folder;

        return new RecentProject(Path, Name ?? string.Empty, opened, kind);
    }

    /// <summary>Settings list to entries: tolerates null, drops entries without a path, de-duplicates and caps (see <see cref="RecentProjectList.Normalize"/>).</summary>
    public static IReadOnlyList<RecentProject> ToEntries(IEnumerable<RecentProjectSetting?>? settings)
    {
        var entries = new List<RecentProject>();
        if (settings != null)
        {
            foreach (RecentProjectSetting? setting in settings)
            {
                RecentProject? entry = setting?.ToEntry();
                if (entry != null) entries.Add(entry);
            }
        }

        return RecentProjectList.Normalize(entries);
    }

    public static List<RecentProjectSetting> FromEntries(IEnumerable<RecentProject> entries)
    {
        var result = new List<RecentProjectSetting>();
        foreach (RecentProject entry in entries)
        {
            result.Add(FromEntry(entry));
        }

        return result;
    }
}
