using System.Globalization;
using System.Text;
using EasyAI.Desktop.ViewModels;
using EasyLabel.Ai.Onnx.Classify;
using EasyLabel.Core;
using EasyLabel.Core.Hardware;
using EasyLabel.Core.Io;
using EasyLabel.Core.Model;
using EasyLabel.Core.Trace;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace EasyAI.Desktop.Services;

/// <summary>
/// Export of one OK/NG model: copies the <c>.eaicls</c> file and the backbone sidecar (<c>cls-source.json</c>) into the output folder,
/// then writes the trace record next to them (<c>{name}_v{n}.trace.json</c> and <c>.manifest.csv</c>). Blocking; call from a worker.
/// Nothing in the project is changed. An existing model file in the output folder is never overwritten.
/// </summary>
public static class OkNgModelExporter
{
    private static readonly string[] ImageExtensions = { ".jpg", ".jpeg", ".png", ".bmp" };

    private static readonly string[] MetricNames = { "accuracy", "ngRecall", "okFalseRejectRate" };

    private const string TrainingListExtension = ".train.json";

    private const int TrainingListSchema = 1;

    /// <summary>Copies the model and writes its record. Throws IOException / UnauthorizedAccessException / ArgumentException on failure.</summary>
    public static TraceWriteResult Export(
        string modelPath,
        string outputDirectory,
        ProjectPaths paths,
        string? modelsRoot,
        string? nativeFolder,
        string? operatorText,
        string? note)
    {
        ArgumentNullException.ThrowIfNull(paths);
        string modelFile = Path.GetFileName(modelPath);
        string baseName = OkNgModelRules.BaseName(modelFile);
        if (baseName.Length == 0 || !File.Exists(modelPath))
        {
            throw new ArgumentException("Not an OK/NG model file: " + modelPath, nameof(modelPath));
        }

        string dir = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(dir);

        // The model is copied first with overwrite off: a second export of the same version fails instead of replacing the file.
        string copiedModel = Path.Combine(dir, modelFile);
        File.Copy(modelPath, copiedModel, overwrite: false);
        string copiedSidecar = Path.Combine(dir, ClassifyBackbone.SidecarName);
        bool sidecarCopied = false;
        try
        {
            sidecarCopied = CopySidecar(modelsRoot, nativeFolder, dir);

            JObject root = JObject.Parse(File.ReadAllText(modelPath, Encoding.UTF8));
            var settings = new Dictionary<string, string>(StringComparer.Ordinal);
            AddSetting(settings, "backboneSha256", root["backbone"]?["sha256"]);
            AddSetting(settings, "backboneFile", root["backbone"]?["file"]);
            AddSetting(settings, "featureSpec", root["features"]?["spec"]);
            AddSetting(settings, "inputLongSide", root["features"]?["inputLongSide"]);
            AddSetting(settings, "metricsBasis", root["metrics"]?["basis"]);

            var metrics = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (string name in MetricNames)
            {
                JToken? token = root["metrics"]?[name];
                if (token != null && double.TryParse(token.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
                {
                    metrics[name] = value;
                }
            }

            // The classifier's own training list (written beside the model when it was trained); the project scan is the fallback.
            JObject? trainingList = ReadTrainingList(modelPath);
            List<TraceImageInput> images = trainingList == null ? new List<TraceImageInput>() : TrainingImages(trainingList, paths);
            if (images.Count > 0)
            {
                AddSetting(settings, "modelVersion", trainingList?["modelVersion"]);
            }
            else
            {
                images = ReadImages(paths);
            }

            var request = new TraceWriteRequest
            {
                ModelKind = ClassifyRules.KindClassify,
                ModelName = baseName,
                ModelFilePath = modelPath,
                ImageRoot = paths.ImageRoot,
                Images = images,
                Metrics = metrics,
                Settings = settings,
                Hardware = new HardwareProbe().Probe(),
                Operator = operatorText,
                Note = note,
            };

            return TraceWriter.Write(request, dir);
        }
        catch
        {
            // No record was written: take the copies back so a retry is not blocked by an existing file. The caller shows the error.
            TryDeleteFile(copiedModel);
            if (sidecarCopied)
            {
                TryDeleteFile(copiedSidecar);
            }

            throw;
        }
    }

    /// <summary>Copies the backbone sidecar when this folder does not have one yet; true when this call copied it.</summary>
    private static bool CopySidecar(string? modelsRoot, string? nativeFolder, string dir)
    {
        string? backbone = ClassifyBackbone.Locate(modelsRoot, nativeFolder);
        if (backbone == null)
        {
            return false;
        }

        string source = Path.Combine(Path.GetDirectoryName(backbone)!, ClassifyBackbone.SidecarName);
        string target = Path.Combine(dir, ClassifyBackbone.SidecarName);
        // Several models can share one folder: the same sidecar is kept, a different one is not replaced.
        if (File.Exists(source) && !File.Exists(target))
        {
            File.Copy(source, target, overwrite: false);
            return true;
        }

        return false;
    }

    /// <summary>The training list of a model: the model's own name with <c>.train.json</c> in place of <c>.eaicls</c>.</summary>
    public static string TrainingListPath(string modelPath)
    {
        return Path.ChangeExtension(modelPath, TrainingListExtension);
    }

    /// <summary>
    /// Writes the training list beside the saved model: the images the head was fitted on (project-relative path, tag, split), the model
    /// version and the metrics basis. The head is fitted on every marked image, so each image is listed as <c>train</c>.
    /// </summary>
    public static void WriteTrainingList(string modelPath, IEnumerable<ClassifyTrainImage> images, string metricsBasis)
    {
        ArgumentNullException.ThrowIfNull(images);
        var entries = new JArray();
        foreach (ClassifyTrainImage image in images.OrderBy(i => i.RelativePath, StringComparer.Ordinal))
        {
            entries.Add(new JObject
            {
                ["path"] = image.RelativePath.Replace('\\', '/'),
                ["tag"] = image.IsNg ? "ng" : "ok",
                ["split"] = "train",
            });
        }

        string modelName = Path.GetFileName(modelPath);
        OkNgModelRules.TryParse(modelName, out _, out int version);
        var document = new JObject
        {
            ["schema"] = TrainingListSchema,
            ["modelFile"] = modelName,
            ["modelVersion"] = version,
            ["metricsBasis"] = metricsBasis,
            ["images"] = entries,
        };
        AtomicFile.WriteAllText(TrainingListPath(modelPath), document.ToString(Formatting.Indented));
    }

    /// <summary>The training list beside the model; null when it is absent or unreadable.</summary>
    private static JObject? ReadTrainingList(string modelPath)
    {
        string file = TrainingListPath(modelPath);
        if (!File.Exists(file))
        {
            return null;
        }

        try
        {
            return JObject.Parse(File.ReadAllText(file, Encoding.UTF8));
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is Newtonsoft.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>The listed images that still exist under the image root; a missing file is dropped, not a failure.</summary>
    private static List<TraceImageInput> TrainingImages(JObject list, ProjectPaths paths)
    {
        var images = new List<TraceImageInput>();
        JArray? entries = list["images"] as JArray;
        if (entries == null)
        {
            return images;
        }

        foreach (JToken entry in entries)
        {
            string? rel = entry["path"]?.ToString();
            if (string.IsNullOrWhiteSpace(rel))
            {
                continue;
            }

            string full = Path.Combine(paths.ImageRoot, rel.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(full))
            {
                images.Add(new TraceImageInput(full, entry["tag"]?.ToString() ?? string.Empty, entry["split"]?.ToString() ?? string.Empty));
            }
        }

        return images;
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void AddSetting(Dictionary<string, string> settings, string key, JToken? token)
    {
        if (token != null)
        {
            settings[key] = token.ToString();
        }
    }

    /// <summary>Every image of the project with its OK/NG tag and train/val split from its label file (flags are only read).</summary>
    private static List<TraceImageInput> ReadImages(ProjectPaths paths)
    {
        var images = new List<TraceImageInput>();
        var labelMe = new LabelMeIO();
        foreach (string image in EnumerateImages(paths))
        {
            string tag = string.Empty;
            string split = string.Empty;
            string json = Path.ChangeExtension(image, ".json");
            if (File.Exists(json))
            {
                try
                {
                    var flags = labelMe.Read(json).Flags;
                    byte classValue = ImageClassFlags.Read(flags);
                    tag = classValue == ImageClassFlags.Ok ? "ok" : classValue == ImageClassFlags.Ng ? "ng" : string.Empty;
                    byte modeValue = ImageModeFlags.Read(flags);
                    split = modeValue == ImageModeFlags.Train ? "train" : modeValue == ImageModeFlags.Val ? "val" : string.Empty;
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is Newtonsoft.Json.JsonException)
                {
                    // An unreadable label file leaves the image unmarked in the record.
                }
            }

            images.Add(new TraceImageInput(image, tag, split));
        }

        return images;
    }

    private static List<string> EnumerateImages(ProjectPaths paths)
    {
        var found = new List<string>();
        var pending = new Stack<string>();
        pending.Push(paths.ImageRoot);
        while (pending.Count > 0)
        {
            string directory = pending.Pop();
            List<string> subdirectories;
            List<string> files;
            try
            {
                subdirectories = Directory.EnumerateDirectories(directory).ToList();
                files = Directory.EnumerateFiles(directory).ToList();
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                continue;
            }

            foreach (string sub in subdirectories)
            {
                if (!paths.IsSkippedDirectory(sub))
                {
                    pending.Push(sub);
                }
            }

            foreach (string file in files)
            {
                if (ImageExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
                {
                    found.Add(file);
                }
            }
        }

        found.Sort(StringComparer.OrdinalIgnoreCase);
        return found;
    }
}
