using EasyLabel.Core;
using EasyLabel.Core.Editing;
using EasyLabel.Core.Io;
using EasyLabel.Core.Migration;
using EasyLabel.Core.Model;
using EasyLabel.SegAI.Convert;
using EasyLabel.SegAI.Data;
using EasyLabel.SegAI.Inspection;
using EasyLabel.SegAI.Model;
using EasyLabel.SegAI.Options;
using EasyLabel.SegAI.Project;
using EasyLabel.SegAI.Training;
using Newtonsoft.Json;
using OpenCvSharp;
using PointF = System.Drawing.PointF;

namespace EasyLabel.SegAI;

/// <summary>Everything the editor needs for one image: the LabelMe document, the EasyLabel sidecar and the holes by shape geometry.</summary>
/// <param name="Holes">Holes keyed by the geometry of the document shapes (<c>holes.TryGet(shape, out var h)</c> is the holesOf lookup).</param>
/// <param name="Stale">The stored regions no longer matched the shapes, so no holes were available.</param>
public sealed record SegAiEditDocument(string Rel, LabelMeDocument Document, EasyLabelSidecar Sidecar, ShapeHoleStore<PointF[][]> Holes, bool Stale);

/// <summary>One image of the project with its Segment AI state.</summary>
/// <param name="Rel">Image path relative to the project root, '/' separated.</param>
/// <param name="Mode">Resolved mode: Ignored when the image has no regions, otherwise Train or Val.</param>
/// <param name="HasLabelMeJson">The LabelMe JSON next to the image exists.</param>
/// <param name="Stale">The regions were derived from LabelMe shapes that no longer match the stored sidecar (holes may be lost).</param>
public sealed record SegAiSampleInfo(string Rel, int Width, int Height, SegTrainingMode Mode, int AnnotationCount, bool HasLabelMeJson, bool Stale, int InstanceCount = 0);

/// <summary>Outcome of <see cref="SegAiSession.ReplaceImage"/>.</summary>
/// <param name="Replaced">The file now holds the new pixels.</param>
/// <param name="SizeChanged">The new frame differs in size from the previous decoded image (false when the old size was unknown).</param>
/// <param name="OldSize">Previous decoded size, (0, 0) when the old file could not be decoded.</param>
/// <param name="OutOfFrameShapes">Number of LabelMe shapes with a point outside the new frame; they are kept, never deleted.</param>
public sealed record SegAiReplaceResult(string Rel, bool Replaced, bool SizeChanged, (int Width, int Height) OldSize, (int Width, int Height) NewSize, int OutOfFrameShapes);

/// <summary>One registry model with its parsed embedded report (null when the model has none or it cannot be read).</summary>
/// <param name="Map50">Report mAP50, or null.</param>
/// <param name="ElapsedText">Training time as h:mm:ss from the report, or null.</param>
public sealed record SegAiCandidateInfo(SegAiRegistryEntry Entry, SegTrainingReport? Report, double? Map50, string? ElapsedText, bool IsActive);

/// <summary>One folder under .easyai/runs. The pipeline writes status.json (and failure.json for a failure); summary/results paths are null unless such files exist.</summary>
/// <param name="TimestampUtc">Parsed from the folder name, or null.</param>
/// <param name="Status">status.json "status" (running, success, failed, cancelled) or null.</param>
/// <param name="Files">File names directly inside the run folder.</param>
public sealed record SegAiRunInfo(string Name, string Folder, DateTime? TimestampUtc, string? Status, IReadOnlyList<string> Files, string? SummaryPath, string? ResultsCsvPath);

/// <summary>Outcome of <see cref="SegAiSession.ImportModel"/>: the new registry entry, or the reason the file was rejected.</summary>
public sealed record SegAiModelImportResult(SegAiRegistryEntry? Entry, SegAiModelProblem? Problem);

public sealed class SegAiImportReport
{
    internal SegAiImportReport(List<string> imported, List<string> skipReasons)
    {
        ImportedImages = imported;
        SkipReasons = skipReasons;
    }

    public int Imported => ImportedImages.Count;

    public int Skipped => SkipReasons.Count;

    /// <summary>Project-relative paths of the images that were added.</summary>
    public IReadOnlyList<string> ImportedImages { get; }

    /// <summary>One "name: reason" line per skipped entry.</summary>
    public IReadOnlyList<string> SkipReasons { get; }
}

public sealed class SegAiExportReport
{
    internal SegAiExportReport(int exported, List<string> skipReasons)
    {
        Exported = exported;
        SkipReasons = skipReasons;
    }

    public int Exported { get; }

    public int Skipped => SkipReasons.Count;

    /// <summary>One "rel: reason" line per image that was not exported.</summary>
    public IReadOnlyList<string> SkipReasons { get; }
}

/// <summary>
/// Segment AI view of one EasyLabel project folder: LabelMe JSON stays pure, SegAI data lives under .easyai.
/// Instance only; every public member runs under one lock, so Predict, LoadModel and edits are serialized.
/// </summary>
public sealed partial class SegAiSession : IDisposable
{
    private static readonly string[] ImageExtensions = { ".jpg", ".jpeg", ".png", ".bmp" };

    private readonly object _gate = new();
    private readonly SegProjectStore _store;

    private readonly bool _instanceHead;
    private SegClassMap _map; // replaced by SaveClassTable under _gate
    private readonly LabelMeIO _labelMe = new();
    private readonly Dictionary<string, SizeStamp> _sizes = new(StringComparer.OrdinalIgnoreCase);
    private readonly ImageMetaCache _imageMeta;
    private SegAiPredictor? _predictor;
    private bool _disposed;
    private readonly SegAiRegistry _registry;
    private readonly CancellationTokenSource _lifetime = new();
    private int _training;
    private int _trainingReserved;
    private readonly object _qualityGate = new();
    private SegInstanceQuality? _quality;
    private readonly object _reportGate = new();
    private readonly Dictionary<string, (long Length, long WriteTicks, SegTrainingReport? Report)> _reportCache = new(StringComparer.OrdinalIgnoreCase);

    private readonly record struct SizeStamp(long Length, long WriteTicks, int Width, int Height);

    private sealed class SampleState
    {
        public List<SegAnnotation> Regions { get; init; } = new();
        public bool Stale { get; init; }
        public bool HasJson { get; init; }
        public LabelMeDocument? Document { get; init; }
        public SegSidecar? Sidecar { get; init; }

        /// <summary>Auto, Train or Val as stored in the train/val flags of the LabelMe JSON (Auto when there is no JSON).</summary>
        public byte Mode { get; init; }
    }

    private SegAiSession(ProjectPaths paths)
    {
        _store = new SegProjectStore(paths);
        Paths = paths;
        // Position projects send circles and oriented boxes as instances; the kind is fixed at creation, a missing key reads as Detect.
        _instanceHead = EasyLabel.Core.Projects.ProjectKindStore.Read(paths) == EasyLabel.Core.Projects.ProjectKind.Position;
        _imageMeta = new ImageMetaCache(paths.CacheDir);
        ProjectRoot = _store.ProjectRoot;
        _map = _store.LoadClassMap();
        _registry = new SegAiRegistry(_store.ModelsDir);
    }

    /// <summary>Seam for unit tests: the native trainer/inferer by default.</summary>
    internal ISegAiTrainBackend TrainBackend { get; set; } = SegAiNativeTrainBackend.Instance;

    /// <summary>The image root (images, LabelMe JSON and classes.txt); the metadata folder is <see cref="Paths"/>.MetaDir.</summary>
    public string ProjectRoot { get; }

    /// <summary>Every path of the project: image root and metadata folder as separate values.</summary>
    public ProjectPaths Paths { get; }

    public SegTrainingOptions Options { get; } = new();

    public SegInferenceSettings Inference { get; } = new();

    /// <summary>True for a Position project: oriented boxes and circles only, trained through the instance head. Fixed at creation.</summary>
    public bool IsInstanceHead => _instanceHead;

    /// <summary>Message of a Position project whose loaded model has no instance head (no mask suggestions are shown for it).</summary>
    public const string InstanceHeadMissingMessage = "This Position model has no instance head. Retrain the Position model to get oriented boxes and circles.";

    /// <summary>Position projects: the instance head option (Performance or Quality) from .easyai/advanced.json, read once and then kept in memory; Quality when unset.</summary>
    public SegInstanceQuality InstanceQuality
    {
        get
        {
            lock (_qualityGate)
            {
                _quality ??= _store.LoadInstanceQuality();
                return _quality.Value;
            }
        }
    }

    /// <summary>Stores the instance head option and the in-memory copy. The auto-label cache key includes it, so results of the other option are not reused.</summary>
    public void SetInstanceQuality(SegInstanceQuality quality)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
        }

        lock (_qualityGate)
        {
            _store.SaveInstanceQuality(quality);
            _quality = quality;
        }
    }

    /// <summary>
    /// True when the loaded model carries the instance head: a Position model, or a Detect model trained in box mode (EAI-POS P9).
    /// Drives the instance suggestion path and the Performance | Quality choice. A polygon-trained model never has it.
    /// </summary>
    public bool InstanceHeadLoaded
    {
        get
        {
            lock (_gate) { return !_disposed && _predictor != null && _predictor.HasInstanceHead; }
        }
    }

    /// <summary>True when suggestions come from the instance head (see <see cref="InstanceHeadLoaded"/>).</summary>
    internal bool UsesInstanceSuggestions => InstanceHeadLoaded;

    /// <summary>True when a Position project has a model loaded that lacks the instance head (see <see cref="AutoLabelService"/>).</summary>
    internal bool NeedsInstanceHead
    {
        get
        {
            lock (_gate) { return !_disposed && EasyLabel.SegAI.AutoLabel.AutoLabelService.MissingInstanceHead(_instanceHead, _predictor != null, _predictor?.HasInstanceHead ?? true); }
        }
    }

    /// <summary>The model loaded by <see cref="LoadModel"/>, or null.</summary>
    public SegAiModelInfo? LoadedModel
    {
        get { lock (_gate) { return _predictor?.Info; } }
    }

    /// <summary>What the open/create call moved from a legacy layout (<c>Needed == false</c> when the project was already current).</summary>
    public ProjectMigrationReport Migration { get; private set; } = new ProjectMigrationReport();

    /// <summary>Opens an existing project (the folder and .easyai/project.json must exist). A legacy layout is migrated first.</summary>
    public static SegAiSession Open(string projectRoot)
    {
        if (string.IsNullOrWhiteSpace(projectRoot)) throw new ArgumentException("Project folder is required.", nameof(projectRoot));
        return Open(ProjectPaths.Legacy(projectRoot));
    }

    /// <summary>Opens an existing project whose image root and metadata folder are given separately (the project file must exist in the metadata folder).</summary>
    public static SegAiSession Open(string imageRoot, string metaDir)
    {
        return Open(new ProjectPaths(imageRoot, metaDir));
    }

    /// <summary>Opens an existing project with the given paths.</summary>
    public static SegAiSession Open(ProjectPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (!Directory.Exists(paths.ImageRoot)) throw new DirectoryNotFoundException("Project folder does not exist: " + paths.ImageRoot);
        ProjectMigrationReport migration = new ProjectMigrationService().MigrateIfNeeded(paths);
        string projectFile = paths.ProjectFile;
        if (!File.Exists(projectFile))
            throw new FileNotFoundException("Not a Segment AI project (missing .easyai/project.json).", projectFile);
        return new SegAiSession(paths) { Migration = migration };
    }

    /// <summary>Creates the folder, the .easyai layout and classes.txt (NG, OK) when missing; safe on an existing project. A legacy layout is migrated first.</summary>
    public static SegAiSession Create(string projectRoot)
    {
        if (string.IsNullOrWhiteSpace(projectRoot)) throw new ArgumentException("Project folder is required.", nameof(projectRoot));
        return Create(ProjectPaths.Legacy(projectRoot));
    }

    /// <summary>Creates the image folder and the metadata layout (in <paramref name="metaDir"/>) and classes.txt when missing.</summary>
    public static SegAiSession Create(string imageRoot, string metaDir)
    {
        return Create(new ProjectPaths(imageRoot, metaDir));
    }

    /// <summary>Creates the image folder, the metadata layout and classes.txt (NG, OK) for the given paths when missing; safe on an existing project.</summary>
    public static SegAiSession Create(ProjectPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        Directory.CreateDirectory(paths.ImageRoot);
        ProjectMigrationReport migration = new ProjectMigrationService().MigrateIfNeeded(paths);
        var session = new SegAiSession(paths) { Migration = migration };
        session._store.EnsureLayout();
        session._map.EnsureClassesFile(paths.ClassesFile);
        return session;
    }

    /// <summary>Snapshot of all images of the project (recursive, dot-folders skipped). Recomputed on every call.</summary>
    public IReadOnlyList<SegAiSampleInfo> Samples
    {
        get
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                var list = new List<SegAiSampleInfo>();
                foreach (string rel in EnumerateImageRels()) list.Add(BuildInfo(rel));
                return list;
            }
        }
    }

    /// <summary>Copies an image into the project root when it is outside the project; an existing different file is never overwritten.</summary>
    public SegAiSampleInfo AddImage(string sourcePath)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (string.IsNullOrWhiteSpace(sourcePath)) throw new ArgumentException("Source path is required.", nameof(sourcePath));
            string source = Path.GetFullPath(sourcePath);
            if (!File.Exists(source)) throw new FileNotFoundException("Image not found.", source);
            string ext = Path.GetExtension(source);
            if (!ImageExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase)) throw new ArgumentException("Unsupported image type: " + ext, nameof(sourcePath));

            string? insideRel = TryRelativeInside(source);
            if (insideRel != null) return BuildInfo(insideRel);

            string stem = Path.GetFileNameWithoutExtension(source);
            string exact = Path.Combine(ProjectRoot, Path.GetFileName(source));
            if (File.Exists(exact) && FilesEqual(source, exact)) return BuildInfo(RelOf(exact));

            string target = CopyUnique(source, stem, ext);
            return BuildInfo(RelOf(target));
        }
    }

    /// <summary>Seam for unit tests: the clock that names captured images.</summary>
    internal Func<DateTime> CaptureClock { get; set; } = () => DateTime.Now;

    /// <summary>
    /// Saves a captured BGR24 frame as a new <c>Cam_yyyyMMdd_HHmmss_fff.bmp</c> in the image root (a numbered suffix is added when the
    /// name exists; nothing is ever overwritten) and returns its project-relative path. Not blocked while training (like AddImage).
    /// </summary>
    /// <param name="bgr">Pixel bytes, 3 bytes per pixel in B, G, R order.</param>
    /// <param name="stride">Bytes per row, at least width * 3.</param>
    public string AddCapturedFrame(byte[] bgr, int width, int height, int stride)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            byte[] bmp;
            using (Mat mat = MatFromBgr(bgr, width, height, stride))
            {
                if (!Cv2.ImEncode(".bmp", mat, out bmp) || bmp.Length == 0) throw new IOException("Could not encode the captured frame.");
            }

            string stem = "Cam_" + CaptureClock().ToString("yyyyMMdd'_'HHmmss'_'fff", System.Globalization.CultureInfo.InvariantCulture);
            string target = WriteUnique(stem, ".bmp", bmp);
            return RelOf(target);
        }
    }

    /// <summary>
    /// Overwrites the pixels of an existing image with a BGR24 frame, keeping the file name and the format chosen by its extension
    /// (BMP, PNG, JPG with quality 95). The new file is written next to the old one and moved over it, so a failure leaves the original
    /// untouched; no backup is kept. LabelMe shapes are never changed (only imageWidth/imageHeight/imageData of the JSON follow the new
    /// pixels); every size/meta cache of the image is dropped. Throws while training is active and when the image does not exist.
    /// </summary>
    public SegAiReplaceResult ReplaceImage(string rel, byte[] bgr, int width, int height, int stride)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (IsTrainingActive) throw new InvalidOperationException(TrainingActiveMessage);
            string normalized = ResolveRel(rel, out string imagePath);
            if (!File.Exists(imagePath)) throw new FileNotFoundException("Image not found.", imagePath);
            string ext = Path.GetExtension(imagePath);
            if (!ImageExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase)) throw new ArgumentException("Unsupported image type: " + ext, nameof(rel));

            GetImageSize(normalized, imagePath, out int oldWidth, out int oldHeight);
            byte[] encoded;
            using (Mat mat = MatFromBgr(bgr, width, height, stride))
            {
                bool ok = string.Equals(ext, ".jpg", StringComparison.OrdinalIgnoreCase) || string.Equals(ext, ".jpeg", StringComparison.OrdinalIgnoreCase)
                    ? Cv2.ImEncode(ext, mat, out encoded, new[] { (int)ImwriteFlags.JpegQuality, 95 })
                    : Cv2.ImEncode(ext, mat, out encoded);
                if (!ok || encoded.Length == 0) throw new IOException("Could not encode the frame as " + ext + ".");
            }

            string directory = Path.GetDirectoryName(imagePath)!;
            string temp = Path.Combine(directory, "." + Path.GetFileName(imagePath) + "." + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                File.WriteAllBytes(temp, encoded);
                File.Move(temp, imagePath, true);
            }
            catch
            {
                TryDelete(temp);
                throw;
            }

            bool unknownOld = oldWidth <= 0 || oldHeight <= 0;
            bool sizeChanged = !unknownOld && (oldWidth != width || oldHeight != height);
            InvalidateImageCaches(normalized, sizeChanged);
            int outside = RefreshJsonAfterReplace(normalized, imagePath, width, height);
            return new SegAiReplaceResult(normalized, true, sizeChanged, (oldWidth, oldHeight), (width, height), outside);
        }
    }

    // Drops everything derived from the old pixels (called under _gate). The sidecar (regions, holes, shapes hash) describes the shapes,
    // which are kept, so it stays valid and is not marked stale; only the label raster, a pure function of regions and image size, is
    // dropped when the size changed and a LabelMe JSON is the source of truth (without a JSON the raster is the labels, so it is kept).
    private void InvalidateImageCaches(string normalizedRel, bool sizeChanged)
    {
        _sizes.Remove(normalizedRel);
        _imageMeta.Remove(normalizedRel);
        if (sizeChanged && File.Exists(JsonPathOf(normalizedRel))) TryDelete(_store.LabelsPath(normalizedRel));
    }

    // Brings imageWidth/imageHeight (and an embedded imageData) of an existing LabelMe JSON in line with the new pixels; shapes untouched.
    // Returns the number of shapes with a point outside the new frame. A JSON that cannot be read or written is skipped (0).
    private int RefreshJsonAfterReplace(string normalizedRel, string imagePath, int width, int height)
    {
        string jsonPath = JsonPathOf(normalizedRel);
        if (!File.Exists(jsonPath)) return 0;
        try
        {
            LabelMeDocument document = _labelMe.Read(jsonPath);
            int outside = 0;
            if (document.Shapes != null)
            {
                foreach (LabelShape shape in document.Shapes)
                {
                    if (shape?.Points != null && shape.Points.Any(p => p != null && p.Length >= 2 && (p[0] < 0 || p[1] < 0 || p[0] > width || p[1] > height))) outside++;
                }
            }

            if (document.ImageWidth != width || document.ImageHeight != height || !string.IsNullOrEmpty(document.ImageData))
            {
                document.ImageWidth = width;
                document.ImageHeight = height;
                _labelMe.WriteKeepingImageData(document, jsonPath, imagePath);
            }

            return outside;
        }
        catch (Exception ex) when (ex is JsonException || ex is IOException || ex is UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private static Mat MatFromBgr(byte[] bgr, int width, int height, int stride)
    {
        ArgumentNullException.ThrowIfNull(bgr);
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width), "Width and height must be positive.");
        int row = checked(width * 3);
        if (stride < row) throw new ArgumentOutOfRangeException(nameof(stride), "Stride is smaller than one row.");
        if (bgr.LongLength < ((long)(height - 1) * stride) + row) throw new ArgumentException("The pixel buffer is smaller than the frame.", nameof(bgr));
        var mat = new Mat(height, width, MatType.CV_8UC3);
        try
        {
            for (int y = 0; y < height; y++)
                System.Runtime.InteropServices.Marshal.Copy(bgr, y * stride, mat.Ptr(y), row);
            return mat;
        }
        catch
        {
            mat.Dispose();
            throw;
        }
    }

    /// <summary>Regions of one image: LabelMe shapes, completed by the sidecar (holes) when its hash still matches, else by labels.png.</summary>
    public IReadOnlyList<SegAnnotation> GetAnnotations(string rel)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            string normalized = ResolveRel(rel, out _);
            return LoadState(normalized, strict: true).Regions;
        }
    }

    /// <summary>
    /// Writes the regions as LabelMe shapes (non-SegAI shapes and flags of an existing document are kept), then the sidecar and
    /// label raster. The image must exist and every region must lie inside it.
    /// </summary>
    public void SetAnnotations(string rel, IReadOnlyList<SegAnnotation> regions)
    {
        ArgumentNullException.ThrowIfNull(regions);
        lock (_gate)
        {
            ThrowIfDisposed();
            string normalized = ResolveRel(rel, out string imagePath);
            if (!File.Exists(imagePath)) throw new FileNotFoundException("Image not found.", imagePath);
            GetImageSize(normalized, imagePath, out int width, out int height);
            SegLabelRaster.ValidateAnnotations(regions, width, height, _map.Count, allowOutside: true);
            SetAnnotationsCore(normalized, regions, width, height, null);
        }
    }

    /// <summary>
    /// Stores Auto, Train or Val for an image as the image-level <c>train</c>/<c>val</c> flags of its LabelMe JSON (Auto removes both; all
    /// other content of the JSON is kept). Ignored is derived from the absence of regions and cannot be stored. Auto on an image without a
    /// JSON writes nothing; Train/Val create a JSON with no shapes.
    /// </summary>
    public void SetMode(string rel, SegTrainingMode mode)
    {
        if (mode != SegTrainingMode.Auto && mode != SegTrainingMode.Train && mode != SegTrainingMode.Val)
            throw new ArgumentOutOfRangeException(nameof(mode), "Only Auto, Train or Val can be stored.");
        lock (_gate)
        {
            ThrowIfDisposed();
            string normalized = ResolveRel(rel, out string imagePath);
            if (!File.Exists(imagePath)) throw new FileNotFoundException("Image not found.", imagePath);
            string jsonPath = JsonPathOf(normalized);
            LabelMeDocument document;
            if (File.Exists(jsonPath))
            {
                try
                {
                    document = _labelMe.Read(jsonPath);
                }
                catch (JsonException ex)
                {
                    throw new InvalidDataException("The LabelMe JSON is not valid: " + jsonPath + " (" + ex.Message + ")", ex);
                }
            }
            else
            {
                if (mode == SegTrainingMode.Auto) return;
                GetImageSize(normalized, imagePath, out int width, out int height);
                document = new LabelMeDocument { ImagePath = Path.GetFileName(normalized), ImageWidth = width, ImageHeight = height };
            }

            document.Flags ??= new Dictionary<string, bool>();
            ImageModeFlags.Apply(document.Flags, (byte)mode);
            _labelMe.WriteKeepingImageData(document, jsonPath, imagePath);
        }
    }

    /// <summary>The class names of the project in class id order (NG then OK for the default two-class project).</summary>
    public IReadOnlyList<string> ClassNames
    {
        get
        {
            lock (_gate)
            {
                var names = new string[_map.Count];
                for (int i = 0; i < names.Length; i++) names[i] = _map.NameOf((byte)(i + 1));
                return names;
            }
        }
    }

    /// <summary>Class table of the project (DEFECT + GOOD for the default NG/OK project).</summary>
    public EasyLabel.SegAI.Model.SegAiClassTable ClassTable
    {
        get { lock (_gate) { return _map.Table; } }
    }

    /// <summary>
    /// Saves an edited class table to .easyai/advanced.json, then reloads the class map and refreshes classes.txt. The project stays
    /// on the NG/OK map until this is called. Class ids are table positions (1..N) and are not remapped: reordering or removing a class
    /// re-points the ids of labels already stored in annotations, so the UI should prefer disabling a class. Throws
    /// <see cref="ArgumentException"/> and writes nothing when the table is invalid.
    /// </summary>
    public void SaveClassTable(SegAiClassTable table)
    {
        ArgumentNullException.ThrowIfNull(table);
        lock (_gate)
        {
            ThrowIfDisposed();
            SegAiModelProblem? problem = SegAiClassTable.Validate(table.Classes) ?? ProjectNameProblem(table.Classes);
            if (problem is not null) throw new ArgumentException("Invalid class table: " + problem.En, nameof(table));

            // A renamed class keeps its id, so its shapes are relabelled first; every JSON is planned before the first write.
            List<PlannedRelabel> relabels = PlanClassRename(table);
            _store.EnsureLayout();
            foreach (PlannedRelabel relabel in relabels)
            {
                AtomicFile.WriteAllText(relabel.JsonPath, relabel.Text);
                if (relabel.Rel is not null && relabel.ShapesHash is not null) _store.SetShapesHash(relabel.Rel, relabel.ShapesHash);
            }
            _store.SaveClassTable(table);
            _map = _store.LoadClassMap(); // a successful write always reads back as a named table
            _map.EnsureClassesFile(Paths.ClassesFile);
        }
    }

    // SegClassMap resolves labels by trimmed name ignoring case, so a name that differs only in case or padding would shadow another class.
    // Runs after SegAiClassTable.Validate, so every name is non-empty here.
    private static SegAiModelProblem? ProjectNameProblem(IReadOnlyList<SegAiClassEntry> classes)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < classes.Count; i++)
        {
            string name = classes[i].Name;
            if (name != name.Trim())
                return new SegAiModelProblem("lớp " + (i + 1) + ": tên có khoảng trắng đầu hoặc cuối", "class " + (i + 1) + ": the name has leading or trailing spaces");
            if (!seen.Add(name))
                return new SegAiModelProblem("lớp " + (i + 1) + ": tên trùng khi không phân biệt hoa thường", "class " + (i + 1) + ": the name duplicates another class ignoring case");
        }
        return null;
    }

    private sealed record PlannedRelabel(string JsonPath, string Text, string? Rel, string? ShapesHash);

    // Caller holds _gate and has not reloaded _map yet, so the old names come from _map. Returns nothing to write when no name changed.
    private List<PlannedRelabel> PlanClassRename(SegAiClassTable table)
    {
        IReadOnlyDictionary<string, string> renames = SegClassRename.RenamesBetween(
            _map.Table.Classes.Select(c => c.Name).ToList(), table.Classes.Select(c => c.Name).ToList());
        var plans = new List<PlannedRelabel>();
        if (renames.Count == 0) return plans;

        var newMap = new SegClassMap(table);
        foreach (string jsonPath in EnumerateAnnotationJsons())
        {
            string text = File.ReadAllText(jsonPath);
            try
            {
                string? relabelled = SegClassRename.RelabelJson(text, renames);
                if (relabelled is null) continue;

                // The sidecar hash covers the labels: it stays valid only when it matched the labels on disk before the rename.
                string? rel = ImageRelOfJson(jsonPath);
                string? hash = null;
                if (rel is not null && _store.ReadSidecar(rel) is { } sidecar)
                {
                    var before = JsonConvert.DeserializeObject<LabelMeDocument>(text) ?? new LabelMeDocument();
                    var after = JsonConvert.DeserializeObject<LabelMeDocument>(relabelled) ?? new LabelMeDocument();
                    if (sidecar.ShapesHash == SegLabelMeConverter.Hash(SegShapes(before)))
                        hash = SegLabelMeConverter.Hash((after.Shapes ?? new List<LabelShape>()).Where(s => s != null && newMap.LabelOf(s.Label) != null).ToList());
                }
                plans.Add(new PlannedRelabel(jsonPath, relabelled, rel, hash));
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException("The LabelMe JSON is not valid: " + jsonPath + " (" + ex.Message + ")", ex);
            }
        }
        return plans;
    }

    /// <summary>Every LabelMe JSON of the project, outside the .easyai metadata folder and the legacy sidecars.</summary>
    private List<string> EnumerateAnnotationJsons()
    {
        return Directory.EnumerateFiles(ProjectRoot, "*.json", SearchOption.AllDirectories)
            .Where(path => !Paths.IsMetadataPath(path))
            .Where(path => !path.EndsWith(ProjectLayout.LegacySidecarSuffix, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    /// <summary>Relative path of the image that owns a LabelMe JSON (same stem, any image extension); null when no image exists.</summary>
    private string? ImageRelOfJson(string jsonPath)
    {
        string stem = Path.ChangeExtension(jsonPath, null);
        foreach (string ext in ImageExtensions)
            if (File.Exists(stem + ext)) return RelOf(stem + ext);
        return null;
    }

    /// <summary>
    /// Loads the LabelMe document, the EasyLabel sidecar and the holes of one image for the editor. Holes are keyed by the geometry
    /// of each document shape; a stale image (sidecar hash mismatch) yields no holes. Without a LabelMe JSON the shapes are built
    /// from the stored regions.
    /// </summary>
    public SegAiEditDocument LoadForEdit(string rel)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            string normalized = ResolveRel(rel, out string imagePath);
            if (!File.Exists(imagePath)) throw new FileNotFoundException("Image not found.", imagePath);
            SampleState state = LoadState(normalized, strict: true);
            GetImageSize(normalized, imagePath, out int width, out int height);

            LabelMeDocument document = state.Document ?? new LabelMeDocument
            {
                ImagePath = Path.GetFileName(normalized),
                ImageWidth = width,
                ImageHeight = height,
                Shapes = SegLabelMeConverter.ToLabelMe(state.Regions, _map),
            };
            document.Shapes ??= new List<LabelShape>();

            // Holes by normalized geometry (the shape of a region), so float rounding of the sidecar cannot cause a miss.
            // A stale image has no cache holes, but shapes whose "easyai" extension still matches their geometry keep theirs.
            var byGeometry = new Dictionary<string, PointF[][]>(StringComparer.Ordinal);
            foreach (SegAnnotation region in state.Regions)
            {
                if (region.Holes == null || region.Holes.Length == 0) continue;
                List<LabelShape> normalizedShape = SegLabelMeConverter.ToLabelMe(new[] { region }, _map);
                if (normalizedShape.Count == 1) byGeometry[ShapeHoleStore<PointF[][]>.KeyOf(normalizedShape[0])] = region.Holes;
            }

            var store = new ShapeHoleStore<PointF[][]>(TransformHoles);
            if (byGeometry.Count > 0)
            {
                foreach (LabelShape shape in SegShapes(document))
                {
                    string? key = NormalizedKey(shape);
                    if (key != null && byGeometry.TryGetValue(key, out PointF[][]? holes)) store.Set(shape, holes);
                }
            }

            EasyLabelSidecar sidecar = new SidecarIO().Read(EasyLabelSidecarPath(normalized));
            return new SegAiEditDocument(normalized, document, sidecar, store, state.Stale);
        }
    }

    /// <summary>
    /// Saves an edited document: validates every region first (nothing is written on failure), then writes the LabelMe JSON, the
    /// .easyai/meta sidecar and the .easyai/cache regions + label raster so the regions, holes and shapes hash stay consistent.
    /// The stored Mode and shapes of other classes are preserved. <paramref name="holesOf"/> is called per SegAI shape of the document.
    /// </summary>
    /// <exception cref="InvalidDataException">A region is invalid or lies outside the image.</exception>
    public void SaveEditedShapes(string rel, LabelMeDocument doc, EasyLabelSidecar sidecar, Func<LabelShape, PointF[][]?> holesOf)
    {
        SaveEditedShapes(rel, doc, sidecar, holesOf, false, null);
    }

    /// <summary>
    /// Same as the four-argument overload. <paramref name="embedImageData"/> stores the image base64 in the LabelMe JSON's
    /// <c>imageData</c>. A non-empty <paramref name="mirrorDirectory"/> additionally writes the same LabelMe JSON to
    /// <c>mirrorDirectory/&lt;rel&gt;.json</c>; the JSON next to the image and the sidecars stay where Segment AI reads them.
    /// </summary>
    public void SaveEditedShapes(
        string rel,
        LabelMeDocument doc,
        EasyLabelSidecar sidecar,
        Func<LabelShape, PointF[][]?> holesOf,
        bool embedImageData,
        string? mirrorDirectory)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(sidecar);
        ArgumentNullException.ThrowIfNull(holesOf);
        lock (_gate)
        {
            ThrowIfDisposed();
            string normalized = ResolveRel(rel, out string imagePath);
            if (!File.Exists(imagePath)) throw new FileNotFoundException("Image not found.", imagePath);
            GetImageSize(normalized, imagePath, out int width, out int height);
            if (width <= 0 || height <= 0) throw new InvalidDataException("The image could not be decoded: " + normalized);

            doc.Shapes ??= new List<LabelShape>();
            doc.Flags ??= new Dictionary<string, bool>();
            List<LabelShape> segShapes = SegShapes(doc);
            var regions = new List<SegAnnotation>();
            foreach (LabelShape shape in segShapes)
            {
                List<SegAnnotation> one = SegLabelMeConverter.FromLabelMe(new[] { shape }, null, _map, out _);
                if (one.Count != 1) continue; // not representable as a region: kept in the document, hash still covers it
                PointF[][]? holes = holesOf(shape);
                one[0].Holes = holes == null ? new PointF[0][] : holes.Select(h => (PointF[])h.Clone()).ToArray();
                SegLabelMeConverter.WriteHoles(shape, one[0].Holes); // survives a deleted cache; absent for shapes without holes
                regions.Add(one[0]);
            }

            SegLabelRaster.ValidateAnnotations(regions, width, height, _map.Count, allowOutside: true); // throws before anything is written; a part cut by the border is kept, the raster paints its visible part

            doc.ImageWidth = width;
            doc.ImageHeight = height;
            if (string.IsNullOrEmpty(doc.ImagePath)) doc.ImagePath = Path.GetFileName(normalized);
            // The stored Train/Val choice wins over the flags of the in-memory document, so a mode set while the editor held an
            // older copy is not reverted by the save. Without a JSON on disk the document's own flags are kept.
            byte mode = StoredMode(normalized) ?? ImageModeFlags.Read(doc.Flags);
            ImageModeFlags.Apply(doc.Flags, mode);

            // JSON first: if a later step fails the hash no longer matches and the regions are re-derived (stale), never lost.
            _labelMe.Write(doc, JsonPathOf(normalized), embedImageData ? imagePath : null);
            new SidecarIO().Write(sidecar, EasyLabelSidecarPath(normalized));
            _store.WriteSample(normalized, width, height, regions, mode, null, segShapes);

            // The mirror copy is written last: an unavailable output folder must never keep the edit away from training.
            if (!string.IsNullOrWhiteSpace(mirrorDirectory))
            {
                string mirrorRoot = Path.GetFullPath(mirrorDirectory);
                string projectRoot = Path.GetFullPath(ProjectRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if ((mirrorRoot + Path.DirectorySeparatorChar).StartsWith(projectRoot, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("The output folder must be outside the project folder: " + mirrorRoot);
                string mirror = Path.Combine(mirrorRoot, Path.ChangeExtension(normalized, ".json").Replace('/', Path.DirectorySeparatorChar));
                _labelMe.Write(doc, mirror, embedImageData ? imagePath : null);
            }
        }
    }

    /// <summary>Existing annotation files of one image: LabelMe JSON, EasyLabel sidecar, Segment AI sidecar and label raster (never the image).</summary>
    public IReadOnlyList<string> AnnotationFiles(string rel)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            string normalized = ResolveRel(rel, out _);
            var candidates = new[]
            {
                JsonPathOf(normalized),
                EasyLabelSidecarPath(normalized),
                _store.SidecarPath(normalized),
                _store.LabelsPath(normalized),
            };
            return candidates.Where(File.Exists).ToList();
        }
    }

    /// <summary>Deletes <see cref="AnnotationFiles"/> of one image (the image itself is never touched) and returns the deleted paths.</summary>
    public IReadOnlyList<string> DeleteAnnotationFiles(string rel)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            IReadOnlyList<string> files = AnnotationFiles(rel);
            foreach (string file in files) File.Delete(file);
            return files;
        }
    }

    private string EasyLabelSidecarPath(string normalizedRel)
    {
        return Paths.MetaSidecarPath(normalizedRel);
    }

    /// <summary>Train/Val mode stored in the LabelMe JSON on disk, or null when there is no readable JSON.</summary>
    private byte? StoredMode(string normalizedRel)
    {
        string jsonPath = JsonPathOf(normalizedRel);
        try
        {
            return File.Exists(jsonPath) ? ImageModeFlags.Read(_labelMe.Read(jsonPath).Flags) : (byte?)null;
        }
        catch (Exception ex) when (ex is JsonException || ex is IOException || ex is UnauthorizedAccessException)
        {
            return null;
        }
    }

    private string? NormalizedKey(LabelShape shape)
    {
        List<SegAnnotation> one = SegLabelMeConverter.FromLabelMe(new[] { shape }, null, _map, out _);
        if (one.Count != 1) return null;
        List<LabelShape> back = SegLabelMeConverter.ToLabelMe(one, _map);
        return back.Count == 1 ? ShapeHoleStore<PointF[][]>.KeyOf(back[0]) : null;
    }

    private static PointF[][] TransformHoles(PointF[][] holes, Func<PointF, PointF> map)
    {
        return holes.Select(h => h.Select(map).ToArray()).ToArray();
    }

    /// <summary>Every edit is written immediately, so there is nothing pending; this only makes sure the .easyai layout exists.</summary>
    public void SaveDataset()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            _store.EnsureLayout();
        }
    }

    // A dataset carries label ids only; ids mean the same classes only when the dataset table equals the project table (names and kinds).
    private void RequireMatchingClasses(SegAiClassTable? dataset)
    {
        // A dataset without a table is the legacy NG/OK dataset: it matches a named table with the same classes (only the enabled flags
        // may differ). Projects without a named table keep accepting it, as before.
        if (dataset == null && !_map.HasNamedTable) return;
        if (SameClasses(dataset ?? SegAiClassTable.Legacy(), _map.Table)) return;
        if (dataset == null) throw new InvalidDataException("The dataset has no class table but the project uses named classes.");
        throw new InvalidDataException("The dataset class table does not match the project classes.");
    }

    /// <summary>Same class count, and per position the same name (ignoring case) and kind; the enabled flags are not compared.</summary>
    internal static bool SameClasses(SegAiClassTable a, SegAiClassTable b)
    {
        bool same = a.Classes.Count == b.Classes.Count;
        for (int i = 0; same && i < b.Classes.Count; i++)
            same = string.Equals(a.Classes[i].Name, b.Classes[i].Name, StringComparison.OrdinalIgnoreCase) && a.Classes[i].Kind == b.Classes[i].Kind;
        return same;
    }

    /// <summary>
    /// Decides how the loaded model runs for the project classes (caller holds _gate and has checked that a model is loaded).
    /// Returns true for <see cref="SegAiPredictor.PredictClasses"/> and false for the NG/OK path (<see cref="SegAiPredictor.Predict"/>).
    /// </summary>
    private bool UseClassPrediction()
    {
        return DecideClassPrediction(_map.Table, _predictor!.ModelClasses);
    }

    /// <summary>
    /// The NG/OK table (both classes enabled) keeps the two-class path with no table check, and a model with more than two classes
    /// is rejected there. Any other project table is predicted per class, and the model must carry the same classes (names, kinds,
    /// order) or a class id would point at another class, so the call is rejected instead. Throws <see cref="InvalidOperationException"/>.
    /// </summary>
    internal static bool DecideClassPrediction(SegAiClassTable projectTable, SegAiClassTable? modelClasses)
    {
        if (IsNgOkTable(projectTable))
        {
            if (modelClasses != null && modelClasses.Classes.Count > 2) throw new InvalidOperationException("The loaded model has " + modelClasses.Classes.Count + " classes, but the project uses NG and OK. Add these classes to the project class table or load a model for the NG/OK project.");
            return false;
        }

        if (modelClasses == null) throw new InvalidOperationException("The loaded model has no class table, but the project uses " + projectTable.Classes.Count + " named classes. Train a model for the project classes.");
        if (!SameClasses(modelClasses, projectTable)) throw new InvalidOperationException("The loaded model's classes do not match the project classes (names or kinds). Load or train a model for this class table.");
        return true;
    }

    /// <summary>True for the NG/OK default table: NG (DEFECT) and OK (GOOD) by position, both enabled.</summary>
    internal static bool IsNgOkTable(SegAiClassTable table)
    {
        return SameClasses(table, SegAiClassTable.Legacy()) && table.Classes.All(c => c.Enabled);
    }

    /// <summary>The class table is part of the cache key of a project that is not NG/OK: a renamed, reordered, retyped or disabled class changes the suggestions.</summary>
    private string ClassCacheSuffix()
    {
        SegAiClassTable table = _map.Table;
        if (IsNgOkTable(table)) return string.Empty;
        return "|" + string.Join("|", table.Classes.Select(c => c.Name + ":" + (int)c.Kind + ":" + (c.Enabled ? "1" : "0")));
    }

    /// <summary>Imports an EasyVision dataset folder (dataset.json) or zip: each entry becomes a PNG in the project root.</summary>
    public SegAiImportReport ImportEasyVisionDataset(string folderOrZip)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (string.IsNullOrWhiteSpace(folderOrZip)) throw new ArgumentException("Dataset path is required.", nameof(folderOrZip));
            SegAiClassTable? datasetClasses;
            IReadOnlyList<EvDatasetEntry> entries = Directory.Exists(folderOrZip)
                ? EasyVisionDataset.ReadFolder(folderOrZip, out datasetClasses)
                : EasyVisionDataset.ReadZip(folderOrZip, out datasetClasses);
            RequireMatchingClasses(datasetClasses);

            var imported = new List<string>();
            var skipped = new List<string>();
            foreach (EvDatasetEntry entry in entries)
            {
                string? created = null;
                bool completed = false;
                try
                {
                    // Position: the instance head has oriented boxes and circles only; a mask region of the dataset is not imported.
                    if (_instanceHead && entry.Regions != null && entry.Regions.Count > 0)
                    {
                        skipped.Add(entry.Name + ": mask regions are not imported into a Position project (oriented boxes and circles only)");
                        continue;
                    }

                    int width, height;
                    using (Mat image = entry.LoadImage())
                    {
                        width = image.Width;
                        height = image.Height;
                    }

                    SegLabelRaster.ValidateAnnotations(entry.Regions, width, height, _map.Count);
                    byte[] bytes = entry.ReadImageBytes();
                    string stem = SafeStem(entry.Name);
                    string? identical = FindIdentical(stem, bytes);
                    if (identical != null)
                    {
                        skipped.Add(entry.Name + ": identical image already in the project (" + identical + ")");
                        continue;
                    }

                    created = WriteUnique(stem, ".png", bytes);
                    string rel = RelOf(created);
                    SetAnnotationsCore(rel, entry.Regions, width, height, SegSample.NormalizeMode(entry.Mode));
                    imported.Add(rel);
                    completed = true;
                }
                catch (Exception ex) when (ex is InvalidDataException || ex is IOException || ex is ArgumentException
                    || ex is UnauthorizedAccessException || ex is OpenCVException || ex is JsonException)
                {
                    skipped.Add(entry.Name + ": " + ex.Message);
                }
                finally
                {
                    if (created != null && !completed)
                    {
                        TryDelete(created);
                        // SetAnnotationsCore may already have written the LabelMe JSON of this new image.
                        TryDelete(JsonPathOf(RelOf(created)));
                    }
                }
            }

            return new SegAiImportReport(imported, skipped);
        }
    }

    /// <summary>
    /// Writes an EasyVision dataset zip from the labelled images (mask rasters are generated from the regions). Images without
    /// regions and images whose regions EasyVision would reject are skipped and listed in the report.
    /// </summary>
    public SegAiExportReport ExportEasyVisionDataset(string zipPath)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            var entries = new List<EvDatasetEntry>();
            var skipped = new List<string>();
            foreach (string rel in EnumerateImageRels())
            {
                try
                {
                    SampleState state = LoadState(rel, strict: true);
                    if (state.Regions.Count == 0)
                    {
                        skipped.Add(rel + ": no regions (ignored)");
                        continue;
                    }

                    string imagePath = ImagePathOf(rel);
                    byte[] bytes = File.ReadAllBytes(imagePath);
                    int width, height;
                    List<SegAnnotation> clippedRegions;
                    using (Mat decoded = Cv2.ImDecode(bytes, ImreadModes.Color))
                    {
                        if (decoded.Empty()) throw new InvalidDataException("The image could not be decoded.");
                        width = decoded.Width;
                        height = decoded.Height;
                        clippedRegions = SegLabelRaster.ClipToImage(state.Regions, width, height); // EasyVision datasets need inside coordinates
                        SegLabelRaster.ValidateAnnotations(clippedRegions, width, height, _map.Count);
                        if (!string.Equals(Path.GetExtension(imagePath), ".png", StringComparison.OrdinalIgnoreCase))
                        {
                            if (!Cv2.ImEncode(".png", decoded, out bytes)) throw new IOException("Could not encode the image.");
                        }
                    }

                    byte mode = state.Mode;
                    entries.Add(new EvDatasetEntry(rel.Replace('/', '_'), null, null, bytes, null, clippedRegions, mode));
                }
                catch (Exception ex) when (ex is InvalidDataException || ex is IOException || ex is ArgumentException
                    || ex is UnauthorizedAccessException || ex is OpenCVException || ex is JsonException)
                {
                    skipped.Add(rel + ": " + ex.Message);
                }
            }

            EasyVisionDataset.WriteZip(zipPath, entries, _map.HasNamedTable ? _map.Table : null);
            return new SegAiExportReport(entries.Count, skipped);
        }
    }

    /// <summary>
    /// Loads a .segai model. A rejected file is returned as an info with <see cref="SegAiModelInfo.Problem"/> set and the
    /// previously loaded model (if any) stays active; the old model is replaced only after the new one loaded.
    /// </summary>
    public SegAiModelInfo LoadModel(string segaiPath)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            SegAiModelInfo info = SegAiModelFile.Inspect(segaiPath);
            if (info.Problem != null) return info;

            SegAiPredictor? loaded = SegAiPredictor.TryLoad(info, Options, Inference, out SegAiModelProblem? problem);
            if (loaded == null) return info with { Problem = problem };

            SegAiPredictor? previous = _predictor;
            _predictor = loaded;
            previous?.Dispose();
            return info;
        }
    }

    /// <summary>Plain EasyVision inspection of a BGR image (null roi = whole image). Throws when no model is loaded.</summary>
    public SegAiResult Predict(Mat bgr, OpenCvSharp.Rect? roi = null)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_predictor == null) throw new InvalidOperationException("No model is loaded. Call LoadModel first.");
            return _predictor.Predict(bgr, roi, Options, Inference);
        }
    }

    /// <summary>Named-class inspection of a BGR image (V2 model with the class API); see <see cref="SegAiResult.PerClass"/>.</summary>
    public SegAiResult PredictClasses(Mat bgr, OpenCvSharp.Rect? roi = null)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_predictor == null) throw new InvalidOperationException("No model is loaded. Call LoadModel first.");
            return _predictor.PredictClasses(bgr, roi, Options, Inference);
        }
    }

    public SegAiResult PredictFile(string rel)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_predictor == null) throw new InvalidOperationException("No model is loaded. Call LoadModel first.");
            string normalized = ResolveRel(rel, out string imagePath);
            if (!File.Exists(imagePath)) throw new FileNotFoundException("Image not found.", imagePath);
            using Mat image = Cv2.ImRead(imagePath, ImreadModes.Color);
            if (image.Empty()) throw new InvalidDataException("The image could not be decoded: " + normalized);
            return _predictor.Predict(image, null, Options, Inference);
        }
    }

    // ---- training and model registry ----

    /// <summary>Trained models of the project (.easyai/models/registry.json), oldest first. Empty when the registry is missing or unreadable.</summary>
    public IReadOnlyList<SegAiRegistryEntry> Models
    {
        get
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                try { _registry.Load(); }
                catch (InvalidDataException) { return new List<SegAiRegistryEntry>(); }
                return _registry.Entries;
            }
        }
    }

    /// <summary>File name of the model the user activated, or null. Training never sets it.</summary>
    public string? ActiveModel
    {
        get
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                try { _registry.Load(); }
                catch (InvalidDataException) { return null; }
                return _registry.Active;
            }
        }
    }

    /// <summary>
    /// The only way a model becomes active: loads a registered model into the inferer, then records it in registry.json (atomic).
    /// A model that does not load throws and leaves both the loaded model and the registry unchanged.
    /// </summary>
    public void SetActiveModel(string file)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (string.IsNullOrWhiteSpace(file)) throw new ArgumentException("Model file name is required.", nameof(file));
            _registry.Load();
            SegAiRegistryEntry? entry = _registry.Entries.FirstOrDefault(e => string.Equals(e.File, file, StringComparison.OrdinalIgnoreCase));
            if (entry == null) throw new ArgumentException("Model is not in the registry: " + file, nameof(file));
            // Persist first: if this throws, the old predictor is still loaded and nothing else changed.
            string? previousActive = _registry.Active;
            _registry.SetActive(entry.File);
            SegAiModelInfo info;
            try { info = LoadModel(_registry.PathOf(entry.File)); }
            catch
            {
                RollBackActive(previousActive);
                throw;
            }

            if (info.Problem != null)
            {
                RollBackActive(previousActive);
                throw new InvalidOperationException("The model cannot be loaded: " + info.Problem.En);
            }
        }
    }

    /// <summary>
    /// Loads a registered model into the session's single inferer for inspection WITHOUT activating it: registry.json and the active model
    /// stay as they are. The previously loaded model is replaced only after the new one loaded; a model that does not load throws and
    /// leaves the loaded model unchanged. Refused while a training run is active. Auto-label and Inspect share the loaded model, so
    /// callers that need the active model (auto-label) must compare <see cref="LoadedModel"/> with <see cref="ActiveModel"/> first.
    /// </summary>
    public SegAiModelInfo LoadCandidate(string file)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (string.IsNullOrWhiteSpace(file)) throw new ArgumentException("Model file name is required.", nameof(file));
            if (IsTrainingActive) throw new InvalidOperationException(InspectTrainingActiveMessage);
            _registry.Load();
            SegAiRegistryEntry? entry = _registry.Entries.FirstOrDefault(e => string.Equals(e.File, file, StringComparison.OrdinalIgnoreCase));
            if (entry == null) throw new ArgumentException("Model is not in the registry: " + file, nameof(file));
            SegAiModelInfo info = LoadModel(_registry.PathOf(entry.File));
            if (info.Problem != null) throw new InvalidOperationException("The model cannot be loaded: " + info.Problem.En);
            return info;
        }
    }

    /// <summary>
    /// Brings a .segai file from outside the project into .easyai/models as the next free vNNN.segai (the source is only read) and
    /// registers it with state IMPORTED. The file is validated (header and a load into a separate probe inferer), so the model
    /// loaded in this session and the active model are never touched, and nothing is activated: use <see cref="SetActiveModel"/>.
    /// A rejected file returns a result with <see cref="SegAiModelImportResult.Problem"/> set and leaves registry and folder unchanged.
    /// </summary>
    public SegAiModelImportResult ImportModel(string path)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Model path is required.", nameof(path));
            string full = Path.GetFullPath(path);
            if (!File.Exists(full))
                return new SegAiModelImportResult(null, new SegAiModelProblem("không tìm thấy file model: " + full, "model file not found: " + full));

            try
            {
                SegAiModelInfo info = SegAiModelFile.Inspect(full);
                if (info.Problem != null) return new SegAiModelImportResult(null, info.Problem);

                SegAiRegistryEntry entry = _registry.ImportFile(full, "Imported from " + Path.GetFileName(full), TrainBackend.CheckLoad);
                return new SegAiModelImportResult(entry, null);
            }
            catch (InvalidDataException ex)
            {
                return new SegAiModelImportResult(null, new SegAiModelProblem("model không hợp lệ: " + ex.Message, "the model is not valid: " + ex.Message));
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return new SegAiModelImportResult(null, new SegAiModelProblem("không thể sao chép model: " + ex.Message, "the model could not be copied: " + ex.Message));
            }
        }
    }

    /// <summary>True while a training run is active or reserved by <see cref="SegAiTrainingService"/>; inference for auto-label is refused meanwhile.</summary>
    public bool IsTrainingActive => Volatile.Read(ref _training) != 0 || Volatile.Read(ref _trainingReserved) != 0;

    /// <summary>Claims the single training slot before the run starts (closes the gap between StartAsync and <see cref="Train"/>).</summary>
    internal bool TryReserveTraining() => Interlocked.CompareExchange(ref _trainingReserved, 1, 0) == 0;

    internal void ReleaseTraining() => Volatile.Write(ref _trainingReserved, 0);

    internal const string TrainingActiveMessage = "Training is running; auto-label is unavailable until it finishes.";

    internal const string InspectTrainingActiveMessage = "Training is running; inspection is unavailable until it finishes.";

    /// <summary>Registry models with their embedded reports, oldest first; never throws for a missing or corrupt report or registry.</summary>
    public IReadOnlyList<SegAiCandidateInfo> ReadCandidates()
    {
        IReadOnlyList<SegAiRegistryEntry> entries;
        string? active;
        lock (_gate)
        {
            ThrowIfDisposed();
            try { _registry.Load(); }
            catch (InvalidDataException) { return new List<SegAiCandidateInfo>(); }
            entries = _registry.Entries;
            active = _registry.Active;
        }

        // File reads happen outside the session lock so a large report cannot block edits or inference.
        var list = new List<SegAiCandidateInfo>(entries.Count);
        foreach (SegAiRegistryEntry entry in entries)
        {
            SegTrainingReport? report = ReadReportCached(_registry.PathOf(entry.File));
            string? elapsed = report != null && report.TotalTrainingSeconds > 0
                ? TimeSpan.FromSeconds(report.TotalTrainingSeconds).ToString(@"h\:mm\:ss", System.Globalization.CultureInfo.InvariantCulture)
                : null;
            list.Add(new SegAiCandidateInfo(entry, report, report?.Map50, elapsed,
                active != null && string.Equals(active, entry.File, StringComparison.OrdinalIgnoreCase)));
        }

        return list;
    }

    /// <summary>Run folders under .easyai/runs, newest first, with the files found in each.</summary>
    public IReadOnlyList<SegAiRunInfo> ReadRuns()
    {
        string runsDir;
        lock (_gate)
        {
            ThrowIfDisposed();
            runsDir = _store.RunsDir;
        }

        var runs = new List<SegAiRunInfo>();
        if (!Directory.Exists(runsDir)) return runs;
        try
        {
            foreach (string folder in Directory.EnumerateDirectories(runsDir))
            {
                string name = Path.GetFileName(folder);
                if (!SegAiRunFolders.IsRunName(name)) continue;
                var files = new List<string>();
                try
                {
                    foreach (string file in Directory.EnumerateFiles(folder)) files.Add(Path.GetFileName(file));
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                }

                files.Sort(StringComparer.OrdinalIgnoreCase);
                DateTime? stamp = DateTime.TryParseExact(name.Substring(0, 19), "yyyyMMdd'T'HHmmssfff'Z'", System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out DateTime parsed) ? parsed : null;
                string summary = Path.Combine(folder, "summary.json");
                string results = Path.Combine(folder, "results.csv");
                runs.Add(new SegAiRunInfo(name, folder, stamp, SegAiRunFolders.ReadStatus(folder), files,
                    files.Contains("summary.json", StringComparer.OrdinalIgnoreCase) ? summary : null,
                    files.Contains("results.csv", StringComparer.OrdinalIgnoreCase) ? results : null));
            }
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
        }

        runs.Sort((a, b) => StringComparer.Ordinal.Compare(b.Name, a.Name));
        return runs;
    }

    private SegTrainingReport? ReadReportCached(string path)
    {
        FileInfo info;
        try
        {
            info = new FileInfo(path);
            if (!info.Exists) return null;
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            return null;
        }

        long length = info.Length, ticks = info.LastWriteTimeUtc.Ticks;
        lock (_reportGate)
        {
            if (_reportCache.TryGetValue(path, out var hit) && hit.Length == length && hit.WriteTicks == ticks) return hit.Report;
        }

        SegTrainingReport? report = null;
        try
        {
            string? json = SegAiModelFile.ReadReportJson(path);
            if (!string.IsNullOrWhiteSpace(json)) report = JsonConvert.DeserializeObject<SegTrainingReport>(json);
        }
        catch (Exception ex) when (ex is JsonException || ex is IOException || ex is UnauthorizedAccessException || ex is InvalidDataException || ex is ArgumentException || ex is FormatException)
        {
            report = null; // missing or corrupt report: the row simply has no metrics
        }

        lock (_reportGate) { _reportCache[path] = (length, ticks, report); }
        return report;
    }

    /// <summary>File name of the loaded model plus a stamp (length and write time) of the image; the auto-label cache key parts.</summary>
    internal (string Model, string Stamp) ProbeForAutoLabel(string rel)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (IsTrainingActive) throw new InvalidOperationException(TrainingActiveMessage);
            if (_predictor == null) throw new InvalidOperationException("No model is loaded. Call LoadModel first.");
            ResolveRel(rel, out string imagePath);
            var info = new FileInfo(imagePath);
            if (!info.Exists) throw new FileNotFoundException("Image not found.", imagePath);
            return (Path.GetFileName(_predictor.Info.Path), info.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + info.LastWriteTimeUtc.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture) + ClassCacheSuffix() + InstanceStampSuffix());
        }
    }

    /// <summary>
    /// PredictFile at an explicit defect threshold (the auto-label floor); the user's <see cref="Inference"/> threshold is restored
    /// before the lock is released. Returns the image size the suggestions must be scaled to.
    /// </summary>
    internal (SegAiResult Result, int Width, int Height) PredictForAutoLabel(string rel, float threshold)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (IsTrainingActive) throw new InvalidOperationException(TrainingActiveMessage);
            if (_predictor == null) throw new InvalidOperationException("No model is loaded. Call LoadModel first.");
            ResolveRel(rel, out string imagePath);
            if (!File.Exists(imagePath)) throw new FileNotFoundException("Image not found.", imagePath);
            using Mat image = Cv2.ImRead(imagePath, ImreadModes.Color);
            if (image.Empty()) throw new InvalidDataException("The image could not be decoded: " + rel);
            bool perClass = UseClassPrediction();
            float saved = Inference.DefectThreshold;
            Inference.DefectThreshold = threshold;
            try
            {
                SegAiResult result = perClass
                    ? _predictor.PredictClasses(image, null, Options, Inference, _map.Table)
                    : _predictor.Predict(image, null, Options, Inference);
                return (result, image.Width, image.Height);
            }
            finally
            {
                Inference.DefectThreshold = saved;
            }
        }
    }

    /// <summary>
    /// Quality part of the auto-label stamp of a Position project or of a Detect model with the instance head (empty otherwise, so the
    /// keys of polygon Detect models do not change).
    /// </summary>
    internal string InstanceStampSuffix()
    {
        return _instanceHead || InstanceHeadLoaded ? "|quality:" + InstanceQuality.ToString() : string.Empty;
    }

    /// <summary>
    /// Instance head prediction of one image for the auto-label floor (Position projects and Detect box mode); the quality is the project
    /// setting. Returns the instances in original image pixels, the image size and the model's output kind (<see cref="SegBoxModeRule"/>).
    /// </summary>
    internal (SegAiInstanceResult[] Instances, int Width, int Height, string OutputShape) PredictInstancesForAutoLabel(string rel, float threshold)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (IsTrainingActive) throw new InvalidOperationException(TrainingActiveMessage);
            if (_predictor == null) throw new InvalidOperationException("No model is loaded. Call LoadModel first.");
            ResolveRel(rel, out string imagePath);
            if (!File.Exists(imagePath)) throw new FileNotFoundException("Image not found.", imagePath);
            using Mat image = Cv2.ImRead(imagePath, ImreadModes.Color);
            if (image.Empty()) throw new InvalidDataException("The image could not be decoded: " + rel);
            Inference.InstanceScoreAnchor = _store.LoadInstanceScoreAnchor();
            SegAiInstanceResult[] instances = _predictor.PredictInstances(image, threshold, InstanceQuality, Options, Inference);
            return (instances, image.Width, image.Height, _predictor.InstanceOutputShape);
        }
    }

    /// <summary>Instance score calibration anchor of the project (0 = off); part of the auto-label cache key because cached scores are calibrated.</summary>
    internal float InstanceScoreAnchorForCache()
    {
        lock (_gate)
        {
            return _disposed ? 0f : _store.LoadInstanceScoreAnchor();
        }
    }

    /// <summary>Length and write time (UTC ticks) of the loaded model file; part of the on-disk result cache key. Empty when there is no model or it cannot be read.</summary>
    internal string ModelFileStampForCache()
    {
        lock (_gate)
        {
            if (_disposed || _predictor == null) return string.Empty;
            try
            {
                var info = new FileInfo(_predictor.Info.Path);
                if (!info.Exists) return string.Empty;
                return info.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + info.LastWriteTimeUtc.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }
    }

    /// <summary>
    /// File name of the loaded model plus a stamp (length and write time) of an image anywhere on disk; the Inspect cache key parts.
    /// Unlike <see cref="ProbeForAutoLabel"/> the image may lie outside the project.
    /// </summary>
    internal (string Model, string Stamp) ProbeForInspect(string fullPath)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (IsTrainingActive) throw new InvalidOperationException(InspectTrainingActiveMessage);
            if (_predictor == null) throw new InvalidOperationException("No model is loaded. Call LoadModel first.");
            var info = new FileInfo(fullPath);
            if (!info.Exists) throw new FileNotFoundException("Image not found.", fullPath);
            return (Path.GetFileName(_predictor.Info.Path), info.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + info.LastWriteTimeUtc.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture) + ClassCacheSuffix());
        }
    }

    /// <summary>
    /// Plain inspection of an image anywhere on disk with the user's <see cref="Inference"/> settings (no threshold override, nothing
    /// is written). Returns the image size the overlay must be scaled to.
    /// </summary>
    internal (SegAiResult Result, int Width, int Height) PredictForInspect(string fullPath)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (IsTrainingActive) throw new InvalidOperationException(InspectTrainingActiveMessage);
            if (_predictor == null) throw new InvalidOperationException("No model is loaded. Call LoadModel first.");
            if (!File.Exists(fullPath)) throw new FileNotFoundException("Image not found.", fullPath);
            using Mat image = Cv2.ImRead(fullPath, ImreadModes.Color);
            if (image.Empty()) throw new InvalidDataException("The image could not be decoded: " + fullPath);
            SegAiResult result = UseClassPrediction()
                ? _predictor.PredictClasses(image, null, Options, Inference, _map.Table)
                : _predictor.Predict(image, null, Options, Inference);
            return (result, image.Width, image.Height);
        }
    }

    private void RollBackActive(string? previousActive)
    {
        try { _registry.SetActive(previousActive); }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is InvalidDataException)
        {
        }
    }

    /// <summary>
    /// Trains a candidate from the labelled images (synchronous; callers use Task.Run). Locking choice: a separate train gate, not the
    /// session lock, so Predict, LoadModel and edits stay available on the current model for the whole run; a second concurrent
    /// Train returns Success = false immediately. The image list and regions are snapshotted under the session lock at the start.
    /// The candidate lands in .easyai/models as the next vNNN.segai (never overwrites a model, never becomes active) and is
    /// registered with state TRAINED. Cancelling (token or Dispose) before the candidate is committed leaves no model and no registry change.
    /// </summary>
    public SegAiTrainResult Train(SegAiTrainRequest request, IProgress<int>? progress, IProgress<SegEpochMetric>? epochs, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Interlocked.CompareExchange(ref _training, 1, 0) != 0)
            return SegAiTrainResult.BusyResult();
        try
        {
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
            SegAiTrainJob job;
            lock (_gate)
            {
                ThrowIfDisposed();
                job = BuildTrainJob(request, progress, epochs, linked.Token);
            }

            return SegAiTrainingPipeline.Run(job);
        }
        finally
        {
            Volatile.Write(ref _training, 0);
        }
    }

    private SegAiTrainJob BuildTrainJob(SegAiTrainRequest request, IProgress<int>? progress, IProgress<SegEpochMetric>? epochs, CancellationToken token)
    {
        _store.EnsureLayout();
        var samples = new List<SegSample>();
        // Detect box mode (SegBoxModeRule): the mapped shapes of each sample (null without a LabelMe JSON) and the shape counts of the set.
        var sampleShapes = new List<List<LabelShape>?>();
        var sampleSizes = new List<(int Width, int Height)>();
        int polygonRegions = 0, rectangleLabels = 0, orientedLabels = 0;
        foreach (string rel in EnumerateImageRels())
        {
            SampleState state;
            try
            {
                state = LoadState(rel, strict: false);
            }
            catch (Exception ex) when (ex is InvalidDataException || ex is OpenCVException)
            {
                continue;
            }

            // Position: circles join the training mask as discs and every oriented box or circle is an instance; Detect is unchanged.
            var annotations = state.Regions.Select(r => r.Clone()).ToList();
            List<SegInstance>? instances = null;
            List<LabelShape>? shapes = state.Document != null ? SegShapes(state.Document) : null;
            if (_instanceHead && shapes != null)
            {
                // Discs go first: Rasterize paints in list order (later wins), so boxes drawn on a disc (e.g. Dinh inside a Chi_OK/T_Chi pad)
                // must come after it, otherwise the disc erases the box and the class ends with no training pixel.
                annotations.InsertRange(0, SegLabelMeConverter.ToCircleRegions(shapes, _map));
                GetImageSize(rel, ImagePathOf(rel), out int imageW, out int imageH);
                instances = SegLabelMeConverter.ToInstances(shapes, _map, false, imageW, imageH);
            }

            if (!_instanceHead)
            {
                if (shapes == null) polygonRegions += annotations.Count; // raster-only regions are polygons
                else CountShapeKinds(shapes, ref polygonRegions, ref rectangleLabels, ref orientedLabels);
            }

            // An image without regions is Ignored for both Train and Val, so it never reaches training.
            if (annotations.Count == 0) continue;
            samples.Add(new SegSample
            {
                ImagePath = ImagePathOf(rel),
                OriginalName = rel,
                Annotations = annotations,
                Instances = instances,
                TrainValMode = state.Mode,
            });
            sampleShapes.Add(shapes);
            GetImageSize(rel, ImagePathOf(rel), out int sampleW, out int sampleH);
            sampleSizes.Add((sampleW, sampleH));
        }

        // Box mode needs the V2 head (the native instance head is V2 only); a V1 run keeps the mask-only path.
        SegTrainingOptions trainOptions = (request.Options ?? Options).Clone();
        string? boxOutput = null, boxNote = null;
        if (!_instanceHead) boxOutput = SegBoxModeRule.Resolve(polygonRegions, rectangleLabels, orientedLabels, trainOptions.Version, request.Retrain, trainOptions.InputSizeOff, out boxNote);
        if (boxOutput != null)
        {
            for (int i = 0; i < samples.Count; i++)
            {
                if (sampleShapes[i] != null) samples[i].Instances = SegLabelMeConverter.ToInstances(sampleShapes[i]!, _map, true, sampleSizes[i].Width, sampleSizes[i].Height);
            }
        }

        // EAI-POS follow-up: angle symmetry of the instance head (Position and box mode). Only the oriented boxes of the training set count;
        // the project override in advanced.json "angleSymmetry" replaces the rule when it is 1, 2 or 4.
        bool instanceRun = _instanceHead || boxOutput != null;
        int angleSymmetry = SegBoxModeRule.FullTurnSymmetry;
        string? angleNote = null;
        if (instanceRun)
        {
            // EAI-POS follow-up: instance epochs = the run's value, else advanced.json "instanceEpochs", else the P7 default (300).
            if (trainOptions.InstanceEpochs == 0) trainOptions.InstanceEpochs = _store.LoadInstanceEpochs() ?? SegTrainingOptions.DefaultInstanceEpochs;
            int? forced = _store.LoadAngleSymmetryOverride();
            angleSymmetry = forced ?? SegBoxModeRule.AngleSymmetry(sampleShapes.Select((s, i) => (s, i)).Where(t => t.s != null).SelectMany(t => SegLabelMeConverter.ToInstances(t.s!, _map, false, sampleSizes[t.i].Width, sampleSizes[t.i].Height)));
            angleNote = SegBoxModeRule.AngleSymmetryNote(angleSymmetry, forced.HasValue);
            // EAI-POS P12: with symmetry 4 the angle is regressed modulo 90 degrees, so every oriented box goes to the canonical frame first.
            if (angleSymmetry == SegBoxModeRule.SquareSymmetry)
            {
                foreach (SegSample sample in samples) sample.Instances = SegBoxModeRule.CanonicaliseForSymmetry(sample.Instances, angleSymmetry);
                angleNote += " Labels canonicalised.";
            }
        }

        string? resume = null;
        if (request.Retrain && !string.IsNullOrWhiteSpace(request.ResumeFromModel))
        {
            string value = request.ResumeFromModel!;
            resume = Path.IsPathRooted(value) ? Path.GetFullPath(value) : Path.GetFullPath(Path.Combine(_store.ModelsDir, value));
        }

        return new SegAiTrainJob
        {
            RunsDir = _store.RunsDir,
            Registry = _registry,
            Backend = TrainBackend,
            Samples = samples,
            Options = trainOptions,
            Inference = SegAiTrainRequest.Snapshot(request.Inference ?? Inference),
            Retrain = request.Retrain,
            ResumeSource = resume,
            ClassTable = _map.HasNamedTable ? _map.Table : null,
            BackbonePath = request.BackbonePathOverride,
            Progress = progress == null ? null : (Action<int>)progress.Report,
            EpochSink = epochs == null ? null : (Action<SegEpochMetric>)epochs.Report,
            InstanceHead = _instanceHead || boxOutput != null,
            BoxOutputShape = boxOutput,
            BoxModeNote = boxNote,
            AngleSymmetry = angleSymmetry,
            AngleSymmetryNote = angleNote,
            Token = token,
        };
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _lifetime.Cancel(); // a running Train stops without committing a candidate
            _predictor?.Dispose();
            _predictor = null;
            _imageMeta.Dispose(); // flushes pending sizes
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    // ---- paths ----

    private string ResolveRel(string rel, out string imagePath)
    {
        string normalized = SegProjectStore.NormalizeRel(rel);
        if (normalized.Split('/').Any(ProjectLayout.IsSkippedFolderName))
            throw new ArgumentException("Image path must not be inside a dot-folder.", nameof(rel));
        imagePath = Path.GetFullPath(Path.Combine(ProjectRoot, normalized.Replace('/', Path.DirectorySeparatorChar)));
        string prefix = ProjectRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!imagePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Image path escapes the project.", nameof(rel));
        if (Paths.IsMetadataPath(imagePath))
            throw new ArgumentException("Image path must not be inside the metadata folder.", nameof(rel));
        return normalized;
    }

    private string ImagePathOf(string normalizedRel)
    {
        return Path.Combine(ProjectRoot, normalizedRel.Replace('/', Path.DirectorySeparatorChar));
    }

    private string RelOf(string fullPath)
    {
        return Path.GetRelativePath(ProjectRoot, fullPath).Replace('\\', '/');
    }

    /// <summary>Relative path when the file is inside the project and not under a dot-folder; otherwise null.</summary>
    private string? TryRelativeInside(string fullPath)
    {
        string prefix = ProjectRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
        if (Paths.IsMetadataPath(fullPath)) return null;
        string rel = RelOf(fullPath);
        return rel.Split('/').Any(ProjectLayout.IsSkippedFolderName) ? null : rel;
    }

    private IEnumerable<string> EnumerateImageRels()
    {
        var found = new List<string>();
        var pending = new Stack<string>();
        pending.Push(ProjectRoot);
        while (pending.Count > 0)
        {
            string directory = pending.Pop();
            IEnumerable<string> subdirectories;
            IEnumerable<string> files;
            try
            {
                subdirectories = Directory.EnumerateDirectories(directory).ToList();
                files = Directory.EnumerateFiles(directory).ToList();
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                continue;
            }

            foreach (string subdirectory in subdirectories)
                if (!Paths.IsSkippedDirectory(subdirectory)) pending.Push(subdirectory);
            foreach (string file in files)
                if (ImageExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase)) found.Add(RelOf(file));
        }

        found.Sort(StringComparer.OrdinalIgnoreCase);
        return found;
    }

    private string JsonPathOf(string normalizedRel)
    {
        return Path.ChangeExtension(ImagePathOf(normalizedRel), ".json");
    }

    // ---- unique file names ----

    private static bool StemTaken(string directory, string stem)
    {
        foreach (string ext in ImageExtensions)
            if (File.Exists(Path.Combine(directory, stem + ext))) return true;
        return File.Exists(Path.Combine(directory, stem + ".json"));
    }

    private static string Candidate(string stem, int attempt)
    {
        return attempt == 0 ? stem : stem + "_" + attempt;
    }

    private string CopyUnique(string source, string stem, string ext)
    {
        for (int attempt = 0; attempt < 10000; attempt++)
        {
            string name = Candidate(stem, attempt);
            if (StemTaken(ProjectRoot, name)) continue;
            string target = Path.Combine(ProjectRoot, name + ext);
            try
            {
                File.Copy(source, target, false);
                return target;
            }
            catch (IOException) when (File.Exists(target))
            {
                // Created concurrently: try the next name.
            }
        }

        throw new IOException("Could not find a free file name for " + stem + ext);
    }

    private string WriteUnique(string stem, string ext, byte[] bytes)
    {
        for (int attempt = 0; attempt < 10000; attempt++)
        {
            string name = Candidate(stem, attempt);
            if (StemTaken(ProjectRoot, name)) continue;
            string target = Path.Combine(ProjectRoot, name + ext);
            try
            {
                using (var stream = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    stream.Write(bytes, 0, bytes.Length);
                return target;
            }
            catch (IOException) when (File.Exists(target))
            {
                // Created concurrently: try the next name.
            }
        }

        throw new IOException("Could not find a free file name for " + stem + ext);
    }

    /// <summary>Project-relative path of a root-level PNG with the same stem (or numbered variant) and identical bytes, else null.</summary>
    private string? FindIdentical(string stem, byte[] bytes)
    {
        for (int attempt = 0; attempt < 10000; attempt++)
        {
            string path = Path.Combine(ProjectRoot, Candidate(stem, attempt) + ".png");
            if (!File.Exists(path))
            {
                if (!StemTaken(ProjectRoot, Candidate(stem, attempt))) return null;
                continue;
            }

            var info = new FileInfo(path);
            if (info.Length == bytes.Length && File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes)) return RelOf(path);
        }

        return null;
    }

    private static readonly System.Text.RegularExpressions.Regex ReservedDeviceNames = new(
        @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(\..*)?$",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    internal static string SafeStem(string? name)
    {
        string stem = Path.GetFileNameWithoutExtension((name ?? "").Replace('\\', '/').Split('/').Last());
        char[] invalid = Path.GetInvalidFileNameChars();
        stem = new string(stem.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim().TrimStart('.');
        if (stem.Length == 0) return "image";
        // Windows reserved device names (CON, NUL.x, COM1 ...) cannot be created as files.
        return ReservedDeviceNames.IsMatch(stem) ? "_" + stem : stem;
    }

    private static bool FilesEqual(string a, string b)
    {
        var left = new FileInfo(a);
        var right = new FileInfo(b);
        if (left.Length != right.Length) return false;
        return File.ReadAllBytes(a).AsSpan().SequenceEqual(File.ReadAllBytes(b));
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    // ---- annotations ----

    private bool IsSegAiShape(LabelShape? shape)
    {
        return shape != null && _map.LabelOf(shape.Label) != null;
    }

    /// <summary>The shapes the sidecar hash covers: shapes whose class is mapped to NG or OK, in document order.</summary>
    private List<LabelShape> SegShapes(LabelMeDocument? document)
    {
        return document?.Shapes == null ? new List<LabelShape>() : document.Shapes.Where(s => IsSegAiShape(s)).ToList();
    }

    /// <summary>Adds the polygon, rectangle and oriented_rectangle shapes of <paramref name="shapes"/> to the box-mode counts (<see cref="SegBoxModeRule"/>).</summary>
    private static void CountShapeKinds(IEnumerable<LabelShape> shapes, ref int polygons, ref int rectangles, ref int oriented)
    {
        foreach (LabelShape shape in shapes)
        {
            string type = shape.ShapeType ?? string.Empty;
            if (type == SegLabelMeConverter.PolygonType) polygons++;
            else if (type == SegLabelMeConverter.RectangleType) rectangles++;
            else if (type == SegLabelMeConverter.RotationType || type == "rotation") oriented++;
        }
    }

    private SampleState LoadState(string normalizedRel, bool strict)
    {
        string jsonPath = JsonPathOf(normalizedRel);
        SegSidecar? sidecar = _store.ReadSidecar(normalizedRel);
        if (File.Exists(jsonPath))
        {
            LabelMeDocument document;
            try
            {
                document = _labelMe.Read(jsonPath);
            }
            catch (Exception ex) when (!strict && (ex is JsonException || ex is IOException || ex is UnauthorizedAccessException))
            {
                return new SampleState { HasJson = true, Sidecar = sidecar };
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException("The LabelMe JSON is not valid: " + jsonPath + " (" + ex.Message + ")", ex);
            }

            document.Shapes ??= new List<LabelShape>();
            List<LabelShape> shapes = SegShapes(document);
            List<SegAnnotation> regions = SegLabelMeConverter.FromLabelMe(shapes, sidecar, _map, out bool stale);
            return new SampleState
            {
                Regions = regions,
                Stale = stale && (sidecar != null || regions.Count > 0),
                HasJson = true,
                Document = document,
                Sidecar = sidecar,
                Mode = ImageModeFlags.Read(document.Flags),
            };
        }

        using Mat? labels = _store.ReadLabels(normalizedRel);
        List<SegAnnotation> fromRaster = labels == null ? new List<SegAnnotation>() : SegLabelRaster.ReadAnnotations(labels, _map.Count);
        return new SampleState { Regions = fromRaster, Sidecar = sidecar };
    }

    private void SetAnnotationsCore(string normalizedRel, IReadOnlyList<SegAnnotation> regions, int width, int height, byte? mode)
    {
        string jsonPath = JsonPathOf(normalizedRel);
        LabelMeDocument document;
        if (File.Exists(jsonPath))
        {
            try
            {
                document = _labelMe.Read(jsonPath);
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException("The existing LabelMe JSON is not valid and was not overwritten: " + jsonPath + " (" + ex.Message + ")", ex);
            }
        }
        else
        {
            document = new LabelMeDocument { ImagePath = Path.GetFileName(normalizedRel) };
        }

        document.Shapes ??= new List<LabelShape>();
        document.Flags ??= new Dictionary<string, bool>();
        List<LabelShape> ours = SegLabelMeConverter.ToLabelMe(regions, _map);
        // Position: circles never become regions, so the circles already in the document are kept after the region shapes.
        List<LabelShape> circles = _instanceHead
            ? document.Shapes.Where(s => s != null && IsSegAiShape(s) && s.ShapeType == "circle").ToList()
            : new List<LabelShape>();
        var shapes = document.Shapes.Where(s => s != null && !IsSegAiShape(s)).ToList();
        shapes.AddRange(ours);
        shapes.AddRange(circles);
        document.Shapes = shapes;
        document.ImageWidth = width;
        document.ImageHeight = height;
        if (string.IsNullOrEmpty(document.ImagePath)) document.ImagePath = Path.GetFileName(normalizedRel);

        if (mode.HasValue) ImageModeFlags.Apply(document.Flags, mode.Value);
        byte storedMode = ImageModeFlags.Read(document.Flags);

        // JSON first: if the next step fails the hash no longer matches and the regions are re-derived (stale), never lost.
        _labelMe.Write(document, jsonPath);
        // The sidecar hash covers the mapped shapes in document order (region shapes, then kept circles), as LoadState recomputes it.
        var hashShapes = new List<LabelShape>(ours);
        hashShapes.AddRange(circles);
        _store.WriteSample(normalizedRel, width, height, regions, storedMode, null, hashShapes);
    }

    private SegAiSampleInfo BuildInfo(string normalizedRel)
    {
        SampleState state;
        try
        {
            state = LoadState(normalizedRel, strict: false);
        }
        catch (Exception ex) when (ex is InvalidDataException || ex is OpenCVException)
        {
            state = new SampleState();
        }

        string imagePath = ImagePathOf(normalizedRel);
        int width = state.Document?.ImageWidth ?? 0;
        int height = state.Document?.ImageHeight ?? 0;
        if (width <= 0 || height <= 0) GetImageSize(normalizedRel, imagePath, out width, out height);

        // Position: circle shapes train as discs and instances, so they count as annotations (BuildTrainJob uses the same count).
        int annotationCount = state.Regions.Count;
        if (_instanceHead && state.Document != null) annotationCount += SegLabelMeConverter.ToCircleRegions(SegShapes(state.Document), _map).Count;
        SegTrainingMode mode = annotationCount == 0
            ? SegTrainingMode.Ignored
            : state.Mode == SegSample.ModeVal ? SegTrainingMode.Val : SegTrainingMode.Train;
        // Position: boxes and circles of the instance head; the Train block rule counts them (SegAiTrainingService.InstanceBlockOf).
        // Detect: the box labels that box mode would train (informational; the Train block never applies to Detect).
        int instanceCount = state.Document != null ? SegLabelMeConverter.ToInstances(SegShapes(state.Document), _map, includeRectangles: !_instanceHead).Count : 0;
        return new SegAiSampleInfo(normalizedRel, width, height, mode, annotationCount, state.HasJson, state.Stale, instanceCount);
    }

    /// <summary>Decoded image size, cached per file length and write time. Zero when the file cannot be decoded.</summary>
    private void GetImageSize(string normalizedRel, string imagePath, out int width, out int height)
    {
        width = 0;
        height = 0;
        FileInfo info;
        try
        {
            info = new FileInfo(imagePath);
            if (!info.Exists) return;
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            return;
        }

        if (_sizes.TryGetValue(normalizedRel, out SizeStamp cached) && cached.Length == info.Length && cached.WriteTicks == info.LastWriteTimeUtc.Ticks)
        {
            width = cached.Width;
            height = cached.Height;
            return;
        }

        bool found = _imageMeta.GetOrDecode(normalizedRel, info.Length, info.LastWriteTimeUtc.Ticks, () =>
        {
            try
            {
                using Mat image = Cv2.ImRead(imagePath, ImreadModes.Color);
                if (image.Empty()) return null;
                return (image.Width, image.Height);
            }
            catch (OpenCVException)
            {
                return null;
            }
        }, out width, out height);
        if (!found)
        {
            width = 0;
            height = 0;
            return;
        }

        _sizes[normalizedRel] = new SizeStamp(info.Length, info.LastWriteTimeUtc.Ticks, width, height);
    }
}
