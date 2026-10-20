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

def ds(s):
    if 'using EasyLabel.SegAI.Model;' not in s:
        s = s.replace('using Newtonsoft.Json;', 'using EasyLabel.SegAI.Model;\nusing Newtonsoft.Json;', 1)
    s = rep(s, '''    private sealed class DatasetFile
    {
        public int Version = 1;''', '''    private sealed class DatasetClass
    {
        public string? Name, Kind;
        public bool Enabled = true;
    }

    private sealed class DatasetFile
    {
        public int Version = 1;
        // Additive named-class table (class id = position + 1); absent => the two-class NG/OK dataset. Version stays 1.
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public List<DatasetClass>? Classes;''')
    # readers
    s = rep(s, '''    public static IReadOnlyList<EvDatasetEntry> ReadFolder(string datasetDir)
    {''', '''    public static IReadOnlyList<EvDatasetEntry> ReadFolder(string datasetDir) => ReadFolder(datasetDir, out _);

    /// <summary>Same as <see cref="ReadFolder(string)"/>; <paramref name="classes"/> is the manifest class table, or null for a two-class NG/OK dataset.</summary>
    public static IReadOnlyList<EvDatasetEntry> ReadFolder(string datasetDir, out SegAiClassTable? classes)
    {''')
    s = rep(s, '''        var data = ParseDataset(File.ReadAllText(manifest));
        var result = new List<EvDatasetEntry>();''', '''        var data = ParseDataset(File.ReadAllText(manifest));
        classes = ParseClasses(data);
        var result = new List<EvDatasetEntry>();''')
    s = rep(s, '''    public static IReadOnlyList<EvDatasetEntry> ReadZip(string zipPath)
    {''', '''    public static IReadOnlyList<EvDatasetEntry> ReadZip(string zipPath) => ReadZip(zipPath, out _);

    /// <summary>Same as <see cref="ReadZip(string)"/>; <paramref name="classes"/> is the manifest class table, or null for a two-class NG/OK dataset.</summary>
    public static IReadOnlyList<EvDatasetEntry> ReadZip(string zipPath, out SegAiClassTable? classes)
    {''')
    s = rep(s, '''        using (var reader = new StreamReader(manifest.Open(), Encoding.UTF8)) data = ParseDataset(reader.ReadToEnd());
        var result = new List<EvDatasetEntry>();''', '''        using (var reader = new StreamReader(manifest.Open(), Encoding.UTF8)) data = ParseDataset(reader.ReadToEnd());
        classes = ParseClasses(data);
        int classCount = classes?.Classes.Count ?? 2;
        var result = new List<EvDatasetEntry>();''')
    s = rep(s, 'SegLabelRaster.ValidateAnnotations(regions, decoded.Width, decoded.Height);', 'SegLabelRaster.ValidateAnnotations(regions, decoded.Width, decoded.Height, classCount);')
    # writers
    s = rep(s, '''    public static void WriteZip(string zipPath, IReadOnlyList<EvDatasetEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        WriteZipCore(zipPath, entries);''', '''    public static void WriteZip(string zipPath, IReadOnlyList<EvDatasetEntry> entries, SegAiClassTable? classes = null)
    {
        ArgumentNullException.ThrowIfNull(entries);
        WriteZipCore(zipPath, entries, classes);''')
    s = rep(s, 'internal static void WriteZipCore(string zipPath, IEnumerable<EvDatasetEntry> entries)', 'internal static void WriteZipCore(string zipPath, IEnumerable<EvDatasetEntry> entries, SegAiClassTable? classes = null)')
    s = rep(s, '''                var data = new DatasetFile();
                int index = 0;
                foreach (var entry in entries)
                    data.Images.Add(AppendEntry(index++, entry, (name, bytes) => WriteEntry(zip, name, bytes)));''', '''                var data = new DatasetFile { Classes = ToDatasetClasses(classes) };
                int index = 0;
                foreach (var entry in entries)
                    data.Images.Add(AppendEntry(index++, entry, (name, bytes) => WriteEntry(zip, name, bytes), classes?.Classes.Count ?? 2));''')
    s = rep(s, '''    public static void WriteFolder(string folder, IReadOnlyList<EvDatasetEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        WriteFolderCore(folder, entries);''', '''    public static void WriteFolder(string folder, IReadOnlyList<EvDatasetEntry> entries, SegAiClassTable? classes = null)
    {
        ArgumentNullException.ThrowIfNull(entries);
        WriteFolderCore(folder, entries, classes);''')
    s = rep(s, 'internal static void WriteFolderCore(string folder, IEnumerable<EvDatasetEntry> entries)', 'internal static void WriteFolderCore(string folder, IEnumerable<EvDatasetEntry> entries, SegAiClassTable? classes = null)')
    s = rep(s, '''        var data = new DatasetFile();
        int index = 0;
        foreach (var entry in entries)
            data.Images.Add(AppendEntry(index++, entry, (name, bytes) => File.WriteAllBytes(Path.Combine(fullFolder, name), bytes)));''', '''        var data = new DatasetFile { Classes = ToDatasetClasses(classes) };
        int index = 0;
        foreach (var entry in entries)
            data.Images.Add(AppendEntry(index++, entry, (name, bytes) => File.WriteAllBytes(Path.Combine(fullFolder, name), bytes), classes?.Classes.Count ?? 2));''')
    s = rep(s, 'private static DatasetEntry AppendEntry(int index, EvDatasetEntry entry, Action<string, byte[]> write)', 'private static DatasetEntry AppendEntry(int index, EvDatasetEntry entry, Action<string, byte[]> write, int classCount)')
    s = rep(s, '''        string image = "image" + index + ".png", mask = "mask" + index + ".png";''', '''        SegLabelRaster.ValidateAnnotations(entry.Regions, int.MaxValue, int.MaxValue, classCount);
        string image = "image" + index + ".png", mask = "mask" + index + ".png";''')
    s = rep(s, '''    private static DatasetFile ParseDataset(string text)''', '''    private static List<DatasetClass>? ToDatasetClasses(SegAiClassTable? table)
        => table?.Classes.Select(c => new DatasetClass { Name = c.Name, Kind = c.Kind == SegAiClassKind.Good ? "good" : "defect", Enabled = c.Enabled }).ToList();

    private static SegAiClassTable? ParseClasses(DatasetFile data)
    {
        if (data.Classes == null) return null;
        var entries = data.Classes.Select(c => new SegAiClassEntry(c?.Name ?? string.Empty,
            string.Equals(c?.Kind, "good", StringComparison.OrdinalIgnoreCase) ? SegAiClassKind.Good : SegAiClassKind.Defect, c?.Enabled ?? true)).ToList();
        SegAiModelProblem? problem = SegAiClassTable.Validate(entries);
        if (problem != null) throw new InvalidDataException("Invalid dataset class table: " + problem.En);
        return new SegAiClassTable(entries);
    }

    private static DatasetFile ParseDataset(string text)''')
    return s
rw('Data/EasyVisionDataset.cs', ds)

def ex(s):
    s = rep(s, 'EasyVisionDataset.WriteZipCore(zipPath, entries);', 'EasyVisionDataset.WriteZipCore(zipPath, entries, ctx.Map.HasNamedTable ? ctx.Map.Table : null);')
    s = rep(s, 'EasyVisionDataset.WriteFolderCore(ctx.OutDir, entries);', 'EasyVisionDataset.WriteFolderCore(ctx.OutDir, entries, ctx.Map.HasNamedTable ? ctx.Map.Table : null);')
    s = rep(s, 'SegLabelRaster.ValidateAnnotations(regions, decoded.Width, decoded.Height);', 'SegLabelRaster.ValidateAnnotations(regions, decoded.Width, decoded.Height, ctx.Map.Count);')
    s = rep(s, '''"' is not mapped to NG or OK");''', '''"' is not a mapped class (" + string.Join("/", ctx.Map.Table.Classes.Select(c => c.Name)) + ")");''')
    return s
rw('Export/ExportService.cs', ex)

def se(s):
    s = rep(s, '''            IReadOnlyList<EvDatasetEntry> entries = Directory.Exists(folderOrZip)
                ? EasyVisionDataset.ReadFolder(folderOrZip)
                : EasyVisionDataset.ReadZip(folderOrZip);
''', '''            SegAiClassTable? datasetClasses;
            IReadOnlyList<EvDatasetEntry> entries = Directory.Exists(folderOrZip)
                ? EasyVisionDataset.ReadFolder(folderOrZip, out datasetClasses)
                : EasyVisionDataset.ReadZip(folderOrZip, out datasetClasses);
            RequireMatchingClasses(datasetClasses);
''')
    s = rep(s, 'EasyVisionDataset.WriteZip(zipPath, entries);', 'EasyVisionDataset.WriteZip(zipPath, entries, _map.HasNamedTable ? _map.Table : null);')
    s = rep(s, '''    /// <summary>Imports an EasyVision dataset folder''', '''    // A dataset carries label ids only; ids mean the same classes only when the dataset table equals the project table (names and kinds).
    private void RequireMatchingClasses(SegAiClassTable? dataset)
    {
        if (dataset == null)
        {
            if (_map.HasNamedTable) throw new InvalidDataException("The dataset has no class table but the project uses named classes.");
            return;
        }

        SegAiClassTable project = _map.Table;
        bool same = dataset.Classes.Count == project.Classes.Count;
        for (int i = 0; same && i < project.Classes.Count; i++)
            same = string.Equals(dataset.Classes[i].Name, project.Classes[i].Name, StringComparison.OrdinalIgnoreCase) && dataset.Classes[i].Kind == project.Classes[i].Kind;
        if (!same) throw new InvalidDataException("The dataset class table does not match the project classes.");
    }

    /// <summary>Imports an EasyVision dataset folder''')
    return s
rw('SegAiSession.cs', se)
print('ok')
