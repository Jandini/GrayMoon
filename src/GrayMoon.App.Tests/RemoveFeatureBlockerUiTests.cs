using GrayMoon.App.Components.Features;
using GrayMoon.Application;

namespace GrayMoon.App.Tests;

/// <summary>
/// Remove Feature blocker diagnostics in the dialog: the pure helpers that turn the Worker's lock-inspection result into
/// what the user reads, the empty-list fallback, and how a refresh or retry folds back into the leftover-files report.
/// </summary>
public sealed class RemoveFeatureBlockerUiTests
{
    private static RemoveFeatureBlockingProcess Process(
        int id,
        string? name = null,
        string? path = null,
        string? kind = "Application",
        string? reason = "OpenFile",
        string? service = null) => new(id, name, path, service, kind, reason);

    [Fact]
    public void Title_uses_name_and_pid_and_falls_back_to_the_executable_name()
    {
        Assert.Equal("Claude (PID 18472)", RemoveFeatureBlockerText.Title(Process(18472, "Claude")));
        Assert.Equal("dotnet (PID 22140)", RemoveFeatureBlockerText.Title(Process(22140, null, @"C:\Program Files\dotnet\dotnet.exe")));
        Assert.Equal("Unknown program (PID 1)", RemoveFeatureBlockerText.Title(Process(1)));
    }

    [Theory]
    [InlineData("Application", "WorkingDirectory", null, "Its current folder is inside this Feature")]
    [InlineData("Console", "OpenFile", null, "Has files open in this Feature")]
    [InlineData("Explorer", "OpenFile", null, "A File Explorer window or preview has files open in this Feature")]
    [InlineData("Service", "OpenFile", "WSearch", "Windows service WSearch has files open in this Feature")]
    [InlineData("Service", "OpenFile", null, "A Windows service has files open in this Feature")]
    [InlineData("Critical", "OpenFile", null, "A system process has files open in this Feature")]
    public void Reason_is_plain_language(string kind, string reason, string? service, string expected)
    {
        Assert.Equal(expected, RemoveFeatureBlockerText.Reason(Process(1, "x", kind: kind, reason: reason, service: service)));
    }

    [Fact]
    public void Ordered_dedupes_by_pid_and_lists_working_directory_holders_first()
    {
        var ordered = RemoveFeatureBlockerText.Ordered(
        [
            Process(3, "zeta"),
            Process(2, "alpha"),
            Process(5, "pwsh", reason: "WorkingDirectory"),
            Process(2, "alpha-duplicate"),
        ]);

        Assert.Equal([5, 2, 3], ordered.Select(p => p.ProcessId));
    }

    [Fact]
    public void Ordered_handles_null()
    {
        Assert.Empty(RemoveFeatureBlockerText.Ordered(null));
    }

    [Fact]
    public void Empty_blocker_list_still_has_an_actionable_fallback_message()
    {
        Assert.Contains("Retry", RemoveFeatureBlockerText.NoBlockerFound);
        Assert.Contains("terminals", RemoveFeatureBlockerText.NoBlockerFound);
        Assert.False(RemoveFeatureBlockerText.AnyProcesses([new RemoveFeatureRepositoryBlockers(1, "api", @"C:\f\api", [], false, null)]));
        Assert.True(RemoveFeatureBlockerText.AnyProcesses([new RemoveFeatureRepositoryBlockers(1, "api", @"C:\f\api", [Process(1)], false, null)]));
    }

    [Fact]
    public void Ui_text_uses_only_ascii_hyphens()
    {
        var texts = new[]
        {
            RemoveFeatureBlockerText.InUseTitle,
            RemoveFeatureBlockerText.InUseBody,
            RemoveFeatureBlockerText.LeftoverBody,
            RemoveFeatureBlockerText.CloseAndRetry,
            RemoveFeatureBlockerText.NoBlockerFound,
        };
        Assert.All(texts, t => Assert.DoesNotContain(t, c => c is '\u2013' or '\u2014'));
    }

    [Fact]
    public void Leftover_warning_line_carries_the_report_entry_for_its_blockers()
    {
        var leftover = new RemoveFeatureRepositoryReport(1, "api", true, RemoveFeatureBranchOutcome.Deleted, null, true, 0, null, "The worktree folder could not be removed.")
        {
            BlockingProcesses = [Process(9, "pwsh", reason: "WorkingDirectory")],
        };

        var warnings = RemoveFeatureModal.BuildReportWarnings([leftover]);

        var warning = Assert.Single(warnings);
        Assert.Equal("api: The worktree folder could not be removed.", warning.Text);
        Assert.Same(leftover, warning.Leftover);
    }

    [Fact]
    public void Non_leftover_warning_lines_have_no_blockers()
    {
        var kept = new RemoveFeatureRepositoryReport(1, "api", true, RemoveFeatureBranchOutcome.Kept, null, false, 0, null, null);

        var warning = Assert.Single(RemoveFeatureModal.BuildReportWarnings([kept]));

        Assert.Null(warning.Leftover);
    }

    [Fact]
    public void Leftover_targets_include_only_entries_that_can_be_retried()
    {
        var target = new RemoveFeatureResidueTarget(@"C:\ws\api", @"C:\f\feat\api", @"C:\f\feat", @"C:\f");
        var withTarget = new RemoveFeatureRepositoryReport(1, "api", true, RemoveFeatureBranchOutcome.Deleted, null, true, 2, null, null) { ResidueTarget = target };
        var root = new RemoveFeatureRepositoryReport(2, "root", true, RemoveFeatureBranchOutcome.Deleted, null, true, 2, null, null);
        var clean = new RemoveFeatureRepositoryReport(3, "web", true, RemoveFeatureBranchOutcome.Deleted, null, false, 0, null, null);

        var targets = RemoveFeatureModal.LeftoverBlockerTargets([withTarget, root, clean]);

        var single = Assert.Single(targets);
        Assert.Equal(1, single.WorkspaceRepositoryId);
        Assert.Equal(target.WorktreePath, single.WorktreePath);
    }

    [Fact]
    public void Refreshed_blockers_replace_the_list_and_a_gone_folder_is_no_longer_a_warning()
    {
        var target = new RemoveFeatureResidueTarget(@"C:\ws\api", @"C:\f\feat\api", @"C:\f\feat", @"C:\f");
        var api = new RemoveFeatureRepositoryReport(1, "api", true, RemoveFeatureBranchOutcome.Deleted, null, true, 2, null, "in use")
        {
            BlockingProcesses = [Process(9, "old")],
            ResidueTarget = target,
        };
        var web = new RemoveFeatureRepositoryReport(2, "web", true, RemoveFeatureBranchOutcome.Deleted, null, true, 1, null, "in use")
        {
            BlockingProcesses = [Process(10, "Code")],
            ResidueTarget = target with { WorktreePath = @"C:\f\feat\web" },
        };

        var updated = RemoveFeatureModal.ApplyRefreshedBlockers(
            [api, web],
            [
                new RemoveFeatureRepositoryBlockers(1, "api", target.WorktreePath, [Process(11, "new")], true, "partial"),
                new RemoveFeatureRepositoryBlockers(2, "web", @"C:\f\feat\web", [], false, null) { PathExists = false },
            ]);

        Assert.Equal(11, Assert.Single(updated[0].BlockingProcesses!).ProcessId);
        Assert.True(updated[0].BlockersMayBeIncomplete);
        Assert.Equal("partial", updated[0].BlockersDiagnostic);
        Assert.True(updated[0].ResidueRemaining);

        Assert.False(updated[1].ResidueRemaining);
        Assert.Null(updated[1].ResidueTarget);
        Assert.False(RemoveFeatureModal.HasReportWarnings([updated[1]]));
    }
}
