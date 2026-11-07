using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

// Protocol on stdin/stdout (little-endian int32 framing):
//   startup  -> int32 0 (ready) | int32 -1, int32 n, n bytes UTF-8 message (failed; the process then exits)
//   request  <- int32 n (n = 3*1008*1008 RGB planes, 0 = quit), n bytes
//   reply    -> int32 0, then for each of the 4 outputs: int32 count, count float32 | int32 -1, int32 n, n bytes message
// The CUDA/cuDNN DLLs sit next to this exe; the provider DLL (under runtimes\) finds its dependencies through PATH.
Environment.SetEnvironmentVariable("PATH", AppContext.BaseDirectory + ";" + Environment.GetEnvironmentVariable("PATH"));
const int Side = 1008;
string[] outputs = { "vision_pos_enc_2", "backbone_fpn_0", "backbone_fpn_1", "backbone_fpn_2" };
Stream stdin = Console.OpenStandardInput();
Stream stdout = Console.OpenStandardOutput();

void WriteError(string message)
{
    byte[] m = System.Text.Encoding.UTF8.GetBytes(message);
    stdout.Write(BitConverter.GetBytes(-1));
    stdout.Write(BitConverter.GetBytes(m.Length));
    stdout.Write(m);
    stdout.Flush();
}

InferenceSession session;
try
{
    if (args.Length != 1 || !File.Exists(args[0])) throw new FileNotFoundException("SAM3 encoder not found: " + string.Join(' ', args));
    using var options = new SessionOptions { EnableMemoryPattern = false, ExecutionMode = ExecutionMode.ORT_SEQUENTIAL };
    // The CUDA call throws when the CUDA libraries cannot be loaded, so a started worker really runs on the GPU.
    options.AppendExecutionProvider_CUDA(new OrtCUDAProviderOptions());
    session = new InferenceSession(args[0], options);
}
catch (Exception ex)
{
    WriteError(ex.Message);
    return 1;
}

stdout.Write(BitConverter.GetBytes(0));
stdout.Flush();

// Without arena shrinkage the VRAM grows to the whole card after the first image and every run then takes ~9 s instead of ~0.7 s.
using var runOptions = new RunOptions();
runOptions.AddRunConfigEntry("memory.enable_memory_arena_shrinkage", "gpu:0");
var header = new byte[4];
var plane = new byte[3 * Side * Side];
while (true)
{
    if (stdin.ReadAtLeast(header, 4, false) < 4) return 0;
    int n = BitConverter.ToInt32(header);
    if (n == 0) return 0;
    if (n != plane.Length) { WriteError("Bad request length " + n); return 2; }
    if (stdin.ReadAtLeast(plane, n, false) < n) return 0;
    try
    {
        var input = new DenseTensor<byte>(plane.AsMemory(), new[] { 3, Side, Side });
        using var r = session.Run(new[] { NamedOnnxValue.CreateFromTensor("image", input) }, outputs, runOptions);
        stdout.Write(BitConverter.GetBytes(0));
        foreach (var value in r)
        {
            float[] data = value.AsTensor<float>().ToArray();
            stdout.Write(BitConverter.GetBytes(data.Length));
            stdout.Write(System.Runtime.InteropServices.MemoryMarshal.AsBytes(data.AsSpan()));
        }
        stdout.Flush();
    }
    catch (Exception ex)
    {
        WriteError(ex.Message);
    }
}

