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

    [JsonPropertyName("head")]
    public GitHubPullRequestHeadDto? Head { get; set; }

    [JsonPropertyName("mergeable")]
    public bool? Mergeable { get; set; }

    [JsonPropertyName("mergeable_state")]
    public string? MergeableState { get; set; }

    [JsonPropertyName("changed_files")]
    public int? ChangedFiles { get; set; }
}

public class GitHubPullRequestHeadDto
{
    [JsonPropertyName("ref")]
    public string Ref { get; set; } = string.Empty;
}
