using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using EasyLabel.SegAI.Model;
using Newtonsoft.Json;

namespace EasyLabel.SegAI.Export;

/// <summary>Why a publish was refused or failed. <see cref="None"/> is a success.</summary>
public enum PublishError
{
    None = 0,
    FolderNotSet,
    FolderMissing,
    FolderNotWritable,
    ModelMissing,
    ModelInvalid,
    UnsupportedVersion,
    Io,
}

/// <summary>What the sidecar JSON records besides the file facts the publisher measures itself.</summary>
/// <param name="ProjectName">Name of the EasyLabel project that trained the model.</param>
/// <param name="RunId">Model id in the project registry (the vNNN.segai file name).</param>
/// <param name="Metrics">The report summary recorded in the registry (mAP, IoU, epochs ...); empty when none was recorded.</param>
/// <param name="DatasetHash">Dataset fingerprint recorded in the registry; empty when unknown.</param>
/// <param name="EasyLabelVersion">Version of the running EasyLabel.</param>
public sealed record PublishMetadata(string ProjectName, string RunId, string Metrics, string DatasetHash, string EasyLabelVersion);

/// <summary>Outcome of <see cref="EasyVisionPublisher.Publish"/>. On failure <see cref="Error"/> is set and nothing is left in the target folder.</summary>
public sealed class PublishResult
{
    public PublishError Error { get; init; }

    public bool Success => Error == PublishError.None;

    /// <summary>English detail of a failure (the file problem, the OS message); empty on success.</summary>
    public string Detail { get; init; } = string.Empty;

    public string ModelPath { get; init; } = string.Empty;

    public string SidecarPath { get; init; } = string.Empty;

    /// <summary>The n of &lt;name&gt;_v&lt;n&gt;.segai; 0 on failure.</summary>
    public int Version { get; init; }

    internal static PublishResult Fail(PublishError error, string detail) => new PublishResult { Error = error, Detail = detail };
}

/// <summary>
/// "Send to EasyVision": copies a trained .segai into the folder EasyVision loads models from as &lt;name&gt;_v&lt;n&gt;.segai (n = highest existing + 1,
/// never an overwrite, earlier versions stay for rollback) plus a &lt;name&gt;_v&lt;n&gt;.json sidecar. UI free. Each file is written to a temp file in the
/// target folder and moved into place, so EasyVision never sees a half-written model. A failure removes everything this call wrote.
/// </summary>
public sealed class EasyVisionPublisher
{
    /// <summary>Highest .segai header version EasyVision reads (BeeNativeSegAI/SegAIFileFormat.h kMaxSupportedSegaiVersion; BeeCore SegmentAI accepts 1..4).</summary>
    public const int EasyVisionMaxSegAiVersion = 4;

    public const string ModelExtension = ".segai";
    public const string SidecarExtension = ".json";
    private const string TempMarker = ".sending-";
    private const int MaxNumberAttempts = 50;

    /// <summary>Test seam: called with "copy" before the model is moved into place and "sidecar" before the sidecar is; a throw simulates that step failing.</summary>
    public Action<string>? FaultHook { get; set; }

    public PublishResult Publish(string modelPath, string? targetFolder, string modelName, PublishMetadata metadata, Func<DateTime> clock)
    {
        if (metadata == null) throw new ArgumentNullException(nameof(metadata));
        if (clock == null) throw new ArgumentNullException(nameof(clock));
        if (string.IsNullOrWhiteSpace(targetFolder))
            return PublishResult.Fail(PublishError.FolderNotSet, "The EasyVision model folder is not set.");
        if (!Directory.Exists(targetFolder))
            return PublishResult.Fail(PublishError.FolderMissing, "The EasyVision model folder does not exist: " + targetFolder);
        if (string.IsNullOrWhiteSpace(modelPath) || !File.Exists(modelPath))
            return PublishResult.Fail(PublishError.ModelMissing, "The model file does not exist: " + modelPath);

        int fileVersion;
        try
        {
            fileVersion = ReadHeaderVersion(modelPath);
        }
        catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
        {
            return PublishResult.Fail(PublishError.ModelInvalid, "The model file cannot be read: " + error.Message);
        }

        if (fileVersion > EasyVisionMaxSegAiVersion)
            return PublishResult.Fail(PublishError.UnsupportedVersion,
                "The model file format version " + fileVersion + " is newer than the newest EasyVision reads (" + EasyVisionMaxSegAiVersion + ").");
        SegAiModelProblem? problem = SegAiModelFile.CheckModelFile(modelPath);
        if (problem != null)
            return PublishResult.Fail(PublishError.ModelInvalid, problem.En);

        string safeName = SafeName(modelName);
        string folder = Path.GetFullPath(targetFolder);
        string token = Guid.NewGuid().ToString("N");
        string tempModel = Path.Combine(folder, safeName + TempMarker + token + ModelExtension + ".tmp");
        string tempSidecar = Path.Combine(folder, safeName + TempMarker + token + SidecarExtension + ".tmp");
        string? finalModel = null;
        string? finalSidecar = null;
        try
        {
            string sha256;
            try
            {
                sha256 = CopyWithHash(modelPath, tempModel);
            }
            catch (UnauthorizedAccessException error)
            {
                return PublishResult.Fail(PublishError.FolderNotWritable, "The EasyVision model folder is not writable: " + error.Message);
            }

            DateTime utc = clock().ToUniversalTime();
            for (int attempt = 0; attempt < MaxNumberAttempts; attempt++)
            {
                int n = NextVersion(folder, safeName);
                string modelTarget = Path.Combine(folder, safeName + "_v" + n.ToString(CultureInfo.InvariantCulture) + ModelExtension);
                string sidecarTarget = Path.Combine(folder, safeName + "_v" + n.ToString(CultureInfo.InvariantCulture) + SidecarExtension);
                if (File.Exists(sidecarTarget) || Directory.Exists(sidecarTarget) || Directory.Exists(modelTarget))
                {
                    // A foreign item already owns the sidecar name: never overwrite it, refuse (caught below as an I/O failure).
                    throw new IOException("The sidecar name is already used by another item: " + sidecarTarget);
                }

                FaultHook?.Invoke("copy");
                try
                {
                    File.Move(tempModel, modelTarget, false);
                }
                catch (IOException) when (File.Exists(modelTarget))
                {
                    continue; // another writer took this number between the scan and the move
                }

                finalModel = modelTarget;
                var sidecar = new
                {
                    schema = 1,
                    modelName = safeName,
                    modelVersion = n,
                    fileName = Path.GetFileName(modelTarget),
                    projectName = metadata.ProjectName ?? string.Empty,
                    runId = metadata.RunId ?? string.Empty,
                    metrics = metadata.Metrics ?? string.Empty,
                    datasetHash = metadata.DatasetHash ?? string.Empty,
                    easyLabelVersion = metadata.EasyLabelVersion ?? string.Empty,
                    segaiFormatVersion = fileVersion,
                    publishedUtc = utc.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture),
                    sourceFile = Path.GetFileName(modelPath),
                    sourceSha256 = sha256,
                    sizeBytes = new FileInfo(modelTarget).Length,
                };
                File.WriteAllText(tempSidecar, JsonConvert.SerializeObject(sidecar, Formatting.Indented));
                FaultHook?.Invoke("sidecar");
                File.Move(tempSidecar, sidecarTarget, false);
                finalSidecar = sidecarTarget;
                return new PublishResult { ModelPath = modelTarget, SidecarPath = sidecarTarget, Version = n };
            }

            return PublishResult.Fail(PublishError.Io, "No free version number could be reserved in " + folder);
        }
        catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
        {
            // Roll back what this call put in the folder; earlier versions are never touched.
            if (finalSidecar == null && finalModel != null) TryDelete(finalModel);
            return PublishResult.Fail(
                error is UnauthorizedAccessException ? PublishError.FolderNotWritable : PublishError.Io,
                "Sending the model failed: " + error.Message);
        }
        finally
        {
            TryDelete(tempModel);
            TryDelete(tempSidecar);
        }
    }

    /// <summary>Next free number for the name: the highest existing &lt;name&gt;_v&lt;n&gt;.segai plus one (a gap is never refilled), 1 for none.</summary>
    public static int NextVersion(string folder, string safeName)
    {
        var pattern = new Regex("^" + Regex.Escape(safeName) + @"_v(\d+)\.segai$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        int highest = 0;
        foreach (string file in Directory.EnumerateFiles(folder, safeName + "_v*" + ModelExtension))
        {
            Match match = pattern.Match(Path.GetFileName(file));
            if (match.Success && int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int n) && n > highest)
                highest = n;
        }

        return highest + 1;
    }

    /// <summary>Model name made safe as a file name part: invalid characters become '_'; empty becomes "model".</summary>
    public static string SafeName(string? name)
    {
        string text = (name ?? string.Empty).Trim();
        foreach (char c in Path.GetInvalidFileNameChars()) text = text.Replace(c, '_');
        text = text.Trim('.', ' ');
        return text.Length == 0 ? "model" : text;
    }

    private static int ReadHeaderVersion(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var header = new byte[12];
        int read = 0;
        while (read < header.Length)
        {
            int count = stream.Read(header, read, header.Length - read);
            if (count <= 0) break;
            read += count;
        }

        return read < header.Length ? 0 : (int)Math.Min(int.MaxValue, BitConverter.ToUInt32(header, 8));
    }

    private static string CopyWithHash(string source, string destination)
    {
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var hash = SHA256.Create();
        var buffer = new byte[81920];
        int count;
        while ((count = input.Read(buffer, 0, buffer.Length)) > 0)
        {
            output.Write(buffer, 0, count);
            hash.TransformBlock(buffer, 0, count, null, 0);
        }

        hash.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        output.Flush(true);
        return System.Convert.ToHexString(hash.Hash!).ToLowerInvariant();
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception)
        {
            // Best effort cleanup of our own temp file.
        }
    }
}
