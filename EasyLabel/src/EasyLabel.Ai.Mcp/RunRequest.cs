using EasyLabel.Core.Io;
using Newtonsoft.Json;

namespace EasyLabel.Ai.Mcp;

/// <summary>One already-existing shape handed to the agent (label, "polygon"/"rectangle"/..., points in image pixels).</summary>
public sealed class RunShape
{
    [JsonProperty("label")]
    public string Label { get; set; } = string.Empty;

    [JsonProperty("shapeType")]
    public string ShapeType { get; set; } = "polygon";

    [JsonProperty("points")]
    public List<double[]> Points { get; set; } = new List<double[]>();
}

/// <summary>
/// <c>&lt;run&gt;/request.json</c>: written by EasyAI before the agent starts, read once by the MCP server.
/// </summary>
public sealed class RunRequest
{
    public const string FileName = "request.json";

    [JsonProperty("imagePath")]
    public string ImagePath { get; set; } = string.Empty;

    [JsonProperty("imageWidth")]
    public int ImageWidth { get; set; }

    [JsonProperty("imageHeight")]
    public int ImageHeight { get; set; }

    [JsonProperty("classNames")]
    public List<string> ClassNames { get; set; } = new List<string>();

    [JsonProperty("existingShapes")]
    public List<RunShape> ExistingShapes { get; set; } = new List<RunShape>();

    /// <summary>
    /// Suggestions of earlier runs the user has neither accepted nor rejected yet (same shape format as <see cref="ExistingShapes"/>).
    /// Optional: a request file without it loads with an empty list. A new shape that duplicates one of them is rejected.
    /// </summary>
    [JsonProperty("pendingShapes")]
    public List<RunShape> PendingShapes { get; set; } = new List<RunShape>();

    [JsonProperty("prompt")]
    public string Prompt { get; set; } = string.Empty;

    /// <summary>
    /// Files the user attached to the prompt as hints (example images of the defect, notes). Optional: a request file without it loads with an
    /// empty list. The agent reads them with the get_reference tool.
    /// </summary>
    [JsonProperty("references")]
    public List<RunReference> References { get; set; } = new List<RunReference>();

    /// <summary>
    /// True (the default, also when the field is absent): a proposed box may extend outside the image (an object cut by the border keeps its true
    /// box; at least <see cref="ProposalStore.MinVisibleFraction"/> of it must be inside). False: proposals are clipped to the image as before.
    /// </summary>
    [JsonProperty("allowOutOfBounds")]
    public bool AllowOutOfBounds { get; set; } = true;

    [JsonProperty("modelsFolder")]
    public string? ModelsFolder { get; set; }

    /// <summary>Path of the project memory file the memory tools read and extend; null = no memory in this run.</summary>
    [JsonProperty("memoryPath")]
    public string? MemoryPath { get; set; }

    /// <summary>Reads and normalises a request file. Throws <see cref="InvalidDataException"/> for unreadable or invalid content.</summary>
    public static RunRequest Load(string path)
    {
        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            throw new InvalidDataException("Cannot read " + path + ": " + ex.Message, ex);
        }

        RunRequest? request;
        try
        {
            request = JsonConvert.DeserializeObject<RunRequest>(text);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Invalid JSON in " + path + ": " + ex.Message, ex);
        }

        if (request == null) throw new InvalidDataException(path + " is empty.");
        request.ClassNames ??= new List<string>();
        request.ExistingShapes ??= new List<RunShape>();
        request.PendingShapes ??= new List<RunShape>();
        request.Prompt ??= string.Empty;
        request.ImagePath ??= string.Empty;
        if (string.IsNullOrWhiteSpace(request.ImagePath)) throw new InvalidDataException(path + " has no imagePath.");
        return request;
    }

    /// <summary>Writes the request atomically.</summary>
    public void Save(string path)
    {
        AtomicFile.WriteAllText(path, JsonConvert.SerializeObject(this, Formatting.Indented));
    }
}

/// <summary>One file attached to a run (full path and the display name).</summary>
public sealed class RunReference
{
    [JsonProperty("path")]
    public string Path { get; set; } = string.Empty;

    [JsonProperty("name")]
    public string Name { get; set; } = string.Empty;
}
