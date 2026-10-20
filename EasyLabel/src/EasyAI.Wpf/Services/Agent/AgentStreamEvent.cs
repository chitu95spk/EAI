namespace EasyAI.Desktop.Services.Agent;

/// <summary>What one live line of an agent run is.</summary>
public enum AgentStreamEventKind
{
    /// <summary>Text the agent wrote (or a short start note).</summary>
    Text = 0,

    /// <summary>The agent called a tool; <see cref="AgentStreamEvent.ToolName"/> is set.</summary>
    ToolCall,

    /// <summary>A tool answered; image data is never part of it.</summary>
    ToolResult,

    /// <summary>The final result line: <see cref="AgentStreamEvent.Cost"/> and <see cref="AgentStreamEvent.Turns"/> are set.</summary>
    Result,

    /// <summary>The run reported an error.</summary>
    Error,
}

/// <summary>One short, readable line of a running agent (streamed to the chat). Never holds image data or the prompt.</summary>
public sealed record AgentStreamEvent(AgentStreamEventKind Kind, string Text)
{
    /// <summary>Tool name without the MCP prefix (tool call and tool result).</summary>
    public string ToolName { get; init; } = string.Empty;

    /// <summary>Id that links a tool result to its call (may be empty).</summary>
    public string ToolUseId { get; init; } = string.Empty;

    /// <summary>Total cost in USD (Result and Error only).</summary>
    public double Cost { get; init; }

    /// <summary>Number of turns (Result and Error only).</summary>
    public int Turns { get; init; }
}
