using System.Net;
using GrayMoon.App.Models;
using GrayMoon.App.Services.Versions;

namespace GrayMoon.App.Tests;

/// <summary>
/// The effective version decision (design "Effective Package Versions", section 6.1, plan package D): inheritance needs
/// the branch point, the commit on origin/&lt;parent&gt; and a positive lookup of every required package; a confirmed
/// missing package is the repository's own version; any other registry answer is unresolved.
/// </summary>
public sealed class EffectiveVersionResolverTests
{
    [Fact]
    public async Task A_fresh_branch_whose_packages_are_published_inherits_the_parent_version()
    {
        await using var s = await EffectiveVersionScenario.CreateAsync();

        var resolution = await s.ResolveAsync();

        var producer = resolution.Decisions.Single(d => d.RepositoryId == s.ProducerId);
        Assert.Equal(EffectiveVersionKind.InheritedVerified, producer.Kind);
        Assert.Equal(EffectiveVersionScenario.ParentVersion, producer.EffectiveVersion);
        var state = await s.ReadStateAsync(s.ProducerLinkId);
        Assert.Equal((int)EffectiveVersionKind.InheritedVerified, state.EffectiveVersionKind);
        Assert.Equal(EffectiveVersionScenario.ParentVersion, state.EffectiveVersion);
        Assert.Equal(EffectiveVersionScenario.BaseSha, state.EffectiveVersionCommitSha);
        Assert.Equal(EffectiveVersionReasonCodes.Verified, state.EffectiveVersionReasonCode);
        // GitVersion stays the calculated value (approved decision 2).
        Assert.Equal(EffectiveVersionScenario.CalculatedVersion, state.GitVersion);
        Assert.Null(resolution.StopMessage);
    }

    [Fact]
    public async Task A_repository_nobody_consumes_keeps_its_own_version_without_a_Worker_or_registry_call()
    {
        await using var s = await EffectiveVersionScenario.CreateAsync();

        var resolution = await s.ResolveAsync();

        var consumer = resolution.Decisions.Single(d => d.RepositoryId == s.ConsumerId);
        Assert.Equal(EffectiveVersionKind.Own, consumer.Kind);
        Assert.Equal(EffectiveVersionReasonCodes.NoRequiredPackages, consumer.ReasonCode);
        Assert.Equal(1, s.BranchPointCalls);
    }

    [Fact]
    public async Task A_background_refresh_on_the_same_HEAD_keeps_the_stored_decision_without_asking_the_Worker_or_registry()
    {
        // The refresh runs after every hook sync, in the queue "Applying pending repository changes..." waits for. On the
        // old code each one asked the Worker for GitVersion again (5-50 s per refresh in a 45-repository Workspace).
        await using var s = await EffectiveVersionScenario.CreateAsync();
        await s.ResolveAsync(bypassCache: true);
        var branchPointCalls = s.BranchPointCalls;
        var lookups = s.Registry.Lookups;

        var refreshed = await s.ResolveAsync(bypassCache: false);

        Assert.Equal(EffectiveVersionKind.InheritedVerified, refreshed.Decisions.Single(d => d.RepositoryId == s.ProducerId).Kind);
        Assert.Equal(EffectiveVersionScenario.ParentVersion, refreshed.Decisions.Single(d => d.RepositoryId == s.ProducerId).EffectiveVersion);
        Assert.Equal(branchPointCalls, s.BranchPointCalls);
        Assert.Equal(lookups, s.Registry.Lookups);

        // The start of an operation asks the registry again, but reuses the parent version stored for this commit.
        await s.ResolveAsync(bypassCache: true);
        Assert.Equal(branchPointCalls, s.BranchPointCalls);
        Assert.True(s.Registry.Lookups > lookups);
    }

    [Fact]
    public async Task A_background_refresh_after_HEAD_moved_does_not_reuse_the_old_decision()
    {
        await using var s = await EffectiveVersionScenario.CreateAsync();
        await s.ResolveAsync(bypassCache: true);
        await s.MutateStateAsync(s.ProducerLinkId, state => state.HeadCommit = EffectiveVersionScenario.MovedSha);

        var refreshed = await s.ResolveAsync(bypassCache: false);

        var producer = refreshed.Decisions.Single(d => d.RepositoryId == s.ProducerId);
        Assert.Equal(EffectiveVersionKind.Own, producer.Kind);
        Assert.Equal(EffectiveVersionReasonCodes.NotOnBaseline, producer.ReasonCode);
    }

    [Fact]
    public async Task A_missing_package_gives_the_repository_its_own_version()
    {
        await using var s = await EffectiveVersionScenario.CreateAsync();
        s.Registry.Lists(EffectiveVersionScenario.PackageId, "2.4.0-beta.1");

        var resolution = await s.ResolveAsync();

        var producer = resolution.Decisions.Single(d => d.RepositoryId == s.ProducerId);
        Assert.Equal(EffectiveVersionKind.Own, producer.Kind);
        Assert.Equal(EffectiveVersionReasonCodes.PackageNotPublished, producer.ReasonCode);
        Assert.Equal(EffectiveVersionScenario.CalculatedVersion, producer.EffectiveVersion);
        Assert.Contains("is not published; publishing", producer.Message);
        Assert.Null(resolution.StopMessage);
    }

    [Fact]
    public async Task One_missing_package_of_two_gives_the_repository_its_own_version()
    {
        await using var s = await EffectiveVersionScenario.CreateAsync(secondPackage: true);
        s.Registry.Answers(EffectiveVersionScenario.SecondPackageId, HttpStatusCode.NotFound);

        var resolution = await s.ResolveAsync();

        var producer = resolution.Decisions.Single(d => d.RepositoryId == s.ProducerId);
        Assert.Equal(EffectiveVersionKind.Own, producer.Kind);
        Assert.Equal(EffectiveVersionReasonCodes.PackageNotPublished, producer.ReasonCode);
        Assert.Equal(2, s.Registry.Lookups);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, EffectiveVersionReasonCodes.RegistryAuthenticationFailed, "returned HTTP 401")]
    [InlineData(HttpStatusCode.Forbidden, EffectiveVersionReasonCodes.RegistryAuthenticationFailed, "returned HTTP 403")]
    [InlineData(HttpStatusCode.InternalServerError, EffectiveVersionReasonCodes.RegistryFailure, "returned HTTP 500")]
    [InlineData(HttpStatusCode.GatewayTimeout, EffectiveVersionReasonCodes.RegistryTimeout, "returned HTTP 504")]
    public async Task A_registry_error_is_unresolved_never_missing(HttpStatusCode status, string reason, string text)
    {
        await using var s = await EffectiveVersionScenario.CreateAsync();
        s.Registry.Answers(EffectiveVersionScenario.PackageId, status);

        var resolution = await s.ResolveAsync();

        var producer = resolution.Decisions.Single(d => d.RepositoryId == s.ProducerId);
        Assert.Equal(EffectiveVersionKind.Unresolved, producer.Kind);
        Assert.Equal(reason, producer.ReasonCode);
        Assert.Null(producer.EffectiveVersion);
        Assert.NotNull(resolution.StopMessage);
        Assert.Contains($"Cannot verify the published package {EffectiveVersionScenario.PackageId} {EffectiveVersionScenario.ParentVersion} for {EffectiveVersionScenario.ProducerName}", resolution.StopMessage);
        Assert.Contains($"connector nuget-test {text}", resolution.StopMessage);
        Assert.Contains("Nothing was changed.", resolution.StopMessage);
    }

    [Fact]
    public async Task A_timeout_and_a_malformed_answer_are_unresolved_and_not_cached()
    {
        await using var s = await EffectiveVersionScenario.CreateAsync();
        s.Registry.TimesOut(EffectiveVersionScenario.PackageId);

        var timedOut = (await s.ResolveAsync(bypassCache: false)).Decisions.Single(d => d.RepositoryId == s.ProducerId);
        Assert.Equal(EffectiveVersionKind.Unresolved, timedOut.Kind);
        Assert.Equal(EffectiveVersionReasonCodes.RegistryTimeout, timedOut.ReasonCode);

        s.Registry.Malformed(EffectiveVersionScenario.PackageId);
        var malformed = (await s.ResolveAsync(bypassCache: false)).Decisions.Single(d => d.RepositoryId == s.ProducerId);
        Assert.Equal(EffectiveVersionKind.Unresolved, malformed.Kind);
        Assert.Equal(EffectiveVersionReasonCodes.RegistryUnexpectedResponse, malformed.ReasonCode);
        Assert.Equal(2, s.Registry.Lookups);
    }

    [Fact]
    public async Task An_available_answer_is_cached_briefly_and_an_operation_start_asks_again()
    {
        await using var s = await EffectiveVersionScenario.CreateAsync();

        await s.ResolveAsync(bypassCache: false);
        await s.ResolveAsync(bypassCache: false);
        Assert.Equal(1, s.Registry.Lookups);

        // The package was unpublished after the cached answer: the start of an operation bypasses the cache (T36).
        s.Registry.Lists(EffectiveVersionScenario.PackageId, "1.0.0");
        var atStart = (await s.ResolveAsync(bypassCache: true)).Decisions.Single(d => d.RepositoryId == s.ProducerId);
        Assert.Equal(2, s.Registry.Lookups);
        Assert.Equal(EffectiveVersionKind.Own, atStart.Kind);

        // A background refresh keeps that decision for the same HEAD without asking.
        s.Registry.Lists(EffectiveVersionScenario.PackageId, EffectiveVersionScenario.ParentVersion);
        await s.ResolveAsync(bypassCache: false);
        Assert.Equal(2, s.Registry.Lookups);

        // And the cached "available" expires (stored decisions cleared so the refresh checks again).
        await s.MutateStateAsync(s.ProducerLinkId, state => state.EffectiveVersionKind = null);
        await s.ResolveAsync(bypassCache: false);
        Assert.Equal(3, s.Registry.Lookups);
        await s.MutateStateAsync(s.ProducerLinkId, state => state.EffectiveVersionKind = null);
        await s.ResolveAsync(bypassCache: false);
        Assert.Equal(3, s.Registry.Lookups);
        s.Clock.Advance(PackageAvailabilityCache.Ttl + TimeSpan.FromSeconds(1));
        await s.MutateStateAsync(s.ProducerLinkId, state => state.EffectiveVersionKind = null);
        await s.ResolveAsync(bypassCache: false);
        Assert.Equal(4, s.Registry.Lookups);
    }

    [Fact]
    public async Task A_repository_that_left_its_branch_point_is_its_own_without_a_Worker_or_registry_call()
    {
        await using var s = await EffectiveVersionScenario.CreateAsync();
        await s.MutateStateAsync(s.ProducerLinkId, state => state.HeadCommit = EffectiveVersionScenario.MovedSha);

        var producer = (await s.ResolveAsync()).Decisions.Single(d => d.RepositoryId == s.ProducerId);

        Assert.Equal(EffectiveVersionKind.Own, producer.Kind);
        Assert.Equal(EffectiveVersionReasonCodes.NotOnBaseline, producer.ReasonCode);
        Assert.Equal(0, s.BranchPointCalls);
        Assert.Equal(0, s.Registry.Lookups);
    }

    [Fact]
    public async Task A_branch_without_a_recorded_branch_point_keeps_its_own_version()
    {
        await using var s = await EffectiveVersionScenario.CreateAsync();
        await s.WithDbAsync(db =>
        {
            db.WorkspaceRepositoryBranchBaselines.RemoveRange(db.WorkspaceRepositoryBranchBaselines);
            return Task.CompletedTask;
        });

        var producer = (await s.ResolveAsync()).Decisions.Single(d => d.RepositoryId == s.ProducerId);

        Assert.Equal(EffectiveVersionKind.Own, producer.Kind);
        Assert.Equal(EffectiveVersionReasonCodes.NoBaseline, producer.ReasonCode);
        Assert.Equal(0, s.BranchPointCalls);
    }

    [Fact]
    public async Task A_branch_point_that_is_not_on_the_remote_parent_keeps_its_own_version()
    {
        await using var s = await EffectiveVersionScenario.CreateAsync();
        s.RespondBranchPoint(success: true, version: null, onRemoteParent: false);

        var producer = (await s.ResolveAsync()).Decisions.Single(d => d.RepositoryId == s.ProducerId);

        Assert.Equal(EffectiveVersionKind.Own, producer.Kind);
        Assert.Equal(EffectiveVersionReasonCodes.BaselineNotOnRemoteParent, producer.ReasonCode);
        Assert.Equal(0, s.Registry.Lookups);
    }

    [Fact]
    public async Task A_parent_version_that_cannot_be_calculated_keeps_its_own_version_and_stops_nothing()
    {
        await using var s = await EffectiveVersionScenario.CreateAsync();
        s.RespondBranchPoint(success: false, version: null, onRemoteParent: true, error: "GitVersion versioned branch 'origin/release/2.4'");

        var resolution = await s.ResolveAsync();

        var producer = resolution.Decisions.Single(d => d.RepositoryId == s.ProducerId);
        Assert.Equal(EffectiveVersionKind.Own, producer.Kind);
        Assert.Equal(EffectiveVersionReasonCodes.ParentVersionUnavailable, producer.ReasonCode);
        Assert.Null(resolution.StopMessage);
    }

    [Fact]
    public async Task A_Worker_that_could_not_be_asked_is_asked_again_on_the_next_resolution()
    {
        // A failed call says nothing about the branch point. It used to be stored as "own version" and reused until
        // HEAD moved, so one Worker timeout made the repository publish a version of its own.
        await using var s = await EffectiveVersionScenario.CreateAsync();
        s.Ctx.WorkerBridge.Respond(GrayMoon.Abstractions.Worker.WorkerHubMethods.GetGitVersionAtBranchPoint, data: null, success: false, error: "The Worker did not answer in time.");

        var failed = await s.ResolveAsync(bypassCache: false);

        var producer = failed.Decisions.Single(d => d.RepositoryId == s.ProducerId);
        Assert.Equal(EffectiveVersionKind.Own, producer.Kind);
        Assert.Equal(EffectiveVersionReasonCodes.ParentVersionProbeFailed, producer.ReasonCode);

        s.RespondBranchPoint(success: true, version: EffectiveVersionScenario.ParentVersion, onRemoteParent: true);
        var recovered = await s.ResolveAsync(bypassCache: false);

        Assert.Equal(EffectiveVersionKind.InheritedVerified, recovered.Decisions.Single(d => d.RepositoryId == s.ProducerId).Kind);
        Assert.Equal(2, s.BranchPointCalls);
    }

    [Fact]
    public async Task An_unresolved_repository_does_not_run_GitVersion_again_for_the_same_commit()
    {
        // "Unresolved" stores no version, so every recompute ran GitVersion on the Worker again while a registry was down.
        await using var s = await EffectiveVersionScenario.CreateAsync();
        s.Registry.TimesOut(EffectiveVersionScenario.PackageId);

        var first = await s.ResolveAsync(bypassCache: false);
        var second = await s.ResolveAsync(bypassCache: false);

        Assert.Equal(EffectiveVersionKind.Unresolved, first.Decisions.Single(d => d.RepositoryId == s.ProducerId).Kind);
        Assert.Equal(EffectiveVersionKind.Unresolved, second.Decisions.Single(d => d.RepositoryId == s.ProducerId).Kind);
        // The registry is asked both times (it may have recovered); the Worker once.
        Assert.Equal(1, s.BranchPointCalls);
    }

    [Fact]
    public async Task An_unmapped_package_that_no_registry_has_after_a_sync_is_missing()
    {
        await using var s = await EffectiveVersionScenario.CreateAsync(mapConnector: false);
        s.Registry.Answers(EffectiveVersionScenario.PackageId, HttpStatusCode.NotFound);

        var producer = (await s.ResolveAsync()).Decisions.Single(d => d.RepositoryId == s.ProducerId);

        Assert.Equal(EffectiveVersionKind.Own, producer.Kind);
        Assert.Equal(EffectiveVersionReasonCodes.PackageNotPublished, producer.ReasonCode);
    }

    [Fact]
    public async Task An_unmapped_package_is_mapped_by_the_sync_and_then_verified()
    {
        await using var s = await EffectiveVersionScenario.CreateAsync(mapConnector: false);

        var producer = (await s.ResolveAsync()).Decisions.Single(d => d.RepositoryId == s.ProducerId);

        Assert.Equal(EffectiveVersionKind.InheritedVerified, producer.Kind);
    }

    [Fact]
    public async Task A_tag_pinned_repository_is_its_own_by_policy()
    {
        await using var s = await EffectiveVersionScenario.CreateAsync();
        await s.MutateStateAsync(s.ProducerLinkId, state => state.CheckedOutTag = "v2.3.0");

        var producer = (await s.ResolveAsync()).Decisions.Single(d => d.RepositoryId == s.ProducerId);

        Assert.Equal(EffectiveVersionKind.Own, producer.Kind);
        Assert.Equal(EffectiveVersionReasonCodes.PolicyProtected, producer.ReasonCode);
        Assert.Equal(0, s.BranchPointCalls);
    }

    [Fact]
    public async Task A_Basic_Workspace_makes_no_registry_or_Worker_call()
    {
        await using var s = await EffectiveVersionScenario.CreateAsync(dotNet: false);

        var resolution = await s.ResolveAsync();

        Assert.All(resolution.Decisions, d => Assert.Equal(EffectiveVersionKind.Own, d.Kind));
        Assert.Equal(0, s.Registry.Lookups);
        Assert.Equal(0, s.BranchPointCalls);
    }

    [Fact]
    public async Task Cancellation_writes_nothing()
    {
        await using var s = await EffectiveVersionScenario.CreateAsync();
        using var cts = new CancellationTokenSource();
        s.Ctx.WorkerBridge.RespondAsync(GrayMoon.Abstractions.Worker.WorkerHubMethods.GetGitVersionAtBranchPoint, (_, ct) =>
        {
            cts.Cancel();
            ct.ThrowIfCancellationRequested();
            throw new InvalidOperationException("unreachable");
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => s.ResolveAsync(cancellationToken: cts.Token));

        Assert.Null((await s.ReadStateAsync(s.ProducerLinkId)).EffectiveVersionKind);
        Assert.Null((await s.ReadStateAsync(s.ConsumerLinkId)).EffectiveVersionKind);
    }

    [Fact]
    public async Task A_Feature_resolves_its_own_state_and_never_the_special_Workspace()
    {
        await using var s = await EffectiveVersionScenario.CreateAsync();
        var featureA = await s.AddFeatureAsync("feat-a");
        var featureB = await s.AddFeatureAsync("feat-b");
        // Feature B committed in the producer: it has left its branch point.
        await s.MutateStateAsync(s.ProducerLinkId, state => state.HeadCommit = EffectiveVersionScenario.MovedSha, featureB);

        await s.ResolveAsync(featureA);
        await s.ResolveAsync(featureB);

        Assert.Equal((int)EffectiveVersionKind.InheritedVerified, (await s.ReadStateAsync(s.ProducerLinkId, featureA)).EffectiveVersionKind);
        Assert.Equal((int)EffectiveVersionKind.Own, (await s.ReadStateAsync(s.ProducerLinkId, featureB)).EffectiveVersionKind);
        Assert.Null((await s.ReadStateAsync(s.ProducerLinkId)).EffectiveVersionKind);
    }

    [Fact]
    public async Task A_Feature_without_its_own_state_row_gets_no_decision()
    {
        await using var s = await EffectiveVersionScenario.CreateAsync();
        var feature = await s.AddFeatureAsync(withStateRows: false);

        var resolution = await s.ResolveAsync(feature);

        Assert.Empty(resolution.Decisions);
        Assert.Equal(0, s.BranchPointCalls);
    }
}
