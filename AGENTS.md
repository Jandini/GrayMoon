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

## Commit message after each task

After you finish a piece of work, always give a short commit message in a
fenced code block so the user can copy it. Do this at the end of the reply,
even if they did not ask for a commit. Do not create the commit unless they
ask.

Write one imperative sentence that says why the change exists, matching the
repo's recent style (`Fix …`, `Add …`, `Keep …`).

```
Fix the race that left the shared DbContext unusable after a failed merge
```
