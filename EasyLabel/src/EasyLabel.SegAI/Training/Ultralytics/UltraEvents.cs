using Newtonsoft.Json.Linq;

namespace EasyLabel.SegAI.Training.Ultralytics;

/// <summary>One model family of the Ultralytics trainer (<c>--list-families</c>).</summary>
public sealed record UltraFamily(
    string Id,
    string Task,
    string Weights,
    int MinVramMb,
    int Batch,
    bool Fits4Gb,
    bool Experimental,
    string Note);

/// <summary>The <c>preflight</c> answer: what the trainer environment can do.</summary>
public sealed record UltraPreflight(
    string Python,
    string Torch,
    string Ultralytics,
    bool Cuda,
    string? Gpu,
    int? VramMb,
    bool DriverOk);

/// <summary>The kinds of stdout event the trainer writes.</summary>
public enum UltraEventKind
{
    Preflight,
    Families,
    Start,
    Epoch,
    Done,
    Error,
}

/// <summary>One parsed stdout line. Only the fields of its <see cref="Kind"/> are filled; the rest keep their defaults.</summary>
public sealed record UltraEvent(UltraEventKind Kind)
{
    public UltraPreflight? Preflight { get; init; }

    public IReadOnlyList<UltraFamily> Families { get; init; } = Array.Empty<UltraFamily>();

    // start
    public string Family { get; init; } = string.Empty;

    public string Task { get; init; } = string.Empty;

    public string Device { get; init; } = string.Empty;

    // start + epoch
    public int Epoch { get; init; }

    public int Epochs { get; init; }

    // epoch
    public double? BoxLoss { get; init; }

    public double? ClsLoss { get; init; }

    public double? DflLoss { get; init; }

    // epoch + done
    public double? Map50 { get; init; }

    public double? Map5095 { get; init; }

    public double ElapsedSeconds { get; init; }

    // done
    public string BestPt { get; init; } = string.Empty;

    public string? Onnx { get; init; }

    public IReadOnlyList<string> Classes { get; init; } = Array.Empty<string>();

    // error
    public string ErrorCode { get; init; } = string.Empty;

    public string Message { get; init; } = string.Empty;
}

/// <summary>Parses the trainer's JSON-lines protocol. Tolerant: blank, non-JSON, unknown and incomplete lines never throw.</summary>
public static class UltraJson
{
    /// <summary>The event on one stdout line, or null when the line is not a known event.</summary>
    public static UltraEvent? ParseLine(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        string trimmed = line.Trim();
        if (trimmed[0] != '{')
        {
            return null;
        }

        JObject root;
        try
        {
            root = JObject.Parse(trimmed);
        }
        catch (Newtonsoft.Json.JsonException)
        {
            return null;
        }

        string name = Str(root, "event");
        switch (name)
        {
            case "preflight":
                return new UltraEvent(UltraEventKind.Preflight)
                {
                    Preflight = new UltraPreflight(
                        Str(root, "python"),
                        Str(root, "torch"),
                        Str(root, "ultralytics"),
                        Bool(root, "cuda"),
                        NullableStr(root, "gpu"),
                        NullableInt(root, "vram_mb"),
                        Bool(root, "driver_ok")),
                };
            case "families":
                return new UltraEvent(UltraEventKind.Families) { Families = ParseFamilies(root["items"]) };
            case "start":
                return new UltraEvent(UltraEventKind.Start)
                {
                    Family = Str(root, "family"),
                    Task = Str(root, "task"),
                    Device = Str(root, "device"),
                    Epochs = Int(root, "epochs"),
                };
            case "epoch":
                return new UltraEvent(UltraEventKind.Epoch)
                {
                    Epoch = Int(root, "epoch"),
                    Epochs = Int(root, "epochs"),
                    BoxLoss = NullableDouble(root, "box_loss"),
                    ClsLoss = NullableDouble(root, "cls_loss"),
                    DflLoss = NullableDouble(root, "dfl_loss"),
                    Map50 = NullableDouble(root, "map50"),
                    Map5095 = NullableDouble(root, "map5095"),
                    ElapsedSeconds = NullableDouble(root, "elapsed_s") ?? 0,
                };
            case "done":
                JToken? metrics = root["metrics"];
                return new UltraEvent(UltraEventKind.Done)
                {
                    BestPt = Str(root, "best_pt"),
                    Onnx = NullableStr(root, "onnx"),
                    Classes = ParseStrings(root["classes"]),
                    Map50 = metrics is JObject m1 ? NullableDouble(m1, "map50") : null,
                    Map5095 = metrics is JObject m2 ? NullableDouble(m2, "map5095") : null,
                    ElapsedSeconds = NullableDouble(root, "elapsed_s") ?? 0,
                };
            case "error":
                return new UltraEvent(UltraEventKind.Error)
                {
                    ErrorCode = Str(root, "code"),
                    Message = Str(root, "message"),
                };
            default:
                return null;
        }
    }

    private static IReadOnlyList<UltraFamily> ParseFamilies(JToken? items)
    {
        List<UltraFamily> list = new List<UltraFamily>();
        if (items is JArray array)
        {
            foreach (JToken token in array)
            {
                if (token is JObject o && Str(o, "id").Length > 0)
                {
                    list.Add(new UltraFamily(
                        Str(o, "id"),
                        Str(o, "task"),
                        Str(o, "weights"),
                        Int(o, "min_vram_mb"),
                        Int(o, "batch"),
                        Bool(o, "fits_4gb"),
                        Bool(o, "experimental"),
                        Str(o, "note")));
                }
            }
        }

        return list;
    }

    private static IReadOnlyList<string> ParseStrings(JToken? token)
    {
        List<string> list = new List<string>();
        if (token is JArray array)
        {
            foreach (JToken item in array)
            {
                list.Add(item.Type == JTokenType.Null ? string.Empty : item.ToString());
            }
        }

        return list;
    }

    private static string Str(JObject o, string key)
    {
        return NullableStr(o, key) ?? string.Empty;
    }

    private static string? NullableStr(JObject o, string key)
    {
        JToken? t = o[key];
        return t == null || t.Type == JTokenType.Null || t.Type == JTokenType.Object || t.Type == JTokenType.Array ? null : t.ToString();
    }

    private static bool Bool(JObject o, string key)
    {
        JToken? t = o[key];
        return t != null && t.Type == JTokenType.Boolean && t.Value<bool>();
    }

    private static int Int(JObject o, string key)
    {
        return NullableInt(o, key) ?? 0;
    }

    private static int? NullableInt(JObject o, string key)
    {
        double? v = NullableDouble(o, key);
        return v.HasValue && v.Value >= int.MinValue && v.Value <= int.MaxValue ? (int)v.Value : null;
    }

    private static double? NullableDouble(JObject o, string key)
    {
        JToken? t = o[key];
        if (t == null || (t.Type != JTokenType.Float && t.Type != JTokenType.Integer))
        {
            return null;
        }

        double v = t.Value<double>();
        return double.IsNaN(v) || double.IsInfinity(v) ? null : v;
    }
}
