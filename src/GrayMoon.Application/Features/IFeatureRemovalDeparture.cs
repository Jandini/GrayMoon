namespace GrayMoon.Application.Features;

/// <summary>
/// After the user confirms Remove Feature, leave for the Workspace immediately.
/// Independent of the Feature's lifecycle (Ready, Creating, Removing, NeedsRepair).
/// The Feature continues removing in the background.
/// </summary>
public interface IFeatureRemovalDeparture
{
    /// <summary>
    /// Queues the removal, persists Workspace as the selected context, starts a Workspace status
    /// refresh that does not wait for removal, and returns the Workspace view the page must open now.
    /// Does not inspect Feature state. Returns
    /// <see cref="FeatureRemovalLeave.AlreadyQueued"/> when this Feature is already being removed.
    /// </summary>
    Task<FeatureRemovalLeave> ConfirmAsync(
        WorkspaceFeatureContextId featureContextId,
        RemoveFeatureOptions options,
        string currentUri,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Result of confirming Remove Feature: either the Workspace to show now, or an error that
/// keeps the dialog open (this Feature is already being removed).
/// </summary>
public sealed class FeatureRemovalLeave
{
    public bool Started { get; private init; }
    public string? Error { get; private init; }
    public WorkspaceFeatureContextInfo? Workspace { get; private init; }
    public string Path { get; private init; } = "";

    public static FeatureRemovalLeave AlreadyQueued() => new()
    {
        Started = false,
        Error = "This Feature is already being removed."
    };

    public static FeatureRemovalLeave ToWorkspace(WorkspaceFeatureContextInfo workspace, string path) => new()
    {
        Started = true,
        Workspace = workspace,
        Path = path
    };
}
