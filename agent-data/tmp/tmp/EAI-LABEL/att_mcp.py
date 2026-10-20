base = r'E:\Code\EasyVision_Unisen - NoService\EasyLabel\src\EasyLabel.Ai.Mcp'

def edit(path, pairs):
    raw = open(path, 'rb').read()
    bom = raw.startswith(b'\xef\xbb\xbf')
    crlf = b'\r\n' in raw
    s = raw.decode('utf-8-sig').replace('\r\n', '\n')
    for old, new in pairs:
        assert old in s, old
        s = s.replace(old, new, 1)
    if crlf:
        s = s.replace('\n', '\r\n')
    open(path, 'wb').write((b'\xef\xbb\xbf' if bom else b'') + s.encode('utf-8'))

edit(base + r'\RunRequest.cs', [
    ('''    [JsonProperty("prompt")]
    public string Prompt { get; set; } = string.Empty;
''', '''    [JsonProperty("prompt")]
    public string Prompt { get; set; } = string.Empty;

    /// <summary>
    /// Files the user attached to the prompt as hints (example images of the defect, notes). Optional: a request file without it loads with an
    /// empty list. The agent reads them with the get_reference tool.
    /// </summary>
    [JsonProperty("references")]
    public List<RunReference> References { get; set; } = new List<RunReference>();
'''),
])

# RunReference class appended to the end of RunRequest.cs
p = base + r'\RunRequest.cs'
raw = open(p, 'rb').read()
bom = raw.startswith(b'\xef\xbb\xbf')
crlf = b'\r\n' in raw
s = raw.decode('utf-8-sig').replace('\r\n', '\n')
s = s.rstrip('\n') + '''

/// <summary>One file attached to a run (full path and the display name).</summary>
public sealed class RunReference
{
    [JsonProperty("path")]
    public string Path { get; set; } = string.Empty;

    [JsonProperty("name")]
    public string Name { get; set; } = string.Empty;
}
'''
if crlf:
    s = s.replace('\n', '\r\n')
open(p, 'wb').write((b'\xef\xbb\xbf' if bom else b'') + s.encode('utf-8'))

edit(base + r'\EasyAiTools.cs', [
    ('            case "get_crop": return GetCrop(arguments);\n',
     '            case "get_crop": return GetCrop(arguments);\n            case "get_reference": return GetReference(arguments);\n'),
    ('''        if (_request.PendingShapes.Count > 0)
        {
            info["pendingShapes"] = CompactShapes(_request.PendingShapes, "s");''',
     '''        if (_request.References.Count > 0)
        {
            // The attached hint files: read them with get_reference (index = the number below).
            JArray references = new JArray();
            for (int i = 0; i < _request.References.Count; i++)
            {
                references.Add(new JObject { ["index"] = i, ["name"] = _request.References[i].Name, ["kind"] = IsReferenceImage(_request.References[i].Path) ? "image" : "text" });
            }

            info["references"] = references;
        }

        if (_request.PendingShapes.Count > 0)
        {
            info["pendingShapes"] = CompactShapes(_request.PendingShapes, "s");'''),
    ('''    private string ListShapes()
    {''', '''    private static readonly string[] ReferenceImageExtensions = { ".png", ".jpg", ".jpeg", ".bmp", ".tif", ".tiff" };

    private const int ReferenceTextLimit = 4000;

    private static bool IsReferenceImage(string path)
    {
        return Array.IndexOf(ReferenceImageExtensions, System.IO.Path.GetExtension(path ?? string.Empty).ToLowerInvariant()) >= 0;
    }

    private JObject GetReference(JObject args)
    {
        double? requested = OptionalNumber(args, "index");
        int index = requested.HasValue ? (int)Math.Round(requested.Value) : 0;
        if (_request.References.Count == 0) throw new ArgumentException("No file is attached to this run.");
        if (index < 0 || index >= _request.References.Count) throw new ArgumentException("index must be 0.." + (_request.References.Count - 1) + ".");
        RunReference reference = _request.References[index];
        if (!File.Exists(reference.Path)) throw new ArgumentException("The attached file is missing: " + reference.Name);
        if (IsReferenceImage(reference.Path))
        {
            // The user's hint picture (for example an example defect with its box); it is a different picture from the one being labelled.
            using ImageSource source = new ImageSource(new RunRequest { ImagePath = reference.Path });
            CropResult picture = source.Crop(null, null, null, null, 1024);
            string info = "reference " + index + " (" + reference.Name + "), original " + picture.Width + "x" + picture.Height + " px, returned " + picture.OutWidth + "x" + picture.OutHeight + " px. "
                + "It is a hint from the user, NOT the image to label; do not use its pixels as coordinates of the image to label.";
            return ToolResults.Content(
                new JObject { ["type"] = "image", ["data"] = Convert.ToBase64String(picture.Jpeg), ["mimeType"] = "image/jpeg" },
                new JObject { ["type"] = "text", ["text"] = info });
        }

        string text = File.ReadAllText(reference.Path);
        if (text.Length > ReferenceTextLimit) text = text.Substring(0, ReferenceTextLimit) + "\\n(cut at " + ReferenceTextLimit + " characters)";
        return ToolResults.Text("reference " + index + " (" + reference.Name + "):\\n" + text);
    }

    private string ListShapes()
    {'''),
    ('''          {
            "name": "memory_read",''', '''          {
            "name": "get_reference",
            "description": "Returns one file the user attached as a hint (listed as references in get_image_info): an example picture of the defect or object (returned as a JPEG) or a short text note. It is NOT the image to label. Look at a reference before you search for the objects; match what it shows.",
            "inputSchema": {
              "type": "object",
              "properties": {
                "index": { "type": "integer", "description": "Which attached file (0-based, default 0)." }
              },
              "required": []
            }
          },
          {
            "name": "memory_read",'''),
])
