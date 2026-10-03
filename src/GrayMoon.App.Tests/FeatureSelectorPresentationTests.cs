using GrayMoon.App.Components.Features;
using GrayMoon.Application.Features;

namespace GrayMoon.App.Tests;

/// <summary>C3: Feature selector label and selectable mapping.</summary>
public sealed class FeatureSelectorPresentationTests
{
    [Theory]
    [InlineData("Creating", false, "Setting up...")]
    [InlineData("Removing", false, "Removing...")]
    [InlineData("NeedsRepair", false, "Needs attention")]
    [InlineData("NeedsRepair", true, "Removal incomplete")]
    [InlineData("Ready", false, null)]
    [InlineData(null, false, null)]
    public void FriendlyLabel_maps_lifecycle(string? state, bool removeIncomplete, string? expected)
    {
        Assert.Equal(expected, FeatureSelectorPresentation.FriendlyLabel(state, removeIncomplete));
    }

    [Fact]
    public void CanSelect_allows_Ready_and_NeedsRepair_only()
    {
        Assert.True(FeatureSelectorPresentation.CanSelect(Info("Ready")));
        Assert.True(FeatureSelectorPresentation.CanSelect(Info("NeedsRepair")));
        Assert.False(FeatureSelectorPresentation.CanSelect(Info("Creating")));
        Assert.False(FeatureSelectorPresentation.CanSelect(Info("Removing")));
        Assert.True(FeatureSelectorPresentation.CanSelect(new WorkspaceFeatureContextInfo
        {
            ContextId = new WorkspaceFeatureContextId(1),
            WorkspaceId = 1,
            IsSpecialWorkspace = true,
            LifecycleState = null,
        }));
    }

    [Fact]
    public void ShowRemoveAction_hides_for_Creating_and_Removing()
    {
        Assert.True(FeatureSelectorPresentation.ShowRemoveAction(Info("Ready")));
        Assert.True(FeatureSelectorPresentation.ShowRemoveAction(Info("NeedsRepair")));
        Assert.False(FeatureSelectorPresentation.ShowRemoveAction(Info("Creating")));
        Assert.False(FeatureSelectorPresentation.ShowRemoveAction(Info("Removing")));
    }

    [Fact]
    public void IsReadOnlyContext_true_only_for_NeedsRepair_Features()
    {
        Assert.True(FeatureSelectorPresentation.IsReadOnlyContext(Info("NeedsRepair")));
        Assert.False(FeatureSelectorPresentation.IsReadOnlyContext(Info("Ready")));
        Assert.False(FeatureSelectorPresentation.IsReadOnlyContext(new WorkspaceFeatureContextInfo
        {
            ContextId = new WorkspaceFeatureContextId(1),
            WorkspaceId = 1,
            IsSpecialWorkspace = true,
            LifecycleState = "NeedsRepair",
        }));
    }

    private static WorkspaceFeatureContextInfo Info(string state) => new()
    {
        ContextId = new WorkspaceFeatureContextId(9),
        WorkspaceId = 1,
        IsSpecialWorkspace = false,
        FeatureName = "f",
        LifecycleState = state,
    };
}
