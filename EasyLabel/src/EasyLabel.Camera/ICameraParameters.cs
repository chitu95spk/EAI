namespace EasyLabel.Camera;

/// <summary>
/// Camera settings exposed by a source (implemented by the same object that implements <see cref="ICameraSource"/>).
/// SDK calls run off the caller's thread. Nothing here throws for a missing node, a closed camera or a rejected value:
/// the returned <see cref="CameraParameter"/> carries Supported=false or an Error text instead. Values are clamped
/// to the live range and snapped to the step before they are written.
/// </summary>
public interface ICameraParameters
{
    /// <summary>Reads every parameter in <see cref="CameraParameterId"/> order.</summary>
    Task<IReadOnlyList<CameraParameter>> RefreshAsync(CancellationToken cancellationToken);

    Task<CameraParameter> GetAsync(CameraParameterId id, CancellationToken cancellationToken);

    /// <summary>
    /// Writes a number (Kind=Number) or an option value (Kind=Option) and returns the re-read parameter.
    /// Writing a manual value to an auto-capable parameter switches its auto mode off first. ROI writes are ordered
    /// so the offsets are reset before a size change and re-applied (clamped) afterwards.
    /// </summary>
    Task<CameraParameter> SetAsync(CameraParameterId id, double value, CancellationToken cancellationToken);

    /// <summary>Turns the auto mode of Exposure/Gain on (continuous) or off. Unsupported parameters are returned unchanged.</summary>
    Task<CameraParameter> SetAutoAsync(CameraParameterId id, bool auto, CancellationToken cancellationToken);
}
