using System.ComponentModel;
using System.Diagnostics;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace EasyLabel.Ai.Runtime;

public sealed class EnvironmentBootstrapper
{
    private const int ProcessTimeoutMilliseconds = 120000;
    private readonly PythonPaths _paths;

    public EnvironmentBootstrapper()
        : this(PythonPaths.Resolve())
    {
    }

    public EnvironmentBootstrapper(PythonPaths paths)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
    }

    public BootstrapResult Ensure(BootstrapMode mode)
    {
        ValidateRuntimeInputs();

        List<RequiredPackage> required = ParseRequirements(_paths.RequirementsPath);
        Dictionary<string, string> installed = ReadInstalledDistributions();
        List<string> alreadyPresent = new();
        List<RequiredPackage> missingOrMismatched = new();

        foreach (RequiredPackage package in required)
        {
            if (installed.TryGetValue(package.NormalizedName, out string? installedVersion)
                && string.Equals(installedVersion, package.Version, StringComparison.OrdinalIgnoreCase))
            {
                alreadyPresent.Add(package.DisplayName);
            }
            else
            {
                missingOrMismatched.Add(package);
            }
        }

        List<RequiredPackage> missingWheels = missingOrMismatched
            .Where(package => !HasOfflineWheel(package))
            .ToList();

        if (missingWheels.Count > 0)
        {
            throw new EnvironmentSetupException(
                "Missing offline wheel(s) in Lib/: " + string.Join(", ", missingWheels.Select(package => package.DisplayName)));
        }

        List<string> installedNow = new();
        if (missingOrMismatched.Count > 0)
        {
            InstallRequirements();
            installedNow.AddRange(missingOrMismatched.Select(package => package.DisplayName));
        }

        bool cudaAvailable = mode == BootstrapMode.Full && CheckCudaAvailable();
        return new BootstrapResult(alreadyPresent, installedNow, Array.Empty<string>(), cudaAvailable);
    }

    private void ValidateRuntimeInputs()
    {
        if (!File.Exists(_paths.PythonExePath))
        {
            throw new EnvironmentSetupException(
                $"Bundled Python runtime is missing. Expected python.exe at: {_paths.PythonExePath}");
        }

        if (!File.Exists(_paths.RequirementsPath))
        {
            throw new EnvironmentSetupException(
                $"Python requirements.lock is missing: {_paths.RequirementsPath}");
        }

        if (!Directory.Exists(_paths.WheelsDirectory))
        {
            throw new EnvironmentSetupException(
                $"Offline wheel folder is missing. Expected Lib/ at: {_paths.WheelsDirectory}");
        }
    }

    private Dictionary<string, string> ReadInstalledDistributions()
    {
        const string script = "import importlib.metadata as m, json; print(json.dumps([{'name': d.metadata['Name'], 'version': d.version} for d in m.distributions()]))";
        ProcessResult result = RunPython("-c", script);
        if (result.ExitCode != 0)
        {
            throw new EnvironmentSetupException(
                $"Could not inspect installed Python packages. {result.StandardError}".Trim());
        }

        try
        {
            JArray packages = JArray.Parse(result.StandardOutput);
            Dictionary<string, string> installed = new(StringComparer.OrdinalIgnoreCase);
            foreach (JToken token in packages)
            {
                string? name = token.Value<string>("name");
                string? version = token.Value<string>("version");
                if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(version))
                {
                    installed[NormalizeName(name)] = version;
                }
            }

            return installed;
        }
        catch (JsonException ex)
        {
            throw new EnvironmentSetupException("Python package inspection returned invalid JSON.", ex);
        }
    }

    private void InstallRequirements()
    {
        Directory.CreateDirectory(_paths.LogDirectory);
        string logPath = Path.Combine(_paths.LogDirectory, $"bootstrap-{DateTime.UtcNow:yyyyMMdd-HHmmss}.log");
        ProcessResult result = RunPython(
            "-m",
            "pip",
            "install",
            "--no-index",
            "--find-links",
            _paths.WheelsDirectory,
            "-r",
            _paths.RequirementsPath);

        File.WriteAllText(
            logPath,
            $"ExitCode: {result.ExitCode}{Environment.NewLine}{Environment.NewLine}STDOUT:{Environment.NewLine}{result.StandardOutput}{Environment.NewLine}STDERR:{Environment.NewLine}{result.StandardError}");

        if (result.ExitCode != 0)
        {
            throw new EnvironmentSetupException(
                $"Offline Python package install failed. See log: {logPath}");
        }
    }

    private bool CheckCudaAvailable()
    {
        ProcessResult result = RunPython("-c", "import torch; print(torch.cuda.is_available())");
        if (result.ExitCode != 0)
        {
            throw new EnvironmentSetupException(
                $"Could not verify CUDA availability. {result.StandardError}".Trim());
        }

        return result.StandardOutput.Trim().Equals("True", StringComparison.OrdinalIgnoreCase);
    }

    private ProcessResult RunPython(params string[] arguments)
    {
        ProcessStartInfo startInfo = new(_paths.PythonExePath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using Process process = new()
        {
            StartInfo = startInfo
        };

        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException)
        {
            throw new EnvironmentSetupException($"Could not start bundled Python: {_paths.PythonExePath}", ex);
        }

        string standardOutput = process.StandardOutput.ReadToEnd();
        string standardError = process.StandardError.ReadToEnd();
        if (!process.WaitForExit(ProcessTimeoutMilliseconds))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }

            throw new EnvironmentSetupException("Bundled Python command timed out.");
        }

        return new ProcessResult(process.ExitCode, standardOutput, standardError);
    }

    private bool HasOfflineWheel(RequiredPackage package)
    {
        string normalizedPackage = NormalizeForWheelFile(package.Name);
        string normalizedVersion = package.Version.Replace('-', '_');

        return Directory.EnumerateFiles(_paths.WheelsDirectory, "*.whl", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileNameWithoutExtension)
            .Any(fileName =>
                fileName is not null
                && fileName.StartsWith($"{normalizedPackage}-{normalizedVersion}-", StringComparison.OrdinalIgnoreCase));
    }

    private static List<RequiredPackage> ParseRequirements(string path)
    {
        List<RequiredPackage> packages = new();
        List<string> unsupported = new();

        foreach (string rawLine in File.ReadLines(path))
        {
            string line = StripComment(rawLine).Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (line.StartsWith("--no-index", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("--find-links", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("-f ", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string[] parts = line.Split(new[] { "==" }, StringSplitOptions.None);
            if (parts.Length != 2 || string.IsNullOrWhiteSpace(parts[0]) || string.IsNullOrWhiteSpace(parts[1]))
            {
                unsupported.Add(rawLine.Trim());
                continue;
            }

            packages.Add(new RequiredPackage(parts[0].Trim(), parts[1].Trim()));
        }

        if (unsupported.Count > 0)
        {
            throw new EnvironmentSetupException(
                "Unsupported requirements.lock line(s). Pin every package as name==version: " + string.Join(", ", unsupported));
        }

        return packages;
    }

    private static string StripComment(string line)
    {
        int commentIndex = line.IndexOf('#');
        return commentIndex < 0 ? line : line[..commentIndex];
    }

    private static string NormalizeName(string name)
    {
        return name.Trim().Replace('_', '-').Replace('.', '-').ToLowerInvariant();
    }

    private static string NormalizeForWheelFile(string name)
    {
        return name.Trim().Replace('-', '_').Replace('.', '_');
    }

    private sealed record RequiredPackage(string Name, string Version)
    {
        public string NormalizedName { get; } = NormalizeName(Name);

        public string DisplayName { get; } = $"{Name}=={Version}";
    }

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
}
