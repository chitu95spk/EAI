namespace EasyLabel.Core.Hardware;

public enum GpuVendor
{
    Other = 0,
    Nvidia = 1,
    Amd = 2,
    Intel = 3,
}

/// <summary>One hardware (non-software) display adapter.</summary>
public sealed class GpuInfo
{
    public GpuInfo(string name, long dedicatedVramBytes, GpuVendor vendor)
    {
        Name = name ?? string.Empty;
        DedicatedVramBytes = dedicatedVramBytes < 0 ? 0 : dedicatedVramBytes;
        Vendor = vendor;
    }

    public string Name { get; }
    public long DedicatedVramBytes { get; }
    public GpuVendor Vendor { get; }
}

/// <summary>
/// Immutable snapshot of this machine. A zero / empty value means "could not be read".
/// </summary>
public sealed class HardwareInfo
{
    public HardwareInfo(
        int physicalCores,
        int logicalCores,
        long totalRamBytes,
        long availableRamBytes,
        IReadOnlyList<GpuInfo>? gpus,
        string? osDescription)
    {
        PhysicalCores = physicalCores < 0 ? 0 : physicalCores;
        LogicalCores = logicalCores < 0 ? 0 : logicalCores;
        TotalRamBytes = totalRamBytes < 0 ? 0 : totalRamBytes;
        AvailableRamBytes = availableRamBytes < 0 ? 0 : availableRamBytes;
        Gpus = gpus == null ? Array.Empty<GpuInfo>() : gpus.ToArray();
        OsDescription = osDescription ?? string.Empty;
    }

    public int PhysicalCores { get; }
    public int LogicalCores { get; }
    public long TotalRamBytes { get; }
    public long AvailableRamBytes { get; }
    public IReadOnlyList<GpuInfo> Gpus { get; }
    public string OsDescription { get; }

    /// <summary>True when an NVIDIA adapter is present (CUDA still needs the driver; --preflight confirms).</summary>
    public bool HasCudaGpu
    {
        get
        {
            foreach (GpuInfo g in Gpus)
                if (g.Vendor == GpuVendor.Nvidia) return true;
            return false;
        }
    }

    /// <summary>Largest dedicated VRAM among NVIDIA adapters, 0 when none.</summary>
    public long CudaVramBytes
    {
        get
        {
            long best = 0;
            foreach (GpuInfo g in Gpus)
                if (g.Vendor == GpuVendor.Nvidia && g.DedicatedVramBytes > best) best = g.DedicatedVramBytes;
            return best;
        }
    }
}

/// <summary>Seam so callers and tests can substitute fake hardware.</summary>
public interface IHardwareProbe
{
    HardwareInfo Probe();
}
