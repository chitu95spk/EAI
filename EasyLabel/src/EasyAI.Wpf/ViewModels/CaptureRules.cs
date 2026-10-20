using EasyLabel.Camera;

namespace EasyAI.Desktop.ViewModels;

/// <summary>Why a capture button is disabled (None = it can run).</summary>
public enum CaptureBlock
{
    None,
    Busy,
    NotConnected,
    NoProject,
    NoImage,
    TrainingActive,
}

/// <summary>Pure rules of the capture buttons on the Label and Inspect tabs (no WPF types).</summary>
public static class CaptureRules
{
    /// <summary>Grab timeout of one capture.</summary>
    public static readonly TimeSpan GrabTimeout = TimeSpan.FromSeconds(5);

    /// <summary>The buttons exist only while the camera is the image source.</summary>
    public static bool IsVisible(bool cameraSourceActive)
    {
        return cameraSourceActive;
    }

    /// <summary>
    /// First reason the button cannot run. <paramref name="needsImage"/> is true for "Capture to current" (it overwrites the selected image).
    /// </summary>
    public static CaptureBlock BlockOf(CameraServiceState state, bool hasProject, bool trainingActive, bool busy, bool needsImage, bool hasImage)
    {
        if (busy)
        {
            return CaptureBlock.Busy;
        }

        if (state != CameraServiceState.Connected)
        {
            return CaptureBlock.NotConnected;
        }

        if (!hasProject)
        {
            return CaptureBlock.NoProject;
        }

        if (trainingActive)
        {
            return CaptureBlock.TrainingActive;
        }

        if (needsImage && !hasImage)
        {
            return CaptureBlock.NoImage;
        }

        return CaptureBlock.None;
    }

    public static bool IsEnabled(CaptureBlock block)
    {
        return block == CaptureBlock.None;
    }

    /// <summary>Resource key of the text that explains a disabled button, or null (nothing to say).</summary>
    public static string? BlockKey(CaptureBlock block)
    {
        switch (block)
        {
            case CaptureBlock.NotConnected: return "CaptureBlockNotConnected";
            case CaptureBlock.NoProject: return "CaptureBlockNoProject";
            case CaptureBlock.NoImage: return "CaptureBlockNoImage";
            case CaptureBlock.TrainingActive: return "CaptureBlockTraining";
            default: return null;
        }
    }

    /// <summary>The overwrite warning is asked once per app session (there is no backup of the original); false after the user accepted it.</summary>
    public static bool NeedsOverwriteConfirmation(bool alreadyConfirmed)
    {
        return !alreadyConfirmed;
    }

    /// <summary>Resource key of the notice for a failed grab: a timeout has its own text, anything else shows the message.</summary>
    public static string GrabFailureKey(Exception exception)
    {
        return exception is TimeoutException ? "CaptureErrTimeoutFormat" : "CaptureErrGrabFormat";
    }

    /// <summary>
    /// Resource key of the notice after "Capture to current" wrote the frame, or null when nothing needs saying. The format arguments are
    /// {0} = old size, {1} = new size (both "W x H") and {2} = number of shapes with a point outside the new frame.
    /// </summary>
    public static string? ReplaceNoticeKey(bool sizeChanged, int outOfFrameShapes)
    {
        if (sizeChanged && outOfFrameShapes > 0)
        {
            return "CaptureReplacedBothFormat";
        }

        if (sizeChanged)
        {
            return "CaptureReplacedSizeFormat";
        }

        return outOfFrameShapes > 0 ? "CaptureReplacedOutOfFrameFormat" : null;
    }

    public static string SizeText(int width, int height)
    {
        return width.ToString(System.Globalization.CultureInfo.InvariantCulture) + " x " + height.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
}
