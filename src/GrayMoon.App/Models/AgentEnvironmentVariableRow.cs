namespace GrayMoon.App.Models;

/// <summary>
/// One stored environment variable for the AI agents GrayMoon starts (Worker page, Environment). Machine-level: it is
/// never written to <c>graymoon.json</c>. <see cref="ProtectedValue"/> is encrypted like a connector token, since a
/// value can be a key or a token.
/// </summary>
public sealed class AgentEnvironmentVariableRow
{
    public int AgentEnvironmentVariableId { get; set; }

    /// <summary><c>all</c> or an AI provider id (<c>claude</c>, <c>codex</c>, <c>cursor</c>).</summary>
    public string Scope { get; set; } = string.Empty;

    /// <summary>Unique within <see cref="Scope"/>, compared without case.</summary>
    public string Name { get; set; } = string.Empty;

    public string ProtectedValue { get; set; } = string.Empty;

    /// <summary>Order in the grid.</summary>
    public int Position { get; set; }
}
