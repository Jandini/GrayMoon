# Implementation plan: AI generation vs mutation-lane decoupling

Design and rationale: `docs/todo/ai-generation-lane-decoupling.md` (read it first). This file is the build recipe. It is written so that each step can be done mechanically: every step names the file, the exact change and how to check it.

All work is in the `GrayMoon/` repository (inside the feature folder, run git and dotnet from `GrayMoon/`). `GrayMoon.Desktop/` is touched only for one README bullet (Phase 4).

## 0. Ground rules (read before editing)

- **Never run `git commit`, `git push`, `git add` or any other git write.** Leave all changes uncommitted.
- **CRLF line endings** in every file you create or edit. **ASCII hyphen only**: no em dash (U+2014) and no en dash (U+2013) in code, comments, strings, Markdown.
- New concrete classes are `sealed`. Prefer primary constructors. Worker DTOs are not touched here.
- Cite architecture docs by file name, not by section number.
- Change only what a step lists. Do **not** touch: `WorkspaceOperationRunner`, `BackgroundJobService`, `WorkspaceJobKeys`, `IWorkspaceOperationLock`, `TrackedJobQueue`, `ReadJobQueue`, `DiffJobQueue`, `IWorkerQueueTracker`, `JobBackgroundService`, `WorkerQueueStateService`, `NewPullRequestModal.*`, `AiProviderControl.razor`, commit/stage code in `WorkspaceGitChanges.MultiRepo.cs`, `GenerateCommitMessageInJobAsync`.
- If a build or test fails and the cause is not in a file you edited, stop and report. Do not "fix" unrelated code.

### The invariants this change must keep

1. Commit, stage-all, discard-all, unstage-all stay disabled while any Workspace mutation runs (`IsJobRunning`).
2. Update and Push, Sync and every other mutation behave exactly as before.
3. AI text is never applied silently over user text or over a result that may describe a changing tree.
4. `GetGitChangeStatus` (read pool) and `GetGitFileDiff` (diff pool) stay isolated from long-running work.
5. The main queue's telemetry (`ReportQueueStatus`, Repositories header "Worker is completing N tasks") is unchanged for every command except the three AI commands.
6. New Pull Request dialog behavior is unchanged.

## 1. What changes (inventory)

| # | Area | File | Action |
|---|---|---|---|
| W1 | Worker | `src/GrayMoon.Worker/WorkerOptions.cs` | add `MaxConcurrentAiCommands` |
| W2 | Worker | `src/GrayMoon.Worker/appsettings.json` | add `"MaxConcurrentAiCommands": 4` |
| W3 | Worker | `src/GrayMoon.Worker/Abstractions/IAiJobQueue.cs` | **new** |
| W4 | Worker | `src/GrayMoon.Worker/Queue/AiJobQueue.cs` | **new** |
| W5 | Worker | `src/GrayMoon.Worker/Hosted/AiJobBackgroundService.cs` | **new** |
| W6 | Worker | `src/GrayMoon.Worker/Services/WorkerCommandPoolRouter.cs` | **new** (pure routing function) |
| W7 | Worker | `src/GrayMoon.Worker/Hosted/SignalRConnectionHostedService.cs` | use router, inject `IAiJobQueue` |
| W8 | Worker | `src/GrayMoon.Worker/Cli/Handlers/RunCommandHandler.cs` | DI + hosted service + startup log |
| W9 | Worker tests | `src/GrayMoon.Worker.Tests/WorkerCommandPoolRouterTests.cs` | **new** |
| W10 | Worker tests | `src/GrayMoon.Worker.Tests/AiJobQueueTests.cs` | **new** |
| A1 | App | `src/GrayMoon.App/Services/Ai/AiTextApplyPolicy.cs` | new reason + `mutationOverlapped` parameter |
| A2 | App | `src/GrayMoon.App/Services/Ai/AiTextGenerationRuns.cs` | `Outcome.MutationOverlap`, fallback passes it |
| A3 | App | `src/GrayMoon.App/Components/Pages/WorkspaceGitChanges.Ai.cs` | gate property, guard, capture busy, consult |
| A4 | App | `src/GrayMoon.App/Components/Pages/WorkspaceGitChanges.razor` | bind AI control to the new gate |
| A5 | App | `src/GrayMoon.App/Components/Pages/WorkspaceGitChanges.JobHelper.cs` | comment only |
| A6 | App tests | `src/GrayMoon.App.Tests/AiGenerationTests.cs` | add policy test |
| A7 | App tests | `src/GrayMoon.App.Tests/WorkspaceGitChangesCommitGenerationTests.cs` | add run-slot test |
| D1 | Docs | `docs/architecture/04-runtime-communication-and-concurrency.md` | update |
| D2 | Docs | `docs/architecture/06-developer-extension-guide.md` | update pool table |
| D3 | Docs | `docs/architecture/02-system-architecture.md` | update pool list |
| D4 | Docs | `CLAUDE.md` (in `GrayMoon/`) | update Worker job-queue sentence |
| D5 | Docs | `../GrayMoon.Desktop/README.md` | one bullet at top of `## Recent GrayMoon changes` |
| D6 | Docs | `docs/todo/ai-generation-lane-decoupling*.md` | trim after the build (Phase 5) |

Order matters: **Phase 2 (overlap protection) must be done before Phase 3 (enabling the button)** so the button is never enabled without the safety net. Phase 1 (Worker pool) is independent and can ship first.

## Phase 0 - Baseline

Run from `GrayMoon/` and write down the result (count of passed/failed). Any pre-existing failure is not yours to fix, but you must know it exists.

```powershell
dotnet build GrayMoon.slnx
dotnet test src/GrayMoon.Worker.Tests/GrayMoon.Worker.Tests.csproj
dotnet test src/GrayMoon.App.Tests/GrayMoon.App.Tests.csproj
```

## Phase 1 - Worker: dedicated AI pool

Goal: `CollectGitEvidence`, `GenerateAiText`, `GetAiProviders` run on their own bounded pool, untracked like read and diff.

### W1. `WorkerOptions.cs`

Add after `MaxConcurrentDiffCommands`:

```csharp
    /// <summary>
    /// Worker count for the dedicated AI command pool (CollectGitEvidence, GenerateAiText, GetAiProviders). A generation
    /// is one provider CLI process that can run for minutes, so it must not hold a main-pool slot that Sync, push,
    /// Update and hook syncs need, and must not sit in the read pool where it would starve status scans. Several
    /// Feature contexts can generate at once, so the default is 4, not smaller than what AI could use before.
    /// </summary>
    public int MaxConcurrentAiCommands { get; set; } = 4;
```

### W2. `appsettings.json`

Add the line after `"MaxConcurrentDiffCommands": 4,` keeping the trailing comma:

```json
    "MaxConcurrentAiCommands": 4,
```

Do not edit `appsettings.Development.json`. Do not add a CLI flag (`WorkerCliOptions` exposes only `--concurrency` for the main pool).

### W3. `Abstractions/IAiJobQueue.cs` (new)

```csharp
namespace GrayMoon.Worker.Abstractions;

/// <summary>
/// Marker interface for the dedicated AI command queue (CollectGitEvidence, GenerateAiText, GetAiProviders). Kept
/// separate from <see cref="IJobQueue"/>, <see cref="IReadJobQueue"/> and <see cref="IDiffJobQueue"/> so it resolves as
/// its own DI service and gets its own small worker pool.
/// </summary>
public interface IAiJobQueue : IJobQueue
{
}
```

### W4. `Queue/AiJobQueue.cs` (new)

Copy `Queue/DiffJobQueue.cs` and change only names, the option, the interface and the comments:

```csharp
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using GrayMoon.Worker.Abstractions;
using GrayMoon.Worker.Jobs;
using Microsoft.Extensions.Options;

namespace GrayMoon.Worker.Queue;

/// <summary>
/// Dedicated queue for the AI commands, sized by <see cref="WorkerOptions.MaxConcurrentAiCommands"/>. Like
/// <see cref="ReadJobQueue"/> and <see cref="DiffJobQueue"/> it does not broadcast <c>ReportQueueStatus</c>: that
/// telemetry drives the Worker badge activity frame and the per-Workspace pending count, and an AI generation shows its
/// own spinner on the AI control (or the commit overlay terminal) instead.
/// </summary>
public sealed class AiJobQueue(IOptions<WorkerOptions> options) : IAiJobQueue, IWorkerQueueTracker
{
    private readonly Channel<JobEnvelope> _channel = Channel.CreateBounded<JobEnvelope>(
        new BoundedChannelOptions(Math.Max(options.Value.MaxConcurrentAiCommands * 2, 8))
        {
            FullMode = BoundedChannelFullMode.Wait
        });

    public async ValueTask EnqueueAsync(JobEnvelope job, CancellationToken cancellationToken = default) =>
        await _channel.Writer.WriteAsync(job, cancellationToken);

    public async IAsyncEnumerable<JobEnvelope> ReadAllAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var envelope in _channel.Reader.ReadAllAsync(cancellationToken))
            yield return envelope;
    }

    /// <inheritdoc />
    public void ReportJobCompleted(JobEnvelope envelope)
    {
        // Intentionally a no-op: AI jobs are not part of the worker-busy queue-status telemetry.
    }
}
```

### W5. `Hosted/AiJobBackgroundService.cs` (new)

Copy `Hosted/DiffJobBackgroundService.cs` and rename:

```csharp
using GrayMoon.Worker.Abstractions;
using GrayMoon.Worker.Queue;
using GrayMoon.Worker.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GrayMoon.Worker.Hosted;

/// <summary>Runs the dedicated AI command queue, sized from <see cref="WorkerOptions.MaxConcurrentAiCommands"/>.</summary>
public sealed class AiJobBackgroundService(
    IAiJobQueue jobQueue,
    AiJobQueue queueTracker,
    ICommandDispatcher dispatcher,
    INotifySyncHandler notifySyncHandler,
    IHubConnectionProvider hubProvider,
    CommandJobCancellationRegistry cancellationRegistry,
    IOptions<WorkerOptions> options,
    ILogger<AiJobBackgroundService> logger)
    : JobBackgroundService(
        jobQueue,
        queueTracker,
        dispatcher,
        notifySyncHandler,
        hubProvider,
        cancellationRegistry,
        options.Value.MaxConcurrentAiCommands,
        logger);
```

### W6. `Services/WorkerCommandPoolRouter.cs` (new)

The routing sets move out of `SignalRConnectionHostedService` into one pure function so they can be unit tested. Keep the sets and the wire names exactly as they are today.

```csharp
using GrayMoon.Abstractions.Worker;

namespace GrayMoon.Worker.Services;

/// <summary>The Worker job pools. Each is a bounded queue drained by its own hosted service.</summary>
public enum WorkerCommandPool
{
    Main,
    Read,
    Diff,
    Ai,
}

/// <summary>Chooses the pool for a command by name. Everything not listed here runs on the main pool.</summary>
public static class WorkerCommandPoolRouter
{
    /// <summary>Commands that only read repository state (never touch the index or working tree) and can
    /// run on the dedicated read pool instead of queuing behind long-running writes.</summary>
    private static readonly HashSet<string> ReadOnlyCommands =
        ["GetGitChangeStatus", "EnsureGitChangesWatchers", WorkerHubMethods.ListGitWorktrees, WorkerHubMethods.InspectWorktree];

    /// <summary>Diff commands get their own dedicated pool, separate from <see cref="ReadOnlyCommands"/>,
    /// so opening a diff never queues behind a workspace status rescan (which can fan out many
    /// GetGitChangeStatus calls) or any other command.</summary>
    private static readonly HashSet<string> DiffCommands = ["GetGitFileDiff"];

    /// <summary>AI commands: bounded read-only Git evidence and one provider CLI process per generation. They get
    /// their own pool so a long CLI run never holds a main-pool slot (Sync, push, Update, hook syncs) or a read-pool
    /// slot (status scans).</summary>
    private static readonly HashSet<string> AiCommands =
        [WorkerHubMethods.CollectGitEvidence, WorkerHubMethods.GenerateAiText, WorkerHubMethods.GetAiProviders];

    public static WorkerCommandPool Resolve(string command)
    {
        if (DiffCommands.Contains(command))
            return WorkerCommandPool.Diff;
        if (ReadOnlyCommands.Contains(command))
            return WorkerCommandPool.Read;
        if (AiCommands.Contains(command))
            return WorkerCommandPool.Ai;
        return WorkerCommandPool.Main;
    }
}
```

### W7. `Hosted/SignalRConnectionHostedService.cs`

1. Add the constructor parameter right after `IDiffJobQueue diffJobQueue,`:

```csharp
    IAiJobQueue aiJobQueue,
```

2. Delete the two `private static readonly HashSet<string> ReadOnlyCommands` / `DiffCommands` fields and their summaries (now in the router). Keep `private readonly WorkerOptions _options` etc.

3. Replace the three-line `targetQueue` selection:

```csharp
                var targetQueue = DiffCommands.Contains(command)
                    ? (IJobQueue)diffJobQueue
                    : ReadOnlyCommands.Contains(command) ? (IJobQueue)readJobQueue : jobQueue;
```

with:

```csharp
                IJobQueue targetQueue = WorkerCommandPoolRouter.Resolve(command) switch
                {
                    WorkerCommandPool.Diff => diffJobQueue,
                    WorkerCommandPool.Read => readJobQueue,
                    WorkerCommandPool.Ai => aiJobQueue,
                    _ => jobQueue,
                };
```

`GrayMoon.Worker.Services` is already imported in this file. Nothing else in the method changes (cancellation registry, error paths stay as they are).

### W8. `Cli/Handlers/RunCommandHandler.cs`

1. Startup log (about lines 45-47): add `MaxConcurrentAiCommands: {MaxConcurrentAiCommands}` to the template and `options.MaxConcurrentAiCommands` to the arguments, at the end of both, in the same order.
2. After the `IDiffJobQueue` registration (about line 115) add:

```csharp
        builder.Services.AddSingleton<AiJobQueue>();
        builder.Services.AddSingleton<IAiJobQueue>(sp => sp.GetRequiredService<AiJobQueue>());
```

Do **not** register `AiJobQueue` as `IJobQueue` or `IWorkerQueueTracker`. `IJobQueue` must stay `TrackedJobQueue` (main) and `IWorkerQueueTracker` must stay `TrackedJobQueue`.

3. After `AddHostedService<DiffJobBackgroundService>()` (about line 233) add:

```csharp
        builder.Services.AddHostedService<AiJobBackgroundService>();
```

### W9. `GrayMoon.Worker.Tests/WorkerCommandPoolRouterTests.cs` (new)

```csharp
using GrayMoon.Worker.Services;

namespace GrayMoon.Worker.Tests;

public sealed class WorkerCommandPoolRouterTests
{
    [Theory]
    [InlineData("CollectGitEvidence", WorkerCommandPool.Ai)]
    [InlineData("GenerateAiText", WorkerCommandPool.Ai)]
    [InlineData("GetAiProviders", WorkerCommandPool.Ai)]
    [InlineData("GetGitChangeStatus", WorkerCommandPool.Read)]
    [InlineData("EnsureGitChangesWatchers", WorkerCommandPool.Read)]
    [InlineData("ListGitWorktrees", WorkerCommandPool.Read)]
    [InlineData("InspectWorktree", WorkerCommandPool.Read)]
    [InlineData("GetGitFileDiff", WorkerCommandPool.Diff)]
    [InlineData("SyncRepository", WorkerCommandPool.Main)]
    [InlineData("StageGitChanges", WorkerCommandPool.Main)]
    [InlineData("CommitGitChanges", WorkerCommandPool.Main)]
    [InlineData("StageAndCommit", WorkerCommandPool.Main)]
    [InlineData("GetHostInfo", WorkerCommandPool.Main)]
    [InlineData("SelfUpdate", WorkerCommandPool.Main)]
    [InlineData("SomethingNew", WorkerCommandPool.Main)]
    public void Commands_are_routed_to_their_pool(string command, WorkerCommandPool expected) =>
        Assert.Equal(expected, WorkerCommandPoolRouter.Resolve(command));
}
```

### W10. `GrayMoon.Worker.Tests/AiJobQueueTests.cs` (new)

```csharp
using GrayMoon.Worker.Jobs;
using GrayMoon.Worker.Queue;
using Microsoft.Extensions.Options;

namespace GrayMoon.Worker.Tests;

public sealed class AiJobQueueTests
{
    private static AiJobQueue Create(int workers = 2) =>
        new(Options.Create(new WorkerOptions { MaxConcurrentAiCommands = workers }));

    private static JobEnvelope Job() =>
        JobEnvelope.Notify(new NotifySyncJob { RepositoryId = 1, WorkspaceId = 1, RepositoryPath = "repo" });

    [Fact]
    public async Task A_queued_job_is_read_back_in_order()
    {
        var queue = Create();
        var first = Job();
        var second = Job();
        await queue.EnqueueAsync(first);
        await queue.EnqueueAsync(second);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var read = new List<JobEnvelope>();
        await foreach (var envelope in queue.ReadAllAsync(cts.Token))
        {
            read.Add(envelope);
            if (read.Count == 2)
                break;
        }

        Assert.Same(first, read[0]);
        Assert.Same(second, read[1]);
    }

    [Fact]
    public void Completing_a_job_is_a_no_op_because_the_queue_is_untracked() =>
        Create().ReportJobCompleted(Job());
}
```

### Phase 1 check

```powershell
dotnet build src/GrayMoon.Worker/GrayMoon.Worker.csproj
dotnet test src/GrayMoon.Worker.Tests/GrayMoon.Worker.Tests.csproj
```

Both must pass, and the Worker test count must be Phase 0 plus 17 (15 theory rows + 2 facts).

## Phase 2 - App: overlap-aware apply policy (no UI change yet)

Goal: a generation that overlapped a mutation is offered, not applied. Nothing user-visible changes in this phase, because the AI control is still disabled during mutations.

### A1. `AiTextApplyPolicy.cs`

Add a constant next to the others:

```csharp
    public const string MutationOverlapReason = "Another operation was changing files while this was being written; check it still fits.";
```

Replace `Decide` with (the first four parameters and every existing behavior stay; only the last parameter and its two uses are new):

```csharp
    public static AiApplyDecision Decide(string textAtClick, string textNow, bool scopeChanged, string staleReason, bool mutationOverlapped = false)
    {
        if (scopeChanged)
            return new AiApplyDecision(false, staleReason);
        if (!string.Equals(textAtClick, textNow, StringComparison.Ordinal))
            return new AiApplyDecision(false, EditedReason);
        if (!string.IsNullOrWhiteSpace(textAtClick))
            return new AiApplyDecision(false, mutationOverlapped ? MutationOverlapReason : null);
        if (mutationOverlapped)
            return new AiApplyDecision(false, MutationOverlapReason);
        return new AiApplyDecision(true, null);
    }
```

Default `false` keeps every existing caller (including New Pull Request and the five existing assertions in `AiGenerationTests`) byte-for-byte equivalent.

### A2. `AiTextGenerationRuns.cs`

1. In `Outcome`, add one trailing positional property with a default and extend the factory:

```csharp
    public sealed record Outcome(
        bool Cancelled,
        bool Success,
        TText? Text,
        string? Error,
        string ProviderLabel,
        string TextAtClick,
        AiGenerationWarning? Warning = null,
        bool MutationOverlap = false)
    {
        public static Outcome CancelledResult { get; } = new(true, false, null, null, "", "");

        public static Outcome Succeeded(TText text, string providerLabel, string textAtClick, AiGenerationWarning? warning = null, bool mutationOverlap = false) =>
            new(false, true, text, null, providerLabel, textAtClick, warning, mutationOverlap);

        public static Outcome Failed(string error, string providerLabel, string textAtClick) =>
            new(false, false, null, error, providerLabel, textAtClick);
    }
```

2. In `FinishAsync`, change the no-page fallback call:

```csharp
            decision ??= AiTextApplyPolicy.Decide(
                outcome.TextAtClick,
                CurrentText(key),
                scopeChanged: false,
                AiTextApplyPolicy.StaleChangesReason,
                outcome.MutationOverlap);
```

Everything else in the class (slot rules, locking, events) stays exactly as is. `WorkspacePullRequestTextGeneration` and `WorkspaceGitChangesCommitGeneration` need no edit.

### A6. `AiGenerationTests.cs`

Add after `Generated_text_is_applied_only_into_an_untouched_empty_field_for_an_unchanged_scope`:

```csharp
    [Fact]
    public void Generated_text_that_overlapped_a_mutation_is_offered_with_a_reason_and_never_applied()
    {
        var overlapped = AiTextApplyPolicy.Decide("", "", false, AiTextApplyPolicy.StaleChangesReason, mutationOverlapped: true);
        Assert.False(overlapped.ApplyDirectly);
        Assert.Equal(AiTextApplyPolicy.MutationOverlapReason, overlapped.Reason);

        // Existing user text: still a suggestion; the overlap reason is added for the user.
        var existing = AiTextApplyPolicy.Decide("my own message", "my own message", false, AiTextApplyPolicy.StaleChangesReason, mutationOverlapped: true);
        Assert.False(existing.ApplyDirectly);
        Assert.Equal(AiTextApplyPolicy.MutationOverlapReason, existing.Reason);

        // A moved scope or an edit keeps its own, more specific reason.
        Assert.Equal(AiTextApplyPolicy.StaleChangesReason, AiTextApplyPolicy.Decide("", "", true, AiTextApplyPolicy.StaleChangesReason, true).Reason);
        Assert.Equal(AiTextApplyPolicy.EditedReason, AiTextApplyPolicy.Decide("", "typed", false, AiTextApplyPolicy.StaleChangesReason, true).Reason);

        // Not overlapped: unchanged behavior.
        Assert.True(AiTextApplyPolicy.Decide("", "", false, AiTextApplyPolicy.StaleChangesReason, mutationOverlapped: false).ApplyDirectly);
    }
```

### A7. `WorkspaceGitChangesCommitGenerationTests.cs`

Add (uses the file's existing `Feature`, `NewGeneration`, `WaitUntilAsync` helpers):

```csharp
    [Fact]
    public async Task A_result_that_overlapped_a_mutation_is_a_suggestion_even_when_no_page_shows_the_context()
    {
        var memory = new WorkspaceGitChangesCommitMessageMemory();
        var generation = NewGeneration(memory);

        generation.TryStart(
            Feature,
            _ => Task.FromResult(WorkspaceGitChangesCommitGeneration.Outcome.Succeeded("ai text", "Claude", "", warning: null, mutationOverlap: true)),
            consult: null);
        await WaitUntilAsync(() => !generation.IsRunning(Feature));

        Assert.Equal(string.Empty, memory.Get(1, 10));
        var offer = generation.OfferFor(Feature);
        Assert.NotNull(offer);
        Assert.Equal("ai text", offer.Text);
        Assert.Equal(AiTextApplyPolicy.MutationOverlapReason, offer.Reason);
    }
```

### Phase 2 check

```powershell
dotnet build src/GrayMoon.App/GrayMoon.App.csproj
dotnet test src/GrayMoon.App.Tests/GrayMoon.App.Tests.csproj --filter "FullyQualifiedName~AiGenerationTests|FullyQualifiedName~WorkspaceGitChangesCommitGenerationTests|FullyQualifiedName~WorkspacePullRequestTextGenerationTests"
```

All existing tests in those classes must still pass untouched (they prove invariant 3 and the New Pull Request invariant).

## Phase 3 - App: free the AI control on Changes

### A3. `WorkspaceGitChanges.Ai.cs`

1. Add near `_aiGenerating` (after the `_aiGenerating` property):

```csharp
    /// <summary>
    /// The AI provider control is off only while this page's own overlay job runs (a commit, which may itself be writing
    /// the message as its first step). Generation takes no Workspace lock, so another operation on the Workspace (for
    /// example Push Updated started on Repositories) does not disable it; <see cref="IsJobRunning"/> is for the
    /// mutating buttons. A result that overlapped such an operation is offered as a suggestion, never applied silently.
    /// </summary>
    private bool IsAiControlDisabled => IsOwnPageJobRunning;
```

2. In `GenerateCommitMessageAsync`, extend the first guard (same page-own rule as the control, so a stray call cannot race the in-job generation):

```csharp
        if (_aiGenerating || IsOwnPageJobRunning || _aiMenu?.Pinned is not { IsAvailable: true } provider || _view is null || CurrentCommitGenerationKey is not { } key)
            return Task.CompletedTask;
```

3. After `var scope = BuildAiCommitScope(mode, targets);` add:

```csharp
        // Read on the renderer thread at the click: true when another operation on this Workspace is changing files now.
        var mutationBusyAtClick = IsJobRunning;
```

4. In the `run` lambda change the success outcome to carry it:

```csharp
                    return result.Success
                        ? WorkspaceGitChangesCommitGeneration.Outcome.Succeeded(result.Value!.Message, provider.Label, messageAtClick, result.Warning, mutationBusyAtClick)
                        : WorkspaceGitChangesCommitGeneration.Outcome.Failed(result.Failure!.Message, provider.Label, messageAtClick);
```

5. In `ConsultCommitApplyAsync`, change the `Decide` call (inside the existing `InvokeAsync`, which runs on the renderer, so `IsJobRunning` is safe to read):

```csharp
            decision = AiTextApplyPolicy.Decide(
                outcome.TextAtClick,
                _workspaceCommitMessage,
                scopeChanged,
                AiTextApplyPolicy.StaleChangesReason,
                outcome.MutationOverlap || IsJobRunning);
```

Do not change `GenerateCommitMessageInJobAsync`, `CommitMessageWriter`, `CommitGeneratesMessage` or anything that starts a commit.

### A4. `WorkspaceGitChanges.razor` (line about 150)

Change only this attribute on the `AiProviderControl`:

```razor
                                                   IsDisabled="@IsAiControlDisabled"
```

(was `IsDisabled="@IsJobRunning"`). Do not touch the `Commit Staged` / `Commit All` buttons (`IsCommitButtonDisabled` keeps `IsJobRunning`).

### A5. `WorkspaceGitChanges.JobHelper.cs`

In the XML summary of `IsJobRunning`, add one sentence after the Feature-selector warning:

```csharp
    /// Do NOT use this to disable the AI provider control either: generation takes no Workspace lock (see
    /// <see cref="IsAiControlDisabled"/>).
```

Comment only; no code change in this file.

### Phase 3 check

```powershell
dotnet build GrayMoon.slnx
dotnet test src/GrayMoon.App.Tests/GrayMoon.App.Tests.csproj
```

## Phase 4 - Documentation and README

Update docs in the same change. Edit by finding the quoted text; keep CRLF and ASCII hyphens.

### D1. `docs/architecture/04-runtime-communication-and-concurrency.md`

1. In "AI generation commands", replace the bullet that starts `` `CollectGitEvidence`, `GetAiProviders` and `GenerateAiText` ... run on the Worker main pool; there is no AI-specific queue `` with:

> - `CollectGitEvidence`, `GetAiProviders` and `GenerateAiText` (`WorkerHubMethods` constants) run on the dedicated Worker AI pool (`MaxConcurrentAiCommands`, default 4), not the main pool. The App reads CLI availability from `GetHostInfo` (main pool) and does not call `GetAiProviders`. The AI pool is untracked like the read and diff pools: it does not feed `ReportQueueStatus`, so the Worker badge activity frame does not animate for a generation (the AI control and the commit overlay show their own progress).

2. After the bullet that says commit generation runs inside the commit job, add:

> - Generation takes no Workspace or context lock. The Git Changes AI control is disabled only while its own page's overlay job runs (`IsOwnPageJobRunning`), not by `IsJobRunning`, so Push Updated or another operation on the Workspace does not block it. Evidence is read with `git --no-optional-locks` and never waits for `GitProcessRunner.RepoLocks`, so it can overlap a write. A result produced while the Workspace was mutation-busy (at the click or when the run finished, carried by `Outcome.MutationOverlap`) is always offered as a suggestion with `AiTextApplyPolicy.MutationOverlapReason`, never applied silently. The New Pull Request dialog keeps its own busy gate.

3. In "Worker job pools": add `AiCommands` to the routing sentence and the mermaid flowchart (`Route -->|"CollectGitEvidence, GenerateAiText, GetAiProviders"| Ai["AI pool"]` and `Ai -.-> Cap`), add `MaxConcurrentAiCommands   AI pool     default 4` to the sizes block, change "routing is by command name in `SignalRConnectionHostedService`" to "in `WorkerCommandPoolRouter` (called from `SignalRConnectionHostedService`)", add `AiJobBackgroundService` to the list of hosted services derived from `JobBackgroundService`, and remove "the AI commands" from the sentence listing what the main pool carries.

### D2. `docs/architecture/06-developer-extension-guide.md`

In the pool table (about line 433) add the row and fix the Main row:

```markdown
| AI (`AiCommands`) | `CollectGitEvidence`, `GenerateAiText`, `GetAiProviders` | `MaxConcurrentAiCommands` (4) |
| Main | everything else, including worktree create/remove and CodeGraph | `MaxConcurrentCommands` (`ProcessorCount * 2`) |
```

Also change the sentence above the table that names `Hosted/SignalRConnectionHostedService.cs` as the pool selector to mention `Services/WorkerCommandPoolRouter.cs`. Where the guide says the Git Changes AI control or generation is gated by busy state (search the file for `AI generation` and `busy`), keep it consistent with D1.

### D3. `docs/architecture/02-system-architecture.md`

Change "the AI commands run in the main pool" to "the AI commands run in their own AI pool", add `AiJobBackgroundService` to the hosted-services list, and add "Git AI pool" to the pool list above it.

### D4. `GrayMoon/CLAUDE.md`

In the Worker bullet (the long sentence beginning `Job queue: three separate bounded Channel<JobEnvelope> pools`): change to four pools, add `a dedicated AI pool (MaxConcurrentAiCommands, default 4) for CollectGitEvidence, GenerateAiText and GetAiProviders (routed by WorkerCommandPoolRouter)`, and add `AiJobBackgroundService` to the hosted-service list.

### D5. `GrayMoon.Desktop/README.md`

Add one bullet at the top of `## Recent GrayMoon changes` (above the existing first bullet), exactly:

```markdown
- **AI commit message button stays usable while Push Updated runs** - on Changes, the AI provider button next to Commit is no longer greyed out while Update and Push (or another operation) is running on the Workspace from Repositories. A message written during such an operation is shown as a suggestion with "Another operation was changing files while this was being written; check it still fits." instead of filling the box, and the Worker status badge no longer animates while an AI message is being written. Commit, Stage all and the other Changes buttons stay disabled until the operation ends, and New Pull Request is unchanged. Nothing else changes for users; the App and the Worker both changed and ship together, and Desktop needs no changes.
```

## Phase 5 - Verify, then close out

### Automated

```powershell
dotnet build GrayMoon.slnx
dotnet test src/GrayMoon.Common.Tests/GrayMoon.Common.Tests.csproj
dotnet test src/GrayMoon.Worker.Tests/GrayMoon.Worker.Tests.csproj
dotnet test src/GrayMoon.App.Tests/GrayMoon.App.Tests.csproj
```

Pass criteria: no new failures versus Phase 0; new tests: +17 Worker, +2 App.

Sanity greps (must return the expected result):

```powershell
rg -n "IsDisabled=\"@IsJobRunning\"" src/GrayMoon.App/Components/Pages/WorkspaceGitChanges.razor   # no match
rg -n "IsCommitButtonDisabled" src/GrayMoon.App/Components/Pages/WorkspaceGitChanges.MultiRepo.cs   # still uses IsJobRunning
rg -n "AiJobQueue|IAiJobQueue" src/GrayMoon.Worker/Cli/Handlers/RunCommandHandler.cs               # registered, never as IJobQueue / IWorkerQueueTracker
```

Also search the changed files for U+2014 / U+2013 and for LF-only line endings.

### Manual QA matrix (needs App + Worker + a Workspace with an AI CLI)

| # | Scenario | Expected |
|---|---|---|
| 1 | Start Update and Push on Repositories, open Changes during it | AI button clickable, Commit/Stage all disabled |
| 2 | Click AI during it with an empty box | Result appears as a suggestion with the "Another operation was changing files..." reason; box stays empty |
| 3 | Same with text already in the box | Suggestion with the overlap reason; typed text untouched |
| 4 | AI click while idle, empty box | Fills the box directly (unchanged) |
| 5 | Commit with empty box and the menu option "Generate commit message" ticked | Overlay writes the message, then commits (unchanged); AI control disabled by the overlay |
| 6 | Click Commit while a standalone generation runs | Behaves as before (button disabled when box empty) |
| 7 | New Pull Request dialog during a job | Every control, including the AI button, stays disabled (unchanged) |
| 8 | Generate AI while a Sync runs | Status scans and diffs stay responsive; Worker log shows `JobBackgroundService starting with 4 workers` for the AI pool |
| 9 | Start a generation, then watch the Worker badge | No activity frame for the AI run; other commands still animate it |
| 10 | Two contexts (Workspace + Feature) generating at once | Both finish; at most 4 AI commands run at once |

### Close-out

Trim `docs/todo/ai-generation-lane-decoupling.md` to what was not built (nothing in scope remains except the possible later work: gating the New Pull Request AI button separately from its `IsBusy`) and delete or mark this plan file "implemented", following `git-changes-large-changeset-scale.md`. Report the result in one sentence.

## Risks and mitigations

| Risk | Why it is safe or how it is contained |
|---|---|
| A bad DI registration stops the Worker starting | Same pattern as `DiffJobQueue`; Phase 1 build + the startup log line show it. Rollback: delete W3-W6, revert W1, W2, W7, W8 |
| AI jobs starve if 4 workers are busy with long CLIs | Bounded queue (8) with `Wait`; App's per-context slot limits one run per context; raise `MaxConcurrentAiCommands` in `appsettings.json` |
| Worker badge no longer shows AI | Intended and documented (D1, D5). The per-Workspace Repositories header never counted AI (no `WorkspaceId` on the requests) |
| Router refactor changes routing of an existing command | The router test pins every wire name; sets are copied verbatim |
| Button enabled but evidence reads a half-updated tree | Phase 2 first: overlap forces a suggestion; fingerprint still guards scope |
| Overlap flag set by an unrelated Feature's mutation | Result is a suggestion instead of a direct fill; acceptable and conservative |
| Mutation started and ended entirely inside the window with no scan change | Residual risk, bounded by the suggestion UX for edited/non-empty and by the user reviewing before commit; not worth a new runner counter |
| App and Worker version skew | App and Worker ship together (version lock); no capability flag needed |

## Out of scope

Anything in `WorkspaceOperationRunner` / job keys, the Update and Push orchestrator, commit and stage code paths, the New Pull Request dialog, changing `IsJobRunning` semantics, adding App-side AI queues, and any change to `AiTextGenerationRuns` slot or locking rules.
