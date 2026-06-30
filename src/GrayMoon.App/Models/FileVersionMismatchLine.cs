namespace GrayMoon.App.Models;

/// <summary>Describes a single file-version token that is out of date for a workspace repository.</summary>
public sealed record FileVersionMismatchLine(string FileName, string TokenName, string CurrentValue, string ExpectedValue);
