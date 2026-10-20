using System.Text.Json;
using System.Text.Json.Serialization;

namespace EasyLabel.Commands.Results;

public sealed record CommandActualCost(
    [property: JsonPropertyName("usd")] decimal Usd = 0,
    [property: JsonPropertyName("turns")] int Turns = 0);

public sealed record CommandResult
{
    [JsonPropertyName("ok")]
    public bool Ok { get; init; }
    [JsonPropertyName("code")]
    public string Code { get; init; } = "OK";
    [JsonPropertyName("message_key")]
    public string MessageKey { get; init; } = "cmd.ok";
    [JsonPropertyName("data")]
    public JsonElement? Data { get; init; }
    [JsonPropertyName("undoToken")]
    public string? UndoToken { get; init; }
    [JsonPropertyName("cost")]
    public CommandActualCost Cost { get; init; } = new();
    [JsonPropertyName("ms")]
    public long Ms { get; init; }

    public static CommandResult Success(JsonElement? data = null, string? undoToken = null,
        CommandActualCost? cost = null) => new()
    {
        Ok = true, Data = data?.Clone(), UndoToken = undoToken, Cost = cost ?? new()
    };

    public static CommandResult Failure(string code, string messageKey, JsonElement? data = null) => new()
    {
        Ok = false, Code = code, MessageKey = messageKey, Data = data?.Clone()
    };
}
