import os
os.chdir(r'E:\Code\EasyVision_Unisen - NoService\EasyLabel')
def rw(p, f):
    s = open(p, encoding='utf-8', newline='').read()
    crlf = '\r\n' in s
    s = s.replace('\r\n', '\n')
    s = f(s)
    if crlf:
        s = s.replace('\n', '\r\n')
    open(p, 'w', encoding='utf-8', newline='').write(s)
def rep(s, old, new, cnt=1):
    assert s.count(old) == cnt, (old[:70], s.count(old))
    return s.replace(old, new)

SRC = 'src/EasyLabel.SegAI/'
TST = 'tests/EasyLabel.SegAI.Tests/'

def backend(s):
    s = rep(s, '''    SegAiPredictResult? Predict(Mat bgr, OpenCvSharp.Rect roi);
}''', '''    SegAiPredictResult? Predict(Mat bgr, OpenCvSharp.Rect roi);

    /// <summary>Named-class prediction with threshold 0 (class API); label plane = class id per pixel, score plane 0..255.</summary>
    SegAiMultiPredictResult? PredictMulti(Mat bgr, OpenCvSharp.Rect roi, int classCount);
}''')
    s = rep(s, '''    public SegTrainingOptions Options { get; init; } = new();
}

internal interface ISegAiEvaluator''', '''    public SegTrainingOptions Options { get; init; } = new();

    /// <summary>Named classes of the model under evaluation; null for the two-class NG/OK path.</summary>
    public SegAiClassTable? ClassTable { get; init; }
}

internal interface ISegAiEvaluator''')
    s = rep(s, '''            Options = config.Options''', '''            Options = config.Options''', 0) if False else s
    s = rep(s, '''        public SegAiPredictResult? Predict(Mat bgr, OpenCvSharp.Rect roi) => _engine.Predict(bgr, roi, 0f);
''', '''        public SegAiPredictResult? Predict(Mat bgr, OpenCvSharp.Rect roi) => _engine.Predict(bgr, roi, 0f);

        public SegAiMultiPredictResult? PredictMulti(Mat bgr, OpenCvSharp.Rect roi, int classCount) => _engine.PredictMulti(bgr, roi, 0f, classCount);
''')
    return s
rw(SRC + 'Training/SegAiTrainBackend.cs', backend)

def fake(s):
    return rep(s, '''internal sealed class FakeEvaluator : ISegAiEvaluator
{''', '''internal sealed class FakeEvaluator : ISegAiEvaluator
{
    public SegAiMultiPredictResult? PredictMulti(Mat bgr, OpenCvSharp.Rect roi, int classCount) => null;
''')
rw(TST + 'TrainingFakes.cs', fake)

def ev(s):
    s = rep(s, '''        List<SegAiTrainingFrame> frames, SegTrainingReport report, CancellationToken token, Action<int>? progress)
    {
        var truth''', '''        List<SegAiTrainingFrame> frames, SegTrainingReport report, CancellationToken token, Action<int>? progress, SegAiClassTable? classTable = null)
    {
        int classCount = classTable?.Classes.Count ?? 2;
        var truth''')
    s = rep(s, '''            EffectiveForeground = report.Version == 2 ? EffectiveForegroundOf(path, options, report) : 0f,
        };''', '''            EffectiveForeground = report.Version == 2 ? EffectiveForegroundOf(path, options, report) : 0f,
            ClassTable = classTable,
        };''')
    s = rep(s, '''                for (int c = 1; c <= 2; c++)
                {
                    using var mask = new Mat();
                    Cv2.Compare(labels, c, mask, CmpTypes.EQ);
                    truth.AddRange''', '''                for (int c = 1; c <= classCount; c++)
                {
                    using var mask = new Mat();
                    Cv2.Compare(labels, c, mask, CmpTypes.EQ);
                    truth.AddRange''')
    s = rep(s, '''                SegAiPredictResult? result = null;
                var watch = new Stopwatch();''', '''                SegAiPredictResult? result = null;
                SegAiMultiPredictResult? multi = null;
                var watch = new Stopwatch();''')
    s = rep(s, '''                    if (!warmed)
                    {
                        try { engine.Predict(image, roi); } catch (InvalidOperationException) { }
                        warmed = true;
                    }

                    watch.Start();
                    result = engine.Predict(image, roi);
                    watch.Stop();
                    if (result == null) throw new InvalidDataException("Validation inference failed. Check the selected CPU/Intel OpenVINO GPU runtime.");''', '''                    if (!warmed)
                    {
                        try { if (classTable != null) engine.PredictMulti(image, roi, classCount); else engine.Predict(image, roi); } catch (InvalidOperationException) { }
                        warmed = true;
                    }

                    watch.Start();
                    if (classTable != null) multi = engine.PredictMulti(image, roi, classCount);
                    else result = engine.Predict(image, roi);
                    watch.Stop();
                    if (result == null && multi == null) throw new InvalidDataException("Validation inference failed. Check the selected CPU/Intel OpenVINO GPU runtime.");''')
    s = rep(s, '''if (!ex.Message.StartsWith("No learned", StringComparison.Ordinal) && !ex.Message.StartsWith("NG and OK examples", StringComparison.Ordinal)) throw;''',
            '''if (!ex.Message.StartsWith("No learned", StringComparison.Ordinal) && !ex.Message.StartsWith("NG and OK examples", StringComparison.Ordinal)
                        && !ex.Message.StartsWith("AI Defect multi-class predict failed (-6)", StringComparison.Ordinal)
                        && !ex.Message.StartsWith("AI Defect multi-class predict failed (-7)", StringComparison.Ordinal)) throw;''')
    s = rep(s, '''                if (result != null)
                {
                    report.Device = result.UsedGpu ? (report.Version == 2 ? "Intel GPU / OpenVINO" : "GPU / OpenCL matching") : "CPU";
                    var imagePredictions = new List<EvaluationBox>();
                    for (int c = 1; c <= 2; c++)
                    {
                        byte[]? confidence = c == 1 ? result.Confidence : result.NormalConfidence;
                        if (confidence == null) continue;''', '''                if (result != null || multi != null)
                {
                    if (result != null) report.Device = result.UsedGpu ? (report.Version == 2 ? "Intel GPU / OpenVINO" : "GPU / OpenCL matching") : "CPU";
                    else report.Device = enableGpu ? "GPU requested" : "CPU";
                    var imagePredictions = new List<EvaluationBox>();
                    for (int c = 1; c <= classCount; c++)
                    {
                        byte[]? confidence = multi != null ? ClassConfidence(multi, c) : c == 1 ? result!.Confidence : result!.NormalConfidence;
                        if (confidence == null) continue;''')
    s = rep(s, '''        for (int label = 1; label <= 2; label++)
        {
            List<EvaluationBox> targets''', '''        for (int label = 1; label <= classCount; label++)
        {
            List<EvaluationBox> targets''')
    s = rep(s, '''Label = label == 1 ? "NG" : "OK", Truth''', '''Label = classTable != null ? classTable.Classes[label - 1].Name : label == 1 ? "NG" : "OK", Truth''')
    s = rep(s, '''BuildImageResults(frames, truth, predictions, report.ScorePercent, noModelOutput, out int imageResultsOmitted, imageResultsBudget);''',
            '''BuildImageResults(frames, truth, predictions, report.ScorePercent, noModelOutput, out int imageResultsOmitted, imageResultsBudget, classTable);''')
    s = rep(s, '''    // Evaluate with the same effective foreground''', '''    // Score plane restricted to the pixels the native label plane assigns to class c (0 elsewhere): the per-class confidence map.
    private static byte[] ClassConfidence(SegAiMultiPredictResult multi, int classId)
    {
        var confidence = new byte[multi.Label.Length];
        for (int i = 0; i < confidence.Length; i++) if (multi.Label[i] == classId) confidence[i] = multi.Score[i];
        return confidence;
    }

    // Evaluate with the same effective foreground''')
    s = rep(s, '''double scorePercent, HashSet<int>? noModelOutput, out int omitted, int byteBudget = ImageResultsByteBudget)''',
            '''double scorePercent, HashSet<int>? noModelOutput, out int omitted, int byteBudget = ImageResultsByteBudget, SegAiClassTable? classTable = null)''')
    s = rep(s, '''            for (int label = 1; label <= 2; label++)
            {
                List<EvaluationBox> t = frameTruth''', '''            // Named classes: DEFECT-kind classes add up to the NG columns, GOOD-kind classes to the OK columns.
            for (int label = 1; label <= (classTable?.Classes.Count ?? 2); label++)
            {
                List<EvaluationBox> t = frameTruth''')
    s = rep(s, '''                if (label == 1) { result.NgTruth = t.Count; result.NgTruePositive = tp; result.NgFalsePositive = fp; }
                else { result.OkTruth = t.Count; result.OkTruePositive = tp; result.OkFalsePositive = fp; }''', '''                bool defect = classTable != null ? classTable.Classes[label - 1].Kind == SegAiClassKind.Defect : label == 1;
                if (defect) { result.NgTruth += t.Count; result.NgTruePositive += tp; result.NgFalsePositive += fp; }
                else { result.OkTruth += t.Count; result.OkTruePositive += tp; result.OkFalsePositive += fp; }''')
    return s
rw(SRC + 'Training/SegAiTrainingEvaluation.cs', ev)

def pipe(s):
    s = rep(s, 'EvaluateTrainingModel(job.Backend, inference, staged, evaluation, report, token, progress);', 'EvaluateTrainingModel(job.Backend, inference, staged, evaluation, report, token, progress, job.ClassTable);')
    return s
rw(SRC + 'Training/SegAiTrainingPipeline.cs', pipe)
print('ok')
