namespace GrayMoon.Abstractions.Worker;

/// <summary>
/// Payload for ResponseCommand: result of a worker command sent from worker to app.
/// Single object avoids argument count/order mismatches; shared type keeps the contract explicit.
/// </summary>
public sealed record WorkerCommandResponse(bool Success, object? Data, string? Error);
