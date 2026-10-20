using Microsoft.Win32.SafeHandles;

namespace EasyLabel.SegAI.Interop;

/// <summary>Owns a native inferer; ReleaseHandle calls SEGAI_InferDestroy.</summary>
internal sealed class SegAiInfererHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public SegAiInfererHandle() : base(true)
    {
    }

    internal static SegAiInfererHandle Create()
    {
        IntPtr raw = SegAiInfererNative.SEGAI_InferCreate();
        if (raw == IntPtr.Zero)
        {
            throw new InvalidOperationException("SEGAI_InferCreate failed.");
        }

        var handle = new SegAiInfererHandle();
        handle.SetHandle(raw);
        return handle;
    }

    protected override bool ReleaseHandle()
    {
        SegAiInfererNative.SEGAI_InferDestroy(handle);
        return true;
    }
}
