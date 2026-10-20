// Replaces PreserveTrainingFailure/CleanupTrainingArtifact of BeeCore/Unit/SegmentAI.TrainingEvaluation.cs@d1201fa3d (:497-523):
// failures are recorded in .easyai/runs/<utc>/ and only the newest few failed runs are kept.
using System.Globalization;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace EasyLabel.SegAI.Training;

/// <summary>
/// .easyai/runs/&lt;utc&gt;/ holds one folder per training run: work/ (temporary, deleted when the run ends), status.json and, for a
/// failure, failure.json. Only folders whose name matches the run pattern and whose status is failed or cancelled are ever pruned;
/// successful runs and anything else in the folder are never deleted.
/// </summary>
internal static class SegAiRunFolders
{
    internal const int KeepFailed = 5;
    internal const string StatusFileName = "status.json";

    private static readonly Regex RunNamePattern = new(@"^\d{8}T\d{9}Z(_\d+)?$", RegexOptions.CultureInvariant);

    /// <summary>Creates a new run folder named after the current UTC time; a name that already exists gets a numeric suffix.</summary>
    internal static string Create(string runsDir)
    {
        if (string.IsNullOrWhiteSpace(runsDir)) throw new ArgumentException("Runs folder is required.", nameof(runsDir));
        string root = Path.GetFullPath(runsDir);
        Directory.CreateDirectory(root);
        string stamp = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmssfff'Z'", CultureInfo.InvariantCulture);
        for (int attempt = 0; attempt < 1000; attempt++)
        {
            string path = Path.Combine(root, attempt == 0 ? stamp : stamp + "_" + attempt.ToString(CultureInfo.InvariantCulture));
            if (Directory.Exists(path) || File.Exists(path)) continue;
            Directory.CreateDirectory(path);
            return path;
        }

        throw new IOException("Could not create a run folder in " + root);
    }

    internal static bool IsRunName(string name) => RunNamePattern.IsMatch(name);

    /// <summary>Best effort: a status record must never hide the real outcome.</summary>
    internal static void WriteStatus(string runDir, string status, string? model, string? message)
    {
        try
        {
            var doc = new JObject
            {
                ["status"] = status,
                ["updatedUtc"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
            };
            if (model != null) doc["model"] = model;
            if (message != null) doc["message"] = message.Length > 4096 ? message.Substring(0, 4096) : message;
            File.WriteAllText(Path.Combine(runDir, StatusFileName), doc.ToString(Formatting.Indented));
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
        }
    }

    internal static void WriteFailure(string runDir, string stage, string inputDimensions, bool gpu, Exception error)
    {
        try
        {
            string details = error.ToString();
            if (details.Length > 16384) details = details.Substring(0, 16384);
            File.WriteAllText(Path.Combine(runDir, "failure.json"), JsonConvert.SerializeObject(new
            {
                Stage = stage,
                Backend = gpu ? "GPU requested" : "CPU",
                Input = inputDimensions,
                Error = details,
            }, Formatting.Indented));
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Reads the status field of a run folder, or null when absent or unreadable.</summary>
    internal static string? ReadStatus(string runDir)
    {
        try
        {
            string path = Path.Combine(runDir, StatusFileName);
            if (!File.Exists(path)) return null;
            return JObject.Parse(File.ReadAllText(path))["status"]?.Value<string>();
        }
        catch (Exception ex) when (ex is JsonException || ex is IOException || ex is UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Deletes a directory only when it lies strictly inside <paramref name="root"/>. Never throws.</summary>
    internal static void TryDeleteInside(string root, string directory)
    {
        try
        {
            string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string full = Path.GetFullPath(directory);
            if (!full.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase) || !Directory.Exists(full)) return;
            if ((File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0) return;
            Directory.Delete(full, true);
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException)
        {
        }
    }

    /// <summary>
    /// Keeps the newest <paramref name="keep"/> runs whose status is failed or cancelled and deletes the older ones. Successful,
    /// running, status-less and oddly named folders are untouched. Never throws.
    /// </summary>
    internal static void PruneFailed(string runsDir, int keep)
    {
        try
        {
            if (!Directory.Exists(runsDir)) return;
            string root = Path.GetFullPath(runsDir);
            List<string> failed = Directory.EnumerateDirectories(root)
                .Where(d => IsRunName(Path.GetFileName(d)))
                .Where(d => (File.GetAttributes(d) & FileAttributes.ReparsePoint) == 0)
                .Where(d =>
                {
                    string? status = ReadStatus(d);
                    return status == "failed" || status == "cancelled";
                })
                .OrderByDescending(d => Path.GetFileName(d), StringComparer.Ordinal)
                .ToList();
            foreach (string old in failed.Skip(Math.Max(0, keep))) TryDeleteInside(root, old);
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
        }
    }
}
