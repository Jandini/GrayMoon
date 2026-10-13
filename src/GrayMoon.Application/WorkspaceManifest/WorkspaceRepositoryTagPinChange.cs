namespace GrayMoon.Application.WorkspaceManifest;

/// <summary>
/// One source repository's tag pin in the Workspace definition. Both <see cref="Tag"/> and <see cref="Commit"/>
/// null clears the pin. Setting a pin requires both.
/// </summary>
public sealed record WorkspaceRepositoryTagPinChange(string RepositoryUrl, string? Tag, string? Commit);
