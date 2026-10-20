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
    assert s.count(old) == cnt, (old[:60], s.count(old))
    return s.replace(old, new)

def cm(s):
    s = rep(s, 'using EasyLabel.Core.Io;\n', 'using EasyLabel.Core.Io;\nusing EasyLabel.SegAI.Model;\n')
    s = rep(s, '''    private readonly string[] _names = { "", "NG", "OK" };
''', '''    private string[] _names = { "", "NG", "OK" };
    private SegAiClassKind[] _kinds = { SegAiClassKind.Defect, SegAiClassKind.Defect, SegAiClassKind.Good };

    /// <summary>
    /// Named-class map: class ids are the table positions 1..N. Names are unique ignoring case (the table validator
    /// guarantees it). The two-class default stays on the legacy constructor.
    /// </summary>
    public SegClassMap(SegAiClassTable table)
    {
        ArgumentNullException.ThrowIfNull(table);
        int count = table.Classes.Count;
        _names = new string[count + 1];
        _kinds = new SegAiClassKind[count + 1];
        _names[0] = "";
        for (int i = 0; i < count; i++)
        {
            _names[i + 1] = table.Classes[i].Name;
            _kinds[i + 1] = table.Classes[i].Kind;
            _byName[table.Classes[i].Name] = (byte)(i + 1);
        }
    }

    /// <summary>Number of classes (ids 1..Count); 2 for the NG/OK default.</summary>
    public int Count => _names.Length - 1;

    /// <summary>Class table equivalent of this map (the NG/OK default maps to a DEFECT + GOOD table).</summary>
    public SegAiClassTable Table
    {
        get
        {
            var entries = new SegAiClassEntry[Count];
            for (int i = 1; i <= Count; i++) entries[i - 1] = new SegAiClassEntry(_names[i], _kinds[i], true);
            return new SegAiClassTable(entries);
        }
    }

    /// <summary>Kind of class <paramref name="label"/>, or null when the id is outside 1..Count.</summary>
    public SegAiClassKind? KindOf(byte label) => label >= 1 && label <= Count ? _kinds[label] : null;
''')
    s = rep(s, '''        return label == Ng ? _names[Ng] : label == Ok ? _names[Ok] : "";''', '''        return label >= 1 && label < _names.Length ? _names[label] : "";''')
    s = rep(s, 'foreach (string wanted in new[] { NameOf(Ng), NameOf(Ok) })', 'foreach (string wanted in _names.Skip(1))')
    s = rep(s, 'Appends the NG and OK class names', 'Appends the class names (NG and OK by default)')
    return s
rw('Convert/SegClassMap.cs', cm)

def rs(s):
    s = rep(s, 'public static List<SegAnnotation> ReadAnnotations(Mat labels)', 'public static List<SegAnnotation> ReadAnnotations(Mat labels, int classCount = 2)')
    s = rep(s, 'for (byte label = 1; label <= 2; label++)', 'for (byte label = 1; label <= classCount; label++)')
    s = rep(s, '''            if (label == 1) Cv2.InRange(labels, new Scalar(200), new Scalar(255), binary);
            else Cv2.InRange(labels, new Scalar(100), new Scalar(199), binary);
''', '''            // The 100..255 aliases of the two-class format collide with ids above 2, so they apply only to classCount == 2.
            if (classCount > 2) binary.Create(labels.Size(), MatType.CV_8UC1, Scalar.All(0));
            else if (label == 1) Cv2.InRange(labels, new Scalar(200), new Scalar(255), binary);
            else Cv2.InRange(labels, new Scalar(100), new Scalar(199), binary);
''')
    s = rep(s, 'public static Mat Normalize(Mat raw)', 'public static Mat Normalize(Mat raw, int classCount = 2)')
    s = rep(s, '''                row[x] = value == 1 || value >= 200 ? (byte)1 : value == 2 || value >= 100 ? (byte)2 : (byte)0;''', '''                if (classCount > 2) row[x] = value >= 1 && value <= classCount ? value : (byte)0;
                else row[x] = value == 1 || value >= 200 ? (byte)1 : value == 2 || value >= 100 ? (byte)2 : (byte)0;''')
    s = rep(s, 'public static void ValidateAnnotations(IReadOnlyList<SegAnnotation>? regions, int width, int height)', 'public static void ValidateAnnotations(IReadOnlyList<SegAnnotation>? regions, int width, int height, int classCount = 2)')
    s = rep(s, '(a.Label != 1 && a.Label != 2)', '(a.Label < 1 || a.Label > classCount)')
    s = rep(s, '/// <summary>Extracts NG (1) and OK (2) regions', '/// <summary>Extracts class regions (NG = 1 and OK = 2 by default; ids 1..classCount)')
    return s
rw('Data/SegLabelRaster.cs', rs)

def ss(s):
    s = rep(s, 'SegLabelRaster.ValidateAnnotations(regions, width, height)', 'SegLabelRaster.ValidateAnnotations(regions, width, height, _map.Count)', 2)
    s = rep(s, 'SegLabelRaster.ValidateAnnotations(entry.Regions, width, height)', 'SegLabelRaster.ValidateAnnotations(entry.Regions, width, height, _map.Count)')
    s = rep(s, 'SegLabelRaster.ValidateAnnotations(state.Regions, width, height)', 'SegLabelRaster.ValidateAnnotations(state.Regions, width, height, _map.Count)')
    s = rep(s, 'SegLabelRaster.ReadAnnotations(labels)', 'SegLabelRaster.ReadAnnotations(labels, _map.Count)')
    s = rep(s, '''    /// <summary>The NG and OK class names of the project (index 0 = NG, 1 = OK).</summary>
    public IReadOnlyList<string> ClassNames
    {
        get { return new[] { _map.NameOf(SegClassMap.Ng), _map.NameOf(SegClassMap.Ok) }; }
    }''', '''    /// <summary>The class names of the project in class id order (NG then OK for the default two-class project).</summary>
    public IReadOnlyList<string> ClassNames
    {
        get
        {
            var names = new string[_map.Count];
            for (int i = 0; i < names.Length; i++) names[i] = _map.NameOf((byte)(i + 1));
            return names;
        }
    }

    /// <summary>Class table of the project (DEFECT + GOOD for the default NG/OK project).</summary>
    public EasyLabel.SegAI.Model.SegAiClassTable ClassTable => _map.Table;''')
    return s
rw('SegAiSession.cs', ss)

def ps(s):
    s = rep(s, '''    /// <summary>Reads .easyai/advanced.json key "segClassMap" (class name to 1 or 2); missing or invalid gives the default map.</summary>
    public SegClassMap LoadClassMap()
    {
        string path = Path.Combine(EasyAiDir, "advanced.json");
        try
        {
            if (File.Exists(path) && JObject.Parse(File.ReadAllText(path))["segClassMap"] is JObject map)''', '''    /// <summary>
    /// Reads .easyai/advanced.json: key "segClasses" (array of name, kind, enabled; ids are positions) wins, then the legacy
    /// "segClassMap" (class name to 1 or 2); missing or invalid gives the default NG/OK map.
    /// </summary>
    public SegClassMap LoadClassMap()
    {
        string path = Path.Combine(EasyAiDir, "advanced.json");
        try
        {
            JObject? root = File.Exists(path) ? JObject.Parse(File.ReadAllText(path)) : null;
            if (root?["segClasses"] is JArray array)
            {
                var entries = new List<SegAiClassEntry>();
                foreach (JToken token in array)
                {
                    string name = token["name"]?.Value<string>() ?? "";
                    var kind = token["kind"]?.Value<string>() == "good" ? SegAiClassKind.Good : SegAiClassKind.Defect;
                    entries.Add(new SegAiClassEntry(name, kind, token["enabled"]?.Value<bool>() ?? true));
                }
                if (SegAiClassTable.Validate(entries) is null) return new SegClassMap(new SegAiClassTable(entries));
            }
            if (root?["segClassMap"] is JObject map)''')
    s = rep(s, '''    private static void WriteBytesAtomic(''', '''    /// <summary>Stores the project class table under .easyai/advanced.json "segClasses" (other keys are kept).</summary>
    public void SaveClassTable(SegAiClassTable table)
    {
        ArgumentNullException.ThrowIfNull(table);
        string path = Path.Combine(EasyAiDir, "advanced.json");
        JObject root = File.Exists(path) ? JObject.Parse(File.ReadAllText(path)) : new JObject();
        var array = new JArray();
        foreach (SegAiClassEntry entry in table.Classes)
            array.Add(new JObject { ["name"] = entry.Name, ["kind"] = entry.Kind == SegAiClassKind.Good ? "good" : "defect", ["enabled"] = entry.Enabled });
        root["segClasses"] = array;
        WriteBytesAtomic(path, new System.Text.UTF8Encoding(false).GetBytes(root.ToString()));
    }

    private static void WriteBytesAtomic(''')
    return s
rw('Project/SegProjectStore.cs', ps)
print("ok")
