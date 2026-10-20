import os
os.chdir(r'E:\Code\EasyVision_Unisen - NoService\EasyLabel\src\EasyLabel.SegAI')
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

def res(s):
    s = rep(s, '''    public bool IsOK { get; init; }
''', '''    /// <summary>
    /// Per-class planes of a named-class prediction (<see cref="SegAiPredictor.PredictClasses"/>), in class id order; null for the
    /// plain NG/OK path. For such a result <see cref="Mask"/> is the union of the DEFECT planes and <see cref="NormalMask"/> the union of the GOOD planes.
    /// </summary>
    public List<SegAiClassPlane>? PerClass { get; init; }

    public bool IsOK { get; init; }
''')
    s += '''
/// <summary>One class of a named-class prediction.</summary>
public sealed class SegAiClassPlane
{
    /// <summary>Class id 1..N (position in the class table).</summary>
    public int ClassId { get; init; }

    public string Name { get; init; } = string.Empty;

    public SegAiClassKind Kind { get; init; }

    /// <summary>Accepted pixels of this class / ROI area (native outClassFraction).</summary>
    public float Fraction { get; init; }

    /// <summary>Width*Height bytes, 0 or 255.</summary>
    public byte[] Mask { get; init; } = Array.Empty<byte>();

    public List<DefectRegionMetrics> Regions { get; init; } = new();

    /// <summary>One score (0..1) per entry of <see cref="Regions"/>, read from the native score plane at the region seed pixel.</summary>
    public float[] RegionScores { get; init; } = Array.Empty<float>();
}
'''
    s = s.replace('namespace EasyLabel.SegAI.Inspection;\n', 'using EasyLabel.SegAI.Model;\n\nnamespace EasyLabel.SegAI.Inspection;\n', 1)
    return s
rw('Inspection/SegAiResult.cs', res)

def pred(s):
    s = rep(s, '''    private static SegAiResult Failed(string error, long started)''', '''    /// <summary>
    /// Named-class prediction (V2 model, class API): one plane per enabled class. A class is "found" when it has a region of at least
    /// the effective minimum area; the image is OK when no DEFECT class is found. Errors are returned in <see cref="SegAiResult.Error"/>.
    /// </summary>
    public SegAiResult PredictClasses(Mat? input, OpenCvSharp.Rect? roiOrNull, SegTrainingOptions options, SegInferenceSettings settings)
    {
        long started = Stopwatch.GetTimestamp();
        try
        {
            if (!IsV2) return Failed("Named-class prediction needs a V2 model.", started);
            if (!_inferer.IsOpened || input == null || input.IsDisposed || input.Empty()) return Failed("Model or inspection image is unavailable.", started);
            SegAiClassTable? table = _inferer.GetClassTable();
            if (table == null) return Failed("The model has no readable class table (BeeNativeSegAI.dll without the class API?).", started);
            float threshold = settings.DefectThreshold;
            OpenCvSharp.Rect roi = roiOrNull ?? new OpenCvSharp.Rect(0, 0, input.Width, input.Height);
            if (roi.Width <= 0 || roi.Height <= 0 || roi.X < 0 || roi.Y < 0 ||
                (long)roi.X + roi.Width > input.Width || (long)roi.Y + roi.Height > input.Height ||
                threshold < 0 || threshold > 1 || float.IsNaN(threshold))
                return Failed("Invalid inspection region or acceptance limits.", started);

            using Mat bgr = AcquireInferenceInput(input);
            ConfigureInferer(options, settings);
            SegAiMultiPredictResult native = _inferer.PredictMulti(bgr, roi, threshold, table.Classes.Count);
            int effectiveMinimum = Math.Max(Math.Max(1, settings.MinDefectArea), Math.Max(1, Info.TrainedMinimumArea ?? 1));
            var planes = new List<SegAiClassPlane>();
            var defectUnion = new byte[native.Label.Length];
            var goodUnion = new byte[native.Label.Length];
            bool anyDefect = false;
            float maxDefectScore = 0;
            for (int i = 0; i < table.Classes.Count; i++)
            {
                SegAiClassEntry entry = table.Classes[i];
                if (!entry.Enabled) continue;
                byte id = (byte)(i + 1);
                var plane = new byte[native.Label.Length];
                for (int p = 0; p < plane.Length; p++) if (native.Label[p] == id) plane[p] = 255;
                DefectRegionAnalysis analysis;
                using (var mask = Mat.FromPixelData(native.Height, native.Width, MatType.CV_8UC1, plane))
                    analysis = DefectRegionAnalyzer.Analyze(mask, null, effectiveMinimum);
                var scores = new float[analysis.Regions.Count];
                for (int r = 0; r < scores.Length; r++)
                {
                    System.Drawing.Point seed = analysis.Regions[r].SeedPixel;
                    scores[r] = seed.X >= 0 && seed.Y >= 0 && seed.X < native.Width && seed.Y < native.Height
                        ? native.Score[seed.Y * native.Width + seed.X] / 255f : 0f;
                }

                // Only components that survive the area filter belong to the plane (the analysis already removed the small ones).
                if (analysis.Count == 0) Array.Clear(plane, 0, plane.Length);
                planes.Add(new SegAiClassPlane
                {
                    ClassId = id, Name = entry.Name, Kind = entry.Kind, Fraction = native.ClassFractions[i],
                    Mask = plane, Regions = analysis.Regions.ToList(), RegionScores = scores,
                });
                byte[] union = entry.Kind == SegAiClassKind.Good ? goodUnion : defectUnion;
                for (int p = 0; p < plane.Length; p++) if (plane[p] != 0) union[p] = 255;
                if (entry.Kind == SegAiClassKind.Defect && analysis.Count > 0)
                {
                    anyDefect = true;
                    foreach (float score in scores) maxDefectScore = Math.Max(maxDefectScore, score);
                }
            }

            return new SegAiResult
            {
                Mask = defectUnion,
                NormalMask = goodUnion,
                Width = native.Width,
                Height = native.Height,
                Score = maxDefectScore,
                Regions = planes.Where(p => p.Kind == SegAiClassKind.Defect).SelectMany(p => p.Regions).ToList(),
                NormalRegions = planes.Where(p => p.Kind == SegAiClassKind.Good).SelectMany(p => p.Regions).ToList(),
                PerClass = planes,
                IsOK = !anyDefect,
                ElapsedMs = ElapsedSince(started),
                RawMask = (byte[])native.Label.Clone(),
            };
        }
        catch (Exception ex)
        {
            return Failed(ex.GetBaseException().Message, started);
        }
    }

    private static SegAiResult Failed(string error, long started)''')
    return s
rw('Inspection/SegAiPredictor.cs', pred)

def sess(s):
    s = rep(s, '''    public SegAiResult PredictFile(string rel)''', '''    /// <summary>Named-class inspection of a BGR image (V2 model with the class API); see <see cref="SegAiResult.PerClass"/>.</summary>
    public SegAiResult PredictClasses(Mat bgr, OpenCvSharp.Rect? roi = null)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_predictor == null) throw new InvalidOperationException("No model is loaded. Call LoadModel first.");
            return _predictor.PredictClasses(bgr, roi, Options, Inference);
        }
    }

    public SegAiResult PredictFile(string rel)''')
    return s
rw('SegAiSession.cs', sess)

def sb(s):
    s = rep(s, '''    private static void AddClass(List<SegAiSuggestion> output''', '''    /// <summary>
    /// Suggestions of a named-class result: one group per enabled class plane, <c>ClassIndex</c> = class id - 1 (the index into the
    /// project class names). Same polygon rules as <see cref="Build"/>.
    /// </summary>
    public static IReadOnlyList<SegAiSuggestion> BuildClasses(string rel, SegAiResult result, int imageWidth, int imageHeight, int minDefectArea, int maxPointsPerShape = DefaultMaxPoints)
    {
        ArgumentNullException.ThrowIfNull(rel);
        ArgumentNullException.ThrowIfNull(result);
        if (imageWidth <= 0 || imageHeight <= 0) throw new ArgumentOutOfRangeException(nameof(imageWidth), "Image size must be positive.");
        if (maxPointsPerShape < 3) throw new ArgumentOutOfRangeException(nameof(maxPointsPerShape));
        var list = new List<SegAiSuggestion>();
        if (result.Error != null || result.PerClass == null) return list;
        foreach (SegAiClassPlane plane in result.PerClass)
            AddClass(list, rel, plane.ClassId - 1, plane.Mask, result.Width, result.Height, plane.Regions, plane.RegionScores, imageWidth, imageHeight, minDefectArea, maxPointsPerShape);
        return list;
    }

    private static void AddClass(List<SegAiSuggestion> output''')
    return s
rw('AutoLabel/SegAiSuggestionBuilder.cs', sb)
print('ok')
