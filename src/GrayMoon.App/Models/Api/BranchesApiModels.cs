using System.Text.Json.Serialization;

namespace GrayMoon.App.Models.Api;

/// <summary>Response from GET /api/branches/get and POST /api/branches/refresh.</summary>
public sealed class BranchesResponse
{
    [JsonPropertyName("localBranches")]
    public List<string> LocalBranches { get; set; } = new();

    [JsonPropertyName("remoteBranches")]
    public List<string> RemoteBranches { get; set; } = new();

    [JsonPropertyName("currentBranch")]
    public string? CurrentBranch { get; set; }

    [JsonPropertyName("defaultBranch")]
    public string? DefaultBranch { get; set; }

    /// <summary>Tag names available in the repository. Empty when no tags are present.</summary>
    [JsonPropertyName("tags")]
    public List<string> Tags { get; set; } = new();

    /// <summary>Tag name HEAD is currently checked out at (detached HEAD on a tag). Null when on a branch.</summary>
    [JsonPropertyName("currentTag")]
    public string? CurrentTag { get; set; }

    /// <summary>Present on refresh response; when false, ErrorMessage describes the failure (e.g. fetch failed).</summary>
    [JsonPropertyName("success")]
    public bool? Success { get; set; }

    [JsonPropertyName("errorMessage")]
    public string? ErrorMessage { get; set; }

    /// <summary>Whether the checked-out branch has a configured upstream, read from git config rather than matched by name against the remote list. Null from workers that predate this field.</summary>
    [JsonPropertyName("hasUpstream")]
    public bool? HasUpstream { get; set; }

    /// <summary>False when the worker could not determine the upstream (or predates the field), in which case <see cref="HasUpstream"/> must not overwrite persisted state.</summary>
    [JsonPropertyName("upstreamProbed")]
    public bool UpstreamProbed { get; set; }
}

/// <summary>Response from POST /api/branches/checkout. Worker may send PascalCase; use case-insensitive deserialization.</summary>
public sealed class CheckoutBranchResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("currentBranch")]
    public string? CurrentBranch { get; set; }

    [JsonPropertyName("errorMessage")]
    public string? ErrorMessage { get; set; }

    /// <summary>Full sha of HEAD after a successful checkout. Null from workers that predate it.</summary>
    [JsonPropertyName("commit")]
    public string? Commit { get; set; }

    /// <summary>Git refused the checkout because local changes would be overwritten. Nothing was changed.</summary>
    [JsonPropertyName("localChangesBlockCheckout")]
    public bool LocalChangesBlockCheckout { get; set; }
}

/// <summary>Worker response for CheckoutTag command (camelCase).</summary>
public sealed class CheckoutTagResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("currentTag")]
    public string? CurrentTag { get; set; }

    /// <summary>Full commit hash HEAD points at after the checkout.</summary>
    [JsonPropertyName("commit")]
    public string? Commit { get; set; }

    [JsonPropertyName("errorMessage")]
    public string? ErrorMessage { get; set; }
}

/// <summary>Worker CreateBranch response (camelCase).</summary>
public sealed class CreateBranchResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("currentBranch")]
    public string? CurrentBranch { get; set; }

    [JsonPropertyName("errorMessage")]
    public string? ErrorMessage { get; set; }

    // Populated only when SkipHooks=true (inline sync path)
    [JsonPropertyName("version")]
    public string? Version { get; set; }

    [JsonPropertyName("branch")]
    public string? Branch { get; set; }

    [JsonPropertyName("tag")]
    public string? Tag { get; set; }

    [JsonPropertyName("outgoingCommits")]
    public int? OutgoingCommits { get; set; }

    [JsonPropertyName("incomingCommits")]
    public int? IncomingCommits { get; set; }

    [JsonPropertyName("hasUpstream")]
    public bool? HasUpstream { get; set; }

    [JsonPropertyName("defaultBranchBehind")]
    public int? DefaultBranchBehind { get; set; }

    [JsonPropertyName("defaultBranchAhead")]
    public int? DefaultBranchAhead { get; set; }

    [JsonPropertyName("fetchError")]
    public string? FetchError { get; set; }

    /// <summary>True when a local branch of that name was already there: it was checked out as it is and the requested base was not used.</summary>
    [JsonPropertyName("alreadyExisted")]
    public bool AlreadyExisted { get; set; }

    /// <summary>The ref the branch was created from; null when it already existed or the repository has no commits.</summary>
    [JsonPropertyName("resolvedStartRef")]
    public string? ResolvedStartRef { get; set; }

    /// <summary>The commit the new branch starts on; null when it already existed or the repository has no commits.</summary>
    [JsonPropertyName("baseCommitSha")]
    public string? BaseCommitSha { get; set; }

    /// <summary>The commit checked out after the command, also when the branch already existed; null when the repository has no commits.</summary>
    [JsonPropertyName("headCommitSha")]
    public string? HeadCommitSha { get; set; }
}

/// <summary>Worker DeleteBranch response (camelCase).</summary>
public sealed class DeleteBranchResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("errorMessage")]
    public string? ErrorMessage { get; set; }
}

/// <summary>Worker SetUpstreamBranch response (camelCase).</summary>
public sealed class SetUpstreamBranchResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("errorMessage")]
    public string? ErrorMessage { get; set; }
}

/// <summary>Worker ReturnToDefaultBranch response (camelCase). Used to parse worker response.Data.</summary>
public sealed class ReturnToDefaultBranchResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("currentBranch")]
    public string? CurrentBranch { get; set; }

    [JsonPropertyName("defaultBranch")]
    public string? DefaultBranch { get; set; }

    [JsonPropertyName("errorMessage")]
    public string? ErrorMessage { get; set; }

    [JsonPropertyName("localBranches")]
    public List<string>? LocalBranches { get; set; }

    [JsonPropertyName("remoteBranches")]
    public List<string>? RemoteBranches { get; set; }

    [JsonPropertyName("tags")]
    public List<string>? Tags { get; set; }

    [JsonPropertyName("currentTag")]
    public string? CurrentTag { get; set; }

    [JsonPropertyName("outgoingCommits")]
    public int? OutgoingCommits { get; set; }

    [JsonPropertyName("incomingCommits")]
    public int? IncomingCommits { get; set; }

    [JsonPropertyName("hasUpstream")]
    public bool? HasUpstream { get; set; }

    [JsonPropertyName("defaultBranchBehind")]
    public int? DefaultBranchBehind { get; set; }

    [JsonPropertyName("defaultBranchAhead")]
    public int? DefaultBranchAhead { get; set; }

    [JsonPropertyName("gitVersion")]
    public string? GitVersion { get; set; }

    [JsonPropertyName("projects")]
    public List<WorkerProjectDto>? Projects { get; set; }

    /// <summary>Authoritative post-checkout state with per-group probe markers. Null from workers that predate this field.</summary>
    [JsonPropertyName("state")]
    public GrayMoon.Abstractions.Notifications.RepositoryStateSnapshot? State { get; set; }

    /// <summary>Set when the remote branch delete was asked for and failed; the return itself carried on.</summary>
    [JsonPropertyName("remoteBranchDeleteError")]
    public string? RemoteBranchDeleteError { get; set; }

    /// <summary>Set when the local branch delete failed; the return itself carried on.</summary>
    [JsonPropertyName("localBranchDeleteError")]
    public string? LocalBranchDeleteError { get; set; }
}

/// <summary>
/// API response for POST /api/branches/return-to-default (camelCase). <see cref="Warning"/> is set on a success whose
/// remote or local branch could not be deleted: the repository is on the default branch and the branch is still there.
/// </summary>
public sealed record ReturnToDefaultApiResult(
    [property: JsonPropertyName("success")] bool Success,
    [property: JsonPropertyName("warning")] string? Warning);

/// <summary>API response for POST /api/branches/delete (camelCase).</summary>
public sealed class DeleteBranchApiResult
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }
}

/// <summary>API response for POST /api/branches/create and /api/branches/set-upstream (camelCase).</summary>
public sealed class CreateBranchApiResult
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    /// <summary>
    /// True on a success that created nothing: a local branch of that name was already there and was checked out as
    /// it is, so the requested base was not used. Always false for set-upstream.
    /// </summary>
    [JsonPropertyName("alreadyExisted")]
    public bool AlreadyExisted { get; set; }

    /// <summary>
    /// Set on a success with a problem that did not undo it: the branch was created but where it started could not be
    /// recorded, so it will be pushed like a branch GrayMoon did not create.
    /// </summary>
    [JsonPropertyName("warning")]
    public string? Warning { get; set; }
}

/// <summary>Worker StageAndCommit response (camelCase).</summary>
public sealed class StageAndCommitResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("committed")]
    public bool Committed { get; set; }

    [JsonPropertyName("errorMessage")]
    public string? ErrorMessage { get; set; }
}

/// <summary>Worker SyncRepositoryDependencies response (camelCase).</summary>
public sealed class SyncRepositoryDependenciesResponse
{
    [JsonPropertyName("updatedCount")]
    public int UpdatedCount { get; set; }
}

/// <summary>Worker PushRepository response (camelCase).</summary>
public sealed class PushRepositoryResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("errorMessage")]
    public string? ErrorMessage { get; set; }
}

/// <summary>Response from POST /api/branches/common (camelCase).</summary>
public sealed class CommonBranchesApiResult
{
    [JsonPropertyName("commonBranchNames")]
    public List<string>? CommonBranchNames { get; set; }

    [JsonPropertyName("commonLocalBranchNames")]
    public List<string>? CommonLocalBranchNames { get; set; }

    [JsonPropertyName("commonRemoteBranchNames")]
    public List<string>? CommonRemoteBranchNames { get; set; }

    /// <summary>Display text for the default option: branch name when all repos share same default (e.g. "main"), or "multiple" when they differ.</summary>
    [JsonPropertyName("defaultDisplayText")]
    public string? DefaultDisplayText { get; set; }
}
