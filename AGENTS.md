# AGENTS.md

Rules for AI coding agents working in this repository, in addition to `CLAUDE.md`.

## DbContext handling (do not skip this)

This has caused real production-visible bugs twice: a bulk PR merge threw
`System.InvalidOperationException: A second operation was started on this context
instance before a previous operation completed`, and the failure left the shared
`AppDbContext` unusable for the rest of the browser tab - including the unrelated
Git Changes page, which then failed to load with
`System.ObjectDisposedException: Cannot access a disposed object. Object name:
'SQLitePCL.sqlite3'`.

### Why this happens

`AppDbContext` is registered as **scoped** (`Program.cs`, built from
`IDbContextFactory<AppDbContext>` - see "DbContext lifetime" in `CLAUDE.md`). In
GrayMoon.App, a Blazor Server **circuit is one browser tab**, and a circuit is one
DI scope. That means:

- Every component in that tab that does `[Inject] private AppDbContext DbContext`
  gets the **same instance**.
- `AppDbContext`, like any EF Core `DbContext`, is **not thread-safe** and does not
  support two operations running on it at once, even two different `await`ed calls
  from two different places in the same tab.
- If anything throws while the shared instance has an operation in flight, that
  instance can be left in a broken/disposed state for the **rest of the tab's
  lifetime**, not just for the caller that broke it.

### The rule

1. **Never inject `AppDbContext` directly into a Blazor page/component
   (`[Inject] private AppDbContext DbContext`).** Inject
   `IDbContextFactory<AppDbContext>` instead and create a short-lived context per
   operation:
   ```csharp
   [Inject] private IDbContextFactory<AppDbContext> DbContextFactory { get; set; } = default!;

   await using var db = await DbContextFactory.CreateDbContextAsync(cancellationToken);
   var thing = await db.SomeTable.AsNoTracking().FirstOrDefaultAsync(...);
   ```
   `WorkspaceGitChangesReadService` and `WorkspaceGitChangesWriteQueue` are the
   in-repo reference examples of this pattern.

2. **Any repository/service method that calls `SaveChangesAsync`, or that is ever
   invoked concurrently (from `Task.WhenAll`, a `SemaphoreSlim`-bounded fan-out, or
   similar), must use `IDbContextFactory<AppDbContext>` and create its own context
   for that call.** Do not accept an injected scoped `AppDbContext` in a class whose
   methods can run in parallel with themselves or with each other. This is what
   broke bulk PR merge: `WorkspacePullRequestService.MergePullRequestsAsync` fans
   out merges with a bounded semaphore, and each parallel branch persisted through
   the same scoped `AppDbContext` via `WorkspacePullRequestRepository`.

3. **Background/fire-and-forget work that can outlive the page (auto-poll loops,
   `Task.Run` bodies) must resolve its own DI scope**
   (`IServiceScopeFactory.CreateAsyncScope()`), not reuse the circuit-scoped
   instance injected into the page. A multi-step background operation whose
   earlier steps rewrite rows takes a fresh scope and context per step (the
   Update and Push push lane does this per level). `WorkspaceActions.AutoRefresh.cs`'s
   `RefreshRowAsync` is the in-repo reference example - see its comment for why.

4. A plain scoped `AppDbContext` field used only for simple, one-at-a-time reads in
   a service that is never fanned out is fine and does not need to change - do not
   rewrite working call sites that do not match rule 2 or 3 just to be thorough.

### Before merging a change that touches `AppDbContext` usage

- Grep the class for every `Task.WhenAll`, `SemaphoreSlim`, or
  `IServiceScopeFactory.CreateAsyncScope()` call and check whether a
  DbContext-touching method sits inside that fan-out.
- If a Blazor page injects `AppDbContext` directly, treat that as a defect to fix
  as part of the same change, not a followup - it is a silent hazard for every
  other feature sharing that circuit, not just the one you are working on.

## Database schema changes

Every schema change needs the `AppDbContext` model update **and** a new guarded,
idempotent `Migrate*Async` method appended to `Migrations.StrictSteps` with the
next free version number (tracked in SQLite `PRAGMA user_version`; each step runs
in its own transaction). Never edit, renumber or delete a shipped step, and never
add to the legacy baseline. Pre-release is not an exception: everyone's existing
`graymoon.db` needs it. See "Database schema" in `CLAUDE.md`.

## Feature-context scoping (worktree Features)

This has caused a real cross-context data-corruption bug: a Feature's dependency
sync recomputed `DependencyLevel`/`Dependencies`/`UnmatchedDeps` from a project
graph that silently mixed every context's rows together, then wrote the single
result onto the special Workspace's own `WorkspaceRepositoryLink` row - so simply
running Update inside a Feature visibly changed the Workspace's own Dependencies
grid.

### Why this happens

A `WorkspaceRepositoryLink` row is shared by the whole workspace (one row per
repo, no context column). A Feature's *own* view of that repo's git/dependency
state lives in a separate `WorkspaceRepositoryContextState` row (one per
`(WorkspaceFeatureContextId, WorkspaceRepositoryId)`), plus context-scoped
sibling tables for PRs, Actions, and Git Changes. `WorkspaceProject` rows also
carry a `WorkspaceFeatureContextId` column. Any query or write that touches one
of these tables **filtered only by `WorkspaceId`** (no context filter) either
mixes every context's data into one answer, or persists onto the wrong row.

### The rule

1. **Any new or edited query/command that reads or writes
   `WorkspaceProject`/dependency stats/`GitVersion`/PR/Actions/Git-Changes data
   for a repository must take a `WorkspaceFeatureContextId?`/`bool
   isSpecialWorkspace` pair** (or an equivalent contextId), not just
   `workspaceId`. Follow the existing convention: omitting the context (or
   passing `isSpecialWorkspace: true`) must reproduce the pre-Feature legacy
   behavior exactly - read straight off the shared `WorkspaceRepositoryLink`/
   `WorkspaceProject` rows.
2. **For a Feature context, read/write the context-scoped table
   (`WorkspaceRepositoryContextState` et al.), never the shared link/row.**
   Never fall back to the Workspace's own value when a Feature has no context
   row yet - the correct answer is "unknown"/`null`, not "borrow the
   Workspace's". `WorkspaceRepositoryLinkListQueryService.Project(...)` and its
   `ApplySort`/`ApplyKeyset`/`GetIndexAsync`/`GetRepositoryIdsAtLevelAsync`/
   `GetGitVersionNameMapAsync` siblings are the in-repo reference examples of
   this "special Workspace reads the link, Feature reads-or-nulls the context
   state" branch.
3. **Only mirror a write onto the shared `WorkspaceRepositoryLink`/
   `WorkspaceProject` row when the context actually is the special Workspace.**
   A Feature's recompute/sync must never touch those shared rows -
   `RecomputeAndPersistRepositoryDependencyStatsAsync` is the in-repo reference
   example (get-or-create the context state row, mirror onto the link only for
   `isSpecialWorkspace`).
4. **Any dictionary/lookup keyed by something that isn't globally unique across
   contexts (e.g. `(RepositoryId, ProjectFilePath)`) must include the context id
   in the key**, or two contexts' rows for "the same" project path silently
   collide and one overwrites the other.
5. When adding a context-aware overload to an existing method, **grep every
   existing call site and update the ones that already have a `contextId` in
   scope** (e.g. a Blazor page's `_selectedContextId`/`_isFeatureContext`
   fields) - a half-migrated method (data layer accepts a context but no caller
   passes one) is a silent no-op fix, not a real one.

6. **`WorkspaceRepositoryStateWriter` is the only writer of per-context
   repository state.** Do not set `BranchName`, `CheckedOutTag`,
   `BranchHasUpstream`, sync status or sync error on a
   `WorkspaceRepositoryLink` directly from a push, commit-sync, bookkeeping or
   UI path. Post-operation bookkeeping (upstream probe, remote-branch rows,
   commit counts, the tag-pinned check) reads branch and tag from the context
   the operation ran in, not from the shared link. This is what
   `WorkspacePushService` post-push bookkeeping and
   `WorkspaceCommitSyncHandler.SetSyncStatusErrorAsync` got wrong (review
   2026-11-10, finding 2).
7. **UI that summarises repositories for a context (notification panels,
   warnings, counters) queries by workspace and context.** Do not compute it
   from a capped display list (for example the 100-capped `n.Repos`) or from
   special-Workspace link columns when the context is a Feature.

### Before merging a change that touches Feature/worktree data

- Grep the table/column you changed for every other query or write that reads
  it without a context filter - a "context-scoped write, workspace-scoped read"
  (or vice versa) split is exactly the shape of bug this section exists to
  prevent.
- Add a test that seeds a Feature context whose `WorkspaceRepositoryContextState`
  (or equivalent) is deliberately different from the shared link/row, and
  assert the context-scoped read/write actually used the context data, not the
  link - see
  `WorkspaceRepositoryLinkListQueryServiceTests.Feature_context_sort_keyset_and_level_grouping_use_context_state_not_shared_link`
  for the pattern.

## Tag-pinned repositories are never part of mutating operations

A repository checked out on a tag (Workspace tag pin in `graymoon.json`, or any
`CheckedOutTag` / `IsOnTag`) must not be included in Push, Sync Commits, Update
Dependencies, Return to Default, Prepare Workspace branch work, bulk Merge PRs...
return-to-default follow-ups, or any similar mutating multi-repo operation.

1. **Filter at the call site** - build candidate id sets with `!wr.IsOnTag` (or
   equivalent). UI tips such as "Repositories checked out on a tag will not be
   affected" match this rule.
2. **Service paths leave tags untouched** - if a tag id still reaches an
   operation, skip it (do not mutate, do not fail the batch for that reason).
   Return to Default classifies `IsOnTag` as excluded, not blocking
   (`ClassifyRepository`, unattended batch, `ExecuteReturnToDefaultAsync`).
3. **Exceptions** - switching onto a tag (which records the pin) and leaving a
   tag through GrayMoon Switch Branch / checkout are intentional.

See `docs/architecture/06-developer-extension-guide.md` section 11 and
`docs/architecture/05-user-capability-reference.md` (tagged repositories /
Return to Default).

## Rules from the 2026-11-10 code review

Every rule below comes from a confirmed defect in
`docs/code-review/2026-11-10-code-review.md` (workspace root; fix plan next to
it). The finding number is in brackets. Follow them in new code and when
editing nearby code; do not copy an existing violation because "the file
already does it".

### Blazor page jobs: page state only on the dispatcher

A job body (`StartJob`, `StartPageJob`, `StartWorkspaceJob`, any `Task.Run`)
runs on the thread pool while the dispatcher may be rendering the page.

1. **Never assign or mutate page fields that the markup reads from a job
   body** - collections such as `_slots`, `_linkByRepoId`, `prByRepositoryId`,
   `repoSyncStatus`, or any status dictionary. Load on the job thread into
   locals or a snapshot object, then apply it inside `InvokeAsync` /
   `SafeInvoke`. The apply step checks `_disposed` and the context generation
   first. [4, 14]
2. **A reload helper that a job awaits must be a load step plus a dispatcher
   apply step.** Do not make it "safe" by catching `InvalidOperationException`
   around a swap done on the job thread; that hides a broken circuit. [4]
3. **Capture the context before the job is queued.** Write
   `var contextId = RequireSelectedContextId();` (and the context generation)
   before `StartJob`, and use the captured values inside. Never call
   `RequireSelectedContextId()` inside a job lambda or inside a
   `ScopedExecutor.ExecuteAsync(svc => ...)` lambda that runs in a job: the
   user can change `?context=` mid-job and the job then targets another
   context's branches. Drop the result when the generation changed. [13]
4. **A job must never touch page-owned disposables after the page is
   disposed** (see "Background job system" in `CLAUDE.md`).
5. **Do not gate one-time setup on `firstRender` when it needs loaded data.**
   The first render happens at the first `await` in `OnInitializedAsync`,
   before the data is there, so `if (firstRender && workspace != null)` never
   runs. Start hub connections and background refresh at the end of the load,
   or on any render while the connection is still null and the data is
   present. [5]
6. **Check that a job actually started.** `StartJob` / `StartWorkspaceJob` can
   return the handle of another running operation, or refuse. A caller without
   an `IsJobRunning` guard must check the result and must not report "running"
   or "completed" for work that was dropped. [12]
7. **Every async load that can be restarted needs a generation guard and its
   own `CancellationTokenSource`.** Reopening a modal, changing a parameter
   (`RunId`), or typing a new search cancels the previous load and ignores its
   late result; a newer request is never silently dropped while an older one
   is in flight. `FilterSearchInput` and `DebouncedQueryLoader` are the
   reference examples. [28, AddFilesModal, GhaWorkflowLiveTerminal]
8. **Background loops in components catch every exception, log it, and keep
   going or stop cleanly.** Catching only `OperationCanceledException` lets
   one fault end the loop for good. `DisposeAsync` never rethrows a stored
   loop fault. [GhaWorkflowLiveTerminal]
9. **JS interop from render or dispose handles `JSDisconnectedException` and
   `InvalidOperationException`**, and is not called on every render when
   nothing changed. Focus an element once (first render or on open), not on
   every render. [LoadingOverlay, Workspaces.razor]
10. **Every submit path of a modal uses the same guard.** Enter, the button,
    and each mode (Save, Delete) share one "already submitting" check; a
    validation that exists on one tab exists on every tab that performs the
    same action. [ConnectorModal, SwitchBranchModal]
11. Start a page's workspace-sync hub through
    `WorkspaceSyncHubConnectionHelper`, not a hand-rolled `StartAsync`.

### Service lifetimes and event subscriptions

1. **A singleton never holds per-circuit or per-user state.** One browser tab
   is one circuit; state in a singleton is shared by every tab. UI
   notification state (toasts, banners, selection) belongs in a scoped
   service. `IToastService` is scoped (one per tab); a singleton that has
   something to tell the user raises an event and a per-circuit component
   shows it (`FeatureRemovalQueue.Completed` to `Toast.razor`). [15]
2. **Every subscription to a longer-lived object is removed in `Dispose`.**
   A component or scoped service that calls `OnStateChanged(...)` or `+=` on a
   singleton (`WorkerConnectionTracker`, queue state, hub helpers) must call
   the matching `RemoveStateChanged(...)` / `-=`. Keep the delegate in a field
   so the same instance is removed. Do not subscribe during prerender. [3]
3. **Do not inject a scoped service into a singleton.** When changing a
   lifetime, grep every consumer first.
4. A fan-out or a job that outlives the page resolves its own scope (see
   "DbContext handling").

### Destructive and merge operations

1. **"Not in the fetched set" is a reason to delete only when the fetch
   succeeded.** A sync or merge that removes rows missing from fresh data must
   limit deletions to the sources that answered successfully. A connector that
   failed (401, rate limit, timeout) contributes no deletions, and the result
   names what was skipped. [1]
2. **Deleting or unlinking an entity removes every dependent table's rows**,
   in every context. When you add a table that references a repository, link
   or context, add it to the matching cleanup
   (`WorkspaceRepositoryLinkCleanup`) and its test in the same change. [16]
3. **Re-check the precondition when a queued destructive action runs.**
   A choice made in an analysis dialog (discard, force) covers what the user
   saw. If the repository changed between analysis and execution, skip it with
   a message; never carry `force` forward blindly. [18]
4. **Rollback and cleanup must not depend on the folder being empty.** Mark a
   leftover folder with `PendingDeleteMarker` so a later Create of the same
   name can finish it. [19]
5. **A backup or integrity check that fails stops the operation** with a
   message naming the path and the cause. Do not log and continue, and do not
   let the raw exception end the process. Skip a backup of a missing or empty
   file. [17]

### Failures, cancellation and waiting

1. **A step may be best-effort, but its outcome is always reported.** A
   Worker command returns `Success=false` (with the stderr tail) when its
   process exits non-zero; the App reads the response and surfaces at least a
   warning. Never discard a Worker response, and never
   `catch (Exception) { }` without logging and returning an outcome. [8]
2. **Inside a wait or poll loop, transient transport errors mean "not yet".**
   `HttpRequestException`, socket errors and a per-request timeout are retried
   until the loop's own deadline; the timeout message carries the last error.
   One DNS blip must not abort a multi-minute wait. [10]
3. **Parallel lanes observe each other's failure promptly.** When one lane
   faults, the other is cancelled at its next boundary, and the final message
   names where it stopped and what was not done. An unknown outcome is a
   failure. [11]
4. **Register for cancellation after the entry exists.** Add to the
   dictionary first, then `cancellationToken.Register(...)`; handle a token
   that is already cancelled, or the callback runs before the entry exists and
   the entry leaks. [9, CommandJobCancellationRegistry]
5. **A long-lived reader loop (`BackgroundService`, `Channel` reader) rethrows
   `OperationCanceledException` only when its own stopping token is
   cancelled.** Any other OCE is one item's failure: log it, fail that item,
   read the next. Shutdown loops (reconnect, retry) observe the stopping
   token. [30]
6. **Retry only what can succeed.** Do not retry `TaskCanceledException` when
   the caller's token is cancelled, and bound the total wait of a retry
   chain. [GitHubService.Http]
7. **Scope failure to what failed.** When a connection drops, fail the
   requests routed to that connection id, not every pending request; do not
   pick "the" Worker connection with `FirstOrDefault`. [32]
8. **Swap or dispose a shared `CancellationTokenSource` atomically**
   (`Interlocked.Exchange`, or one lock shared by Cancel and Finish). [AI
   runs, line stats]
9. **`First()` / `Single()` on user or configuration data needs a clear
   error.** Use `FirstOrDefault` and report "provider X is not configured".
10. **Process stdin writes are inside the `IOException` handling.** A process
    that exits before reading stdin yields its exit-code result, not an
    exception. [31]
11. **Bounded collections stay bounded.** A static or singleton dictionary,
    set or handler list needs a removal path (`_finishedInits`, pending maps,
    caches). Delete dead caches instead of leaving them.

### Worker: input, processes and git

1. **Every path that arrives from outside the Worker is validated before
   use** - Worker request DTO properties (`*Path`, `*Paths`) with
   `GitRepositoryPathValidator` (rejects rooted paths and `.`/`..`), and hook
   payloads against a workspace root the Worker already knows. The App is not
   a trusted source of paths. [21, 22]
2. **Never build a command line by string interpolation.** Use
   `ArgumentList`; put `--` before refs and paths so a name starting with `-`
   is not an option; use `:(literal)` pathspecs. [21, GitCliRepositoryReader]
3. **Launch every process through `ICommandLineService` /
   `IBoundedProcessRunner`** and take an `IRepositoryAccess` lease for a new
   LibGit2Sharp handle or file walk.
4. **Never assume `.git` is a directory.** In a Feature worktree it is a
   file. Resolve git-internal files with `git rev-parse --git-path <name>`
   (`MERGE_HEAD`, `index.lock`, hooks), never
   `Path.Combine(repo, ".git", ...)`. Test with a `GitScenarios` worktree. [20]
5. **Local HTTP listeners cap the body and check the content type** (64 KB,
   `application/json`, 413 / 400 otherwise) and stay bound to loopback. A
   loopback caller is not automatically trusted: any local process or a
   browser page can POST to `127.0.0.1`. [22]
6. **A SignalR handler never blocks on a bounded queue.** Use `TryWrite` and
   answer "queue full", or hand the wait to a background task, so
   `CancelCommand` is never stuck behind a `RequestCommand`. [23]
7. **No fire-and-forget work outside the pools.** Follow-up scans run through
   the matching pool with the job's cancellation, so the queue count and
   cancel cover them. [PushRepositoryCommand, UndoPushCommand]
8. A sweep that deletes takes the same lock as the code that adds.
   [FeatureFolderCleaner]

### Trust boundaries (App)

1. **Secure by default.** A secret, an allowlist or an origin check must be
   required from the first start, not "optional until configured". An empty
   allowlist means loopback only, with a startup warning - never "allow
   all". [7, Worker secret]
2. **An Origin whose host equals the request Host is not proof** (DNS
   rebinding). Accept loopback or a host listed in `GRAYMOON_AllowedHosts`.
   Do not add `AllowedHosts "*"` style defaults. [7]
3. **An endpoint that returns a credential requires the paired Worker
   secret**, whatever the other middleware allows. Never return a PAT to an
   unauthenticated caller. [7]
4. **Everything that goes into an AI prompt passes `Sanitize` and
   `EvidenceRedactor`** - commit subjects and branch names as well as
   diffs. [AiPromptBuilder]
5. **Keep the raw value for I/O and escape only for output.** Do not download
   from an HTML-escaped URL or store an escaped path. A server-side fetch of
   a user-supplied URL has a size cap and a deliberate host policy.
   [MarkdownImageEmbedder]
6. A version or feature gate must not return before the writes that are
   always required. [GitChangesSnapshotPushHandler]

### Migrations

In addition to "Database schema changes":

- A tolerant `catch` in a migration clears the change tracker
  (`ChangeTracker.Clear()`), or the half-added entities are flushed by a
  later step.
- Every migration helper called from a tolerant block has the same catch
  shape; do not leave one call unguarded next to guarded ones.

### Tests

- **A fix ships with a regression test that fails on the old code.**
- **A failure-path test must seed data that the failure could destroy.**
  The connector-refresh fan-out test used `repoCount: 0` for the failing
  connector, so it could not see the deletion. Give the failing side rows and
  assert they survive. [1]
- Path and ref handling gets tricky-input cases: rooted, `..`, space, quote,
  leading `-`.
- A convention that matters (subscriptions removed, context captured, paths
  validated) gets a source-scan test under `*.Tests/Conventions/` once the
  existing violations are fixed.

### Documentation matches the code

When a document describes behavior the code does not have (single-instance
activation, idle polling tier), fix the code or the document in the same
change and say which. Do not describe intended behavior as current in
`docs/architecture/` or in `CLAUDE.md`. When a review finding is fixed, update
its row in the review document to `Fixed (<branch>)`.

## Buttons and short action labels never word-wrap

When adding or changing Blazor UX, button text must stay on a single line.
The same applies to short labels paired with buttons (checkbox captions such
as "Push committed", toolbar action text, and similar). Use Bootstrap
`text-nowrap` and/or `white-space: nowrap`, and on tight flex rows also
`flex-shrink: 0` / `flex-wrap: nowrap`, so a narrow column never stacks
"Commit" / "Commit All" or wraps a checkbox label. See also "CSS conventions
for buttons and action labels" in `CLAUDE.md`.

## Desktop README when features change

When you add a user-facing feature or change how an existing one behaves,
update the sibling `../GrayMoon.Desktop/README.md` **Recent GrayMoon changes**
section in the same turn (that file lives in the Desktop repo, not this one).

- **New feature** - add a bullet at the top of the list.
- **Existing feature** - edit the matching bullet instead of adding a duplicate.
- Match the existing style: `- **Short title** - one paragraph of what the
  user sees and how it works.` The separator is ` - ` (ASCII hyphen), never
  an em or en dash.
- Skip internal-only refactors, tests, docs-only edits, or plumbing with no
  user-visible behavior change.
- Do not create a Desktop commit unless the user asks.

## Architecture docs stay current

`docs/architecture/` describes the current state. Update the relevant
architecture doc in the same change as the behavior it describes. Proposals
and design notes for work not yet built go in `docs/todo/`.

## Never commit or push without an explicit request

**Never run `git commit` or `git push` (or stage, tag, amend, or open a pull
request) unless the user explicitly asks for that exact action in the current
message.** Requests like "update the README", "fix it" or "let me build it"
are not requests to commit or push. Leave the working tree's changes
uncommitted.

## Unchecked checkboxes and radios are gray

Every new checkbox or radio gets `class="form-check-input form-check-input-gray"` so it is gray, not Bootstrap white, until checked (see `.cursor/rules/gray-unchecked-checkbox-radio.mdc`). Do not add per-page overrides.
