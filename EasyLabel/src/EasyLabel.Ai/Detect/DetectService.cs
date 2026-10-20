using EasyLabel.Ai.Runtime;
using Newtonsoft.Json;
using Python.Runtime;

namespace EasyLabel.Ai.Detect;

public sealed class DetectService
{
    private static readonly object PythonLock = new();
    private static bool _pythonInitialized;

    private readonly PythonPaths _paths;
    private readonly EnvironmentBootstrapper _bootstrapper;

    public DetectService()
        : this(PythonPaths.Resolve())
    {
    }

    public DetectService(PythonPaths paths)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _bootstrapper = new EnvironmentBootstrapper(_paths);
    }

    public IReadOnlyList<DetectImageResult> Detect(DetectRequest request)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        ValidateRequest(request);
        _bootstrapper.Ensure(BootstrapMode.Full);

        string requestJson = JsonConvert.SerializeObject(request);
        string responseJson;

        lock (PythonLock)
        {
            EnsurePythonInitialized();
            using (Py.GIL())
            {
                dynamic detectModule = Py.Import("detect");
                using PyObject response = detectModule.detect_json(requestJson);
                responseJson = response.As<string>();
            }
        }

        List<DetectImageResult>? results = JsonConvert.DeserializeObject<List<DetectImageResult>>(responseJson);
        return results ?? throw new EnvironmentSetupException("detect.py returned an empty response.");
    }

    private static void ValidateRequest(DetectRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Model))
        {
            throw new ArgumentException("Detect model path is required.", nameof(request));
        }

        if (request.Images.Count == 0)
        {
            throw new ArgumentException("At least one image is required.", nameof(request));
        }

        string task = request.Task.Trim().ToLowerInvariant();
        if (task is not ("detect" or "obb" or "segment"))
        {
            throw new ArgumentException("Detect task must be detect, obb, or segment.", nameof(request));
        }

        request.Task = task;
    }

    private void EnsurePythonInitialized()
    {
        if (_pythonInitialized)
        {
            return;
        }

        string runtimeDirectory = Path.GetDirectoryName(_paths.PythonExePath) ?? string.Empty;
        string? pythonDll = Directory.EnumerateFiles(runtimeDirectory, "python*.dll", SearchOption.TopDirectoryOnly)
            .OrderByDescending(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

        if (!string.IsNullOrWhiteSpace(pythonDll))
        {
            Python.Runtime.Runtime.PythonDLL = pythonDll;
        }

        PythonEngine.PythonHome = runtimeDirectory;
        PythonEngine.PythonPath = string.Join(
            Path.PathSeparator,
            new[] { _paths.ScriptsDirectory, PythonEngine.PythonPath }
                .Where(path => !string.IsNullOrWhiteSpace(path)));
        PythonEngine.Initialize();
        _pythonInitialized = true;
    }
}
