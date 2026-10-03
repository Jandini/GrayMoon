namespace GrayMoon.Application.Features;

public enum FeatureRepositoryOperationOutcome
{
    Succeeded = 0,
    Failed = 1,
    Skipped = 2
}

public sealed record FeatureRepairRepositoryResult(
    string RepositoryName,
    FeatureRepositoryOperationOutcome Outcome,
    string? Message);

public sealed record RepairFeatureResult(
    bool Success,
    string? Error,
    IReadOnlyList<FeatureRepairRepositoryResult> Repositories);

public sealed record RollbackFeatureResult(
    bool Success,
    string? Error,
    IReadOnlyList<FeatureRepairRepositoryResult> Repositories);

public sealed record FeatureStatusRepository(
    string RepositoryName,
    string State,
    string? LastError);

public sealed record FeatureStatusSnapshot(
    string? FeatureName,
    string? LastError,
    bool IsRemoveIncomplete,
    IReadOnlyList<FeatureStatusRepository> Repositories);
