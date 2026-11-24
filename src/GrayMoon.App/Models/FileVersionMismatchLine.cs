namespace GrayMoon.App.Models;

/// <summary>
/// Describes a single file-version token that needs attention for a workspace repository: it is out of date, or the
/// check could not compare it (<paramref name="NotCheckedReason"/> says why).
/// </summary>
public sealed record FileVersionMismatchLine(
    string FileName,
    string TokenName,
    string CurrentValue,
    string ExpectedValue,
    string? NotCheckedReason = null)
{
    public bool IsNotChecked => !string.IsNullOrWhiteSpace(NotCheckedReason);
}
