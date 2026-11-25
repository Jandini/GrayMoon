namespace GrayMoon.Worker.Models;

/// <summary>
/// What HEAD is, read with one git process (two when detached). Everything is null when the repository is missing,
/// unborn or unreadable.
/// </summary>
/// <param name="Commit">The full SHA of HEAD.</param>
/// <param name="Branch">The checked-out branch; null when detached.</param>
/// <param name="Tag">The tag whose tip is HEAD when detached; null on a branch or when no tag points at HEAD.</param>
public sealed record HeadState(string? Commit, string? Branch, string? Tag)
{
    public static HeadState None { get; } = new(null, null, null);
}
