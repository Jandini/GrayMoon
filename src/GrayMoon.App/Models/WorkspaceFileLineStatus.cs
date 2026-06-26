using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace GrayMoon.App.Models;

/// <summary>One row per out-of-date file-config repository token on a dependent repo. Powers dependency badge tooltips.</summary>
[Table("WorkspaceFileLineStatuses")]
public sealed class WorkspaceFileLineStatus
{
    public int StatusId { get; set; }
    public int WorkspaceId { get; set; }
    public int RepositoryId { get; set; }

    [MaxLength(2000)]
    public string FilePath { get; set; } = "";

    [MaxLength(260)]
    public string FileName { get; set; } = "";

    /// <summary>Referenced workspace repository name (file-config token).</summary>
    [MaxLength(260)]
    public string TokenName { get; set; } = "";

    [MaxLength(100)]
    public string? CurrentValue { get; set; }

    [MaxLength(100)]
    public string? ExpectedValue { get; set; }

    /// <summary>Legacy aggregate fields; retained for schema compatibility.</summary>
    public int TotalMatchedLines { get; set; }

    /// <summary>Legacy aggregate fields; retained for schema compatibility.</summary>
    public int OutOfDateLines { get; set; }
}
