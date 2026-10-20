// Cloned from BeeCore/Func/NativeSegAI.cs@b0504bc31
using System.Runtime.InteropServices;
using System.Text;
using EasyLabel.SegAI.Data;
using EasyLabel.SegAI.Interop;
using EasyLabel.SegAI.Model;
using EasyLabel.SegAI.Native;
using OpenCvSharp;

namespace EasyLabel.SegAI.Engine;

/// <summary>Caller-owned native int flag passed to training as the cancel pointer.</summary>
public sealed class SegAiCancelFlag : IDisposable
{
    private IntPtr _pointer;

    public SegAiCancelFlag()
    {
        _pointer = Marshal.AllocHGlobal(4);
        Marshal.WriteInt32(_pointer, 0);
    }

    public IntPtr Pointer
    {
        get
        {
            ObjectDisposedException.ThrowIf(_pointer == IntPtr.Zero, this);
            return _pointer;
        }
    }

    public bool IsCancelled => _pointer != IntPtr.Zero && Marshal.ReadInt32(_pointer) != 0;

    public void Cancel()
    {
        ObjectDisposedException.ThrowIf(_pointer == IntPtr.Zero, this);
        Marshal.WriteInt32(_pointer, 1);
    }

    public void Reset()
    {
        ObjectDisposedException.ThrowIf(_pointer == IntPtr.Zero, this);
        Marshal.WriteInt32(_pointer, 0);
    }

    public void Dispose()
    {
        IntPtr pointer = Interlocked.Exchange(ref _pointer, IntPtr.Zero);
        if (pointer != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(pointer);
        }
    }
}

public sealed class SegAiTrainer : IDisposable
{
    private readonly SegAiTrainerHandle _handle;

    public SegAiTrainer()
    {
        NativeSegAiLibrary.EnsureLoaded();
        _handle = SegAiTrainerHandle.Create();
    }

    public bool IsOpened => !_handle.IsClosed && !_handle.IsInvalid;

    public string LastError
    {
        get
        {
            if (!IsOpened)
            {
                return "Trainer is disposed.";
            }

            try
            {
                var buffer = new byte[4096];
                int n = SegAiTrainerNative.SEGAI_TrainerGetLastError(_handle.DangerousGetHandle(), buffer, buffer.Length);
                return n > 0 ? Encoding.UTF8.GetString(buffer, 0, Math.Min(n, buffer.Length)) : string.Empty;
            }
            catch (EntryPointNotFoundException)
            {
                return string.Empty;
            }
        }
    }

    public void ConfigureV2(string path)
    {
        IntPtr h = Handle();
        try
        {
            if (SegAiTrainerNative.SEGAI_TrainerConfigureV2(h, path) != 0)
            {
                throw new InvalidOperationException("Cannot configure V2 training. " + LastError);
            }
        }
        catch (EntryPointNotFoundException)
        {
            throw new InvalidOperationException("Update BeeNativeSegAI.dll to train V2.");
        }
    }

    public void UseStructuralModel(int detail)
    {
        IntPtr h = Handle();
        try
        {
            if (SegAiTrainerNative.SEGAI_TrainerSetProfile(h, 1, detail) != 0)
            {
                throw new InvalidOperationException("Cannot initialize structural model training.");
            }
        }
        catch (EntryPointNotFoundException)
        {
            throw new InvalidOperationException("Update BeeNativeSegAI.dll to train structural models.");
        }
    }

    public int SetRoi(int x, int y, int width, int height)
        => SegAiTrainerNative.SEGAI_TrainerSetROI(Handle(), x, y, width, height);

    public int AddSample(IntPtr bgr, int width, int height, int step, IntPtr mask, int maskStep)
        => SegAiTrainerNative.SEGAI_TrainerAddSample(Handle(), bgr, width, height, step, mask, maskStep);

    public int AddSample(Mat bgr, Mat mask)
    {
        if (!IsValidBgr(bgr) || !IsValidMask(mask) || bgr.Size() != mask.Size())
        {
            return -2;
        }

        int result = AddSample(bgr.Data, bgr.Width, bgr.Height, Step32(bgr), mask.Data, Step32(mask));
        if (result != 0 && !string.IsNullOrEmpty(LastError))
        {
            throw new InvalidOperationException(LastError);
        }

        if (result == -8)
        {
            throw new InvalidOperationException("A labeled region has too few stable details. Check the image and Check coverage, or use pixel training.");
        }

        if (result == -9)
        {
            throw new InvalidOperationException("Structural training supports at most 256 labeled regions. Reduce duplicate samples.");
        }

        return result;
    }

    public void ClearSamples() => SegAiTrainerNative.SEGAI_TrainerClearSamples(Handle());

    /// <summary>
    /// Attaches instance labels to the LAST successful AddSample (call it right after that sample; an empty list is a no-op).
    /// Returns the native code (0 ok). Throws when the DLL predates the instance head.
    /// </summary>
    public int AddInstances(IReadOnlyList<SegInstance> instances)
    {
        ArgumentNullException.ThrowIfNull(instances);
        if (instances.Count == 0) return 0;
        var items = new NativeInstanceV1[instances.Count];
        for (int i = 0; i < items.Length; i++) items[i] = NativeInstanceV1.From(instances[i]);
        IntPtr h = Handle();
        int rc;
        try
        {
            rc = SegAiTrainerNative.SEGAI_TrainerAddInstances(h, items, items.Length, Marshal.SizeOf(typeof(NativeInstanceV1)));
        }
        catch (EntryPointNotFoundException)
        {
            throw new InvalidOperationException("Update BeeNativeSegAI.dll to train the instance head.");
        }

        if (rc != 0) throw new InvalidOperationException("Cannot learn instances (" + rc + "). " + LastError);
        return rc;
    }

    /// <param name="cancelFlag">Native int* owned by the caller (see <see cref="SegAiCancelFlag"/>); IntPtr.Zero for none.</param>
    public int Train(int numTrees, int maxDepth, int minSampleCount, Action<int>? onProgress = null, IntPtr cancelFlag = default)
    {
        Handle();
        bool addRef = false;
        Exception? callbackError = null;
        SegAiProgressCallback? callback = null;
        if (onProgress is not null)
        {
            callback = (p, _) =>
            {
                if (callbackError is not null)
                {
                    return;
                }

                try
                {
                    onProgress(p);
                }
                catch (Exception error)
                {
                    callbackError = error;
                    if (cancelFlag != IntPtr.Zero)
                    {
                        Marshal.WriteInt32(cancelFlag, 1);
                    }
                }
            };
        }

        try
        {
            _handle.DangerousAddRef(ref addRef);
            int rc = SegAiTrainerNative.SEGAI_TrainerTrain(
                _handle.DangerousGetHandle(), numTrees, maxDepth, minSampleCount, callback, IntPtr.Zero, cancelFlag);
            if (callbackError is not null)
            {
                throw new InvalidOperationException("Training progress callback failed.", callbackError);
            }

            return rc;
        }
        finally
        {
            GC.KeepAlive(callback);
            if (addRef)
            {
                _handle.DangerousRelease();
            }
        }
    }

    public int Save(string path, float defectThreshold, uint minDefectArea)
    {
        IntPtr h = Handle();
        if (string.IsNullOrEmpty(path))
        {
            return -2;
        }

        return SegAiTrainerNative.SEGAI_TrainerSave(h, path, defectThreshold, minDefectArea);
    }

    /// <summary>
    /// Names the classes of the model this trainer will save (V2 trainer; call after <see cref="ConfigureV2"/> and before the
    /// first AddSample). Returns the native code (0 ok). A trainer that never gets a table keeps writing the version 3 format.
    /// </summary>
    public int SetClassTable(SegAiClassTable table)
    {
        ArgumentNullException.ThrowIfNull(table);
        byte[] bytes = table.Serialize();
        return SegAiTrainerNative.SEGAI_TrainerSetClassTable(Handle(), bytes, bytes.Length);
    }

    public int GetSampleCount(out int defectPixels, out int normalPixels)
        => SegAiTrainerNative.SEGAI_TrainerSampleCount(Handle(), out defectPixels, out normalPixels);

    public void Dispose() => _handle.Dispose();

    private IntPtr Handle()
    {
        ObjectDisposedException.ThrowIf(_handle.IsClosed, this);
        return _handle.DangerousGetHandle();
    }

    private static bool IsValidBgr(Mat? mat) => mat is not null && !mat.Empty() && mat.Type() == MatType.CV_8UC3;

    private static bool IsValidMask(Mat? mat) => mat is not null && !mat.Empty() && mat.Type() == MatType.CV_8UC1;

    private static int Step32(Mat mat) => checked((int)mat.Step());
}
