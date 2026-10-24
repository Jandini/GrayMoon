# Git Changes: scale to tens of thousands of changed files

Status: Phases 0–1 are implemented (permanent benches + M0; circuit/tree summaries, lazy expand, `Virtualize`, per-repo live patch). Phases 2–5 are not (next: Phase 2, then Phase 3a digest only; Phase 3b, 4 and 5 are postponed). Current product behavior is documented in `docs/architecture/07-git-changes-projection-and-tree.md` (fold Phase 1 detail in Phase 5). Progress is tracked in `docs/todo/git-changes-large-changeset-scale-tracker.md`.

## 0. Non-negotiable requirements (full inventory)

**Graceful scale means rendering / windowing / loading strategy only - never reducing the inventory.**

- **No hard caps** on how many changed files may exist, be persisted, or be reached by the user.
- If git reports 20K (or 100K) changed paths, **all of them remain available**: scroll the virtualized list, expand every folder, page through every prefix, search/jump to any match. Nothing is dropped as "first N", hidden behind "too many files - refine your filter", or otherwise truncated from the product inventory.
- Paging (`skip`/`take`), virtualization, lazy folder expand, and chunked Worker?App transfer are **transport and paint windows**. They fetch or render a slice at a time; they do not define a maximum set size. Every path in Layer B (SQLite) must be reachable through further scroll, expand, or search pagination.
- Forbidden UX: silent truncation, "showing 1-500 of 20_000", hard stop walls, or omitting paths from persistence to save work.
- Allowed UX: progressive paint, collapsed-by-default folders on large sets, virtualized DOM, server-backed pages of visible rows, search result pages that continue until every match is listed.

The small-inventory fast path (section 4.2 / 4.11) is an **internal loading optimization only**: below a count threshold the App may hydrate the full list in one read (today's behavior). That threshold must never become a user-visible limit on how many files exist or can be opened.

## 1. Problem

Opening the Changes page with a very large inventory (thousands to tens of thousands of paths) is already painful around ~5K. The product impression matches the code: **everything is loaded into the Blazor Server circuit**, then the tree is built and painted as a flat `@foreach` of visible rows with **no virtualization**. Live watcher updates reload the **entire** context inventory again. The inventory itself is already uncapped; the failure is that the client materializes and renders it all at once.

What must stay true (in addition to section 0):

- App never runs git; Worker owns status, watchers, diffs and mutations.
- Opening Changes still paints from SQLite without a Worker round-trip for the tree.
- Small workspaces (tens to low hundreds of paths) must not regress - no mandatory multi-hop loading tax.
- Snapshot versioning / latest-wins, write-queue serialization, and Feature context scoping stay intact.
- Stage / unstage / discard / commit / multi-select / search / AI commit evidence remain correct for the user's intent, even when the UI has not materialised every path in the circuit yet (scopes and Layer B queries cover the full set).

## 2. Bottlenecks (today)

| Hop | What happens | Why it hurts at 10Ks |
|---|---|---|
| Worker | `git status --porcelain=v2 -z --untracked-files=all` ? full `GitChangeSnapshot.Changes` | CPU/IO and large in-memory lists per watched repo |
| Worker ? App SignalR | Full `GitChangesSnapshotNotification` (10 MiB receive cap) | JSON size, deserialize GC, hub contention |
| App SQLite | Delete all entries for repo+context, insert all | Write-queue latency; WAL growth; slow subsequent reads |
| Circuit `LoadAsync` | All context entries ? `WorkspaceGitChangesView` | Per-tab retained heap; reload on every `ContextGitChangesUpdated` |
| `GitChangesTreeBuilder` | Filter + flatten; default **fully expanded** | O(N) rebuild on filter keystroke / expand |
| `GitChangesTree.razor` | `@foreach` every row, no `Virtualize` | Thousands of DOM nodes synced over the circuit |

Collapse-aware flatten only skips **rendering** descendants; it never shrinks `_view`, SQLite, or SignalR payloads.

## 3. Design goals

1. **All paths remain reachable** at any scale (section 0) - discoverable by scroll, folder expand, and search/jump with continued paging until exhausted.
2. **Open Changes in bounded time and memory** even when the context has 50K+ paths (target: interactive tree chrome + repo/folder summaries within ~200-400 ms of SQLite readiness on a warm App; file rows fill progressively as the user explores).
3. **Circuit memory O(visible viewport)** for the tree, not O(total paths). Retain only the virtualized window (plus overscan and hydrated folder pages the user opened); eviction of unused page caches never deletes paths from Layer B or blocks re-fetch.
4. **Worker ? App payloads stay manageable** for status pushes (prefer metadata + digests; stream or page path bodies when needed) while still persisting **every** path into SQLite before the snapshot version is committed.
5. **Search and bulk actions** work without requiring the circuit to hold every path, yet can still address the full matching set (paged results, Worker scopes).
6. **Small inventory fast path** identical in UX to today (single read, full expand, immediate file rows) below an internal hydration threshold - not a product limit.
7. **No App/Worker version fork** - ship together; no capability flags (per GrayMoon version lock).

Non-goals for the first shippable arc:

- Capping, truncating, or sampling the changed-file inventory (explicitly rejected - see section 0).
- Changing git's own cost of `status` on a dirty mega-tree (can be mitigated later with optional pathspec / ignore heuristics; not required for UI scale).
- Replacing Monaco diff virtualization (already on-demand per file).
- Persisting the tree expand/collapse model in SQLite. Summary-mode expand state is kept in browser `localStorage` instead (section 4.5, "Expand state survives reload").

## 4. Proposed architecture

### 4.1 Headline: summary-first projection + windowed tree + optional path store

Split today's single "full entry list everywhere" model into three layers:

```text
Layer A - Repository / folder summaries (always hot)
  counts, branch, line stats, top-level folder rollups, content digest

Layer B - Path inventory (App-owned, queryable, not circuit-resident)
  all paths for context+repo, indexed for prefix and search

Layer C - Visible window (circuit-only)
  flattened rows for expanded + scrolled viewport (+ small overscan)
```

Worker still produces authoritative porcelain. The App persists Layer B. Circuits fetch Layer A immediately and Layer C on demand. Small inventories may still hydrate Layer C in one shot (compatibility fast path).

### 4.2 Internal loading knobs (tunable in `GitChangesOptions`)

These tune **how** data is fetched and painted. They are **not** product limits on inventory size. Counts above every knob remain fully reachable via further pages / scroll / expand / search.

| Name | Suggested default | Meaning |
|---|---|---|
| `FullHydrationMaxEntries` | 1_500 | **Internal only:** at or below this, use today's single-shot circuit hydration + full expand. Above it, use summary-first + lazy pages. Never hides paths. |
| `TreePageSize` | 200 | Rows fetched **per request** when expanding a folder or asking the virtualizer for more items. Scroll/expand continues until the prefix is exhausted. |
| `FolderRollupDepth` | 2 | Depth of folder summaries computed at persist time for Layer A (badges); deeper folders still open and list every file. |
| `SnapshotPathsInlineMax` | 2_000 | Per-repo paths inlined on one SignalR message; above this, **chunked transfer of the remainder** until every path is persisted. |
| `CircuitVisibleRowBudget` | 500 | Soft bound on **retained** `GitChangesTreeRow` instances (evict collapsed page caches). Re-expand re-fetches from Layer B; inventory untouched. Must be at least `TreePageSize` plus overscan, and eviction must never drop pages inside the virtualized window. |

These live on the existing `GitChangesOptions` (`GrayMoon.Common.Git`), which today holds only concurrency, debounce and watcher settings. Unlike those, the App-only knobs here need no Worker copy. `SnapshotPathsInlineMax` is the one the Worker also reads, which is fine under the version lock. Defaults are placeholders until M0 numbers exist.

### 4.3 Server-side aggregation (Layer A)

Extend the persisted status model (context tables) with cheap rollups written in `GitChangesSnapshotPushHandler` when a snapshot is applied:

```text
WorkspaceGitContextRepositoryStatus (existing counts stay)
  + ContentDigest            // hash of sorted (path, index, worktree, ...) tuples
  + PathsTransferPending     // true while chunked Worker path pages for this version are still arriving (not user-facing truncation)

WorkspaceGitContextFolderSummaries (new, optional table)
  WorkspaceFeatureContextId
  WorkspaceRepositoryId
  Section                    // Staged | Changed (or two rows)
  FolderPrefix               // "" = repo root; "src/Foo"
  Depth
  FileCount
  ConflictCount
  // optional: dominant change kinds bitset
```

Rollups are derived from the same entry set as today - one writer, no second source of truth. `HasAnyChangesAsync` and the nav dot keep using status counts (already O(repos)).

**Opening Changes (large path):**

1. Read all repository **status** rows + folder summaries for the context (small).
2. Build section ? repository ? (collapsed) folder skeleton with badges from summaries.
3. Do **not** load `WorkspaceGitContextChangeEntries` into `_view` yet.
4. As the user expands a folder (or the fast path auto-expands when under threshold), query Layer B for that prefix page.

### 4.4 Path inventory (Layer B)

Keep `WorkspaceGitContextChangeEntries` as the path store (already exists; `Path` is `MaxLength(2000)`, with `IndexChange`, `WorktreeChange`, `IsConflicted`, `IsTracked`, `IsSubmodule` columns). Today it has only a non-unique `(WorkspaceFeatureContextId, WorkspaceRepositoryId)` index. Add indexes for scale; the first one supersedes the existing index (drop it in the same migration, since its prefix is covered):

```text
IX (WorkspaceFeatureContextId, WorkspaceRepositoryId, Path)   // replaces existing (ctx, repo) index
IX (WorkspaceFeatureContextId, Path)                          // cross-repo search
```

Query rules that the read service must follow:

- **Sections are derived, not stored.** A path appears in *Staged* when `IndexChange != None` and in *Changed* when `WorktreeChange != None` (or untracked/conflicted), so one path can be in both sections. Per-section counts and folder summaries therefore do not sum to the path count; `PathCount` / `ContentDigest` are per distinct path, summaries per section.
- **Prefix match uses a binary range, not `LIKE`.** Use `Path >= prefix + '/'` and `Path < prefix + '0'` (`'0'` is the character after `'/'`) with ordinal collation. SQLite `LIKE` is ASCII case-insensitive and treats `%`/`_` as wildcards, which would mis-match real file names and may not use the index.
- **Paging is keyset, not `skip`/`take`.** Live updates replace entries between page fetches, so offset paging can skip or duplicate rows. Fetch "next `TreePageSize` rows after last path seen" and carry the snapshot version with the page so the UI can detect a stale cache. (The public method signature may still expose `skip`/`take` for the virtualizer; translate internally or cache per version.)
- **A folder page lists direct children, not all descendants.** The tree needs child folders and files at one level. A flat prefix scan returns every descendant, so the read service must either group by next path segment (`substr`/`instr` on the remainder) or the paging unit must be defined as "descendant files, folders synthesised client-side". Pick one in phase 1; the first is the safer fit for lazy expand and `FolderRollupDepth`.
- **Ordering must match `GitChangesTreeBuilder`** (today's folders-first / name ordering). Ordinal path order is not identical to it, so state the comparer once and test it against the builder.

Replace the circuit-wide `GetContextAsync` full materialization with focused queries on `IWorkspaceGitChangesReadService`:

```text
GetContextSummariesAsync(workspaceId, contextId)
  ? repos + counts + folder summaries (no entry lists)

GetEntryPageAsync(workspaceId, contextId, workspaceRepositoryId, section, pathPrefix, skip, take, filter)
  ? page of WorkspaceGitChangeEntryView + totalCount for that prefix

SearchEntriesAsync(workspaceId, contextId, filter, skip, take)
  ? cross-repo hits (repo name + path + kinds) without loading non-matching paths
```

`WorkspaceGitChangesView.Changes` becomes either empty (large mode) or fully populated (small/fast path). Prefer a new summary DTO rather than overloading the existing view with null semantics long-term.

### 4.5 Tree UI: lazy nodes + virtualization (Layer C)

**Lazy expansion**

- Default for large mode: expand **sections** only; repositories show count badges (full counts); folders under a repo load children on expand. Collapsing is presentation, not omission - every path remains in Layer B and under the badge count.
- Optional "Expand all" stays available: it marks prefixes expanded and lets the virtualizer request pages until **every** file under those prefixes can be scrolled into view. It must never park 50K DOM nodes at once, and must never stop early with a truncated inventory.
- `GitChangesTreeBuilder` gains a mode that builds from a **partial** node cache (`TreeNodeCache`: prefix ? children pages, with `totalCount` so the UI knows when more pages exist) instead of requiring the full entry list in the circuit.

**Virtualization**

- Replace `@foreach` in `GitChangesTree.razor` with Blazor `Virtualize<GitChangesTreeRow>` (or an equivalent fixed-height window) over the flattened logical row list (or an `ItemsProvider` that pages from Layer B).
- Keep `@key="row.Key"` for stable identity.
- Row height should be CSS-stable so virtualization does not thrash.
- Scrolling to the end of a 20K-file expanded folder must eventually show file 20_000 - via successive provider fetches, not by cutting the list short.

**Expand state survives reload (Summary mode, implemented in Phase 1)**

F5 and Ctrl+F5 destroy the Blazor circuit, so `_collapsedKeys` alone loses the user's expansion and every repo re-collapses. In Summary mode the tree keeps whatever the user expanded or collapsed across a reload:

- Storage: browser `localStorage` (via `graymoonStorageGet`/`graymoonStorageSet`), key `graymoon.gitChanges.tree.{workspaceId}.{contextId}`. Client-side only; not SQLite, no Worker involvement, no inventory impact.
- Shape: JSON `{ c: [collapsed keys], k: [known keys] }`. `k` is every key whose default was already decided (repo section nodes plus `_summaryFoldersSeeded`). A key in `k` but not in `c` is one the user opened. Keys absent from `k` get today's defaults (new repos and newly seen folders open collapsed), so paths that appear after the save never come up expanded by accident.
- Save: after each expand/collapse toggle, and after a selection reveal (`EnsureAncestorsExpandedAsync`). Not saved in Full mode, where everything defaults to expanded.
- Restore: `LoadProjectionAsync` seeds the default collapsed repos, applies the stored state over them, then `HydrateRestoredTreeAsync` fetches the first page of each expanded prefix (`RehydrateExpandedAsync`) so the tree paints as the user left it. Pages beyond the first reload on scroll as usual. Because every `LoadAsync` (refresh, mutation) goes through this path, expand state also now survives those reloads.
- Stale keys (deleted folders) are harmless: they match no row. Context switch uses its own key.
- Never persisted: scroll offset, selection (separate memory), loaded pages.

**Why both lazy and virtualize?** Lazy expansion avoids building a 50K-row flat list before the user opens folders; virtualization avoids syncing 50K DOM nodes when a large prefix is expanded. Together they keep paint O(viewport) while the **logical** inventory stays complete and fully traversable.

### 4.6 Incremental updates / diffs (no full circuit reload)

Today: any `ContextGitChangesUpdated` ? `LoadAsync` ? full SQLite read.

Proposed:

1. Broadcast stays, but payload gains `repositoryId`, `contentDigest`, `stagedCount`, `changedCount` (counts already derivable; digest is new).
2. Page handler:
   - Update that repository's summary badges in place.
   - If digest unchanged ? no-op.
   - If digest changed and repo is **not** expanded / has no hydrated prefixes ? drop only that repo's cached pages.
   - If digest changed and prefixes are hydrated ? invalidate those prefixes and re-fetch the visible page(s) only.
3. Never re-query unrelated repositories' entries.

Worker pushes can eventually send **path deltas** (`added` / `removed` / `modified` entries vs previous digest) once Layer B supports apply-delta; phase 1 can keep full replace in SQLite while fixing the circuit reload shape (largest UX win per engineering cost).

### 4.7 Search / filter without full client inventory

- Small mode: keep `WorkspaceGitChangeSearchMatcher` in-process (today).
- Large mode: debounce input (cancel the in-flight query when a new keystroke arrives) ? `SearchEntriesAsync` (substring match needs `instr`/`LIKE` with escaped wildcards and an explicit case rule matching `WorkspaceGitChangeSearchMatcher`; `ext:` as suffix match, status filters via the `IndexChange`/`WorktreeChange` columns, `repo:` via join). Substring search cannot use the path index and is O(N) in SQLite; that is acceptable for 50K rows but should be measured in M2, with FTS5 as a later option. Reuse `FilterSearchExpression` parsing on the App; execute predicates in the read service, not in the circuit over a giant list.
- Results UI: flat hit list or tree-with-ancestors for the **current result page**, with clear total match count and continued paging / infinite scroll until **every** match is listed. Never "too many results - refine your filter" as a hard stop that hides matches.

Field tokens (`repo:`, `status:`, `staged:`, `ext:`) must keep working; map them to SQL predicates in one place next to the matcher so behavior stays aligned.

### 4.8 Worker ? App payload strategy

Note: the 10 MiB limit is the App hub's `MaximumReceiveMessageSize`. At roughly 100-200 bytes of JSON per entry, a single 50K-path repo is about 5-10 MiB, so it is near or over the limit once long paths are involved. Phase 0 must measure real bytes per entry (permanent `GitChangesBench` log lines in the Worker and App) before `SnapshotPathsInlineMax` is fixed (2_000 is a placeholder; consider a byte budget rather than a count).

Phase numbers below are *payload stages*, not the plan phases in section 7. Payload stage 1 = plan Phase 1-3, stage 2 = plan Phase 4 item 1, stage 3 = plan Phase 4 item 2.

**Payload stage 1 (App/UI scale, Worker snapshot shape unchanged):** keep full `Changes` on the wire. Still helps UX if circuit and tree are fixed; SignalR/SQLite remain heavy for mega-sets. Note that `ContentDigest` can be computed App-side in the push handler at this stage, so no Worker change is needed.

**Payload stage 2 (chunked path transfer when over `SnapshotPathsInlineMax`):**

```text
GitChangesSnapshotNotification
  SnapshotMetadata (counts, branch, flags, line stats, ContentDigest, PathCount)
  Changes            // first page inline when PathCount is large; otherwise full list
  PathsTransferId    // when chunked - App must pull until PathCount paths are stored

Follow-up commands (read pool):
  GetGitChangePathsPage { transferId | repoKey, version, skip, take }
```

Chunking exists so SignalR messages stay under the hub size limit. The App must not publish `ContextGitChangesUpdated` as complete until **PathCount** entries for that version are in SQLite (or the transfer fails and the version is abandoned). Partial UI may show a loading state; it must not present a shorter list as the final inventory.

Or: Worker writes path blobs to a short-lived local temp file and the App pulls pages via a new Worker command (still Worker-owned FS). Prefer SignalR paging first to avoid new file lifetime rules.

**Payload stage 3 (optional):** delta pushes from `GitStatusRefreshCoordinator` when the previous snapshot is still in `GitChangesSnapshotCache` - send only path diffs + new digest. App applies to Layer B inside the write queue. Deltas must still converge to the full path set.

Hard rule: version monotonicity and write-queue single-reader stay. Chunks for one version must commit atomically (buffer pages until complete, or write to a side table and swap).

### 4.9 Mutations, multi-select, commit, AI

Bulk operations must not depend on circuit inventory:

| Action | Large-mode rule |
|---|---|
| Stage/Unstage folder or repo | Already pathspec/scope based (`GitChangeOperationScope`) - keep; ensure scopes are built from **keys/prefixes**, not from enumerated `_view.Changes` |
| Stage selected | Selection set is explicit paths - OK for click/ctrl-select. **Shift-range and select-all-in-folder** span rows not yet materialised: represent them as a prefix/section scope (or resolve via a Layer B query), never as "the rows currently in the window" |
| Discard all changed | Repository / section scope on Worker - OK |
| Commit All / Commit Staged | Worker stages/commits by scope - OK; do not enumerate paths in the circuit |
| Copy all paths under folder | Query Layer B for that prefix (paged clipboard assembly or Worker-side path listing command) |
| AI `CollectGitEvidence` | Already Worker-side with budgets - unchanged; may still be slow on huge dirty trees (separate limit work) |
| Change walk next/prev | Operate on current result window or request next page of entries for the active section/filter |

Audit call sites that today do `_view.Repositories.SelectMany(r => r.Changes)` and replace with scoped queries or Worker scopes.

### 4.10 Memory and GC on Blazor Server circuits

- One circuit = one browser tab (existing rule). Large `_view` retained for the tab lifetime is the main leak-shaped cost today.
- After this design, retained state should be: summaries + `TreeNodeCache` for expanded prefixes + virtualize window + selection/diff.
- Invalidate caches on context switch and on digest change.
- Avoid building giant `List<GitChangesTreeRow>` for collapsed trees; build only the flattened visible slice the virtualizer needs (or a modest overscan).

### 4.11 What stays the same for small workspaces

When `totalEntries <= FullHydrationMaxEntries` (internal hydration switch only - see section 0):

1. `GetContextAsync` still returns full entry lists (or a single combined summaries+entries read).
2. Tree builder runs as today; default expand preserved.
3. Client-side filter matcher preserved.
4. Virtualize may still wrap the list (harmless for hundreds of rows) but must not add visible latency or empty-flash.

Acceptance: a Workspace with =500 changed files feels as snappy as today on open, filter, expand, stage, and commit. Larger sets use the progressive path but still expose every file.

## 5. Compatibility and migration

- **DB:** additive tables/columns + indexes via numbered `Migrate*Async` steps; nullable/defaults only. Old App builds ignore new columns; new App tolerates missing folder summaries (compute on read once, or treat as "no rollup until next snapshot").
- **Worker:** phase 1 needs no Worker change. Phase 2+ ships App and Worker together (version lock). No dual-protocol forever.
- **Legacy dual-write:** folder summaries only required on context tables; legacy tables can keep entry replace only until dual-write is removed.
- **Feature contexts:** all APIs take `WorkspaceFeatureContextId`; same as today.
- **Offline Worker:** summaries + whatever entries are already in SQLite still render; chunked transfer unavailable until Worker reconnects (same as today's offline banner).

## 6. Risks

| Risk | Mitigation |
|---|---|
| Two modes (small/large) diverge in bugs | One tree component; mode only changes data source. Shared row model and keys. |
| Expand-all expectations | Keep action; virtualize + page until every file under expanded prefixes is scrollable. Show true total counts always. Never imply a max file count. |
| Accidental inventory caps | Code review / tests forbid "take first N and stop", truncated persist, and "too many files" hard-stop UX. |
| Search SQL vs client matcher drift | Single parser; shared tests for token ? predicate. |
| Chunked snapshot torn writes | Buffer by version in write queue; swap atomically; reject incomplete versions. |
| Write-queue backlog during mega replace | Phase 2 chunking + optional delta apply; measure queue lag under watcher storms. |
| Virtualize + variable row height | Fix row height; avoid multi-line labels in the row chrome. |
| Selection restored across digest changes | Existing stale-selection clear; extend to prefix cache invalidation. |
| Page cache stale versus a newer snapshot | Tag pages with snapshot version; discard on mismatch; keyset paging (section 4.4). |
| Path in both Staged and Changed sections double-counted | Counts and summaries are per section; `PathCount` is per distinct path (section 4.4). |
| Underestimation of `git status` itself | UI scale still wins; the permanent Worker `GitChangesBench` log line records `StatusMs` separately from App-side costs. |

## 7. Phased implementation plan

### Phase 0 - Measure and log permanently (0.5-1 day)

Benchmarks are **permanent structured log lines in both the App and the Worker**, not temporary instrumentation. They stay after this work ships so regressions and real-world mega-trees are visible in normal logs. Rules:

- Use the normal `ILogger` pipeline (Serilog sinks already in use), one line per event, named properties (not interpolated text) so they can be filtered and aggregated. Prefix the message with `GitChangesBench` for easy search.
- Log at `Information` only for events above a cheap threshold (for example entry count or elapsed time over a configurable floor in `GitChangesOptions`); log everything at `Debug`. Small workspaces must not flood the log or pay measurable cost (use `Stopwatch` and byte counts already at hand; no extra serialization just to measure).
- Never log path names or file contents, only counts, durations and byte sizes.
- Include correlation fields: `WorkspaceId`, `ContextId`, `RepositoryId` (or repo key on the Worker), and snapshot `Version`.

**Worker** (`GrayMoon.Worker`):

| Event | Properties |
|---|---|
| `git status` scan complete (`GitCliRepositoryGitChangesService.GetStatusAsync`) | `StatusMs`, `PathCount`, `StagedCount`, `ChangedCount`, `PorcelainBytes` |
| Snapshot built and published (`GitChangesSnapshotPublisher`) | `BuildMs`, `SerializeMs`, `PayloadUtf8Bytes`, `BytesPerEntry`, `PathCount`, `Chunked`, `ChunkCount` (phase 4) |
| Send result | `SendMs`, success or hub-size rejection |

**App** (`GrayMoon.App`):

| Event | Properties |
|---|---|
| Snapshot received and applied (`GitChangesSnapshotPushHandler`) | `ReceiveUtf8Bytes`, `WriteQueueWaitMs`, `PersistMs`, `PathCount`, `Version`, whether skipped as stale |
| Changes page open (`LoadAsync` / summaries read) | `EntryCount`, `ReadMs`, `BuildTreeMs`, `CircuitPaintMs` (StateHasChanged ? next `OnAfterRender`), `RowCountRendered`, `HydrationMode` (Full or Summary), `OpenMs` |
| Page fetch (`GetEntryPageAsync`, phase 1) | `Prefix` depth only, `Take`, `ReturnedCount`, `QueryMs` |
| Search (`SearchEntriesAsync`, phase 2) | `FilterLength`, `MatchCount`, `QueryMs` |
| Live update handled | `Reloaded` vs `PatchedOnly` vs `NoOp`, `HandleMs`, `ReadMs`, `BuildTreeMs`, `CircuitPaintMs` |
| Circuit heap on open | `GC.GetTotalMemory(false)` before and after open (Debug only; it is not per-circuit exact, so treat as a trend) |

Tasks:

1. Add the log lines above on the existing code paths (App and Worker), with the option floors in `GitChangesOptions`.
2. Build a synthetic fixture Workspace (scripted dirty tree at 100, 2K, 5K, 25K, 50K paths) and confirm the ~5K pain from the logs.
3. Record the numbers in this doc and use them to set `SnapshotPathsInlineMax` (preferably as a byte budget), `FullHydrationMaxEntries` and the other knobs.

**Milestone M0:** benchmark log lines merged in App and Worker; numbers attached to this doc; fixture reproducible. The log lines are kept (not removed) after M0 and are reused to verify every later milestone.

**M0 results (2026-10-24):** see the table in `git-changes-large-changeset-scale-tracker.md`. Fixture: `dev/New-GitChangesScaleFixture.ps1`. App harness: `GitChangesBenchM0MeasurementTests`. Takeaways: ~145 UTF-8 bytes per short path on the notification JSON; 50K ˜ 8.9 MiB (under the 10 MiB hub receive cap with little headroom); Worker `StatusMs` stays small; App `PersistMs` and full-circuit row count dominate by ~5K paths. Prefer a **byte budget** for `SnapshotPathsInlineMax` (still an open decision) rather than a fixed path count.

### Phase 1 - Circuit and tree (App-only) - highest UX leverage

1. Split read API: summaries vs paged entries (entries table unchanged).
2. Large-mode open path: summaries only; default collapse repos/folders when over threshold.
3. Lazy load children on expand (`GetEntryPageAsync`).
4. `Virtualize` the flattened row list.
5. Live updates: per-repo summary patch + cache invalidation (stop full `LoadAsync`).
6. Keep small-mode full hydration.
7. Summary-mode expand state persists across F5 / Ctrl+F5 (section 4.5).

**Milestone M1:** 20K-path fixture opens with responsive chrome; scrolling/expand reaches all 20K files; small Workspace unchanged.

### Phase 2 - Search and bulk path consumers

Status: implemented (M2 manual check on the 20K fixture pending). Search streams entries in `(repo, path)` keyset order and applies the compiled `WorkspaceGitChangeSearchMatcher` predicate, so it matches the in-circuit filter exactly; SQL pushdown and FTS5 stay optional.

1. Server-side `SearchEntriesAsync` for large mode; debounce from the page; page until all matches listed.
2. Audit/replace `_view` full-enumeration call sites (copy-all, change walk, any stats).
3. Indexes migration for Path queries.

**Milestone M2:** filtering a 20K set does not spike circuit memory; every match is reachable via result paging; copy/walk work.

### Phase 3 - Digest and rollups (split)

Phase 3 is split in two. Only 3a is scheduled; 3b is postponed.

#### Phase 3a - Content digest (scheduled, after Phase 2)

1. `ContentDigest` and `PathCount` computed App-side in `GitChangesSnapshotPushHandler` and stored on `WorkspaceGitContextRepositoryStatus` (nullable column, additive migration). No Worker change.
2. `ContextGitChangesUpdated` broadcast carries `repositoryId` and `contentDigest`, so an unchanged digest is a no-op in the page handler (section 4.6).
3. Tests: digest stability (same entries in any order give the same digest), and a no-op when the digest is unchanged.

**Milestone M3a:** a watcher push with identical content causes no circuit reload or page re-fetch. The `GitChangesBench` live-update line reports `NoOp`.

#### Phase 3b - Folder summaries table (postponed)

1. `WorkspaceGitContextFolderSummaries` written in `GitChangesSnapshotPushHandler`.
2. Open path uses persisted rollups (no on-the-fly grouping of all entries).

Start 3b only if the `GitChangesBench` open-time logs, after Phase 2, still show open cost growing with path count.

**Milestone M3b:** open cost independent of path count for collapsed tree.

### Phase 4 - Worker payload chunking / deltas

1. Chunked path transfer for huge snapshots (every path still persisted before the version commits).
2. Optional delta apply when cache hit on Worker.
3. Extend the permanent benchmark log lines (Phase 0) with chunk counts and per-chunk sizes, and check payload sizes; ensure under SignalR message size limits with margin (transport limit only).

**Milestone M4:** 50K-path repo snapshot cannot wedge the hub or write queue; all 50K paths land in SQLite and are reachable in the UI; App and Worker ship together.

### Phase 5 - Polish

- Expand-all strategy, empty/error states, tests (`GitChangesTreeBuilder` large-mode, read service paging, handler digest).
- Fold completed behavior into `07-git-changes-projection-and-tree.md` and remove the known-gap bullets in 04/06.
- Desktop README Recent changes bullet only when user-visible behavior ships (not for this design-only task).

## 8. Concrete implementation sequence (engineering order)

1. Options + threshold helpers.
2. `IWorkspaceGitChangesReadService` summary/page/search methods + EF queries + indexes migration.
3. Page state machine: `HydrationMode`, `TreeNodeCache`, replace blanket `LoadAsync` on hub events.
4. `GitChangesTreeBuilder` partial-cache build; default collapse in large mode.
5. `GitChangesTree.razor` ? `Virtualize`.
6. Wire expand handlers to page fetches; loading placeholders on folder rows.
7. Server search path + UI debounce.
8. Call-site audit for full entry enumeration.
9. Digest + folder summary persistence.
10. Worker chunked transfer + atomic persist.
11. Tests and architecture doc fold-in.

Do not start Worker chunking before M1: most user pain is circuit/DOM, fixable in App alone.

## 9. Test plan (when implementing)

- Unit: tree builder large-mode pages; search SQL predicates vs matcher samples; digest stability.
- App integration: fixture Workspace with 0, 100, 2_000, 25_000 synthetic entries in SQLite (no Worker) - open, expand, scroll, filter.
- Worker: snapshot notify with > inline max pages; version gate; cancel mid-transfer.
- Regression: existing `GitChangesTreeBuilderTests`, directory-entry tests, activation/snapshot tests.
- Manual: small dirty repo feels identical; mega dirty tree opens without "GrayMoon needs to reload"; watcher updates do not freeze the tab.

## 10. Success criteria

- Context with =20K paths: Changes route interactive (section/repo chrome + **accurate** counts) without loading 20K strings into the circuit at once.
- **All 20K paths remain reachable:** expand folders and scroll (or search/page) until every path can be selected; no truncation, no "first N", no refine-filter wall.
- Scroll/expand remain usable; no multi-second UI freezes on watcher updates for a single repo.
- Context with =500 paths: no extra round trips beyond today's single read (or one equivalent batched read); expand-all still immediate (fast path only).
- SignalR status pushes for a 50K-path repo stay within message size limits via chunking (phase 4) without dropping the Worker connection, and the committed snapshot contains all 50K paths.

## 11. References

- Current path: `docs/architecture/07-git-changes-projection-and-tree.md`
- Watchers / write queue: `docs/architecture/04-runtime-communication-and-concurrency.md` sections 9-13
- Persistence: `docs/architecture/03-data-model-and-persistence.md` section 13
- Page capabilities: `docs/architecture/05-user-capability-reference.md` sections 21-22
- Code anchors: `WorkspaceGitChangesReadService`, `GitChangesTreeBuilder`, `GitChangesTree.razor`, `GitChangesSnapshotPushHandler`, `GitCliRepositoryGitChangesService.GetStatusAsync`, `GitChangesSnapshotPublisher`
