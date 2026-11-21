using System.Text.Json;
using GrayMoon.Abstractions.Worker;
using GrayMoon.App.Data;
using GrayMoon.App.Models;
using GrayMoon.App.Services.Application;
using GrayMoon.App.Services.Workspaces;
using Microsoft.EntityFrameworkCore;

namespace GrayMoon.App.Tests;

/// <summary>
/// A folder GrayMoon stops managing is detached first (the Worker releases its watchers and removes GrayMoon's Git
/// hooks) and only then its link rows go: Remove Workspace, a Source repository leaving a Workspace, disabling the
/// Workspace repository, a Source that becomes the Workspace repository, and a rolled-back restore.
/// </summary>
public sealed class WorkspaceDetachOperationsTests
{
    private const string Detach = WorkerHubMethods.DetachRepositories;

    // ---- Remove Workspace ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Remove_detaches_every_repository_while_the_workspace_still_exists_and_then_removes_it()
    {
        await using var fixture = await OperationsFixture.CreateAsync();
        var workspace = await fixture.Seed.SeedWorkspaceWithRootRepositoryAsync();
        var existedDuringDetach = false;
        fixture.Bridge.Handler = (command, args) =>
        {
            if (command == Detach)
            {
                using var db = new AppDbContext(fixture.Seed.Options);
                existedDuringDetach = db.Workspaces.Any(w => w.WorkspaceId == workspace.WorkspaceId)
                    && db.WorkspaceRepositories.Count(l => l.WorkspaceId == workspace.WorkspaceId) == 3;
            }

            return DetachScript.Answer(command, args);
        };

        var result = await fixture.Operations.RemoveWorkspaceAsync(workspace.WorkspaceId);

        Assert.True(result.Success, result.Error);
        var sent = Assert.Single(fixture.Bridge.Sent, s => s.Command == Detach);
        var args = ManifestTestFixture.ToJson(sent.Args);
        Assert.True(args.GetProperty("workspaceRemoved").GetBoolean());
        Assert.Equal(workspace.WorkspaceId, args.GetProperty("workspaceId").GetInt32());
        Assert.Equal(ManifestTestFixture.WorkspaceFolder, args.GetProperty("workspaceName").GetString());
        Assert.Equal("ws-root", args.GetProperty("workspaceRepositoryName").GetString());
        // Every link, the Workspace repository included.
        Assert.Equal(
            new[] { "Api", "Web", "ws-root" },
            args.GetProperty("repositories").EnumerateArray().Select(r => r.GetProperty("repositoryName").GetString()).Order());
        Assert.True(existedDuringDetach, "the rows are the record of the folders: they go only after the detach");
        await fixture.AssertNoWorkspaceAsync();
    }

    [Fact]
    public async Task Remove_with_a_repository_that_cannot_be_cleaned_deletes_nothing_and_names_the_file()
    {
        await using var fixture = await OperationsFixture.CreateAsync();
        var workspace = await fixture.Seed.SeedWorkspaceWithRootRepositoryAsync();
        var links = await fixture.GetLinksAsync(workspace.WorkspaceId);
        var apiId = await RepositoryIdAsync(fixture, "Api");
        const string lockedFile = @"C:\work\ws\Api\.git\hooks\post-commit";
        fixture.Bridge.Handler = (command, args) => DetachScript.Answer(command, args, repository =>
            repository.RepositoryId == apiId
                ? DetachScript.Failed(repository, "HookNotDeleted", lockedFile, "Access to the path is denied.")
                : null);

        var result = await fixture.Operations.RemoveWorkspaceAsync(workspace.WorkspaceId);

        Assert.False(result.Success);
        Assert.StartsWith(DetachFailureText.WorkspaceNotRemoved, result.Error);
        Assert.Contains("1 of 3 repositories", result.Error);
        Assert.Contains("press Remove again", result.Error);
        Assert.Contains("other 2 repositories are already cleaned up", result.Error);
        var line = Assert.Single(result.RepoErrors!).Value;
        Assert.StartsWith("Api: the Git hook file", line);
        Assert.Contains(lockedFile, line);
        Assert.Contains("Access to the path is denied", line);

        // Everything a removal would have destroyed is still there.
        await using var db = new AppDbContext(fixture.Seed.Options);
        Assert.True(await db.Workspaces.AnyAsync(w => w.WorkspaceId == workspace.WorkspaceId));
        Assert.Equal(
            links.Select(l => l.WorkspaceRepositoryId).Order(),
            (await fixture.GetLinksAsync(workspace.WorkspaceId)).Select(l => l.WorkspaceRepositoryId).Order());
        Assert.False(fixture.MonitoringPause.IsPaused(1), "monitoring resumes when the removal did not happen");
    }

    [Fact]
    public async Task Remove_without_the_worker_asks_nothing_and_deletes_nothing()
    {
        await using var fixture = await OperationsFixture.CreateAsync();
        var workspace = await fixture.Seed.SeedWorkspaceWithRootRepositoryAsync();
        fixture.Bridge.IsWorkerConnected = false;

        var result = await fixture.Operations.RemoveWorkspaceAsync(workspace.WorkspaceId);

        Assert.False(result.Success);
        Assert.StartsWith(DetachFailureText.WorkspaceNotRemoved, result.Error);
        Assert.Contains("Git hooks", result.Error);
        Assert.Empty(fixture.Bridge.Sent);
        Assert.Equal(3, (await fixture.GetLinksAsync(workspace.WorkspaceId)).Count);
    }

    [Fact]
    public async Task Remove_is_refused_while_the_workspace_has_features_and_the_worker_is_not_asked()
    {
        await using var fixture = await OperationsFixture.CreateAsync();
        var workspace = await fixture.Seed.SeedWorkspaceWithRootRepositoryAsync();
        await fixture.AddFeatureAsync(workspace.WorkspaceId);

        var result = await fixture.Operations.RemoveWorkspaceAsync(workspace.WorkspaceId);

        Assert.False(result.Success);
        Assert.Equal("This workspace has 1 Feature: feature-a. Remove the Feature first, then remove the workspace.", result.Error);
        Assert.Empty(fixture.Bridge.Sent);
        Assert.Equal(3, (await fixture.GetLinksAsync(workspace.WorkspaceId)).Count);
        await using var db = new AppDbContext(fixture.Seed.Options);
        Assert.Equal(1, await db.WorkspaceFeatures.CountAsync(f => f.WorkspaceId == workspace.WorkspaceId));
    }

    [Fact]
    public async Task Remove_is_not_blocked_by_another_workspaces_feature_and_leaves_that_workspace_alone()
    {
        await using var fixture = await OperationsFixture.CreateAsync();
        var workspace = await fixture.Seed.SeedWorkspaceWithRootRepositoryAsync();
        var other = await fixture.Seed.AddWorkspaceAsync("other");
        await fixture.AddFeatureAsync(other.WorkspaceId);

        var result = await fixture.Operations.RemoveWorkspaceAsync(workspace.WorkspaceId);

        Assert.True(result.Success, result.Error);
        await using var db = new AppDbContext(fixture.Seed.Options);
        Assert.Equal(["other"], await db.Workspaces.AsNoTracking().Select(w => w.Name).ToListAsync());
        Assert.Equal(1, await db.WorkspaceFeatures.CountAsync(f => f.WorkspaceId == other.WorkspaceId));
        // The repositories themselves stay in the catalog.
        Assert.Equal(3, await db.Repositories.CountAsync());
    }

    [Fact]
    public async Task Remove_is_refused_while_another_operation_holds_the_workspace()
    {
        await using var fixture = await OperationsFixture.CreateAsync();
        var workspace = await fixture.Seed.SeedWorkspaceWithRootRepositoryAsync();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(fixture.Lock.TryStartStructural(
            workspace.WorkspaceId, "sync", "/workspaces/1", "Syncing...", (_, _) => release.Task, out var running));

        var result = await fixture.Operations.RemoveWorkspaceAsync(workspace.WorkspaceId);
        release.SetResult();
        await running.WhenCompleted;

        Assert.False(result.Success);
        Assert.Equal($"{DetachFailureText.WorkspaceNotRemoved} {WorkspaceRepositoryOperations.StructuralBusyMessage}", result.Error);
        Assert.Empty(fixture.Bridge.Sent);
        Assert.Equal(3, (await fixture.GetLinksAsync(workspace.WorkspaceId)).Count);
    }

    [Fact]
    public async Task Remove_keeps_change_monitoring_paused_from_the_detach_until_the_rows_are_gone()
    {
        await using var fixture = await OperationsFixture.CreateAsync();
        var workspace = await fixture.Seed.SeedWorkspaceWithRootRepositoryAsync();
        var pausedDuringDetach = false;
        fixture.Bridge.Handler = (command, args) =>
        {
            if (command == Detach)
                pausedDuringDetach = fixture.MonitoringPause.IsPaused(1);
            return DetachScript.Answer(command, args);
        };

        var result = await fixture.Operations.RemoveWorkspaceAsync(workspace.WorkspaceId);

        Assert.True(result.Success, result.Error);
        Assert.True(pausedDuringDetach, "a watcher renewal during the detach would put the watcher back");
        Assert.False(fixture.MonitoringPause.IsPaused(1));
    }

    [Fact]
    public async Task Remove_tells_which_of_the_users_own_hooks_were_put_back()
    {
        await using var fixture = await OperationsFixture.CreateAsync();
        var workspace = await fixture.Seed.SeedWorkspaceWithRootRepositoryAsync();
        var apiId = await RepositoryIdAsync(fixture, "Api");
        fixture.Bridge.Handler = (command, args) => DetachScript.Answer(command, args, repository =>
            repository.RepositoryId == apiId
                ? new
                {
                    repositoryId = repository.RepositoryId,
                    repositoryName = repository.RepositoryName,
                    outcome = "Removed",
                    restoredHooks = new[] { "post-commit" },
                    leftoverReplacedHooks = new[] { "post-commit.replaced-by-graymoon" },
                }
                : null);

        var result = await fixture.Operations.RemoveWorkspaceAsync(workspace.WorkspaceId);

        Assert.True(result.Success, result.Error);
        Assert.Equal(
            [
                "Your own Git hooks were put back in 1 repository: Api.",
                "Older replaced hook files were left in 1 repository: Api (post-commit.replaced-by-graymoon).",
            ],
            result.Warnings!);
        await fixture.AssertNoWorkspaceAsync();
    }

    [Fact]
    public async Task Remove_of_a_workspace_that_is_already_gone_succeeds_without_the_worker_being_asked()
    {
        await using var fixture = await OperationsFixture.CreateAsync();

        var result = await fixture.Operations.RemoveWorkspaceAsync(987);

        Assert.True(result.Success, result.Error);
        Assert.Empty(fixture.Bridge.Sent);
    }

    [Fact]
    public async Task Remove_fails_and_deletes_nothing_when_the_worker_answer_cannot_be_read()
    {
        await using var fixture = await OperationsFixture.CreateAsync();
        var workspace = await fixture.Seed.SeedWorkspaceWithRootRepositoryAsync();
        fixture.Bridge.Handler = (command, _) => command == Detach
            ? new WorkerCommandResponse(false, null, "The Worker timed out.")
            : new WorkerCommandResponse(true, new { success = true }, null);

        var result = await fixture.Operations.RemoveWorkspaceAsync(workspace.WorkspaceId);

        Assert.False(result.Success);
        Assert.Contains("The Worker timed out.", result.Error);
        Assert.Equal(3, (await fixture.GetLinksAsync(workspace.WorkspaceId)).Count);
    }

    // ---- A Source repository leaves the Workspace --------------------------------------------------------------

    [Fact]
    public async Task Save_that_removes_a_source_detaches_only_that_repository_and_then_removes_its_link()
    {
        await using var fixture = await OperationsFixture.CreateAsync();
        var workspace = await fixture.Seed.SeedWorkspaceWithRootRepositoryAsync();
        var apiId = await RepositoryIdAsync(fixture, "Api");
        var webId = await RepositoryIdAsync(fixture, "Web");

        var result = await fixture.Operations.SaveWorkspaceAsync(workspace.WorkspaceId, workspace.Name, [apiId], null);

        Assert.True(result.Success, result.Error);
        var args = ManifestTestFixture.ToJson(Assert.Single(fixture.Bridge.Sent, s => s.Command == Detach).Args);
        Assert.False(args.GetProperty("workspaceRemoved").GetBoolean());
        var only = Assert.Single(args.GetProperty("repositories").EnumerateArray());
        Assert.Equal(webId, only.GetProperty("repositoryId").GetInt32());
        Assert.Equal("Web", only.GetProperty("repositoryName").GetString());

        var remaining = await fixture.GetLinksAsync(workspace.WorkspaceId);
        Assert.DoesNotContain(remaining, l => l.RepositoryId == webId);
        Assert.Contains(remaining, l => l.RepositoryId == apiId);
        Assert.Contains(remaining, l => l.Role == WorkspaceRepositoryRole.Workspace);
    }

    [Fact]
    public async Task Save_that_removes_nothing_needs_neither_the_worker_nor_the_lock()
    {
        await using var fixture = await OperationsFixture.CreateAsync();
        var workspace = await fixture.Seed.SeedWorkspaceWithRootRepositoryAsync();
        var apiId = await RepositoryIdAsync(fixture, "Api");
        var webId = await RepositoryIdAsync(fixture, "Web");
        fixture.Bridge.IsWorkerConnected = false;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(fixture.Lock.TryStartStructural(
            workspace.WorkspaceId, "sync", "/workspaces/1", "Syncing...", (_, _) => release.Task, out var running));

        var result = await fixture.Operations.SaveWorkspaceAsync(workspace.WorkspaceId, workspace.Name, [apiId, webId], null);
        release.SetResult();
        await running.WhenCompleted;

        Assert.True(result.Success, result.Error);
        Assert.Empty(fixture.Bridge.Sent);
        Assert.Equal(3, (await fixture.GetLinksAsync(workspace.WorkspaceId)).Count);
    }

    [Fact]
    public async Task Save_whose_detach_fails_keeps_the_link_and_its_state()
    {
        await using var fixture = await OperationsFixture.CreateAsync();
        var workspace = await fixture.Seed.SeedWorkspaceWithRootRepositoryAsync();
        var apiId = await RepositoryIdAsync(fixture, "Api");
        var webId = await RepositoryIdAsync(fixture, "Web");
        await using (var seed = new AppDbContext(fixture.Seed.Options))
        {
            var web = await seed.WorkspaceRepositories.SingleAsync(l => l.WorkspaceId == workspace.WorkspaceId && l.RepositoryId == webId);
            web.BranchName = "feature/in-progress";
            await seed.SaveChangesAsync();
        }

        fixture.Bridge.Handler = (command, args) => DetachScript.Answer(command, args, repository =>
            DetachScript.Failed(repository, "WorkerBusy", @"C:\work\ws\Web", "A git process did not stop."));

        var result = await fixture.Operations.SaveWorkspaceAsync(workspace.WorkspaceId, workspace.Name, [apiId], null);

        Assert.False(result.Success);
        Assert.StartsWith(DetachFailureText.RepositoriesNotRemoved, result.Error);
        Assert.Contains("save again", result.Error);
        Assert.Contains("Web: the Worker is still working in", Assert.Single(result.RepoErrors!).Value);
        var kept = Assert.Single(await fixture.GetLinksAsync(workspace.WorkspaceId), l => l.RepositoryId == webId);
        Assert.Equal("feature/in-progress", kept.BranchName);
    }

    [Fact]
    public async Task Save_that_removes_a_source_is_refused_without_the_worker()
    {
        await using var fixture = await OperationsFixture.CreateAsync();
        var workspace = await fixture.Seed.SeedWorkspaceWithRootRepositoryAsync();
        var apiId = await RepositoryIdAsync(fixture, "Api");
        fixture.Bridge.IsWorkerConnected = false;

        var result = await fixture.Operations.SaveWorkspaceAsync(workspace.WorkspaceId, workspace.Name, [apiId], null);

        Assert.False(result.Success);
        Assert.StartsWith(DetachFailureText.RepositoriesNotRemoved, result.Error);
        Assert.Empty(fixture.Bridge.Sent);
        Assert.Equal(3, (await fixture.GetLinksAsync(workspace.WorkspaceId)).Count);
    }

    [Fact]
    public async Task Save_refused_by_a_feature_or_a_taken_name_fails_before_any_folder_is_touched()
    {
        await using var fixture = await OperationsFixture.CreateAsync();
        var workspace = await fixture.Seed.SeedWorkspaceWithRootRepositoryAsync();
        var apiId = await RepositoryIdAsync(fixture, "Api");
        await fixture.Seed.AddWorkspaceAsync("taken");

        var taken = await fixture.Operations.SaveWorkspaceAsync(workspace.WorkspaceId, "taken", [apiId], null);
        await fixture.AddFeatureAsync(workspace.WorkspaceId);
        var feature = await fixture.Operations.SaveWorkspaceAsync(workspace.WorkspaceId, workspace.Name, [apiId], null);

        Assert.Equal("Workspace name already exists.", taken.Error);
        Assert.Equal(GrayMoon.App.Repositories.WorkspaceRepository.MembershipBlockedWhileFeaturesExist, feature.Error);
        Assert.Empty(fixture.Bridge.Sent);
        Assert.Equal(3, (await fixture.GetLinksAsync(workspace.WorkspaceId)).Count);
    }

    // ---- Disable the Workspace repository ----------------------------------------------------------------------

    [Fact]
    public async Task Disable_detaches_the_workspace_folder_and_then_removes_the_link()
    {
        await using var fixture = await OperationsFixture.CreateAsync();
        var workspace = await fixture.Seed.SeedWorkspaceWithRootRepositoryAsync();

        var result = await fixture.Operations.DisableWorkspaceRepositoryAsync(workspace.WorkspaceId);

        Assert.True(result.Success, result.Error);
        var args = ManifestTestFixture.ToJson(Assert.Single(fixture.Bridge.Sent, s => s.Command == Detach).Args);
        Assert.Equal("ws-root", Assert.Single(args.GetProperty("repositories").EnumerateArray()).GetProperty("repositoryName").GetString());
        Assert.Equal("ws-root", args.GetProperty("workspaceRepositoryName").GetString());
        Assert.False(args.GetProperty("workspaceRemoved").GetBoolean());
        Assert.Empty(await fixture.GetLinksAsync(workspace.WorkspaceId, WorkspaceRepositoryRole.Workspace));
        Assert.Equal(2, (await fixture.GetLinksAsync(workspace.WorkspaceId, WorkspaceRepositoryRole.Source)).Count);
    }

    [Fact]
    public async Task Disable_whose_detach_fails_keeps_the_workspace_repository()
    {
        await using var fixture = await OperationsFixture.CreateAsync();
        var workspace = await fixture.Seed.SeedWorkspaceWithRootRepositoryAsync();
        fixture.Bridge.Handler = (command, args) => DetachScript.Answer(command, args, repository =>
            DetachScript.Failed(repository, "HookNotRestored", @"C:\work\ws\.git\hooks\pre-push.replaced-by-graymoon", "The file is in use."));

        var result = await fixture.Operations.DisableWorkspaceRepositoryAsync(workspace.WorkspaceId);

        Assert.False(result.Success);
        Assert.StartsWith(DetachFailureText.WorkspaceRepositoryNotDisabled, result.Error);
        Assert.Contains("your own hook", Assert.Single(result.RepoErrors!).Value);
        Assert.Single(await fixture.GetLinksAsync(workspace.WorkspaceId, WorkspaceRepositoryRole.Workspace));
    }

    // ---- A Source repository becomes the Workspace repository --------------------------------------------------

    [Fact]
    public async Task Moving_a_source_detaches_its_old_folder_by_name_and_not_the_new_root()
    {
        await using var fixture = await OperationsFixture.CreateAsync();
        var (workspace, root) = await fixture.SeedEnableScenarioAsync();
        await fixture.Seed.LinkAsync(workspace, root, WorkspaceRepositoryRole.Source);

        var result = await fixture.Operations.EnableWorkspaceRepositoryAsync(workspace.WorkspaceId, root.RepositoryId);

        Assert.True(result.Success, result.Error);
        var commands = fixture.Bridge.Sent.Select(s => s.Command).ToList();
        Assert.True(commands.IndexOf("AttachWorkspaceRepository") < commands.IndexOf(Detach), "the old folder is cleaned after the attach worked");
        var args = ManifestTestFixture.ToJson(Assert.Single(fixture.Bridge.Sent, s => s.Command == Detach).Args);
        // Without the Workspace repository name the Worker resolves "ws-root" to the Source folder <root>/ws-root,
        // not to the Workspace folder, which now holds the fresh checkout and its new hooks.
        Assert.Equal(JsonValueKind.Null, args.GetProperty("workspaceRepositoryName").ValueKind);
        Assert.Equal("ws-root", Assert.Single(args.GetProperty("repositories").EnumerateArray()).GetProperty("repositoryName").GetString());
    }

    [Fact]
    public async Task A_moved_source_whose_old_folder_cannot_be_cleaned_is_a_warning_and_the_move_stands()
    {
        await using var fixture = await OperationsFixture.CreateAsync();
        var (workspace, root) = await fixture.SeedEnableScenarioAsync();
        await fixture.Seed.LinkAsync(workspace, root, WorkspaceRepositoryRole.Source);
        fixture.Bridge.Handler = (command, args) => DetachScript.Answer(command, args, repository =>
            DetachScript.Failed(repository, "HookNotDeleted", @"C:\work\ws\ws-root\.git\hooks\post-merge", "Access to the path is denied."));

        var result = await fixture.Operations.EnableWorkspaceRepositoryAsync(workspace.WorkspaceId, root.RepositoryId);

        Assert.True(result.Success, result.Error);
        Assert.Contains(result.Warnings!, w => w.Contains("still has GrayMoon's Git hooks") && w.Contains(@"C:\work\ws\ws-root\.git\hooks\post-merge"));
        var link = Assert.Single(await fixture.GetLinksAsync(workspace.WorkspaceId));
        Assert.Equal(WorkspaceRepositoryRole.Workspace, link.Role);
    }

    // ---- Restore rolled back -----------------------------------------------------------------------------------

    [Fact]
    public async Task A_rolled_back_restore_removes_the_hooks_before_the_folder_and_the_rows()
    {
        await using var fixture = await OperationsFixture.CreateAsync();
        var root = await fixture.SeedRootAsync();
        fixture.UseDefinition(OperationsDefinition.Basic());
        // The clone works, then the definition read after it is invalid: the Workspace repository link exists.
        fixture.Bridge.RespondWithFile("{ \"version\": 1 }");

        var result = await fixture.Operations.RestoreFromRepositoryAsync(root.RepositoryId, "restored");

        Assert.False(result.Success);
        var commands = fixture.Bridge.Sent.Select(s => s.Command).ToList();
        var detach = commands.IndexOf(Detach);
        Assert.True(detach >= 0, "the attach wrote hooks; the rollback has to take them back");
        Assert.True(detach < commands.IndexOf(WorkerHubMethods.DiscardWorkspaceRoot));
        var args = ManifestTestFixture.ToJson(fixture.Bridge.Sent[detach].Args);
        Assert.True(args.GetProperty("workspaceRemoved").GetBoolean());
        Assert.Equal("ws-root", Assert.Single(args.GetProperty("repositories").EnumerateArray()).GetProperty("repositoryName").GetString());
        await fixture.AssertNoWorkspaceAsync();
    }

    [Fact]
    public async Task A_rollback_that_cannot_remove_the_hooks_says_so_in_the_residue()
    {
        await using var fixture = await OperationsFixture.CreateAsync();
        var root = await fixture.SeedRootAsync();
        fixture.UseDefinition(OperationsDefinition.Basic());
        fixture.Bridge.Handler = (command, args) => command switch
        {
            "GetFileContents" => new WorkerCommandResponse(true, new { content = "{ \"version\": 1 }" }, null),
            _ => DetachScript.Answer(command, args, repository =>
                DetachScript.Failed(repository, "HookNotDeleted", @"C:\work\restored\.git\hooks\post-commit", "Access to the path is denied.")),
        };

        var result = await fixture.Operations.RestoreFromRepositoryAsync(root.RepositoryId, "restored");

        Assert.False(result.Success);
        Assert.Contains(@"C:\work\restored\.git\hooks\post-commit", result.CleanupResidue);
        await fixture.AssertNoWorkspaceAsync();
    }

    // ---- The text ----------------------------------------------------------------------------------------------

    [Fact]
    public void Each_failure_kind_says_what_happened_where_and_what_to_do()
    {
        static DetachRepositoryOutcome Failed(string kind, string path, string message) =>
            new(1, "Core", @"C:\ws\Core", DetachOutcomes.Failed, kind, path, message, [], []);

        Assert.Equal(
            @"Core: the Git hook file C:\ws\Core\.git\hooks\post-commit could not be deleted (Access denied). Close programs that use this repository, or check the file's permissions.",
            DetachFailureText.Describe(Failed(DetachOutcomes.HookNotDeleted, @"C:\ws\Core\.git\hooks\post-commit", "Access denied.")));
        Assert.Equal(
            @"Core: your own hook C:\ws\Core\.git\hooks\pre-push.replaced-by-graymoon could not be renamed back (In use). GrayMoon's hook is already gone; rename the file yourself or close the program that has it open.",
            DetachFailureText.Describe(Failed(DetachOutcomes.HookNotRestored, @"C:\ws\Core\.git\hooks\pre-push.replaced-by-graymoon", "In use")));
        Assert.Equal(
            @"Core: the Worker is still working in C:\ws\Core and could not stop in time (Timed out). Wait a moment and try again.",
            DetachFailureText.Describe(Failed(DetachOutcomes.WorkerBusy, @"C:\ws\Core", "Timed out")));
        Assert.Equal(
            @"Core: Git could not read the repository in C:\ws\Core (fatal: not a git repository). Repair the repository, or delete the folder if you no longer need it.",
            DetachFailureText.Describe(Failed(DetachOutcomes.GitFailed, @"C:\ws\Core", "fatal: not a git repository")));
    }

    [Fact]
    public void A_single_line_dialog_gets_the_headline_followed_by_each_repository()
    {
        var failure = GrayMoon.Application.OperationResult.Fail("Not removed.", new Dictionary<int, string> { [2] = "B: second.", [1] = "A: first." });

        Assert.Equal("Not removed. A: first. B: second.", DetachFailureText.Flatten(failure));
    }

    private static async Task<int> RepositoryIdAsync(OperationsFixture fixture, string name)
    {
        await using var db = new AppDbContext(fixture.Seed.Options);
        return await db.Repositories.Where(r => r.RepositoryName == name).Select(r => r.RepositoryId).SingleAsync();
    }
}

/// <summary>A Workspace definition with the Basic profile and no repositories.</summary>
internal static class OperationsDefinition
{
    public static string Basic() =>
        new GrayMoon.Common.Manifest.WorkspaceManifestFile().Serialize(new GrayMoon.Common.Manifest.WorkspaceManifest(
            1,
            new GrayMoon.Common.Manifest.WorkspaceManifestWorkspace(new GrayMoon.Common.Manifest.WorkspaceManifestProfile("basic", "none", "none")),
            [],
            []));
}

/// <summary>Answers <c>DetachRepositories</c> the way the Worker does, one result per requested repository.</summary>
internal static class DetachScript
{
    public sealed record Requested(int RepositoryId, string RepositoryName);

    /// <summary>Every repository is <c>Removed</c> unless <paramref name="perRepository"/> returns its own result.</summary>
    public static WorkerCommandResponse Answer(string command, object args, Func<Requested, object?>? perRepository = null)
    {
        if (command != WorkerHubMethods.DetachRepositories)
            return new WorkerCommandResponse(true, new { success = true, removed = true, written = true }, null);

        var requested = ManifestTestFixture.ToJson(args).GetProperty("repositories").EnumerateArray()
            .Select(r => new Requested(r.GetProperty("repositoryId").GetInt32(), r.GetProperty("repositoryName").GetString() ?? ""))
            .ToList();
        var results = requested
            .Select(r => perRepository?.Invoke(r) ?? new { repositoryId = r.RepositoryId, repositoryName = r.RepositoryName, outcome = "Removed" })
            .ToList();
        var failed = results.Any(r => ManifestTestFixture.ToJson(r).GetProperty("outcome").GetString() == "Failed");
        return new WorkerCommandResponse(true, new { success = !failed, results, releasedWatchers = Array.Empty<string>() }, null);
    }

    public static object Failed(Requested repository, string failureKind, string failedPath, string errorMessage) => new
    {
        repositoryId = repository.RepositoryId,
        repositoryName = repository.RepositoryName,
        outcome = "Failed",
        failureKind,
        failedPath,
        errorMessage,
    };
}
