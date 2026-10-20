namespace EasyLabel.Ai.Runtime;

public sealed record BootstrapResult(
    IReadOnlyList<string> AlreadyPresent,
    IReadOnlyList<string> Installed,
    IReadOnlyList<string> Missing,
    bool CudaAvailable);
