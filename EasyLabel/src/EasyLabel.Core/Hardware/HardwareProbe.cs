using System.Runtime.InteropServices;

namespace EasyLabel.Core.Hardware;

/// <summary>
/// Reads cores, RAM and GPUs of the local machine with plain Win32 / DXGI P/Invoke (no WMI, no
/// process spawn). Every part is isolated: a failure leaves that part at its "unknown" value.
/// </summary>
public sealed class HardwareProbe : IHardwareProbe
{
    public HardwareInfo Probe()
    {
        int logical = 0, physical = 0;
        long totalRam = 0, availRam = 0;
        List<GpuInfo> gpus = new List<GpuInfo>();
        string os = string.Empty;

        try { logical = Environment.ProcessorCount; } catch { logical = 0; }
        try { os = System.Runtime.InteropServices.RuntimeInformation.OSDescription; } catch { os = string.Empty; }

        bool windows = false;
        try { windows = OperatingSystem.IsWindows(); } catch { windows = false; }

        if (windows)
        {
            try { physical = ReadPhysicalCores(); } catch { physical = 0; }
            try { ReadRam(out totalRam, out availRam); } catch { totalRam = 0; availRam = 0; }
            try { gpus = ReadGpus(); } catch { gpus = new List<GpuInfo>(); }
        }

        if (physical <= 0 && logical > 0) physical = logical;
        return new HardwareInfo(physical, logical, totalRam, availRam, gpus, os);
    }

    // ---------------------------------------------------------------- cores

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetLogicalProcessorInformationEx(int relationshipType, IntPtr buffer, ref int returnedLength);

    private const int RelationProcessorCore = 0;

    private static int ReadPhysicalCores()
    {
        int len = 0;
        GetLogicalProcessorInformationEx(RelationProcessorCore, IntPtr.Zero, ref len);
        if (len <= 0 || len > 1 << 20) return 0;

        IntPtr buf = Marshal.AllocHGlobal(len);
        try
        {
            if (!GetLogicalProcessorInformationEx(RelationProcessorCore, buf, ref len)) return 0;
            int count = 0;
            int offset = 0;
            // Each record: int Relationship, int Size, then payload. Walk by Size.
            while (offset + 8 <= len)
            {
                int relationship = Marshal.ReadInt32(buf, offset);
                int size = Marshal.ReadInt32(buf, offset + 4);
                if (size <= 0) break;
                if (relationship == RelationProcessorCore) count++;
                offset += size;
            }
            return count;
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    // ------------------------------------------------------------------ RAM

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx status);

    private static void ReadRam(out long total, out long available)
    {
        total = 0;
        available = 0;
        MemoryStatusEx s = new MemoryStatusEx();
        s.dwLength = (uint)Marshal.SizeOf<MemoryStatusEx>();
        if (!GlobalMemoryStatusEx(ref s)) return;
        total = s.ullTotalPhys > long.MaxValue ? long.MaxValue : (long)s.ullTotalPhys;
        available = s.ullAvailPhys > long.MaxValue ? long.MaxValue : (long)s.ullAvailPhys;
    }

    // ----------------------------------------------------------------- GPUs

    [DllImport("dxgi.dll")]
    private static extern int CreateDXGIFactory1(ref Guid riid, out IntPtr factory);

    private static readonly Guid IidFactory1 = new Guid("770aae78-f26f-4dba-a829-253c83d1b387");

    // vtable slots (IUnknown 0-2)
    private const int FactoryEnumAdapters1Slot = 12;   // IDXGIFactory1::EnumAdapters1
    private const int AdapterGetDesc1Slot = 10;        // IDXGIAdapter1::GetDesc1

    // DXGI_ADAPTER_DESC1 on x64: WCHAR[128] (256) + 4 UINT (16) + 2 SIZE_T (16) -> SIZE_T SharedSystemMemory @288, LUID @296, Flags @304.
    private const int DescSize = 312;
    private const int DescVendorOffset = 256;
    private const int DescDedicatedVramOffset = 272;
    private const int DescFlagsOffset = 304;
    private const int DxgiAdapterFlagSoftware = 2;

    private const int MaxAdapters = 16;

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int EnumAdapters1Fn(IntPtr self, uint index, out IntPtr adapter);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetDesc1Fn(IntPtr self, IntPtr desc);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint ReleaseFn(IntPtr self);

    private static IntPtr Slot(IntPtr comObject, int index)
    {
        IntPtr vtbl = Marshal.ReadIntPtr(comObject);
        return Marshal.ReadIntPtr(vtbl, index * IntPtr.Size);
    }

    private static void Release(IntPtr comObject)
    {
        if (comObject == IntPtr.Zero) return;
        try
        {
            ReleaseFn rel = Marshal.GetDelegateForFunctionPointer<ReleaseFn>(Slot(comObject, 2));
            rel(comObject);
        }
        catch
        {
        }
    }

    private static GpuVendor VendorFromId(int id)
    {
        switch (id)
        {
            case 0x10DE: return GpuVendor.Nvidia;
            case 0x1002:
            case 0x1022: return GpuVendor.Amd;
            case 0x8086: return GpuVendor.Intel;
            default: return GpuVendor.Other;
        }
    }

    private static List<GpuInfo> ReadGpus()
    {
        List<GpuInfo> list = new List<GpuInfo>();
        Guid iid = IidFactory1;
        if (CreateDXGIFactory1(ref iid, out IntPtr factory) < 0 || factory == IntPtr.Zero) return list;

        IntPtr desc = Marshal.AllocHGlobal(DescSize);
        try
        {
            EnumAdapters1Fn enumFn = Marshal.GetDelegateForFunctionPointer<EnumAdapters1Fn>(Slot(factory, FactoryEnumAdapters1Slot));
            for (uint i = 0; i < MaxAdapters; i++)
            {
                if (enumFn(factory, i, out IntPtr adapter) < 0 || adapter == IntPtr.Zero) break;
                try
                {
                    for (int b = 0; b < DescSize; b += 8) Marshal.WriteInt64(desc, b, 0);
                    GetDesc1Fn getDesc = Marshal.GetDelegateForFunctionPointer<GetDesc1Fn>(Slot(adapter, AdapterGetDesc1Slot));
                    if (getDesc(adapter, desc) < 0) continue;

                    int flags = Marshal.ReadInt32(desc, DescFlagsOffset);
                    if ((flags & DxgiAdapterFlagSoftware) != 0) continue;

                    string name = (Marshal.PtrToStringUni(desc, 128) ?? string.Empty).TrimEnd('\0').Trim();
                    if (name.IndexOf("Microsoft Basic Render", StringComparison.OrdinalIgnoreCase) >= 0) continue;

                    int vendorId = Marshal.ReadInt32(desc, DescVendorOffset);
                    long vram = Marshal.ReadInt64(desc, DescDedicatedVramOffset);
                    list.Add(new GpuInfo(name, vram, VendorFromId(vendorId)));
                }
                catch
                {
                    // skip this adapter, keep the rest
                }
                finally
                {
                    Release(adapter);
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(desc);
            Release(factory);
        }
        return list;
    }
}
