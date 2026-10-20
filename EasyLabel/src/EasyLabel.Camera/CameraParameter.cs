namespace EasyLabel.Camera;

public enum CameraParameterId
{
    ExposureTime,
    Gain,
    Gamma,
    TriggerMode,
    TriggerSource,
    PixelFormat,
    FrameRate,
    Width,
    Height,
    OffsetX,
    OffsetY,
    WhiteBalanceAuto,
    WhiteBalanceRed,
    WhiteBalanceGreen,
    WhiteBalanceBlue,
}

public enum CameraParameterKind
{
    /// <summary>Continuous or stepped number in [Min, Max].</summary>
    Number,

    /// <summary>One of <see cref="CameraParameter.Options"/>; Value holds the option value.</summary>
    Option,
}

public sealed record CameraParameterOption(long Value, string Name);

/// <summary>
/// Snapshot of one camera setting. When Supported is false (the camera lacks the node) every other field is a
/// neutral default and the parameter should be hidden. Error is set when the last write was rejected; the other
/// fields then describe the state after the failed write.
/// </summary>
public sealed record CameraParameter(
    CameraParameterId Id,
    bool Supported,
    CameraParameterKind Kind,
    double Min,
    double Max,
    double Step,
    double Value,
    bool SupportsAuto,
    bool IsAuto,
    string Unit,
    IReadOnlyList<CameraParameterOption> Options,
    string? Error)
{
    private static readonly IReadOnlyList<CameraParameterOption> NoOptions = Array.Empty<CameraParameterOption>();

    public static CameraParameter NotSupported(CameraParameterId id, CameraParameterKind kind, string unit = "")
        => new CameraParameter(id, false, kind, 0, 0, 0, 0, false, false, unit, NoOptions, null);

    /// <summary>Name of the current option, or the numeric value when it is not in the list.</summary>
    public string ValueText()
    {
        if (Kind == CameraParameterKind.Option)
        {
            foreach (CameraParameterOption option in Options)
            {
                if (option.Value == (long)Value)
                {
                    return option.Name;
                }
            }
        }

        return Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
}
