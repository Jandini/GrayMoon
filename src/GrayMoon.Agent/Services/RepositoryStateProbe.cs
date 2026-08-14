using GrayMoon.Abstractions.Notifications;
using GrayMoon.Agent.Abstractions;
using GrayMoon.Agent.Models;

namespace GrayMoon.Agent.Services;

/// <inheritdoc cref="IRepositoryStateProbe" />
public sealed class RepositoryStateProbe(IGitService git, ICsProjFileService csProjFileService) : IRepositoryStateProbe
{
    public async Task<RepositoryStateCapture> CaptureAsync(string repoPath, RepositoryStateProbeOptions options, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(repoPath) || !git.DirectoryExists(repoPath))
            return new RepositoryStateCapture(new RepositoryStateSnapshot { ErrorMessage = options.ErrorMessage }, null);

        // Start the csproj scan first; it is IO-bound and independent of every git call below.
        var projectsTask = options.IncludeProjects ? csProjFileService.FindAsync(repoPath, ct) : null;

        var defaultRef = options.DefaultBranchOriginRef ?? await git.GetDefaultBranchOriginRefAsync(repoPath, ct);
        var defaultBranchName = await git.GetDefaultBranchNameAsync(repoPath, ct);

        string? branch;
        string? gitVersion = null;
        if (options.IncludeGitVersion)
        {
            var (versionResult, _) = await git.GetVersionAsync(repoPath, options.GitVersionNonNormalize, ct);
            gitVersion = versionResult?.InformationalVersion;
            branch = versionResult?.BranchName ?? versionResult?.EscapedBranchName;
        }
        else
        {
            branch = options.BranchNameOverride ?? await git.GetCurrentBranchNameAsync(repoPath, ct);
        }

        // A tag checkout is a detached HEAD, so there is no branch to report or count against.
        var currentTag = await git.GetCheckedOutTagAsync(repoPath, ct);
        if (currentTag != null)
            branch = null;
        else if (string.IsNullOrWhiteSpace(branch) || branch == "-")
            branch = options.BranchNameOverride ?? await git.GetCurrentBranchNameAsync(repoPath, ct);

        var hasBranch = currentTag == null && !string.IsNullOrWhiteSpace(branch) && branch != "-";

        CommitCountsProbeResult counts = CommitCountsProbeResult.Unknown;
        int? defaultBehind = null;
        int? defaultAhead = null;
        var vsDefaultProbed = false;
        if (hasBranch)
        {
            counts = await git.ProbeCommitCountsAsync(repoPath, branch!, defaultRef, ct);
            var (behind, ahead, _) = await git.GetCommitCountsVsDefaultAsync(repoPath, defaultRef, ct);
            defaultBehind = behind;
            defaultAhead = ahead;
            vsDefaultProbed = defaultRef != null;
        }

        List<string>? localBranches = null;
        List<string>? remoteBranches = null;
        List<string>? tags = null;
        var branchesProbed = false;
        if (options.IncludeBranchLists)
        {
            localBranches = [.. await git.GetLocalBranchesAsync(repoPath, ct)];
            remoteBranches = [.. await git.GetRemoteBranchesFromRefsAsync(repoPath, ct)];
            tags = [.. await git.GetTagsAsync(repoPath, ct)];
            branchesProbed = true;
        }
        else if (options.IncludeRemoteBranchesOnly)
        {
            remoteBranches = [.. await git.GetRemoteBranchesFromRefsAsync(repoPath, ct)];
        }

        IReadOnlyList<CsProjFileInfo>? rawProjects = null;
        List<RepositorySyncProjectNotification>? projects = null;
        if (projectsTask != null)
        {
            rawProjects = await projectsTask;
            projects = ToNotifications(rawProjects);
        }

        var snapshot = new RepositoryStateSnapshot
        {
            BranchName = hasBranch ? branch : null,
            CheckedOutTag = currentTag,
            GitVersion = gitVersion,
            DefaultBranchName = defaultBranchName,
            OutgoingCommits = counts.Outgoing,
            IncomingCommits = counts.Incoming,
            DefaultBranchBehind = defaultBehind,
            DefaultBranchAhead = defaultAhead,
            HasUpstream = hasBranch ? counts.HasUpstream : null,
            LocalBranches = localBranches,
            RemoteBranches = remoteBranches,
            Tags = tags,
            Projects = projects,
            ErrorMessage = options.ErrorMessage,
            IdentityProbed = true,
            GitVersionProbed = options.IncludeGitVersion && gitVersion != null,
            // On a tag there is nothing to count, and the app clears those columns from the tag state
            // itself, so reporting the group as probed keeps the two paths consistent.
            CommitCountsProbed = !hasBranch || (counts.CountsProbed && vsDefaultProbed),
            UpstreamProbed = !hasBranch || counts.UpstreamProbed,
            BranchesProbed = branchesProbed,
            ProjectsProbed = projects != null,
        };

        return new RepositoryStateCapture(snapshot, rawProjects);
    }

    /// <summary>Maps the agent's csproj model onto the wire shape shared with the app.</summary>
    public static List<RepositorySyncProjectNotification> ToNotifications(IReadOnlyList<CsProjFileInfo>? projects)
    {
        if (projects == null)
            return [];
        return projects
            .Where(p => !string.IsNullOrWhiteSpace(p.Name))
            .Select(p => new RepositorySyncProjectNotification
            {
                Name = p.Name.Trim(),
                ProjectType = (int)p.ProjectType,
                ProjectPath = p.ProjectPath ?? "",
                TargetFramework = p.TargetFramework ?? "",
                PackageId = p.PackageId,
                PackageReferences = p.PackageReferences
                    .Where(pr => !string.IsNullOrWhiteSpace(pr.Name))
                    .Select(pr => new RepositorySyncPackageReferenceNotification
                    {
                        Name = pr.Name.Trim(),
                        Version = pr.Version ?? ""
                    })
                    .ToList()
            })
            .ToList();
    }
}
