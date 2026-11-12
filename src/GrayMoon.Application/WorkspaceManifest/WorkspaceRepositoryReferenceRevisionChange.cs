namespace GrayMoon.Application.WorkspaceManifest;

/// <summary>
/// Where one read-only source repository is, for the Workspace definition: the branch it is on and the full commit
/// hash of its HEAD. Both null removes the recorded branch revision. A repository on a tag is recorded as a tag pin
/// (<see cref="WorkspaceRepositoryTagPinChange"/>), not here.
/// </summary>
public sealed record WorkspaceRepositoryReferenceRevisionChange(string RepositoryUrl, string? Branch, string? Commit);
