namespace GrayMoon.App.Models;

/// <summary>Describes a single file-version token line (current value) for display in a badge tooltip.</summary>
public sealed record FileVersionDisplayLine(string FileName, string TokenName, string Version);
