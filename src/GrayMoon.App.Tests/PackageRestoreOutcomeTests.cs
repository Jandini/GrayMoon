namespace GrayMoon.App.Tests;

/// <summary>
/// A long restore error was cut to its last 400 characters. The Worker's text ends with the list of package
/// feeds, so the callout on the repository lost the error code and the package name and read as
/// "...ient. No packages exist with this id in source(s): ...". The reason now starts at the error.
/// </summary>
public sealed class PackageRestoreOutcomeTests
{
    private const string Project = @"C:\Workspace\AVR\auroraverityreview-ingestion-webapp\src\AuroraVerityReview.Ingestion.Cli\AuroraVerityReview.Ingestion.Cli.csproj";

    [Fact]
    public void A_long_restore_error_keeps_the_error_code_and_the_package_name()
    {
        var feeds = string.Join(", ", Enumerable.Range(1, 40).Select(i => $"Package Feed Number {i}"));
        var text = $@"src\AuroraVerityReview.Ingestion.Cli\AuroraVerityReview.Ingestion.Cli.csproj: dotnet restore failed (exit code 1): {Project} : error NU1101: Unable to find package AuroraVerityReview.Gateway.ApiClient. No packages exist with this id in source(s): {feeds}"
            + Environment.NewLine + $"  Failed to restore {Project} (in 1.7 sec).";

        var outcome = PackageRestoreOutcome.Failed("auroraverityreview-ingestion-webapp", text);

        Assert.StartsWith(
            "Package restore failed for auroraverityreview-ingestion-webapp: error NU1101: Unable to find package AuroraVerityReview.Gateway.ApiClient.",
            outcome.Warning);
        Assert.EndsWith("...", outcome.Warning);
        Assert.True(PackageRestoreOutcome.IsWarning(outcome.Warning));
    }

    [Fact]
    public void A_short_restore_error_is_shown_whole()
    {
        var outcome = PackageRestoreOutcome.Failed("graymoon-api", "app.csproj : error NU1101: Unable to find package Foo.");

        Assert.Equal("Package restore failed for graymoon-api: app.csproj : error NU1101: Unable to find package Foo.", outcome.Warning);
    }

    [Fact]
    public void A_long_text_without_an_error_marker_keeps_its_end()
    {
        var text = new string('a', 500) + " the end";

        var outcome = PackageRestoreOutcome.Failed("graymoon-api", text);

        Assert.StartsWith("Package restore failed for graymoon-api: ...", outcome.Warning);
        Assert.EndsWith(" the end", outcome.Warning);
    }
}
