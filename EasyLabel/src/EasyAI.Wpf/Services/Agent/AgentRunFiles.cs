using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace EasyAI.Desktop.Services.Agent;

/// <summary>One shape that already exists in the document, handed to the agent (points in image pixels).</summary>
public sealed class AgentShapeInfo
{
    public AgentShapeInfo(string label, string shapeType, IReadOnlyList<double[]> points)
    {
        Label = label;
        ShapeType = shapeType;
        Points = points;
    }

    public string Label { get; }

    /// <summary>"polygon", "rectangle", ... as stored in the document.</summary>
    public string ShapeType { get; }

    public IReadOnlyList<double[]> Points { get; }
}

/// <summary>What one agent run does; it decides which built-in skills go into the system prompt.</summary>
public enum AgentRunKind
{
    /// <summary>Find objects and propose new shapes (the default).</summary>
    Label,

    /// <summary>Review the existing shapes and propose replace / remove fixes.</summary>
    Check,

    /// <summary>Label an image of a batch from the experience block of the first labelled image.</summary>
    Repeat,
}

/// <summary>Everything one agent run needs to know about the image and the request.</summary>
public sealed class AgentRunInput
{
    public AgentRunInput(
        string imagePath,
        string rel,
        int imageWidth,
        int imageHeight,
        IReadOnlyList<string> classNames,
        IReadOnlyList<AgentShapeInfo> existingShapes,
        string prompt,
        string? modelsFolder,
        int fallbackClassIndex,
        bool bypassCache,
        string? memoryPath = null,
        string? outcomeDigest = null,
        string? experienceBlock = null,
        IReadOnlyList<AgentShapeInfo>? pendingShapes = null,
        AgentRunKind kind = AgentRunKind.Label,
        string? vocabularyBlock = null,
        IReadOnlyList<string>? references = null)
    {
        Kind = kind;
        References = references ?? Array.Empty<string>();
        VocabularyBlock = vocabularyBlock ?? string.Empty;
        PendingShapes = pendingShapes ?? Array.Empty<AgentShapeInfo>();
        OutcomeDigest = outcomeDigest ?? string.Empty;
        ExperienceBlock = experienceBlock ?? string.Empty;
        ImagePath = imagePath;
        Rel = rel;
        ImageWidth = imageWidth;
        ImageHeight = imageHeight;
        ClassNames = classNames;
        ExistingShapes = existingShapes;
        Prompt = prompt;
        ModelsFolder = modelsFolder;
        FallbackClassIndex = fallbackClassIndex;
        BypassCache = bypassCache;
        MemoryPath = memoryPath;
    }

    /// <summary>
    /// True (default): the agent's boxes may extend outside the image (an object cut by the border). It mirrors <c>AppSettings.AllowOutOfBounds</c>;
    /// the request file carries it only when false, so an ordinary request.json is unchanged.
    /// </summary>
    public bool AllowOutOfBounds { get; init; } = true;

    /// <summary>
    /// Full paths of the files the user attached to the prompt as hints (example pictures of the defect, short notes); the agent reads them with
    /// get_reference. Empty = none, and then request.json, the prompt and the cache key are exactly as before.
    /// </summary>
    public IReadOnlyList<string> References { get; }

    /// <summary>The project memory file (<c>.easyai/agent-memory.md</c>) the memory tools use; null = no memory.</summary>
    public string? MemoryPath { get; }

    /// <summary>
    /// The review digest ("Learned from review ...", at most 1200 characters) computed once for this run or batch and then frozen; empty = none.
    /// </summary>
    public string OutcomeDigest { get; }

    /// <summary>
    /// The experience block of a batch (built from the first labelled image, at most 1500 characters, text only); empty = none, and then the
    /// prompt and the cache key are exactly as before.
    /// </summary>
    public string ExperienceBlock { get; }

    /// <summary>What this run does (default <see cref="AgentRunKind.Label"/>); it selects the skills of the system prompt.</summary>
    public AgentRunKind Kind { get; }

    /// <summary>
    /// The project vocabulary block ("quat" means class FAN, at most 600 characters) computed once for this run or batch and then frozen; empty = none,
    /// and then the prompt and the cache key are exactly as before.
    /// </summary>
    public string VocabularyBlock { get; }

    /// <summary>
    /// A copy of this input with <paramref name="experienceBlock"/> (empty = none). A label run that gets an experience block becomes a
    /// <see cref="AgentRunKind.Repeat"/> run (the later images of a batch); a check run stays a check run.
    /// </summary>
    public AgentRunInput WithExperienceBlock(string? experienceBlock)
    {
        AgentRunKind kind = Kind == AgentRunKind.Label && !string.IsNullOrWhiteSpace(experienceBlock) ? AgentRunKind.Repeat : Kind;
        return new AgentRunInput(ImagePath, Rel, ImageWidth, ImageHeight, ClassNames, ExistingShapes, Prompt, ModelsFolder, FallbackClassIndex, BypassCache, MemoryPath, OutcomeDigest, experienceBlock, PendingShapes, kind, VocabularyBlock, References) { AllowOutOfBounds = AllowOutOfBounds };
    }

    /// <summary>A copy of this input with another <see cref="Kind"/>.</summary>
    public AgentRunInput WithKind(AgentRunKind kind)
    {
        return new AgentRunInput(ImagePath, Rel, ImageWidth, ImageHeight, ClassNames, ExistingShapes, Prompt, ModelsFolder, FallbackClassIndex, BypassCache, MemoryPath, OutcomeDigest, ExperienceBlock, PendingShapes, kind, VocabularyBlock, References) { AllowOutOfBounds = AllowOutOfBounds };
    }

    /// <summary>
    /// Suggestions of this image that the user has not reviewed yet (outline only). The agent must not propose them again; empty = none, and
    /// then request.json, the prompt and the cache key are exactly as before.
    /// </summary>
    public IReadOnlyList<AgentShapeInfo> PendingShapes { get; }

    /// <summary>Full path of the image; it is read in place, never copied.</summary>
    public string ImagePath { get; }

    /// <summary>Project-relative path of the image (the suggestions belong to it; the usage log names it).</summary>
    public string Rel { get; }

    public int ImageWidth { get; }

    public int ImageHeight { get; }

    public IReadOnlyList<string> ClassNames { get; }

    public IReadOnlyList<AgentShapeInfo> ExistingShapes { get; }

    public string Prompt { get; }

    /// <summary>The folder with the sam2 / yolo-world sub-folders the app uses, or null.</summary>
    public string? ModelsFolder { get; }

    /// <summary>Class used when a proposal names a label the project does not have.</summary>
    public int FallbackClassIndex { get; }

    /// <summary>Run even when the same request has a cached result.</summary>
    public bool BypassCache { get; }
}

/// <summary>Content of the files the agent run folder holds: <c>request.json</c>, <c>mcp.json</c>, <c>system.md</c>.</summary>
public static class AgentRunFiles
{
    public const string RequestFileName = "request.json";
    public const string McpConfigFileName = "mcp.json";
    public const string SystemPromptFileName = "system.md";
    public const string ProposalsFileName = "proposals.json";
    public const string TempFolderName = "t";

    public const string SystemPromptPrefix = "You label images for an annotation tool through the easyai MCP tools. Follow these procedures.";

    /// <summary>The request the MCP server reads (field names are those of EasyLabel.Ai.Mcp.RunRequest).</summary>
    public static string BuildRequestJson(AgentRunInput input)
    {
        JArray shapes = new JArray();
        foreach (AgentShapeInfo shape in input.ExistingShapes)
        {
            shapes.Add(new JObject
            {
                ["label"] = shape.Label,
                ["shapeType"] = shape.ShapeType,
                ["points"] = new JArray(shape.Points.Select(p => (object)new JArray(p[0], p[1]))),
            });
        }

        JArray pendingShapes = new JArray();
        foreach (AgentShapeInfo shape in input.PendingShapes)
        {
            pendingShapes.Add(new JObject
            {
                ["label"] = shape.Label,
                ["shapeType"] = shape.ShapeType,
                ["points"] = new JArray(shape.Points.Select(p => (object)new JArray(p[0], p[1]))),
            });
        }

        JObject root = new JObject
        {
            ["imagePath"] = input.ImagePath,
            ["imageWidth"] = input.ImageWidth,
            ["imageHeight"] = input.ImageHeight,
            ["classNames"] = new JArray(input.ClassNames),
            ["existingShapes"] = shapes,
            ["prompt"] = input.Prompt,
            ["modelsFolder"] = string.IsNullOrWhiteSpace(input.ModelsFolder) ? JValue.CreateNull() : new JValue(input.ModelsFolder),
            ["memoryPath"] = string.IsNullOrWhiteSpace(input.MemoryPath) ? JValue.CreateNull() : new JValue(input.MemoryPath),
        };
        if (pendingShapes.Count > 0)
        {
            // Optional field: written only when there is something pending, so an ordinary request.json is unchanged.
            root["pendingShapes"] = pendingShapes;
        }

        if (!input.AllowOutOfBounds)
        {
            root["allowOutOfBounds"] = false;
        }

        if (input.References.Count > 0)
        {
            // Optional field, same rule: only when the user attached hint files.
            root["references"] = new JArray(input.References.Select(path => (object)new JObject { ["path"] = path, ["name"] = Path.GetFileName(path) }));
        }

        return root.ToString(Formatting.Indented);
    }

    /// <summary>The MCP config naming the easyai server: <c>EasyLabel.Ai.Mcp.exe --run &lt;run folder&gt;</c>.</summary>
    public static string BuildMcpConfigJson(string mcpExePath, string runFolder, IReadOnlyList<McpServerEntry>? approvedServers = null)
    {
        JObject servers = new JObject
        {
            ["easyai"] = new JObject
            {
                ["command"] = mcpExePath,
                ["args"] = new JArray("--run", runFolder),
            },
        };

        // Only entries whose approval hash still matches are written; an unapproved or edited one never reaches the agent.
        foreach (McpServerEntry server in McpServerRegistry.OnlyApproved(approvedServers))
        {
            servers[server.Name] = new JObject
            {
                ["command"] = server.Command,
                ["args"] = new JArray(ReplaceRun(server.Args, runFolder).Cast<object>()),
            };
        }

        JObject root = new JObject { ["mcpServers"] = servers };
        return root.ToString(Formatting.Indented);
    }

    /// <summary>The arguments of an external server with every <c>{run}</c> replaced by the run folder path.</summary>
    public static IReadOnlyList<string> ReplaceRun(IEnumerable<string>? args, string runFolder)
    {
        return (args ?? Array.Empty<string>()).Select(a => (a ?? string.Empty).Replace(McpServerEntry.WorkingFolderPlaceholder, runFolder)).ToList();
    }

    /// <summary>
    /// The system-prompt block that tells the agent about the attached hint files (empty when none). The size and time of every file are part of
    /// the text, so the cache key (it hashes this block) changes when an attachment is replaced or edited.
    /// </summary>
    public static string ReferenceBlock(IReadOnlyList<string> references)
    {
        if (references == null || references.Count == 0)
        {
            return string.Empty;
        }

        StringBuilder text = new StringBuilder("The user attached " + references.Count + " hint file(s). Call get_reference (index 0.." + (references.Count - 1)
            + ") before you search: they show what to find (for example an example picture of the defect with its box). They are NOT the image to label.");
        for (int i = 0; i < references.Count; i++)
        {
            string signature = string.Empty;
            try
            {
                FileInfo file = new FileInfo(references[i]);
                signature = file.Exists ? " (" + file.Length + " bytes, " + file.LastWriteTimeUtc.Ticks + ")" : " (missing)";
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException)
            {
                signature = " (unreadable)";
            }

            text.Append("\n").Append(i).Append(": ").Append(Path.GetFileName(references[i])).Append(signature);
        }

        return text.ToString();
    }

    /// <summary>The system prompt: the fixed sentence followed by the skill texts, each separated by a blank line.</summary>
    public static string BuildSystemPrompt(IReadOnlyList<string> skillTexts)
    {
        StringBuilder text = new StringBuilder(SystemPromptPrefix);
        foreach (string skill in skillTexts)
        {
            text.Append("\n\n").Append(skill.Trim());
        }

        text.Append('\n');
        return text.ToString();
    }

    /// <summary>The skill file only a "Suggest prompt" run reads; a label run leaves it out.</summary>
    public const string SuggestSkillFileName = "prompt-suggestion.md";

    /// <summary>The texts of the <c>Skills\*.md</c> files next to the MCP executable (the suggestion skill excluded), in file-name order.</summary>
    public static IReadOnlyList<string> ReadSkillTexts(IAgentEnvironment env, string agentFolder)
    {
        return ReadSkillTexts(env, agentFolder, null, null);
    }

    /// <summary>
    /// The built-in skill texts plus the skills of <paramref name="userSkillsRoot"/> (<c>&lt;name&gt;\SKILL.md</c>, null = none), leaving out every
    /// name in <paramref name="disabledNames"/>: a disabled skill is in neither the system prompt nor the cache key.
    /// </summary>
    public static IReadOnlyList<string> ReadSkillTexts(IAgentEnvironment env, string agentFolder, IReadOnlyCollection<string>? disabledNames, string? userSkillsRoot)
    {
        return RunSkillCatalog.EnabledTexts(env, agentFolder, userSkillsRoot, disabledNames);
    }

    /// <summary>
    /// The skill texts of one kind of run: only the built-in skills <see cref="RunSkillCatalog.BuiltInNamesFor"/> names for
    /// <paramref name="kind"/>, plus every enabled user skill. The cache key hashes exactly these texts.
    /// </summary>
    public static IReadOnlyList<string> ReadSkillTexts(IAgentEnvironment env, string agentFolder, IReadOnlyCollection<string>? disabledNames, string? userSkillsRoot, AgentRunKind kind)
    {
        return RunSkillCatalog.EnabledTexts(env, agentFolder, userSkillsRoot, disabledNames, kind);
    }

    /// <summary>Most existing and most pending shapes the image facts list (the rest is only counted).</summary>
    public const int MaxFactShapes = 100;

    /// <summary>
    /// The "image facts" block of the system prompt: size, class names and a compact list of the existing shapes (and pending suggestions) as
    /// <c>id label type [x,y,w,h]</c>, ids as the MCP tools name them (e0.. existing, s0.. pending). Deterministic for the same input;
    /// empty when the image size is unknown.
    /// </summary>
    public static string ImageFactsBlock(AgentRunInput input)
    {
        if (input.ImageWidth <= 0 || input.ImageHeight <= 0)
        {
            return string.Empty;
        }

        StringBuilder text = new StringBuilder();
        text.Append("Image facts (already read for you; do not call get_image_info or list_shapes for them):\n");
        text.Append("size: ").Append(input.ImageWidth.ToString(CultureInfo.InvariantCulture)).Append('x').Append(input.ImageHeight.ToString(CultureInfo.InvariantCulture)).Append(" px\n");
        text.Append("classes: ").Append(input.ClassNames.Count == 0 ? "(none)" : string.Join(", ", input.ClassNames)).Append('\n');
        AppendFactShapes(text, "existing shapes", "e", input.ExistingShapes);
        if (input.PendingShapes.Count > 0)
        {
            AppendFactShapes(text, "pending suggestions (do not propose them again)", "s", input.PendingShapes);
        }

        return text.ToString().TrimEnd('\n');
    }

    private static void AppendFactShapes(StringBuilder text, string title, string prefix, IReadOnlyList<AgentShapeInfo> shapes)
    {
        text.Append(title).Append(" (").Append(shapes.Count.ToString(CultureInfo.InvariantCulture)).Append(')');
        if (shapes.Count == 0)
        {
            text.Append(": none\n");
            return;
        }

        text.Append(":\n");
        for (int i = 0; i < shapes.Count && i < MaxFactShapes; i++)
        {
            AgentShapeInfo shape = shapes[i];
            text.Append(prefix).Append(i.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(shape.Label).Append(' ').Append(shape.ShapeType);
            string? box = FactBox(shape);
            if (box != null)
            {
                text.Append(' ').Append(box);
            }

            text.Append('\n');
        }

        if (shapes.Count > MaxFactShapes)
        {
            text.Append("... ").Append((shapes.Count - MaxFactShapes).ToString(CultureInfo.InvariantCulture)).Append(" more (list_shapes has them all)\n");
        }
    }

    // [x,y,w,h] of the shape's outline (a circle is centre + a point on the rim), rounded to 0.1 px; null without points.
    private static string? FactBox(AgentShapeInfo shape)
    {
        if (shape.Points.Count == 0)
        {
            return null;
        }

        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        if (string.Equals(shape.ShapeType, "circle", StringComparison.OrdinalIgnoreCase) && shape.Points.Count >= 2)
        {
            double r = Math.Sqrt(Math.Pow(shape.Points[1][0] - shape.Points[0][0], 2) + Math.Pow(shape.Points[1][1] - shape.Points[0][1], 2));
            minX = shape.Points[0][0] - r;
            maxX = shape.Points[0][0] + r;
            minY = shape.Points[0][1] - r;
            maxY = shape.Points[0][1] + r;
        }
        else
        {
            foreach (double[] p in shape.Points)
            {
                minX = Math.Min(minX, p[0]);
                maxX = Math.Max(maxX, p[0]);
                minY = Math.Min(minY, p[1]);
                maxY = Math.Max(maxY, p[1]);
            }
        }

        return "[" + Num(minX) + "," + Num(minY) + "," + Num(maxX - minX) + "," + Num(maxY - minY) + "]";
    }

    private static string Num(double value)
    {
        // "+ 0.0" turns a negative zero into 0.
        return (Math.Round(value, 1) + 0.0).ToString("0.#", CultureInfo.InvariantCulture);
    }

    /// <summary>The text of the prompt-suggestion skill, or an empty string when the file is missing.</summary>
    public static string ReadSuggestSkill(IAgentEnvironment env, string agentFolder)
    {
        string path = Path.Combine(agentFolder, McpLocator.SkillsFolderName, SuggestSkillFileName);
        return env.FileExists(path) ? env.ReadAllText(path).Trim() : string.Empty;
    }

    /// <summary>The project memory as a system-prompt block, or an empty string when the memory is empty.</summary>
    public static string MemoryBlock(string? memoryText)
    {
        string text = (memoryText ?? string.Empty).Trim();
        return text.Length == 0 ? string.Empty : "Project memory (notes from earlier runs; the task text wins over them):\n" + text;
    }

    /// <summary>The digest of the agent's review history (<c>agent-outcomes.jsonl</c>), or an empty string; never throws for a missing or unreadable file.</summary>
    public static string OutcomeDigest(string? outcomesPath)
    {
        if (string.IsNullOrWhiteSpace(outcomesPath))
        {
            return string.Empty;
        }

        return EasyLabel.Core.Io.AgentOutcomeDigest.Build(EasyLabel.Core.Io.AgentOutcomeLog.ReadAll(outcomesPath), MaxDigestChars);
    }

    /// <summary>Most characters of the review digest in the system prompt.</summary>
    public const int MaxDigestChars = 1200;

    /// <summary>The digest as a system-prompt block (trimmed, at most <see cref="MaxDigestChars"/>), or an empty string when there is none.</summary>
    public static string OutcomeBlock(string? digest)
    {
        string text = (digest ?? string.Empty).Trim();
        return text.Length <= MaxDigestChars ? text : text.Substring(0, MaxDigestChars);
    }

    /// <summary>The project vocabulary block (<c>.easyai/vocabulary.json</c>), or an empty string; never throws for a missing or unreadable file.</summary>
    public static string VocabularyBlockOf(string? vocabularyPath)
    {
        return string.IsNullOrWhiteSpace(vocabularyPath) ? string.Empty : EasyLabel.Core.Io.ProjectVocabulary.Block(vocabularyPath);
    }

    /// <summary>The vocabulary as a system-prompt block (trimmed, at most <see cref="EasyLabel.Core.Io.ProjectVocabulary.MaxBlockChars"/>), or an empty string.</summary>
    public static string VocabularyTextBlock(string? block)
    {
        string text = (block ?? string.Empty).Trim();
        return text.Length <= EasyLabel.Core.Io.ProjectVocabulary.MaxBlockChars ? text : text.Substring(0, EasyLabel.Core.Io.ProjectVocabulary.MaxBlockChars);
    }

    /// <summary>Most characters of the batch experience block in the system prompt.</summary>
    public const int MaxExperienceChars = 1500;

    /// <summary>The batch experience as a system-prompt block (trimmed, at most <see cref="MaxExperienceChars"/>), or an empty string when there is none.</summary>
    public static string ExperienceTextBlock(string? experience)
    {
        string text = (experience ?? string.Empty).Trim();
        return text.Length <= MaxExperienceChars ? text : text.Substring(0, MaxExperienceChars);
    }

    /// <summary>
    /// The texts of the system prompt: skills, then the project memory block, then the review digest block, then the batch experience block;
    /// empty blocks are left out.
    /// </summary>
    public static IReadOnlyList<string> SystemPromptTexts(IReadOnlyList<string> skills, string memoryBlock, string outcomeBlock, string experienceBlock = "", string imageFactsBlock = "", string vocabularyBlock = "")
    {
        List<string> texts = skills.ToList();
        if (!string.IsNullOrEmpty(memoryBlock))
        {
            texts.Add(memoryBlock);
        }

        if (!string.IsNullOrEmpty(outcomeBlock))
        {
            texts.Add(outcomeBlock);
        }

        if (!string.IsNullOrEmpty(experienceBlock))
        {
            texts.Add(experienceBlock);
        }

        if (!string.IsNullOrEmpty(imageFactsBlock))
        {
            texts.Add(imageFactsBlock);
        }

        if (!string.IsNullOrEmpty(vocabularyBlock))
        {
            texts.Add(vocabularyBlock);
        }

        return texts;
    }

    /// <summary>
    /// The texts hashed by <see cref="SkillPackHash"/> for the result cache: skills, memory block (even when empty), the review digest block
    /// only when it is not empty (so an empty digest leaves the hash, and the cache key, exactly as before), then the options tag.
    /// </summary>
    public static IReadOnlyList<string> CacheHashTexts(IReadOnlyList<string> skills, string memoryBlock, string outcomeBlock, string cacheTag, string experienceBlock = "", string approvedMcpSetHash = "", string pendingShapesHash = "", string imageFactsBlock = "", string vocabularyBlock = "")
    {
        List<string> texts = skills.ToList();
        texts.Add(memoryBlock);
        if (!string.IsNullOrEmpty(outcomeBlock))
        {
            texts.Add(outcomeBlock);
        }

        // The batch experience block joins the hash only when it is not empty (same rule as the digest).
        if (!string.IsNullOrEmpty(experienceBlock))
        {
            texts.Add(experienceBlock);
        }

        // The approved external MCP set (McpServerRegistry.ApprovedSetHash) joins only when at least one server is approved.
        if (!string.IsNullOrEmpty(approvedMcpSetHash))
        {
            texts.Add("mcp-servers:" + approvedMcpSetHash);
        }

        // The pending suggestions (ShapesHash of AgentRunInput.PendingShapes) join only when there are some: none = the key as before.
        if (!string.IsNullOrEmpty(pendingShapesHash))
        {
            texts.Add("pending-shapes:" + pendingShapesHash);
        }

        // The image facts (ImageFactsBlock) join only when not empty; the text itself is hashed, so a changed size, class list or shape is a new key.
        if (!string.IsNullOrEmpty(imageFactsBlock))
        {
            texts.Add("image-facts:" + imageFactsBlock);
        }

        // The project vocabulary joins only when not empty (same rule as the digest): no learned words = the key as before.
        if (!string.IsNullOrEmpty(vocabularyBlock))
        {
            texts.Add("vocabulary:" + vocabularyBlock);
        }

        texts.Add(cacheTag);
        return texts;
    }

    /// <summary>Hash of the skill pack: a changed skill text is a different request for the result cache.</summary>
    public static string SkillPackHash(IReadOnlyList<string> skillTexts)
    {
        return Sha256Hex(string.Join("\n\u001f\n", skillTexts));
    }

    /// <summary>Hash of the existing shapes (label, shape type, points in order): a changed document is a different request for the cache.</summary>
    public static string ShapesHash(IReadOnlyList<AgentShapeInfo>? shapes)
    {
        StringBuilder text = new StringBuilder();
        foreach (AgentShapeInfo shape in shapes ?? Array.Empty<AgentShapeInfo>())
        {
            text.Append(shape.Label).Append('\u001f').Append(shape.ShapeType);
            foreach (double[] p in shape.Points)
            {
                text.Append('\u001f').Append(p[0].ToString("R", CultureInfo.InvariantCulture)).Append(',').Append(p[1].ToString("R", CultureInfo.InvariantCulture));
            }

            text.Append('\n');
        }

        return Sha256Hex(text.ToString());
    }

    public static string Sha256Hex(string text)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        StringBuilder hex = new StringBuilder(hash.Length * 2);
        foreach (byte b in hash)
        {
            hex.Append(b.ToString("x2", CultureInfo.InvariantCulture));
        }

        return hex.ToString();
    }
}

/// <summary>One run folder below <c>%LOCALAPPDATA%\EasyAI\agent-runs</c>.</summary>
public sealed class AgentRunFolder
{
    private AgentRunFolder(string path)
    {
        Path = path;
    }

    public string Path { get; }

    public string RequestPath => System.IO.Path.Combine(Path, AgentRunFiles.RequestFileName);

    public string McpConfigPath => System.IO.Path.Combine(Path, AgentRunFiles.McpConfigFileName);

    public string SystemPromptPath => System.IO.Path.Combine(Path, AgentRunFiles.SystemPromptFileName);

    public string ProposalsPath => System.IO.Path.Combine(Path, AgentRunFiles.ProposalsFileName);

    /// <summary>Where <c>codex exec -o</c> writes the closing message of the run.</summary>
    public string LastMessagePath => System.IO.Path.Combine(Path, "last-message.txt");

    /// <summary>TEMP / TMP of the agent process.</summary>
    public string TempPath => System.IO.Path.Combine(Path, AgentRunFiles.TempFolderName);

    /// <summary><c>%LOCALAPPDATA%\EasyAI\agent-runs</c>.</summary>
    public static string DefaultRoot()
    {
        return System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EasyAI", "agent-runs");
    }

    /// <summary>Folder name <c>yyyyMMdd-HHmmss-xxxxxxxx</c> (time, then the first 8 hex digits of the guid).</summary>
    public static string NewName(DateTime time, Guid id)
    {
        return time.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + id.ToString("N", CultureInfo.InvariantCulture).Substring(0, 8);
    }

    /// <summary>Creates the folder (and its temp folder) below <paramref name="root"/> and writes the three input files.</summary>
    public static AgentRunFolder Create(string root, DateTime time, Guid id, AgentRunInput input, string mcpExePath, string systemPrompt, IReadOnlyList<McpServerEntry>? approvedServers = null)
    {
        AgentRunFolder folder = new AgentRunFolder(System.IO.Path.Combine(root, NewName(time, id)));
        Directory.CreateDirectory(folder.Path);
        Directory.CreateDirectory(folder.TempPath);
        UTF8Encoding utf8 = new UTF8Encoding(false);
        File.WriteAllText(folder.RequestPath, AgentRunFiles.BuildRequestJson(input), utf8);
        File.WriteAllText(folder.McpConfigPath, AgentRunFiles.BuildMcpConfigJson(mcpExePath, folder.Path, approvedServers), utf8);
        File.WriteAllText(folder.SystemPromptPath, systemPrompt, utf8);
        return folder;
    }
}

/// <summary>A run folder as the retention rule sees it.</summary>
public sealed class AgentRunEntry
{
    public AgentRunEntry(string name, DateTime stampUtc)
    {
        Name = name;
        StampUtc = stampUtc;
    }

    public string Name { get; }

    public DateTime StampUtc { get; }
}

/// <summary>
/// Run folders older than 7 days or beyond the newest 50 are deleted when the next run starts. Only folders whose name is a run-folder
/// name (<see cref="AgentRunFolder.NewName"/>) inside the agent-runs root are ever touched; <c>log.csv</c> and everything else stay.
/// </summary>
public static class AgentRunRetention
{
    public const int KeepNewest = 50;
    public const int MaxAgeDays = 7;

    public static bool IsRunFolderName(string name)
    {
        if (name == null || name.Length != 24 || name[8] != '-' || name[15] != '-')
        {
            return false;
        }

        for (int i = 0; i < name.Length; i++)
        {
            if (i == 8 || i == 15)
            {
                continue;
            }

            char c = name[i];
            bool digit = c >= '0' && c <= '9';
            bool hex = i >= 16 && ((c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F'));
            if (!digit && !hex)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Names to delete: not a run folder is never listed; of the run folders everything older than the age limit or beyond the newest <see cref="KeepNewest"/>.</summary>
    public static IReadOnlyList<string> SelectToDelete(IReadOnlyList<AgentRunEntry> entries, DateTime nowUtc)
    {
        List<AgentRunEntry> runs = entries.Where(e => IsRunFolderName(e.Name)).ToList();
        runs.Sort((a, b) =>
        {
            int byTime = b.StampUtc.CompareTo(a.StampUtc);
            return byTime != 0 ? byTime : string.CompareOrdinal(b.Name, a.Name);
        });

        DateTime limit = nowUtc - TimeSpan.FromDays(MaxAgeDays);
        List<string> doomed = new List<string>();
        for (int i = 0; i < runs.Count; i++)
        {
            if (i >= KeepNewest || runs[i].StampUtc < limit)
            {
                doomed.Add(runs[i].Name);
            }
        }

        return doomed;
    }

    /// <summary>Applies <see cref="SelectToDelete"/> to the real <paramref name="root"/>; a folder that cannot be deleted is skipped. Returns how many were deleted.</summary>
    public static int Cleanup(string root, DateTime nowUtc)
    {
        if (!Directory.Exists(root))
        {
            return 0;
        }

        List<AgentRunEntry> entries = new List<AgentRunEntry>();
        try
        {
            foreach (string directory in Directory.GetDirectories(root))
            {
                string name = System.IO.Path.GetFileName(directory);
                if (IsRunFolderName(name))
                {
                    entries.Add(new AgentRunEntry(name, Directory.GetLastWriteTimeUtc(directory)));
                }
            }
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            return 0;
        }

        int deleted = 0;
        foreach (string name in SelectToDelete(entries, nowUtc))
        {
            try
            {
                Directory.Delete(System.IO.Path.Combine(root, name), true);
                deleted++;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
            }
        }

        return deleted;
    }
}
