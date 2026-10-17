using GrayMoon.App.Services.Worker;

namespace GrayMoon.App.Tests;

public sealed class HostPrerequisiteStateTests
{
    [Fact]
    public void AnyMissing_is_false_when_all_versions_present()
    {
        var versions = new HostPrerequisiteVersions("10.0.100", "2.47.0", "5.12.0", "1.0.0", "2.1.0", "0.98.0");
        Assert.False(HostPrerequisiteState.AnyMissing(versions));
        Assert.Empty(HostPrerequisiteState.GetMissingIds(versions));
        Assert.Equal("All prerequisites are installed.", HostPrerequisiteState.Note(versions));
    }

    [Fact]
    public void GetMissingIds_returns_only_missing_prerequisites_in_stable_order()
    {
        var versions = new HostPrerequisiteVersions(null, "2.47.0", "  ", "1.0.0", "2.1.0", "0.98.0");
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
        var versions = new HostPrerequisiteVersions(null, null, null, "1.0.0", "2.1.0", "0.98.0");
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
        Assert.Equal(
            "irm 'https://claude.ai/install.ps1' | iex",
            HostPrerequisiteState.CommandFor(HostPrerequisiteIds.Claude));
        Assert.Equal(
            "irm 'https://chatgpt.com/codex/install.ps1' | iex",
            HostPrerequisiteState.CommandFor(HostPrerequisiteIds.Codex));
        Assert.Equal(
            "irm 'https://cursor.com/install?win32=true' | iex",
            HostPrerequisiteState.CommandFor(HostPrerequisiteIds.CursorAgent));
    }

    [Theory]
    [InlineData(HostPrerequisiteIds.DotnetSdk, ".NET SDK")]
    [InlineData(HostPrerequisiteIds.Git, "Git")]
    [InlineData(HostPrerequisiteIds.GitVersion, "GitVersion")]
    [InlineData(HostPrerequisiteIds.Claude, "Claude")]
    [InlineData(HostPrerequisiteIds.Codex, "Codex")]
    [InlineData(HostPrerequisiteIds.CursorAgent, "Cursor Agent")]
    [InlineData("unknown", "unknown")]
    public void DisplayName_maps_known_ids_and_passes_through_unknown(string id, string expected)
    {
        Assert.Equal(expected, HostPrerequisiteState.DisplayName(id));
    }

    [Fact]
    public void A_missing_Cursor_Agent_is_optional_and_never_needs_attention()
    {
        var versions = new HostPrerequisiteVersions("10.0.100", "2.47.0", "5.12.0", null, "2.1.0", "0.98.0");

        Assert.Equal([HostPrerequisiteIds.CursorAgent], HostPrerequisiteState.GetMissingIds(versions));
        Assert.False(HostPrerequisiteState.AnyRequiredMissing(versions, HostPrerequisiteRequirements.All));
        Assert.Equal(
            "All required prerequisites are installed. Cursor Agent is optional: it is only needed to write AI text with Cursor.",
            HostPrerequisiteState.Note(versions, HostPrerequisiteRequirements.All));
    }

    [Fact]
    public void A_missing_Claude_CLI_is_optional_and_never_needs_attention()
    {
        var versions = new HostPrerequisiteVersions("10.0.100", "2.47.0", "5.12.0", "1.0.0", null, "0.98.0");

        Assert.Equal([HostPrerequisiteIds.Claude], HostPrerequisiteState.GetMissingIds(versions));
        Assert.False(HostPrerequisiteState.AnyRequiredMissing(versions, HostPrerequisiteRequirements.All));
        Assert.Equal(
            "All required prerequisites are installed. Claude is optional: it is only needed to write AI text with Claude.",
            HostPrerequisiteState.Note(versions, HostPrerequisiteRequirements.All));
    }

    [Fact]
    public void A_missing_Codex_CLI_is_optional_and_never_needs_attention()
    {
        var versions = new HostPrerequisiteVersions("10.0.100", "2.47.0", "5.12.0", "1.0.0", "2.1.0");

        Assert.Equal([HostPrerequisiteIds.Codex], HostPrerequisiteState.GetMissingIds(versions));
        Assert.False(HostPrerequisiteState.AnyRequiredMissing(versions, HostPrerequisiteRequirements.All));
        Assert.Equal(
            "All required prerequisites are installed. Codex is optional: it is only needed to write AI text with Codex.",
            HostPrerequisiteState.Note(versions, HostPrerequisiteRequirements.All));
    }
}
