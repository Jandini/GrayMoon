using GrayMoon.Common.Git;
using WorkspaceRepositoryReferenceRevisionChange = GrayMoon.Application.WorkspaceManifest.WorkspaceRepositoryReferenceRevisionChange;
using WorkspaceRepositoryTagPinChange = GrayMoon.Application.WorkspaceManifest.WorkspaceRepositoryTagPinChange;

namespace GrayMoon.App.Services.WorkspaceManifest;

// Inside the namespace, so WorkspaceManifest binds to the type and not to this namespace.
using GrayMoon.Common.Manifest;

/// <summary>
/// Applies recorded revisions onto a Workspace definition without adding or removing repository entries: tag pins
/// (<c>tag</c> + <c>commit</c>) and the revision of a read-only repository on a branch (<c>branch</c> + <c>commit</c>).
/// The database holds neither; the file is their only store.
/// </summary>
public static class WorkspaceRepositoryRevisionPins
{
    /// <summary>
    /// Whether a repository entry is read-only: its own <c>reference</c>, else the definition's
    /// <c>workspace.reference</c>, else false.
    /// </summary>
    public static bool IsReference(WorkspaceManifest manifest, WorkspaceManifestRepository repository) =>
        repository.Reference ?? manifest.Workspace?.Reference ?? false;

    /// <summary>
    /// Copies the recorded revisions of <paramref name="source"/> (the file) onto <paramref name="target"/> (built
    /// from the database). A tag pin is always carried. A branch revision is carried only onto an entry the target
    /// marks read-only; for any other entry it is dropped, together with a commit that has no tag.
    /// </summary>
    public static WorkspaceManifest CopyOnto(WorkspaceManifest target, WorkspaceManifest source)
    {
        var pins = new Dictionary<string, WorkspaceManifestRepository>(StringComparer.OrdinalIgnoreCase);
        foreach (var repository in source.Repositories ?? [])
        {
            if (repository.Commit is null || (repository.Tag is null && repository.Branch is null))
                continue;
            pins.TryAdd(RepositoryUrlIdentity.NormalizeRepositoryUrl(repository.RepositoryUrl), repository);
        }

        if (pins.Count == 0)
            return target;

        var repositories = (target.Repositories ?? []).Select(repository =>
        {
            if (!pins.TryGetValue(RepositoryUrlIdentity.NormalizeRepositoryUrl(repository.RepositoryUrl), out var pin))
                return repository;
            if (pin.Tag is not null)
                return repository with { Tag = pin.Tag, Branch = null, Commit = pin.Commit };
            return IsReference(target, repository)
                ? repository with { Tag = null, Branch = pin.Branch, Commit = pin.Commit }
                : repository;
        }).ToList();

        return target with { Repositories = repositories };
    }

    public static bool TryApply(
        WorkspaceManifest manifest,
        IReadOnlyList<WorkspaceRepositoryTagPinChange> changes,
        out WorkspaceManifest updated,
        out string? error)
    {
        updated = manifest;
        error = null;
        var repositories = (manifest.Repositories ?? []).ToList();
        var changed = false;

        foreach (var change in changes)
        {
            var index = IndexOf(repositories, change.RepositoryUrl);
            var clearing = change.Tag is null && change.Commit is null;
            if (clearing)
            {
                // Only a tag pin is cleared here. The revision of a read-only repository on a branch has no tag
                // and is not this path's to remove.
                if (index < 0 || repositories[index].Tag is null)
                    continue;

                repositories[index] = repositories[index] with { Tag = null, Commit = null };
                changed = true;
                continue;
            }

            if (!WorkspaceDefinitionTagPin.TryNormalize(change.Tag, change.Commit, RepositoryLabel(repositories, index, change.RepositoryUrl), out var tag, out var commit, out var pinError))
            {
                error = pinError;
                return false;
            }

            if (index < 0)
            {
                error = $"Repository '{change.RepositoryUrl}' is not in the Workspace definition.";
                return false;
            }

            var current = repositories[index];
            if (current.Branch is null
                && string.Equals(current.Tag, tag, StringComparison.Ordinal)
                && string.Equals(current.Commit, commit, StringComparison.Ordinal))
                continue;

            // An entry records a tag or a branch, never both.
            repositories[index] = current with { Tag = tag, Branch = null, Commit = commit };
            changed = true;
        }

        if (changed)
            updated = manifest with { Repositories = repositories };
        return true;
    }

    /// <summary>
    /// Records where read-only repositories on a branch are. A change with no branch and no commit removes the
    /// recorded branch revision and leaves a tag pin alone. Recording one for an entry that is not read-only in the
    /// definition is an error: <c>branch</c> is never written for a writable repository.
    /// </summary>
    public static bool TryApply(
        WorkspaceManifest manifest,
        IReadOnlyList<WorkspaceRepositoryReferenceRevisionChange> changes,
        out WorkspaceManifest updated,
        out string? error)
    {
        updated = manifest;
        error = null;
        var repositories = (manifest.Repositories ?? []).ToList();
        var changed = false;

        foreach (var change in changes)
        {
            var index = IndexOf(repositories, change.RepositoryUrl);
            var clearing = change.Branch is null && change.Commit is null;
            if (clearing)
            {
                if (index < 0 || repositories[index].Branch is null)
                    continue;

                repositories[index] = repositories[index] with { Branch = null, Commit = null };
                changed = true;
                continue;
            }

            if (index < 0)
            {
                error = $"Repository '{change.RepositoryUrl}' is not in the Workspace definition.";
                return false;
            }

            var current = repositories[index];
            if (string.IsNullOrWhiteSpace(change.Branch))
            {
                error = $"Repository '{RepositoryLabel(repositories, index, change.RepositoryUrl)}' has a commit to record but no branch.";
                return false;
            }

            if (!WorkspaceDefinitionRevisionPin.TryNormalize(
                    null,
                    change.Branch,
                    change.Commit,
                    IsReference(manifest, current),
                    RepositoryLabel(repositories, index, change.RepositoryUrl),
                    out _,
                    out var branch,
                    out var commit,
                    out var pinError))
            {
                error = pinError;
                return false;
            }

            if (current.Tag is null
                && string.Equals(current.Branch, branch, StringComparison.Ordinal)
                && string.Equals(current.Commit, commit, StringComparison.Ordinal))
                continue;

            repositories[index] = current with { Tag = null, Branch = branch, Commit = commit };
            changed = true;
        }

        if (changed)
            updated = manifest with { Repositories = repositories };
        return true;
    }

    private static int IndexOf(List<WorkspaceManifestRepository> repositories, string repositoryUrl)
    {
        for (var i = 0; i < repositories.Count; i++)
        {
            if (RepositoryUrlIdentity.RepositoryUrlsEqual(repositories[i].RepositoryUrl, repositoryUrl))
                return i;
        }

        return -1;
    }

    private static string? RepositoryLabel(List<WorkspaceManifestRepository> repositories, int index, string repositoryUrl) =>
        index >= 0 && !string.IsNullOrWhiteSpace(repositories[index].Name)
            ? repositories[index].Name
            : repositoryUrl;
}
