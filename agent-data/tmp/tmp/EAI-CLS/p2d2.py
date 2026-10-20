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
    s = rep(s, '''    int AddSample(Mat bgr, Mat mask);

    /// <param name="cancelFlag">''', '''    int AddSample(Mat bgr, Mat mask);

    /// <summary>Names the classes of the model to save (V2, after ConfigureV2 and before the first AddSample); 0 = ok.</summary>
    int SetClassTable(SegAiClassTable table);

    /// <param name="cancelFlag">''')
    s = rep(s, '''        public int AddSample(Mat bgr, Mat mask) => _trainer.AddSample(bgr, mask);
''', '''        public int AddSample(Mat bgr, Mat mask) => _trainer.AddSample(bgr, mask);

        public int SetClassTable(SegAiClassTable table) => _trainer.SetClassTable(table);
''')
    if 'using EasyLabel.SegAI.Model;' not in s:
        s = s.replace('using OpenCvSharp;', 'using EasyLabel.SegAI.Model;\nusing OpenCvSharp;', 1)
    return s
rw(SRC + 'Training/SegAiTrainBackend.cs', backend)

def fake(s):
    s = rep(s, '''    public void ConfigureV2(string settingsPath)
    {
        _work = ''', '''    public SegAiClassTable? ClassTable => _backend.ClassTable;

    public int SetClassTable(SegAiClassTable table)
    {
        _backend.ClassTable = table;
        return 0;
    }

    public void ConfigureV2(string settingsPath)
    {
        _work = ''')
    return s
rw(TST + 'TrainingFakes.cs', fake)

def timing(s):
    s = rep(s, '''        public void ConfigureV2(string settingsPath) => _inner.ConfigureV2(settingsPath);
''', '''        public void ConfigureV2(string settingsPath) => _inner.ConfigureV2(settingsPath);

        public int SetClassTable(EasyLabel.SegAI.Model.SegAiClassTable table) => _inner.SetClassTable(table);
''')
    return s
rw(TST + 'SegAiRealDatasetTimingTests.cs', timing)

def pipe(s):
    s = rep(s, 'public int Index, ClassBits, Width, Height;', 'public int Index, Width, Height;\n    // Bit c-1 is set when class id c has pixels (up to 32 classes).\n    public uint ClassBits;')
    s = rep(s, '''    public Action<int>? Progress { get; init; }

    public Action<SegEpochMetric>? EpochSink { get; init; }
''', '''    public Action<int>? Progress { get; init; }

    /// <summary>Named classes of a V2 run (ids 1..N); null keeps the two-class NG/OK behaviour and the version 3 model format.</summary>
    public SegAiClassTable? ClassTable { get; init; }

    public Action<SegEpochMetric>? EpochSink { get; init; }
''')
    s = rep(s, '''            List<SegAiTrainingFrame> frames = PrepareTrainingFrames(ctx.Labelled, token, progress, value => inputDimensions = value, work, true, out string preparedDimensions);''',
'''            int classCount = job.ClassTable?.Classes.Count ?? 2;
            if (options.Version == 1 && job.ClassTable != null) throw new InvalidOperationException("V1 training supports only the NG/OK classes.");
            string[] classNames = job.ClassTable?.Classes.Select(c => c.Name).ToArray() ?? new[] { "NG", "OK" };
            List<SegAiTrainingFrame> frames = PrepareTrainingFrames(ctx.Labelled, token, progress, value => inputDimensions = value, work, true, out string preparedDimensions, classCount);''')
    s = rep(s, 'ValidateValidationSplit(frames, validation);\n            List<SegAiTrainingFrame> trainFrames', 'ValidateValidationSplit(frames, validation, classNames);\n            List<SegAiTrainingFrame> trainFrames')
    s = rep(s, 'int presentClasses = trainFrames.Aggregate(0, (bits, frame) => bits | frame.ClassBits);', 'uint presentClasses = trainFrames.Aggregate(0u, (bits, frame) => bits | frame.ClassBits);')
    s = rep(s, '''throw new InvalidOperationException(options.Version == 2 ? "V2 training requires NG and/or OK labels." : "V1 training requires NG and OK labels.");''',
            '''throw new InvalidOperationException(options.Version == 2 ? "V2 training requires " + string.Join(" and/or ", classNames) + " labels." : "V1 training requires NG and OK labels.");''')
    s = rep(s, '''                    trainer.ConfigureV2(config);
''', '''                    trainer.ConfigureV2(config);
                    if (job.ClassTable != null && trainer.SetClassTable(job.ClassTable) != 0)
                        throw new InvalidOperationException("Cannot set the class table. " + trainer.LastError);
''')
    s = rep(s, '''Action<int>? progress, Action<string>? dimensionsChanged, string? work, bool writeSnapshots, out string inputDimensions)
    {''', '''Action<int>? progress, Action<string>? dimensionsChanged, string? work, bool writeSnapshots, out string inputDimensions, int classCount = 2)
    {''')
    s = rep(s, 'using Mat aligned = LoadOrBuildMask(sample, input.Size());', 'using Mat aligned = LoadOrBuildMask(sample, input.Size(), classCount);')
    s = rep(s, '''            using (var ng = new Mat())
            using (var ok = new Mat())
            {
                Cv2.Compare(aligned, 1, ng, CmpTypes.EQ);
                Cv2.Compare(aligned, 2, ok, CmpTypes.EQ);
                frame.ClassBits = (Cv2.CountNonZero(ng) > 0 ? 1 : 0) | (Cv2.CountNonZero(ok) > 0 ? 2 : 0);
            }
''', '''            frame.ClassBits = ClassBitsOf(aligned, classCount);
''')
    s = rep(s, 'throw new InvalidDataException("No NG/OK labels in image: " + frame.Name);', 'throw new InvalidDataException("No class labels in image: " + frame.Name);')
    s = rep(s, '''    private static Mat LoadOrBuildMask(SegSample sample, OpenCvSharp.Size size)
    {''', '''    /// <summary>Bit c-1 set when class id c (1..classCount) has at least one pixel in the label raster.</summary>
    internal static uint ClassBitsOf(Mat labels, int classCount)
    {
        uint bits = 0;
        for (int c = 1; c <= classCount && c <= 32; c++)
        {
            using var hit = new Mat();
            Cv2.Compare(labels, c, hit, CmpTypes.EQ);
            if (Cv2.CountNonZero(hit) > 0) bits |= 1u << (c - 1);
        }

        return bits;
    }

    private static Mat LoadOrBuildMask(SegSample sample, OpenCvSharp.Size size, int classCount = 2)
    {''')
    s = rep(s, 'try { return SegLabelRaster.Normalize(mask); }', 'try { return SegLabelRaster.Normalize(mask, classCount); }')
    s = rep(s, '''    internal static void ValidateValidationSplit(List<SegAiTrainingFrame> frames, HashSet<int> validation)
    {
        int trainBits = 0, valBits = 0, trainCount = 0;''', '''    internal static void ValidateValidationSplit(List<SegAiTrainingFrame> frames, HashSet<int> validation, IReadOnlyList<string>? classNames = null)
    {
        uint trainBits = 0, valBits = 0;
        int trainCount = 0;''')
    s = rep(s, '''        if (trainCount == 0 || trainBits == 0)
            throw new InvalidOperationException("At least one labelled Train image with NG/OK labels is required (no Train image is left after the Val split).");
        int missing = valBits & ~trainBits;
        if (missing != 0)
        {
            throw new InvalidOperationException("Val images contain " +
                (missing == 3 ? "NG and OK" : (missing & 1) != 0 ? "NG" : "OK") +
                " that no Train image has; move an image with that class to Train or remove it from Val.");
        }''', '''        IReadOnlyList<string> names = classNames ?? new[] { "NG", "OK" };
        if (trainCount == 0 || trainBits == 0)
            throw new InvalidOperationException("At least one labelled Train image with " + string.Join("/", names) + " labels is required (no Train image is left after the Val split).");
        uint missing = valBits & ~trainBits;
        if (missing != 0)
        {
            var missingNames = new List<string>();
            for (int i = 0; i < names.Count && i < 32; i++)
                if ((missing & (1u << i)) != 0) missingNames.Add(names[i]);
            throw new InvalidOperationException("Val images contain " + string.Join(" and ", missingNames) +
                " that no Train image has; move an image with that class to Train or remove it from Val.");
        }''')
    s = rep(s, 'internal static HashSet<int> SelectRehearsal(List<SegAiTrainingFrame> pool, int classBits, int generation)', 'internal static HashSet<int> SelectRehearsal(List<SegAiTrainingFrame> pool, uint classBits, int generation)')
    s = rep(s, '''        foreach (int bit in new[] { 1, 2 })
        {
            if ((classBits & bit) == 0) continue;''', '''        for (int index = 0; index < 32; index++)
        {
            uint bit = 1u << index;
            if ((classBits & bit) == 0) continue;''')
    return s
rw(SRC + 'Training/SegAiTrainingPipeline.cs', pipe)

def sess(s):
    s = rep(s, '''            ResumeSource = resume,
            BackbonePath = request.BackbonePathOverride,''', '''            ResumeSource = resume,
            ClassTable = _map.HasNamedTable ? _map.Table : null,
            BackbonePath = request.BackbonePathOverride,''')
    return s
rw(SRC + 'SegAiSession.cs', sess)

def cm(s):
    s = rep(s, '''    /// <summary>Number of classes (ids 1..Count); 2 for the NG/OK default.</summary>''', '''    /// <summary>True when the map came from a class table (named-class project), false for the NG/OK default and legacy overrides.</summary>
    public bool HasNamedTable { get; private set; }

    /// <summary>Number of classes (ids 1..Count); 2 for the NG/OK default.</summary>''')
    s = rep(s, '''        ArgumentNullException.ThrowIfNull(table);
        int count = table.Classes.Count;''', '''        ArgumentNullException.ThrowIfNull(table);
        HasNamedTable = true;
        int count = table.Classes.Count;''')
    return s
rw(SRC + 'Convert/SegClassMap.cs', cm)

def splits(s):
    s = rep(s, 'ClassBits = i == 7 ? 2 : 1', 'ClassBits = i == 7 ? 2u : 1u')
    return s
rw(TST + 'SegTrainingSplitTests.cs', splits)
print('ok')
