using System.Globalization;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EasyAI.Desktop.Resources;
using EasyLabel.Camera;
using EasyLabel.SegAI;
using InfoBarSeverity = Wpf.Ui.Controls.InfoBarSeverity;

namespace EasyAI.Desktop.ViewModels;

/// <summary>
/// The capture buttons of the Label tab ("Capture new", "Capture to current") and of the Inspect tab ("Capture"). Each one grabs one frame
/// from the shared <see cref="CameraService"/>, writes it into the open project and refreshes the lists. Errors are shown as notices,
/// never thrown; one capture runs at a time.
/// </summary>
public sealed partial class CaptureViewModel : ObservableObject, IDisposable
{
    private readonly MainViewModel _host;
    private readonly CameraService _camera;
    private readonly Dispatcher _dispatcher;
    private bool _overwriteConfirmed;
    private bool _disposed;

    [ObservableProperty]
    private bool _isCapturing;

    public CaptureViewModel(MainViewModel host, CameraService camera)
    {
        _host = host;
        _camera = camera;
        _dispatcher = Dispatcher.CurrentDispatcher;
        _camera.StateChanged -= OnCameraStateChanged;
        _camera.StateChanged += OnCameraStateChanged;
    }

    /// <summary>The buttons are shown only while the camera is the image source.</summary>
    public bool IsVisible => CaptureRules.IsVisible(_host.IsCameraSourceActive);

    public string NewToolTip => ToolTipOf(CaptureRules.BlockKey(BlockOf(false)), Strings.CaptureNewTip);

    public string CurrentToolTip => ToolTipOf(CaptureRules.BlockKey(BlockOf(true)), Strings.CaptureCurrentTip);

    public string InspectToolTip => ToolTipOf(CaptureRules.BlockKey(BlockOf(false)), Strings.CaptureInspectTip);

    public void Dispose()
    {
        _disposed = true;
        _camera.StateChanged -= OnCameraStateChanged;
    }

    /// <summary>Source, project, selection or busy flag changed: re-evaluates the buttons.</summary>
    internal void NotifyStateChanged()
    {
        if (_disposed)
        {
            return;
        }

        OnPropertyChanged(nameof(IsVisible));
        OnPropertyChanged(nameof(NewToolTip));
        OnPropertyChanged(nameof(CurrentToolTip));
        OnPropertyChanged(nameof(InspectToolTip));
        CaptureNewCommand.NotifyCanExecuteChanged();
        CaptureToCurrentCommand.NotifyCanExecuteChanged();
        CaptureToInspectCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsCapturingChanged(bool value)
    {
        NotifyStateChanged();
    }

    private void OnCameraStateChanged(object? sender, EventArgs e)
    {
        // Raised on a worker thread.
        if (_disposed)
        {
            return;
        }

        _dispatcher.BeginInvoke(new Action(NotifyStateChanged));
    }

    private CaptureBlock BlockOf(bool needsImage)
    {
        SegAiSession? session = _host.Session;
        return CaptureRules.BlockOf(
            _camera.State,
            session != null,
            session != null && session.IsTrainingActive,
            IsCapturing || _host.IsBusy,
            needsImage,
            _host.SelectedImage != null);
    }

    private static string ToolTipOf(string? blockKey, string normal)
    {
        return blockKey == null ? normal : Strings.ByName(blockKey);
    }

    private bool CanCaptureNew() => CaptureRules.IsEnabled(BlockOf(false));

    private bool CanCaptureToCurrent() => CaptureRules.IsEnabled(BlockOf(true));

    // ---------- Label tab ----------

    [RelayCommand(CanExecute = nameof(CanCaptureNew))]
    private async Task CaptureNewAsync()
    {
        SegAiSession? session = Begin(false);
        if (session == null)
        {
            return;
        }

        try
        {
            CameraBgrImage? image = await GrabAsync(session);
            if (image == null)
            {
                return;
            }

            string? rel = await WriteNewAsync(session, image);
            if (rel == null)
            {
                return;
            }

            ImageItemViewModel? item = FindItem(rel);
            if (item != null)
            {
                _host.SelectedImage = item;
            }
        }
        catch (Exception ex)
        {
            Fail(ex);
        }
        finally
        {
            IsCapturing = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanCaptureToCurrent))]
    private async Task CaptureToCurrentAsync()
    {
        SegAiSession? session = Begin(true);
        if (session == null)
        {
            return;
        }

        try
        {
            ImageItemViewModel? item = _host.SelectedImage;
            if (item == null)
            {
                return;
            }

            // There is no backup of the original, so the user is warned once per session.
            if (CaptureRules.NeedsOverwriteConfirmation(_overwriteConfirmed))
            {
                if (_host.Dialogs == null || !_host.Dialogs.Confirm(Strings.CaptureOverwriteTitle, Strings.CaptureOverwriteMessage, Strings.CaptureOverwriteConfirm))
                {
                    return;
                }

                _overwriteConfirmed = true;
            }

            CameraBgrImage? image = await GrabAsync(session);
            if (image == null || !ReferenceEquals(item, _host.SelectedImage))
            {
                return;
            }

            string rel = item.Rel;
            string fullPath = item.FullPath;
            SegAiReplaceResult? result = await _host.RunStep1Async(
                s => s.ReplaceImage(rel, image.Bgr, image.Width, image.Height, image.Width * 3),
                true);
            if (result == null)
            {
                return;
            }

            // The caller drops everything derived from the old pixels (the session only drops its own size caches).
            _host.Label.InvalidateImage(rel);
            _host.Inspect.InvalidateImage(fullPath);
            _host.InvalidatePreview(fullPath);

            if (ReferenceEquals(item, _host.SelectedImage))
            {
                // The normal selection path: new pixels and a fresh edit document (labels kept, undo history of the old pixels gone).
                await _host.ReloadSelectedImageAsync();
            }

            string? key = CaptureRules.ReplaceNoticeKey(result.SizeChanged, result.OutOfFrameShapes);
            if (key != null)
            {
                _host.ShowDataNotice(
                    Strings.CaptureReplacedTitle,
                    string.Format(
                        CultureInfo.CurrentCulture,
                        Strings.ByName(key),
                        CaptureRules.SizeText(result.OldSize.Width, result.OldSize.Height),
                        CaptureRules.SizeText(result.NewSize.Width, result.NewSize.Height),
                        result.OutOfFrameShapes),
                    InfoBarSeverity.Warning);
            }
        }
        catch (Exception ex)
        {
            Fail(ex);
        }
        finally
        {
            IsCapturing = false;
        }
    }

    // ---------- Inspect tab ----------

    [RelayCommand(CanExecute = nameof(CanCaptureNew))]
    private async Task CaptureToInspectAsync()
    {
        SegAiSession? session = Begin(false);
        if (session == null)
        {
            return;
        }

        try
        {
            CameraBgrImage? image = await GrabAsync(session);
            if (image == null)
            {
                return;
            }

            string? rel = await WriteNewAsync(session, image);
            if (rel == null)
            {
                return;
            }

            ImageItemViewModel? item = FindItem(rel);
            string fullPath = item != null
                ? item.FullPath
                : System.IO.Path.Combine(session.ProjectRoot, rel.Replace('/', System.IO.Path.DirectorySeparatorChar));
            _host.Inspect.AddCaptured(fullPath);
        }
        catch (Exception ex)
        {
            Fail(ex);
        }
        finally
        {
            IsCapturing = false;
        }
    }

    // ---------- shared steps ----------

    /// <summary>Takes the one-at-a-time flag. Returns the session to capture into, or null (a notice names the reason).</summary>
    private SegAiSession? Begin(bool needsImage)
    {
        CaptureBlock block = BlockOf(needsImage);
        if (block != CaptureBlock.None)
        {
            string? key = CaptureRules.BlockKey(block);
            if (key != null)
            {
                _host.ShowDataNotice(Strings.CaptureErrorTitle, Strings.ByName(key), InfoBarSeverity.Warning);
            }

            return null;
        }

        SegAiSession? session = _host.Session;
        if (session == null)
        {
            return null;
        }

        IsCapturing = true;
        return session;
    }

    /// <summary>Grabs one frame (5 s). Null when the grab failed (notice shown) or the project changed meanwhile.</summary>
    private async Task<CameraBgrImage?> GrabAsync(SegAiSession session)
    {
        CameraBgrImage image;
        try
        {
            image = await _camera.GrabOneAsync(CaptureRules.GrabTimeout);
        }
        catch (Exception ex)
        {
            _host.LogData("Capture grab failed: " + ex.Message);
            _host.ShowDataNotice(
                Strings.CaptureErrorTitle,
                string.Format(CultureInfo.CurrentCulture, Strings.ByName(CaptureRules.GrabFailureKey(ex)), ex.Message),
                InfoBarSeverity.Error);
            return null;
        }

        return !_disposed && ReferenceEquals(session, _host.Session) ? image : null;
    }

    /// <summary>Adds the frame to the project (the image list is reloaded; a failure shows a notice). Returns the project-relative path, or null.</summary>
    private async Task<string?> WriteNewAsync(SegAiSession session, CameraBgrImage image)
    {
        return await _host.RunStep1Async(s => s.AddCapturedFrame(image.Bgr, image.Width, image.Height, image.Width * 3), true);
    }

    private ImageItemViewModel? FindItem(string rel)
    {
        return _host.Images.FirstOrDefault(i => string.Equals(i.Rel, rel, StringComparison.OrdinalIgnoreCase));
    }

    private void Fail(Exception ex)
    {
        _host.LogData("Capture failed: " + ex);
        _host.ShowDataNotice(Strings.CaptureErrorTitle, ex.Message, InfoBarSeverity.Error);
    }
}
