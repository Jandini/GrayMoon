using System.Text.Json;
using GrayMoon.Abstractions.Worker;
using GrayMoon.App.Models;
using GrayMoon.App.Services.Ai;
using GrayMoon.App.Services.GitChanges;
using GrayMoon.App.Services.Orchestration;
using GrayMoon.App.Services.Worker;
using GrayMoon.Application.Ai;
using GrayMoon.Application.Features;
using GrayMoon.Common.Ai;
using GrayMoon.Common.Git;
using GrayMoon.Common.Manifest;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GrayMoon.App.Tests;

public sealed class AiGenerationTests
{
    private static readonly WorkspaceFeatureContextId Context = new(1);

    private static string Definition(WorkspaceManifest manifest) => new WorkspaceManifestFile().Serialize(manifest);

    // ---- graymoon.json: the ai section is owned by the file ----

    [Fact]
    public async Task Rewriting_the_definition_keeps_the_ai_section_exactly()
    {
        await using var fixture = await ManifestTestFixture.CreateAsync();
        var workspace = await fixture.SeedWorkspaceWithRootRepositoryAsync();
        var service = fixture.CreateService();
        var ai = new WorkspaceManifestAi([new("claude", "sonnet"), new("codex"), new("future-tool", "x1")]);
        var definition = Definition(await service.BuildFromDatabaseAsync(workspace.WorkspaceId) with { Ai = ai });
        fixture.Bridge.RespondWithFile(definition);

        var result = await service.WriteAuthoritativeManifestAsync(workspace.WorkspaceId);

        Assert.True(result.Success, result.Error);
        var write = Assert.Single(fixture.Bridge.Sent, s => s.Command == WorkerHubMethods.WriteRepositoryFile);
        Assert.Equal(definition, ManifestTestFixture.ToJson(write.Args).GetProperty("content").GetString());
    }

    [Fact]
    public async Task Drift_ignores_the_ai_section()
    {
        await using var fixture = await ManifestTestFixture.CreateAsync();
        var workspace = await fixture.SeedWorkspaceWithRootRepositoryAsync();
        var service = fixture.CreateService();
        fixture.Bridge.RespondWithFile(Definition(await service.BuildFromDatabaseAsync(workspace.WorkspaceId) with
        {
            Ai = new WorkspaceManifestAi([new("claude", "opus")]),
        }));

        Assert.False((await service.DetectDriftAsync(workspace.WorkspaceId)).HasDrift);
    }

    [Fact]
    public async Task Read_ai_returns_the_section_and_reports_a_broken_definition()
    {
        await using var fixture = await ManifestTestFixture.CreateAsync();
        var workspace = await fixture.SeedWorkspaceWithRootRepositoryAsync();
        var service = fixture.CreateService();

        fixture.Bridge.RespondWithFile(Definition(await service.BuildFromDatabaseAsync(workspace.WorkspaceId) with
        {
            Ai = new WorkspaceManifestAi([new("codex", "gpt-5.1-codex")]),
        }));
        var (ai, error) = await service.ReadAiAsync(workspace.WorkspaceId);
        Assert.Null(error);
        Assert.Equal("gpt-5.1-codex", Assert.Single(ai!.Providers).Model);

        fixture.Bridge.RespondWithFile("{ not json");
        (ai, error) = await service.ReadAiAsync(workspace.WorkspaceId);
        Assert.Null(ai);
        Assert.NotNull(error);
    }

    // ---- Generation: one evidence request, one provider call, exact scope ----

    private sealed class Harness : IAsyncDisposable
    {
        public required ManifestTestFixture Fixture { get; init; }
        public required Workspace Workspace { get; init; }
        public required WorkspaceAiGenerationOperations Operations { get; init; }
        public required Dictionary<string, int> RepositoryIds { get; init; }
        public string ManifestContent { get; set; } = string.Empty;
        public Func<JsonElement, object> Evidence { get; set; } = _ => new { repositories = Array.Empty<object>() };
        public object Generation { get; set; } = new { success = true, json = """{"subject":"Add the feature","body":"Details."}""", errorKind = "None" };
        public bool ClaudeInstalled { get; set; } = true;

        public IEnumerable<(string Command, JsonElement Args)> Sent =>
            Fixture.Bridge.Sent.Select(s => (s.Command, ManifestTestFixture.ToJson(s.Args)));

        public ValueTask DisposeAsync() => Fixture.DisposeAsync();
    }

    private static async Task<Harness> CreateHarnessAsync(WorkspaceManifestAi? ai = null)
    {
        var fixture = await ManifestTestFixture.CreateAsync();
        var workspace = await fixture.SeedWorkspaceWithRootRepositoryAsync();
        var manifestService = fixture.CreateService();
        await using var db = await fixture.Factory.CreateDbContextAsync();
        var ids = await db.Repositories.ToDictionaryAsync(r => r.RepositoryName, r => r.RepositoryId);

        var harness = new Harness
        {
            Fixture = fixture,
            Workspace = workspace,
            RepositoryIds = ids,
            Operations = new WorkspaceAiGenerationOperations(
                fixture.Bridge,
                fixture.Factory,
                new FakeWorkspacePathResolver(fixture.Factory),
                manifestService,
                new AiProviderAvailabilityCache(NullLogger<AiProviderAvailabilityCache>.Instance),
                new WorkspaceAiProviderPins(fixture.Factory),
                Options.Create(new GitChangesOptions()),
                Options.Create(new AiGenerationOptions()),
                NullLogger<WorkspaceAiGenerationOperations>.Instance),
        };
        harness.ManifestContent = Definition(await manifestService.BuildFromDatabaseAsync(workspace.WorkspaceId) with { Ai = ai });

        fixture.Bridge.Handler = (command, args) => command switch
        {
            "GetFileContents" => new WorkerCommandResponse(true, new { content = harness.ManifestContent }, null),
            WorkerHubMethods.GetAiProviders => new WorkerCommandResponse(true, new
            {
                providers = AiProviderIds.All.Select(id => new
                {
                    providerId = id,
                    isInstalled = id == AiProviderIds.Claude && harness.ClaudeInstalled,
                    version = "1.0",
                    hasNoTools = id != AiProviderIds.Cursor,
                    problem = id == AiProviderIds.Claude && harness.ClaudeInstalled ? null : "not found",
                }).ToArray(),
            }, null),
            WorkerHubMethods.CollectGitEvidence => new WorkerCommandResponse(true, harness.Evidence(ManifestTestFixture.ToJson(args)), null),
            WorkerHubMethods.GenerateAiText => new WorkerCommandResponse(true, harness.Generation, null),
            _ => new WorkerCommandResponse(false, null, $"unexpected {command}"),
        };
        return harness;
    }

    private static object EvidenceFor(JsonElement request, Func<JsonElement, object>? perRepository = null) => new
    {
        repositories = request.GetProperty("repositories").EnumerateArray().Select(r => perRepository?.Invoke(r) ?? (object)new
        {
            repositoryId = r.GetProperty("repositoryId").GetInt32(),
            repositoryName = r.GetProperty("repositoryName").GetString(),
            success = true,
            totalFiles = 1,
            totalAdded = 3,
            files = new[] { new { path = "src/A.cs", change = "Modified", added = 3, deleted = 0, category = "Source", patch = "@@ -1 +1,3 @@\n+a\n+b\n+c\n" } },
        }).ToArray(),
        elapsedMs = 5,
    };

    [Fact]
    public async Task Commit_message_uses_one_evidence_request_and_exactly_one_provider_call_with_the_configured_model()
    {
        await using var h = await CreateHarnessAsync(new WorkspaceManifestAi([new("claude", "sonnet")]));
        h.Evidence = request => EvidenceFor(request);
        var scope = new AiCommitScope(h.Workspace.WorkspaceId, Context, GitCommitEvidenceMode.Staged,
            [new(h.RepositoryIds["Api"], "feature/x"), new(h.RepositoryIds["Web"], "feature/x")]);

        var result = await h.Operations.GenerateCommitMessageAsync(scope, AiProviderIds.Claude, progress: null, CancellationToken.None);

        Assert.True(result.Success, result.Failure?.Message);
        Assert.Equal("Add the feature\n\nDetails.", result.Value!.Message);

        var evidence = Assert.Single(h.Sent, s => s.Command == WorkerHubMethods.CollectGitEvidence).Args;
        Assert.Equal("Commit", evidence.GetProperty("kind").GetString());
        Assert.Equal("Staged", evidence.GetProperty("commitMode").GetString());
        Assert.Equal(new[] { "Api", "Web" }, evidence.GetProperty("repositories").EnumerateArray().Select(r => r.GetProperty("repositoryName").GetString()!).Order().ToArray());

        var generation = Assert.Single(h.Sent, s => s.Command == WorkerHubMethods.GenerateAiText).Args;
        Assert.Equal("claude", generation.GetProperty("providerId").GetString());
        Assert.Equal("sonnet", generation.GetProperty("model").GetString());
        var prompt = generation.GetProperty("userPrompt").GetString()!;
        Assert.Contains("## repository Api", prompt);
        Assert.Contains("## repository Web", prompt);
        Assert.Contains("branch: feature/x", prompt);
    }

    [Fact]
    public async Task Without_a_configured_model_claude_runs_on_haiku()
    {
        await using var h = await CreateHarnessAsync();
        h.Evidence = request => EvidenceFor(request);
        var scope = new AiCommitScope(h.Workspace.WorkspaceId, Context, GitCommitEvidenceMode.All, [new(h.RepositoryIds["Api"], "main")]);

        var result = await h.Operations.GenerateCommitMessageAsync(scope, AiProviderIds.Claude, progress: null, CancellationToken.None);

        Assert.True(result.Success, result.Failure?.Message);
        var generation = Assert.Single(h.Sent, s => s.Command == WorkerHubMethods.GenerateAiText).Args;
        Assert.Equal("haiku", generation.GetProperty("model").GetString());
        var menu = await h.Operations.GetProvidersAsync(h.Workspace.WorkspaceId, refresh: false, CancellationToken.None);
        Assert.Equal("haiku", menu.Options.Single(o => o.Id == AiProviderIds.Claude).Model);
        Assert.Equal("grok-4.7-low-fast", menu.Options.Single(o => o.Id == AiProviderIds.Cursor).Model);
    }

    [Fact]
    public async Task Pull_request_evidence_uses_the_selected_target_of_each_repository()
    {
        await using var h = await CreateHarnessAsync();
        h.Evidence = request => EvidenceFor(request);
        h.Generation = new { success = true, json = """{"title":"Ship it","description":"Summary."}""", errorKind = "None" };
        var scope = new AiPullRequestScope(h.Workspace.WorkspaceId, Context,
        [
            new(h.RepositoryIds["Api"], "feature/BAM-2874", "release/2.0"),
            new(h.RepositoryIds["Web"], "feature/BAM-2874", "develop"),
        ]);

        var result = await h.Operations.GeneratePullRequestTextAsync(scope, AiProviderIds.Claude, CancellationToken.None);

        Assert.True(result.Success, result.Failure?.Message);
        Assert.Equal("Ship it", result.Value!.Title);
        var repositories = Assert.Single(h.Sent, s => s.Command == WorkerHubMethods.CollectGitEvidence).Args
            .GetProperty("repositories").EnumerateArray()
            .ToDictionary(r => r.GetProperty("repositoryName").GetString()!, r => (Head: r.GetProperty("headBranch").GetString(), Base: r.GetProperty("baseBranch").GetString()));
        Assert.Equal(("feature/BAM-2874", "release/2.0"), repositories["Api"]);
        Assert.Equal(("feature/BAM-2874", "develop"), repositories["Web"]);
        Assert.Single(h.Sent, s => s.Command == WorkerHubMethods.GenerateAiText);
    }

    [Fact]
    public async Task One_repository_that_cannot_be_compared_stops_the_whole_generation()
    {
        await using var h = await CreateHarnessAsync();
        var webId = h.RepositoryIds["Web"];
        h.Evidence = request => EvidenceFor(request, r => r.GetProperty("repositoryId").GetInt32() == webId
            ? new
            {
                repositoryId = webId,
                repositoryName = "Web",
                success = false,
                errorCode = GitEvidenceErrorCodes.TargetBranchNotFound,
                errorMessage = "The target branch origin/release/9 is not in the local repository.",
            }
            : null!);
        var scope = new AiPullRequestScope(h.Workspace.WorkspaceId, Context,
        [
            new(h.RepositoryIds["Api"], "feature/a", "main"),
            new(webId, "feature/a", "release/9"),
        ]);

        var result = await h.Operations.GeneratePullRequestTextAsync(scope, AiProviderIds.Claude, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(AiGenerationErrorKind.Evidence, result.Failure!.Kind);
        Assert.Contains("Web", result.Failure.Message);
        Assert.DoesNotContain(h.Sent, s => s.Command == WorkerHubMethods.GenerateAiText);
    }

    [Fact]
    public async Task A_missing_or_identical_target_is_refused_before_any_git_work()
    {
        await using var h = await CreateHarnessAsync();
        var scope = new AiPullRequestScope(h.Workspace.WorkspaceId, Context,
        [
            new(h.RepositoryIds["Api"], "feature/a", ""),
            new(h.RepositoryIds["Web"], "feature/a", "feature/a"),
        ]);

        var result = await h.Operations.GeneratePullRequestTextAsync(scope, AiProviderIds.Claude, CancellationToken.None);

        Assert.Equal(AiGenerationErrorKind.Evidence, result.Failure!.Kind);
        Assert.Empty(h.Sent);
    }

    [Fact]
    public async Task Commit_message_reports_each_step_in_order_for_the_overlay()
    {
        await using var h = await CreateHarnessAsync(new WorkspaceManifestAi([new("claude", "sonnet")]));
        h.Evidence = request => EvidenceFor(request);
        var scope = new AiCommitScope(h.Workspace.WorkspaceId, Context, GitCommitEvidenceMode.All,
            [new(h.RepositoryIds["Api"], "main"), new(h.RepositoryIds["Web"], "main")]);
        var steps = new List<string>();

        var result = await h.Operations.GenerateCommitMessageAsync(scope, AiProviderIds.Claude, new SynchronousProgress(steps.Add), CancellationToken.None);

        Assert.True(result.Success, result.Failure?.Message);
        Assert.Equal(
            ["Collecting the changes of 2 repositories...", "Writing the commit message with Claude (sonnet)..."],
            steps);
    }

    [Fact]
    public async Task A_cli_that_is_not_installed_fails_before_collecting_evidence()
    {
        await using var h = await CreateHarnessAsync();
        h.ClaudeInstalled = false;
        var scope = new AiCommitScope(h.Workspace.WorkspaceId, Context, GitCommitEvidenceMode.All, [new(h.RepositoryIds["Api"], "main")]);

        var result = await h.Operations.GenerateCommitMessageAsync(scope, AiProviderIds.Claude, progress: null, CancellationToken.None);

        Assert.Equal(AiGenerationErrorKind.NotInstalled, result.Failure!.Kind);
        Assert.DoesNotContain(h.Sent, s => s.Command == WorkerHubMethods.CollectGitEvidence);
    }

    [Fact]
    public async Task An_unsafe_model_in_the_definition_is_a_configuration_error_and_nothing_runs()
    {
        await using var h = await CreateHarnessAsync(new WorkspaceManifestAi([new("claude", "sonnet & calc")]));
        var scope = new AiCommitScope(h.Workspace.WorkspaceId, Context, GitCommitEvidenceMode.All, [new(h.RepositoryIds["Api"], "main")]);

        var result = await h.Operations.GenerateCommitMessageAsync(scope, AiProviderIds.Claude, progress: null, CancellationToken.None);

        Assert.Equal(AiGenerationErrorKind.Configuration, result.Failure!.Kind);
        Assert.DoesNotContain(h.Sent, s => s.Command is WorkerHubMethods.CollectGitEvidence or WorkerHubMethods.GenerateAiText);
    }

    [Fact]
    public async Task No_changes_in_scope_is_reported_without_calling_the_provider()
    {
        await using var h = await CreateHarnessAsync();
        h.Evidence = request => EvidenceFor(request, r => new
        {
            repositoryId = r.GetProperty("repositoryId").GetInt32(),
            repositoryName = r.GetProperty("repositoryName").GetString(),
            success = true,
            totalFiles = 0,
        });
        var scope = new AiCommitScope(h.Workspace.WorkspaceId, Context, GitCommitEvidenceMode.Staged, [new(h.RepositoryIds["Api"], "main")]);

        var result = await h.Operations.GenerateCommitMessageAsync(scope, AiProviderIds.Claude, progress: null, CancellationToken.None);

        Assert.Equal(AiGenerationErrorKind.NothingToDescribe, result.Failure!.Kind);
        Assert.DoesNotContain(h.Sent, s => s.Command == WorkerHubMethods.GenerateAiText);
    }

    [Fact]
    public async Task A_provider_answer_that_is_not_the_expected_shape_is_invalid_output()
    {
        await using var h = await CreateHarnessAsync();
        h.Evidence = request => EvidenceFor(request);
        h.Generation = new { success = true, json = """{"title":"wrong shape"}""", errorKind = "None" };
        var scope = new AiCommitScope(h.Workspace.WorkspaceId, Context, GitCommitEvidenceMode.Staged, [new(h.RepositoryIds["Api"], "main")]);

        var result = await h.Operations.GenerateCommitMessageAsync(scope, AiProviderIds.Claude, progress: null, CancellationToken.None);

        Assert.Equal(AiGenerationErrorKind.InvalidOutput, result.Failure!.Kind);
    }

    [Fact]
    public async Task Provider_failures_from_the_worker_keep_their_kind()
    {
        await using var h = await CreateHarnessAsync();
        h.Evidence = request => EvidenceFor(request);
        h.Generation = new { success = false, errorKind = "NotAuthenticated", errorMessage = "The CLI is not signed in." };
        var scope = new AiCommitScope(h.Workspace.WorkspaceId, Context, GitCommitEvidenceMode.Staged, [new(h.RepositoryIds["Api"], "main")]);

        var result = await h.Operations.GenerateCommitMessageAsync(scope, AiProviderIds.Claude, progress: null, CancellationToken.None);

        Assert.Equal(AiGenerationErrorKind.NotAuthenticated, result.Failure!.Kind);
        Assert.Equal("The CLI is not signed in.", result.Failure.Message);
    }

    [Fact]
    public async Task Provider_menu_combines_installed_clis_models_and_the_local_pin()
    {
        await using var h = await CreateHarnessAsync(new WorkspaceManifestAi([new("claude", "opus"), new("codex", "bad model!"), new("other")]));

        await h.Operations.PinProviderAsync(h.Workspace.WorkspaceId, AiProviderIds.Claude, CancellationToken.None);
        var menu = await h.Operations.GetProvidersAsync(h.Workspace.WorkspaceId, refresh: false, CancellationToken.None);

        Assert.Equal(AiProviderIds.Claude, menu.PinnedProviderId);
        var claude = menu.Pinned!;
        Assert.True(claude.IsAvailable);
        Assert.Equal("opus", claude.Model);
        Assert.False(menu.Options.Single(o => o.Id == AiProviderIds.Cursor).IsAvailable);
        Assert.Contains("other", menu.Notice);
    }

    // ---- Local pin: one per workspace, never in graymoon.json ----

    [Fact]
    public async Task Pin_is_per_workspace_and_never_written_to_the_definition()
    {
        await using var fixture = await ManifestTestFixture.CreateAsync();
        var workspace = await fixture.SeedWorkspaceWithRootRepositoryAsync();
        var other = await fixture.AddWorkspaceAsync("other");
        var pins = new WorkspaceAiProviderPins(fixture.Factory);

        await pins.SetAsync(workspace.WorkspaceId, AiProviderIds.Codex, CancellationToken.None);
        await pins.SetAsync(other.WorkspaceId, AiProviderIds.Cursor, CancellationToken.None);
        Assert.Equal(AiProviderIds.Codex, await pins.GetAsync(workspace.WorkspaceId, CancellationToken.None));
        Assert.Equal(AiProviderIds.Cursor, await pins.GetAsync(other.WorkspaceId, CancellationToken.None));

        await pins.SetAsync(workspace.WorkspaceId, null, CancellationToken.None);
        Assert.Null(await pins.GetAsync(workspace.WorkspaceId, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => pins.SetAsync(workspace.WorkspaceId, "made-up", CancellationToken.None));

        await pins.SetAsync(workspace.WorkspaceId, AiProviderIds.Claude, CancellationToken.None);
        var service = fixture.CreateService();
        var text = Definition(await service.BuildFromDatabaseAsync(workspace.WorkspaceId));
        Assert.DoesNotContain("claude", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"ai\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Pin_column_migration_is_strict_step_10_and_idempotent()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<GrayMoon.App.Data.AppDbContext>().UseSqlite(connection).Options;
        await using var db = new GrayMoon.App.Data.AppDbContext(options);
        await db.Database.EnsureCreatedAsync();
        await using (var drop = connection.CreateCommand())
        {
            drop.CommandText = "ALTER TABLE \"Workspaces\" DROP COLUMN \"PinnedAiProviderId\"";
            await drop.ExecuteNonQueryAsync();
        }

        var step = Migrations.StrictSteps.Single(s => s.Version == 10);
        await step.Action(db);
        await Migrations.MigrateWorkspacePinnedAiProviderColumnAsync(db);

        await using var count = connection.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM pragma_table_info('Workspaces') WHERE name='PinnedAiProviderId'";
        Assert.Equal(1L, (long)(await count.ExecuteScalarAsync())!);
    }

    // ---- Applying results never overwrites the user's text ----

    [Fact]
    public void Generated_text_is_applied_only_into_an_untouched_empty_field_for_an_unchanged_scope()
    {
        Assert.True(AiTextApplyPolicy.Decide("", "", false, AiTextApplyPolicy.StaleChangesReason).ApplyDirectly);

        var edited = AiTextApplyPolicy.Decide("", "typed meanwhile", false, AiTextApplyPolicy.StaleChangesReason);
        Assert.False(edited.ApplyDirectly);
        Assert.Equal(AiTextApplyPolicy.EditedReason, edited.Reason);

        var existing = AiTextApplyPolicy.Decide("my own message", "my own message", false, AiTextApplyPolicy.StaleChangesReason);
        Assert.False(existing.ApplyDirectly);
        Assert.Null(existing.Reason);

        var stale = AiTextApplyPolicy.Decide("", "", true, AiTextApplyPolicy.StaleBranchesReason);
        Assert.False(stale.ApplyDirectly);
        Assert.Equal(AiTextApplyPolicy.StaleBranchesReason, stale.Reason);
    }

    [Fact]
    public void Commit_scope_fingerprint_changes_when_a_watcher_rescan_lands_and_ignores_non_targets()
    {
        static WorkspaceGitChangesRepositoryView Repo(int id, int staged, int changed, long ticks) => new()
        {
            WorkspaceRepositoryId = id,
            RepositoryId = id,
            RepositoryName = $"r{id}",
            HeadCommit = "abc",
            StagedCount = staged,
            ChangedCount = changed,
            WorkerScannedAt = new DateTimeOffset(ticks, TimeSpan.Zero),
            Changes = [],
        };

        var before = AiTextApplyPolicy.CommitScopeFingerprint(WorkspaceCommitMode.Staged, [Repo(1, 2, 0, 100), Repo(2, 0, 5, 100)]);
        // Repository 2 has no staged files: not a Commit Staged target, so its rescan does not matter.
        Assert.Equal(before, AiTextApplyPolicy.CommitScopeFingerprint(WorkspaceCommitMode.Staged, [Repo(1, 2, 0, 100), Repo(2, 0, 6, 200)]));
        Assert.NotEqual(before, AiTextApplyPolicy.CommitScopeFingerprint(WorkspaceCommitMode.Staged, [Repo(1, 2, 0, 300), Repo(2, 0, 5, 100)]));
        Assert.NotEqual(before, AiTextApplyPolicy.CommitScopeFingerprint(WorkspaceCommitMode.All, [Repo(1, 2, 0, 100), Repo(2, 0, 5, 100)]));
    }

    [Fact]
    public void Pull_request_fingerprint_follows_the_selected_targets()
    {
        var a = new AiPullRequestScope(1, Context, [new(1, "feature/x", "release/2.0"), new(2, "feature/x", "main")]);
        var reordered = new AiPullRequestScope(1, Context, [new(2, "feature/x", "main"), new(1, "feature/x", "release/2.0")]);
        var retargeted = new AiPullRequestScope(1, Context, [new(1, "feature/x", "main"), new(2, "feature/x", "main")]);

        Assert.Equal(a.Fingerprint(), reordered.Fingerprint());
        Assert.NotEqual(a.Fingerprint(), retargeted.Fingerprint());
    }
}
