using System.Collections.Concurrent;
using GrayMoon.Abstractions.Agent;
using GrayMoon.Abstractions.Exceptions;
using GrayMoon.Abstractions.Notifications;
using GrayMoon.App.Data;
using GrayMoon.App.Hubs;
using GrayMoon.App.Models;
using GrayMoon.App.Models.Api;
using GrayMoon.App.Repositories;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace GrayMoon.App.Services.Git;

public sealed partial class WorkspaceGitService
{
    /// <summary>Maps the App's project model onto the wire shape carried by <see cref="RepositoryStateSnapshot"/>. An empty (not null) input stays empty, since a probed empty scan is meaningful.</summary>
    private static List<RepositorySyncProjectNotification>? ToProjectNotifications(IReadOnlyList<SyncProjectInfo>? projects)
    {
        if (projects == null)
            return null;
        return projects
            .Select(p => new RepositorySyncProjectNotification
            {
                Name = p.ProjectName,
                ProjectType = (int)p.ProjectType,
                ProjectPath = p.ProjectFilePath,
                TargetFramework = p.TargetFramework,
                PackageId = p.PackageId,
                PackageReferences = (p.PackageReferences ?? [])
                    .Select(pr => new RepositorySyncPackageReferenceNotification { Name = pr.Name, Version = pr.Version })
                    .ToList()
            })
            .ToList();
    }

    private static RepoGitVersionInfo ParseSyncRepositoryResponse(AgentCommandResponse response)
    {
        if (!response.Success || response.Data == null)
            return new RepoGitVersionInfo { Version = "-", Branch = "-", ErrorMessage = response.Error ?? "Sync failed" };

        var (version, branch, tag, gitVersionError, gitFetchError, commandSucceeded) = GetVersionBranch(response.Data);
        var projectsCount = GetProjects(response.Data);
        var projectsDetail = GetProjectsDetail(response.Data);
        var (outgoingCommits, incomingCommits, defaultBehind, defaultAhead) = GetCommitCounts(response.Data);
        var (localBranches, remoteBranches, defaultBranch, tags, currentTag) = GetBranches(response.Data);
        var (hasUpstream, upstreamProbed) = GetUpstream(response.Data);
        // Prefer Tag from the top-level response, fall back to currentTag from the branches block.
        var resolvedTag = !string.IsNullOrWhiteSpace(tag) ? tag : currentTag;
        var combinedError = CombineRepoErrors(gitFetchError, gitVersionError);

        // The transport envelope only says the command ran; the payload says whether it did its job.
        // When it bailed out (a failed fetch, say) every field below is absent rather than genuinely
        // null, so nothing is marked probed and no column gets cleared on the strength of a null.
        var probed = commandSucceeded;
        var onTag = !string.IsNullOrWhiteSpace(resolvedTag);

        return new RepoGitVersionInfo
        {
            Version = version,
            Branch = branch,
            Tag = resolvedTag,
            Tags = tags,
            Projects = projectsCount,
            ProjectsDetail = projectsDetail,
            OutgoingCommits = outgoingCommits,
            IncomingCommits = incomingCommits,
            DefaultBranchBehindCommits = defaultBehind,
            DefaultBranchAheadCommits = defaultAhead,
            HasUpstream = hasUpstream,
            LocalBranches = localBranches,
            RemoteBranches = remoteBranches,
            DefaultBranch = defaultBranch,
            ErrorMessage = combinedError,
            Snapshot = new RepositoryStateSnapshot
            {
                BranchName = branch,
                CheckedOutTag = resolvedTag,
                GitVersion = version == "-" ? null : version,
                DefaultBranchName = defaultBranch,
                OutgoingCommits = outgoingCommits,
                IncomingCommits = incomingCommits,
                DefaultBranchBehind = defaultBehind,
                DefaultBranchAhead = defaultAhead,
                HasUpstream = onTag ? null : hasUpstream,
                LocalBranches = localBranches?.ToList(),
                RemoteBranches = remoteBranches?.ToList(),
                Tags = tags?.ToList(),
                Projects = HasProjectsBlock(response.Data) ? ToProjectNotifications(projectsDetail) ?? [] : null,
                ErrorMessage = combinedError,
                IdentityProbed = probed,
                GitVersionProbed = probed && version != "-",
                CommitCountsProbed = probed && !onTag,
                UpstreamProbed = probed && !onTag && upstreamProbed,
                BranchesProbed = probed && localBranches != null,
                ProjectsProbed = probed && HasProjectsBlock(response.Data),
            }
        };
    }

    private static RepoGitVersionInfo ParseRefreshRepositoryVersionResponse(AgentCommandResponse response)
    {
        if (!response.Success || response.Data == null)
            return new RepoGitVersionInfo { Version = "-", Branch = "-" };

        var (version, branch, tag, gitVersionError, gitFetchError, _) = GetVersionBranch(response.Data);
        var (outgoingCommits, incomingCommits, defaultBehind, defaultAhead) = GetCommitCounts(response.Data);
        var (hasUpstream, remoteBranches, localBranches) = GetRefreshBranchesAndUpstream(response.Data);
        var combinedError = CombineRepoErrors(gitFetchError, gitVersionError);
        var onTag = !string.IsNullOrWhiteSpace(tag);
        return new RepoGitVersionInfo
        {
            Version = version,
            Branch = branch,
            Tag = tag,
            OutgoingCommits = outgoingCommits,
            IncomingCommits = incomingCommits,
            DefaultBranchBehindCommits = defaultBehind,
            DefaultBranchAheadCommits = defaultAhead,
            RemoteBranches = remoteBranches,
            LocalBranches = localBranches,
            ErrorMessage = combinedError,
            Snapshot = new RepositoryStateSnapshot
            {
                BranchName = branch,
                CheckedOutTag = tag,
                GitVersion = version == "-" ? null : version,
                OutgoingCommits = outgoingCommits,
                IncomingCommits = incomingCommits,
                DefaultBranchBehind = defaultBehind,
                DefaultBranchAhead = defaultAhead,
                // When pinned to a tag there is no branch to compare against.
                HasUpstream = onTag ? null : hasUpstream,
                RemoteBranches = remoteBranches?.ToList(),
                LocalBranches = localBranches?.ToList(),
                ErrorMessage = combinedError,
                IdentityProbed = true,
                GitVersionProbed = version != "-",
                CommitCountsProbed = !onTag,
                UpstreamProbed = !onTag && hasUpstream.HasValue,
                // This command lists branches but not tags, so it must not replace the persisted refs.
                BranchesProbed = false,
                ProjectsProbed = false,
            }
        };
    }

    /// <summary>Reads the agent's git-config upstream answer plus whether it actually resolved it, so an agent that omits both leaves the persisted flag alone.</summary>
    private static (bool? HasUpstream, bool UpstreamProbed) GetUpstream(object data)
    {
        var r = AgentResponseJson.DeserializeAgentResponse<AgentVersionBranchResponse>(data);
        return (r?.HasUpstream, r?.UpstreamProbed ?? false);
    }

    private static (bool? HasUpstream, IReadOnlyList<string>? RemoteBranches, IReadOnlyList<string>? LocalBranches) GetRefreshBranchesAndUpstream(object data)
    {
        var r = AgentResponseJson.DeserializeAgentResponse<AgentVersionBranchResponse>(data);
        var remote = r?.RemoteBranches?.Where(b => !string.IsNullOrWhiteSpace(b)).ToList();
        var local = r?.LocalBranches?.Where(b => !string.IsNullOrWhiteSpace(b)).ToList();
        return (r?.HasUpstream, remote?.Count > 0 ? remote : null, local?.Count > 0 ? local : null);
    }

    private static (string version, string branch, string? tag, string? gitVersionError, string? gitFetchError, bool commandSucceeded) GetVersionBranch(object data)
    {
        var r = AgentResponseJson.DeserializeAgentResponse<AgentVersionBranchResponse>(data);
        // Commands that do not report their own result are treated as successful, which is what they were before.
        var commandSucceeded = r?.Success ?? true;
        return (r?.Version ?? "-", r?.Branch ?? "-", string.IsNullOrWhiteSpace(r?.Tag) ? null : r!.Tag, r?.GitVersionError, r?.GitFetchError, commandSucceeded);
    }

    private static string? CombineRepoErrors(string? fetchError, string? versionError)
    {
        if (string.IsNullOrWhiteSpace(fetchError) && string.IsNullOrWhiteSpace(versionError))
            return null;
        if (string.IsNullOrWhiteSpace(fetchError))
            return versionError;
        if (string.IsNullOrWhiteSpace(versionError))
            return fetchError;
        return $"{fetchError.Trim()}. {versionError.Trim()}";
    }

    private static int? GetProjects(object data)
    {
        var r = AgentResponseJson.DeserializeAgentResponse<AgentSyncProjectsResponse>(data);
        var projects = r?.Projects;
        if (projects == null) return null;
        return projects.Count > 0 ? projects.Count : null;
    }

    private static (int? Outgoing, int? Incoming, int? DefaultBehind, int? DefaultAhead) GetCommitCounts(object data)
    {
        var r = AgentResponseJson.DeserializeAgentResponse<AgentCommitCountsResponse>(data);
        return (r?.OutgoingCommits, r?.IncomingCommits, r?.DefaultBranchBehind, r?.DefaultBranchAhead);
    }

    private static (IReadOnlyList<string>? LocalBranches, IReadOnlyList<string>? RemoteBranches, string? DefaultBranch, IReadOnlyList<string>? Tags, string? CurrentTag) GetBranches(object data)
    {
        var r = AgentResponseJson.DeserializeAgentResponse<AgentBranchesResponse>(data);
        var local = r?.LocalBranches?.Where(b => !string.IsNullOrWhiteSpace(b)).ToList();
        var remote = r?.RemoteBranches?.Where(b => !string.IsNullOrWhiteSpace(b)).ToList();
        var defaultBranch = !string.IsNullOrWhiteSpace(r?.DefaultBranch) ? r.DefaultBranch : null;
        var tags = r?.Tags?.Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
        var currentTag = !string.IsNullOrWhiteSpace(r?.CurrentTag) ? r.CurrentTag : null;
        return (local, remote, defaultBranch, tags, currentTag);
    }

    private static ProjectType? ComputeRepositoryType(IReadOnlyList<SyncProjectInfo>? projects)
    {
        if (projects == null || projects.Count == 0) return null;
        if (projects.Any(p => p.ProjectType == ProjectType.Service)) return ProjectType.Service;
        if (projects.Any(p => p.ProjectType == ProjectType.Package)) return ProjectType.Package;
        if (projects.Any(p => p.ProjectType == ProjectType.Executable)) return ProjectType.Executable;
        if (projects.Any(p => p.ProjectType == ProjectType.Library)) return ProjectType.Library;
        return ProjectType.Test;
    }

    private static IReadOnlyList<SyncProjectInfo>? GetProjectsDetail(object data)
    {
        var r = AgentResponseJson.DeserializeAgentResponse<AgentSyncProjectsResponse>(data);
        return GetProjectsDetail(r?.Projects);
    }

    /// <summary>
    /// Whether the response carried a project list at all. <see cref="GetProjectsDetail(object)"/> folds an
    /// empty list into null, which cannot be told apart from "the agent never scanned"; a probe marker needs
    /// exactly that distinction.
    /// </summary>
    private static bool HasProjectsBlock(object data)
        => AgentResponseJson.DeserializeAgentResponse<AgentSyncProjectsResponse>(data)?.Projects != null;

    private static IReadOnlyList<SyncProjectInfo>? GetProjectsDetail(List<AgentProjectDto>? projects)
    {
        if (projects == null || projects.Count == 0) return null;
        var list = new List<SyncProjectInfo>();
        foreach (var p in projects)
        {
            if (string.IsNullOrWhiteSpace(p.Name)) continue;
            var projectType = p.ProjectType >= 0 && p.ProjectType <= 4 ? (ProjectType)p.ProjectType : ProjectType.Library;
            var packageRefs = (p.PackageReferences ?? new List<AgentPackageRefDto>())
                .Where(pr => !string.IsNullOrWhiteSpace(pr.Name))
                .Select(pr => new SyncPackageReference(pr.Name!.Trim(), pr.Version ?? ""))
                .ToList();
            list.Add(new SyncProjectInfo(
                p.Name,
                projectType,
                p.ProjectPath ?? "",
                p.TargetFramework ?? "",
                p.PackageId,
                packageRefs));
        }
        return list.Count > 0 ? list : null;
    }

    private static RepoSyncStatus ParseGetRepositoryVersionToStatus(object data, string? persistedVersion, string? persistedBranch)
    {
        var r = AgentResponseJson.DeserializeAgentResponse<AgentGetRepositoryVersionResponse>(data);
        if (r == null || !r.Exists)
            return RepoSyncStatus.NotCloned;
        if (string.IsNullOrEmpty(r.Version) || string.IsNullOrEmpty(r.Branch))
            return RepoSyncStatus.VersionMismatch;
        return (r.Version == persistedVersion && r.Branch == persistedBranch) ? RepoSyncStatus.InSync : RepoSyncStatus.VersionMismatch;
    }
}
