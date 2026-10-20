namespace EasyLabel.Camera;

/// <summary>
/// SDK calls run on an internal worker thread, never the UI thread. StreamAsync is latest-frame-wins:
/// frames the consumer has not taken are disposed by the source. Faulted is raised off the UI thread.
/// </summary>
public interface ICameraSource : IAsyncDisposable
{
    CameraInfo Info { get; }

    CameraState State { get; }

    event EventHandler<CameraFaultEventArgs> Faulted;

    Task OpenAsync(CancellationToken cancellationToken);

    Task<CameraFrame> GrabOneAsync(TimeSpan timeout, CancellationToken cancellationToken);

    IAsyncEnumerable<CameraFrame> StreamAsync(CancellationToken cancellationToken);

    Task CloseAsync();
}
