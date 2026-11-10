using System.Text.RegularExpressions;

namespace GrayMoon.App.Tests.Conventions;

/// <summary>
/// Guardrail 1 (review 2026-11-10, finding 3): a component that subscribes to an event of an injected
/// service removes that subscription again. Scans every component under Components/.
/// </summary>
public sealed class EventSubscriptionConventionTests
{
    /// <summary>Subscriptions that are deliberately never removed. "Component: subscription" with the reason.</summary>
    private static readonly Dictionary<string, string> Exceptions = new(StringComparer.Ordinal)
    {
    };

    private static readonly Regex Subscribe = new(
        @"(?<target>[A-Za-z_]\w*(?:\.[A-Za-z_]\w*)+)\s*\+=\s*(?<handler>[^;]+);", RegexOptions.Compiled);

    private static readonly Regex StateChanged = new(@"(?<target>[\w.]+)\.OnStateChanged\(\s*(?<handler>[^()]*)\)", RegexOptions.Compiled);

    private static readonly Regex MethodGroup = new(@"^[A-Za-z_][\w.]*$", RegexOptions.Compiled);

    [Fact]
    public void Every_component_removes_the_event_subscriptions_it_adds()
    {
        var components = Path.Combine(ConventionSource.AppDirectory, "Components");
        var subscriptions = 0;
        var violations = new List<string>();

        // A component is its .razor file plus its partial class files: same folder, same name up to the first dot.
        foreach (var component in ConventionSource.Files(components, ".razor", ".cs")
                     .GroupBy(f => Path.Combine(Path.GetDirectoryName(f)!, Path.GetFileName(f).Split('.')[0]), StringComparer.OrdinalIgnoreCase))
        {
            var source = string.Join("\n", component.Select(File.ReadAllText));
            var name = ConventionSource.Relative(component.Key);
            subscriptions += Subscribe.Matches(ConventionSource.WithoutLineComments(source)).Count;
            violations.AddRange(FindUnremovedSubscriptions(source).Select(s => $"{name}: {s}"));
        }

        var stale = Exceptions.Keys.Except(violations).ToList();
        Assert.True(stale.Count == 0, "Exceptions that no longer apply; remove them from the list: " + string.Join("; ", stale));
        violations.RemoveAll(Exceptions.ContainsKey);

        Assert.True(subscriptions > 10, "The scan found almost no subscriptions; it is broken.");
        Assert.True(violations.Count == 0,
            "A component must remove every event subscription it adds (Dispose / DisposeAsync), or the service keeps the dead component alive:"
            + Environment.NewLine + string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void Scan_reports_a_subscription_without_a_removal()
    {
        const string leaking = """
            protected override void OnInitialized()
            {
                JobService.Changed += OnJobChanged;
                Tracker.OnStateChanged(OnWorkerStateChanged);
                Notifier.Changed += () => InvokeAsync(StateHasChanged);
            }
            """;
        Assert.Equal(
            ["JobService.Changed += OnJobChanged", "Notifier.Changed += () => InvokeAsync(StateHasChanged)", "Tracker.OnStateChanged(OnWorkerStateChanged)"],
            FindUnremovedSubscriptions(leaking));

        const string clean = leaking + """
            public void Dispose()
            {
                JobService.Changed -= OnJobChanged;
                Tracker.RemoveStateChanged(OnWorkerStateChanged);
            }
            """;
        // The lambda can never be removed, so it stays a violation even with a Dispose.
        Assert.Equal(["Notifier.Changed += () => InvokeAsync(StateHasChanged)"], FindUnremovedSubscriptions(clean));

        // Arithmetic on a member is not a subscription.
        Assert.Empty(FindUnremovedSubscriptions("state.Total += 1; state.Name += \"x\"; state.Count += other.Count * 2;"));
    }

    /// <summary>Subscriptions in one component's source that have no matching removal in the same source.</summary>
    internal static IReadOnlyList<string> FindUnremovedSubscriptions(string componentSource)
    {
        var source = ConventionSource.WithoutLineComments(componentSource);
        var result = new List<string>();

        foreach (Match m in Subscribe.Matches(source))
        {
            var target = m.Groups["target"].Value;
            var handler = Regex.Replace(m.Groups["handler"].Value.Trim(), @"\s+", " ");
            var isLambda = handler.Contains("=>", StringComparison.Ordinal);
            if (!isLambda && !MethodGroup.IsMatch(handler))
                continue; // a literal or an expression: arithmetic, not an event
            if (!isLambda && char.IsLower(handler[0]) && !handler.Contains('.'))
                continue; // a local or parameter value, not a method group

            var removed = !isLambda && Regex.IsMatch(source, $@"{Regex.Escape(target)}\s*-=\s*{Regex.Escape(handler)}\b");
            if (!removed)
                result.Add($"{target} += {handler}");
        }

        foreach (Match m in StateChanged.Matches(source))
        {
            var handler = m.Groups["handler"].Value.Trim();
            if (!source.Contains($".RemoveStateChanged({handler})", StringComparison.Ordinal))
                result.Add($"{m.Groups["target"].Value}.OnStateChanged({handler})");
        }

        return result;
    }
}

/// <summary>
/// Guardrails 2 and 3 (review 2026-11-10, findings 4, 13 and 14) for every component, not only the pages
/// the findings named: a job lambda uses the context captured when it was queued, and it changes page
/// collections only on the dispatcher.
/// </summary>
public sealed class JobLambdaConventionTests
{
    /// <summary>"File:line" of a job lambda that may read the selected context lazily, with the reason.</summary>
    private static readonly Dictionary<string, string> LazyContextExceptions = new(StringComparer.Ordinal);

    /// <summary>"File:line" of a job lambda that may write a page collection off the dispatcher, with the reason.</summary>
    private static readonly Dictionary<string, string> PageStateExceptions = new(StringComparer.Ordinal);

    /// <summary>
    /// A write to one of the Repositories page collections the virtual-scroll table renders from:
    /// assignment, indexer assignment or a mutating call.
    /// </summary>
    private static readonly Regex PageCollectionWrite = new(
        @"\b(?:_slots|_linkByRepoId|prByRepositoryId|repoSyncStatus)\s*(?:=(?![=>])|\[[^\]]*\]\s*=(?!=)|\.(?:Add|AddRange|Insert|Remove|RemoveAt|RemoveAll|Clear|TryAdd|Sort)\s*\()",
        RegexOptions.Compiled);

    [Fact]
    public void No_job_lambda_reads_the_selected_context_lazily()
    {
        var (lambdas, violations) = Scan(ReadsSelectedContext, LazyContextExceptions);

        Assert.True(lambdas > 20, "The scan found almost no job lambdas; it is broken.");
        Assert.True(violations.Count == 0,
            "RequireSelectedContextId() must be captured before the job is queued; inside the job lambda a context switch retargets the job:"
            + Environment.NewLine + string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void No_job_lambda_writes_page_collections_off_the_dispatcher()
    {
        var (lambdas, violations) = Scan(WritesPageCollectionOffDispatcher, PageStateExceptions);

        Assert.True(lambdas > 20, "The scan found almost no job lambdas; it is broken.");
        Assert.True(violations.Count == 0,
            "A job lambda runs on the thread pool; write _slots, _linkByRepoId, prByRepositoryId and repoSyncStatus only inside InvokeAsync / SafeInvoke:"
            + Environment.NewLine + string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void Scan_reports_a_lazy_context_read_and_an_off_dispatcher_write()
    {
        const string source = """
            private void Queue()
            {
                var jobContextId = RequireSelectedContextId();
                JobService.StartJob("Sync", async job =>
                {
                    var result = await ScopedExecutor.ExecuteAsync<IWorkspaceSyncOperations, OperationResult>(
                        svc => svc.SyncAsync(WorkspaceId, RequireSelectedContextId()));
                    repoSyncStatus[1] = RepoSyncStatus.InSync;
                    await InvokeAsync(() => { prByRepositoryId = result.Prs; StateHasChanged(); });
                });
            }
            """;
        var lambdas = JobLambdaScanner.Find(source);
        Assert.Equal(["StartJob", "ExecuteAsync"], lambdas.Select(l => l.Call).ToArray());
        Assert.All(lambdas, l => Assert.True(ReadsSelectedContext(l)));
        Assert.True(WritesPageCollectionOffDispatcher(lambdas[0]));
        Assert.False(WritesPageCollectionOffDispatcher(lambdas[1]));

        const string fixedSource = """
            private void Queue()
            {
                var jobContextId = RequireSelectedContextId();
                JobService.StartWorkspaceJob(WorkspaceId, RequireSelectedContextId(), "Sync", async job =>
                {
                    var result = await ScopedExecutor.ExecuteAsync<IWorkspaceSyncOperations, OperationResult>(
                        svc => svc.SyncAsync(WorkspaceId, jobContextId));
                    SafeInvoke(() => repoSyncStatus[1] = RepoSyncStatus.InSync);
                    await InvokeAsync(() => { prByRepositoryId = result.Prs; _slots.Clear(); StateHasChanged(); });
                    if (repoSyncStatus.Count == 0 || prByRepositoryId == null) return;
                });
            }
            """;
        var fixedLambdas = JobLambdaScanner.Find(fixedSource);
        Assert.Equal(2, fixedLambdas.Count);
        Assert.All(fixedLambdas, l => Assert.False(ReadsSelectedContext(l)));
        Assert.All(fixedLambdas, l => Assert.False(WritesPageCollectionOffDispatcher(l)));
    }

    private static bool ReadsSelectedContext(JobLambda lambda) =>
        lambda.Body.Contains("RequireSelectedContextId()", StringComparison.Ordinal);

    private static bool WritesPageCollectionOffDispatcher(JobLambda lambda) =>
        PageCollectionWrite.IsMatch(JobLambdaScanner.OffDispatcherPart(lambda.Body));

    private static (int Lambdas, List<string> Violations) Scan(Func<JobLambda, bool> isViolation, Dictionary<string, string> exceptions)
    {
        var lambdas = 0;
        var violations = new List<string>();
        foreach (var file in ConventionSource.Files(Path.Combine(ConventionSource.AppDirectory, "Components"), ".razor", ".cs"))
        {
            foreach (var lambda in JobLambdaScanner.Find(File.ReadAllText(file)))
            {
                lambdas++;
                var key = $"{ConventionSource.Relative(file)}:{lambda.Line}";
                if (isViolation(lambda) && !exceptions.ContainsKey(key))
                    violations.Add($"{key} ({lambda.Call})");
            }
        }

        return (lambdas, violations);
    }
}
