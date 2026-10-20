using System.Globalization;
using EasyAI.Desktop.Resources;
using EasyLabel.Ai.Onnx.Classify;
using EasyLabel.Core.Classify;
using EasyLabel.Core.Hardware;
using EasyLabel.Core.Model;
using EasyLabel.SegAI.Inspection;
using EasyLabel.SegAI.Native;

namespace EasyAI.Desktop.ViewModels;

/// <summary>OK/NG marks of the Label step: the count line. Classes come from <see cref="ImageItemViewModel.ImageClass"/>.</summary>
public sealed partial class LabelStepViewModel
{
    /// <summary>"OK n · NG n · Not marked n" of the project images, from the class each image holds in memory.</summary>
    public string ClassCountsText
    {
        get
        {
            ClassifyCounts counts = ClassifyRules.Count(_host.Images.Select(i => i.ImageClass));
            return string.Format(
                CultureInfo.CurrentCulture,
                Text("ClassifyCounts", "OK {0} · NG {1} · Not marked {2}"),
                counts.Ok,
                counts.Ng,
                counts.Unmarked);
        }
    }

    /// <summary>Called after an image class changed or the project list was replaced: re-reads the count line.</summary>
    internal void NotifyClassesChanged()
    {
        OnPropertyChanged(nameof(ClassCountsText));
    }

    private Dictionary<string, ImageUncertainty> _classifierUncertainty = new Dictionary<string, ImageUncertainty>(StringComparer.OrdinalIgnoreCase);
    private string? _classifierDir;
    private CancellationTokenSource? _classifierCts;

    /// <summary>
    /// Classifier uncertainty of an image from the last OK/NG pass of the open project (memory only); null when unknown: no model, no pass yet,
    /// or the pass failed.
    /// </summary>
    internal ImageUncertainty? ClassifierUncertaintyOf(string rel)
    {
        if (!string.Equals(_classifierDir, _host.Project?.Paths.ClassifyDir, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return _classifierUncertainty.TryGetValue(rel, out ImageUncertainty value) ? value : null;
    }

    /// <summary>Stops the running OK/NG pass (the list sort was switched off). The cache keeps its last values.</summary>
    internal void CancelClassifierUncertainty()
    {
        _classifierCts?.Cancel();
        _classifierCts = null;
    }

    /// <summary>
    /// Predicts P(NG) of the project images with the newest OK/NG model, on a background task (cancellable; the UI thread only swaps the
    /// result in). The cache is replaced when the pass finishes; no model or a failed run leaves every image unknown. Returns true when the
    /// cache was replaced, so the caller re-sorts the list. Uses the feature cache of the model folder, so it is cheap after training.
    /// </summary>
    internal async Task<bool> RefreshClassifierUncertaintyAsync()
    {
        // Only while "Most doubtful first" is on and the host is idle; otherwise the pass (inference) does not run and the cache is kept.
        if (!_host.Panels.Files.MostDoubtfulFirst || _host.IsBusy)
        {
            return false;
        }

        CancelClassifierUncertainty();
        var cts = new CancellationTokenSource();
        _classifierCts = cts;
        string? dir = _host.Project?.Paths.ClassifyDir;
        (string FullPath, string Rel)[] snapshot = _host.Images.Select(i => (i.FullPath, i.Rel)).ToArray();
        try
        {
            var map = new Dictionary<string, ImageUncertainty>(StringComparer.OrdinalIgnoreCase);
            string? model = await Task.Run(() => NewestClassifyModel(dir), cts.Token);
            if (model != null)
            {
                List<ClassifyImageRef> images = await Task.Run(() => ExistingImages(snapshot), cts.Token);
                if (images.Count > 0)
                {
                    int threads = await Task.Run(ClassifyThreads, cts.Token);
                    string cache = Path.Combine(Path.GetDirectoryName(model)!, "cache");
                    var request = new ClassifyPredictRequest(model, images, _host.ModelsRoot, NativeSegAiLibrary.LoadedFolder, threads, false, cache);
                    ClassifyPredictResult result = await ClassifyService.PredictAsync(request, null, cts.Token);
                    if (result.Status == ClassifyModelStatus.Ok)
                    {
                        foreach (ClassifyPrediction prediction in result.Predictions)
                        {
                            ImageUncertainty? uncertainty = ImageUncertaintyCalculator.FromClassProbability(prediction.PNg);
                            if (uncertainty != null)
                            {
                                map[prediction.RelativePath] = uncertainty.Value;
                            }
                        }
                    }
                }
            }

            return Publish(cts, dir, map);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            _host.LogData("OK/NG uncertainty pass failed: " + ex.Message);
            return Publish(cts, dir, new Dictionary<string, ImageUncertainty>(StringComparer.OrdinalIgnoreCase));
        }
        finally
        {
            if (ReferenceEquals(_classifierCts, cts))
            {
                _classifierCts = null;
            }

            cts.Dispose();
        }
    }

    /// <summary>Swaps in the finished pass when it is still the current one and the project did not change; false otherwise.</summary>
    private bool Publish(CancellationTokenSource cts, string? dir, Dictionary<string, ImageUncertainty> map)
    {
        if (!ReferenceEquals(_classifierCts, cts) || !string.Equals(_host.Project?.Paths.ClassifyDir, dir, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        _classifierUncertainty = map;
        _classifierDir = dir;
        _classifierCts = null;
        return true;
    }

    /// <summary>The OK/NG model file of the folder that was written last (full path); null when the folder has none.</summary>
    private static string? NewestClassifyModel(string? dir)
    {
        if (dir == null || !Directory.Exists(dir))
        {
            return null;
        }

        // One newest version per model name, then the most recently written of those.
        return OkNgModelRules.LatestPerName(Directory.EnumerateFiles(dir).Select(f => Path.GetFileName(f)).ToList())
            .Select(name => Path.Combine(dir, name))
            .OrderByDescending(f => File.GetLastWriteTimeUtc(f))
            .FirstOrDefault();
    }

    /// <summary>The images that still exist on disk (a missing file would fail the whole pass).</summary>
    private static List<ClassifyImageRef> ExistingImages((string FullPath, string Rel)[] snapshot)
    {
        var images = new List<ClassifyImageRef>(snapshot.Length);
        foreach ((string fullPath, string rel) in snapshot)
        {
            if (File.Exists(fullPath))
            {
                images.Add(new ClassifyImageRef(fullPath, rel));
            }
        }

        return images;
    }

    /// <summary>Threads from the hardware recommendation for the classifier (at least one).</summary>
    private static int ClassifyThreads()
    {
        return Math.Max(1, RecommendedSettings.For(new HardwareProbe().Probe(), HardwareTask.ClassifyTrain).Threads);
    }

    /// <summary>Resource text with English fallback (the key is missing from a resource pack).</summary>
    private static string Text(string key, string english)
    {
        string text = Strings.ByName(key);
        return string.Equals(text, key, StringComparison.Ordinal) ? english : text;
    }
}
