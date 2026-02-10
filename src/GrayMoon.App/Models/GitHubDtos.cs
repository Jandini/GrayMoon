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
}

public class GitHubWorkflowDto
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

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

/// <summary>Response from GET /repos/{owner}/{repo}/commits/{ref}/status (combined commit status).</summary>
public class GitHubCombinedStatusResponse
{
    [JsonPropertyName("state")]
    public string State { get; set; } = string.Empty;

    [JsonPropertyName("statuses")]
    public List<GitHubCommitStatusDto> Statuses { get; set; } = new();

    [JsonPropertyName("sha")]
    public string? Sha { get; set; }
}

public class GitHubCommitStatusDto
{
    [JsonPropertyName("state")]
    public string State { get; set; } = string.Empty;

    [JsonPropertyName("context")]
    public string? Context { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("target_url")]
    public string? TargetUrl { get; set; }
}

/// <summary>Response from GET /repos/{owner}/{repo}/commits/{ref}/check-suites.</summary>
public class GitHubCheckSuitesResponse
{
    [JsonPropertyName("total_count")]
    public int TotalCount { get; set; }

    [JsonPropertyName("check_suites")]
    public List<GitHubCheckSuiteDto> CheckSuites { get; set; } = new();
}

public class GitHubCheckSuiteDto
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("conclusion")]
    public string? Conclusion { get; set; }
}

/// <summary>Response from GET /repos/{owner}/{repo}/commits/{ref}/check-runs or .../check-suites/{id}/check-runs.</summary>
public class GitHubCheckRunsResponse
{
    [JsonPropertyName("total_count")]
    public int TotalCount { get; set; }

    [JsonPropertyName("check_runs")]
    public List<GitHubCheckRunDto> CheckRuns { get; set; } = new();
}

public class GitHubCheckRunDto
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("conclusion")]
    public string? Conclusion { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("html_url")]
    public string? HtmlUrl { get; set; }
}
