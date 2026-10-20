using System.Globalization;
using System.Text.RegularExpressions;
using EasyLabel.Core.Io;
using EasyLabel.SegAI.Engine;
using Newtonsoft.Json;

namespace EasyLabel.SegAI.Model;

/// <summary>One trained model of the project. State is always "TRAINED" in this version; activation is a separate user action.</summary>
public sealed record SegAiRegistryEntry(string File, DateTime CreatedUtc, string State, string ReportSummary, string DatasetFingerprint)
{
    public const string StateTrained = "TRAINED";

    /// <summary>A model file brought in from outside the project (SegAiSession.ImportModel); never activated by the import.</summary>
    public const string StateImported = "IMPORTED";
}

/// <summary>
/// .easyai/models/registry.json (schema 1): the list of trained models and the optional active one. Model files are named
/// v001.segai, v002.segai ... and a name is never reused or overwritten. Every write is atomic. Instance state is guarded by one lock;
/// there is no cross-process locking.
/// </summary>
public sealed class SegAiRegistry
{
    public const string RegistryFileName = "registry.json";
    public const string PendingFolderName = ".pending";

    private static readonly Regex NamePattern = new(@"^v(\d+)\.segai$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private readonly object _gate = new();
    private List<SegAiRegistryEntry> _entries = new();
    private string? _active;

    public SegAiRegistry(string modelsDir)
    {
        if (string.IsNullOrWhiteSpace(modelsDir)) throw new ArgumentException("Models folder is required.", nameof(modelsDir));
        ModelsDir = Path.GetFullPath(modelsDir);
    }

    public string ModelsDir { get; }

    public string RegistryPath => Path.Combine(ModelsDir, RegistryFileName);

    /// <summary>Entries as of the last <see cref="Load"/> or mutation.</summary>
    public IReadOnlyList<SegAiRegistryEntry> Entries
    {
        get { lock (_gate) { return _entries.ToList(); } }
    }

    /// <summary>File name of the active model, or null (never set automatically).</summary>
    public string? Active
    {
        get { lock (_gate) { return _active; } }
    }

    public string PathOf(string file) => Path.Combine(ModelsDir, file);

    /// <summary>Reads registry.json; a missing file is an empty registry. A corrupt or foreign file throws InvalidDataException and leaves the state unchanged.</summary>
    public void Load()
    {
        lock (_gate) { LoadCore(); }
    }

    /// <summary>Writes registry.json atomically from the in-memory state.</summary>
    public void Save()
    {
        lock (_gate) { SaveCore(); }
    }

    /// <summary>Re-reads the file, appends the entry and saves. A duplicate file name is rejected.</summary>
    public void Add(SegAiRegistryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (_gate)
        {
            LoadCore();
            AddCore(entry);
        }
    }

    /// <summary>The next unused vNNN.segai: one more than the highest number in the registry or in the models folder.</summary>
    public string NextFileName()
    {
        lock (_gate) { return NextFileNameCore(); }
    }

    /// <summary>Sets (or clears with null) the active model; the file must be a registered entry.</summary>
    public void SetActive(string? file)
    {
        lock (_gate)
        {
            LoadCore();
            if (file != null && !_entries.Any(e => string.Equals(e.File, file, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("Model is not in the registry: " + file, nameof(file));
            string? previous = _active;
            _active = file == null ? null : _entries.First(e => string.Equals(e.File, file, StringComparison.OrdinalIgnoreCase)).File;
            try { SaveCore(); }
            catch { _active = previous; throw; }
        }
    }

    /// <summary>
    /// Moves a produced model into .pending, validates it (header via <see cref="SegAiModelFile.Inspect"/> and a load into a fresh
    /// CPU inferer), then moves it without overwrite to the next vNNN.segai and registers it. On any failure the pending file is
    /// deleted and the registry is unchanged; the produced file is left where it was only if the first move failed.
    /// </summary>
    public SegAiRegistryEntry StageCandidate(string producedFile, string reportSummary, string datasetFingerprint)
        => StageCandidate(producedFile, reportSummary, datasetFingerprint, NativeLoadCheck);

    /// <summary>Same as the public overload with an explicit load check (returns an error text, or null when the file loads).</summary>
    internal SegAiRegistryEntry StageCandidate(string producedFile, string reportSummary, string datasetFingerprint, Func<string, string?> loadCheck, string state = SegAiRegistryEntry.StateTrained)
    {
        if (string.IsNullOrWhiteSpace(producedFile)) throw new ArgumentException("Produced model path is required.", nameof(producedFile));
        ArgumentNullException.ThrowIfNull(loadCheck);
        lock (_gate)
        {
            LoadCore(); // a corrupt registry fails here, before anything is moved

            string pendingDir = Path.Combine(ModelsDir, PendingFolderName);
            Directory.CreateDirectory(pendingDir);
            string pending = Path.Combine(pendingDir, Guid.NewGuid().ToString("N") + ".segai");
            string? finalPath = null;
            try
            {
                File.Move(producedFile, pending);
                SegAiModelInfo info = SegAiModelFile.Inspect(pending);
                if (info.Problem != null) throw new InvalidDataException("The trained model is not a valid SegAI file: " + info.Problem.En);
                string? error = loadCheck(pending);
                if (error != null) throw new InvalidDataException("The trained model failed to load: " + error);

                for (int attempt = 0; attempt < 1000; attempt++)
                {
                    string name = NextFileNameCore();
                    string candidate = Path.Combine(ModelsDir, name);
                    try
                    {
                        File.Move(pending, candidate); // no overwrite: throws when the name was taken meanwhile
                    }
                    catch (IOException) when (System.IO.File.Exists(candidate))
                    {
                        continue;
                    }

                    finalPath = candidate;
                    var entry = new SegAiRegistryEntry(name, DateTime.UtcNow, state, reportSummary ?? string.Empty, datasetFingerprint ?? string.Empty);
                    AddCore(entry);
                    return entry;
                }

                throw new IOException("Could not find a free model file name.");
            }
            catch
            {
                TryDelete(pending);
                if (finalPath != null) TryDelete(finalPath); // registered nowhere, so removing it keeps registry and folder consistent
                throw;
            }
        }
    }

    /// <summary>
    /// Copies an existing model file (the source is left untouched) into .pending, validates it like <see cref="StageCandidate(string, string, string)"/>,
    /// then places it without overwrite as the next vNNN.segai and registers it with state IMPORTED. Never activates. On failure the
    /// registry and folder are unchanged and InvalidDataException (invalid or unloadable model) or an IO exception is thrown.
    /// </summary>
    internal SegAiRegistryEntry ImportFile(string sourceFile, string reportSummary, Func<string, string?> loadCheck)
    {
        if (string.IsNullOrWhiteSpace(sourceFile)) throw new ArgumentException("Model path is required.", nameof(sourceFile));
        ArgumentNullException.ThrowIfNull(loadCheck);
        lock (_gate)
        {
            LoadCore(); // a corrupt registry fails here, before anything is copied
            string pendingDir = Path.Combine(ModelsDir, PendingFolderName);
            Directory.CreateDirectory(pendingDir);
            string copy = Path.Combine(pendingDir, Guid.NewGuid().ToString("N") + "-import.segai");
            try
            {
                System.IO.File.Copy(sourceFile, copy, false);
                return StageCandidate(copy, reportSummary, string.Empty, loadCheck, SegAiRegistryEntry.StateImported);
            }
            finally
            {
                TryDelete(copy); // StageCandidate moved it on success, so this only cleans a failed attempt
            }
        }
    }

    internal static string? NativeLoadCheck(string path)
    {
        try
        {
            using var probe = new SegAiInferer();
            int rc = probe.Load(path);
            if (rc != 0) return "SEGAI_InferLoad returned " + rc + ". " + probe.LastError;
            try { probe.SetGpu(false); }
            catch (EntryPointNotFoundException) { }
            return null;
        }
        catch (Exception ex) when (ex is InvalidOperationException || ex is DllNotFoundException || ex is EntryPointNotFoundException || ex is BadImageFormatException)
        {
            return ex.Message;
        }
    }

    // ---- locked helpers ----

    private void AddCore(SegAiRegistryEntry entry)
    {
        if (_entries.Any(e => string.Equals(e.File, entry.File, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("A model with this file name is already registered: " + entry.File);
        _entries.Add(entry);
        try { SaveCore(); }
        catch { _entries.RemoveAt(_entries.Count - 1); throw; }
    }

    private string NextFileNameCore()
    {
        int highest = 0;
        foreach (SegAiRegistryEntry entry in _entries) highest = Math.Max(highest, NumberOf(entry.File));
        try
        {
            if (Directory.Exists(ModelsDir))
                foreach (string file in Directory.EnumerateFiles(ModelsDir, "*.segai")) highest = Math.Max(highest, NumberOf(Path.GetFileName(file)));
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
        }

        return "v" + (highest + 1).ToString("D3", CultureInfo.InvariantCulture) + ".segai";
    }

    private static int NumberOf(string fileName)
    {
        Match match = NamePattern.Match(fileName ?? string.Empty);
        return match.Success && int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int n) ? n : 0;
    }

    private void LoadCore()
    {
        if (!System.IO.File.Exists(RegistryPath))
        {
            _entries = new List<SegAiRegistryEntry>();
            _active = null;
            return;
        }

        RegistryDocument? document;
        try
        {
            document = JsonConvert.DeserializeObject<RegistryDocument>(System.IO.File.ReadAllText(RegistryPath));
        }
        catch (Exception ex) when (ex is JsonException || ex is IOException || ex is UnauthorizedAccessException)
        {
            throw new InvalidDataException("The model registry cannot be read: " + RegistryPath + " (" + ex.Message + ")", ex);
        }

        if (document == null || document.Schema != 1 || document.Models == null)
            throw new InvalidDataException("The model registry has an unsupported format: " + RegistryPath);

        var entries = new List<SegAiRegistryEntry>();
        foreach (EntryDocument item in document.Models)
        {
            if (item == null || string.IsNullOrWhiteSpace(item.File) || item.File.IndexOfAny(new[] { '/', '\\' }) >= 0)
                throw new InvalidDataException("The model registry contains an invalid entry: " + RegistryPath);
            DateTime created = DateTime.TryParse(item.CreatedUtc, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime parsed) ? parsed : DateTime.MinValue;
            entries.Add(new SegAiRegistryEntry(item.File, created, item.State ?? SegAiRegistryEntry.StateTrained, item.ReportSummary ?? string.Empty, item.DatasetFingerprint ?? string.Empty));
        }

        if (document.Active != null && !entries.Any(e => string.Equals(e.File, document.Active, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("The model registry names an active model that is not registered: " + document.Active);

        _entries = entries;
        _active = document.Active;
    }

    private void SaveCore()
    {
        var document = new RegistryDocument
        {
            Schema = 1,
            Active = _active,
            Models = _entries.Select(e => new EntryDocument
            {
                File = e.File,
                CreatedUtc = e.CreatedUtc.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture),
                State = e.State,
                ReportSummary = e.ReportSummary,
                DatasetFingerprint = e.DatasetFingerprint,
            }).ToList(),
        };
        Directory.CreateDirectory(ModelsDir);
        AtomicFile.WriteAllText(RegistryPath, JsonConvert.SerializeObject(document, Formatting.Indented));
    }

    private static void TryDelete(string path)
    {
        try { if (System.IO.File.Exists(path)) System.IO.File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed class RegistryDocument
    {
        [JsonProperty("schema")] public int Schema { get; set; }

        [JsonProperty("active", NullValueHandling = NullValueHandling.Include)] public string? Active { get; set; }

        [JsonProperty("models")] public List<EntryDocument>? Models { get; set; }
    }

    private sealed class EntryDocument
    {
        [JsonProperty("file")] public string File { get; set; } = string.Empty;

        [JsonProperty("createdUtc")] public string? CreatedUtc { get; set; }

        [JsonProperty("state")] public string? State { get; set; }

        [JsonProperty("reportSummary")] public string? ReportSummary { get; set; }

        [JsonProperty("datasetFingerprint")] public string? DatasetFingerprint { get; set; }
    }
}
