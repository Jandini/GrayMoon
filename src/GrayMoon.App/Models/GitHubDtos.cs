using System.Text.Json.Serialization;

namespace GrayMoon.App.Models;

public class GitHubOrganizationDto
{
    [JsonPropertyName("login")]
    public string Login { get; set; } = string.Empty;

    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;
}

public class GitHubRepositoryOwnerDto
{
    [JsonPropertyName("login")]
    public string Login { get; set; } = string.Empty;
}

public class GitHubRepositoryDto
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("node_id")]
    public string NodeId { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("full_name")]
    public string FullName { get; set; } = string.Empty;

    [JsonPropertyName("private")]
    public bool Private { get; set; }

    [JsonPropertyName("archived")]
    public bool Archived { get; set; }

    [JsonPropertyName("clone_url")]
    public string CloneUrl { get; set; } = string.Empty;

    [JsonPropertyName("owner")]
    public GitHubRepositoryOwnerDto Owner { get; set; } = new();

    [JsonPropertyName("topics")]
    public List<string> Topics { get; set; } = new();
}

public class GitHubWorkflowDto
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    [JsonPropertyName("html_url")]
    public string HtmlUrl { get; set; } = string.Empty;

    [JsonPropertyName("state")]
    public string State { get; set; } = string.Empty;
}

public class GitHubWorkflowsResponse
{
    [JsonPropertyName("workflows")]
    public List<GitHubWorkflowDto> Workflows { get; set; } = new();
}

public class GitHubWorkflowRunDto
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("workflow_id")]
    public long WorkflowId { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("event")]
    public string Event { get; set; } = string.Empty;

    [JsonPropertyName("head_branch")]
    public string? HeadBranch { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("conclusion")]
    public string? Conclusion { get; set; }

    [JsonPropertyName("updated_at")]
    public DateTimeOffset? UpdatedAt { get; set; }

    [JsonPropertyName("html_url")]
    public string HtmlUrl { get; set; } = string.Empty;
}

public class GitHubWorkflowRunsResponse
{
    [JsonPropertyName("workflow_runs")]
    public List<GitHubWorkflowRunDto> WorkflowRuns { get; set; } = new();
}

/// <summary>GET /repos/{owner}/{repo}/actions/runs/{run_id}/jobs</summary>
public sealed class GitHubWorkflowJobsResponse
{
    [JsonPropertyName("total_count")]
    public int TotalCount { get; set; }

    [JsonPropertyName("jobs")]
    public List<GitHubWorkflowJobDto> Jobs { get; set; } = new();
}

public sealed class GitHubWorkflowJobDto
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("conclusion")]
    public string? Conclusion { get; set; }

    [JsonPropertyName("created_at")]
    public DateTimeOffset? CreatedAt { get; set; }

    [JsonPropertyName("started_at")]
    public DateTimeOffset? StartedAt { get; set; }

    [JsonPropertyName("steps")]
    public List<GitHubWorkflowJobStepDto> Steps { get; set; } = new();
}

public sealed class GitHubWorkflowJobStepDto
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("conclusion")]
    public string? Conclusion { get; set; }

    [JsonPropertyName("number")]
    public long Number { get; set; }

    [JsonPropertyName("started_at")]
    public DateTimeOffset? StartedAt { get; set; }

    [JsonPropertyName("completed_at")]
    public DateTimeOffset? CompletedAt { get; set; }
}

/// <summary>Single file from GET /repos/{owner}/{repo}/contents/{path}.</summary>
public sealed class GitHubContentResponse
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("encoding")]
    public string? Encoding { get; set; }

    [JsonPropertyName("content")]
    public string? Content { get; set; }
}

public sealed class GitHubUserDto
{
    [JsonPropertyName("login")]
    public string? Login { get; set; }

    /// <summary>"User" for human accounts, "Bot" for GitHub Apps and automation accounts.</summary>
    [JsonPropertyName("type")]
    public string? Type { get; set; }
}

/// <summary>Pull request list item from GET /repos/{owner}/{repo}/pulls.</summary>
public class GitHubPullRequestDto
{
    [JsonPropertyName("number")]
    public int Number { get; set; }

    [JsonPropertyName("state")]
    public string State { get; set; } = string.Empty;

    [JsonPropertyName("merged_at")]
    public DateTimeOffset? MergedAt { get; set; }

    [JsonPropertyName("html_url")]
    public string HtmlUrl { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("body")]
    public string? Body { get; set; }

    [JsonPropertyName("head")]
    public GitHubPullRequestHeadDto? Head { get; set; }

    [JsonPropertyName("mergeable")]
    public bool? Mergeable { get; set; }

    [JsonPropertyName("mergeable_state")]
    public string? MergeableState { get; set; }

    [JsonPropertyName("changed_files")]
    public int? ChangedFiles { get; set; }

    [JsonPropertyName("user")]
    public GitHubUserDto? User { get; set; }

    [JsonPropertyName("base")]
    public GitHubPullRequestHeadDto? Base { get; set; }

    [JsonPropertyName("requested_reviewers")]
    public List<GitHubUserDto>? RequestedReviewers { get; set; }

    [JsonPropertyName("requested_teams")]
    public List<GitHubTeamDto>? RequestedTeams { get; set; }

    [JsonPropertyName("draft")]
    public bool Draft { get; set; }
}

public class GitHubPullRequestHeadDto
{
    [JsonPropertyName("ref")]
    public string Ref { get; set; } = string.Empty;

    [JsonPropertyName("sha")]
    public string? Sha { get; set; }
}

/// <summary>Body for POST /repos/{owner}/{repo}/pulls.</summary>
public sealed class GitHubCreatePullRequestRequestDto
{
    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("head")]
    public string Head { get; set; } = string.Empty;

    [JsonPropertyName("base")]
    public string Base { get; set; } = string.Empty;

    [JsonPropertyName("body")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Body { get; set; }

    [JsonPropertyName("draft")]
    public bool Draft { get; set; }
}

/// <summary>Body for PATCH /repos/{owner}/{repo}/pulls/{pull_number} when only the title is changing.</summary>
public sealed class GitHubUpdatePullRequestTitleRequestDto
{
    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;
}

/// <summary>Body for POST /repos/{owner}/{repo}/pulls/{pull_number}/requested_reviewers.</summary>
public sealed class GitHubRequestReviewersRequestDto
{
    [JsonPropertyName("reviewers")]
    public List<string> Reviewers { get; set; } = new();

    [JsonPropertyName("team_reviewers")]
    public List<string> TeamReviewers { get; set; } = new();
}

/// <summary>Item from GET /repos/{owner}/{repo}/collaborators.</summary>
public sealed class GitHubCollaboratorDto
{
    [JsonPropertyName("login")]
    public string Login { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;
}

/// <summary>Item from GET /repos/{owner}/{repo}/teams.</summary>
public sealed class GitHubTeamDto
{
    [JsonPropertyName("slug")]
    public string Slug { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;
}

/// <summary>Subset of GET /repos/{owner}/{repo} used to determine which merge methods GitHub currently permits for this repository.</summary>
public sealed class GitHubRepositoryMergeSettingsDto
{
    [JsonPropertyName("allow_merge_commit")]
    public bool? AllowMergeCommit { get; set; }

    [JsonPropertyName("allow_squash_merge")]
    public bool? AllowSquashMerge { get; set; }

    [JsonPropertyName("allow_rebase_merge")]
    public bool? AllowRebaseMerge { get; set; }
}

/// <summary>Item from GET /repos/{owner}/{repo}/pulls/{pull_number}/reviews.</summary>
public sealed class GitHubPullRequestReviewDto
{
    [JsonPropertyName("user")]
    public GitHubUserDto? User { get; set; }

    /// <summary>APPROVED, CHANGES_REQUESTED, COMMENTED, DISMISSED, or PENDING.</summary>
    [JsonPropertyName("state")]
    public string State { get; set; } = string.Empty;

    [JsonPropertyName("body")]
    public string? Body { get; set; }

    [JsonPropertyName("submitted_at")]
    public DateTimeOffset? SubmittedAt { get; set; }
}

/// <summary>GET /repos/{owner}/{repo}/commits/{ref}/check-runs</summary>
public sealed class GitHubCheckRunsResponse
{
    [JsonPropertyName("total_count")]
    public int TotalCount { get; set; }

    [JsonPropertyName("check_runs")]
    public List<GitHubCheckRunDto> CheckRuns { get; set; } = new();
}

public sealed class GitHubCheckRunDto
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>queued, in_progress, completed.</summary>
    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    /// <summary>success, failure, neutral, cancelled, skipped, timed_out, action_required, or null while not completed.</summary>
    [JsonPropertyName("conclusion")]
    public string? Conclusion { get; set; }

    [JsonPropertyName("started_at")]
    public DateTimeOffset? StartedAt { get; set; }

    [JsonPropertyName("completed_at")]
    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>Browser URL for this check run. GitHub Actions jobs typically land on <c>/actions/runs/{runId}/job/{jobId}</c>.</summary>
    [JsonPropertyName("html_url")]
    public string? HtmlUrl { get; set; }
}

/// <summary>Body for PUT /repos/{owner}/{repo}/pulls/{pull_number}/merge.</summary>
public sealed class GitHubMergePullRequestRequestDto
{
    /// <summary>merge, squash, or rebase.</summary>
    [JsonPropertyName("merge_method")]
    public string MergeMethod { get; set; } = string.Empty;

    /// <summary>Expected head SHA. GitHub returns 409 if the branch has moved since this value was read, preventing an unintended merge of newer commits.</summary>
    [JsonPropertyName("sha")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Sha { get; set; }
}

/// <summary>Response from PUT /repos/{owner}/{repo}/pulls/{pull_number}/merge.</summary>
public sealed class GitHubMergePullRequestResponseDto
{
    [JsonPropertyName("sha")]
    public string? Sha { get; set; }

    [JsonPropertyName("merged")]
    public bool Merged { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }
}
