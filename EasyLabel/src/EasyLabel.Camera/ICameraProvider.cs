namespace EasyLabel.Camera;

public interface ICameraProvider
{
    Task<IReadOnlyList<CameraInfo>> EnumerateAsync(CancellationToken cancellationToken);

    ICameraSource Create(CameraInfo info);
}
