using EasyLabel.SegAI.Training.Ultralytics;

namespace EasyLabel.SegAI.Tests;

public sealed class UltraTrainArgsTests
{
    private static UltraTrainRequest Request(bool allowCpu)
    {
        return new UltraTrainRequest
        {
            DataYaml = @"C:\data\data.yaml",
            FamilyId = "yolov8n",
            Epochs = 5,
            ImgSize = 320,
            RunsDir = @"C:\runs",
            RunName = "r1",
            AllowCpu = allowCpu,
        };
    }

    [Fact]
    public void GpuRun_AddsNoDeviceOverride()
    {
        IReadOnlyList<string> args = UltraTrainService.BuildTrainArgumentList(Request(allowCpu: false));

        Assert.DoesNotContain("--allow-cpu", args);
        Assert.DoesNotContain(args, a => a.StartsWith("--device", StringComparison.Ordinal));
    }

    [Fact]
    public void CpuRun_AddsDeviceCpuAndAllowCpu()
    {
        IReadOnlyList<string> args = UltraTrainService.BuildTrainArgumentList(Request(allowCpu: true));

        Assert.Contains("--device=cpu", args);
        Assert.Contains("--allow-cpu", args);
    }

    [Fact]
    public void StringOptions_UseSingleTokenForm()
    {
        IReadOnlyList<string> args = UltraTrainService.BuildTrainArgumentList(Request(allowCpu: true));

        Assert.Contains(@"--data=C:\data\data.yaml", args);
        Assert.Contains("--family=yolov8n", args);
        Assert.Contains("--imgsz", args);
        Assert.Contains("320", args);
    }
}
