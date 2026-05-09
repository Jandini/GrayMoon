namespace GrayMoon.Abstractions.Agent;

/// <summary>One line (or segment) of command output delivered to the app for overlay terminal display.</summary>
/// <param name="StreamLabel">Bracket prefix source: repository name, workspace name, command name, or null for <c>[agent]</c>.</param>
public sealed record AgentCommandStreamLine(
    string? StreamLabel,
    AgentCommandStreamKind Kind,
    string Text);
