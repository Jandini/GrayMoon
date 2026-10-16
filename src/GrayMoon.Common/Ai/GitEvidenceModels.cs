using System.Text.Json.Serialization;

namespace GrayMoon.Common.Ai;

/// <summary>What the evidence is collected for.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<GitEvidenceKind>))]
public enum GitEvidenceKind
{
    /// <summary>One workspace commit: each repository's HEAD against its index (Staged) or working tree (All).</summary>
    Commit,

    /// <summary>One shared pull request: each repository's source branch against its selected target branch.</summary>
    PullRequest,
}

/// <summary>Which changes a workspace commit takes, mirroring Commit Staged / Commit All.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<GitCommitEvidenceMode>))]
public enum GitCommitEvidenceMode
{
    /// <summary>HEAD to index only. Unstaged edits are not part of the commit, even in a partly staged file.</summary>
    Staged,

    /// <summary>HEAD to the final working tree, untracked files included: what <c>git add --all</c> then commit records.</summary>
    All,
}

[JsonConverter(typeof(JsonStringEnumConverter<GitEvidenceChange>))]
public enum GitEvidenceChange
{
    Added,
    Modified,
    Deleted,
    Renamed,
    Copied,
    TypeChanged,
    Unmerged,
}

/// <summary>Deterministic file classification used to rank evidence and describe what was left out.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<GitEvidenceFileCategory>))]
public enum GitEvidenceFileCategory
{
    Source,
    Test,
    Project,
    Ci,
    Config,
    Docs,
    Other,
    Asset,
    Lock,
    Generated,
}

/// <summary>Caps the Worker applies while collecting, before anything large is read or sent to the App.</summary>
public sealed class GitEvidenceLimits
{
    /// <summary>Changed files listed per repository (highest ranked first); the rest are only counted.</summary>
    [JsonPropertyName("maxInventoryFiles")]
    public int MaxInventoryFiles { get; set; } = 200;

    /// <summary>Patch text kept per repository across all its files.</summary>
    [JsonPropertyName("maxPatchBytesPerRepository")]
    public int MaxPatchBytesPerRepository { get; set; } = 6_000;

    /// <summary>Patch text kept per file.</summary>
    [JsonPropertyName("maxPatchBytesPerFile")]
    public int MaxPatchBytesPerFile { get; set; } = 1_500;

    /// <summary>Raw diff output read per git process before it is stopped; protects memory for huge changes.</summary>
    [JsonPropertyName("maxRawDiffBytes")]
    public int MaxRawDiffBytes { get; set; } = 1_048_576;

    /// <summary>Bytes read from one untracked file (Commit All).</summary>
    [JsonPropertyName("maxUntrackedReadBytes")]
    public int MaxUntrackedReadBytes { get; set; } = 8_192;

    /// <summary>Pull request: commit subjects listed per repository.</summary>
    [JsonPropertyName("maxCommitSubjects")]
    public int MaxCommitSubjects { get; set; } = 30;

    /// <summary>Commit: recent HEAD subjects per repository, as a style reference.</summary>
    [JsonPropertyName("maxRecentSubjects")]
    public int MaxRecentSubjects { get; set; } = 5;
}

/// <summary>One repository the App asks evidence for. Branches are set for <see cref="GitEvidenceKind.PullRequest"/> only.</summary>
public sealed class GitEvidenceRepositoryScope
{
    [JsonPropertyName("repositoryId")]
    public int RepositoryId { get; set; }

    [JsonPropertyName("repositoryName")]
    public string? RepositoryName { get; set; }

    /// <summary>Local source branch (short name) the pull request is created from.</summary>
    [JsonPropertyName("headBranch")]
    public string? HeadBranch { get; set; }

    /// <summary>Selected target branch (short name on <c>origin</c>) the pull request is created against.</summary>
    [JsonPropertyName("baseBranch")]
    public string? BaseBranch { get; set; }
}

/// <summary>Error codes of <see cref="RepositoryGitEvidence.ErrorCode"/>.</summary>
public static class GitEvidenceErrorCodes
{
    public const string RepositoryNotFound = "RepositoryNotFound";
    public const string InvalidBranchName = "InvalidBranchName";
    public const string SourceBranchNotFound = "SourceBranchNotFound";
    public const string TargetBranchNotFound = "TargetBranchNotFound";
    public const string NoMergeBase = "NoMergeBase";
    public const string NoCommitsAhead = "NoCommitsAhead";
    public const string GitFailed = "GitFailed";
}

/// <summary>
/// Compact, bounded facts about one repository's change. Never contains whole files: patches are trimmed hunks under the
/// <see cref="GitEvidenceLimits"/>, and untracked files contribute a short excerpt at most.
/// </summary>
public sealed class RepositoryGitEvidence
{
    [JsonPropertyName("repositoryId")]
    public int RepositoryId { get; set; }

    [JsonPropertyName("repositoryName")]
    public string RepositoryName { get; set; } = string.Empty;

    [JsonPropertyName("success")]
    public bool Success { get; set; } = true;

    [JsonPropertyName("errorCode")]
    public string? ErrorCode { get; set; }

    [JsonPropertyName("errorMessage")]
    public string? ErrorMessage { get; set; }

    /// <summary>Commit: the checked-out branch; pull request: the source branch.</summary>
    [JsonPropertyName("headRef")]
    public string? HeadRef { get; set; }

    [JsonPropertyName("headOid")]
    public string? HeadOid { get; set; }

    /// <summary>Pull request: the selected target, as compared (<c>origin/&lt;branch&gt;</c>).</summary>
    [JsonPropertyName("baseRef")]
    public string? BaseRef { get; set; }

    [JsonPropertyName("baseOid")]
    public string? BaseOid { get; set; }

    [JsonPropertyName("mergeBaseOid")]
    public string? MergeBaseOid { get; set; }

    [JsonPropertyName("isUnbornHead")]
    public bool IsUnbornHead { get; set; }

    /// <summary>Pull request: commits on the source branch that the target does not have.</summary>
    [JsonPropertyName("commitCount")]
    public int CommitCount { get; set; }

    /// <summary>Pull request: subjects of those commits (merges left out), newest first, capped.</summary>
    [JsonPropertyName("commitSubjects")]
    public List<string> CommitSubjects { get; set; } = [];

    /// <summary>Commit: recent subjects on HEAD, only as a style reference.</summary>
    [JsonPropertyName("recentSubjects")]
    public List<string> RecentSubjects { get; set; } = [];

    [JsonPropertyName("totalFiles")]
    public int TotalFiles { get; set; }

    [JsonPropertyName("totalAdded")]
    public int TotalAdded { get; set; }

    [JsonPropertyName("totalDeleted")]
    public int TotalDeleted { get; set; }

    /// <summary>Changed files, highest ranked first, capped at <see cref="GitEvidenceLimits.MaxInventoryFiles"/>.</summary>
    [JsonPropertyName("files")]
    public List<GitEvidenceFile> Files { get; set; } = [];

    /// <summary>Files beyond the inventory cap, counted per category so the prompt can say what was left out.</summary>
    [JsonPropertyName("omittedFilesByCategory")]
    public Dictionary<GitEvidenceFileCategory, int> OmittedFilesByCategory { get; set; } = [];

    [JsonPropertyName("conflictedPaths")]
    public List<string> ConflictedPaths { get; set; } = [];

    /// <summary>True when a diff stopped at <see cref="GitEvidenceLimits.MaxRawDiffBytes"/>, so later patches are missing.</summary>
    [JsonPropertyName("patchReadTruncated")]
    public bool PatchReadTruncated { get; set; }
}

public sealed class GitEvidenceFile
{
    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    [JsonPropertyName("oldPath")]
    public string? OldPath { get; set; }

    [JsonPropertyName("change")]
    public GitEvidenceChange Change { get; set; }

    /// <summary>Added lines; null for binary files.</summary>
    [JsonPropertyName("added")]
    public int? Added { get; set; }

    /// <summary>Deleted lines; null for binary files.</summary>
    [JsonPropertyName("deleted")]
    public int? Deleted { get; set; }

    [JsonPropertyName("category")]
    public GitEvidenceFileCategory Category { get; set; }

    [JsonPropertyName("isBinary")]
    public bool IsBinary { get; set; }

    [JsonPropertyName("isSubmodule")]
    public bool IsSubmodule { get; set; }

    [JsonPropertyName("isSymlink")]
    public bool IsSymlink { get; set; }

    [JsonPropertyName("isUntracked")]
    public bool IsUntracked { get; set; }

    /// <summary>Likely to hold secrets (keys, certificates, .env); its content is never read.</summary>
    [JsonPropertyName("isSensitive")]
    public bool IsSensitive { get; set; }

    /// <summary>Trimmed unified-diff hunks (or an excerpt of an untracked file as added lines). Null when not collected.</summary>
    [JsonPropertyName("patch")]
    public string? Patch { get; set; }

    /// <summary>The file changed more than <see cref="Patch"/> shows.</summary>
    [JsonPropertyName("patchTruncated")]
    public bool PatchTruncated { get; set; }

    [JsonIgnore]
    public int Churn => (Added ?? 0) + (Deleted ?? 0);
}
