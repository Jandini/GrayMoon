# Git Changes scale: remaining follow-ups

Status: **closed for now.** Work stopped after Phase 3a. Everything built (summary-first open, lazy paged expand, `Virtualize`, per-repo live patch, server-side search, path indexes, `ContentDigest` no-op) is documented as current behavior in `docs/architecture/07-git-changes-projection-and-tree.md`, with data model notes in `03-data-model-and-persistence.md`. This file keeps only what was not built, so it can be picked up later. Nothing here is scheduled.

## Guardrails for any follow-up

- **No inventory cap or truncation.** Every changed path stays persisted and reachable by scroll, expand or search. Paging, virtualization and chunked transfer are transport and paint windows only. Forbidden: "first N", "too many files, refine your filter", skipping paths to save work.
- Small workspaces (at or below `FullHydrationMaxEntries`, 1_500) keep the single-read Full path with no extra round trips.
- App never runs git. Snapshot versioning (latest wins), write-queue serialization and Feature context scoping stay intact.
- App and Worker ship together (version lock); no capability flags.
- Never log path names or file contents.

## Reference numbers (2026-10-24)

Windows laptop, in-memory SQLite, real `git status` on synthetic untracked trees. The benchmark code and fixture script were removed afterwards, so re-create a fixture if you need new numbers.

| Paths | Snapshot bytes | `StatusMs` | `PersistMs` | `ReadMs` | `BuildTreeMs` |
|---|---|---|---|---|---|
| 100 | 18_172 | 39 | 4 | 1 | 0 |
| 2K | 356_372 | 41 | 84 | 8 | 2 |
| 5K | 890_372 | 46 | 254 | 14 | 5 |
| 25K | 4_475_372 | 86 | 1_201 | 62 | 54 |
| 50K | 8_950_372 | 120 | 3_613 | 151 | 137 |

About 145 UTF-8 bytes per short path on the wire. 50K paths is about 8.9 MiB against the 10 MiB hub receive cap (`MaximumReceiveMessageSize` in `Program.cs`), so very large or long-path snapshots can be rejected today. Prefer a byte budget over a path count for any inline limit.

## Open items

### Manual check (was M2)

- On a 20K-path fixture: filter memory trend, and scroll to the last search match.

### Phase 3b - persisted folder summaries

Only start if open time after Phase 2 still grows with path count.

- `WorkspaceGitContextFolderSummaries` (context, repo, section, folder prefix, depth, file count, conflict count) via a numbered migration, written in `GitChangesSnapshotPushHandler` from the same entry set.
- Open path reads persisted rollups; tolerate missing rollups on old data.
- Note: sections are derived (a path can be in both Staged and Changed), so per-section summaries do not sum to `PathCount`.

### Phase 4 - Worker to App payload chunking and deltas

Only needed if real users hit the 10 MiB cap or write-queue stalls at 50K+ paths.

- `GitChangesSnapshotNotification` metadata (counts, digest, `PathCount`) plus a first inline page and a `PathsTransferId`; new read-pool command `GetGitChangePathsPage`; `PathsTransferPending` on repository status.
- Atomic commit per version (buffer, or side table and swap); reject incomplete versions; no `ContextGitChangesUpdated` until `PathCount` entries are stored; handle cancel and failure mid-transfer.
- Optional: delta pushes when `GitChangesSnapshotCache` holds the previous snapshot; App applies in the write queue; deltas must converge to the full set.
- Open decisions: `SnapshotPathsInlineMax` as a byte budget (preferred) or a count; compute the digest App-side (today) or Worker-side.

### Phase 5 - leftover polish

- Tests: tree builder large mode, read-service paging under a concurrent update, token-to-predicate parity, guard tests against "first N and stop" UX.
- Integration fixtures with 0, 100, 2_000 and 25_000 entries (open, expand, scroll, filter); Worker tests for over-inline-max pages, version gate and cancel mid-transfer (only if Phase 4 is built).
- Optional: FTS5 or SQL pushdown for search if O(N) streaming (about 0.5 s for 5K matches of 20K paths) proves too slow.

### Dropped or changed from the original design

- `FolderRollupDepth` knob: not needed without 3b.
- Search is a streamed scan with the compiled in-process predicate, not SQL pushdown, so results always equal the in-circuit filter.
