using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EasyAI.Desktop.Resources;
using EasyAI.Desktop.Services;
using EasyLabel.Ai.Onnx.Classify;
using EasyLabel.Core.Classify;
using EasyLabel.Core.Hardware;
using EasyLabel.SegAI.Native;
using InfoBarSeverity = Wpf.Ui.Controls.InfoBarSeverity;

namespace EasyAI.Desktop.ViewModels;

/// <summary>The OK/NG classify kind of the Train tab: block rules, fix mapping and the cancellable background run.</summary>
public sealed partial class TrainStepViewModel
{
    /// <summary>Model name stored in the .eaicls file name (vNNN is added by the writer).</summary>
    private const string ClassifyModelName = "okng";

    private CancellationTokenSource? _classifyCts;

    /// <summary>The OK/NG trainer is chosen (not persisted). Set by the kind buttons, or by the Simple step when the marks are OK/NG.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSegmentKind))]
    private bool _isClassifyKind;

    /// <summary>Caption of the OK/NG kind button.</summary>
    public string ClassifyKindText => ClassifyText("KindClassify", "OK/NG");

    /// <summary>The first failing rule of the OK/NG run, from the marks in memory and the installed backbone.</summary>
    public ClassifyBlock CurrentClassifyBlock()
    {
        ClassifyCounts counts = ClassifyRules.Count(_host.Images.Select(i => i.ImageClass));
        bool busy = _host.IsBusy || IsRunning;
        bool backbone = ClassifyBackbone.Locate(_host.ModelsRoot, NativeSegAiLibrary.LoadedFolder) != null;
        return ClassifyRules.Check(_host.HasSession, busy, counts.Ok, counts.Ng, backbone);
    }

    /// <summary>Block text for the Train step (formatted with the minimum per class); null when there is none to show.</summary>
    private string? ClassifyBlockText(ClassifyBlock block)
    {
        if (block.Reason == ClassifyBlockReason.NoSession)
        {
            return Strings.ByName("TrainBlockNoSession");
        }

        string? key = ClassifyRules.ResourceKeyOf(block.Reason);
        if (key == null)
        {
            return null;
        }

        string text = Strings.ByName(key);
        return block.Reason == ClassifyBlockReason.TooFewTagged
            ? string.Format(CultureInfo.CurrentCulture, text, ClassifyRules.MinPerClass)
            : text;
    }

    /// <summary>The fix row of the block: Go to Label for too few or one-class marks, open the model folder for a missing backbone.</summary>
    private static TrainStepRules.TrainFixKind ClassifyFixOf(ClassifyBlock block)
    {
        switch (ClassifyRules.TrainActionOf(block.Reason))
        {
            case ClassifyRules.TrainAction.GoToLabel:
                return TrainStepRules.TrainFixKind.GoToLabel;
            case ClassifyRules.TrainAction.OpenModelFolder:
                return TrainStepRules.TrainFixKind.OpenModelFolder;
            default:
                return TrainStepRules.TrainFixKind.None;
        }
    }

    /// <summary>Called when an image class changed: the block rules and the fix row follow.</summary>
    internal void NotifyClassesChanged()
    {
        RefreshState();
    }

    partial void OnIsClassifyKindChanged(bool value)
    {
        RefreshState();
    }

    /// <summary>Hardware recommendation for the OK/NG run (ClassifyTrain); null while the hardware probe has no result.</summary>
    private RecommendedSettings? ClassifyRecommendation()
    {
        HardwareInfo? info = _hardwareInfo;
        return info == null ? null : RecommendedSettings.For(info, HardwareTask.ClassifyTrain);
    }

    private void OnClassifyProgress(ClassifyProgress progress)
    {
        if (progress.Stage != ClassifyStage.Reading || progress.Total <= 0)
        {
            return;
        }

        ProgressValue = 100.0 * progress.Done / progress.Total;
        ProgressText = string.Format(CultureInfo.CurrentCulture, ClassifyText("ClassifyReading", "Reading images {0}/{1}"), progress.Done, progress.Total);
    }

    private string ClassifyDoneText(ClassifyTrainResult result)
    {
        if (ClassifyRules.DoneKey(_host.IsSimpleMode) == "ClassifyReady")
        {
            return ClassifyText("ClassifyReady", "OK/NG model ready");
        }

        return string.Format(
            CultureInfo.CurrentCulture,
            ClassifyText("ClassifyDone", "OK/NG model ready · {0} correct on {1} check images"),
            result.Metrics.Correct,
            result.Metrics.Count);
    }

    /// <summary>
    /// Trains the OK/NG head in the background (feature extraction, held-out check, save into the project's classify folder).
    /// Stop cancels between images. The UI thread only sets the progress text.
    /// </summary>
    private async Task StartClassifyAsync()
    {
        string? folder = _host.Project?.Paths.ClassifyDir;
        if (folder == null || IsRunning || _host.IsBusy || !CurrentClassifyBlock().CanTrain)
        {
            return;
        }

        List<ClassifyTrainImage> images = _host.Images
            .Where(i => i.HasClass)
            .Select(i => new ClassifyTrainImage(i.FullPath, i.Rel, i.IsNg))
            .ToList();

        RecommendedSettings? recommended = ClassifyRecommendation();
        int threads = recommended?.Threads ?? RecommendedSettings.UnknownHardwareThreads;
        int longSide = recommended?.MaxInputPixels ?? RecommendedSettings.SafeClassifyInputPixels;

        CancellationTokenSource cts = new CancellationTokenSource();
        _classifyCts = cts;
        IsRunning = true;
        ProgressValue = 0;
        ProgressText = string.Empty;
        RefreshState();
        try
        {
            // Progress<T> captures the UI context here, so OnClassifyProgress runs on the UI thread.
            var progress = new Progress<ClassifyProgress>(OnClassifyProgress);
            var request = new ClassifyTrainRequest(images, folder, ClassifyModelName, _host.ModelsRoot, NativeSegAiLibrary.LoadedFolder, longSide, threads, false);
            ClassifyTrainResult result = await ClassifyService.TrainAsync(request, progress, cts.Token);
            SaveClassifyTrainingList(result, images);
            ProgressValue = 100;
            ProgressText = ClassifyDoneText(result);
            // The new model re-scores the images for the Label list; it runs in the background and does not hold the Train run.
            _ = RefreshDoubtfulOrderAfterTrainAsync();
        }
        catch (OperationCanceledException)
        {
            ProgressText = string.Empty;
        }
        catch (Exception ex)
        {
            _host.LogData("OK/NG training failed: " + ex.Message);
            _host.ShowDataNotice(Strings.TrainNoticeTitle, ex.Message, InfoBarSeverity.Warning);
        }
        finally
        {
            _classifyCts = null;
            cts.Dispose();
            IsRunning = false;
            RefreshState();
        }
    }

    /// <summary>After a new OK/NG model: the classifier pass of the Label list, then the list is sorted again (no-op when the sort is off).</summary>
    private async Task RefreshDoubtfulOrderAfterTrainAsync()
    {
        if (!_host.Panels.Files.MostDoubtfulFirst || _host.IsBusy)
        {
            return;
        }

        LabelStepViewModel? label = _host.Label;
        if (label != null && await label.RefreshClassifierUncertaintyAsync())
        {
            _host.Panels.Files.RefreshOrder();
        }
    }

    /// <summary>
    /// Keeps the images the new model was fitted on (tags and metrics basis) beside the .eaicls file, so the trace record can name them.
    /// A write failure is logged and does not fail the run; the exporter then falls back to the project scan.
    /// </summary>
    private void SaveClassifyTrainingList(ClassifyTrainResult result, IReadOnlyList<ClassifyTrainImage> images)
    {
        try
        {
            OkNgModelExporter.WriteTrainingList(result.ModelPath, images, result.MetricsBasis);
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            _host.LogData("OK/NG training list not saved: " + ex.Message);
        }
    }

    /// <summary>Text with English fallback for keys missing from the resource pack.</summary>
    private static string ClassifyText(string key, string english)
    {
        string text = Strings.ByName(key);
        return string.Equals(text, key, StringComparison.Ordinal) ? english : text;
    }
}
