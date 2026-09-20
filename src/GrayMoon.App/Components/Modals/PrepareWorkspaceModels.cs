namespace GrayMoon.App.Components.Modals;

/// <summary>Input collected by the Prepare Workspace modal when the user clicks Create.</summary>
public sealed record PrepareWorkspaceRequest(
    string NewBranchName,
    string BaseBranch,
    bool SkipReposOnTags,
    bool UpdateDependencies,
    bool PushChanges);
