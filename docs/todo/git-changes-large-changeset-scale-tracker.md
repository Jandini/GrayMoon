# Git Changes large changeset scale - tracker

Design: `docs/todo/git-changes-large-changeset-scale.md` (section numbers below refer to it). Update this file as work lands; fold finished behavior into `docs/architecture/07-git-changes-projection-and-tree.md` (Phase 5).

Status key: `[ ]` not started, `[~]` in progress, `[x]` done, `[-]` dropped (say why).

Standing rules for every item: no cap or truncation of the inventory (section 0); small workspaces must not regress (section 4.11); App and Worker ship together; benchmark `GitChangesBench` log lines are permanent.

## Summary

| Phase | Scope | Milestone | Status |
|---|---|---|---|
| 0 | Permanent benchmark logs (App + Worker), fixture | M0 | [ ] |
| 1 | Circuit and tree: summaries, lazy expand, `Virtualize`, live-update patching | M1 | [ ] |
| 2 | Server-side search, bulk-path call sites, indexes | M2 | [ ] |
| 3 | `ContentDigest` and folder summaries persisted | M3 | [ ] |
| 4 | Worker payload chunking and deltas | M4 | [ ] |
| 5 | Polish, tests, architecture fold-in | - | [ ] |

## Open decisions

- [ ] Folder page unit: direct children grouped by next path segment, or descendant files with client-side folders (section 4.4).
- [ ] Comparer that matches `GitChangesTreeBuilder` ordering (section 4.4).
- [ ] `SnapshotPathsInlineMax` as a count or a byte budget (decide from M0 numbers).
- [ ] Digest computed App-side (stage 1) vs Worker-side (later).
- [ ] Final values for `FullHydrationMaxEntries`, `TreePageSize`, `FolderRollupDepth`, `CircuitVisibleRowBudget` (from M0 numbers).

## Phase 0 - Measure and log permanently

- [ ] Benchmark floors in `GitChangesOptions` (Information threshold; Debug logs everything)
- [ ] Worker: `git status` scan log (`StatusMs`, counts, `PorcelainBytes`)
- [ ] Worker: snapshot build/serialize/publish log (`PayloadUtf8Bytes`, `BytesPerEntry`)
- [ ] Worker: send result log (including hub-size rejection)
- [ ] App: snapshot receive/apply log (`ReceiveUtf8Bytes`, `DeserializeMs`, `WriteQueueWaitMs`, `PersistMs`)
- [ ] App: Changes open log (`EntryCount`, `ReadMs`, `BuildTreeMs`, `RowCountRendered`, `HydrationMode`)
- [ ] App: live-update handling log (`Reloaded` / `PatchedOnly` / `NoOp`)
- [ ] App: heap trend on open (Debug)
- [ ] Confirm no path names or contents are logged
- [ ] Synthetic fixture Workspace at 100, 2K, 5K, 25K, 50K paths (scripted)
- [ ] Numbers recorded in the design doc
- [ ] **M0:** log lines merged, numbers attached, fixture reproducible

## Phase 1 - Circuit and tree (App-only)

- [ ] Options and threshold helpers (`FullHydrationMaxEntries`, `TreePageSize`, ...)
- [ ] `GetContextSummariesAsync` (no entry lists) on `IWorkspaceGitChangesReadService`
- [ ] `GetEntryPageAsync` with keyset paging and snapshot version tag
- [ ] Indexes migration (replace `(ctx, repo)` index; add `(ctx, repo, Path)` and `(ctx, Path)`)
- [ ] Summary DTO (not overloading `WorkspaceGitChangesView` with null semantics)
- [ ] `HydrationMode` + `TreeNodeCache` in the page state machine
- [ ] `GitChangesTreeBuilder` partial-cache build; default collapse in large mode
- [ ] `GitChangesTree.razor` uses `Virtualize` with fixed row height and `@key`
- [ ] Expand handlers fetch pages; loading placeholders on folder rows
- [ ] Live updates: per-repo badge patch and cache invalidation (no blanket `LoadAsync`)
- [ ] Small-mode fast path unchanged (no extra round trip, no empty flash)
- [ ] Expand all: pages until every file under expanded prefixes is scrollable
- [ ] **M1:** 20K fixture opens responsive; every file reachable; small workspace unchanged

## Phase 2 - Search and bulk path consumers

- [ ] `SearchEntriesAsync` with token-to-predicate mapping next to `WorkspaceGitChangeSearchMatcher`
- [ ] Debounce and cancel in-flight query in the page
- [ ] Result paging until every match is listed; total match count shown
- [ ] Call-site audit of `_view.Repositories.SelectMany(r => r.Changes)` and replacements
- [ ] Copy all paths under a folder (Layer B query)
- [ ] Change walk next/prev across pages
- [ ] Shift-range and select-all-in-folder as prefix/section scopes
- [ ] Search substring cost measured (FTS5 only if needed)
- [ ] **M2:** filtering 20K does not spike circuit memory; all matches reachable

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

| Paths | Snapshot bytes | `StatusMs` | `PersistMs` | `ReadMs` | `BuildTreeMs` | Rows rendered |
|---|---|---|---|---|---|---|
| 100 | | | | | | |
| 2K | | | | | | |
| 5K | | | | | | |
| 25K | | | | | | |
| 50K | | | | | | |

## Log

| Date | Change |
|---|---|
| 2026-10-24 | Tracker created; design reviewed and corrected (indexes, query rules, payload stages, permanent benchmark logging). |
