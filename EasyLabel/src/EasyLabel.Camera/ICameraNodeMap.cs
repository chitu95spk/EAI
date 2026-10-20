namespace EasyLabel.Camera;

/// <summary>
/// Minimal GenICam-style node access used by <see cref="CameraParameterEngine"/>. Every method returns false when
/// the node is missing, has another type, is not accessible, or the value is rejected; none may throw.
/// </summary>
internal interface ICameraNodeMap
{
    bool TryGetInt(string name, out long current, out long min, out long max, out long increment);

    bool TrySetInt(string name, long value);

    bool TryGetFloat(string name, out double current, out double min, out double max);

    bool TrySetFloat(string name, double value);

    bool TryGetEnum(string name, out uint current, out uint[] supported);

    bool TrySetEnum(string name, uint value);

    bool TryGetEnumSymbolic(string name, uint value, out string symbolic);

    bool TryGetBool(string name, out bool value);

    bool TrySetBool(string name, bool value);

    bool TryExecute(string name);
}
