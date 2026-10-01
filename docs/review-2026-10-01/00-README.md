# GrayMoon Review (2026-10-01): Summary and Worktree v1 Roadmap

This folder is a full review of GrayMoon and GrayMoon.Desktop, focused on closing the first release of worktree Features. Every finding in the detailed documents carries file and line evidence. Reviewed at GrayMoon `a06fe33` and GrayMoon.Desktop `8c4fb90`, both on branch `opus-review`.

| Doc | Contents |
|---|---|
| [01-documentation-audit.md](01-documentation-audit.md) | What was changed in the existing docs (20 files), the remaining gaps, and places where the code contradicts the docs |
| [02-worktree-code-and-approach-review.md](02-worktree-code-and-approach-review.md) | Map of the Feature implementation from start to finish, findings F-1 to F-16, status of the 2026-09-21 review items, and an assessment of the Feature model |
| [03-worktree-ux-review.md](03-worktree-ux-review.md) | User flows (create, switch, changes, sync, PR, remove, recovery) and UX findings U-1 to U-20 |
| [04-worktree-v1-release-roadmap.md](04-worktree-v1-release-roadmap.md) | Detailed P0, P1 and P2 items and the release-readiness checklist |
| [05-general-code-and-ux-review.md](05-general-code-and-ux-review.md) | Everything outside Features: architecture, security, reliability, tests, Desktop, UX, enhancement ideas, and a backlog |
| [06-worktree-release-implementation-plan.md](06-worktree-release-implementation-plan.md) | **Live implementation plan.** Give this single file to an AI agent: it contains the agent's working rules, owner decisions, a status tracker, testable units grouped into parallel lanes, and manual test gates |
| [07-appendix-why-lane-g-hooks.md](07-appendix-why-lane-g-hooks.md) | Why the plan fixes Git hooks in v1: what the hooks do, how they destroy user hooks and miss `core.hooksPath` repos, and what G1 changes |
| [08-plan-regression-risk-review.md](08-plan-regression-risk-review.md) | Risk register for the plan: what each unit could break in standard Workspace workflows, the mitigation written into `06`, and the Workspace smoke test |
| [09-switch-branch-in-feature-analysis.md](09-switch-branch-in-feature-analysis.md) | Is the per-repository Switch Branch dialog helpful and safe while a Feature is selected? Findings SB-1 to SB-8, options, chosen v1 behaviour (option B), planned as lane I (I1 to I4) in `06` |
| [10-plan-in-plain-words.md](10-plan-in-plain-words.md) | For the owner, not for AI agents: every step of `06` in plain words, what changes and what you will notice as a user |

## Verdict

The core design is strong. It has a strict App/Worker split, persistence-first UI, a lock that can cover the whole workspace or a single context, careful process execution, and good tests that run against real git and SQLite. All 788 tests pass. Every item from the 2026-09-21 code review has been fixed.

Worktree Features are **not ready to release**. The happy path works. The gaps are in the paths users will hit in the first week: removing a Feature in Docker, a create that fails part-way, a crash mid-operation, locked files on Windows, and upgrading an existing 0.1.0 database. Separately, any local program or web page can read decrypted tokens and trigger actions over the local HTTP port. GrayMoon is a desktop dev tool and needs no user login, but it does need local hardening. That is not specific to worktrees, but it should not ship in another public build.

## The five things that matter most

1. **Remove Feature can destroy uncommitted work (Critical).** The safety check runs `Directory.Exists` on the App's own filesystem instead of asking the Agent (`WorkspaceFeatureOperations.cs:340`). `WorkspaceExternalWorktreeOperations.cs:80` hard-codes "not dirty". In the documented Docker deployment, every repo looks missing, so the user's only option is "discard uncommitted", which force-deletes work they were never shown.
2. **Remove leaves data behind in the database and on disk.** On the review machine, about 85% of `WorkspaceProjects` rows belong to removed Features. There are also about 18 leftover `features\<name>` folders and 5 orphaned remote branches. Some queries scope only by `WorkspaceId` and read these leftover rows.
3. **There is no way out of a failed or interrupted Feature.** `Creating` and `Removing` Features are hidden from the selector but still hold their name. `NeedsRepair` has no retry or rollback. Nothing reconciles these states at startup.
4. **The first real schema upgrade fails silently.** `Migrations.Features.cs:14-35` swallows every error in `catch { }`, with no transaction, version number or backup. No test upgrades a real 0.1.0 database.
5. **Local hardening is missing.** No user login is needed for a desktop dev tool, but nothing stops other local programs or web pages either. `GET /repos/{id}/connector` returns decrypted PATs to any caller, although only the Worker needs it. The fallback encryption key is derived from a constant in the source, and Desktop never sets its own key. Body-less `POST /push` can be triggered from any web page.

## Combined roadmap to close worktrees v1

Effort sizes: **S** is a day or less, **M** is 2 to 3 days, **L** is about a week. The IDs refer to the detailed documents.

### Milestone 1: release blockers (about 3.5 weeks for one developer, about 2 weeks for two)

| # | Item | Source | Effort |
|---|---|---|---|
| B1 | An Agent `InspectWorktree` command reports existence, dirty state, ahead/behind and locked state. The App stops using `System.IO` on Agent paths, enforced by an architecture test. | 04 P0-1, 02 F-1 | M |
| B2 | Remove deletes per-context projects, dependencies and line statuses. A migration deletes orphans and rebuilds the tables with a cascading foreign key. Every remaining query is scoped by context. | 04 P0-2, 02 F-2 | M |
| B3 | Create runs in one transaction. Reconciliation at startup and on Agent reconnect turns stuck Features into `NeedsRepair`. The selector shows every state. | 04 P0-3, 02 F-3 | M |
| B4 | Repair and rollback for `NeedsRepair`, with a panel showing each repo's error and offering Retry, Roll back and Remove. | 04 P0-4, 02 F-4 | M (after B3) |
| B5 | Honest removal: stop watchers, retry around locked files, clean up leftover and empty folders, and show a report for each repo. | 04 P0-5, 02 F-5 | M |
| B6 | Transactional, logged migrations that stop startup on failure and back up the DB first, plus a golden upgrade test from a 0.1.0 database. Absorbs 04 P1-6. | 05 R1, R2, A3 | M |
| B7 | Local security, no login: require a Worker secret for the connector-token endpoint and the Worker hub, use a per-install key or Data Protection and re-encrypt existing tokens, restrict `AllowedHosts` to loopback, and require a custom header and `Origin` check on mutating endpoints. | 05 S1, S2, S3 | S to M |
| B8 | Branch rules inside a Feature. Remove judges the Feature branch it deletes. The Switch Branch dialog and the services allow only the Feature branch, or a tag for tag-pinned repos. "Return to Feature branch" is available, and the grid shows when a repo is off its Feature branch. | 09 SB-1 to SB-8; `06` lane I | M |

B1, B2, B3, B6 and B7 are independent and can run in parallel.

### Milestone 2: should ship in v1 (about 2 weeks)

| # | Item | Source |
|---|---|---|
| S1 | Validate Feature names with Git's and Windows' rules, and make duplicate checks case-insensitive | 04 P1-1 |
| S2 | Use live outgoing-commit and PR facts in the remove analysis, so "unknown" is no longer treated as zero | 04 P1-2 |
| S3 | Offer remote branch deletion safely: opt-in, fetch first, delete only if the remote tip matches what was last pushed, pass the token, and report the result. Merges 04 P1-3 and 05 R3. | 04 P1-3, 05 R3 |
| S4 | Show Remove-modal checkboxes only for the conditions that are present | 04 P1-4 |
| S5 | Keep the primary toolbar button as the next action, but show "Remove" only when every PR in the Feature is merged or closed. Today "no PR" also triggers it, so a brand-new Feature offers Remove (`WorkspaceRepositoriesHeader.razor:461-462`) | 04 P1-5, 05 R8/U3 |
| S6 | Close the remaining places where one context reads another's data (packages, files, push plan, notifications, bulk branch switch) | 04 P1-7 |
| S7 | Hooks: honor `core.hooksPath` and chain existing hooks, or at least detect the conflict and warn | 05 R4, A1 |
| S8 | Desktop "Open in..." launchers use `WorkingDirectory` and `ArgumentList` instead of building `cmd /c` strings | 04 P1-8, 05 D2 |
| S9 | Structured logging for Feature operations, carrying WorkspaceId, FeatureId, ContextId, repo, duration and outcome | 04 P1-9 |
| S10 | Housekeeping: fix the CS8619 warning, delete the committed `test-*.txt` logs, de-flake the pipe test, and enable `TreatWarningsAsErrors` | 04 P1-10, 05 R7/T1/T2 |
| S11 | Spike: check that the GitVersion calculated from the default branch tip matches running GitVersion on a real checkout | 04 P1-11 |

### Milestone 3: release gate (2 to 3 days)

- All regression tests from the [checklist](04-worktree-v1-release-roadmap.md#release-readiness-checklist) are green on Windows and in the Linux container. At minimum: Docker topology (path absent on the App, dirty on the Agent), crash mid-create and mid-remove, partial create then retry, and remove with a locked file.
- A user guide for Features: what a Feature is, where it lives on disk, what Remove deletes and keeps, and how to repair. A troubleshooting page with a manual cleanup recipe (`git worktree list/remove/prune`, `git branch -D`). See 01 section 4.3.
- `docs/worktree/*` labelled as superseded design history, pointing to `docs/architecture` (01 section 4.2).
- A feature flag that hides Features UI and creation, and a documented DB-backup restore for rollback.
- A GrayMoon changelog. Today the Desktop README serves as one.

### After v1 (v1.x)

These come from 04 P2 and 05 section 9:
- Choose which repos are in a Feature.
- Adopt an existing branch as a Feature.
- Set `core.longpaths` and support locked worktrees.
- Selector type-ahead and one consistent term ("Feature") everywhere.
- Refactor the 1,181-line `WorkspaceFeatureOperations`.
- A Feature-aware REST API (`contextId`).

## Beyond worktrees: what would make GrayMoon best in class

Ordered by leverage. Details are in 05 sections 7 and 9.

1. **A trust model** (local pairing and an API key). It is the prerequisite for MCP, automation and team use.
2. **Plan, then execute, for every multi-repo mutation.** Generalize Return to Default's analyze-then-execute pattern to Push, Update and Undo Push. This is the single biggest confidence builder for a tool that pushes many repos at once.
3. **A persistent activity log**, so the user can answer "did that actually work?" after the toast is gone. It also covers the places that report failures as success today, such as `dotnet restore` (05 A2).
4. **Undo across repos**, by snapshotting refs under `refs/graymoon/backup/<op-id>` before each operation.
5. **A health and diagnostics page**: Worker versions, hook status, migration version, rate limits, and a support bundle.
6. **A command palette (Ctrl+K) and a terminology cleanup.** Branch, Feature, context, workspace and level overlap today (05 U2).
7. **Event-driven freshness.** Replace per-tab 2-second PR polling with shared pollers or webhooks feeding an in-process event bus. This also fixes components connecting to their own SignalR hub through the browser-facing URL, which breaks behind proxies (05 A4).
8. **Safer destructive UX**: connector delete with an impact summary and type-to-confirm (05 U1), and accessibility fixes (05 U4).
9. **Transparent Desktop telemetry**, with a plain URL, documentation and an opt-out (05 D1).
