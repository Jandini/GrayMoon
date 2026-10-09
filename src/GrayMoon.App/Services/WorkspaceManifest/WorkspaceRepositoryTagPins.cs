using GrayMoon.Common.Git;
using WorkspaceManifestModel = GrayMoon.Application.WorkspaceManifest.WorkspaceManifest;
using WorkspaceManifestRepository = GrayMoon.Application.WorkspaceManifest.WorkspaceManifestRepository;
using WorkspaceRepositoryTagPinChange = GrayMoon.Application.WorkspaceManifest.WorkspaceRepositoryTagPinChange;

namespace GrayMoon.App.Services.WorkspaceManifest;

/// <summary>Applies tag pins onto a Workspace definition without adding or removing repository entries.</summary>
public static class WorkspaceRepositoryTagPins
{
    public static WorkspaceManifestModel CopyOnto(WorkspaceManifestModel target, WorkspaceManifestModel source)
    {
        var pins = new Dictionary<string, WorkspaceManifestRepository>(StringComparer.OrdinalIgnoreCase);
        foreach (var repository in source.Repositories ?? [])
        {
            if (repository.Tag is null || repository.Commit is null)
                continue;
            pins.TryAdd(RepositoryUrlIdentity.NormalizeRepositoryUrl(repository.RepositoryUrl), repository);
        }

        if (pins.Count == 0)
            return target;

        var repositories = (target.Repositories ?? []).Select(repository =>
        {
            if (!pins.TryGetValue(RepositoryUrlIdentity.NormalizeRepositoryUrl(repository.RepositoryUrl), out var pin))
                return repository;
            return repository with { Tag = pin.Tag, Commit = pin.Commit };
        }).ToList();

        return target with { Repositories = repositories };
    }

    public static bool TryApply(
        WorkspaceManifestModel manifest,
        IReadOnlyList<WorkspaceRepositoryTagPinChange> changes,
        out WorkspaceManifestModel updated,
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
                if (index < 0 || (repositories[index].Tag is null && repositories[index].Commit is null))
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
            if (string.Equals(current.Tag, tag, StringComparison.Ordinal) && string.Equals(current.Commit, commit, StringComparison.Ordinal))
                continue;

            repositories[index] = current with { Tag = tag, Commit = commit };
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
