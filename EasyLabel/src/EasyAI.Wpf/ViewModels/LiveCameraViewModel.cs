using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EasyAI.Desktop.Resources;
using EasyLabel.Camera;
using InfoBarSeverity = Wpf.Ui.Controls.InfoBarSeverity;

namespace EasyAI.Desktop.ViewModels;

/// <summary>
/// State of the Live camera tab: the preview (latest-frame-wins, at most 30 fps, drawn on the UI thread), Start / Stop / Snapshot,
/// the connection banner and the "Set parameter" rows. Owned by <see cref="MainViewModel"/>; the camera itself is the shared
/// <see cref="CameraService"/>. The service pumps frames only while this view model is subscribed, which is only while the tab is
/// shown, the camera is connected, live is wanted and no snapshot is frozen.
/// </summary>
public sealed partial class LiveCameraViewModel : ObservableObject, IDisposable
{
    private const int SnapshotTimeoutMs = 5000;

    private readonly MainViewModel _host;
    private readonly CameraService _camera;
    private readonly Dispatcher _dispatcher;
    private readonly LiveFrameGate _gate = new LiveFrameGate(LiveCameraRules.MaxPreviewFps);
    private readonly SemaphoreSlim _paramLock = new SemaphoreSlim(1, 1);
    private readonly Action _present;
    private byte[]? _buffer;
    private int _bufferWidth;
    private int _bufferHeight;
    private WriteableBitmap? _bitmap;
    private bool _tabActive;
    private bool _subscribed;
    private bool _wasConnected;
    private bool _disposed;
    private int _stateRefreshQueued;
    private int _pendingParamOps;

    [ObservableProperty]
    private ImageSource? _preview;

    [ObservableProperty]
    private bool _isConnected;

    [ObservableProperty]
    private bool _isLiveWanted = true;

    [ObservableProperty]
    private bool _isFrozen;

    [ObservableProperty]
    private bool _isGrabbing;

    [ObservableProperty]
    private bool _showBanner;

    [ObservableProperty]
    private string _bannerTitle = string.Empty;

    [ObservableProperty]
    private string _bannerText = string.Empty;

    [ObservableProperty]
    private InfoBarSeverity _bannerSeverity = InfoBarSeverity.Informational;

    [ObservableProperty]
    private string _actionText = string.Empty;

    [ObservableProperty]
    private bool _paramsBusy;

    public LiveCameraViewModel(MainViewModel host, CameraService camera)
    {
        _host = host;
        _camera = camera;
        _dispatcher = Dispatcher.CurrentDispatcher;
        _present = Present;
        _camera.StateChanged -= OnCameraStateChanged;
        _camera.StateChanged += OnCameraStateChanged;
    }

    /// <summary>The parameter rows (supported parameters only). Kept while the camera is away; the panel is greyed then.</summary>
    public ObservableCollection<LiveParamRowViewModel> Rows { get; } = new ObservableCollection<LiveParamRowViewModel>();

    /// <summary>The frame of the last Snapshot (a BGR copy the caller owns), for the capture buttons; null before the first one.</summary>
    public CameraBgrImage? LastSnapshot { get; private set; }

    public bool HasActionText => !string.IsNullOrEmpty(ActionText);

    /// <summary>Start is offered when live is stopped or a snapshot is shown.</summary>
    public bool CanStartLive => IsConnected && (!IsLiveWanted || IsFrozen);

    public bool CanStopLive => IsConnected && IsLiveWanted && !IsFrozen;

    public bool CanSnapshot => IsConnected && !IsFrozen && !IsGrabbing;

    partial void OnIsConnectedChanged(bool value)
    {
        RaiseCommandState();
        SyncStreaming();
    }

    partial void OnIsLiveWantedChanged(bool value)
    {
        RaiseCommandState();
        SyncStreaming();
    }

    partial void OnIsFrozenChanged(bool value)
    {
        RaiseCommandState();
        SyncStreaming();
    }

    partial void OnIsGrabbingChanged(bool value)
    {
        RaiseCommandState();
    }

    partial void OnActionTextChanged(string value)
    {
        OnPropertyChanged(nameof(HasActionText));
    }

    private void RaiseCommandState()
    {
        OnPropertyChanged(nameof(CanStartLive));
        OnPropertyChanged(nameof(CanStopLive));
        OnPropertyChanged(nameof(CanSnapshot));
        StartLiveCommand.NotifyCanExecuteChanged();
        StopLiveCommand.NotifyCanExecuteChanged();
        SnapshotCommand.NotifyCanExecuteChanged();
    }

    // ---- tab lifetime ----

    /// <summary>The step strip changed tab. Opening the tab starts live (when connected); leaving it stops the frame pump.</summary>
    internal void OnStepChanged(WorkflowStep step)
    {
        bool active = step == WorkflowStep.Live;
        if (active == _tabActive || _disposed)
        {
            return;
        }

        _tabActive = active;
        if (active)
        {
            ActionText = string.Empty;
            IsFrozen = false;
            IsLiveWanted = true;
            // Forces RefreshState to re-read the parameters when the camera is connected.
            _wasConnected = false;
            RefreshState();
        }

        SyncStreaming();
    }

    /// <summary>Subscribes to the frame stream exactly when it is wanted; unsubscribing stops the camera pump.</summary>
    private void SyncStreaming()
    {
        bool want = !_disposed && _tabActive && IsConnected && IsLiveWanted && !IsFrozen;
        if (want == _subscribed)
        {
            return;
        }

        _camera.FrameReceived -= OnFrame;
        if (want)
        {
            _camera.FrameReceived += OnFrame;
        }

        _subscribed = want;
    }

    private void OnCameraStateChanged(object? sender, EventArgs e)
    {
        if (_disposed || Interlocked.Exchange(ref _stateRefreshQueued, 1) == 1)
        {
            return;
        }

        _dispatcher.BeginInvoke(new Action(RefreshState));
    }

    private void RefreshState()
    {
        Interlocked.Exchange(ref _stateRefreshQueued, 0);
        if (_disposed)
        {
            return;
        }

        bool connected = _camera.IsConnected;
        if (connected)
        {
            ShowBanner = false;
        }
        else
        {
            string status = string.Format(
                CultureInfo.CurrentCulture,
                Strings.ByName(_camera.StatusKey),
                _camera.DeviceName ?? string.Empty,
                _camera.ReconnectAttempt,
                CameraService.MaxReconnectAttempts);
            BannerTitle = Strings.LiveBannerTitle;
            BannerText = LiveCameraRules.BannerText(status, _camera.ErrorText);
            BannerSeverity = _camera.State == CameraServiceState.Faulted && _camera.ReconnectAttempt == 0
                ? InfoBarSeverity.Warning
                : InfoBarSeverity.Informational;
            ShowBanner = true;
        }

        IsConnected = connected;
        if (!connected)
        {
            ClearPreview();
        }
        else if (!_wasConnected && _tabActive)
        {
            // A new connection (first or after a reconnect): the camera may have other ranges.
            _ = RunParameterOperationAsync(null, null);
        }

        _wasConnected = connected;
    }

    // ---- preview ----

    private void OnFrame(object? sender, CameraFrameEventArgs e)
    {
        // Worker thread. The frame is valid only here, so it is converted into the one reusable buffer now; the gate keeps the next
        // frame out until the UI thread has drawn this one (the buffer is never written while it is read).
        if (!_gate.TryAccept(Environment.TickCount64))
        {
            return;
        }

        bool queued = false;
        try
        {
            CameraFrame frame = e.Frame;
            int width = frame.Width;
            int height = frame.Height;
            int size = LiveCameraRules.BufferSize(width, height);
            byte[]? buffer = _buffer;
            if (buffer == null || buffer.Length < size)
            {
                buffer = new byte[size];
                _buffer = buffer;
            }

            CameraFrameConverter.ToBgr24(frame, buffer.AsSpan(0, size));
            _bufferWidth = width;
            _bufferHeight = height;
            _dispatcher.BeginInvoke(DispatcherPriority.Background, _present);
            queued = true;
        }
        catch (Exception)
        {
            // A frame that cannot be shown (unknown pixel format, bad size) is skipped.
        }
        finally
        {
            if (!queued)
            {
                _gate.Completed();
            }
        }
    }

    private void Present()
    {
        try
        {
            byte[]? buffer = _buffer;
            if (_disposed || !_subscribed || IsFrozen || buffer == null)
            {
                return;
            }

            ShowBgr(buffer, _bufferWidth, _bufferHeight);
        }
        catch (Exception)
        {
            // Size changed under a queued draw: the next frame fixes it.
        }
        finally
        {
            _gate.Completed();
        }
    }

    private void ShowBgr(byte[] bgr, int width, int height)
    {
        if (LiveCameraRules.NeedsNewBitmap(_bitmap?.PixelWidth ?? 0, _bitmap?.PixelHeight ?? 0, width, height))
        {
            _bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgr24, null);
            Preview = _bitmap;
        }

        _bitmap!.WritePixels(new Int32Rect(0, 0, width, height), bgr, LiveCameraRules.Stride(width), 0);
    }

    private void ClearPreview()
    {
        _bitmap = null;
        Preview = null;
        IsFrozen = false;
    }

    // ---- commands ----

    [RelayCommand(CanExecute = nameof(CanStartLive))]
    private void StartLive()
    {
        ActionText = string.Empty;
        IsFrozen = false;
        IsLiveWanted = true;
    }

    [RelayCommand(CanExecute = nameof(CanStopLive))]
    private void StopLive()
    {
        IsLiveWanted = false;
    }

    /// <summary>Back to live after a snapshot.</summary>
    [RelayCommand]
    private void Resume()
    {
        ActionText = string.Empty;
        IsFrozen = false;
        IsLiveWanted = true;
    }

    [RelayCommand(CanExecute = nameof(CanSnapshot))]
    private async Task SnapshotAsync()
    {
        IsGrabbing = true;
        ActionText = string.Empty;
        try
        {
            CameraBgrImage image = await _camera.GrabOneAsync(TimeSpan.FromMilliseconds(SnapshotTimeoutMs));
            if (_disposed)
            {
                return;
            }

            LastSnapshot = image;
            IsFrozen = true;
            ShowBgr(image.Bgr, image.Width, image.Height);
        }
        catch (Exception ex)
        {
            ActionText = string.Format(CultureInfo.CurrentCulture, Strings.LiveSnapshotFailedFormat, ex.Message);
        }
        finally
        {
            IsGrabbing = false;
        }
    }

    [RelayCommand]
    private void OpenCameraSettings()
    {
        _host.Dialogs?.ShowSettingsPage(EasyAI.Desktop.Infrastructure.SettingsCatalog.PageCamera);
    }

    [RelayCommand]
    private async Task RefreshParamsAsync()
    {
        await RunParameterOperationAsync(null, null);
    }

    // ---- parameters ----

    internal void CommitValue(LiveParamRowViewModel row, double value)
    {
        CameraParameterId id = row.Id;
        row.SetError(null);
        _ = RunParameterOperationAsync(row, p => p.SetAsync(id, value, CancellationToken.None));
    }

    internal void CommitAuto(LiveParamRowViewModel row, bool auto)
    {
        CameraParameterId id = row.Id;
        row.SetError(null);
        _ = RunParameterOperationAsync(row, p => p.SetAutoAsync(id, auto, CancellationToken.None));
    }

    /// <summary>
    /// Runs an optional write on a worker, then re-reads every parameter (the camera may have clamped the value, and ranges depend on
    /// each other, for example the offsets on the size). Operations run one at a time. Never throws. Roi and pixel format writes stop and
    /// restart the acquisition inside the camera source, so the live stream continues by itself.
    /// </summary>
    private async Task RunParameterOperationAsync(LiveParamRowViewModel? row, Func<ICameraParameters, Task<CameraParameter>>? write)
    {
        ICameraParameters? parameters = _camera.Parameters;
        if (parameters == null || _disposed)
        {
            return;
        }

        _pendingParamOps++;
        ParamsBusy = true;
        await _paramLock.WaitAsync();
        try
        {
            string? error = null;
            CameraParameter? written = null;
            IReadOnlyList<CameraParameter>? all = null;
            try
            {
                all = await Task.Run<IReadOnlyList<CameraParameter>>(async () =>
                {
                    if (write != null)
                    {
                        written = await write(parameters).ConfigureAwait(false);
                    }

                    return await parameters.RefreshAsync(CancellationToken.None).ConfigureAwait(false);
                });
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }

            if (_disposed || !ReferenceEquals(_camera.Parameters, parameters))
            {
                return;
            }

            if (all != null)
            {
                ApplyParameters(all);
            }

            if (row != null)
            {
                row.SetError(error ?? written?.Error);
            }
            else if (error != null)
            {
                ActionText = error;
            }
        }
        finally
        {
            _pendingParamOps--;
            if (_pendingParamOps == 0)
            {
                ParamsBusy = false;
            }

            _paramLock.Release();
        }
    }

    private void ApplyParameters(IReadOnlyList<CameraParameter> parameters)
    {
        IReadOnlyList<LiveParamRowModel> models = LiveCameraRules.BuildRows(parameters);
        bool same = models.Count == Rows.Count;
        for (int i = 0; same && i < models.Count; i++)
        {
            same = Rows[i].Id == models[i].Id && Rows[i].Kind == models[i].Kind;
        }

        if (same)
        {
            for (int i = 0; i < models.Count; i++)
            {
                Rows[i].Update(models[i]);
            }

            return;
        }

        Rows.Clear();
        foreach (LiveParamRowModel model in models)
        {
            Rows.Add(model.Kind == CameraParameterKind.Option
                ? (LiveParamRowViewModel)new LiveOptionParamViewModel(this, model)
                : new LiveNumberParamViewModel(this, model));
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _camera.StateChanged -= OnCameraStateChanged;
        _camera.FrameReceived -= OnFrame;
        _subscribed = false;
    }
}
