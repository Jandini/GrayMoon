namespace GrayMoon.Abstractions.Worker;

/// <summary>One line (or segment) of command output delivered to the app for overlay terminal display.</summary>
/// <param name="StreamLabel">Bracket prefix source: repository name, workspace name, command name, or null for <c>[worker]</c>.</param>
public sealed record WorkerCommandStreamLine(
    string? StreamLabel,
    WorkerCommandStreamKind Kind,
    string Text);
