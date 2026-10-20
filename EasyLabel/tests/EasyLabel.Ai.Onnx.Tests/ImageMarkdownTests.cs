using EasyLabel.Ai.Onnx;
using OpenCvSharp;
using Xunit;

namespace EasyLabel.Ai.Onnx.Tests;

public class ImageMarkdownTests
{
    [Fact]
    public void Build_ListsEachDarkBlobWithItsCentreInOriginalPixels()
    {
        using var image = new Mat(new Size(1280, 960), MatType.CV_8UC3, new Scalar(230, 230, 230));
        Cv2.Rectangle(image, new Rect(200, 200, 200, 60), new Scalar(60, 60, 60), -1);
        Cv2.Rectangle(image, new Rect(800, 600, 60, 200), new Scalar(60, 60, 60), -1);

        string md = ImageMarkdown.Build(image);

        Assert.Contains("2 found", md);
        Assert.Matches(@"\| 1 \| (29\d|30\d) \| (22\d|23\d) \|", md);
        Assert.Matches(@"\| 2 \| (82\d|83\d) \| (69\d|70\d) \|", md);
        Assert.Contains("Occupancy map", md);
    }

    [Fact]
    public void Build_OnABlankImageFindsNothing()
    {
        using var image = new Mat(new Size(640, 480), MatType.CV_8UC3, new Scalar(200, 200, 200));
        Assert.Contains("0 found", ImageMarkdown.Build(image));
    }

    [Fact]
    public void Build_OnTheLiveCameraSampleWritesTheTable()
    {
        string path = @"C:\Users\chitu\OneDrive\Documents\EAI\LIVE_CAM.images\Cam_20261019_171247_850.bmp";
        if (!File.Exists(path)) return;
        using Mat shot = Cv2.ImRead(path);
        File.WriteAllText(@"E:\Code\EasyVision_Unisen - NoService\artifacts\LIVE-CAM-test\image_to_md.md", ImageMarkdown.Build(shot));
    }
}
