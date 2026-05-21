namespace GrayMoon.Common.Search;

/// <summary>A single search leaf: plain text or field:value (e.g. topic:blazor).</summary>
public sealed record FilterSearchTerm(string? Field, string Value);
