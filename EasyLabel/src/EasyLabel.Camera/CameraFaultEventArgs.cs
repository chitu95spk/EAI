namespace EasyLabel.Camera;

public sealed class CameraFaultEventArgs : EventArgs
{
    public CameraFaultEventArgs(string message, Exception? exception = null)
    {
        Message = message;
        Exception = exception;
    }

    public string Message { get; }

    public Exception? Exception { get; }
}
