using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using GrayMoon.App.Components.Pages;

namespace GrayMoon.App.Tests;

/// <summary>
/// Regression tests for the page-job findings of the 2026-11-10 review on the Repositories, Actions and
/// Git Changes pages: reload applies on the dispatcher (4), the sync hub attaches after the load (5), a job
/// uses the context it was queued with (13), and the sync status dictionary is written on the dispatcher (14).
/// </summary>
public sealed class WorkspacePageJobDisciplineTests
{
    private static readonly Regex CaptureLine = new(@"^var \w+ = RequireSelectedContextId\(\);$", RegexOptions.Compiled);

    /// <summary>
    /// Uses of <c>RequireSelectedContextId()</c> that are not a capture into a local. Each one runs on the
    /// dispatcher before the first <c>await</c> of its event handler, so it reads the context the user acted in.
    /// A read after an <c>await</c> does not belong here: capture it at the top of the method. File name, trimmed line.
    /// </summary>
    private static readonly (string File, string Line)[] AllowedDirectUses =
    [
        ("WorkspaceGitChanges.Ai.cs", "RequireSelectedContextId(),"),
        ("WorkspaceRepositories.Update.cs", "RequireSelectedContextId(),"),
    ];

    [Theory]
    [InlineData(false, 3, 3, true)]
    [InlineData(true, 3, 3, false)]
    [InlineData(false, 3, 4, false)]
    public void Apply_guard_drops_a_result_for_a_disposed_page_or_a_switched_context(
        bool disposed, int capturedGeneration, int currentGeneration, bool expected)
    {
        Assert.Equal(expected, PageStateApplyGuard.ShouldApply(disposed, capturedGeneration, currentGeneration));
    }

    [Fact]
    public void Hub_attaches_on_a_later_render_once_the_data_is_loaded()
    {
        // First render: OnInitializedAsync is still awaiting the load.
        Assert.False(PageRealtimeGate.ShouldAttachHub(hubAttached: false, dataLoaded: false, disposed: false));
        // A later render (firstRender is false by then) with the data present must attach.
        Assert.True(PageRealtimeGate.ShouldAttachHub(hubAttached: false, dataLoaded: true, disposed: false));
        // Never twice, never after dispose.
        Assert.False(PageRealtimeGate.ShouldAttachHub(hubAttached: true, dataLoaded: true, disposed: false));
        Assert.False(PageRealtimeGate.ShouldAttachHub(hubAttached: false, dataLoaded: true, disposed: true));
    }

    [Fact]
    public void Background_refresh_starts_once_after_the_rows_are_loaded()
    {
        Assert.False(PageRealtimeGate.ShouldStartOnce(alreadyStarted: false, isLoading: true, hasRows: false, disposed: false));
        Assert.True(PageRealtimeGate.ShouldStartOnce(alreadyStarted: false, isLoading: false, hasRows: true, disposed: false));
        Assert.False(PageRealtimeGate.ShouldStartOnce(alreadyStarted: true, isLoading: false, hasRows: true, disposed: false));
        Assert.False(PageRealtimeGate.ShouldStartOnce(alreadyStarted: false, isLoading: false, hasRows: true, disposed: true));
    }

    [Fact]
    public void Pages_do_not_gate_the_hub_on_first_render()
    {
        var actions = ReadPage("WorkspaceActions.razor.cs");
        var realtime = ReadPage("WorkspaceRepositories.Realtime.cs");

        Assert.Contains("PageRealtimeGate.ShouldAttachHub(", actions);
        Assert.Contains("PageRealtimeGate.ShouldAttachHub(", realtime);
        Assert.DoesNotContain("if (!firstRender) return;", actions);
        Assert.DoesNotContain("firstRender && workspace != null", realtime);
    }

    [Fact]
    public void Job_context_is_captured_when_the_job_is_queued()
    {
        var offenders = new List<string>();
        var captures = 0;
        foreach (var file in PageFiles("WorkspaceRepositories*.cs").Concat(PageFiles("WorkspaceGitChanges*.cs")))
        {
            var name = Path.GetFileName(file);
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i].Trim();
                if (!line.Contains("RequireSelectedContextId()", StringComparison.Ordinal))
                    continue;
                if (line.StartsWith("private WorkspaceFeatureContextId RequireSelectedContextId()", StringComparison.Ordinal))
                    continue;
                if (CaptureLine.IsMatch(line))
                {
                    captures++;
                    continue;
                }
                if (AllowedDirectUses.Contains((name, line)))
                    continue;
                offenders.Add($"{name}:{i + 1}: {line}");
            }
        }

        Assert.True(captures > 0, "The scan found no context captures at all; it is broken.");
        Assert.True(offenders.Count == 0,
            "RequireSelectedContextId() must be captured into a local before the job is queued, not read inside the job:"
            + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    [Fact]
    public void Sync_status_is_written_on_the_dispatcher()
    {
        var sync = ReadPage("WorkspaceRepositories.Sync.cs");
        Assert.Contains("setRepoSyncStatus: (repoId, status) => SafeInvoke(", sync);
    }

    [Fact]
    public void Reload_loads_off_the_dispatcher_and_applies_on_it()
    {
        var loading = ReadPage("WorkspaceRepositories.Loading.cs");
        var load = MethodBody(loading, "LoadWorkspaceReloadSnapshotAsync()");
        foreach (var write in new[] { "workspace =", "_headerState =", "prByRepositoryId =", "repoSyncStatus =", "BuildSlots(", "_linkByRepoId.", "totalCount =", "SetPageError(" })
            Assert.DoesNotContain(write, load);

        var refresh = MethodBody(loading, "private async Task RefreshFromSync()");
        Assert.Contains("await InvokeAsync(() => ApplyWorkspaceReloadSnapshotAsync(snapshot));", refresh);
        foreach (var write in new[] { "workspace =", "BuildSlots(", "repoSyncStatus =" })
            Assert.DoesNotContain(write, refresh);

        var apply = MethodBody(loading, "ApplyWorkspaceReloadSnapshotAsync(WorkspaceReloadSnapshot snapshot)");
        Assert.StartsWith("if (!PageStateApplyGuard.ShouldApply(_disposed, snapshot.ContextGeneration, _contextGeneration))", apply.TrimStart('{', '\r', '\n', ' '));
    }

    /// <summary>The text from the method's opening brace to its matching closing brace.</summary>
    private static string MethodBody(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Method '{signature}' was not found.");
        var open = source.IndexOf('{', start);
        var depth = 0;
        for (var i = open; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0)
                return source[open..(i + 1)];
        }
        throw new InvalidOperationException($"Method '{signature}' has no closing brace.");
    }

    private static string ReadPage(string fileName) => File.ReadAllText(Path.Combine(PagesDirectory(), fileName));

    private static IEnumerable<string> PageFiles(string pattern) => Directory.EnumerateFiles(PagesDirectory(), pattern);

    private static string PagesDirectory([CallerFilePath] string thisFile = "")
    {
        // The build output can live outside the repository (--artifacts-path), so fall back to this source file's location.
        foreach (var origin in new[] { AppContext.BaseDirectory, Path.GetDirectoryName(thisFile)! })
        {
            var dir = new DirectoryInfo(origin);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "GrayMoon.slnx")))
                dir = dir.Parent;
            if (dir != null)
                return Path.Combine(dir.FullName, "src", "GrayMoon.App", "Components", "Pages");
        }
        throw new InvalidOperationException("GrayMoon.slnx was not found above the test output or the test source.");
    }
}
