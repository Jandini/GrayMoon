using System.Reflection;
using GrayMoon.App.Models;
using GrayMoon.App.Services.Queries;

namespace GrayMoon.App.Tests.Conventions;

/// <summary>
/// Whether an operation may run on a repository is decided by the repository operation policy
/// (GrayMoon.Application.Repositories.Policy). Code that reads the tag of a checkout itself is making that decision
/// a second time, so every type that reads it is listed here with the reason it may. A new reader fails the test
/// until it either asks the policy or is added with a reason; an entry that no longer reads the tag fails too.
/// </summary>
public sealed class RepositoryPolicyConventionTests
{
    /// <summary>The getters that say "this checkout is on a tag".</summary>
    private static readonly MethodBase[] TagGetters =
    [
        typeof(WorkspaceRepositoryLink).GetProperty(nameof(WorkspaceRepositoryLink.CheckedOutTag))!.GetMethod!,
        typeof(WorkspaceRepositoryLink).GetProperty(nameof(WorkspaceRepositoryLink.IsOnTag))!.GetMethod!,
        typeof(WorkspaceRepositoryContextState).GetProperty(nameof(WorkspaceRepositoryContextState.CheckedOutTag))!.GetMethod!,
        typeof(WorkspaceRepositoryLinkListItemDto).GetProperty(nameof(WorkspaceRepositoryLinkListItemDto.CheckedOutTag))!.GetMethod!,
    ];

    /// <summary>Outermost type -> why it may read the tag of a checkout without asking the policy.</summary>
    private static readonly Dictionary<string, string> AllowedReaders = new(StringComparer.Ordinal)
    {
        ["GrayMoon.App.Services.Policy.RepositoryPolicySubjectExtensions"] =
            "Builds the policy subject from a link or a list row.",
        ["GrayMoon.App.Models.WorkspaceRepositoryLink"] =
            "Defines IsOnTag and copies the tag in the model's own copy method.",
        ["GrayMoon.App.Services.Queries.WorkspaceRepositoryLinkListItemDto"] =
            "The record's own generated members (equality, printing) read its property.",
        ["GrayMoon.App.Services.Queries.WorkspaceRepositoryLinkListMapper"] =
            "Copies the selected context's tag from a list row to a detached link for the grid.",
        ["GrayMoon.App.Services.Workspaces.WorkspaceRepositoryStateWriter"] =
            "The owner of per-context state: stores the tag and mirrors the special Workspace context onto the link.",
        ["GrayMoon.App.Services.Application.WorkspaceSyncOperations"] =
            "Copies the Feature context's tag onto links loaded for a handler; the handler asks the policy.",
        ["GrayMoon.App.Migrations"] =
            "Data migration: copies the stored tag from the link to the special Workspace context's state.",
        ["GrayMoon.App.Services.Git.WorkspaceGitService"] =
            "Tag pin bookkeeping: notes which repositories leave a tag when branches are created, to clear the pin in the Workspace definition.",
        ["GrayMoon.App.Services.Application.WorkspaceBranchOperations"] =
            "Reports the current tag to the branch dialog, keeps the Workspace definition's tag pin in step when a checkout "
            + "leaves a tag, and leaves tag checkouts out of the common-branch list (they have no branch to share).",
        ["GrayMoon.App.Components.Features.WorkspaceContextBar"] =
            "Shows the branch the repositories share; a tag checkout has no branch to show.",
        ["GrayMoon.App.Components.Pages.WorkspaceRepositoriesRow"] =
            "Shows the tag. OPEN: the commits and sync badges of a row are disabled by the tag itself, not by a policy "
            + "rule, because Sync is allowed on a tag for the whole Workspace but not from a row.",
        ["GrayMoon.App.Components.Pages.WorkspaceRepositories"] =
            "Shows tag state and passes it to badges; 'Skip repos on tags' in the Workspace-wide dialogs is a user option, "
            + "not a rule. OPEN: the level action buttons and the row and level Sync leave a tag checkout alone by reading "
            + "the tag, because Sync is allowed on a tag for the whole Workspace but not from a row or a level.",
    };

    [Fact]
    public void The_tag_of_a_checkout_is_read_only_by_the_policy_and_the_listed_types()
    {
        var readers = FindReaders(typeof(WorkspaceRepositoryLink).Assembly);

        Assert.NotEmpty(readers);
        var unlisted = readers.Where(r => !AllowedReaders.ContainsKey(r)).Order(StringComparer.Ordinal).ToList();
        Assert.True(unlisted.Count == 0,
            "These types read CheckedOutTag or IsOnTag themselves. Ask the repository operation policy whether the operation "
            + "is allowed (RepositoryOperationPolicy, IWorkspaceRepositoryOperationPolicy, the Allows extensions), or list the "
            + "type in AllowedReaders with the reason it only shows, stores or copies the tag:"
            + Environment.NewLine + string.Join(Environment.NewLine, unlisted));

        var stale = AllowedReaders.Keys.Where(k => !readers.Contains(k)).Order(StringComparer.Ordinal).ToList();
        Assert.True(stale.Count == 0, "Listed types that no longer read the tag; remove them from the list: " + string.Join(", ", stale));
    }

    [Fact]
    public void Every_listed_reader_has_a_reason()
    {
        Assert.All(AllowedReaders, entry => Assert.False(string.IsNullOrWhiteSpace(entry.Value), entry.Key));
    }

    [Fact]
    public void Scan_finds_a_tag_read_in_plain_async_and_lambda_code()
    {
        var readers = FindReaders(typeof(RepositoryPolicyConventionTests).Assembly);

        Assert.Contains(typeof(SampleTagReader).FullName!, readers);
        Assert.DoesNotContain(typeof(SampleBranchReader).FullName!, readers);
    }

    /// <summary>Outermost declaring types whose methods (lambdas and state machines included) read the tag of a checkout.</summary>
    internal static HashSet<string> FindReaders(Assembly assembly)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (method, callee) in IlCallScanner.Calls(assembly))
        {
            if (!TagGetters.Any(g => g.Module == callee.Module && g.MetadataToken == callee.MetadataToken))
                continue;

            var owner = method.DeclaringType!;
            while (owner.DeclaringType != null)
                owner = owner.DeclaringType;
            result.Add(owner.FullName!);
        }

        return result;
    }
}

/// <summary>Synthetic code the scan must report.</summary>
internal static class SampleTagReader
{
    public static bool Plain(WorkspaceRepositoryLink link) => link.IsOnTag;

    public static async Task<string?> InAsync(WorkspaceRepositoryContextState state)
    {
        await Task.Yield();
        return state.CheckedOutTag;
    }

    public static Func<WorkspaceRepositoryLinkListItemDto, string?> InLambda() => row => row.CheckedOutTag;
}

/// <summary>Synthetic code the scan must not report.</summary>
internal static class SampleBranchReader
{
    public static string? Read(WorkspaceRepositoryLink link) => link.BranchName;
}
