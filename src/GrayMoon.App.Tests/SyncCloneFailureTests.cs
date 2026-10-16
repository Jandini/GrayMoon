using System.Text.Json;
using GrayMoon.Abstractions.Worker;
using GrayMoon.App.Components.Pages;
using GrayMoon.App.Data;
using GrayMoon.App.Models;
using GrayMoon.App.Services.Git;
using GrayMoon.Common.Git;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GrayMoon.App.Tests;

public sealed class SyncCloneFailureTests
{
    private const string Secret = "SSOQUERYTOKEN";
    private const string SsoUrl = "https://github.com/enterprises/acme-labs-global/sso?authorization_request=" + Secret;

    [Fact]
    public async Task Sso_clone_failure_stays_not_cloned_and_keeps_the_previous_branch()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        ctx.WorkerBridge.Respond("SyncRepository", SsoPayload(), success: true);

        var results = await SyncAsync(ctx);
        var info = results[ctx.RepositoryId];

        Assert.True(info.FatalSyncFailure);
        Assert.True(info.NotCloned);
        Assert.True(info.SsoAuthorizationRequired);
        Assert.Equal(SsoUrl, info.SsoAuthorizationUrl);
        Assert.Equal("Visual Studio", info.SsoOAuthApplication);
        Assert.Equal("acme-labs", info.SsoOrganization);
        Assert.DoesNotContain(Secret, info.ErrorMessage, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, JsonSerializer.Serialize(info.Snapshot), StringComparison.Ordinal);
        Assert.False(info.Snapshot!.IdentityProbed);

        var link = await ctx.ReadLinkAsync();
        Assert.Equal(RepoSyncStatus.NotCloned, link.SyncStatus);
        Assert.Equal("feature/x", link.BranchName);
        Assert.Equal("1.0.0", link.GitVersion);
        Assert.False(await ReadInSyncAsync(ctx));
    }

    [Fact]
    public async Task Older_payload_without_the_new_fields_still_syncs()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        ctx.WorkerBridge.Respond("SyncRepository", new
        {
            version = "1.2.3",
            branch = "main",
            defaultBranch = "main",
        });

        var results = await SyncAsync(ctx);
        var info = results[ctx.RepositoryId];

        Assert.False(info.FatalSyncFailure);
        Assert.False(info.NotCloned);
        Assert.Null(info.SsoAuthorizationUrl);
        Assert.Equal("main", info.Branch);

        var link = await ctx.ReadLinkAsync();
        Assert.Equal(RepoSyncStatus.InSync, link.SyncStatus);
        Assert.Equal("main", link.BranchName);
    }

    [Fact]
    public async Task Duplicate_fetch_error_is_shown_once()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        ctx.WorkerBridge.Respond("SyncRepository", new
        {
            success = false,
            errorMessage = "Git fetch failed.",
            gitFetchError = "Git fetch failed.",
            version = "-",
            branch = "-",
        });

        var results = await SyncAsync(ctx);
        var info = results[ctx.RepositoryId];

        Assert.Equal("Git fetch failed.", info.ErrorMessage);
        Assert.True(info.FatalSyncFailure);
        Assert.False(info.NotCloned);
        Assert.Equal(RepoSyncStatus.Error, (await ctx.ReadLinkAsync()).SyncStatus);
        Assert.Equal("feature/x", (await ctx.ReadLinkAsync()).BranchName);
    }

    [Fact]
    public async Task Basic_workspace_success_with_no_version_is_in_sync()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        ctx.WorkerBridge.Respond("SyncRepository", new
        {
            success = true,
            version = "-",
            branch = "main",
            defaultBranch = "main",
        });

        var results = await SyncAsync(ctx);

        Assert.False(results[ctx.RepositoryId].FatalSyncFailure);
        Assert.Equal(RepoSyncStatus.InSync, results[ctx.RepositoryId].ToProgressStatus());
        Assert.True(await ReadInSyncAsync(ctx));
        Assert.Equal(RepoSyncStatus.InSync, (await ctx.ReadLinkAsync()).SyncStatus);
    }

    [Fact]
    public async Task One_sso_failure_does_not_stop_the_other_repository()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        var secondId = await AddRepositoryAsync(ctx, "second-repo");
        var entered = 0;
        var allEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ctx.WorkerBridge.RespondAsync("SyncRepository", async (args, _) =>
        {
            if (Interlocked.Increment(ref entered) == 2)
                allEntered.TrySetResult();
            await release.Task;
            var name = args.GetType().GetProperty("repositoryName")?.GetValue(args) as string;
            var data = name == "graymoon-api" ? SsoPayload() : SuccessPayload();
            return new WorkerCommandResponse(true, data, null);
        });

        await using var scope = ctx.CreateScope();
        var git = scope.ServiceProvider.GetRequiredService<WorkspaceGitService>();
        var run = git.SyncAsync(ctx.WorkspaceId, await ctx.GetSpecialContextIdAsync());
        await allEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        release.TrySetResult();
        var results = await run.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(2, ctx.WorkerBridge.Calls.Count(call => call.Command == "SyncRepository"));
        Assert.True(results[ctx.RepositoryId].FatalSyncFailure);
        Assert.Equal(SsoUrl, results[ctx.RepositoryId].SsoAuthorizationUrl);
        Assert.DoesNotContain(Secret, results[ctx.RepositoryId].ErrorMessage, StringComparison.Ordinal);
        Assert.Null(results[secondId].SsoAuthorizationUrl);
        Assert.False(results[secondId].FatalSyncFailure);

        var failed = await ctx.ReadLinkAsync();
        Assert.Equal(RepoSyncStatus.NotCloned, failed.SyncStatus);
        Assert.Equal(RepoSyncStatus.InSync, await ReadStatusAsync(ctx, secondId));
        Assert.False(await ReadInSyncAsync(ctx));
    }

    [Fact]
    public void Progress_status_does_not_treat_an_error_string_as_in_sync()
    {
        Assert.Equal(RepoSyncStatus.NotCloned, new RepoGitVersionInfo { FatalSyncFailure = true, NotCloned = true }.ToProgressStatus());
        Assert.Equal(RepoSyncStatus.Error, new RepoGitVersionInfo { FatalSyncFailure = true, ErrorMessage = "Git fetch failed.", Branch = "feature/x" }.ToProgressStatus());
        Assert.Equal(RepoSyncStatus.InSync, new RepoGitVersionInfo { Branch = "main", ErrorMessage = "GitVersion warning", Version = "-" }.ToProgressStatus());
        Assert.Equal(RepoSyncStatus.Error, new RepoGitVersionInfo { ErrorMessage = "Git clone failed.", Branch = "-", Version = "-" }.ToProgressStatus());
    }

    [Fact]
    public void Sync_alert_replaces_the_previous_error_and_a_success_clears_only_that_repository()
    {
        var errors = new Dictionary<int, string> { [1] = "old failure", [2] = "other repo" };
        var sso = new Dictionary<int, RepositorySsoPresentation>();

        RepositorySyncAlert.Apply(errors, sso, 1, new RepoGitVersionInfo
        {
            FatalSyncFailure = true,
            NotCloned = true,
            SsoAuthorizationRequired = true,
            SsoOAuthApplication = "Visual Studio",
            SsoOrganization = "acme-labs",
            SsoAuthorizationUrl = SsoUrl,
            ErrorMessage = "GitHub SSO authorization required. Reauthorize Visual Studio to access acme-labs. Complete authorization in your browser, then retry cloning.",
        });

        Assert.Equal("other repo", errors[2]);
        Assert.DoesNotContain("old failure", errors[1], StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, sso[1].VisibleText, StringComparison.Ordinal);
        Assert.DoesNotContain("authorization_request", sso[1].VisibleText, StringComparison.OrdinalIgnoreCase);
        Assert.True(sso[1].ShowsAuthorize);
        Assert.Contains("Visual Studio", sso[1].Explanation, StringComparison.Ordinal);
        Assert.Contains("acme-labs", sso[1].Explanation, StringComparison.Ordinal);

        RepositorySyncAlert.Apply(errors, sso, 1, new RepoGitVersionInfo
        {
            FatalSyncFailure = true,
            ErrorMessage = "Git could not access this repository (HTTP 403).",
        });
        Assert.Equal("Git could not access this repository (HTTP 403).", errors[1]);
        Assert.False(sso.ContainsKey(1));
        Assert.Equal("other repo", errors[2]);

        RepositorySyncAlert.Apply(errors, sso, 1, new RepoGitVersionInfo { Branch = "main", Version = "1.0.0" });
        Assert.False(errors.ContainsKey(1));
        Assert.Equal("other repo", errors[2]);
    }

    [Fact]
    public void Malformed_authorization_url_is_not_a_button_and_stale_context_is_ignored()
    {
        var presentation = RepositorySsoPresentation.TryCreate(true, "Visual Studio", "acme-labs", "javascript:alert(1)");
        Assert.NotNull(presentation);
        Assert.False(presentation.ShowsAuthorize);
        Assert.DoesNotContain("javascript", presentation.VisibleText, StringComparison.OrdinalIgnoreCase);

        Assert.False(RepositorySyncAlert.ShouldApply(jobGeneration: 1, currentGeneration: 2));
        Assert.True(RepositorySyncAlert.ShouldApply(4, 4));
    }

    private static object SsoPayload() => new
    {
        success = false,
        errorMessage = "GitHub SSO authorization required. Reauthorize Visual Studio to access acme-labs. Complete authorization in your browser, then retry cloning.",
        failureKind = GitRemoteFailureKind.SsoAuthorizationRequired,
        ssoAuthorizationUrl = SsoUrl,
        ssoOAuthApplication = "Visual Studio",
        ssoOrganization = "acme-labs",
        notCloned = true,
        version = "-",
        branch = "-",
    };

    private static object SuccessPayload() => new
    {
        success = true,
        version = "2.0.0",
        branch = "main",
        defaultBranch = "main",
        outgoingCommits = 0,
        incomingCommits = 0,
        hasUpstream = true,
        upstreamProbed = true,
    };

    private static async Task<IReadOnlyDictionary<int, RepoGitVersionInfo>> SyncAsync(SyncStateTestContext ctx)
    {
        await using var scope = ctx.CreateScope();
        var git = scope.ServiceProvider.GetRequiredService<WorkspaceGitService>();
        return await git.SyncAsync(ctx.WorkspaceId, await ctx.GetSpecialContextIdAsync());
    }

    private static async Task<bool> ReadInSyncAsync(SyncStateTestContext ctx)
    {
        await using var scope = ctx.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Workspaces.AsNoTracking()
            .Where(workspace => workspace.WorkspaceId == ctx.WorkspaceId)
            .Select(workspace => workspace.IsInSync)
            .FirstAsync();
    }

    private static async Task<RepoSyncStatus> ReadStatusAsync(SyncStateTestContext ctx, int repositoryId)
    {
        await using var scope = ctx.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.WorkspaceRepositories.AsNoTracking()
            .Where(link => link.WorkspaceId == ctx.WorkspaceId && link.RepositoryId == repositoryId)
            .Select(link => link.SyncStatus)
            .FirstAsync();
    }

    private static async Task<int> AddRepositoryAsync(SyncStateTestContext ctx, string name)
    {
        await using var scope = ctx.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var connectorId = await db.Connectors.Select(connector => connector.ConnectorId).FirstAsync();
        var repo = new Repository
        {
            ConnectorId = connectorId,
            RepositoryName = name,
            OrgName = "acme",
            Visibility = "Public",
            CloneUrl = $"https://github.com/acme/{name}.git",
        };
        db.Repositories.Add(repo);
        await db.SaveChangesAsync();
        db.WorkspaceRepositories.Add(new WorkspaceRepositoryLink
        {
            WorkspaceId = ctx.WorkspaceId,
            RepositoryId = repo.RepositoryId,
            BranchName = "main",
            DefaultBranchName = "main",
            SyncStatus = RepoSyncStatus.InSync,
        });
        await db.SaveChangesAsync();
        return repo.RepositoryId;
    }
}
