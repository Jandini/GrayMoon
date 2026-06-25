using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace GrayMoon.App.Models;

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

    [MaxLength(200)]
    public string TokenName { get; set; } = "";

    public string? CurrentValue { get; set; }
    public string? ExpectedValue { get; set; }
}
