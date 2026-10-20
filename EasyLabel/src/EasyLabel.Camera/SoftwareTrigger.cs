namespace EasyLabel.Camera;

internal static class SoftwareTrigger
{
    // CameraParams.h:855 MV_TRIGGER_MODE_ON, :885 MV_TRIGGER_SOURCE_SOFTWARE.
    private const uint TriggerModeOn = 1;
    private const uint TriggerSourceSoftware = 7;

    /// <summary>Sends TriggerSoftware when the camera waits for a software trigger. Returns true when it fired.</summary>
    internal static bool FireIfEnabled(ICameraNodeMap nodes)
    {
        if (nodes.TryGetEnum("TriggerMode", out uint mode, out _)
            && mode == TriggerModeOn
            && nodes.TryGetEnum("TriggerSource", out uint source, out _)
            && source == TriggerSourceSoftware)
        {
            return nodes.TryExecute("TriggerSoftware");
        }

        return false;
    }
}
