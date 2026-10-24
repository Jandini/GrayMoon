# Git Changes large changeset scale - tracker

Design: `docs/todo/git-changes-large-changeset-scale.md` (section numbers below refer to it). Update this file as work lands; fold finished behavior into `docs/architecture/07-git-changes-projection-and-tree.md` (Phase 5).

Status key: `[ ]` not started, `[~]` in progress, `[x]` done, `[-]` dropped (say why).

Standing rules for every item: no cap or truncation of the inventory (section 0); small workspaces must not regress (section 4.11); App and Worker ship together; benchmark `GitChangesBench` log lines are permanent.

## Summary

| Phase | Scope | Milestone | Status |
|---|---|---|---|
| 0 | Permanent benchmark logs (App + Worker), fixture | M0 | [x] |
| 1 | Circuit and tree: summaries, lazy expand, `Virtualize`, live-update patching | M1 | [x] |
| 2 | Server-side search, bulk-path call sites, indexes | M2 | [~] |
| 3 | `ContentDigest` and folder summaries persisted | M3 | [ ] |
| 4 | Worker payload chunking and deltas | M4 | [ ] |
| 5 | Polish, tests, architecture fold-in | - | [ ] |

## Open decisions

- [x] Folder page unit: **direct children** grouped by next path segment (`GroupDirectChildren` / `GetEntryPageAsync`).
- [x] Comparer that matches `GitChangesTreeBuilder` ordering: folders (incl. directory entries) first, then files; `OrdinalIgnoreCase` by name.
- [ ] `SnapshotPathsInlineMax` as a count or a byte budget (M0 favors byte budget; ~145 B/entry short paths, 50K � 8.9 MiB vs 10 MiB hub cap).
- [ ] Digest computed App-side (stage 1) vs Worker-side (later).
- [x] Knob defaults from M0: `FullHydrationMaxEntries` = 1_500, `TreePageSize` = 200, `FolderRollupDepth` = 2, `CircuitVisibleRowBudget` = 500 (still tunable).

## Phase 0 - Measure and log permanently

- [x] Benchmark floors in `GitChangesOptions` (Information threshold; Debug logs everything)
- [x] Worker: `git status` scan log (`StatusMs`, counts, `PorcelainBytes`)
- [x] Worker: snapshot build/serialize/publish log (`PayloadUtf8Bytes`, `BytesPerEntry`)
- [x] Worker: send result log (including hub-size rejection)
- [x] App: snapshot receive/apply log (`ReceiveUtf8Bytes`, `WriteQueueWaitMs`, `PersistMs`)
- [x] App: Changes open log (`EntryCount`, `ReadMs`, `BuildTreeMs`, `CircuitPaintMs`, `RowCountRendered`, `HydrationMode`)
- [x] App: live-update handling log (`Reloaded` / `PatchedOnly` / `NoOp`, with `CircuitPaintMs` on reload)
- [x] App: heap trend on open (Debug)
- [x] Confirm no path names or contents are logged
- [x] Synthetic fixture Workspace at 100, 2K, 5K, 25K, 50K paths (scripted)
- [x] Numbers recorded in the design doc
- [x] **M0:** log lines merged, numbers attached, fixture reproducible

## Phase 1 - Circuit and tree (App-only)

- [x] Options and threshold helpers (`FullHydrationMaxEntries`, `TreePageSize`, ...)
- [x] `GetContextSummariesAsync` (no entry lists) on `IWorkspaceGitChangesReadService`
- [x] `GetEntryPageAsync` with keyset paging and snapshot version tag
- [x] Indexes migration (replace `(ctx, repo)` index; add `(ctx, repo, Path)` and `(ctx, Path)`)
- [x] Summary DTO (not overloading `WorkspaceGitChangesView` with null semantics)
- [x] `HydrationMode` + `TreeNodeCache` in the page state machine
- [x] `GitChangesTreeBuilder` partial-cache build; default collapse in large mode
- [x] `GitChangesTree.razor` uses `Virtualize` with fixed row height and `@key`
- [x] Expand handlers fetch pages; loading placeholders on folder rows
- [x] Live updates: per-repo badge patch and cache invalidation (no blanket `LoadAsync`)
- [x] Small-mode fast path unchanged (no extra round trip, no empty flash)
- [x] Expand of a prefix: pages until every direct child under that prefix is scrollable (nested folders still expand on demand)
- [x] **M1:** summary open + virtualize + per-repo live patch; small workspace unchanged; filter on large inventories falls back to full hydration until Phase 2

## Phase 2 - Search and bulk path consumers

- [x] `SearchEntriesAsync`: keyset-paged (repo id, path), streams entries in index order and drops non-matches; evaluates the same compiled predicate as `WorkspaceGitChangeSearchMatcher` (`Compile`), so results equal the in-circuit filter. SQL pushdown / FTS5 not needed yet
- [x] Debounce and cancel in-flight query in the page (`ScheduleSearch`; each keystroke cancels the previous timer and query)
- [x] Result paging until every match is listed; total match count shown ("N matches") above the tree
- [x] Filter on a large inventory stays in Summary mode as a search overlay (no more forced full hydration); live updates re-run the search to the user's loaded depth
- [x] Call-site audit of `_view.Repositories.SelectMany(r => r.Changes)`: copy-all and selection checks already use Layer B (Phase 1); change walk now collects its list with paged search instead of `GetContextAsync`
- [x] Copy all paths under a folder (Layer B query, `GetEntryPathsAsync`)
- [x] Change walk next/prev across pages (paged read, list held weakly)
- [x] Shift-range and select-all-in-folder: reviewed. Shift-range covers only rows between anchor and target, which are materialised rows; folder/section actions already use prefix/section scopes. No change needed
- [x] Search substring cost measured: 20K-path inventory, 5K matches, all 25 pages of 200 in about 0.5 s total (`GitChangesEntryPageReadServiceTests`); `GitChangesBench` "search page" line logs `ScannedCount` and `QueryMs`
- [ ] **M2:** automated parts done (all matches reachable, circuit holds only loaded hits). Manual check on the 20K fixture still to do: filter memory trend and scroll to the last match
## Phase 3 - Persist rollups and digest

- [ ] `ContentDigest` and `PathsTransferPending` on repository status
- [ ] `WorkspaceGitContextFolderSummaries` table (per section) via numbered migration
- [ ] Written in `GitChangesSnapshotPushHandler` from the same entry set
- [ ] Open path uses persisted rollups; tolerate missing rollups on old data
- [ ] Broadcast carries `repositoryId`, `contentDigest`, counts; no-op on unchanged digest
- [ ] **M3:** open cost independent of path count for a collapsed tree

## Phase 4 - Worker payload chunking and deltas

- [ ] `GitChangesSnapshotNotification` metadata + `PathsTransferId`
- [ ] `GetGitChangePathsPage` read-pool command
- [ ] Atomic commit per version (buffer or side table swap); reject incomplete versions
- [ ] No `ContextGitChangesUpdated` until `PathCount` entries are stored
- [ ] Cancel and failure handling mid-transfer
- [ ] Optional: delta pushes when `GitChangesSnapshotCache` hit; App applies in write queue
- [ ] Benchmark logs extended with chunk counts and sizes
- [ ] **M4:** 50K-path snapshot lands fully; hub and write queue not wedged

## Phase 5 - Polish

- [ ] Tests: tree builder large mode, read service paging, handler digest, token-to-predicate parity, keyset paging under concurrent update
- [ ] Integration: fixtures with 0, 100, 2_000, 25_000 entries (open, expand, scroll, filter)
- [ ] Worker tests: over-inline-max pages, version gate, cancel mid-transfer
- [ ] Guard tests against "first N and stop" / "too many files" UX
- [ ] Fold behavior into `07-git-changes-projection-and-tree.md`; remove known-gap bullets in 04 and 06
- [ ] Desktop README Recent changes bullet when user-visible behavior ships

## Benchmark results (fill from M0)

Measured 2026-10-24 on a Windows laptop (in-memory SQLite for App persist/read/tree; real `git status` on synthetic untracked trees under `%TEMP%\graymoon-git-changes-scale`). Snapshot bytes are camelCase JSON size of a full `GitChangesSnapshotNotification`. Rows rendered include section/repo/folder chrome (not path count alone). Fixture: `dev/New-GitChangesScaleFixture.ps1`. App harness: `GitChangesBenchM0MeasurementTests`.

| Paths | Snapshot bytes | `StatusMs` | `PersistMs` | `ReadMs` | `BuildTreeMs` | Rows rendered |
|---|---|---|---|---|---|---|
| 100 | 18_172 | 39 | 4 | 1 | 0 | 203 |
| 2K | 356_372 | 41 | 84 | 8 | 2 | 2_103 |
| 5K | 890_372 | 46 | 254 | 14 | 5 | 5_103 |
| 25K | 4_475_372 | 86 | 1_201 | 62 | 54 | 25_103 |
| 50K | 8_950_372 | 120 | 3_613 | 151 | 137 | 50_103 |

Notes from M0: ~145 UTF-8 bytes/entry on the wire for short paths; 50K is ~8.9 MiB (under the 10 MiB hub receive cap, thin margin). `git status` itself stays modest; PersistMs and full-circuit row count dominate by 5K+. Prefer a **byte budget** for `SnapshotPathsInlineMax` (open decision) over a raw count.

## Log

| Date | Change |
|---|---|
| 2026-10-24 | Phase 1 done: summary-first open above `FullHydrationMaxEntries`, `GetEntryPageAsync` + `TreeNodeCache`, `Virtualize`, per-repo live patch (`PatchedOnly`), path indexes migration 13. |
| 2026-10-24 | App open/live-update benches include `CircuitPaintMs` (tree StateHasChanged ? OnAfterRender) so felt UI lag is visible in logs. |
| 2026-10-24 | Phase 0 done: permanent `GitChangesBench` logs (App+Worker), floors on `GitChangesOptions`, fixture script, M0 numbers recorded. |
| 2026-10-24 | Tracker created; design reviewed and corrected (indexes, query rules, payload stages, permanent benchmark logging). |
