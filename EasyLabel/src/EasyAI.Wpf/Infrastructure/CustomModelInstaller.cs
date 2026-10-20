using System.IO;
using System.Text.Json;
using EasyLabel.SegAI.Training.Ultralytics;

namespace EasyAI.Desktop.Infrastructure;

/// <summary>Why a YOLO run cannot be installed as the Custom YOLO auto-label model.</summary>
public enum CustomModelProblem
{
    None,

    /// <summary>The run did not finish with status success.</summary>
    NotSuccess,

    /// <summary>The run is not a detect run (segment / obb boxes cannot be used by the box auto-labeller).</summary>
    NotDetect,

    /// <summary>The run has no existing ONNX file.</summary>
    OnnxMissing,

    /// <summary>The models folder is unknown.</summary>
    NoModelsFolder,

    /// <summary>Copying failed (file in use, disk full, no access).</summary>
    CopyFailed,
}

/// <summary>Outcome of <see cref="CustomModelInstaller.Install"/>.</summary>
public sealed class CustomModelInstallResult
{
    public CustomModelInstallResult(CustomModelProblem problem, string message, string? replacedRun)
    {
        Problem = problem;
        Message = message;
        ReplacedRun = replacedRun;
    }

    public CustomModelProblem Problem { get; }

    public bool Success => Problem == CustomModelProblem.None;

    /// <summary>Technical detail of a failure (empty on success).</summary>
    public string Message { get; }

    /// <summary>Name of the run whose model was replaced, or null when there was none (or it is unknown).</summary>
    public string? ReplacedRun { get; }
}

/// <summary>
/// Installs the ONNX of a finished YOLO detect run as the "custom-detect" AI model: <c>&lt;modelsRoot&gt;\custom-detect\&lt;name&gt;.onnx</c>
/// plus <c>&lt;name&gt;.classes.txt</c> (the file name the YOLO backend looks for) and a small <c>source.json</c>. Pure file work with explicit
/// paths, so it is tested with a temp folder. The folder is built next to the target and swapped in: the old folder is renamed aside (to
/// <c>.old-*</c>, retried 3 times), the new one moved in, then the old one deleted best-effort. If the rename or the move fails the old model stays
/// in place (a loaded model locks it; the caller releases the backend first).
/// </summary>
public static class CustomModelInstaller
{
    public const string SourceFileName = "source.json";

    private const string DetectTask = "detect";
    private const string ClassesSuffix = ".classes.txt";

    public static string TargetFolder(string modelsRoot) => Path.Combine(modelsRoot, AiModelCatalog.CustomDetectId);

    /// <summary>The existing ONNX file of a run, or null: the recorded path (absolute or relative to the run folder), then the trainer's default places.</summary>
    public static string? ResolveOnnx(UltraRunRecord run)
    {
        List<string> candidates = new List<string>();
        string recorded = (run.Onnx ?? string.Empty).Trim();
        string dir = run.RunDirectory ?? string.Empty;
        if (recorded.Length > 0)
        {
            candidates.Add(Path.IsPathRooted(recorded) || dir.Length == 0 ? recorded : Path.Combine(dir, recorded));
        }

        if (dir.Length > 0)
        {
            candidates.Add(Path.Combine(dir, "train", "weights", "model.onnx"));
            candidates.Add(Path.Combine(dir, "weights", "model.onnx"));
        }

        foreach (string candidate in candidates)
        {
            try
            {
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch (ArgumentException)
            {
            }
        }

        return null;
    }

    /// <summary>None when the run can be installed: status success, task detect and an existing ONNX file.</summary>
    public static CustomModelProblem Check(UltraRunRecord run)
    {
        if (!string.Equals(run.Status, UltraRunRecord.StatusSuccess, StringComparison.OrdinalIgnoreCase))
        {
            return CustomModelProblem.NotSuccess;
        }

        if (!string.Equals(run.Task, DetectTask, StringComparison.OrdinalIgnoreCase))
        {
            return CustomModelProblem.NotDetect;
        }

        return ResolveOnnx(run) == null ? CustomModelProblem.OnnxMissing : CustomModelProblem.None;
    }

    /// <summary>True when the custom-detect folder holds any file (a model is installed, usable or not).</summary>
    public static bool HasInstalled(string modelsRoot)
    {
        if (string.IsNullOrWhiteSpace(modelsRoot))
        {
            return false;
        }

        try
        {
            string folder = TargetFolder(modelsRoot);
            return Directory.Exists(folder) && Directory.EnumerateFileSystemEntries(folder).Any();
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>The run name stored in <c>source.json</c> of the installed model, or null when there is none or it cannot be read.</summary>
    public static string? ReadInstalledRunName(string modelsRoot)
    {
        if (string.IsNullOrWhiteSpace(modelsRoot))
        {
            return null;
        }

        try
        {
            string path = Path.Combine(TargetFolder(modelsRoot), SourceFileName);
            if (!File.Exists(path))
            {
                return null;
            }

            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("runName", out JsonElement name)
                && name.ValueKind == JsonValueKind.String)
            {
                string value = name.GetString() ?? string.Empty;
                return value.Length == 0 ? null : value;
            }

            return null;
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is JsonException)
        {
            return null;
        }
    }

    /// <summary>Replaces the custom-detect folder with the model of <paramref name="run"/>. Never throws for I/O problems.</summary>
    public static CustomModelInstallResult Install(string modelsRoot, UltraRunRecord run)
    {
        CustomModelProblem problem = Check(run);
        if (problem != CustomModelProblem.None)
        {
            return new CustomModelInstallResult(problem, string.Empty, null);
        }

        if (string.IsNullOrWhiteSpace(modelsRoot))
        {
            return new CustomModelInstallResult(CustomModelProblem.NoModelsFolder, string.Empty, null);
        }

        string onnx = ResolveOnnx(run)!;
        string target = TargetFolder(modelsRoot);
        string stage = target + ".tmp-" + Guid.NewGuid().ToString("N");
        string? replaced = ReadInstalledRunName(modelsRoot);
        SweepStale(target);
        try
        {
            Directory.CreateDirectory(stage);
            string stagedOnnx = Path.Combine(stage, Path.GetFileName(onnx));
            File.Copy(onnx, stagedOnnx, true);
            WriteClasses(run, onnx, Path.ChangeExtension(stagedOnnx, null) + ClassesSuffix);
            WriteSource(stage, run);
            string? old = null;
            if (Directory.Exists(target))
            {
                old = target + ".old-" + Guid.NewGuid().ToString("N");
                if (!TryRenameWithRetry(target, old, out string renameError))
                {
                    TryDelete(stage);
                    return new CustomModelInstallResult(CustomModelProblem.CopyFailed, renameError, replaced);
                }
            }

            try
            {
                Directory.Move(stage, target);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                if (old != null)
                {
                    try
                    {
                        Directory.Move(old, target);
                    }
                    catch (Exception restore) when (restore is IOException || restore is UnauthorizedAccessException)
                    {
                    }
                }

                TryDelete(stage);
                return new CustomModelInstallResult(CustomModelProblem.CopyFailed, ex.Message, replaced);
            }

            if (old != null)
            {
                TryDelete(old);
            }

            return new CustomModelInstallResult(CustomModelProblem.None, string.Empty, replaced);
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            TryDelete(stage);
            return new CustomModelInstallResult(CustomModelProblem.CopyFailed, ex.Message, replaced);
        }
    }

    private static bool TryRenameWithRetry(string from, string to, out string error)
    {
        error = string.Empty;
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                Directory.Move(from, to);
                return true;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                if (attempt >= 3)
                {
                    error = ex.Message;
                    return false;
                }

                Thread.Sleep(200);
            }
        }
    }

    // Leftovers of an interrupted or partly failed install; harmless to Resolve (it only looks inside custom-detect), removed best-effort.
    private static void SweepStale(string target)
    {
        try
        {
            string? parent = Path.GetDirectoryName(target);
            if (string.IsNullOrEmpty(parent) || !Directory.Exists(parent))
            {
                return;
            }

            string name = Path.GetFileName(target);
            foreach (string suffix in new[] { ".tmp-", ".old-" })
            {
                foreach (string dir in Directory.GetDirectories(parent, name + suffix + "*"))
                {
                    TryDelete(dir);
                }
            }
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
        }
    }

    // The classes file lives next to the source ONNX under the source's own stem; it is written under the staged ONNX's stem, which is
    // what YoloWorldBackend looks for. Fallbacks: the trainer's model.classes.txt, then the class list of the run record.
    private static void WriteClasses(UltraRunRecord run, string sourceOnnx, string targetClasses)
    {
        string own = Path.ChangeExtension(sourceOnnx, null) + ClassesSuffix;
        string trainer = Path.Combine(Path.GetDirectoryName(sourceOnnx) ?? string.Empty, "model" + ClassesSuffix);
        if (File.Exists(own))
        {
            File.Copy(own, targetClasses, true);
        }
        else if (File.Exists(trainer))
        {
            File.Copy(trainer, targetClasses, true);
        }
        else if (run.Classes != null && run.Classes.Count > 0)
        {
            File.WriteAllLines(targetClasses, run.Classes);
        }

        // Otherwise the model's own "names" metadata is used by the backend.
    }

    private static void WriteSource(string folder, UltraRunRecord run)
    {
        var source = new
        {
            runName = run.RunName,
            family = run.Family,
            createdUtc = run.CreatedUtc,
            map50 = run.Map50 is double value && !double.IsNaN(value) && !double.IsInfinity(value) ? (double?)value : null,
        };
        File.WriteAllText(Path.Combine(folder, SourceFileName), JsonSerializer.Serialize(source, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void TryDelete(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, true);
            }
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
        }
    }
}
