using EasyAI.Desktop.Infrastructure;

namespace EasyLabel.SegAI.Tests;

public sealed class CameraSettingsTests
{
    [Fact]
    public void ImageSource_DefaultsToFiles_AndUnknownTextReadsBackAsFiles()
    {
        AppSettings s = new AppSettings();

        Assert.Equal("files", s.ImageSource);
        Assert.Null(s.LastCameraId);

        s.ImageSource = "cameraMvs";
        Assert.Equal("cameraMvs", s.ImageSource);
        s.ImageSource = " cameraMvs ";
        Assert.Equal("cameraMvs", s.ImageSource);
        s.ImageSource = "webcam";
        Assert.Equal("files", s.ImageSource);
        s.ImageSource = null!;
        Assert.Equal("files", s.ImageSource);
    }

    [Fact]
    public void Accessor_StoresOnlyKnownSourcesAndAnyDeviceId()
    {
        AppSettings s = new AppSettings();

        Assert.True(SettingsAccessor.TryGetString(s, SettingsAccessor.RowCameraSource, out string source));
        Assert.Equal("files", source);
        Assert.True(SettingsAccessor.TrySetString(s, SettingsAccessor.RowCameraSource, "cameraMvs"));
        Assert.Equal("cameraMvs", s.ImageSource);
        Assert.False(SettingsAccessor.TrySetString(s, SettingsAccessor.RowCameraSource, "webcam"));
        Assert.Equal("cameraMvs", s.ImageSource);

        Assert.True(SettingsAccessor.TryGetString(s, SettingsAccessor.RowCameraDevice, out string device));
        Assert.Equal(string.Empty, device);
        Assert.True(SettingsAccessor.TrySetString(s, SettingsAccessor.RowCameraDevice, "GEV:1234"));
        Assert.Equal("GEV:1234", s.LastCameraId);
        Assert.True(SettingsAccessor.TrySetString(s, SettingsAccessor.RowCameraDevice, "  "));
        Assert.Null(s.LastCameraId);
    }

    [Fact]
    public void CameraPage_HasTheSevenRowsWithTheirKinds()
    {
        IReadOnlyList<SettingPageModel> pages = SettingsCatalog.Build(key => key, Array.Empty<AiModelStatus>());
        SettingPageModel page = pages.Single(p => p.Id == SettingsCatalog.PageCamera);

        Assert.Equal(
            new[]
            {
                SettingsAccessor.RowCameraSource,
                SettingsAccessor.RowCameraDevice,
                SettingsAccessor.RowCameraRefresh,
                SettingsAccessor.RowCameraConnect,
                SettingsAccessor.RowCameraDisconnect,
                SettingsAccessor.RowCameraStatus,
                SettingsAccessor.RowCameraError,
            },
            page.Rows.Select(r => r.Id).ToArray());
        Assert.Equal(SettingKind.Choice, page.Rows[0].Kind);
        Assert.Equal(new[] { "files", "cameraMvs" }, page.Rows[0].Options.Select(o => o.Value).ToArray());
        Assert.Equal(SettingKind.Choice, page.Rows[1].Kind);
        Assert.Equal(SettingKind.Button, page.Rows[2].Kind);
        Assert.Equal(SettingKind.Button, page.Rows[3].Kind);
        Assert.Equal(SettingKind.Button, page.Rows[4].Kind);
        Assert.Equal(SettingKind.Info, page.Rows[5].Kind);
        Assert.Equal(SettingKind.Info, page.Rows[6].Kind);
        Assert.True(SettingsAccessor.IsCameraButtonRow(SettingsAccessor.RowCameraConnect));
        Assert.False(SettingsAccessor.IsCameraButtonRow(SettingsAccessor.RowCameraStatus));
    }
}
