using GrayMoon.Abstractions.Notifications;
using GrayMoon.App.Models.Api;

namespace GrayMoon.App.Tests;

/// <summary>
/// The Worker reports the state it left a repository in as a snapshot with probe markers, next to the readable
/// fields of its response. Tests that build a response from those fields add the snapshot here, the way the
/// Worker's <c>RepositoryStateProbe</c> does after a successful Return to Default.
/// </summary>
internal static class WorkerStateTestExtensions
{
    /// <summary>The commit a test response reports when the test does not care which one.</summary>
    public const string HeadCommit = "0123456789abcdef0123456789abcdef01234567";

    /// <summary>Adds <see cref="ReturnToDefaultBranchResponse.State"/> to a successful response. A failed one is returned as it is.</summary>
    public static ReturnToDefaultBranchResponse WithProbedState(this ReturnToDefaultBranchResponse response, string? headCommit = HeadCommit)
    {
        if (!response.Success)
            return response;

        var branchesProbed = response.LocalBranches != null;
        response.State = new RepositoryStateSnapshot
        {
            BranchName = response.CurrentBranch ?? response.DefaultBranch,
            CheckedOutTag = response.CurrentTag,
            HeadCommit = headCommit,
            GitVersion = response.GitVersion,
            DefaultBranchName = response.DefaultBranch,
            OutgoingCommits = response.OutgoingCommits,
            IncomingCommits = response.IncomingCommits,
            DefaultBranchBehind = response.DefaultBranchBehind,
            DefaultBranchAhead = response.DefaultBranchAhead,
            HasUpstream = response.HasUpstream,
            LocalBranches = response.LocalBranches?.ToList(),
            RemoteBranches = response.RemoteBranches?.ToList() ?? (branchesProbed ? [] : null),
            Tags = response.Tags?.ToList() ?? (branchesProbed ? [] : null),
            Projects = response.Projects?.Select(ToNotification).ToList(),
            IdentityProbed = true,
            GitVersionProbed = !string.IsNullOrWhiteSpace(response.GitVersion),
            CommitCountsProbed = true,
            UpstreamProbed = response.HasUpstream.HasValue,
            BranchesProbed = branchesProbed,
            ProjectsProbed = response.Projects != null,
        };
        return response;
    }

    /// <summary>
    /// The snapshot a hook sync carries for a checkout on <paramref name="branch"/>: identity and version are always
    /// read, counts and upstream when the hook reported them.
    /// </summary>
    public static RepositoryStateSnapshot HookState(
        string branch,
        string version,
        int? outgoingCommits = null,
        int? incomingCommits = null,
        bool? hasUpstream = null,
        string? headCommit = HeadCommit) => new()
    {
        BranchName = branch,
        HeadCommit = headCommit,
        GitVersion = version,
        OutgoingCommits = outgoingCommits,
        IncomingCommits = incomingCommits,
        HasUpstream = hasUpstream,
        IdentityProbed = true,
        GitVersionProbed = true,
        CommitCountsProbed = outgoingCommits.HasValue || incomingCommits.HasValue,
        UpstreamProbed = hasUpstream.HasValue,
    };

    private static RepositorySyncProjectNotification ToNotification(WorkerProjectDto project) => new()
    {
        Name = project.Name ?? "",
        ProjectType = project.ProjectType,
        ProjectPath = project.ProjectPath,
        TargetFramework = project.TargetFramework,
        PackageId = project.PackageId,
        PackageReferences = (project.PackageReferences ?? [])
            .Select(reference => new RepositorySyncPackageReferenceNotification { Name = reference.Name ?? "", Version = reference.Version })
            .ToList(),
    };
}
