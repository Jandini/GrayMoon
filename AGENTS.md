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
   instance injected into the page. `WorkspaceActions.AutoRefresh.cs`'s
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
  user sees and how it works.`
- Skip internal-only refactors, tests, docs-only edits, or plumbing with no
  user-visible behavior change.
- Do not create a Desktop commit unless the user asks. If that README changed,
  give a second short commit message labeled for GrayMoon.Desktop.

## Never commit or push - always give a commit message instead

**Never run `git commit` or `git push` (or any equivalent staging/committing/
pushing action) yourself, even if the user's request could be read as asking
for one.** Leave the working tree's changes uncommitted; committing and
pushing are the user's call to make, not the agent's.

After you finish a piece of work, always give a short commit message in a
fenced code block so the user can copy it. Do this at the end of every reply
that changed files, whether or not the user asked for a commit message.

Write one imperative sentence that says why the change exists, matching the
repo's recent style (`Fix …`, `Add …`, `Keep …`).

```
Fix the race that left the shared DbContext unusable after a failed merge
```
