using System.Runtime.InteropServices;
using EasyLabel.SegAI.Data;

namespace EasyLabel.SegAI.Interop;

/// <summary>Mirrors SegInstanceV1 in BeeNativeSegAI/SegAINativeExport.h (32 bytes, no padding). Keep the field order.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct NativeInstanceV1
{
    public int StructSize;

    public int Kind;

    public float Cx;

    public float Cy;

    public float W;

    public float H;

    public float Theta;

    public int ClassIndex;

    public static NativeInstanceV1 From(SegInstance instance)
    {
        return new NativeInstanceV1
        {
            StructSize = Marshal.SizeOf(typeof(NativeInstanceV1)),
            Kind = instance.Kind,
            Cx = instance.Cx,
            Cy = instance.Cy,
            W = instance.W,
            H = instance.H,
            Theta = instance.Theta,
            ClassIndex = instance.ClassIndex,
        };
    }
}

/// <summary>Mirrors SegInstanceResultV1 in BeeNativeSegAI/SegAINativeExport.h (40 bytes, no padding). Keep the field order.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct NativeInstanceResultV1
{
    public int StructSize;

    public int Kind;

    public float Cx;

    public float Cy;

    public float W;

    public float H;

    public float Theta;

    public float Score;

    public int ClassIndex;

    public int Refined;
}
