using Microsoft.Win32.SafeHandles;

namespace EasyLabel.SegAI.Interop;

/// <summary>Owns a native trainer; ReleaseHandle calls SEGAI_TrainerDestroy.</summary>
internal sealed class SegAiTrainerHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public SegAiTrainerHandle() : base(true)
    {
    }

    internal static SegAiTrainerHandle Create()
    {
        IntPtr raw = SegAiTrainerNative.SEGAI_TrainerCreate();
        if (raw == IntPtr.Zero)
        {
            throw new InvalidOperationException("SEGAI_TrainerCreate failed.");
        }

        var handle = new SegAiTrainerHandle();
        handle.SetHandle(raw);
        return handle;
    }

    protected override bool ReleaseHandle()
    {
        SegAiTrainerNative.SEGAI_TrainerDestroy(handle);
        return true;
    }
}
