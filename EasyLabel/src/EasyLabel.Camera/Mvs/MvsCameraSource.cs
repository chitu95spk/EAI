using System.Runtime.InteropServices;

namespace EasyLabel.Camera.Mvs;

/// <summary>
/// One MVS camera, polled with MV_CC_GetOneFrameTimeout on a worker thread (no native callback). The SDK copies the
/// frame into a pinned buffer owned by this object, then the pixels are copied once more into a pooled
/// <see cref="CameraFrame"/>. Real-camera behaviour is unverified; see the K1 evidence notes.
/// </summary>
public sealed class MvsCameraSource : CameraSourceBase, ICameraParameters
{
    private const uint PollTimeoutMilliseconds = 100;

    private readonly byte[]? _rawDeviceInfo;
    private readonly Func<MvsSdkLoadResult> _loader;
    private MV_FRAME_OUT_INFO_EX _frameInfo = MvsStructFactory.FrameInfo();
    private IntPtr _handle;
    private IntPtr _deviceInfoPtr;
    private byte[]? _buffer;
    private IntPtr _bufferPtr;
    private MvsNodeMap? _nodes;
    private CameraParameterEngine? _engine;

    internal MvsCameraSource(CameraInfo info, byte[]? rawDeviceInfo, Func<MvsSdkLoadResult> loader)
        : base(info)
    {
        _rawDeviceInfo = rawDeviceInfo;
        _loader = loader;
    }

    // ---- device hooks (SdkGate held by the base class) ----

    protected override void OnOpen(CancellationToken cancellationToken)
    {
        MvsSdkLoadResult load = _loader();
        if (!load.IsLoaded)
        {
            throw new InvalidOperationException("MVS runtime is not available: " + load.Message);
        }

        if (_rawDeviceInfo == null || _rawDeviceInfo.Length < MvsNativeSizes.DeviceInfo)
        {
            throw new InvalidOperationException(
                "Camera '" + Info.Id + "' was not found by the last enumeration. Refresh the device list and try again.");
        }

        MvsRuntime.EnsureInitialized();

        // The SDK may keep reading the device record after CreateHandle; keep the copy until OnClose.
        _deviceInfoPtr = Marshal.AllocHGlobal(MvsNativeSizes.DeviceInfo);
        Marshal.Copy(_rawDeviceInfo, 0, _deviceInfoPtr, MvsNativeSizes.DeviceInfo);

        IntPtr handle = IntPtr.Zero;
        int rc = MvsNative.MV_CC_CreateHandle(ref handle, _deviceInfoPtr);
        if (rc != MvsRuntime.MvOk)
        {
            throw new InvalidOperationException("MV_CC_CreateHandle failed: " + MvsRuntime.Describe(rc));
        }

        _handle = handle;
        cancellationToken.ThrowIfCancellationRequested();
        rc = MvsNative.MV_CC_OpenDevice(handle, MvsRuntime.AccessExclusive, 0);
        if (rc != MvsRuntime.MvOk)
        {
            throw new InvalidOperationException(
                "MV_CC_OpenDevice failed: " + MvsRuntime.Describe(rc)
                + " (is another application using the camera?)");
        }

        var nodes = new MvsNodeMap(handle);
        uint layer = BitConverter.ToUInt32(_rawDeviceInfo, MvsNativeSizes.DeviceInfoTLayerTypeOffset);
        if ((layer & (MvsRuntime.GigeDevice | MvsRuntime.GentlGigeDevice)) != 0)
        {
            // MvCameraControl.h:1708: only valid for GigE; a failure just keeps the camera default.
            try
            {
                int packet = MvsNative.MV_CC_GetOptimalPacketSize(handle);
                if (packet > 0)
                {
                    nodes.TrySetInt("GevSCPSPacketSize", packet);
                }
            }
            catch (EntryPointNotFoundException)
            {
                // Older runtime: keep the default packet size.
            }
        }

        // Best effort: always deliver the newest frame, free-running continuous acquisition.
        try
        {
            MvsNative.MV_CC_SetGrabStrategy(handle, 1); // CameraParams.h:418 MV_GrabStrategy_LatestImagesOnly
        }
        catch (EntryPointNotFoundException)
        {
            // Older runtime: default strategy.
        }

        nodes.TrySetEnum("AcquisitionMode", 2);      // CameraParams.h:822 MV_ACQ_MODE_CONTINUOUS

        _nodes = nodes;
        _engine = new CameraParameterEngine(nodes, RunStructural);
    }

    protected override void OnClose()
    {
        IntPtr handle = _handle;
        _handle = IntPtr.Zero;
        _nodes = null;
        _engine = null;
        if (handle != IntPtr.Zero)
        {
            try
            {
                MvsNative.MV_CC_CloseDevice(handle);
            }
            catch (Exception)
            {
                // Keep releasing.
            }

            try
            {
                MvsNative.MV_CC_DestroyHandle(handle);
            }
            catch (Exception)
            {
                // Nothing more to release.
            }
        }

        if (_deviceInfoPtr != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_deviceInfoPtr);
            _deviceInfoPtr = IntPtr.Zero;
        }

        _buffer = null;
        _bufferPtr = IntPtr.Zero;
    }

    protected override void OnStartGrabbing()
    {
        MvsNodeMap? nodes = _nodes;
        if (_handle == IntPtr.Zero || nodes == null)
        {
            throw new InvalidOperationException("The camera is not open.");
        }

        // The frame size depends on ROI and pixel format, so it is re-read at every start.
        if (!nodes.TryGetInt("PayloadSize", out long payload, out _, out _, out _) || payload <= 0 || payload > int.MaxValue)
        {
            throw new InvalidOperationException("The camera did not report a valid PayloadSize.");
        }

        if (_buffer == null || _buffer.Length < payload)
        {
            // Pinned-object-heap array: its address stays valid for the native call without a GCHandle.
            _buffer = GC.AllocateArray<byte>((int)payload, pinned: true);
            _bufferPtr = Marshal.UnsafeAddrOfPinnedArrayElement(_buffer, 0);
        }

        int rc = MvsNative.MV_CC_StartGrabbing(_handle);
        if (rc != MvsRuntime.MvOk)
        {
            throw new InvalidOperationException("MV_CC_StartGrabbing failed: " + MvsRuntime.Describe(rc));
        }
    }

    protected override void OnStopGrabbing()
    {
        if (_handle != IntPtr.Zero)
        {
            MvsNative.MV_CC_StopGrabbing(_handle);
        }
    }

    protected override PollStatus Poll(out CameraFrame? frame, out string? error)
    {
        frame = null;
        error = null;
        byte[]? buffer = _buffer;
        if (_handle == IntPtr.Zero || buffer == null)
        {
            error = "The camera is not open.";
            return PollStatus.Error;
        }

        int rc = MvsNative.MV_CC_GetOneFrameTimeout(
            _handle, _bufferPtr, (uint)buffer.Length, ref _frameInfo, PollTimeoutMilliseconds);
        if (rc != MvsRuntime.MvOk)
        {
            if (MvsRuntime.IsNoData(rc))
            {
                return PollStatus.NoData;
            }

            error = "MV_CC_GetOneFrameTimeout failed: " + MvsRuntime.Describe(rc);
            return PollStatus.Error;
        }

        MV_FRAME_OUT_INFO_EX info = _frameInfo;
        int width = info.nExtendWidth > 0 ? (int)info.nExtendWidth : info.nWidth;
        int height = info.nExtendHeight > 0 ? (int)info.nExtendHeight : info.nHeight;
        CameraPixelFormat format = MvsPixelTypes.ToCameraFormat(info.enPixelType);
        if (format == CameraPixelFormat.Unknown)
        {
            error = "Unsupported camera pixel format 0x" + info.enPixelType.ToString("X8")
                + ". Choose Mono8, Bayer8, RGB8 or BGR8 in the pixel format setting.";
            return PollStatus.Error;
        }

        long stride = (long)width * CameraFrameConverter.BytesPerPixel(format);
        long needed = stride * height;
        ulong reported = info.nFrameLenEx > 0 ? info.nFrameLenEx : info.nFrameLen;
        if (width <= 0 || height <= 0 || needed > buffer.Length || reported < (ulong)needed)
        {
            error = "Inconsistent frame: " + width + "x" + height + ", length " + reported + ", buffer " + buffer.Length + ".";
            return PollStatus.Error;
        }

        frame = CameraFrame.CopyFrom(
            new ReadOnlySpan<byte>(buffer, 0, (int)needed),
            width, height, (int)stride, format, info.nFrameNum, DateTime.UtcNow);
        return PollStatus.Frame;
    }

    protected override bool IsDeviceConnected()
    {
        // MvCameraControl.h:339 returns a one-byte bool.
        return _handle != IntPtr.Zero && MvsNative.MV_CC_IsDeviceConnected(_handle);
    }

    protected override void PrepareGrabOne()
    {
        lock (SdkGate)
        {
            MvsNodeMap? nodes = _nodes;
            if (nodes != null)
            {
                SoftwareTrigger.FireIfEnabled(nodes);
            }
        }
    }

    // ---- ICameraParameters ----

    public Task<IReadOnlyList<CameraParameter>> RefreshAsync(CancellationToken cancellationToken)
    {
        return Task.Run(() => WithGate(() => _engine?.GetAll() ?? CameraParameterEngine.AllUnavailable()), cancellationToken);
    }

    public Task<CameraParameter> GetAsync(CameraParameterId id, CancellationToken cancellationToken)
    {
        return Task.Run(() => WithGate(() => _engine?.Get(id) ?? CameraParameterEngine.Unavailable(id)), cancellationToken);
    }

    public Task<CameraParameter> SetAsync(CameraParameterId id, double value, CancellationToken cancellationToken)
    {
        return Task.Run(() => WithGate(() => _engine?.Set(id, value) ?? CameraParameterEngine.Unavailable(id)), cancellationToken);
    }

    public Task<CameraParameter> SetAutoAsync(CameraParameterId id, bool auto, CancellationToken cancellationToken)
    {
        return Task.Run(() => WithGate(() => _engine?.SetAuto(id, auto) ?? CameraParameterEngine.Unavailable(id)), cancellationToken);
    }
}
