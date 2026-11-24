using System.Text.Json;
using GrayMoon.Abstractions.Notifications;
using GrayMoon.Abstractions.Worker;
using GrayMoon.App.Data;
using GrayMoon.App.Models;
using GrayMoon.App.Services.Worker;
using GrayMoon.App.Services.Workspaces;
using GrayMoon.Application.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace GrayMoon.App.Tests;

/// <summary>
/// The file-version check reads a <c>{@Repo:commit}</c> value from the HEAD commit this context has stored. It used
/// to ask the Worker (<c>GetHeadCommits</c>, three git processes per repository) on every check, and the check runs
/// after every hook sync: 512 calls and 496 seconds of Worker time on one measured day.
/// </summary>
public sealed class FileVersionStoredCommitTests
{
    private const string CommitToken = "@" + EffectiveVersionScenario.ProducerName + ":commit";

    [Fact]
    public async Task The_check_sends_the_stored_commit_and_never_asks_the_Worker_for_it()
    {
        await using var s = await EffectiveVersionScenario.CreateAsync();
        await AddCommitVersionFileAsync(s);
        await s.MutateStateAsync(s.ProducerLinkId, state => state.HeadCommit = " " + EffectiveVersionScenario.MovedSha + " ");

        var sent = await CheckAsync(s, s.Special);

        Assert.Contains(EffectiveVersionScenario.MovedSha, sent);
        Assert.DoesNotContain(" " + EffectiveVersionScenario.MovedSha, sent);
        Assert.DoesNotContain(s.Ctx.WorkerBridge.Calls, c => c.Command == WorkerHubMethods.GetHeadCommits);
    }

    [Fact]
    public async Task A_Feature_sends_its_own_commit_never_the_Workspace_one()
    {
        await using var s = await EffectiveVersionScenario.CreateAsync();
        await AddCommitVersionFileAsync(s);
        await s.MutateStateAsync(s.ProducerLinkId, state => state.HeadCommit = EffectiveVersionScenario.MovedSha);
        var feature = await s.AddFeatureAsync();

        var sent = await CheckAsync(s, feature);

        Assert.Contains(EffectiveVersionScenario.BaseSha, sent);
        Assert.DoesNotContain(EffectiveVersionScenario.MovedSha, sent);
    }

    [Fact]
    public async Task A_Feature_without_a_state_row_sends_no_commit_and_reports_the_token_as_not_checked()
    {
        await using var s = await EffectiveVersionScenario.CreateAsync();
        await AddCommitVersionFileAsync(s);
        await s.MutateStateAsync(s.ProducerLinkId, state => state.HeadCommit = EffectiveVersionScenario.MovedSha);
        var feature = await s.AddFeatureAsync(withStateRows: false);

        var sent = await CheckAsync(s, feature);

        // The file is sent with nothing to compare (the Worker still reports whether it is there).
        Assert.Contains("\"expectedValues\":{}", sent);
        var row = Assert.Single(await ReadStatusesAsync(s, feature));
        Assert.Equal(CommitToken, row.TokenName);
        Assert.Equal(WorkspaceFileVersionService.CommitNotRecordedReason(EffectiveVersionScenario.ProducerName), row.NotCheckedReason);
    }

    [Fact]
    public async Task A_token_without_a_stored_commit_is_not_checked_instead_of_up_to_date()
    {
        await using var s = await EffectiveVersionScenario.CreateAsync();
        await AddCommitVersionFileAsync(s);
        await s.MutateStateAsync(s.ProducerLinkId, state => state.HeadCommit = null);
        // What an earlier check found. The old code deleted it and left nothing, so the file looked up to date.
        await s.WithDbAsync(db =>
        {
            db.WorkspaceFileLineStatuses.Add(new WorkspaceFileLineStatus
            {
                WorkspaceId = s.Ctx.WorkspaceId,
                WorkspaceFeatureContextId = s.Special.Value,
                RepositoryId = s.ConsumerId,
                FilePath = "version.props",
                FileName = "version.props",
                TokenName = CommitToken,
                CurrentValue = "aaaaaaa",
                ExpectedValue = "bbbbbbb",
            });
            return Task.CompletedTask;
        });

        var sent = await CheckAsync(s, s.Special);

        // The file is sent with nothing to compare (the Worker still reports whether it is there).
        Assert.Contains("\"expectedValues\":{}", sent);
        Assert.DoesNotContain(s.Ctx.WorkerBridge.Calls, c => c.Command == WorkerHubMethods.GetHeadCommits);
        var row = Assert.Single(await ReadStatusesAsync(s, s.Special));
        Assert.Equal(s.ConsumerId, row.RepositoryId);
        Assert.Equal(CommitToken, row.TokenName);
        Assert.Null(row.ExpectedValue);
        Assert.Equal(WorkspaceFileVersionService.CommitNotRecordedReason(EffectiveVersionScenario.ProducerName), row.NotCheckedReason);
        Assert.Equal(1, (await s.ReadStateAsync(s.ConsumerLinkId)).OutOfDateFileRepos);
    }

    [Fact]
    public async Task A_file_with_no_comparable_token_is_still_asked_for_and_a_missing_one_is_not_reported_as_not_checked()
    {
        // Such a file was not sent to the Worker at all, so the check never learned that it had been deleted.
        await using var s = await EffectiveVersionScenario.CreateAsync();
        await AddCommitVersionFileAsync(s);
        await s.MutateStateAsync(s.ProducerLinkId, state => state.HeadCommit = null);
        s.Ctx.WorkerBridge.Respond("CheckFileVersions", new
        {
            files = new[] { new { repositoryName = EffectiveVersionScenario.ConsumerName, filePath = "version.props", fileName = "version.props", fileMissing = true } },
        });

        await using (var scope = s.Ctx.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<WorkspaceFileVersionService>()
                .CheckAndPersistFileVersionStatusAsync(s.Ctx.WorkspaceId, s.Special, CancellationToken.None);
        }

        Assert.Empty(await ReadStatusesAsync(s, s.Special));
        Assert.Null((await s.ReadStateAsync(s.ConsumerLinkId)).OutOfDateFileRepos);
        await using var db = await s.Ctx.Resolve<IDbContextFactory<AppDbContext>>().CreateDbContextAsync();
        Assert.True(await db.WorkspaceFileContextStates.AsNoTracking()
            .Where(state => state.WorkspaceFeatureContextId == s.Special.Value)
            .Select(state => state.IsMissingOnDisk)
            .SingleAsync());
    }

    [Fact]
    public async Task The_update_still_reads_the_commit_from_the_checkout_and_writes_that_one()
    {
        await using var s = await EffectiveVersionScenario.CreateAsync();
        await AddCommitVersionFileAsync(s);
        await s.MutateStateAsync(s.ProducerLinkId, state => state.HeadCommit = EffectiveVersionScenario.BaseSha);
        s.Ctx.WorkerBridge.Respond(WorkerHubMethods.GetHeadCommits, new
        {
            commits = new Dictionary<string, string> { [EffectiveVersionScenario.ProducerName] = EffectiveVersionScenario.MovedSha },
        });
        s.Ctx.WorkerBridge.Respond("UpdateFileVersions", new { updatedCount = 1 });
        s.Ctx.WorkerBridge.Calls.Clear();

        await using var scope = s.Ctx.CreateScope();
        var result = await scope.ServiceProvider.GetRequiredService<WorkspaceFileVersionService>()
            .UpdateAllVersionsAsync(s.Ctx.WorkspaceId, s.Special);

        Assert.Null(result.Error);
        Assert.Single(s.Ctx.WorkerBridge.Calls, c => c.Command == WorkerHubMethods.GetHeadCommits);
        var update = JsonSerializer.Serialize(Assert.Single(s.Ctx.WorkerBridge.Calls, c => c.Command == "UpdateFileVersions").Args);
        Assert.Contains(EffectiveVersionScenario.MovedSha, update);
        Assert.DoesNotContain(EffectiveVersionScenario.BaseSha, update);
    }

    [Fact]
    public async Task A_hook_sync_with_a_new_commit_leads_to_a_check_with_that_commit()
    {
        await using var s = await EffectiveVersionScenario.CreateAsync();
        await AddCommitVersionFileAsync(s);
        await s.MutateStateAsync(s.ProducerLinkId, state => state.HeadCommit = EffectiveVersionScenario.BaseSha);
        s.Ctx.WorkerBridge.Respond("CheckFileVersions", new { files = Array.Empty<object>() });
        s.Ctx.WorkerBridge.Calls.Clear();

        await using var scope = s.Ctx.CreateScope();
        await scope.ServiceProvider.GetRequiredService<SyncCommandHandler>().HandleAsync(new RepositorySyncNotification
        {
            WorkspaceId = s.Ctx.WorkspaceId,
            RepositoryId = s.ProducerId,
            Version = "2.3.1-abc.4",
            Branch = "feature/x",
            State = new RepositoryStateSnapshot
            {
                BranchName = "feature/x",
                HeadCommit = EffectiveVersionScenario.MovedSha,
                GitVersion = "2.3.1-abc.4",
                IdentityProbed = true,
                GitVersionProbed = true,
            },
        });

        var check = JsonSerializer.Serialize(Assert.Single(s.Ctx.WorkerBridge.Calls, c => c.Command == "CheckFileVersions").Args);
        Assert.Contains(EffectiveVersionScenario.MovedSha, check);
        Assert.DoesNotContain(s.Ctx.WorkerBridge.Calls, c => c.Command == WorkerHubMethods.GetHeadCommits);
    }

    [Fact]
    public async Task An_operation_start_reads_the_commits_version_files_use_and_stores_them()
    {
        await using var s = await EffectiveVersionScenario.CreateAsync();
        await AddCommitVersionFileAsync(s);
        // HEAD was moved with git reset: no hook told GrayMoon.
        await s.MutateStateAsync(s.ProducerLinkId, state => state.HeadCommit = EffectiveVersionScenario.BaseSha);
        s.Ctx.WorkerBridge.Respond(WorkerHubMethods.GetHeadCommits, new
        {
            commits = new Dictionary<string, string> { [EffectiveVersionScenario.ProducerName] = EffectiveVersionScenario.MovedSha },
        });

        await using var scope = s.Ctx.CreateScope();
        var (commits, error) = await scope.ServiceProvider.GetRequiredService<WorkspaceFileVersionService>()
            .ReadLiveCommitTokenHeadsAsync(s.Ctx.WorkspaceId, s.Special);
        var changed = await scope.ServiceProvider.GetRequiredService<WorkspaceRepositoryStateWriter>()
            .WriteHeadCommitsAsync(s.Special, commits);

        Assert.Null(error);
        Assert.Equal(1, changed);
        Assert.Equal(EffectiveVersionScenario.MovedSha, (await s.ReadStateAsync(s.ProducerLinkId)).HeadCommit);
        // Only the repository a commit token names is read and written.
        Assert.Equal([s.ProducerId], commits.Keys);
    }

    [Fact]
    public async Task An_operation_start_names_the_repository_whose_commit_cannot_be_read_and_stores_nothing()
    {
        await using var s = await EffectiveVersionScenario.CreateAsync();
        await AddCommitVersionFileAsync(s);
        await s.MutateStateAsync(s.ProducerLinkId, state => state.HeadCommit = EffectiveVersionScenario.BaseSha);
        s.Ctx.WorkerBridge.Respond(WorkerHubMethods.GetHeadCommits, new { commits = new Dictionary<string, string>() });

        await using var scope = s.Ctx.CreateScope();
        var (commits, error) = await scope.ServiceProvider.GetRequiredService<WorkspaceFileVersionService>()
            .ReadLiveCommitTokenHeadsAsync(s.Ctx.WorkspaceId, s.Special);

        Assert.Empty(commits);
        Assert.Contains(EffectiveVersionScenario.ProducerName, error);
        Assert.Equal(EffectiveVersionScenario.BaseSha, (await s.ReadStateAsync(s.ProducerLinkId)).HeadCommit);
    }

    [Fact]
    public async Task An_operation_start_makes_no_Worker_call_when_no_version_file_uses_a_commit()
    {
        await using var s = await EffectiveVersionScenario.CreateAsync();
        s.Ctx.WorkerBridge.Calls.Clear();

        await using var scope = s.Ctx.CreateScope();
        var (commits, error) = await scope.ServiceProvider.GetRequiredService<WorkspaceFileVersionService>()
            .ReadLiveCommitTokenHeadsAsync(s.Ctx.WorkspaceId, s.Special);

        Assert.Null(error);
        Assert.Empty(commits);
        Assert.Empty(s.Ctx.WorkerBridge.Calls);
    }

    [Fact]
    public async Task Update_stops_before_any_change_when_a_commit_version_files_use_cannot_be_read()
    {
        await using var s = await EffectiveVersionScenario.CreateAsync(configureServices: Policy.Characterization.PolicyHarness.AddFacades);
        await AddCommitVersionFileAsync(s);
        s.Ctx.WorkerBridge.Respond(WorkerHubMethods.GetHeadCommits, new { commits = new Dictionary<string, string>() });
        s.Ctx.WorkerBridge.Calls.Clear();
        var levelErrors = new List<string>();

        await using var scope = s.Ctx.CreateScope();
        var result = await scope.ServiceProvider.GetRequiredService<GrayMoon.App.Services.Orchestration.DependencyUpdateOrchestrator>().RunAsync(
            s.Ctx.WorkspaceId,
            s.Special,
            CancellationToken.None,
            progress: null,
            onRepoError: (_, _) => { },
            onLevelError: (_, message) => levelErrors.Add(message));

        Assert.False(result.Success);
        Assert.Contains(EffectiveVersionScenario.ProducerName, Assert.Single(levelErrors));
        Assert.Equal([WorkerHubMethods.GetHeadCommits], s.Ctx.WorkerBridge.Calls.Select(c => c.Command));
    }

    [Fact]
    public async Task Update_decides_from_the_commits_in_the_checkouts_not_from_an_old_stored_one()
    {
        await using var s = await EffectiveVersionScenario.CreateAsync(configureServices: Policy.Characterization.PolicyHarness.AddFacades);
        await AddCommitVersionFileAsync(s);
        // HEAD was moved with git reset: the stored commit is old and no hook said so.
        await s.MutateStateAsync(s.ProducerLinkId, state => state.HeadCommit = EffectiveVersionScenario.BaseSha);
        s.Ctx.WorkerBridge.Respond(WorkerHubMethods.GetHeadCommits, new
        {
            commits = new Dictionary<string, string> { [EffectiveVersionScenario.ProducerName] = EffectiveVersionScenario.MovedSha },
        });
        s.Ctx.WorkerBridge.Respond("CheckFileVersions", new { files = Array.Empty<object>() });
        s.Ctx.WorkerBridge.Respond("RefreshRepositoryProjects", data: null);
        s.Ctx.WorkerBridge.Calls.Clear();

        await using var scope = s.Ctx.CreateScope();
        await scope.ServiceProvider.GetRequiredService<GrayMoon.App.Services.Orchestration.DependencyUpdateOrchestrator>().RunAsync(
            s.Ctx.WorkspaceId,
            s.Special,
            CancellationToken.None,
            progress: null,
            onRepoError: (_, _) => { },
            onLevelError: (_, _) => { });

        Assert.Equal(EffectiveVersionScenario.MovedSha, (await s.ReadStateAsync(s.ProducerLinkId)).HeadCommit);
        // The first two Worker calls of the run: the commits, then one check with the commit just read.
        var calls = s.Ctx.WorkerBridge.Calls.ToList();
        Assert.Equal(WorkerHubMethods.GetHeadCommits, calls[0].Command);
        Assert.Equal("CheckFileVersions", calls[1].Command);
        Assert.Contains(EffectiveVersionScenario.MovedSha, JsonSerializer.Serialize(calls[1].Args));
    }

    [Fact]
    public async Task A_burst_of_hook_syncs_runs_one_check_and_announces_the_context_once()
    {
        await using var s = await EffectiveVersionScenario.CreateAsync();
        await AddCommitVersionFileAsync(s);
        s.Ctx.WorkerBridge.Respond("CheckFileVersions", new { files = Array.Empty<object>() });
        s.Ctx.WorkerBridge.Calls.Clear();
        var queue = new WorkerSyncNotificationQueue(
            s.Ctx.Resolve<IServiceScopeFactory>(),
            new AppBackgroundWorkStateService(),
            NullLogger<WorkerSyncNotificationQueue>.Instance);
        var broadcastsBefore = s.Ctx.Broadcasts.Count;

        queue.Enqueue(HookSync(s, s.ProducerId, EffectiveVersionScenario.BaseSha));
        queue.Enqueue(HookSync(s, s.ConsumerId, EffectiveVersionScenario.ConsumerSha));
        queue.Enqueue(HookSync(s, s.ProducerId, EffectiveVersionScenario.MovedSha));
        var applied = queue.WaitForPendingAsync(s.Ctx.WorkspaceId, CancellationToken.None);
        await queue.StartAsync(CancellationToken.None);
        await applied.WaitAsync(TimeSpan.FromSeconds(30));
        await queue.StopAsync(CancellationToken.None);

        // Three state writes, the last one wins; one recompute, so one check, with the last commit.
        Assert.Equal(EffectiveVersionScenario.MovedSha, (await s.ReadStateAsync(s.ProducerLinkId)).HeadCommit);
        var check = JsonSerializer.Serialize(Assert.Single(s.Ctx.WorkerBridge.Calls, c => c.Command == "CheckFileVersions").Args);
        Assert.Contains(EffectiveVersionScenario.MovedSha, check);

        var sent = s.Ctx.Broadcasts.Skip(broadcastsBefore).Select(b => b.Method).ToList();
        Assert.Equal(2, sent.Count(m => m == "ContextRepositorySynced"));
        Assert.Single(sent, m => m == "ContextSynced");
        Assert.Single(sent, m => m == "WorkspaceSynced");
        Assert.Equal("WorkspaceSynced", sent[^1]);
    }

    [Fact]
    public async Task Create_Feature_decides_the_effective_versions_before_it_finishes()
    {
        // Measured 2026-11-24 on Edx1: the grid kept changing for 7 s after the overlay closed, because the versions
        // of the new Feature were first decided in the recompute of its first hook sync.
        await using var s = await EffectiveVersionScenario.CreateAsync();
        await s.WithDbAsync(db =>
        {
            foreach (var link in db.WorkspaceRepositories.Where(l => l.WorkspaceId == s.Ctx.WorkspaceId))
                link.BranchName = "release/2.4";
            return Task.CompletedTask;
        });
        s.Ctx.WorkerBridge.Respond(WorkerHubMethods.GetHeadCommits, new
        {
            commits = new Dictionary<string, string>
            {
                [EffectiveVersionScenario.ProducerName] = EffectiveVersionScenario.BaseSha,
                [EffectiveVersionScenario.ConsumerName] = EffectiveVersionScenario.ConsumerSha,
            },
            branches = new Dictionary<string, string>
            {
                [EffectiveVersionScenario.ProducerName] = "release/2.4",
                [EffectiveVersionScenario.ConsumerName] = "release/2.4",
            },
        });
        s.Ctx.WorkerBridge.Respond(WorkerHubMethods.CreateGitWorktree, new { success = true, worktreePath = @"C:\wt" });
        s.Ctx.WorkerBridge.Calls.Clear();

        await using var scope = s.Ctx.CreateScope();
        var created = await scope.ServiceProvider.GetRequiredService<IWorkspaceFeatureOperations>()
            .CreateFeatureAsync(s.Ctx.WorkspaceId, "feat-final", WorkspaceFeatureBaseKindApplication.CurrentWorkspace);

        Assert.True(created.Success, created.Error);
        // No hook sync has been applied: the decision is there when Create returns.
        var producer = await s.ReadStateAsync(s.ProducerLinkId, created.ContextId);
        Assert.Equal((int)EffectiveVersionKind.InheritedVerified, producer.EffectiveVersionKind);
        Assert.Equal(EffectiveVersionScenario.ParentVersion, producer.EffectiveVersion);
        Assert.Equal(1, s.BranchPointCalls);
    }

    [Fact]
    public async Task A_burst_of_hook_syncs_asks_an_unreachable_registry_once_not_once_per_notification()
    {
        // An unverifiable package is not stored as a decision to reuse, so every recompute asks again. With one
        // recompute per notification a burst asked the registry (and waited for its timeout) once per repository.
        await using var s = await EffectiveVersionScenario.CreateAsync();
        s.Registry.TimesOut(EffectiveVersionScenario.PackageId);
        s.Ctx.WorkerBridge.Calls.Clear();
        var lookupsBefore = s.Registry.Lookups;
        var queue = new WorkerSyncNotificationQueue(
            s.Ctx.Resolve<IServiceScopeFactory>(),
            new AppBackgroundWorkStateService(),
            NullLogger<WorkerSyncNotificationQueue>.Instance);

        queue.Enqueue(HookSync(s, s.ProducerId, EffectiveVersionScenario.BaseSha));
        queue.Enqueue(HookSync(s, s.ConsumerId, EffectiveVersionScenario.ConsumerSha));
        queue.Enqueue(HookSync(s, s.ProducerId, EffectiveVersionScenario.BaseSha));
        var applied = queue.WaitForPendingAsync(s.Ctx.WorkspaceId, CancellationToken.None);
        await queue.StartAsync(CancellationToken.None);
        await applied.WaitAsync(TimeSpan.FromSeconds(30));
        await queue.StopAsync(CancellationToken.None);

        Assert.Equal(1, s.Registry.Lookups - lookupsBefore);
        Assert.Equal(1, s.BranchPointCalls);
    }

    private static RepositorySyncNotification HookSync(EffectiveVersionScenario s, int repositoryId, string headCommit) => new()
    {
        WorkspaceId = s.Ctx.WorkspaceId,
        RepositoryId = repositoryId,
        Branch = "feature/x",
        State = new RepositoryStateSnapshot
        {
            BranchName = "feature/x",
            HeadCommit = headCommit,
            IdentityProbed = true,
        },
    };

    private static async Task AddCommitVersionFileAsync(EffectiveVersionScenario s)
        => await s.WithDbAsync(async db =>
        {
            var file = new WorkspaceFile { WorkspaceId = s.Ctx.WorkspaceId, RepositoryId = s.ConsumerId, FilePath = "version.props", FileName = "version.props" };
            db.WorkspaceFiles.Add(file);
            await db.SaveChangesAsync();
            db.WorkspaceFileVersionConfigs.Add(new WorkspaceFileVersionConfig
            {
                FileId = file.FileId,
                VersionPattern = $"API_SHA={{@{EffectiveVersionScenario.ProducerName}:commit}}",
            });
        });

    /// <summary>Runs the file-version check and returns the request it sent to the Worker, or null when it sent nothing.</summary>
    private static async Task<string?> CheckAsync(EffectiveVersionScenario s, WorkspaceFeatureContextId contextId)
    {
        s.Ctx.WorkerBridge.Respond("CheckFileVersions", new { files = Array.Empty<object>() });
        s.Ctx.WorkerBridge.Calls.Clear();
        await using var scope = s.Ctx.CreateScope();
        await scope.ServiceProvider.GetRequiredService<WorkspaceFileVersionService>()
            .CheckAndPersistFileVersionStatusAsync(s.Ctx.WorkspaceId, contextId, CancellationToken.None);
        var call = s.Ctx.WorkerBridge.Calls.Where(c => c.Command == "CheckFileVersions").Select(c => c.Args).FirstOrDefault();
        return call is null ? null : JsonSerializer.Serialize(call);
    }

    private static async Task<List<WorkspaceFileLineStatus>> ReadStatusesAsync(EffectiveVersionScenario s, WorkspaceFeatureContextId contextId)
    {
        await using var db = await s.Ctx.Resolve<IDbContextFactory<AppDbContext>>().CreateDbContextAsync();
        return await db.WorkspaceFileLineStatuses.AsNoTracking()
            .Where(row => row.WorkspaceFeatureContextId == contextId.Value)
            .ToListAsync();
    }
}
