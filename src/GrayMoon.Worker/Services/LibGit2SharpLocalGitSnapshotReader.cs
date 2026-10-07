using GrayMoon.Worker.Abstractions;
using GrayMoon.Worker.Models;
using LibGit2Sharp;

namespace GrayMoon.Worker.Services;

/// <summary>
/// LibGit2Sharp-backed <see cref="ILocalGitSnapshotReader"/>. Holds no state; every call opens its own
/// <see cref="Repository"/> and disposes it before returning, so no handle outlives the call (an open handle on
/// Windows can block worktree removal and pack deletion). Answers match the Git CLI reads they replace
/// (<see cref="GitCliRepositoryReader"/>), with one deliberate difference: branch names are always the full
/// name below <c>refs/heads/</c> or <c>refs/remotes/origin/</c>, never git's disambiguated short form
/// (<c>heads/dup</c> when a tag is also called <c>dup</c>), and comparison refs are resolved by their full name
/// rather than by git's revision lookup, so a tag never stands in for a branch of the same name.
/// </summary>
public sealed class LibGit2SharpLocalGitSnapshotReader : ILocalGitSnapshotReader
{
    private const string HeadsPrefix = "refs/heads/";
    private const string TagsPrefix = "refs/tags/";
    private const string OriginPrefix = "refs/remotes/origin/";

    public LocalGitSnapshot Read(string repositoryPath, LocalGitSnapshotRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(repositoryPath))
            throw new ArgumentException("Repository path is required.", nameof(repositoryPath));
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            using var repository = new Repository(repositoryPath);
            if (repository.Info.IsBare)
                throw new LocalGitReadException(repositoryPath, "Repository has no work tree.");

            return ReadCore(repository, request, ct);
        }
        catch (Exception ex) when (ex is LibGit2SharpException or DllNotFoundException or TypeInitializationException or IOException or UnauthorizedAccessException)
        {
            throw new LocalGitReadException(repositoryPath, $"Could not read the repository in process: {ex.Message}", ex);
        }
    }

    private static LocalGitSnapshot ReadCore(Repository repository, LocalGitSnapshotRequest request, CancellationToken ct)
    {
        // HEAD: attached (possibly unborn) or detached. Read from the HEAD reference itself so an unborn branch
        // still has its name, as "git branch --show-current" prints it. In a linked worktree this is that
        // worktree's own HEAD.
        var headRef = repository.Refs.Head;
        string? currentBranch = null;
        if (headRef is SymbolicReference && headRef.TargetIdentifier.StartsWith(HeadsPrefix, StringComparison.Ordinal))
            currentBranch = headRef.TargetIdentifier[HeadsPrefix.Length..];
        var isUnborn = repository.Info.IsHeadUnborn;
        var headCommit = isUnborn ? null : PeelToCommit(headRef.ResolveToDirectReference()?.Target);
        ct.ThrowIfCancellationRequested();

        // One pass over the refs: tags with their sort date, both branch lists, and where origin/HEAD points.
        var tags = new List<TagEntry>();
        var local = new List<string>();
        var remote = new List<string>();
        var remoteNames = new HashSet<string>(StringComparer.Ordinal);
        var localNames = new HashSet<string>(StringComparer.Ordinal);
        string? originHeadTarget = null;
        foreach (var reference in repository.Refs)
        {
            var name = reference.CanonicalName;
            if (name == OriginPrefix + "HEAD")
            {
                originHeadTarget = reference is SymbolicReference ? OriginDefaultRef.HeadTargetBranch(reference.TargetIdentifier) : null;
                continue;
            }

            var isTag = name.StartsWith(TagsPrefix, StringComparison.Ordinal);
            var isLocal = !isTag && name.StartsWith(HeadsPrefix, StringComparison.Ordinal);
            var isRemote = !isTag && !isLocal && name.StartsWith(OriginPrefix, StringComparison.Ordinal);
            if (!isTag && !isLocal && !isRemote)
                continue;

            // git leaves a symbolic ref that points nowhere out of its listing; so does this.
            var direct = reference.ResolveToDirectReference();
            if (direct == null)
                continue;

            if (isTag)
            {
                var tagName = name[TagsPrefix.Length..];
                if (tagName.Length > 0)
                    tags.Add(ReadTag(repository, tagName, direct));
            }
            else if (isLocal)
            {
                var branchName = name[HeadsPrefix.Length..];
                if (branchName.Length > 0)
                {
                    local.Add(branchName);
                    localNames.Add(branchName);
                }
            }
            else
            {
                var branchName = name[OriginPrefix.Length..];
                if (branchName.Length > 0)
                {
                    remote.Add(branchName);
                    remoteNames.Add(branchName);
                }
            }
        }

        ct.ThrowIfCancellationRequested();

        // "git tag --sort=-creatordate": newest first by the tagger date (annotated) or the commit date
        // (lightweight), whole seconds; ties in ascending ref name order.
        var orderedTags = tags
            .OrderByDescending(t => t.Date)
            .ThenBy(t => t.Name, StringComparer.Ordinal)
            .Select(t => t.Name)
            .ToList();

        var defaultOriginRef = OriginDefaultRef.Pick(originHeadTarget, remoteNames.Contains);
        var refs = new RefSnapshot(
            orderedTags,
            local.OrderBy(b => b).ToList(),
            remote.OrderBy(b => b).ToList(),
            currentBranch != null && !isUnborn && localNames.Contains(currentBranch) ? currentBranch : null,
            defaultOriginRef,
            originHeadTarget != null && remoteNames.Contains(originHeadTarget));

        // Only a detached HEAD can sit on a tag ("git symbolic-ref -q HEAD" fails first).
        var checkedOutTag = headRef is not SymbolicReference && headCommit != null ? PickCheckedOutTag(tags, headCommit) : null;

        var graphCalculations = 0;

        // "vs default" counts: against the Feature's parent on origin when there is one, else the default branch.
        int? defaultBehind = null;
        int? defaultAhead = null;
        var divergenceRef = OriginDefaultRef.ToOriginBranchRef(request.DivergenceBaseBranch) ?? defaultOriginRef;
        if (headCommit != null && divergenceRef != null)
        {
            var target = ResolveCommit(repository, RemoteTrackingRefName(divergenceRef));
            if (target != null)
            {
                ct.ThrowIfCancellationRequested();
                var divergence = repository.ObjectDatabase.CalculateHistoryDivergence(headCommit, target);
                graphCalculations++;
                defaultAhead = divergence.AheadBy;
                defaultBehind = divergence.BehindBy;
            }
        }

        CommitCountsProbeResult? branchCounts = null;
        if (currentBranch != null)
        {
            ct.ThrowIfCancellationRequested();
            branchCounts = ReadCurrentBranchCounts(repository, headRef, headCommit, defaultOriginRef, request.DivergenceBaseBranch, ref graphCalculations);
        }

        return new LocalGitSnapshot(
            refs,
            currentBranch,
            isUnborn,
            headCommit?.Sha,
            checkedOutTag,
            defaultBehind,
            defaultAhead,
            branchCounts,
            graphCalculations);
    }

    /// <summary>
    /// What <c>ProbeCommitCountsAsync</c> answers for the checked-out branch: ahead/behind against its upstream;
    /// when it has none or the upstream ref is gone, outgoing against the local divergence base (when that branch
    /// exists) or else the default origin branch, with no incoming count; unknown counts when there is nothing to
    /// compare with or HEAD has no commit.
    /// </summary>
    private static CommitCountsProbeResult ReadCurrentBranchCounts(
        Repository repository,
        Reference headRef,
        Commit? headCommit,
        string? defaultOriginRef,
        string? divergenceBaseBranch,
        ref int graphCalculations)
    {
        var unknown = new CommitCountsProbeResult(null, null, false, CountsProbed: false, UpstreamProbed: true);
        if (headCommit == null)
            return unknown;

        // The local ref the configured upstream maps to (refs/remotes/<remote>/<name>, or a local branch for a
        // "." remote), when that ref exists. A missing ("gone") upstream and no upstream at all take the same
        // compare path below, as they do on the CLI, so they need not be told apart. A configuration libgit2
        // cannot resolve counts as no upstream, exactly as a failed upstream listing does on the CLI.
        Commit? upstream;
        try
        {
            upstream = PeelToCommit(repository.Branches[headRef.TargetIdentifier]?.TrackedBranch?.Reference?.ResolveToDirectReference()?.Target);
        }
        catch (LibGit2SharpException)
        {
            upstream = null;
        }

        if (upstream != null)
        {
            var divergence = repository.ObjectDatabase.CalculateHistoryDivergence(headCommit, upstream);
            graphCalculations++;
            return new CommitCountsProbeResult(divergence.AheadBy, divergence.BehindBy, true, CountsProbed: true, UpstreamProbed: true);
        }

        // No upstream, or one that is gone: the same compare ref either way.
        var compare = ResolveNoUpstreamCompare(repository, defaultOriginRef, divergenceBaseBranch);
        if (compare == null)
            return unknown;

        var ahead = repository.ObjectDatabase.CalculateHistoryDivergence(headCommit, compare);
        graphCalculations++;
        return new CommitCountsProbeResult(ahead.AheadBy, null, false, CountsProbed: true, UpstreamProbed: true);
    }

    /// <summary>
    /// The local divergence base branch when it exists (a Feature counts ahead of its parent), otherwise the
    /// default origin branch. The base is normalised the way the divergence base file stores and reads it.
    /// </summary>
    private static Commit? ResolveNoUpstreamCompare(Repository repository, string? defaultOriginRef, string? divergenceBaseBranch)
    {
        var divergenceLocal = StripOrigin(StripOrigin(divergenceBaseBranch));
        if (divergenceLocal != null)
        {
            var parent = ResolveCommit(repository, HeadsPrefix + divergenceLocal);
            if (parent != null)
                return parent;
        }

        return defaultOriginRef == null ? null : ResolveCommit(repository, RemoteTrackingRefName(defaultOriginRef));
    }

    private static string? StripOrigin(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;
        var trimmed = name.Trim();
        if (trimmed.StartsWith("origin/", StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed["origin/".Length..].Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }

    /// <summary><c>origin/&lt;name&gt;</c> (prefix in any case) to <c>refs/remotes/origin/&lt;name&gt;</c>.</summary>
    private static string RemoteTrackingRefName(string originRef)
        => originRef.StartsWith("origin/", StringComparison.OrdinalIgnoreCase)
            ? OriginPrefix + originRef["origin/".Length..]
            : "refs/remotes/" + originRef;

    /// <summary>The commit a full ref name resolves to, or null when the ref or its object is missing.</summary>
    private static Commit? ResolveCommit(Repository repository, string canonicalName)
        => PeelToCommit(repository.Refs[canonicalName]?.ResolveToDirectReference()?.Target);

    /// <summary>Follows annotated tags down to the commit they name; null for a tree, a blob or a missing object.</summary>
    private static Commit? PeelToCommit(GitObject? target)
    {
        for (var depth = 0; target != null && depth < 16; depth++)
        {
            switch (target)
            {
                case Commit commit:
                    return commit;
                case TagAnnotation annotation:
                    target = annotation.Target;
                    break;
                default:
                    return null;
            }
        }

        return null;
    }

    private static TagEntry ReadTag(Repository repository, string name, DirectReference direct)
    {
        var target = direct.Target;
        if (target is TagAnnotation annotation)
        {
            return new TagEntry(
                name,
                annotation.Tagger?.When.ToUnixTimeSeconds() ?? 0,
                IsAnnotated: true,
                PeelToCommit(annotation)?.Id);
        }

        if (target is Commit commit)
            return new TagEntry(name, commit.Committer.When.ToUnixTimeSeconds(), IsAnnotated: false, commit.Id);

        // A tag on a tree or blob has no date for git either, and no commit to be checked out on.
        return new TagEntry(name, 0, IsAnnotated: false, null);
    }

    /// <summary>
    /// "git describe --tags --exact-match" for HEAD: an annotated tag beats a lightweight one; between annotated
    /// tags the newer tagger date wins; otherwise the first in ascending ref name order is kept.
    /// </summary>
    private static string? PickCheckedOutTag(List<TagEntry> tags, Commit head)
    {
        TagEntry? best = null;
        foreach (var tag in tags.Where(t => t.CommitId == head.Id).OrderBy(t => t.Name, StringComparer.Ordinal))
        {
            if (best == null
                || (tag.IsAnnotated && !best.IsAnnotated)
                || (tag.IsAnnotated && best.IsAnnotated && tag.Date > best.Date))
                best = tag;
        }

        return best?.Name;
    }

    private sealed record TagEntry(string Name, long Date, bool IsAnnotated, ObjectId? CommitId);
}
