using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;

namespace EasyLabel.Core.Models;

public enum ModelPackError
{
    None = 0,
    InvalidArgument,
    PackMissing,
    BadArchive,
    ManifestInvalid,
    UnsafeEntry,
    UnlistedEntry,
    MissingEntry,
    TooLarge,
    SizeMismatch,
    BadHash,
    Cancelled,
    Io,
}

public sealed class ModelPackInstallOptions
{
    public const long DefaultMaxTotalBytes = 2L * 1024 * 1024 * 1024;

    /// <summary>Upper bound on the sum of the declared file sizes; a larger pack is refused before anything is extracted.</summary>
    public long MaxTotalBytes { get; init; } = DefaultMaxTotalBytes;
}

/// <summary>Outcome of reading a pack's manifest (no files extracted). Use it to show the licence before installing.</summary>
public sealed class ModelPackReadResult
{
    public ModelPackError Error { get; init; }

    public string Message { get; init; } = string.Empty;

    public ModelPackManifest? Manifest { get; init; }

    public bool Success => Error == ModelPackError.None && Manifest != null;
}

public sealed class ModelPackInstallResult
{
    public ModelPackError Error { get; init; }

    public string Message { get; init; } = string.Empty;

    /// <summary>The file that failed the size or checksum check; empty otherwise.</summary>
    public string BadFile { get; init; } = string.Empty;

    /// <summary>Installed paths relative to the models root, e.g. <c>classify-backbone\mobilenetv2-12-cls.onnx</c>.</summary>
    public IReadOnlyList<string> InstalledFiles { get; init; } = Array.Empty<string>();

    /// <summary>Folder names of replaced installs, kept as <c>{target}.bak-{utc}</c> under the models root.</summary>
    public IReadOnlyList<string> BackupFolders { get; init; } = Array.Empty<string>();

    public bool Success => Error == ModelPackError.None;
}

/// <summary>
/// Installs a model pack (zip with <c>manifest.json</c>) into a models root. Nothing in the pack is executed: entries are
/// only copied as bytes. Flow: validate manifest, reject unsafe or unlisted entries, extract to <c>.staging-{guid}</c>,
/// verify size and SHA-256 of every file, then move each target folder into place. A replaced folder is kept as
/// <c>{target}.bak-{utc}</c>. If a move fails, the already-placed folders are removed and the backups restored.
/// Not safe for two concurrent installs into the same models root.
/// </summary>
public static class ModelPackInstaller
{
    public const string ManifestEntryName = "manifest.json";

    public const int MaxManifestBytes = 1024 * 1024;

    public const int MaxFiles = 64;

    private const int BufferBytes = 81920;

    private static readonly Regex IdPattern = new Regex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,63}\z", RegexOptions.CultureInvariant);

    private static readonly Regex FilePattern = new Regex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,127}\z", RegexOptions.CultureInvariant);

    private static readonly Regex TargetPattern = new Regex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,63}\z", RegexOptions.CultureInvariant);

    private static readonly Regex Sha256Pattern = new Regex(@"^[0-9a-fA-F]{64}\z", RegexOptions.CultureInvariant);

    /// <summary>Reads and validates only the manifest. Does not extract or verify the files.</summary>
    public static ModelPackReadResult ReadManifest(string packPath)
    {
        try
        {
            using ZipArchive zip = OpenPack(packPath);
            return new ModelPackReadResult { Manifest = ReadManifestEntry(zip, ModelPackInstallOptions.DefaultMaxTotalBytes) };
        }
        catch (PackFailure ex)
        {
            return new ModelPackReadResult { Error = ex.Error, Message = ex.Message };
        }
        catch (InvalidDataException ex)
        {
            return new ModelPackReadResult { Error = ModelPackError.BadArchive, Message = ex.Message };
        }
        catch (NotSupportedException ex)
        {
            return new ModelPackReadResult { Error = ModelPackError.BadArchive, Message = ex.Message };
        }
        catch (IOException ex)
        {
            return new ModelPackReadResult { Error = ModelPackError.Io, Message = ex.Message };
        }
        catch (UnauthorizedAccessException ex)
        {
            return new ModelPackReadResult { Error = ModelPackError.Io, Message = ex.Message };
        }
    }

    /// <summary>Installs the pack into <paramref name="modelsRoot"/>. Blocking; call from a background thread.</summary>
    public static ModelPackInstallResult Install(string packPath, string modelsRoot, ModelPackInstallOptions? options = null, CancellationToken ct = default)
    {
        ModelPackInstallOptions opts = options ?? new ModelPackInstallOptions();
        if (string.IsNullOrWhiteSpace(modelsRoot) || opts.MaxTotalBytes <= 0)
        {
            return InstallFailed(ModelPackError.InvalidArgument, "A models folder and a positive size limit are required.");
        }

        string root;
        try
        {
            root = Path.GetFullPath(modelsRoot);
        }
        catch (ArgumentException ex)
        {
            return InstallFailed(ModelPackError.InvalidArgument, ex.Message);
        }

        string staging = Path.Combine(root, ".staging-" + Guid.NewGuid().ToString("N"));
        try
        {
            using ZipArchive zip = OpenPack(packPath);
            ModelPackManifest manifest = ReadManifestEntry(zip, opts.MaxTotalBytes);
            Dictionary<string, ZipArchiveEntry> entries = IndexEntries(zip, manifest);

            Directory.CreateDirectory(root);
            Directory.CreateDirectory(staging);
            foreach (ModelPackFile file in manifest.Files)
            {
                ct.ThrowIfCancellationRequested();
                ExtractAndVerify(entries[file.FileName], file, staging, ct);
            }

            return Commit(manifest, root, staging);
        }
        catch (PackFailure ex)
        {
            return InstallFailed(ex.Error, ex.Message, ex.BadFile);
        }
        catch (OperationCanceledException)
        {
            return InstallFailed(ModelPackError.Cancelled, "Model pack install was cancelled.");
        }
        catch (InvalidDataException ex)
        {
            return InstallFailed(ModelPackError.BadArchive, ex.Message);
        }
        catch (NotSupportedException ex)
        {
            return InstallFailed(ModelPackError.BadArchive, ex.Message);
        }
        catch (IOException ex)
        {
            return InstallFailed(ModelPackError.Io, ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            return InstallFailed(ModelPackError.Io, ex.Message);
        }
        finally
        {
            DeleteQuietly(staging);
        }
    }

    private static ModelPackInstallResult InstallFailed(ModelPackError error, string message, string badFile = "")
    {
        return new ModelPackInstallResult { Error = error, Message = message, BadFile = badFile };
    }

    private static ZipArchive OpenPack(string packPath)
    {
        if (string.IsNullOrWhiteSpace(packPath) || !File.Exists(packPath))
        {
            throw new PackFailure(ModelPackError.PackMissing, "Model pack not found: " + packPath);
        }

        return ZipFile.OpenRead(packPath);
    }

    private static ModelPackManifest ReadManifestEntry(ZipArchive zip, long maxTotalBytes)
    {
        ZipArchiveEntry? entry = zip.GetEntry(ManifestEntryName);
        if (entry == null)
        {
            throw Invalid("manifest.json is missing from the pack.");
        }

        string text;
        using (Stream source = entry.Open())
        using (MemoryStream buffer = new MemoryStream())
        {
            CopyCapped(source, buffer, MaxManifestBytes, ModelPackError.TooLarge, CancellationToken.None);
            text = Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
        }

        ModelPackManifest? manifest;
        try
        {
            manifest = JsonConvert.DeserializeObject<ModelPackManifest>(text);
        }
        catch (JsonException ex)
        {
            throw Invalid("manifest.json is not valid: " + ex.Message);
        }

        return ValidateManifest(manifest, maxTotalBytes);
    }

    private static ModelPackManifest ValidateManifest(ModelPackManifest? manifest, long maxTotalBytes)
    {
        if (manifest == null)
        {
            throw Invalid("manifest.json is empty.");
        }

        if (manifest.Schema != ModelPackManifest.CurrentSchema)
        {
            throw Invalid("Unsupported manifest schema " + manifest.Schema.ToString(CultureInfo.InvariantCulture) + ".");
        }

        if (string.IsNullOrEmpty(manifest.Id) || !IdPattern.IsMatch(manifest.Id))
        {
            throw Invalid("Pack id is missing or not a safe name.");
        }

        if (manifest.Files == null || manifest.Files.Count == 0 || manifest.Files.Count > MaxFiles)
        {
            throw Invalid("A pack must list between 1 and " + MaxFiles.ToString(CultureInfo.InvariantCulture) + " files.");
        }

        HashSet<string> keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (ModelPackFile file in manifest.Files)
        {
            if (file == null)
            {
                throw Invalid("A file entry is empty.");
            }

            if (string.IsNullOrEmpty(file.FileName))
            {
                throw Invalid("A file entry has no file name.");
            }

            if (!FilePattern.IsMatch(file.FileName) || string.Equals(file.FileName, ManifestEntryName, StringComparison.OrdinalIgnoreCase))
            {
                throw new PackFailure(ModelPackError.UnsafeEntry, "Unsafe file name in manifest: " + file.FileName, file.FileName);
            }

            if (string.IsNullOrEmpty(file.Target))
            {
                throw Invalid("A file entry has no install folder: " + file.FileName + ".");
            }

            if (!TargetPattern.IsMatch(file.Target))
            {
                throw new PackFailure(ModelPackError.UnsafeEntry, "Unsafe install folder for " + file.FileName, file.FileName);
            }

            if (!keys.Add(file.Target + "/" + file.FileName))
            {
                throw Invalid("File " + file.FileName + " is listed twice for " + file.Target + ".");
            }

            if (file.Size < 0)
            {
                throw Invalid("Negative size for " + file.FileName + ".");
            }

            if (file.Size > maxTotalBytes - total)
            {
                throw new PackFailure(ModelPackError.TooLarge, "Model pack is larger than the allowed size.");
            }

            total += file.Size;
            if (string.IsNullOrEmpty(file.Sha256) || !Sha256Pattern.IsMatch(file.Sha256))
            {
                throw Invalid("Checksum for " + file.FileName + " is not a SHA-256 hex value.");
            }

            file.Sha256 = file.Sha256.ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(file.Licence))
            {
                throw Invalid("Licence is missing for " + file.FileName + ".");
            }
        }

        return manifest;
    }

    private static Dictionary<string, ZipArchiveEntry> IndexEntries(ZipArchive zip, ModelPackManifest manifest)
    {
        Dictionary<string, ZipArchiveEntry> index = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
        foreach (ZipArchiveEntry entry in zip.Entries)
        {
            if (IsUnsafeEntryName(entry.FullName))
            {
                throw new PackFailure(ModelPackError.UnsafeEntry, "Unsafe entry name in pack: " + entry.FullName, entry.FullName);
            }

            if (index.ContainsKey(entry.FullName))
            {
                throw Invalid("Entry " + entry.FullName + " appears twice in the pack.");
            }

            index.Add(entry.FullName, entry);
        }

        HashSet<string> listed = new HashSet<string>(StringComparer.Ordinal) { ManifestEntryName };
        foreach (ModelPackFile file in manifest.Files)
        {
            listed.Add(file.FileName);
        }

        foreach (string name in index.Keys)
        {
            if (!listed.Contains(name))
            {
                throw new PackFailure(ModelPackError.UnlistedEntry, "Entry not listed in the manifest: " + name, name);
            }
        }

        foreach (ModelPackFile file in manifest.Files)
        {
            if (!index.ContainsKey(file.FileName))
            {
                throw new PackFailure(ModelPackError.MissingEntry, "Listed file is missing from the pack: " + file.FileName, file.FileName);
            }
        }

        return index;
    }

    /// <summary>Rejects absolute, drive-qualified, backslash, directory and dot-segment names (zip-slip).</summary>
    private static bool IsUnsafeEntryName(string name)
    {
        if (string.IsNullOrEmpty(name) || name.Contains('\\') || name.Contains(':') || name.EndsWith('/') || name.StartsWith('/'))
        {
            return true;
        }

        if (Path.IsPathRooted(name))
        {
            return true;
        }

        foreach (string segment in name.Split('/'))
        {
            if (segment.Length == 0 || segment == "." || segment == "..")
            {
                return true;
            }
        }

        return false;
    }

    private static void ExtractAndVerify(ZipArchiveEntry entry, ModelPackFile file, string staging, CancellationToken ct)
    {
        string dir = Path.Combine(staging, file.Target);
        string dest = Path.Combine(dir, file.FileName);
        EnsureInside(staging, dest);
        Directory.CreateDirectory(dir);

        using (Stream source = entry.Open())
        using (FileStream target = new FileStream(dest, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferBytes))
        {
            long copied = CopyCapped(source, target, file.Size, ModelPackError.SizeMismatch, ct);
            if (copied != file.Size)
            {
                throw new PackFailure(ModelPackError.SizeMismatch, "Size of " + file.FileName + " differs from the manifest.", file.FileName);
            }
        }

        if (!string.Equals(Sha256Hex(dest), file.Sha256, StringComparison.Ordinal))
        {
            throw new PackFailure(ModelPackError.BadHash, "File " + file.FileName + " is damaged (checksum differs).", file.FileName);
        }
    }

    private static ModelPackInstallResult Commit(ModelPackManifest manifest, string root, string staging)
    {
        string stamp = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        List<string> targets = manifest.Files.Select(f => f.Target).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        List<string> placed = new List<string>();
        List<KeyValuePair<string, string>> backups = new List<KeyValuePair<string, string>>();
        try
        {
            foreach (string target in targets)
            {
                string final = Path.Combine(root, target);
                if (Directory.Exists(final))
                {
                    string backup = BackupPath(root, target, stamp);
                    Directory.Move(final, backup);
                    backups.Add(new KeyValuePair<string, string>(final, backup));
                }

                Directory.Move(Path.Combine(staging, target), final);
                placed.Add(final);
            }
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            bool restored = Rollback(placed, backups);
            string text = "Install failed: " + ex.Message + (restored ? " Previous files were restored." : " Rollback was incomplete; check the .bak folders.");
            return InstallFailed(ModelPackError.Io, text);
        }

        return new ModelPackInstallResult
        {
            InstalledFiles = manifest.Files.Select(f => Path.Combine(f.Target, f.FileName)).ToArray(),
            BackupFolders = backups.Select(b => Path.GetFileName(b.Value)).ToArray(),
        };
    }

    private static bool Rollback(List<string> placed, List<KeyValuePair<string, string>> backups)
    {
        bool ok = true;
        for (int i = placed.Count - 1; i >= 0; i--)
        {
            try
            {
                if (Directory.Exists(placed[i]))
                {
                    Directory.Delete(placed[i], true);
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                ok = false;
            }
        }

        for (int i = backups.Count - 1; i >= 0; i--)
        {
            try
            {
                if (!Directory.Exists(backups[i].Key))
                {
                    Directory.Move(backups[i].Value, backups[i].Key);
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                ok = false;
            }
        }

        return ok;
    }

    private static string BackupPath(string root, string target, string stamp)
    {
        string candidate = Path.Combine(root, target + ".bak-" + stamp);
        for (int i = 2; Directory.Exists(candidate) || File.Exists(candidate); i++)
        {
            candidate = Path.Combine(root, target + ".bak-" + stamp + "-" + i.ToString(CultureInfo.InvariantCulture));
        }

        return candidate;
    }

    private static void EnsureInside(string root, string path)
    {
        string rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(path).StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
        {
            throw new PackFailure(ModelPackError.UnsafeEntry, "Path escapes the staging folder.");
        }
    }

    /// <summary>Copies at most <paramref name="cap"/> bytes; more than that fails with <paramref name="overError"/>.</summary>
    private static long CopyCapped(Stream source, Stream destination, long cap, ModelPackError overError, CancellationToken ct)
    {
        byte[] buffer = new byte[BufferBytes];
        long total = 0;
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            total += read;
            if (total > cap)
            {
                throw new PackFailure(overError, "A file in the pack is larger than declared.");
            }

            destination.Write(buffer, 0, read);
        }

        return total;
    }

    private static string Sha256Hex(string path)
    {
        using FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferBytes);
        return System.Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static void DeleteQuietly(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
        }
    }

    private static PackFailure Invalid(string message)
    {
        return new PackFailure(ModelPackError.ManifestInvalid, message);
    }

    /// <summary>Internal failure carrying the public error code; converted to a result at the public boundary.</summary>
    private sealed class PackFailure : Exception
    {
        public PackFailure(ModelPackError error, string message, string badFile = "")
            : base(message)
        {
            Error = error;
            BadFile = badFile;
        }

        public ModelPackError Error { get; }

        public string BadFile { get; }
    }
}
