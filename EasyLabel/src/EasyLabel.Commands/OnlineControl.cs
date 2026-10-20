using System.Text.Json;
using EasyLabel.Commands.Discovery;
using EasyLabel.Commands.Results;

namespace EasyLabel.Commands;

/// <summary>Runtime side of the pilot ONL.* commands. Implemented by the host over the Online session.</summary>
public interface IOnlineControl
{
    bool IsRunning { get; }
    void Stop();
    string StatusJson();
    /// <summary>Returns null on success, otherwise a short error code.</summary>
    string? SetThreshold(string? className, double score, double nms);
}

internal sealed class OnlineControlEndpoint : ICommandEndpoint
{
    private readonly Func<JsonElement, CommandResult> _run;
    public OnlineControlEndpoint(Func<JsonElement, CommandResult> run) { _run = run; }
    public bool CanExecute(ICommandContext context, JsonElement args) => true;

    public Task<CommandResult> ExecuteAsync(ICommandContext context, JsonElement args, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_run(args));
    }

    internal static ICommandEndpoint Stop(IOnlineControl control) => new OnlineControlEndpoint(_ =>
    {
        if (!control.IsRunning) return CommandResult.Failure("NOT_RUNNING", "cmd.err.online_not_running");
        control.Stop();
        return CommandResult.Success();
    });

    internal static ICommandEndpoint Status(IOnlineControl control) => new OnlineControlEndpoint(_ =>
    {
        using JsonDocument document = JsonDocument.Parse(control.StatusJson());
        return CommandResult.Success(document.RootElement);
    });

    internal static ICommandEndpoint Threshold(IOnlineControl control) => new OnlineControlEndpoint(args =>
    {
        double score = args.GetProperty("score").GetDouble();
        double nms = args.GetProperty("nms").GetDouble();
        string? className = args.TryGetProperty("class", out JsonElement c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
        if (!control.IsRunning) return CommandResult.Failure("NOT_RUNNING", "cmd.err.online_not_running");
        string? error = control.SetThreshold(className, score, nms);
        return error == null ? CommandResult.Success() : CommandResult.Failure(error, "cmd.err.online_threshold");
    });
}
