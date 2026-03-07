namespace GrayMoon.Abstractions.Agent;

/// <summary>
/// Payload for ResponseCommand: result of an agent command sent from agent to app.
/// Single object avoids argument count/order mismatches; shared type keeps the contract explicit.
/// </summary>
public sealed record AgentCommandResponse(bool Success, object? Data, string? Error);
