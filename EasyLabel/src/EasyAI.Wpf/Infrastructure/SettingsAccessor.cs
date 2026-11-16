namespace EasyAI.Desktop.Infrastructure;

/// <summary>Maps a Settings dialog row id to a property of <see cref="AppSettings"/> (UI-free, unit tested).</summary>
public static class SettingsAccessor
{
    public const string RowShowLabelDialog = "showLabelDialog";
    public const string RowShapeColorMode = "shapeColorMode";
    public const string RowShiftAutoColor = "shiftAutoColor";
    public const string RowUniformColor = "uniformColor";
    public const string RowFallbackColor = "fallbackColor";
    public const string RowFillWhileDrawing = "fillWhileDrawing";
    public const string RowAllowOutOfBounds = "allowOutOfBounds";
    public const string RowShowLabelOnCanvas = "showLabelOnCanvas";
    public const string RowKeepAnnotation = "keepAnnotation";
    public const string RowKeepZoom = "keepZoom";
    public const string RowKeepBrightness = "keepBrightness";
    public const string RowPredefinedLabels = "predefinedLabels";
    public const string RowPredefinedImageFlags = "predefinedImageFlags";
    public const string RowPredefinedShapeFlags = "predefinedShapeFlags";
    public const string RowSortLabels = "sortLabels";
    public const string RowShowLabelTextField = "showLabelTextField";
    public const string RowAutoComplete = "autoComplete";
    public const string RowThemeAccent = "themeAccent";
    public const string RowThemeGradientStart = "themeGradientStart";
    public const string RowThemeGradientEnd = "themeGradientEnd";
    public const string RowThemeBackground = "themeBackground";
    public const string RowThemeReset = "themeReset";
    public const string RowCacheSize = "cacheSize";
    public const string RowCacheMax = "cacheMax";
    public const string RowCacheClear = "cacheClear";

    /// <summary>Button of the AI models page: installs a model pack (zip) into the models folder.</summary>
    public const string RowInstallModelPack = "installModelPack";

    /// <summary>Advanced opt-in toggle: YOLO / RT-DETR may train on the CPU when no NVIDIA GPU is found (see <see cref="AppSettings.UltraAllowCpu"/>).</summary>
    public const string RowUltraAllowCpu = "ultraAllowCpu";

    /// <summary>Choice row of the AI models page: CPU / Intel GPU / NVIDIA CUDA for Segment AI (see <see cref="AppSettings.SegAiDevice"/>).</summary>
    public const string RowSegAiDevice = "segAiDevice";
    public const string RowAgentOfflineOnly = "agentOfflineOnly";
    public const string RowAgentMaxImages = "agentMaxImages";
    public const string RowAgentBatchWorkers = "agentBatchWorkers";
    public const string RowAgentMaxCost = "agentMaxCost";
    public const string RowAgentMaxUsdPerDay = "agentMaxUsdPerDay";
    public const string RowAgentRouteLocalFirst = "agentRouteLocalFirst";
    public const string RowAgentBatchRetryNormal = "agentBatchRetryNormal";
    public const string RowAgentBatchSam2Refine = "agentBatchSam2Refine";
    public const string RowCameraSource = "cameraSource";
    public const string RowCameraDevice = "cameraDevice";
    public const string RowCameraRefresh = "cameraRefresh";
    public const string RowCameraConnect = "cameraConnect";
    public const string RowCameraDisconnect = "cameraDisconnect";
    public const string RowCameraStatus = "cameraStatus";
    public const string RowCameraError = "cameraError";
    public const string RowEasyVisionFolder = "easyVisionFolder";
    public const string RowEasyVisionFolderBrowse = "easyVisionFolderBrowse";
    public const string RowCounterAiWeights = "counterAiWeights";
    public const string RowCounterAiWeightsBrowse = "counterAiWeightsBrowse";

    /// <summary>True for the button rows that run <c>CameraActionCommand</c>: the three of the Camera page and the Browse buttons of the EasyVision folder / FastNet weights rows.</summary>
    public static bool IsCameraButtonRow(string id)
    {
        return id == RowCameraRefresh || id == RowCameraConnect || id == RowCameraDisconnect || id == RowEasyVisionFolderBrowse || id == RowCounterAiWeightsBrowse;
    }

    /// <summary>True for the four colour rows of the theme palette.</summary>
    public static bool IsThemeColorRow(string id)
    {
        return id == RowThemeAccent || id == RowThemeGradientStart || id == RowThemeGradientEnd || id == RowThemeBackground;
    }

    /// <summary>The stored palette as a <see cref="ThemePalette"/> (invalid stored colours fall back to the logo colours).</summary>
    public static ThemePalette PaletteOf(AppSettings s)
    {
        return ThemePalette.FromSettings(s.ThemeAccentColor, s.ThemeGradientStart, s.ThemeGradientEnd, s.ThemeBackgroundTint);
    }

    /// <summary>Puts the logo colours back (the "Reset to logo" button).</summary>
    public static void ResetPalette(AppSettings s)
    {
        s.ThemeAccentColor = ThemePalette.DefaultAccent;
        s.ThemeGradientStart = ThemePalette.DefaultGradientStart;
        s.ThemeGradientEnd = ThemePalette.DefaultGradientEnd;
        s.ThemeBackgroundTint = ThemePalette.DefaultBackgroundTint;
    }

    public static bool TryGetBool(AppSettings s, string id, out bool value)
    {
        value = false;
        switch (id)
        {
            case RowShowLabelDialog: value = s.ShowLabelDialog; return true;
            case RowShiftAutoColor: value = s.ShiftAutoColor; return true;
            case RowFillWhileDrawing: value = s.FillWhileDrawing; return true;
            case RowAllowOutOfBounds: value = s.AllowOutOfBounds; return true;
            case RowShowLabelOnCanvas: value = s.ShowLabelOnCanvas; return true;
            case RowKeepAnnotation: value = s.KeepPreviousAnnotation; return true;
            case RowKeepZoom: value = s.KeepPreviousZoom; return true;
            case RowKeepBrightness: value = s.KeepPreviousBrightness; return true;
            case RowSortLabels: value = s.SortLabels; return true;
            case RowShowLabelTextField: value = s.ShowLabelTextField; return true;
            case RowAgentOfflineOnly: value = s.AgentOfflineOnly; return true;
            case RowAgentRouteLocalFirst: value = s.AgentRouteLocalFirst; return true;
            case RowAgentBatchRetryNormal: value = s.AgentBatchRetryNormal; return true;
            case RowAgentBatchSam2Refine: value = s.AgentBatchSam2Refine; return true;
            case RowUltraAllowCpu: value = s.UltraAllowCpu; return true;
            default: return false;
        }
    }

    public static bool TrySetBool(AppSettings s, string id, bool value)
    {
        switch (id)
        {
            case RowShowLabelDialog: s.ShowLabelDialog = value; return true;
            case RowShiftAutoColor: s.ShiftAutoColor = value; return true;
            case RowFillWhileDrawing: s.FillWhileDrawing = value; return true;
            case RowAllowOutOfBounds: s.AllowOutOfBounds = value; return true;
            case RowShowLabelOnCanvas: s.ShowLabelOnCanvas = value; return true;
            case RowKeepAnnotation: s.KeepPreviousAnnotation = value; return true;
            case RowKeepZoom: s.KeepPreviousZoom = value; return true;
            case RowKeepBrightness: s.KeepPreviousBrightness = value; return true;
            case RowSortLabels: s.SortLabels = value; return true;
            case RowShowLabelTextField: s.ShowLabelTextField = value; return true;
            case RowAgentOfflineOnly: s.AgentOfflineOnly = value; return true;
            case RowAgentRouteLocalFirst: s.AgentRouteLocalFirst = value; return true;
            case RowAgentBatchRetryNormal: s.AgentBatchRetryNormal = value; return true;
            case RowAgentBatchSam2Refine: s.AgentBatchSam2Refine = value; return true;
            case RowUltraAllowCpu: s.UltraAllowCpu = value; return true;
            default: return false;
        }
    }

    public static bool TryGetString(AppSettings s, string id, out string value)
    {
        value = string.Empty;
        switch (id)
        {
            case RowShapeColorMode: value = ShapeColorRules.NormalizeMode(s.ShapeColorMode); return true;
            case RowUniformColor: value = s.UniformColor ?? string.Empty; return true;
            case RowFallbackColor: value = s.FallbackColor ?? string.Empty; return true;
            case RowThemeAccent: value = ThemePalette.Normalize(s.ThemeAccentColor, ThemePalette.DefaultAccent); return true;
            case RowThemeGradientStart: value = ThemePalette.Normalize(s.ThemeGradientStart, ThemePalette.DefaultGradientStart); return true;
            case RowThemeGradientEnd: value = ThemePalette.Normalize(s.ThemeGradientEnd, ThemePalette.DefaultGradientEnd); return true;
            case RowThemeBackground: value = ThemePalette.Normalize(s.ThemeBackgroundTint, ThemePalette.DefaultBackgroundTint); return true;
            case RowPredefinedLabels: value = s.PredefinedLabels ?? string.Empty; return true;
            case RowPredefinedImageFlags: value = s.PredefinedImageFlags ?? string.Empty; return true;
            case RowPredefinedShapeFlags: value = s.PredefinedShapeFlags ?? string.Empty; return true;
            case RowAutoComplete: value = LabelListRules.NormalizeAutoComplete(s.AutoComplete); return true;
            case RowCacheMax: value = s.CacheMaxMegabytes.ToString(System.Globalization.CultureInfo.InvariantCulture); return true;
            case RowAgentMaxImages: value = s.AgentMaxImages.ToString(System.Globalization.CultureInfo.InvariantCulture); return true;
            case RowAgentBatchWorkers: value = s.AgentBatchWorkers.ToString(System.Globalization.CultureInfo.InvariantCulture); return true;
            case RowAgentMaxCost: value = s.AgentMaxCostUsd.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture); return true;
            case RowAgentMaxUsdPerDay: value = s.AgentMaxUsdPerDay.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture); return true;
            case RowCameraSource: value = s.ImageSource; return true;
            case RowCameraDevice: value = s.LastCameraId ?? string.Empty; return true;
            case RowEasyVisionFolder: value = s.EasyVisionModelFolder ?? string.Empty; return true;
            case RowCounterAiWeights: value = s.CounterAiWeights ?? string.Empty; return true;
            case RowSegAiDevice: value = s.SegAiDevice; return true;
            default: return false;
        }
    }

    /// <summary>Stores a text / choice value. Colours must be valid hex; unknown ids and invalid values return false and change nothing.</summary>
    public static bool TrySetString(AppSettings s, string id, string? value)
    {
        string text = (value ?? string.Empty).Trim();
        switch (id)
        {
            case RowShapeColorMode:
                if (!ShapeColorRules.IsMode(text)) return false;
                s.ShapeColorMode = text;
                return true;
            case RowUniformColor:
                if (!ShapeColorRules.TryParseColor(text, out _)) return false;
                s.UniformColor = text;
                return true;
            case RowFallbackColor:
                if (!ShapeColorRules.TryParseColor(text, out _)) return false;
                s.FallbackColor = text;
                return true;
            case RowThemeAccent:
                if (!ThemePalette.TryParseHex(text, out uint accent)) return false;
                s.ThemeAccentColor = ThemePalette.ToHex(accent);
                return true;
            case RowThemeGradientStart:
                if (!ThemePalette.TryParseHex(text, out uint gradientStart)) return false;
                s.ThemeGradientStart = ThemePalette.ToHex(gradientStart);
                return true;
            case RowThemeGradientEnd:
                if (!ThemePalette.TryParseHex(text, out uint gradientEnd)) return false;
                s.ThemeGradientEnd = ThemePalette.ToHex(gradientEnd);
                return true;
            case RowThemeBackground:
                if (!ThemePalette.TryParseHex(text, out uint tint)) return false;
                s.ThemeBackgroundTint = ThemePalette.ToHex(tint);
                return true;
            case RowSegAiDevice:
                if (!SegAiDeviceRules.IsDevice(text)) return false;
                s.SegAiDevice = text;
                return true;
            case RowPredefinedLabels: s.PredefinedLabels = value ?? string.Empty; return true;
            case RowPredefinedImageFlags: s.PredefinedImageFlags = value ?? string.Empty; return true;
            case RowPredefinedShapeFlags: s.PredefinedShapeFlags = value ?? string.Empty; return true;
            case RowAutoComplete:
                if (text != "startsWith" && text != "contains") return false;
                s.AutoComplete = text;
                return true;
            case RowCacheMax:
                if (!int.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int megabytes)) return false;
                s.CacheMaxMegabytes = Math.Min(AppSettings.CacheMaxMegabytesMax, Math.Max(AppSettings.CacheMaxMegabytesMin, megabytes));
                return true;
            case RowAgentMaxImages:
                if (!int.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int images)) return false;
                s.AgentMaxImages = Math.Min(AppSettings.AgentMaxImagesMax, Math.Max(AppSettings.AgentMaxImagesMin, images));
                return true;
            case RowAgentBatchWorkers:
                if (!int.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int workers)) return false;
                s.AgentBatchWorkers = Math.Min(AppSettings.AgentBatchWorkersMax, Math.Max(AppSettings.AgentBatchWorkersMin, workers));
                return true;
            case RowAgentMaxCost:
                // A comma is accepted as the decimal mark; 0 (or less) switches the cost cap off.
                if (!double.TryParse(text.Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double dollars)
                    || double.IsNaN(dollars) || double.IsInfinity(dollars)) return false;
                s.AgentMaxCostUsd = Math.Min(AppSettings.AgentMaxCostUsdMax, Math.Max(0.0, dollars));
                return true;
            case RowAgentMaxUsdPerDay:
                // Same rules as the batch cap: a comma is a decimal mark, 0 (or less) = no daily cap.
                if (!double.TryParse(text.Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double perDay)
                    || double.IsNaN(perDay) || double.IsInfinity(perDay)) return false;
                s.AgentMaxUsdPerDay = Math.Min(AppSettings.AgentMaxUsdPerDayMax, Math.Max(0.0, perDay));
                return true;
            case RowCameraSource:
                if (text !=AppSettings.ImageSourceFiles && text != AppSettings.ImageSourceCameraMvs) return false;
                s.ImageSource = text;
                return true;
            case RowCameraDevice:
                s.LastCameraId = text.Length == 0 ? null : text;
                return true;
            case RowEasyVisionFolder:
                s.EasyVisionModelFolder = text.Length == 0 ? null : text;
                return true;
            case RowCounterAiWeights:
                s.CounterAiWeights = text.Length == 0 ? null : text;
                return true;
            default: return false;
        }
    }
}
