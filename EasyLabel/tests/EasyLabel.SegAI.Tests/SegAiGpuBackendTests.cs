using System.Text.Json;
using EasyAI.Desktop.Infrastructure;
using EasyLabel.SegAI.Options;
using EasyLabel.SegAI.Training;

namespace EasyLabel.SegAI.Tests;

/// <summary>Segment AI device choice (CPU / Intel GPU / NVIDIA CUDA): rules, persistence, old-file compatibility, fallback labels. No GPU needed.</summary>
public sealed class SegAiGpuBackendTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "segai-gpu-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }
        catch (IOException) { }
    }

    [Fact]
    public void Inference_DefaultsToIntelOpenVino_AndEnumNumbersAreStable()
    {
        SegInferenceSettings s = new SegInferenceSettings();
        Assert.False(s.EnableGpu);
        Assert.Equal(SegGpuBackend.IntelOpenVino, s.GpuBackend);
        Assert.Equal(0, (int)SegGpuBackend.IntelOpenVino);
        Assert.Equal(1, (int)SegGpuBackend.NvidiaCuda);
    }

    [Theory]
    [InlineData("cpu", false, SegGpuBackend.IntelOpenVino)]
    [InlineData("intel", true, SegGpuBackend.IntelOpenVino)]
    [InlineData("nvidia", true, SegGpuBackend.NvidiaCuda)]
    [InlineData("garbage", false, SegGpuBackend.IntelOpenVino)]
    [InlineData(null, false, SegGpuBackend.IntelOpenVino)]
    public void Apply_MapsTheChoiceToTheInferenceSettings(string? device, bool gpu, SegGpuBackend backend)
    {
        SegInferenceSettings s = new SegInferenceSettings { EnableGpu = true, GpuBackend = SegGpuBackend.NvidiaCuda };
        SegAiDeviceRules.Apply(s, device);
        Assert.Equal(gpu, s.EnableGpu);
        Assert.Equal(backend, s.GpuBackend);
    }

    [Fact]
    public void Normalize_UnknownFallsBackToCpu_KnownIsCaseInsensitive()
    {
        Assert.Equal(SegAiDeviceRules.Cpu, SegAiDeviceRules.Normalize(null));
        Assert.Equal(SegAiDeviceRules.Cpu, SegAiDeviceRules.Normalize("tpu"));
        Assert.Equal(SegAiDeviceRules.Nvidia, SegAiDeviceRules.Normalize(" NVIDIA "));
        Assert.True(SegAiDeviceRules.IsDevice(SegAiDeviceRules.Intel));
        Assert.False(SegAiDeviceRules.IsDevice("tpu"));
        Assert.False(SegAiDeviceRules.IsDevice(null));
    }

    [Fact]
    public void AppSettings_DefaultIsCpu_AndRowAcceptsOnlyKnownDevices()
    {
        AppSettings s = new AppSettings();
        Assert.Equal(SegAiDeviceRules.Cpu, s.SegAiDevice);
        Assert.True(SettingsAccessor.TryGetString(s, SettingsAccessor.RowSegAiDevice, out string value));
        Assert.Equal(SegAiDeviceRules.Cpu, value);
        Assert.True(SettingsAccessor.TrySetString(s, SettingsAccessor.RowSegAiDevice, SegAiDeviceRules.Nvidia));
        Assert.Equal(SegAiDeviceRules.Nvidia, s.SegAiDevice);
        Assert.False(SettingsAccessor.TrySetString(s, SettingsAccessor.RowSegAiDevice, "tpu"));
        Assert.Equal(SegAiDeviceRules.Nvidia, s.SegAiDevice);
    }

    // Same options as SettingsStore (settings.json is read with System.Text.Json; a missing key keeps the property default).
    private static readonly JsonSerializerOptions StoreOptions = new JsonSerializerOptions
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private string WriteAndRead(string json)
    {
        Directory.CreateDirectory(_dir);
        string path = Path.Combine(_dir, "settings.json");
        File.WriteAllText(path, json);
        AppSettings? s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), StoreOptions);
        Assert.NotNull(s);
        return s!.SegAiDevice;
    }

    [Fact]
    public void SettingsFile_RoundTripsTheChoice()
    {
        string json = JsonSerializer.Serialize(new AppSettings { SegAiDevice = SegAiDeviceRules.Nvidia }, StoreOptions);
        Assert.Equal(SegAiDeviceRules.Nvidia, WriteAndRead(json));
    }

    [Fact]
    public void OldSettingsFileWithoutTheKey_ReadsAsCpu()
    {
        Assert.Equal(SegAiDeviceRules.Cpu, WriteAndRead("{ \"UltraAllowCpu\": true }"));
    }

    [Fact]
    public void SettingsFileWithAnUnknownDevice_ReadsAsCpu()
    {
        Assert.Equal(SegAiDeviceRules.Cpu, WriteAndRead("{ \"SegAiDevice\": \"quantum\" }"));
    }

    [Fact]
    public void TrainRequestSnapshot_CopiesTheBackend()
    {
        SegInferenceSettings source = new SegInferenceSettings { EnableGpu = true, GpuBackend = SegGpuBackend.NvidiaCuda };
        SegInferenceSettings copy = SegAiTrainRequest.Snapshot(source);
        Assert.True(copy.EnableGpu);
        Assert.Equal(SegGpuBackend.NvidiaCuda, copy.GpuBackend);
        Assert.NotSame(source, copy);
    }

    [Fact]
    public void DeviceLabel_CudaUsedCudaFellBackAndLegacyLabels()
    {
        Assert.Equal("NVIDIA GPU / CUDA (ONNX Runtime)", SegAiTrainingPipeline.DeviceLabel(true, SegGpuBackend.NvidiaCuda, true, 2));
        string fell = SegAiTrainingPipeline.DeviceLabel(true, SegGpuBackend.NvidiaCuda, false, 2);
        Assert.StartsWith("CPU", fell);
        Assert.Contains("CUDA", fell);
        string withNote = SegAiTrainingPipeline.DeviceLabel(true, SegGpuBackend.NvidiaCuda, false, 2, " thiếu cublasLt64_13.dll ");
        Assert.Contains("thiếu cublasLt64_13.dll", withNote);
        Assert.StartsWith("CPU", withNote);

        Assert.Equal("Intel GPU / OpenVINO", SegAiTrainingPipeline.DeviceLabel(true, SegGpuBackend.IntelOpenVino, true, 2));
        Assert.Equal("GPU / OpenCL matching", SegAiTrainingPipeline.DeviceLabel(true, SegGpuBackend.IntelOpenVino, true, 1));
        Assert.Equal("CPU", SegAiTrainingPipeline.DeviceLabel(false, SegGpuBackend.IntelOpenVino, false, 2));
        Assert.Equal("CPU", SegAiTrainingPipeline.DeviceLabel(false, SegGpuBackend.NvidiaCuda, false, 2));
    }
}
