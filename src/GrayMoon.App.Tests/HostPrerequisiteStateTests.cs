using GrayMoon.App.Services.Agent;

namespace GrayMoon.App.Tests;

public sealed class HostPrerequisiteStateTests
{
    [Fact]
    public void AnyMissing_is_false_when_all_versions_present()
    {
        var versions = new HostPrerequisiteVersions("10.0.100", "2.47.0", "5.12.0");
        Assert.False(HostPrerequisiteState.AnyMissing(versions));
        Assert.Empty(HostPrerequisiteState.GetMissingIds(versions));
        Assert.Equal("All prerequisites are installed.", HostPrerequisiteState.Note(versions));
    }

    [Fact]
    public void GetMissingIds_returns_only_missing_prerequisites_in_stable_order()
    {
        var versions = new HostPrerequisiteVersions(null, "2.47.0", "  ");
        var missing = HostPrerequisiteState.GetMissingIds(versions);

        Assert.Equal(
            [HostPrerequisiteIds.DotnetSdk, HostPrerequisiteIds.GitVersion],
            missing);
        Assert.True(HostPrerequisiteState.AnyMissing(versions));
        Assert.Equal("Install the missing prerequisites above.", HostPrerequisiteState.Note(versions));
    }

    [Fact]
    public void GetMissingIds_includes_all_three_when_everything_is_missing()
    {
        var versions = new HostPrerequisiteVersions(null, null, null);
        Assert.Equal(
            [HostPrerequisiteIds.DotnetSdk, HostPrerequisiteIds.Git, HostPrerequisiteIds.GitVersion],
            HostPrerequisiteState.GetMissingIds(versions));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("   ", true)]
    [InlineData("10.0.100", false)]
    public void IsMissing_treats_blank_as_missing(string? version, bool expected)
    {
        Assert.Equal(expected, HostPrerequisiteState.IsMissing(version));
    }

    [Fact]
    public void CommandFor_returns_exact_user_visible_commands()
    {
        Assert.Equal(
            "winget install Microsoft.DotNet.SDK.10 --source winget",
            HostPrerequisiteState.CommandFor(HostPrerequisiteIds.DotnetSdk));
        Assert.Equal(
            "winget install -e --id Git.Git --source winget",
            HostPrerequisiteState.CommandFor(HostPrerequisiteIds.Git));
        Assert.Equal(
            "dotnet tool install --global GitVersion.Tool --version 5.*",
            HostPrerequisiteState.CommandFor(HostPrerequisiteIds.GitVersion));
    }
}
