using System.Text.Json;
using GrayMoon.Application;
using GrayMoon.Application.Repositories.Policy;

namespace GrayMoon.App.Tests;

/// <summary>
/// The REST endpoints return the operation result types as they are (<c>Results.Ok(result)</c>), so their JSON is a
/// public contract. These tests pin the JSON each shape had before the typed outcome contract was added: a result
/// that carries no <see cref="OperationOutcome"/> serializes exactly as it always did.
/// </summary>
public sealed class OperationResultContractTests
{
    // What minimal APIs use when the application configures nothing else.
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Fact]
    public void A_successful_result_keeps_its_shape()
        => Assert.Equal(
            """{"success":true,"error":null,"repoErrors":null,"levelErrors":null,"skipped":[],"removeFeatureReport":null,"removeFeaturePendingDeletionFolder":null,"warnings":null,"warningSummary":null,"hasWarnings":false}""",
            JsonSerializer.Serialize(OperationResult.Ok(), Web));

    [Fact]
    public void A_failed_result_with_repository_and_level_errors_keeps_its_shape()
        => Assert.Equal(
            """{"success":false,"error":"Push failed.","repoErrors":{"7":"rejected"},"levelErrors":{"2":"stopped"},"skipped":[],"removeFeatureReport":null,"removeFeaturePendingDeletionFolder":null,"warnings":null,"warningSummary":null,"hasWarnings":false}""",
            JsonSerializer.Serialize(
                OperationResult.Fail("Push failed.", new Dictionary<int, string> { [7] = "rejected" }, new Dictionary<int, string> { [2] = "stopped" }),
                Web));

    [Fact]
    public void A_result_with_warnings_keeps_its_shape()
        => Assert.Equal(
            """{"success":true,"error":null,"repoErrors":null,"levelErrors":null,"skipped":[],"removeFeatureReport":null,"removeFeaturePendingDeletionFolder":null,"warnings":["restore failed"],"warningSummary":"1 warning","hasWarnings":true}""",
            JsonSerializer.Serialize(OperationResult.Ok() with { Warnings = ["restore failed"], WarningSummary = "1 warning" }, Web));

    [Fact]
    public void A_Remove_Feature_report_keeps_its_shape()
    {
        var result = OperationResult.Ok() with
        {
            RemoveFeatureReport =
            [
                new RemoveFeatureRepositoryReport(
                    11, "graymoon-api", true, RemoveFeatureBranchOutcome.KeptUnmerged, "has commits", true, 2, ["a.txt"], "left behind",
                    "other", RemoveFeatureRemoteBranchOutcome.RefusedLease, "tip moved"),
            ],
            RemoveFeaturePendingDeletionFolder = @"C:\features\x",
        };

        Assert.Equal(
            """{"success":true,"error":null,"repoErrors":null,"levelErrors":null,"skipped":[],"removeFeatureReport":[{"workspaceRepositoryId":11,"repositoryName":"graymoon-api","worktreeRemoved":true,"branchOutcome":2,"branchMessage":"has commits","residueRemaining":true,"residueFileCount":2,"residueSampleFiles":["a.txt"],"residueMessage":"left behind","keptBranchName":"other","remoteBranchOutcome":3,"remoteBranchMessage":"tip moved"}],"removeFeaturePendingDeletionFolder":"C:\\features\\x","warnings":null,"warningSummary":null,"hasWarnings":false}""",
            JsonSerializer.Serialize(result, Web));
    }

    [Fact]
    public void The_dependency_update_and_combined_results_keep_their_shape()
    {
        var update = DependencyUpdateRunResult.Ok(new HashSet<int> { 3 });
        Assert.Equal("""{"success":true,"syncedRepoIds":[3],"skipped":[]}""", JsonSerializer.Serialize(update, Web));

        var combined = new UpdateAndPushResult(true, update, OperationResult.Ok(), PushedRepoCount: 1);
        Assert.Equal(
            """{"pipelined":true,"update":{"success":true,"syncedRepoIds":[3],"skipped":[]},"push":{"success":true,"error":null,"repoErrors":null,"levelErrors":null,"skipped":[],"removeFeatureReport":null,"removeFeaturePendingDeletionFolder":null,"warnings":null,"warningSummary":null,"hasWarnings":false},"pushedRepoCount":1,"success":true,"skipped":[]}""",
            JsonSerializer.Serialize(combined, Web));
    }

    [Fact]
    public void A_Prepare_Workspace_result_keeps_its_shape()
        => Assert.Equal(
            """{"branchesCreated":false,"branchErrors":{"5":"exists"},"update":null,"combined":null,"pushPending":false,"branchSkipped":[],"branchAlreadyExisted":[],"skipped":[],"success":false,"syncedRepoIds":[]}""",
            JsonSerializer.Serialize(PrepareWorkspaceResult.BranchesFailed(new Dictionary<int, string> { [5] = "exists" }), Web));
}
