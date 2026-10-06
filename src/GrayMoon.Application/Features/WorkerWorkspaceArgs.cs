namespace GrayMoon.Application.Features;

public sealed record WorkerWorkspaceArgs(
    string WorkspaceRoot,
    string WorkspaceFolderName,
    string? WorkspaceRepositoryName);
