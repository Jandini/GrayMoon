# Changes page: commit timeline ("time machine") for reviewing a feature commit by commit

Status: **idea, design only.** Nothing is built. First written 2026-11-11, revised the same day after review (scale, persistence, scope), then reviewed again with laptop measurements (see "Review findings"). The enterprise VDI measurement is in (see "Measured on the enterprise VDI"); the design text below is not yet revised from it. Written against `07-git-changes-projection-and-tree.md` and the current Changes page (`WorkspaceGitChanges*.razor/.cs`, `GitChangesTree.razor`, `GitChangesHeader`).

## Review findings (2026-11-11, second review, with measurements)

The git commands this design depends on were measured on a developer laptop and on the enterprise VDI (numbers in "Performance reality and why we persist"). **Where a finding below conflicts with the older text further down, the finding wins.** The VDI numbers are recorded and the rest of the note is not yet revised from them, because those numbers may still change the targets.

Decided:

- **The three SQLite tables stay in v1.** An in-memory cache was considered and rejected: persistence keeps the "Changes page reads SQLite only" rule, and on a machine where every git process is slow, a refill after each App restart is exactly the cost to avoid.

Findings that change the design:

| # | Finding | Evidence | Change |
|---|---|---|---|
| 1 | Process start dominates everything except line counts | `git --version` takes 76 ms; a metadata `log` of 181 commits takes 78 ms | Count **processes**, not commits. Every design choice below minimizes the number of git processes per repository |
| 2 | Changed-file lists for every commit are nearly free inside the list call | `log --name-status` for 181 commits is 90 ms, 12 ms more than metadata only. A separate `diff-tree` per clicked commit is 76 ms plus the hub round trip | `ListBranchCommits` returns the file lists (`--name-status`, with `-M`) and the hydrator fills `WorkspaceGitContextCommitFiles` for single commits **eagerly**. A dot click is then a pure SQLite read. `GetCommitChanges` is needed only for ranges and the combined view. Hover prefetch of file lists is no longer needed |
| 3 | Incremental mode costs more than a full list | `merge-base --is-ancestor` (74 ms) plus `log known..tip` (76 ms) is two processes, 150 ms; the full list is one process, 78-90 ms | **Drop incremental mode.** Always list the full range and reuse rows by sha in the App. Removes `KnownTipSha`, the `Mode` field and the incremental-versus-full tests. The "under 100 ms incremental" target is replaced by "one process per HEAD change" |
| 4 | Parallelism buys little | 48 list calls: 4.2 s at 1 process, 4.0 s at 4, 2.5 s at 8, 1.8 s at 16. Process creation looks close to serialized (antivirus) | A cold backfill of 100 repositories is about 5-8 s here, not "a few seconds". Still fine in the background. Do not expect a higher parallelism cap to rescue a slow machine |
| 5 | Line counts are the only expensive part | `--numstat` for 181 commits: 351 ms in the list call, 373 ms as one `diff-tree --stdin` process | Unchanged: tier 2 stays a separate low-priority pass |
| 6 | Each side of a commit diff is its own process | `git show <sha>:<path>` is 73 ms, so a diff is about 150 ms | Read both sides with one `git cat-file --batch` process |
| 7 | **The ahead badge does not measure what this note assumes** | `GitCliRepositoryReader.GetCommitCountsVsDefaultAsync` counts `<default ref>...HEAD`: the tip is `HEAD` and the base is the default ref. This note uses the Feature branch as tip and `ParentBranchName` as base, and calls "count equals badge" an integrity check | The check would fail for a stacked feature and for a worktree flipped to another branch. Choose the timeline's own range and **drop the badge equality check** (and `ExpectedCount`), or change the badge first. Still to confirm: which default ref the App passes for a Feature repository |
| 8 | Merge commits would show no files | `git diff-tree <merge>` and `git log --name-status` print nothing for a merge by default | Diff a merge explicitly against its first parent (`<sha>^1 <sha>`, or `--first-parent -m` in the list call), with a test |
| 9 | Purge rules contradict each other | "Key and lifetime" purges rows when the checked-out branch changes; the state table says rows are retained so flipping back does not recompute | Pick one (open question 1c). Suggested: retain until the branch is deleted or the Feature is removed |
| 10 | The merged cross-repository order is fragile | A rebase gives every commit of that repository nearly the same committer date, so they clump, and a date-ordered walk swaps the tree between repositories on every step | Suggested default: group by repository (open question 0a) |
| 11 | The walk changes today's Working tree behaviour | Stepping past the last working change would wrap into commit 1 | Suggested: the Working tree keeps wrapping inside itself and is left only by dot, `[` / `]` or `Shift` (open question 0) |

Suggested, not decided - a smaller first version: a single-repository strip (the scope picker chooses the repository), the commit list, one-commit selection, the per-repository combined view and the two-button walk. The merged all-repositories timeline, bubbles, lanes, zoom, the file-progress arc, the crossing pulse and dot sizing would wait until someone asks for them.

## Problem

The Changes page shows only the **working tree** (Staged / Changed). To review a Feature before a PR the user has to leave GrayMoon. We want the page to also show **what each commit changed, as it was when it was committed**, for every commit of the Feature that is not yet on its parent branch (the commits the "ahead" badge in Workspace Repositories counts), pushed or not.

In GrayMoon "the commits of a Feature" always means **commits across many repositories**. Design for that from the start: one repository with 181 commits ahead is normal, and a Feature can span tens or hundreds of repositories.

Goals:

1. A compact **timeline of dots** plus a node for the **current changes** (working tree), matching the GM dark theme, that stays usable at 181 commits in one repository and thousands across repositories.
2. Selecting a commit shows its files in the existing tree and its diff in the existing diff pane. No commit box, no Stage / Undo / Discard in that mode, so the tree takes the whole left column.
3. Costs as little screen height as possible.
4. **Opens instantly.** The page never runs `git log` while the user waits; history is persisted per Feature branch and kept current in the background.

Non-goals (v1): editing history, cherry-pick, revert, checkout of an old commit, blame, merge-lane graphs, history of the default branch, the special Workspace context. The App never runs git; everything goes through the Worker.

## Scope of the first version

Start narrow, learn how well it performs, then widen.

| In v1 | Out of v1 (and why) |
|---|---|
| **Feature contexts** only, and **every Feature branch created from the repository's default branch is a first-class, must-work case** (the primary acceptance case for v1). "Default branch" is **per repository**: `main` in one, `master` or `develop` in another, whatever that repository's default is, taken from the same default-ref resolution the ahead badge uses (`origin/HEAD` or the stored default). No assumption that it is called `main` anywhere in code, UI text, or tests | The special **Workspace** context. It shares state with the main checkout and is usually on the default branch |
| A repository contributes history when it has commits to review in a Feature worktree, in any of the shapes in "Repository states" below (feature branch, parent that is not main, detached at a pinned tag) | **Default branch (main)** history. `main` has no natural "since" boundary, so a history of it is unbounded. The only bounded questions are "unpushed commits" and "commits since my last pull" |
| Commits in `base..tip`, where base and tip are resolved per repository (below) and equal the ahead badge whenever the badge has a parent to measure against | Older history, search across all history, file history |

Later stages, each reusing the same tables:

1. **Workspace context on a non-default branch**: same mechanism, no new design.
2. **Default branch, bounded**: only `origin/main..main` (unpushed) or `lastSeenSha..main` (since last Sync), never the open-ended log. Needs its own cap discussion and a "show more" model, so it waits until v1 numbers exist.

Repositories with nothing to review contribute no dots, no rows and no Worker work. This also bounds cost: **cost scales with the number of repositories that have commits to review, not the number of repositories in the Feature.**

## Repository states: a Feature is never uniform

A Feature's repositories are separate worktrees created from whatever the Workspace had checked out at that moment, so a single Feature routinely mixes: repositories on a feature branch off `main`, repositories whose parent is some **other branch** (`release/1.4`, another feature), repositories **detached at a tag** with no branch at all, repositories still sitting on their parent branch, and repositories that are missing, offline or mid-operation. The model stores what is needed (`WorkspaceFeatureRepository.BaseCommitSha`, `ParentBranchName`, `PinnedTag`, `WorktreePath`, `State`; the status row has `BranchName`, `HeadCommit`, `IsDetachedHead`, `IsUnbornBranch`, `IsMerging`; Sync reports `CurrentTag`). Every state below is **derived from rows already in SQLite, with no git call**, and every one has a defined, calm presentation (a state chip, never a red error for an expected situation).

| State (derived without git) | Base | Tip | History | Shown as |
|---|---|---|---|---|
| **Feature branch created from the repository's default branch** (the primary case; the default may be `main`, `master`, `develop`, and differs per repository) | The repository's default ref, resolved exactly as the ahead badge resolves it (`ParentBranchName` when it names the default branch, otherwise the default ref). Stored per repository in the history row | Feature branch tip | Yes | Dots; the base marker shows the default branch name, or `default` plus a tooltip listing each repository's actual name when they differ across the Feature |
| **Feature branch, parent is another branch** (`release/*`, a stacked feature) | The repository's own parent ref (`ParentBranchName`), resolved exactly as the badge does it. Base is **per repository**, stored in the history row (`BaseRef`) | Feature branch tip | Yes | Dots; base marker reads `main +1`, tooltip lists each repository's parent |
| **Parent ref cannot be resolved** (deleted, never fetched, renamed) | Falls back to `BaseCommitSha` (the fork point recorded at creation) | Feature branch tip | Yes, marked | Dots with a small "since fork point" marker; count may differ from the badge and the tooltip says why. A degraded answer beats no answer |
| **Detached at a pinned tag** (`PinnedTag` set, no Feature branch exists, none is ever created) | `BaseCommitSha` (the tag commit) | `HEAD` | Yes, if commits were made on the detached HEAD | Dots plus a "Detached at v1.2.3, commits are not on a branch and can be lost on checkout" chip. This is a safety win: those commits are otherwise invisible |
| **Detached at a tag, nothing committed** | `BaseCommitSha` | `HEAD` | No (empty range) | State chip "On tag v1.2.3, no commits" |
| **Detached at an arbitrary commit** | `BaseCommitSha` | `HEAD` | Yes if ahead of base | Chip "Detached at abc1234" |
| **On the parent branch itself** (user checked out `main` in the worktree), zero ahead | n/a | n/a | No | State chip "On main, nothing to review". Rows kept from a previous branch are retained, not purged, so flipping back does not recompute |
| **Feature branch checked out, zero ahead** | n/a | n/a | No | State chip "Up to date with main" |
| **Unborn branch** | n/a | n/a | No | State chip "No commits yet" |
| **Merge, rebase or cherry-pick in progress** (`IsMerging` and the rebase flags) | n/a | n/a | Frozen | Show the stored history with an "operation in progress" chip; hydration is **deferred until the operation ends** (HEAD moves many times and the list would flap) |
| **Feature repository `Pending` / failed / worktree missing / Worker offline** | n/a | n/a | Whatever is stored | State chip with the reason; never blocks other repositories |
| **Hydrating** | n/a | n/a | Loading | Faint rail segment and "loading" chip |

Rules that follow from the table:

- **The tip is the Feature branch (or `HEAD` when detached), not blindly `HEAD`.** If the user flips a worktree to another branch temporarily, the Feature's history stays correct and stable. The tip and base are decided by the Worker with the same resolution code as the ahead badge, plus the fork-point fallback.
- **Base is per repository.** There is no single "main" for a Feature. The history row carries `BaseRef` / `BaseSha` / `MergeBaseSha` / `BaseKind` (`ParentRef`, `ForkPoint`, `PinnedTag`) and `TipKind` (`Branch`, `DetachedHead`), and `BranchName` is nullable.
- **State is data, not an error.** The UI groups the scope picker by state: `Has commits (12)`, `Up to date (61)`, `On a tag (5)`, `No base / unavailable (2)`, `Loading (n)`. Only the first group is expanded; the others sit behind one muted line, "88 repositories have no commits to review", with a popover listing them by reason. A Feature with 100 repositories and 12 that are ahead feels like 12.
- **The badge and the timeline must agree or say why.** When base comes from the fork-point fallback the count can differ; the tooltip names the base used. Otherwise the stored count equals the badge (integrity check).
- **Changing state is incremental.** A repository leaving or entering a state (tag checked out, switched to `main`, new commit on a pinned tag) triggers only that repository's re-evaluation, from the status row change, with no git call unless HEAD or the base moved.

## Existing facts this design relies on

| Fact | Where |
|---|---|
| Ahead count is `git rev-list --count <ref>..HEAD` (and `--left-right` for behind/ahead). The history list must use the **same ref resolution and range**, so dot counts equal the badge and can be used as an **integrity check** (stored count must equal the badge). | `GitCliRepositoryReader.GetCommitCountsVsDefaultAsync`, `GitWorktreeService` (~752, ~908) |
| A Feature repository stores `ParentBranchName` (provenance, PR target suggestion). Confirm whether the badge uses it or the default ref; the history follows whatever the badge follows. | `WorkspaceFeatureRepository.ParentBranchName` |
| The Changes page reads **SQLite only** and the Worker pushes into SQLite through a single-writer queue; digest no-ops, per-repository live patch, SQL paging and virtualization already exist as patterns. This feature extends that model. | doc 07 |
| The status row and snapshot already carry `HeadCommit`, so the App knows when HEAD moved without extra git calls. | `GitChangeSnapshot`, `WorkspaceGitContextRepositoryStatuses` |
| The tree is a flattened `GitChangesTreeRow` list from a pure builder, rendered with `Virtualize`. | doc 07 |
| Diff requests are `GitDiffRequest(path, Staged or Unstaged)`; Monaco renders Original and Modified; Binary / TooLarge / UnsupportedEncoding show placeholders. | `GitChangeModels.cs`, `GetGitFileDiffCommand` |

## Responsiveness contract: GrayMoon must never feel slower

This feature is only worth shipping if nothing existing gets slower and everything new feels immediate. These are requirements, with the guards that enforce them.

**1. Zero cost to what exists today**

- The Changes page open path is unchanged: tree and working tree paint exactly as today. History never gates first paint.
- The strip mounts only when the existing status rows (already in SQLite) show at least one repository with commits to review. Otherwise there is no query, no component, no layout change.
- To avoid layout shift, the strip's height is reserved from that same cheap check and its content fills in after first paint; the tree never jumps.
- Collapsing the strip stops all history reads except one count query.
- No additional git process runs on watcher events. Hydration is queued only when `HeadCommit`, the ahead count, or a repository state flag **changes**, compared in memory against the stored history row. A working tree edit never touches history.
- History hydration is **not in the Sync critical path**. It starts after a Sync finishes and runs on its own low-priority lane.

**2. Background work that stays out of the way**

- Dedicated parallelism cap (a fraction of `MaxParallelRepositoryOperations`), read pool only, never the diff pool the user is waiting on.
- It **yields** to user work: paused while a Commit, Push, Sync, Undo, branch update or rebase is running for that repository or while the user is actively scrubbing, then resumes. A user click on a repository whose history is still loading **jumps that repository to the front of the queue**.
- Order: selected repository, visible repositories, then the rest by commits ahead.
- One bounded process per repository per HEAD change; incremental mode makes the common case one tiny `git log`.
- Startup backfill of existing Features happens once, in the background, so a "cold" open is rare. If a user does beat it, they see ready repositories instantly and the rest filling in, never a blocking spinner.

**3. Interactions answer within a frame or two**

| Interaction | Budget and technique |
|---|---|
| Click a dot, or press `[` / `]` | **Selection paints immediately** (optimistic highlight, before any data). Tree rows come from SQLite in under 100 ms when the file list is persisted; otherwise from one on-demand Worker call (target under 150 ms) with skeleton rows, never a blocking overlay |
| Hold `]` to scrub quickly | Only the latest selection fetches; older in-flight loads are discarded by a version counter. No queue of stale work |
| Hover a dot | After about 120 ms of intent, **prefetch** that commit's file list and the neighbours' (previous and next); after selection, prefetch the next file's diff the same way the page already treats the selected file |
| Scroll or drag the rail | Windowed SQL keyset read, constant size; strip stays 60 fps with virtualized dots |
| Switch scope or repository | Pure SQLite summaries, under 50 ms |
| Return to Working tree | Instant; its state was never discarded |
| Σ view | Per-repository file lists lazily filled and persisted; first repository paints first, others stream in, tree remains scrollable |

**4. Failures are invisible to the main flow**

A failed or slow history load never disables the working tree, commit box, Sync or any other control. It shows a muted chip on that repository only, is retried on the next trigger, and is logged (no paths).

**5. Measured, logged, and guarded**

- Every stage logs a timing, like `StatusMs` / `PersistMs` do today: `ListMs`, `ApplyMs`, `WindowReadMs`, `FilesLoadMs`, `TimeToDotPaint`, `TimeToTreePaint` (never paths or messages).
- Phase 0's fixture becomes an automated benchmark test with the budgets above; a regression fails it.
- A guard test compares Changes page open time on a Feature with the strip on and with it disabled and asserts the difference stays small (target within 5%).
- A guard test asserts that no history git command runs on a watcher event that did not change HEAD, the ahead count, or the state.

## Performance reality and why we persist

### Measured on a developer laptop (2026-11-11)

Script: `changes-commit-timeline-measure.ps1` (next to this note). One synthetic repository: 400 files on `main`, then 181 commits on `feature` (5 files each, 60 files in every tenth commit, 300 lines per file). Each command is started the way the Worker starts it (a .NET process, no shell), once cold and then 9 times; the table shows milliseconds including process start. Machine: i7-8665U (8 logical processors), Windows 11, git 2.56.0.windows.4, standard Defender, local SSD.

| Command | First run | Min | Median | Max |
|---|---|---|---|---|
| Process start floor (`git --version`) | 81 | 69 | 76 | 85 |
| `rev-list --count` (the ahead badge) | 79 | 75 | 80 | 103 |
| `log` 181 commits, metadata only | 88 | 74 | 78 | 84 |
| `log` 181 commits with `--name-status` | 92 | 86 | 90 | 106 |
| `log` 181 commits with `--numstat` | 368 | 347 | 351 | 367 |
| `log` one new commit (incremental) | 75 | 73 | 76 | 82 |
| `merge-base --is-ancestor` | 78 | 70 | 74 | 80 |
| `diff-tree` one commit, 5 files | 78 | 75 | 76 | 81 |
| `diff-tree` one commit, 60 files | 86 | 81 | 83 | 84 |
| `diff -M --numstat main...feature` (all commits combined) | 129 | 126 | 130 | 135 |
| `diff-tree --stdin --numstat`, 181 commits, one process | 366 | 368 | 373 | 376 |
| `show <sha>:<path>` (one side of a diff) | 79 | 71 | 73 | 80 |

Parallel sweep, 48 metadata `log` calls (stands in for a backfill of 48 repositories):

| Parallel processes | 48 calls total ms | ms per call |
|---|---|---|
| 1 | 4,244 | 88 |
| 2 | 3,881 | 81 |
| 4 | 3,971 | 83 |
| 8 | 2,504 | 52 |
| 16 | 1,827 | 38 |

What this says:

- Walking 181 commits costs git about 2-15 ms. The other 75 ms is starting the process. A fixture of 100 repositories would only repeat that floor 100 times, so it was not built.
- `--numstat` makes git diff every commit and is the only thing that scales with content (about 350 ms here). Dots do not need it.
- Parallelism does not scale: process creation is the bottleneck, not CPU or disk.
- Commit data is **immutable per SHA**; only the membership of the range changes (new commit, amend, rebase, parent moved). Paying 80 ms or more per repository on every page open would be waste.

Not covered by the synthetic fixture: a large real repository (a big tree makes each diff heavier), a cold disk, and security software that inspects every process start. The last one is why the same script is run on the enterprise VDI.

So: persist per Feature branch, update in the background with one process per repository per HEAD change, and make the page open a pure SQLite read. Targets, restated against the laptop numbers (the VDI run is recorded under "Measured on the enterprise VDI"; these targets are not restated yet):

| Measurement | Target | Laptop |
|---|---|---|
| `ListBranchCommits` for 181 commits with file lists, no line counts | under 150 ms including process start | 90 ms |
| Update after one new commit | one git process | 90 ms |
| Cold backfill of 100 repositories that are ahead, background | under 10 s total, no visible UI impact | about 5-8 s (extrapolated from the sweep) |
| Page open, 100 repositories x 181 commits (18,100 rows), windowed read | under 100 ms | not measured (SQLite only, measured in Phase 2) |
| Click a commit | SQLite read, no git process | n/a |
| Combined view of one repository | under 250 ms | 130 ms |

### VDI measurement

Measured on 2026-11-11 with `changes-commit-timeline-measure.ps1`. The results are below. The procedure prompt is kept as the record of what was run. The design text is not revised from these numbers.

### Measured on the enterprise VDI (2026-11-11)

Same script and synthetic fixture as the laptop section: one repository, 400 files on the default branch, then 181 commits ahead. Each command is a .NET process start, once cold and then 9 times; the table is milliseconds including process start. The run shown is the second. Every median, first sample, min, max and parallel total from the first run stayed within 30% of this run (closest: the first `--name-status` sample, 123 ms versus 157 ms; largest median gap: `log` with `--numstat`, 430 ms versus 481 ms). There was no third run. Fixture build was 5 s on both runs. No local feature branch was many commits ahead of its default branch (the largest lead found was 2 commits), so the real-repository rows were not run.

Environment: git 2.51.2.windows.1, Windows PowerShell 5.1.26100.9711, 12 logical processors. Windows Defender and a separate endpoint antimalware product are both active, including process inspection.

| Command | First run ms | Min ms | Median ms | Max ms |
|---|---|---|---|---|
| Process start floor (`git --version`) | 113 | 104 | 106 | 115 |
| `rev-list --count` (the ahead badge) | 114 | 114 | 121 | 137 |
| `log` 181 commits, metadata only | 135 | 123 | 125 | 132 |
| `log` 181 commits with `--name-status` | 157 | 127 | 136 | 152 |
| `log` 181 commits with `--numstat` | 483 | 455 | 481 | 492 |
| `log` one new commit (incremental) | 117 | 113 | 119 | 129 |
| `merge-base --is-ancestor` | 124 | 107 | 119 | 129 |
| `diff-tree` one commit, 5 files | 113 | 109 | 116 | 138 |
| `diff-tree` one commit, 60 files | 124 | 123 | 128 | 133 |
| `diff -M --numstat main...feature` (all commits combined) | 176 | 172 | 176 | 186 |
| `diff-tree --stdin --numstat`, 181 commits, one process | 520 | 489 | 510 | 525 |
| `show <sha>:<path>` (one side of a diff) | 118 | 108 | 115 | 125 |

Parallel sweep, 48 metadata `log` calls (stands in for a backfill of 48 repositories):

| Parallel processes | 48 calls total ms | ms per call |
|---|---|---|
| 1 | 6,058 | 126 |
| 2 | 3,327 | 69 |
| 4 | 2,107 | 44 |
| 8 | 1,390 | 29 |
| 16 | 1,156 | 24 |

Comparison with the laptop table. Command rows are VDI median divided by laptop median. Sweep rows are one sample each, so that ratio is the VDI total divided by the laptop total (the per-call ratio is the same).

| Command | Laptop median ms | VDI median ms | VDI / laptop |
|---|---|---|---|
| Process start floor (`git --version`) | 76 | 106 | 1.39 |
| `rev-list --count` (the ahead badge) | 80 | 121 | 1.51 |
| `log` 181 commits, metadata only | 78 | 125 | 1.60 |
| `log` 181 commits with `--name-status` | 90 | 136 | 1.51 |
| `log` 181 commits with `--numstat` | 351 | 481 | 1.37 |
| `log` one new commit (incremental) | 76 | 119 | 1.57 |
| `merge-base --is-ancestor` | 74 | 119 | 1.61 |
| `diff-tree` one commit, 5 files | 76 | 116 | 1.53 |
| `diff-tree` one commit, 60 files | 83 | 128 | 1.54 |
| `diff -M --numstat main...feature` (all commits combined) | 130 | 176 | 1.35 |
| `diff-tree --stdin --numstat`, 181 commits, one process | 373 | 510 | 1.37 |
| `show <sha>:<path>` (one side of a diff) | 73 | 115 | 1.58 |

| Parallel processes | Laptop total ms | VDI total ms | VDI / laptop |
|---|---|---|---|
| 1 | 4,244 | 6,058 | 1.43 |
| 2 | 3,881 | 3,327 | 0.86 |
| 4 | 3,971 | 2,107 | 0.53 |
| 8 | 2,504 | 1,390 | 0.56 |
| 16 | 1,827 | 1,156 | 0.63 |

Single-process commands cost about 1.4x to 1.6x the laptop. From 2 processes upward the sweep is faster here than on the laptop (12 logical processors on this machine, 8 on the laptop).

Answers:

a. The process start floor is 106 ms median. The first sample is 113 ms, 7 ms (about 7%) above that median. On the other run the first sample was 109 ms against a 113 ms median.

b. The parallel sweep gets faster as the process count rises: 6.1 s at 1 process, 3.3 s at 2, 2.1 s at 4, 1.4 s at 8 and 1.2 s at 16. The gain from 8 to 16 is small.

c. Scaling the 48-call sweep by 100/48, a cold backfill of 100 repositories is about 4.4 s at 4 parallel processes (2,107 ms x 100/48) and about 2.9 s at 8 (1,390 ms x 100/48).

d. `log` with `--name-status` stays close to metadata only: 136 ms versus 125 ms, 11 ms more (about 9%). The other run's gap was 7 ms (125 versus 118).

e. No timed target is missed. `ListBranchCommits` for 181 commits with file lists is 136 ms, 14 ms inside the 150 ms cap. The update after one new commit stays one git process (full metadata list 125 ms, with file lists 136 ms). The 100-repository backfill is about 4.4 s at 4 processes and about 2.9 s at 8, inside the 10 s cap. The combined view is 176 ms, 74 ms inside the 250 ms cap. Page open and click-a-commit are SQLite reads and were not part of this run.

The floor is under the 200 ms row in "How the VDI numbers will be read", so that row leaves the revised design in place. The first sample stays close to the median, so the cold-start row stays unused.

Finding 4 (parallelism buys little because process creation is serialized) is contradicted: the sweep gets faster through 16 processes, from 6.1 s at 1 process to 1.2 s at 16.

The procedure that produced these numbers:

```text
You are measuring git performance on this machine for a design note. Do not change any product code.

1. Read docs/todo/ideas/changes-commit-timeline.md in the GrayMoon repository, sections "Review findings" and
   "Performance reality and why we persist", so you know what the numbers are for.

2. Run the script next to it with Windows PowerShell, from any folder:
     powershell.exe -NoProfile -ExecutionPolicy Bypass -File <path>\changes-commit-timeline-measure.ps1
   It creates one synthetic git repository in a new folder under the temp directory, times a fixed list of
   read-only git commands, and prints two Markdown tables. It takes one to ten minutes. It writes nothing
   outside that temp folder. If execution policy or security software blocks it, report the exact message
   and stop; do not work around a security control.

3. Run it a second time and keep both outputs. If a number differs by more than 30% between the runs, run it
   a third time and say which run looks like the outlier.

4. If a real repository with a feature branch that is many commits ahead exists on this machine, run the
   script once more against it (read-only):
     ... -File <path>\changes-commit-timeline-measure.ps1 -RealRepositoryPath <repo> -RealBaseRef origin/main
   Use that repository's own default branch for -RealBaseRef. Skip this step if there is no such repository.

5. Delete the temp fixture folder the script printed.

6. Write the results into the note, in the section "VDI measurement", under a new heading
   "### Measured on the enterprise VDI (<date>)":
   - git version, PowerShell version, logical processors, and in general words what security software is
     active (for example "endpoint protection with process inspection"). No host names, user names, paths,
     repository names or product licence details.
   - both tables from the most representative run, plus the real-repository rows if you have them.
   - a comparison with the laptop table: for each row, VDI median divided by laptop median.
   - answers to these questions, each in one or two sentences:
     a. What is the process start floor, and how far is the first run from the median?
     b. Does the parallel sweep get faster with more processes, stay flat, or get slower?
     c. Extrapolated from the sweep, how long does a cold backfill of 100 repositories take at 4 and at 8
        parallel processes?
     d. Is "log with --name-status" still close to "log metadata only"?
     e. Which of the targets in the note's target table are missed, and by how much?
   Do not redesign the feature. Only if a result contradicts a finding in "Review findings", add one line
   under your results that says which finding and why.

7. Follow the repository's writing rules: ASCII hyphen only (no em dash or en dash), Windows line endings.
   Do not commit or push.
```

How the VDI numbers will be read:

| VDI result | Consequence |
|---|---|
| Process floor under about 200 ms | Design stands as revised in "Review findings" |
| Process floor 200-500 ms | Same design. The backfill of 100 repositories takes a minute or more, so the "History: 14 / 30 repositories" chip and the selected-repository-first order become essential, and hydration must never run for a repository that is not ahead |
| Process floor above 500 ms, or the sweep gets slower with more processes | Lower the hydration parallelism cap to 1-2, move tier 2 line counts out of v1, and consider reading history in-process with LibGit2Sharp (already used by the Worker for config reads) instead of one git process per repository |
| First run far above the median | Cold cost matters more than warm cost: hydrate right after Sync and after Feature create, so the user never pays it on a click |

## Persistence design

### Key and lifetime

Everything is keyed by **(context, repository, branch name)** and lives **until that branch is purged**. Purged means any of:

- the Feature is removed (`WorkspaceFeatureOperations` removal path),
- the repository is removed from the Workspace or Feature,
- the branch no longer exists (deleted after merge, "Return to default" with branch removal, observed at Sync),
- the repository's checked-out branch changed to a different branch (rows for the old branch are kept only if the Feature still tracks that branch, otherwise purged).

Rules from the defect-prevention list apply: delete only for sources that **answered** (a failed or partial Worker answer never deletes history), clean **every** dependent table in the same transaction, and re-check that the branch is still gone before queued destructive work runs. A purge that cannot run (Worker offline) leaves rows in place and retries later. Orphans are removed by a sweep when a Feature or repository no longer exists.

Commit rows are **per (context, repository, sha)**, not shared globally. That duplicates a row if two contexts track the same branch, which is rare, and buys simple cascade deletion and no reference counting.

### Tables (new numbered migration, additive)

| Table | Columns (summary) | Purpose |
|---|---|---|
| `WorkspaceGitContextBranchHistories` | Id, ContextId, RepositoryId, BranchName (**nullable**, null when detached), BaseKind (`ParentRef`, `ForkPoint`, `PinnedTag`), TipKind (`Branch`, `DetachedHead`), BaseRef, BaseSha, MergeBaseSha, TipSha, UpstreamSha (nullable), ExpectedCount (from the ahead badge, nullable when the base is a fork-point fallback), CommitCount, State (`Ready`, `Loading`, `Stale`, `Failed`), Version (monotonic, latest wins), LoadedAtUtc | One row per repository history; the integrity and freshness record. Unique on (ContextId, RepositoryId, BranchName-or-`@detached`) |
| `WorkspaceGitContextBranchCommits` | HistoryId, Sha, Ordinal (1 = oldest, topological), ParentShas, AuthorName, AuthorDateUtc, CommitDateUtc, Subject (up to 1000), IsMerge, FilesChanged, Insertions, Deletions (last three nullable, tier 2) | Immutable commit metadata plus membership and order. **No message body, no author email** (privacy and size) |
| `WorkspaceGitContextCommitFiles` | HistoryId, RangeKey (`sha` for one commit, `from..to` for a range or the Σ view), Path, OriginalPath, Status, Insertions, Deletions, IsBinary, IsSubmodule | **Lazily filled** changed-file lists, on first click. Dropped for a range key when HEAD moves; per-commit keys stay valid forever |

Indexes: `(HistoryId, Ordinal)`, `(HistoryId, Sha)` unique, `(ContextId-via-history, CommitDateUtc, RepositoryId, Ordinal)` for the merged cross-repository order, and `(HistoryId, RangeKey, Path)`.

"Pushed" is **not stored per commit**. The history stores `UpstreamSha`; a commit is pushed when its ordinal is at or below the ordinal of `UpstreamSha`. A push only updates `UpstreamSha`, no `git log`.

Paths and file names are never logged; messages are not persisted beyond the subject.

### Hydration pipeline (background, incremental)

```text
Trigger: status row HeadCommit or ahead count changed (snapshot push, Sync, Commit, Push, Undo push,
         Update from default, Feature create)  -> CommitHistoryHydrator queue (per history, coalesced)

Worker ListBranchCommits(repoPath, baseRef, knownHeadSha?)
   knownHeadSha is ancestor of HEAD   -> Incremental: only knownHeadSha..HEAD  (the common case, one new commit)
   otherwise (rebase, amend, reset)    -> Full: baseRef..HEAD, rows reused by sha so tier 2 data survives
App (write queue, version gate)       -> apply in one transaction, verify CommitCount == ExpectedCount
   mismatch                            -> State=Stale, one full retry, then Failed with a log line (no paths)
App                                   -> publish a ContextCommitHistoryUpdated event (digest, like Git changes)
```

- **Only repositories that are ahead** are hydrated, ordered so the repository of the currently selected file or the visible tree rows goes first.
- A dedicated parallelism limit (a fraction of `MaxParallelRepositoryOperations`, for example 4) so history never starves status scans or Sync.
- Hydration is **background and resumable**. It is not tied to the circuit, so leaving the page does not cancel it and the work is not repeated on return.
- Page open before hydration finishes shows what is ready and a muted "History: 14 / 30 repositories" chip; ready repositories are fully usable, the rest fill in live.
- Triggered after Sync, so by the time the user opens Changes the data is normally already there.
- Tier 2 (`FilesChanged`, `Insertions`, `Deletions` per commit, needed only for dot-size encoding) is filled by an optional low-priority batched pass, using one `git diff-tree --stdin --numstat` process per repository, never by the list call.

### Reading for the UI (SQL, never loads everything)

The strip and tree read through an `IWorkspaceCommitHistoryReadService`, using the same discipline as `WorkspaceGitChangesReadService`:

- **Summaries** (per repository: count, first and last date, hydrated state) for the repository picker and the Σ chip.
- **Rail buckets**: `GROUP BY` time slice (and repository count) in SQL, constant size regardless of commit count.
- **Window**: keyset page over the merged order `(CommitDateUtc, RepositoryId, Ordinal)` for the dots that are currently visible, never "first N and stop".
- **Commit files**: `WorkspaceGitContextCommitFiles` for the key, or one on-demand Worker call that fills the table, then read.
- The worst case, 100 repositories x 181 commits = 18,100 rows, is about 3 MB in SQLite and is never materialized in the circuit.

## UX design

### Where it lives (screen budget)

| Placement | Height cost | Width cost | Verdict |
|---|---|---|---|
| **A. In the header's title row, right side (the slot after the summary), flexing to the available width, wrapping to its own line under the title and summary when too narrow** | **0px on wide windows**, about 32px only while wrapped, 0 when nothing is ahead | Uses leftover title-row width only; nothing else moves | **Chosen** |
| B. Separate full-width row between the header and the splitter | 32-40px always | 0 | Rejected: permanent height cost |
| C. Vertical rail beside the tree | 0px | 40-60px of the tree column | Rejected |
| D. Popover only | 0px | 0 | Used as a complement (the commit list), not alone |

Rules that protect review space:

- Absent (0px) when no repository in the Feature has commits to review, and in every non-Feature context in v1.
- On wide windows the strip lives in space the header already occupies, so working-tree mode costs the tree **no height**. Only when it wraps does it add one line, and then the user can hide it (remembered in localStorage, `graymoon.git-changes.timeline-hidden`); hiding leaves a small `Commits` button in the header that brings it back.
- Stays in wider-review mode (the header is always visible); it does not grow there.
- In commit mode the commit composer is gone, freeing about 70-110px. Net effect on tree height is positive in commit mode.

### Header integration (`GitChangesHeader`)

Today the header's title row holds `Changes`, then the summary (workspace name, "1 of 3 repositories", staged, changed, branch, status letters, line stats), then a spacer, then the refresh progress indicator (`git-changes-header__scan-indicator`); the context selector, filter, next / previous change and Refresh sit in the actions block. The strip takes **the slot the scan indicator uses**, which already sits right-aligned after the spacer:

```text
Wide:    Changes  GrayMoon  1 of 3 repositories  0 staged  2 changed  main  M 1  A 1  +380 -0   [Σ 7 ≡ ○─●─●─●─◌]   [context][filter][v][^][↻]
Narrow:  Changes  GrayMoon  1 of 3 repositories  0 staged  2 changed  main  M 1  A 1  +380 -0                         [context][filter][v][^][↻]
         [Σ 7  ≡  ○──●──●──●──●──●──●──●──◌ Working tree +12 ~3]                                        (own line, full width)
```

- The title row is a wrapping flex row. The strip is `flex: 1 1 auto` with `min-width` about 280px and `order` after the summary. When the leftover width falls below the minimum it wraps to its own full-width line under the title and summary, where it can use the whole width. No JavaScript layout is needed; CSS wrapping decides.
- The header summary plus the actions already take a large share of the width, so the strip often has only about 300-500px on laptop and mid-size screens. Design for that as the **typical** case, not the exception: the compact tier below is the default, and the wrapped full-width line is common at narrower widths. Measure on real window widths in Phase 4 and adjust the minimum.
- **Container-query tiers** (CSS `@container` on the strip, so it reacts to the width it actually gets, not the window), degrading in this order, each keeping the one before's essentials:

| Strip width | What shows |
|---|---|
| 560px and up | Σ chip with count, scope picker with name, `≡`, base label, dots with the overview rail, Working tree node with counts and label |
| 360-560px | Σ chip, scope as an icon with count, `≡`, dots with rail, Working tree node as a counted dot (no label) |
| 280-360px | Σ chip, `≡` (scope moves inside it), dots (compact pitch), Working tree node as a dot |
| Below 280px | Wraps to its own line (above) |

The overview rail is drawn as a 3-4px density line along the bottom edge of the strip, so it does not make the title row taller.

- **The header summary must follow the selection.** "0 staged / 2 changed" and the `M` / `A` / `+380 -0` chips describe the **working tree**. In commit mode they would be misleading, so they swap to the selected commit or range: `abc1234 - 4 files - M 2  A 1  D 1 - +380 -52` (the Σ view shows the combined totals), and "Staged" and "Changed" are not shown. Returning to Working tree restores them. The branch label shows the Feature branch.
- **Refresh progress takes the slot.** The scan indicator and the strip never show together. While `IsScanRunning` is true the strip is removed from the layout (not just hidden) and the indicator shows in the same place; when the scan ends the strip returns with a short fade. The selection, scope and window live in the page state, not the component, so nothing is lost, and background history hydration keeps running. While the strip is gone, the `[` / `]` / `Home` shortcuts are disabled so state never changes invisibly, and the Refresh button remains disabled as it is today. If the strip was wrapped onto its own line, that line collapses while the indicator shows, so nothing is left behind.
- **Refresh also revalidates history.** A manual Refresh asks the hydrator to re-check each repository's tip and state against what is stored (a cheap comparison, plus a git call only where the tip or state changed), so Refresh remains the single "make everything current" action.
- **Layout shift:** the slot is reserved the same way as before, from the cheap status-row check, so the strip appearing after first paint does not move the title or the tree. The scan-to-strip swap is the only intentional change, and it happens in one right-aligned slot.

### Scaling the strip: 1 to thousands of commits

One dot per commit works up to a few dozen. The strip has **three levels** and picks automatically, never hiding a commit:

| Level | When | What the user sees |
|---|---|---|
| **Dots** | The visible window holds up to about 60 commits at a 12-16px pitch | Individual dots, tooltips, selection |
| **Windowed dots + rail** | More than fits (181 in one repository, or the merged order of many repositories) | A **window of dots** that scrolls horizontally plus a thin **overview rail** (3-4px, along the bottom edge of the strip so the header does not grow, taller only in the wrapped full-width form). The rail is a density histogram of the entire range with date ticks and a draggable window handle; click or drag it to move |
| **Bubbles** | Zoomed out to the whole range at 1,000+ commits | Time buckets as bubbles: `Nov 3 - 214 commits - 17 repos`. Click a bubble to zoom the window onto it. A breadcrumb shows `All 2,340  >  Nov 3 (214)  >  repo` with click-to-zoom-out |

Dots are virtualized (only the window is in the DOM), reading from the SQL window query. Wheel over the strip scrolls it horizontally. A zoom control (`+` / `-`, or `Ctrl`+wheel) changes the pitch within the dot levels.

**The commit list is the escape hatch.** A `≡` button opens a virtualized popover list of every commit in the current scope (sha, subject, author, time, repository), with search on subject, sha, author and repository, and filters (repository, author, pushed / unpushed, "touches file X"). It is how a reviewer jumps to commit 137 of 181 by name. The timeline is for flow and orientation; the list is for random access. Both drive the same selection.

Also, for a review this long, commit-by-commit is not the only path: the Σ view (all commits combined) is the default review mode for big Features, and per-commit stepping is for digging in.

### The strip

```text
+----------------------------------------------------------------------------------------------+
| [Σ 2,340] [All repos ▾] [≡]  main ○─●─●─●─●─●─●─●─●─●─●─●─●─●─●─●─●─●  ─ ─ ◌ Working tree +12 ~3  [⌃] |
|   ░░▓▓▓░░░░▒▒▓▓▓▓▓░░▒▒░░░░░▓▓▓▓▓▓▓▓▓░░  <- overview rail (density, date ticks, window handle)      |
+----------------------------------------------------------------------------------------------+
```

Left to right:

1. **`Σ` chip**: "All commits" for the scope, the cumulative `mergeBase..HEAD` diff of each repository in scope (what a PR would contain, committed part only). The number is the sum of the ahead badges.
2. **Scope picker**: `All repos` (default) or one repository (lists only repositories that are ahead, with counts, sorted by count, with search; repository filter as chips for multi-select). Picking a repository gives a single-repository strip, including the 181-commit case.
3. **`≡` commit list**.
4. **Base marker** with the parent branch name (`main`, or `main +1` when repositories differ, tooltip lists them).
5. **Dots / bubbles** per the table above, oldest to newest, equal spacing within the window (not time proportional).
6. **Working tree node** pinned right: dashed ring, staged/changed counts; always present, default selection; `Home` or `Esc` returns to it.
7. **Hide toggle** (only offered while the strip is wrapped onto its own line; see "Header integration").

Dot states (existing tokens from `app.css`):

| State | Look |
|---|---|
| Pushed | Solid fill, `--text-secondary` |
| Unpushed | Solid fill, `--accent-blue` with a small upward tick |
| Hover | 12px, tooltip after 250 ms |
| Selected ("you are here") | 2px ring `--accent-blue-hover`, fill `--accent-blue-light`, plus the position cues in "Navigation" below (sliding ring, file-progress arc, position label); a selected range shows a soft underline across its dots |
| Merge commit | Diamond |
| Working tree | Dashed ring `--text-secondary`; `--warning` when conflicts exist |
| Repository (merged order) | Small colored underline per repository (repository badge color if one exists, else a hashed muted palette); with many repositories this is only a hint, the picker and list are the real filters |
| Dot size (Phase 5) | 8-12px by lines changed, once tier 2 data exists |
| Hydrating repository | Faint placeholder segment on the rail, no dots yet |

Dots, chips and toggles are `<button>` elements and each sets its own `color`, `background` and `border` (no inherited Bootstrap blue); check next to the Workspace Repositories, Repositories and Actions grids before shipping. Tooltip: `abc1234 - subject`, author, relative and absolute time, `+ins -del in N files` when known, repository chip, "Not pushed" when applicable.

Keyboard: left/right on the strip, `[` / `]` page-wide (not in a text field) for previous / next commit **in scope**, `Home` Working tree, `End` HEAD of the scope, `Shift`+arrow or `Shift`+click for a range inside one repository, `Ctrl`+`K`-style focus to the commit list search.

### Navigation: one simple place to move

Today the header's next / previous change buttons (`v` / `^`) step through the hunks of the open file, then on to the adjacent file, and wrap at the ends of the file list of the current view (`WorkspaceGitChanges.ChangeWalk.cs`: `StepWithinFileAsync`, then `WalkToAdjacentFileAsync`, with a wrap). With history, the user needs the same buttons to carry on **across commits**, so there is one place to navigate. Two candidate designs:

| | **Option 1: the same two buttons, the walk continues across commits** | **Option 2: add `[<]` `[>]` for commits, keep `v` `^` circling the current view** |
|---|---|---|
| Buttons in the header | 2 (no change) | 4 (about 80px more in an already tight header), or 2 extra as end caps inside the strip (no header cost) |
| Mental model | "Next" always means "show me the next thing to review", like reading a book | Horizontal arrows move along the horizontal timeline, vertical arrows move along the vertical tree. Clean mapping |
| Reviewing a long Feature | One key held down walks hunk by hunk, file by file, commit by commit | Two controls to alternate between; commit skipping is explicit |
| Skipping a whole commit | Needs a modifier (`Shift`+click or `Shift`+key), `[` / `]`, or a dot click | One click, obvious |
| Risk of surprise | The view can change commit at a boundary without the user asking. Mitigated by the position cues below | None; each button does one thing |
| Fits "one simple place" | Yes | No, two places (unless the caps live in the strip) |
| Width and tiers | Nothing to fit | Header: tight. Strip caps: cost two 24px targets, kept in every tier |
| Existing muscle memory | Same buttons, same keys | Unchanged for `v` `^`; two new keys |

**Recommendation: Option 1, with Option 2's commit stepping available as a modifier and in the strip.** It matches the request for one place, adds nothing to the header, and the horizontal `[` / `]` and `Shift` shortcuts plus the dots cover "skip this commit". If testing shows people want a visible commit-skip button, Option 2's `[<]` `[>]` can be added later as end caps inside the strip with no header cost and no change to the walk semantics, so choosing Option 1 now closes no door.

**Walk semantics (Option 1)**

- **Unit of the walk:** hunk, then file, then commit. The ring is `Commit 1 ... Commit N, Working tree`, oldest to newest. "Next" at the last hunk of the last file of a ring element goes to the first hunk of the first file of the next element; "Previous" at the first goes to the last hunk of the last file of the previous element. This is the existing walk with the edge extended.
- **Only single-element views cross.** Working tree and a single commit are ring elements. **Σ and a selected range are combined views, so the walk stays inside them and wraps inside them** exactly as today; leaving them is a dot click. Likewise, if a text filter is active it still applies, and the walk never crosses into a commit that has no matching file.
- **Order across repositories:** in the `All repos` scope the ring follows the merged commit order, so one step can change repository (the tree swaps to that repository's files, header and dot follow). In a single-repository scope the ring is that repository's commits. Whether the walk should instead group by repository is an open question.
- **Empty elements are skipped.** A commit with no files (empty merge, submodule-only) and a clean Working tree are not stops; they stay reachable by dot and list. A repository still hydrating or failed has no stops until it is ready.
- **Ends of the ring wrap.** Forward from the last element returns to the first, with the same single wrap the current buttons have, announced in the strip's position label ("Back at commit 1 of 181") instead of a toast.
- **Working tree workflow is preserved for features without history.** When the Feature has no commits to review there is no ring and the walk is exactly today's. With history, the Working tree is the last element of the ring: stepping forward past its last change wraps to commit 1, and stepping back from its first change goes to the newest commit. Because that changes today's edge behaviour for pre-commit review, it is called out in the open questions and the wrap is always announced.
- **Skip a whole commit:** `Shift`+click on `v` / `^`, `Shift`+their keyboard shortcut, `[` / `]` page-wide, and left/right when the strip has focus. These move to the previous or next ring element's first file, never mid-commit.
- **Latest wins and stays instant.** The walk keeps the `_changeWalkBusy` guard, but crossing a commit boundary is prefetched: when the user is on the last couple of files of a commit, the next commit's file list is already being fetched (and the diff of its first file), so crossing costs the same as stepping to the next file. Holding the key coalesces to the latest target and discards stale loads with the version counter. A crossing that is not prefetched shows the target dot and skeleton rows immediately and fills in.
- **Buttons reflect context.** The tooltips read "Next change (continues into the next commit)" and "Previous change (continues into the previous commit)", with "Shift: next / previous commit" in a second line. The buttons are enabled in history mode whenever the ring has any other stop, not only when the open file has a change.
- **Read-only is irrelevant to navigation:** the walk, dots and keys work in commit mode and in Working tree mode, and are removed with the strip while a refresh runs (as in "Header integration").

**The dot shows where you are**

The selected dot is the single, always-visible "you are here" marker, and it moves as the walk moves:

1. **Sliding ring.** The selection ring animates to the new dot (about 120 ms, none under reduced motion), so a step across a commit boundary is visible as movement and the eye follows it. The strip scrolls the dot into view and the rail's window handle follows.
2. **File-progress arc.** The ring around the selected dot fills clockwise by position in the commit, `3 of 7` files being about 43% filled. It shows how far through this commit you are, and the next step into the following dot starts a fresh arc. In the Working tree node the same arc shows progress through the working changes.
3. **Position label** (wide tiers only; in narrower tiers it is the dot's tooltip and its `aria-label`): `Commit 137 of 181 - file 3 of 7`, in the repository's name when the scope is `All repos`. Ends read "Back at commit 1 of 181" for a moment after a wrap.
4. **Header summary** already follows the selection (sha, files, M / A / D, lines), which confirms the commit after each step.
5. **Crossing pulse.** When a step lands in a new commit, that dot gets a brief 300 ms halo, which is the cue that the commit changed even if the eye was on the diff.
6. **Range and Σ.** A range lights all of its dots with the underline, Σ lights the whole row and the `Σ` chip; neither shows a single pointer, and the label says `Range 12-15 - file 4 of 28` or `All 181 commits - file 12 of 340`.
7. **Working tree.** Its node takes the same ring and arc when selected, so the user can tell instantly whether they are looking at history or at their uncommitted work.
8. **Reviewed marker (Phase 6).** A faint tick on dots whose files have all been walked, building toward the "viewed" and "since my last review" features.

Screen readers get `aria-current="step"` on the selected dot and a polite live-region update with the same text as the position label.

### Selection modes

| Mode | How | Tree shows | Diff shows per file |
|---|---|---|---|
| Working tree (default) | Right node | Today's Staged / Changed tree with composer | Today's diffs |
| One commit | Dot or list row | That commit's files, scoped to its repository (vs first parent; root commit vs empty tree) | Parent content vs commit content |
| Range | Shift-click, rail drag, or list multi-select; **one repository** | Combined files from the parent of the first to the last | Parent-of-first vs last |
| All (Σ) | Σ chip | Every repository in scope, each its own `mergeBase..HEAD`, as Repository > folders > files (the existing tree shape) | Merge-base vs HEAD per file |

Σ across repositories needs one changed-file list per repository (`RangeKey = mergeBase..head`), filled lazily and cached in `WorkspaceGitContextCommitFiles`, invalidated when that repository's HEAD or merge base moves.

### Multi-repository behavior

- The unit of selection is **(repository, commit)**; the tree is scoped to that commit's repository: section `Commit abc1234` > repository > folders > files.
- All repositories' commits order by `CommitDateUtc` with a stable tiebreak (repository name, then ordinal), which tells the story of the Feature across repositories.
- Lane view (toggle, off by default, max 4 visible lanes then scroll) for people who want per-repository rows; it costs height only on request.
- A per-repository commit **cannot be split across repositories** in a range in v1 (a range stays inside one repository).
- Repositories still hydrating, failed, or not ahead are shown in the scope picker with their state (`loading`, `history unavailable`) and never block others.

### Commit mode: what disappears, what stays

Disappears: commit message box, Commit / Commit Staged / push-after-commit / AI split button, Stage / Unstage / Undo / Discard row actions and menu items, multi-select action bar, drag-to-stage.

Stays: filter box (works on commit files), expand/collapse, copy path, previous/next change walk (which now continues across commits, see "Navigation"), open-in actions, wider-review toggle, markdown preview, Monaco diff (read-only).

The subject and short sha go in the diff pane's existing header (`abc1234 · Fix null reference in sync · A. Author · 2 days ago`), with the full message in a popover; no extra row. A thin `--accent-blue-light` top border on the tree and diff panes marks the read-only history view; no banner.

## Worker commands

All in the **read pool** (`ReadOnlyCommands`), using `GitRepositoryPathValidator`, `ArgumentList` with `--end-of-options`, never logging paths, accepting **only full hex SHAs** from the App.

| Command | Request | Git | Response |
|---|---|---|---|
| `ListBranchCommits` | repository path, **tip** (Feature branch name, or detached `HEAD`), parent ref name and `BaseCommitSha` from the App (resolution **by the same code as the ahead badge**, then fork-point fallback), optional `KnownTipSha` | `git merge-base --is-ancestor <known> <tip>`, then `git log --reverse --no-color --format=<sha, parents, author name, author date, committer date, subject> --end-of-options <base>..<tip>` (or `<known>..<tip>`). `git rev-list <upstream>..<tip>` gives `UpstreamSha` (none for a detached tip). **No stats** | `Outcome` (`Ok`, `ParentUnresolved`, `TipMissing`, `Unborn`), `BaseKind`, `Mode` (`Incremental` or `Full`), `BaseSha`, `MergeBaseSha`, `TipSha`, `UpstreamSha`, `Commits[]`. No count cap. An unresolved parent is an **outcome**, not an exception, so the App can fall back or show a state chip |
| `GetCommitChanges` | repository path, `FromSha` or empty tree, `ToSha` | `git diff-tree -r -M --name-status -z --numstat` (one commit) or `git diff -M ...` (range, Σ) | changed-file entries (status A/M/D/R/C/T, path, original path, insertions, deletions, binary, submodule). Never truncated; chunk instead of cutting if near the 10 MiB hub receive cap |
| `GetCommitStats` (tier 2, optional) | repository path, list of SHAs | one `git diff-tree --stdin --numstat --shortstat` process | per-SHA files / insertions / deletions |
| `GetGitFileDiff` (extended) | `GitDiffRequest.Revisions { FromSha, ToSha }` | `git show <sha>:<path>` for both sides, same soft size / binary / encoding classification | same `GitDiffDocument`; new `Revision` comparison value |

The commit body for the popover is a separate `GetCommitDetail`; this is also the natural reader for the `pr-description-commit-history` idea.

Merge commits are diffed against the first parent and shown as diamonds; the list is the full `base..HEAD` set including merges so counts equal the badge. Whether to offer a `--first-parent` view is an open question.

## App design

- `CommitHistoryHydrator` (background service) owns the trigger queue, coalescing per history, version gate and write-queue application (sibling of the Git changes write path, so no `SQLITE_BUSY` contention). It runs under the same "report an outcome" rule: every failure is logged (no paths) and shown as a repository state, never swallowed.
- `IWorkspaceCommitHistoryReadService` for the SQL reads above (scoped read, returns immutable view models).
- Page state: `_historySelection` (None = Working tree, or `{Repository, FromSha, ToSha, Kind}`), `_historyScope`, `_historyWindow`, plus a versioned request counter (like `_diffRequestVersion`) to discard stale loads.
- Tree: map commit file rows to `GitChangesTreeRow` with section kind `Commit` and reuse the pure builder, `Virtualize`, change walk and a **separate** persisted expand-state key (browsing history must never change the working tree expand state).
- Selection survives reload via `?commit=<sha>&repo=<id>` or `?range=<a>..<b>&repo=<id>`, validated on load, per context.
- Live: `ContextCommitHistoryUpdated(workspaceId, contextId, repositoryId, digest)` patches only that repository's rail and window, with a digest no-op like the Git changes event.

### Defect-prevention rules this feature must follow

- **Feature-context scoping**: history is keyed by context and read from the context's repository; never write `WorkspaceRepositoryLink`; capture `RequireSelectedContextId()` before queueing any history job.
- **Page jobs**: load off-thread, apply inside `InvokeAsync` / `SafeInvoke`; a context switch discards in-flight window loads.
- **Lifetimes**: hydrator is a singleton that holds no per-circuit state; the read service is scoped; the page removes every subscription in `Dispose`.
- **Destructive work**: purge only for sources that answered, clean every dependent table, re-check before queued deletes (see Persistence).
- **Failures**: a failed list marks the repository `Failed` with a muted chip and a log line; transient Worker disconnects are "not yet", retried on reconnect and on the next trigger.
- **Worker input**: SHAs by regex, paths validated, `ArgumentList` with `--`.
- **Read-only enforced in code**: every mutating page handler checks `IsHistoryMode` and returns early; a test enumerates the page's mutating methods.

## Live behavior while browsing

| Event | Behavior |
|---|---|
| New commit | Incremental hydration appends; the dot animates in; selection unchanged; Working tree stays selected if it was |
| Working tree changes while viewing a commit | Tree and diff stay put; the Working tree node counts update |
| HEAD rewritten (amend, rebase, reset, force-push, undo push) | `Full` hydration reuses rows by sha; if the selected sha is gone, fall back to Working tree with a toast "Commit abc1234 is no longer on this branch (history was rewritten)" |
| Parent branch moved (Update from default, base advanced) | `Full` hydration; membership shrinks as commits land in the parent |
| Push | `UpstreamSha` updates, dots turn to pushed style, no `git log` |
| Branch deleted or Feature removed | Purge per the lifetime rules |

## Accessibility and polish

Strip is `role="listbox"` horizontal, dots are `role="option"` with `aria-selected` and an `aria-label` such as "Commit 137 of 181 in repo-name: subject, author, time". Focus uses the global focus ring token; no dot state relies on color alone (ring, diamond, tick, dash). Reduced motion removes dot-in animation and hover scale. Width tiers follow the container-query table in "Header integration" (labels drop first, then the scope picker moves into the commit list popover); the mockups above show the full-width form.

## Phases

0. **Measure.** Laptop and enterprise VDI done (2026-11-11, `changes-commit-timeline-measure.ps1`; see "Measured on a developer laptop" and "Measured on the enterprise VDI"). Remaining: revise this note from "Review findings" and set final targets. The SQLite insert and windowed read of 18,100 rows is measured in Phase 2 with the real tables.
1. **Worker and Common.** `ListBranchCommits` (incremental and full), `GetCommitChanges`, `GetGitFileDiff` revisions, SHA validation, pool routing. Tests with `TempGitRepositoryFixture`: count equals `DefaultBranchAhead` for Feature branches created from the default branch (fixtures with default `main`, `master` and `develop` in the same Feature, remote default differing from the local name, default ref present only as `origin/<name>`), parent that is not the default, unresolvable parent (fork-point fallback), detached at a pinned tag with and without commits, branch not checked out, merge, root, rename, binary, incremental versus full agreement, rewritten history, upstream boundary, rebase in progress.
2. **Persistence and hydrator.** Migration, tables, hydrator with coalescing, version gate, integrity check against the badge, purge on Feature and repository removal. Tests: failed Worker answer never deletes, purge cleans every table, stale answer discarded, rewrite reuses rows.
3. **Read service and page state.** SQL summaries, rail buckets, keyset window, selection, URL query, rewritten-history fallback.
   Also the **state derivation** (pure function from status and Feature repository rows to the state table, unit-tested for every row, no git) and the responsiveness guards from the contract.
4. **Strip and commit mode (single commit and Working tree).** Dots, rail, scope picker, commit list, composer removal, read-only border, diff header, read-only guard test. Header integration: strip in the title-row slot with wrap and container-query tiers, header summary following the selection, strip and scan indicator swapping on `IsScanRunning` with no state loss, Refresh revalidating history. Measure the leftover title-row width at common window sizes (1366, 1600, 1920) and set the tier breakpoints from that. Navigation: extend `NavigateChangeAsync` / `WalkToAdjacentFileAsync` so the walk continues across ring elements, with `Shift` and `[` `]` commit skips, commit-boundary prefetch, the sliding ring, file-progress arc and position label. Tests: the ring order, empty-element skipping, wrap at both ends, Σ and range staying inside their view, the unchanged walk when the Feature has no history, stale-load discard while holding the key.
5. **Range, Σ, unpushed styling, keyboard, repository filter, bubbles level, hide toggle for the wrapped line.**
6. **Polish and widening.** Tier 2 stats and dot size, lane view, commit message popover, "viewed" markers and "since my last review" (as GitHub and Reviewable do), AI summary of a range, then the Workspace-context and bounded default-branch stages from "Scope".

## Docs and release steps when built

Update `docs/architecture/07-git-changes-projection-and-tree.md` (new data flow and tables), `03-data-model-and-persistence.md` (new tables and lifetime), `04-runtime-communication-and-concurrency.md` (hydrator, new read-pool commands), `05-user-capability-reference.md`, `01-product-model-and-user-workflows.md` (section 18), and the wiki page for the Changes screen. Add one bullet at the top of `## Recent GrayMoon changes` in `GrayMoon.Desktop/README.md` (App and Worker ship together). Delete this note and its index row when shipped.

## Research summary (unchanged conclusion)

A dedicated Blazor component is the right call: vis-timeline is time-proportional and hard to theme, gitgraph.js and seanrobertwright/GitGraph draw vertical branch DAGs (and the latter is React, brand new), GitLens Visual History is a commercial chart (inspiration for dot size only), Mermaid `gitGraph` is static. GitHub's commit filter and Reviewable's revision strip validate the all / one / range interaction. At 181+ commits the timeline alone is not enough, which is why this revision adds the overview rail, bubbles and the searchable commit list.

## Open questions

0. **Navigation**: ship Option 1 (same two buttons continue across commits, `Shift` or `[` `]` skips a commit) as recommended, or Option 2 (extra `[<]` `[>]`, as header buttons or strip end caps)? And is it acceptable that with history present, stepping forward past the last Working tree change wraps to commit 1 instead of the first Working tree change, or should the Working tree always wrap inside itself and be left only by dot, `[` or `Shift`?
0a. **Cross-repository walk order**: merged commit-date order (default here) or grouped by repository (finish repository A's commits, then B's)?

1. **Base ref for Feature contexts**: confirm whether the badge uses `ParentBranchName` or the default ref and follow it exactly; if they differ, fix that first. Also confirm how a Feature repository's branch name is obtained (status row `BranchName` versus a stored Feature branch field) and what the badge does for `PinnedTag` repositories.
1a. **Pinned-tag repositories**: is `BaseCommitSha..HEAD` the right range for commits made on a detached HEAD, and should the "can be lost on checkout" chip also offer a one-click "create a branch here" action (a mutating action, so separate from this read-only feature)?
1b. **Fork-point fallback**: acceptable to show a count that differs from the badge, with an explanatory tooltip, or should the badge also switch to the fork-point basis in that case?
1c. **Retained history for a branch no longer checked out**: keep until the branch is deleted (this note) or until the Feature is removed?
2. **Measured numbers** from Phase 0 may change the targets, the parallelism limit, or whether Sync should trigger hydration eagerly.
3. **Should Σ optionally include the working tree** (PR preview of committed plus uncommitted work)?
4. **Merge commits**: all of `base..HEAD` (default here, matches the badge) or a `--first-parent` view?
5. **Default scope with many repositories**: `All repos` merged (this note) or last-selected repository?
6. **Cross-repository ranges** are not in v1; is a time-based "feature range" worth it later?
7. **Repository colors**: does a stable repository color already exist to reuse?
8. **Retention when a Feature branch is merged but kept**: purge at merge, or keep read-only history until the Feature is removed?
9. **Hub payload**: confirm the largest realistic commit file list against the 10 MiB receive cap.
