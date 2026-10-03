using GrayMoon.Application.Features;

namespace GrayMoon.App.Components.Features;

/// <summary>Pure label / selectable mapping for the Feature selector (C3).</summary>
internal static class FeatureSelectorPresentation
{
    public static string? FriendlyLabel(string? lifecycleState, bool isRemoveIncomplete)
    {
        if (string.IsNullOrWhiteSpace(lifecycleState)
            || string.Equals(lifecycleState, "Ready", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (string.Equals(lifecycleState, "Creating", StringComparison.OrdinalIgnoreCase))
            return "Setting up...";

        if (string.Equals(lifecycleState, "Removing", StringComparison.OrdinalIgnoreCase))
            return "Removing...";

        if (string.Equals(lifecycleState, "NeedsRepair", StringComparison.OrdinalIgnoreCase))
            return isRemoveIncomplete ? "Removal incomplete" : "Needs attention";

        return lifecycleState;
    }

    public static string? FriendlyLabel(WorkspaceFeatureContextInfo option) =>
        option.IsSpecialWorkspace ? null : FriendlyLabel(option.LifecycleState, option.IsRemoveIncomplete);

    public static bool CanSelect(WorkspaceFeatureContextInfo option) =>
        option.IsSpecialWorkspace
        || string.Equals(option.LifecycleState, "Ready", StringComparison.OrdinalIgnoreCase)
        || string.Equals(option.LifecycleState, "NeedsRepair", StringComparison.OrdinalIgnoreCase);

    public static bool ShowRemoveAction(WorkspaceFeatureContextInfo option) =>
        !option.IsSpecialWorkspace
        && !string.Equals(option.LifecycleState, "Creating", StringComparison.OrdinalIgnoreCase)
        && !string.Equals(option.LifecycleState, "Removing", StringComparison.OrdinalIgnoreCase);

    public static bool IsReadOnlyContext(WorkspaceFeatureContextInfo? option) =>
        option is not null
        && !option.IsSpecialWorkspace
        && string.Equals(option.LifecycleState, "NeedsRepair", StringComparison.OrdinalIgnoreCase);
}
