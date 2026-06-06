namespace GrayMoon.App.Components.Modals;

/// <summary>Input collected by the New Feature modal when the user clicks Create.</summary>
public sealed record NewFeatureRequest(
    string NewBranchName,
    string BaseBranch,
    bool SkipReposOnTags,
    bool UpdateDependencies,
    bool PushChanges);
