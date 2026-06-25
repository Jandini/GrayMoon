using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace GrayMoon.App.Models;

/// <summary>One row per configured version file per workspace repository. Stores aggregate line counts so the UI can display per-file "X of Y" summaries without storing every individual token value.</summary>
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

    /// <summary>Total number of pattern lines that matched in this file.</summary>
    public int TotalMatchedLines { get; set; }

    /// <summary>Number of matched lines whose current value differs from the expected repo GitVersion.</summary>
    public int OutOfDateLines { get; set; }
}
