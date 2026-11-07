using System.Diagnostics;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace EasyLabel.Ai.Onnx;

/// <summary>
/// Client of <c>cuda\Sam3CudaWorker.exe</c>: the SAM3 image encoder on the NVIDIA GPU in its own process (the DirectML onnxruntime.dll of this app
/// cannot also load the CUDA provider). Protocol: see Program.cs of the worker. One request at a time; callers serialise (Sam3Backend does).
/// </summary>
internal sealed class Sam3CudaEncoder : IDisposable
{
    private const int Side = 1008;
    private static readonly int[][] Shapes =
    {
        new[] { 1, 256, 72, 72 },   // vision_pos_enc_2
        new[] { 1, 256, 288, 288 }, // backbone_fpn_0
        new[] { 1, 256, 144, 144 }, // backbone_fpn_1
        new[] { 1, 256, 72, 72 },   // backbone_fpn_2
    };

    private readonly string _encoderPath;
    private Process _process;

    private Sam3CudaEncoder(string encoderPath, Process process)
    {
        _encoderPath = encoderPath;
        _process = process;
    }

    /// <summary>Path of the worker next to the app, or null when this build ships without it.</summary>
    public static string? WorkerPath()
    {
        string p = Path.Combine(AppContext.BaseDirectory, "cuda", "Sam3CudaWorker.exe");
        return File.Exists(p) ? p : null;
    }

    /// <summary>Starts the worker and waits until the model is loaded on the GPU. Returns null and the reason when that is not possible.</summary>
    public static Sam3CudaEncoder? TryStart(string encoderPath, out string? reason)
    {
        reason = null;
        string? worker = WorkerPath();
        if (worker == null) { reason = "CUDA worker not installed"; return null; }
        try
        {
            return new Sam3CudaEncoder(encoderPath, StartProcess(worker, encoderPath));
        }
        catch (AiModelException ex)
        {
            reason = "CUDA unavailable: " + ex.Message;
            return null;
        }
    }

    private static Process StartProcess(string worker, string encoderPath)
    {
        Process? p = null;
        try
        {
            p = Process.Start(new ProcessStartInfo(worker)
            {
                ArgumentList = { encoderPath },
                WorkingDirectory = Path.GetDirectoryName(worker)!,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            }) ?? throw new AiModelException("CUDA worker did not start");
            p.ErrorDataReceived += (_, _) => { }; // drained so a chatty onnxruntime log can never fill the pipe and stall the worker
            p.BeginErrorReadLine();
            Stream o = p.StandardOutput.BaseStream;
            if (ReadInt(o) != 0) throw new AiModelException(ReadMessage(o));
            return p;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception or AiModelException)
        {
            Kill(p);
            p?.Dispose();
            throw ex as AiModelException ?? new AiModelException(ex.Message);
        }
    }

    /// <summary>Runs the encoder on 3*1008*1008 RGB planes and returns vision_pos_enc_2, backbone_fpn_0, _1, _2. Cancelling stops the worker; the next call starts it again (about 4 s).</summary>
    public DenseTensor<float>[] Run(byte[] planes, CancellationToken ct)
    {
        if (planes.Length != 3 * Side * Side) throw new ArgumentException("Expected 3x1008x1008 bytes.", nameof(planes));
        if (_process.HasExited)
        {
            _process.Dispose();
            _process = StartProcess(WorkerPath() ?? throw new AiModelException("CUDA worker not installed"), _encoderPath);
        }

        Process process = _process;
        using CancellationTokenRegistration _ = ct.Register(() => Kill(process));
        try
        {
            Stream i = _process.StandardInput.BaseStream;
            i.Write(BitConverter.GetBytes(planes.Length));
            i.Write(planes);
            i.Flush();
            Stream o = _process.StandardOutput.BaseStream;
            if (ReadInt(o) != 0) throw new AiModelException("SAM3 CUDA encoder failed: " + ReadMessage(o));
            var result = new DenseTensor<float>[Shapes.Length];
            for (int k = 0; k < Shapes.Length; k++)
            {
                int count = ReadInt(o);
                if (count != Shapes[k].Aggregate(1, (a, b) => a * b)) throw new AiModelException("SAM3 CUDA encoder returned an unexpected tensor size.");
                var data = new float[count];
                ReadExactly(o, System.Runtime.InteropServices.MemoryMarshal.AsBytes(data.AsSpan()));
                result[k] = new DenseTensor<float>(data, Shapes[k]);
            }

            return result;
        }
        catch (Exception ex) when (ex is IOException or EndOfStreamException or InvalidOperationException)
        {
            ct.ThrowIfCancellationRequested();
            Kill(_process);
            throw new AiModelException("SAM3 CUDA worker stopped: " + ex.Message);
        }
    }

    public void Dispose()
    {
        try { _process.StandardInput.BaseStream.Write(BitConverter.GetBytes(0)); _process.StandardInput.BaseStream.Flush(); } catch (Exception ex) when (ex is IOException or InvalidOperationException) { }
        if (!_process.WaitForExit(2000)) Kill(_process);
        _process.Dispose();
    }

    private static void Kill(Process? p)
    {
        try { if (p != null && !p.HasExited) p.Kill(true); } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }

    private static void ReadExactly(Stream s, Span<byte> buffer)
    {
        int got = 0;
        while (got < buffer.Length)
        {
            int n = s.Read(buffer.Slice(got));
            if (n == 0) throw new EndOfStreamException("The CUDA worker closed its output.");
            got += n;
        }
    }

    private static int ReadInt(Stream s)
    {
        Span<byte> b = stackalloc byte[4];
        ReadExactly(s, b);
        return BitConverter.ToInt32(b);
    }

    private static string ReadMessage(Stream s)
    {
        int n = ReadInt(s);
        if (n < 0 || n > 65536) return "unreadable error";
        var b = new byte[n];
        ReadExactly(s, b);
        return System.Text.Encoding.UTF8.GetString(b);
    }
}
