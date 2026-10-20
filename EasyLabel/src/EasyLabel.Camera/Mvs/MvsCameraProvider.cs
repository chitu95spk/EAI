using System.Runtime.InteropServices;

namespace EasyLabel.Camera.Mvs;

/// <summary>
/// Enumerates Hikrobot MVS GigE and USB3 Vision cameras. Never throws for a missing runtime or SDK failure: the
/// result is an empty list and <see cref="LastMessage"/> / <see cref="LastLoadResult"/> explain why.
/// </summary>
public sealed class MvsCameraProvider : ICameraProvider
{
    private readonly Func<MvsSdkLoadResult> _loader;
    private readonly object _devicesLock = new();
    private readonly Dictionary<string, byte[]> _devices = new(StringComparer.Ordinal);

    public MvsCameraProvider()
        : this(MvsSdk.TryLoad)
    {
    }

    // Test seam: lets tests simulate an absent runtime without touching the process-wide loader.
    internal MvsCameraProvider(Func<MvsSdkLoadResult> loader)
    {
        _loader = loader ?? throw new ArgumentNullException(nameof(loader));
    }

    /// <summary>Result of the runtime load attempt made by the latest enumeration (null before the first).</summary>
    public MvsSdkLoadResult? LastLoadResult { get; private set; }

    /// <summary>Human readable outcome of the latest enumeration; empty when it succeeded.</summary>
    public string LastMessage { get; private set; } = string.Empty;

    public Task<IReadOnlyList<CameraInfo>> EnumerateAsync(CancellationToken cancellationToken)
    {
        return Task.Run(() => Enumerate(cancellationToken), cancellationToken);
    }

    public ICameraSource Create(CameraInfo info)
    {
        if (info == null)
        {
            throw new ArgumentNullException(nameof(info));
        }

        byte[]? raw;
        lock (_devicesLock)
        {
            raw = _devices.TryGetValue(info.Id, out byte[]? found) ? found : null;
        }

        return new MvsCameraSource(info, raw, _loader);
    }

    private IReadOnlyList<CameraInfo> Enumerate(CancellationToken cancellationToken)
    {
        var result = new List<CameraInfo>();
        var devices = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        MvsSdkLoadResult load;
        try
        {
            load = _loader();
        }
        catch (Exception ex)
        {
            LastLoadResult = null;
            LastMessage = "Loading the MVS runtime threw " + ex.GetType().Name + ": " + ex.Message;
            SetDevices(devices);
            return result;
        }

        LastLoadResult = load;
        if (!load.IsLoaded)
        {
            LastMessage = load.Message;
            SetDevices(devices);
            return result;
        }

        IntPtr list = IntPtr.Zero;
        try
        {
            MvsRuntime.EnsureInitialized();
            list = Marshal.AllocHGlobal(MvsNativeSizes.DeviceInfoList);
            Marshal.Copy(new byte[MvsNativeSizes.DeviceInfoList], 0, list, MvsNativeSizes.DeviceInfoList);

            int rc;
            lock (MvsRuntime.EnumLock)
            {
                rc = MvsNative.MV_CC_EnumDevices(MvsRuntime.GigeDevice | MvsRuntime.UsbDevice, list);
                if (rc == MvsRuntime.MvOk)
                {
                    // The device records are SDK-owned and may be freed by the next enumeration: copy them now.
                    ReadDevices(list, result, devices, cancellationToken);
                }
            }

            LastMessage = rc == MvsRuntime.MvOk ? string.Empty : "MV_CC_EnumDevices failed: " + MvsRuntime.Describe(rc);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            result.Clear();
            devices.Clear();
            LastMessage = "Enumerating cameras threw " + ex.GetType().Name + ": " + ex.Message;
        }
        finally
        {
            if (list != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(list);
            }
        }

        SetDevices(devices);
        return result;
    }

    private void SetDevices(Dictionary<string, byte[]> devices)
    {
        lock (_devicesLock)
        {
            _devices.Clear();
            foreach (KeyValuePair<string, byte[]> pair in devices)
            {
                _devices[pair.Key] = pair.Value;
            }
        }
    }

    private static void ReadDevices(
        IntPtr list, List<CameraInfo> infos, Dictionary<string, byte[]> raws, CancellationToken cancellationToken)
    {
        // MV_CC_DEVICE_INFO_LIST (CameraParams.h:194): uint nDeviceNum, then pDeviceInfo[256] at offset 8.
        int count = Math.Min(Marshal.ReadInt32(list), MvsNativeSizes.MaxDeviceNum);
        for (int i = 0; i < count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IntPtr device = Marshal.ReadIntPtr(list, MvsNativeSizes.DeviceInfoListPointersOffset + (i * IntPtr.Size));
            if (device == IntPtr.Zero)
            {
                continue;
            }

            var raw = new byte[MvsNativeSizes.DeviceInfo];
            Marshal.Copy(device, raw, 0, raw.Length);
            uint layer = BitConverter.ToUInt32(raw, MvsNativeSizes.DeviceInfoTLayerTypeOffset);
            IntPtr special = device + MvsNativeSizes.DeviceInfoSpecialOffset;

            CameraInfo? info = null;
            if ((layer & MvsRuntime.GigeDevice) != 0)
            {
                MV_GIGE_DEVICE_INFO gige = Marshal.PtrToStructure<MV_GIGE_DEVICE_INFO>(special);
                string serial = MvsText.FromBytes(gige.chSerialNumber);
                string key = serial.Length > 0 ? serial : FormatIp(gige.nCurrentIp);
                info = Describe("GEV:" + key, MvsText.FromBytes(gige.chUserDefinedName),
                    MvsText.FromBytes(gige.chModelName), serial, MvsText.FromBytes(gige.chManufacturerName));
            }
            else if ((layer & MvsRuntime.UsbDevice) != 0)
            {
                MV_USB3_DEVICE_INFO usb = Marshal.PtrToStructure<MV_USB3_DEVICE_INFO>(special);
                string serial = MvsText.FromBytes(usb.chSerialNumber);
                string key = serial.Length > 0 ? serial : usb.nDeviceAddress.ToString(System.Globalization.CultureInfo.InvariantCulture);
                info = Describe("U3V:" + key, MvsText.FromBytes(usb.chUserDefinedName),
                    MvsText.FromBytes(usb.chModelName), serial, MvsText.FromBytes(usb.chVendorName));
            }

            if (info == null)
            {
                continue;
            }

            string id = info.Id;
            int suffix = 1;
            while (raws.ContainsKey(id))
            {
                id = info.Id + "#" + (++suffix).ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            if (!string.Equals(id, info.Id, StringComparison.Ordinal))
            {
                info = info with { Id = id };
            }

            infos.Add(info);
            raws[id] = raw;
        }
    }

    private static CameraInfo Describe(string id, string userName, string model, string serial, string vendor)
    {
        string display = userName.Length > 0
            ? userName + " (" + model + ")"
            : model + (serial.Length > 0 ? " (" + serial + ")" : string.Empty);
        return new CameraInfo(id, display.Trim(), model, serial, vendor);
    }

    private static string FormatIp(uint ip)
        => ((ip >> 24) & 0xFF) + "." + ((ip >> 16) & 0xFF) + "." + ((ip >> 8) & 0xFF) + "." + (ip & 0xFF);
}
