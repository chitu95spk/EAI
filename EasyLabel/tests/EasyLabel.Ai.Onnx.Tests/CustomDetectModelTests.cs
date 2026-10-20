using System.Runtime.InteropServices;
using OpenCvSharp;

namespace EasyLabel.Ai.Onnx.Tests;

public class CustomDetectModelTests
{
    [Fact]
    public void CustomDetectFolderNeedsExactlyOneOnnx()
    {
        string folder = Path.Combine(AppContext.BaseDirectory, "custom-detect-" + Guid.NewGuid().ToString("N"));
        Assert.False(AiModelFiles.Resolve(AiModelFiles.CustomDetectId, folder).Found);

        Directory.CreateDirectory(folder);
        Assert.False(AiModelFiles.Resolve(AiModelFiles.CustomDetectId, folder).Found);
        File.WriteAllText(Path.Combine(folder, "model.onnx"), "x");
        AiModelResolution one = AiModelFiles.Resolve(AiModelFiles.CustomDetectId, folder);
        Assert.True(one.Found);
        Assert.Equal(Path.Combine(folder, "model.onnx"), one.ModelPath);
        File.WriteAllText(Path.Combine(folder, "second.onnx"), "x");
        Assert.False(AiModelFiles.Resolve(AiModelFiles.CustomDetectId, folder).Found);
    }

    /// <summary>
    /// Opt-in: runs only when EASYAI_CUSTOM_ONNX (a trained detect model with its .classes.txt next to it) and EASYAI_CUSTOM_IMAGE
    /// are set, otherwise it returns without doing anything. Loads the model the way the app does (custom-detect options) and expects
    /// at least one detection whose label is a line of the classes file, also when the prompt names a class the model does not have.
    /// </summary>
    [Fact]
    public async Task RealCustomOnnxDetectsAClassOfItsClassesFile()
    {
        string? onnx = Environment.GetEnvironmentVariable("EASYAI_CUSTOM_ONNX");
        string? imagePath = Environment.GetEnvironmentVariable("EASYAI_CUSTOM_IMAGE");
        if (string.IsNullOrWhiteSpace(onnx) || string.IsNullOrWhiteSpace(imagePath))
        {
            return;
        }

        Assert.True(File.Exists(onnx), "EASYAI_CUSTOM_ONNX not found: " + onnx);
        Assert.True(File.Exists(imagePath), "EASYAI_CUSTOM_IMAGE not found: " + imagePath);
        string classesFile = Path.ChangeExtension(onnx, null) + ".classes.txt";
        Assert.True(File.Exists(classesFile), "classes file not found: " + classesFile);

        // The model folder of the app holds exactly one .onnx; stage it the same way.
        string folder = Path.Combine(AppContext.BaseDirectory, "custom-real-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string stagedOnnx = Path.Combine(folder, Path.GetFileName(onnx));
        File.Copy(onnx, stagedOnnx);
        File.Copy(classesFile, Path.ChangeExtension(stagedOnnx, null) + ".classes.txt");
        AiModelResolution files = AiModelFiles.Resolve(AiModelFiles.CustomDetectId, folder);
        Assert.True(files.Found, files.Reason);

        using Mat decoded = Cv2.ImDecode(File.ReadAllBytes(imagePath), ImreadModes.Color);
        Assert.False(decoded.Empty());
        using Mat continuous = decoded.Clone();
        byte[] pixels = new byte[(long)continuous.Width * continuous.Height * 3];
        Marshal.Copy(continuous.Data, pixels, 0, pixels.Length);
        AiImage image = new AiImage(pixels, continuous.Width, continuous.Height, imagePath);

        var options = new YoloWorldOptions { DisplayName = "Custom YOLO", IgnoreTextClasses = true };
        using YoloWorldBackend backend = YoloWorldBackend.Load(files, options);
        Assert.Equal("Custom YOLO", backend.Name);
        string[] classes = File.ReadAllLines(classesFile).Select(l => l.Trim()).Where(l => l.Length > 0).ToArray();
        Assert.Equal(classes, backend.ClassNames.ToArray());

        IReadOnlyList<AiProposal> all = await backend.RunAsync(image, new AiPrompt(), CancellationToken.None);
        Assert.NotEmpty(all);
        Assert.All(all, p => Assert.Contains(p.Label, classes));
        Assert.All(all, p => Assert.True(p.IsBox));

        // A prompt that names an unknown class is ignored for a custom model (no "Re-export needed").
        IReadOnlyList<AiProposal> prompted = await backend.RunAsync(image, new AiPrompt(textClasses: new[] { "definitely-not-a-class" }), CancellationToken.None);
        Assert.Equal(all.Count, prompted.Count);
    }
}
