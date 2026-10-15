namespace GrayMoon.App.Services.GitChanges;

/// <summary>
/// Decides the order in which the Git Changes page's next / previous change buttons walk through files
/// once the last (or first) change of the current file is passed. To change the walk order, add another
/// implementation and swap the registration in <c>Program.cs</c>; nothing else needs to change.
/// </summary>
public interface IGitChangesFileOrdering
{
    /// <summary>
    /// Returns every walkable File row in walk order. <paramref name="fileRows"/> arrives in tree order
    /// (Staged then Changed, repository, folder, file), already narrowed by the active filter and with
    /// collapsed folders ignored.
    /// </summary>
    IReadOnlyList<GitChangesTreeRow> Order(IReadOnlyList<GitChangesTreeRow> fileRows);
}

/// <summary>Walks files in the same order the changed-files tree shows them.</summary>
public sealed class TreeGitChangesFileOrdering : IGitChangesFileOrdering
{
    public IReadOnlyList<GitChangesTreeRow> Order(IReadOnlyList<GitChangesTreeRow> fileRows) => fileRows;
}
