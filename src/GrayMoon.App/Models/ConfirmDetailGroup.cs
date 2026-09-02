namespace GrayMoon.App.Models;

/// <summary>One line in a structured confirmation-dialog details group.</summary>
public sealed record ConfirmDetailItem(string Text, string? Secondary = null);

/// <summary>A labeled group of items shown in <c>ConfirmModal</c> when the caller supplies structured details.</summary>
public sealed record ConfirmDetailGroup(string Heading, IReadOnlyList<ConfirmDetailItem> Items);
