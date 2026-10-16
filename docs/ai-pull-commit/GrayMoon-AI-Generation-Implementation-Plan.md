# GrayMoon — AI Git Metadata Generation: Detailed Implementation Plan

**Status:** Implementation-ready plan, pending baseline compile and test validation  
**Source:** `Jandini/GrayMoon` main reviewed 16 October 2026  
**Companion:** `GrayMoon-AI-Generation-Design.md`  
**Rules:** Incremental, owner-separated, independently testable, lowest possible Git risk. Do not implement unrelated refactors.

## 0. Implementation guardrails and definition of done

**Must retain unchanged:** Git commit orchestration, worker stage/commit semantics, repository target selection, default-branch warnings, push-success filtering, PR create requests, reviewer selection, source/target choice, worktree removal leases, error reporting, and watchers.

**Strict invariants:**

1. One visible adaptive primary Commit button; never ambiguous between Staged and All.
2. One shared commit message or one shared PR title/body.
3. Each PR repository comparison is **selected target/base versus selected source**, matching the PR creation request; never default fallback.
4. One AI generation call per user action, not per repository.
5. AI receives **GM-prepared bounded evidence**, does not run Git or inspect the repository.
6. Source code changes and Git metadata are read-only during evidence gathering.
7. CLI provider pins are local per-workspace; `graymoon.json` lists optional per-provider model overrides.
8. Generation never overwrites user edits without explicit confirmation.
9. No unbounded patches in memory, across Worker/App, or in provider prompts.
10. CLI errors do not prevent non-AI Git workflows.

**Baseline tasks before editing:** check latest main commit, run target .NET solution build, identify test projects and existing command handlers, capture failing baseline tests, record existing Changes and PR UX screenshots, inspect Worker identity/environment under Desktop and VDI. Check the current provider CLI supported arguments against installed versions and official docs. Keep discoveries and deviations in a living implementation log.

## 1. Code map and touchpoints

| Concern | Existing files to review | Intended changes |
| --- | --- | --- |
| Adaptive commit | `src/GrayMoon.App/Components/Pages/WorkspaceGitChanges.razor`; `WorkspaceGitChanges.MultiRepo.cs`; `src/GrayMoon.App/Services/GitChanges/WorkspaceCommitMode.cs` | UI-only keyed alternative, same mode helper/execution. |
| Shortcut | `src/GrayMoon.App/wwwroot/js/git-changes-splitter.js` | Explicit primary-commit class selector. |
| Toolbar CSS | `src/GrayMoon.App/wwwroot/css/git-changes.css` | Joined segmented control, responsive and overflow rules. |
| PR dialog | `src/GrayMoon.App/Components/Modals/NewPullRequestModal.razor`; `NewPullRequestModels.cs` | Tab-right AI control, scope capture, fill existing shared title/body. |
| PR creation | `src/GrayMoon.App/Components/Pages/WorkspaceRepositories.PullRequests.cs`; `Services/GitHub/PullRequestService.cs` | **No semantics change**; reuse actual `BaseBranchByRepositoryId`. |
| Manifest | `src/GrayMoon.Common/Manifest/WorkspaceManifest.cs`, `WorkspaceManifestFile.cs`, `IWorkspaceManifestFile.cs` | Optional `ai.providers`, roundtrip/restore/rewrite safety. |
| Tool catalog | `src/GrayMoon.App/Components/Features/FeatureOpenInTools.cs`; `Services/Ui/InstalledOpenInTools.cs`, `WorkspaceOpenInRecentTools.cs` | Reuse patterns/icons/IDs appropriately; dedicated headless provider catalog and one-pin preference. |
| Git snapshots | `src/GrayMoon.Common/Git/GitChangeModels.cs`; Git Changes App read client/scanner | Input inventory and change validity, not a substitute for byte freshness. |
| Worker Git reads | `src/GrayMoon.Worker/Abstractions/IRepositoryGitChangesService.cs`; `Services/GitChanges/GitCliRepositoryGitChangesService.cs`; `Services/LibGit2SharpLocalGitSnapshotReader.cs` | New compact evidence operation using existing safe Git access. |
| Worker queues | `src/GrayMoon.Worker/Queue/DiffJobQueue.cs`; `WorkerOptions.cs`; command dispatcher/handler | Bounded repository-scoped evidence request; do not block interactive reads. |
| Tests | `src/GrayMoon.App.Tests/WorkspaceCommitModeHelperTests.cs`, `src/GrayMoon.Worker.Tests/GitCliRepositoryGitChangesServiceTests.cs` and relevant manifest/PR tests | Targeted regression and integration tests. |

Keep namespace/project placement consistent with repository conventions and actual dependency direction, not this table if main has evolved.

## 2. Work package A — Freeze existing behavior and adaptive Commit UI

**Owner:** App UI / Blazor. **Dependencies:** none. **Merge separately before AI work.**

### Steps

A1. Document exact current states: no staged, some staged, mixed staged/unstaged across repos, no message, Worker disconnected, job running, Push committed checked, default-branch warning.

A2. Replace dual visible buttons with mutually exclusive keyed alternatives. Capture `OfferedCommitMode` and `PushAfterCommit` at render/click boundary; pass mode into current `CommitWorkspaceAsync` and never recompute mid-operation. Preserve `WorkspaceCommitModeHelper.IsRepositoryTarget` and `StageAllFirst` unchanged.

A3. Add explicit CSS class/data attribute to primary commit element; amend `git-changes-splitter.js` Ctrl/Cmd+Enter selector to target only that button. Test pointer handler rerender while watcher updates staged state.

A4. Keep existing button disabled styles, tooltips, no-wrap, overlay and push checkbox. Provide a placeholder contiguous `Commit | AI | chevron` styling primitive only if necessary for later phase; do not add functionality yet.

### Tests / gate

- Unit: staged/no staged/mixed repo target selection.
- UI: exactly one primary commit button, correct label, correct handler after render transitions, shortcut unaffected.
- Regression: no mode substitution with queued Blazor event, default-branch warning, push-only-succeeded behavior unchanged.
- Git Worker mutation code should show no production diff in this package.

## 3. Work package B — Manifest + local preference + provider discovery

**Owner:** Common/App configuration. **Can parallelize with C after shared contracts agreed.**

### Manifest

B1. Add optional `Ai` configuration model with provider list `{provider, model?}` to `WorkspaceManifest` and serialization.

B2. Validate stable IDs (`claude`, `codex`, `cursor`), no duplicates, length and safety of model strings; empty model = provider default. Follow existing forward-compatible handling of unknown providers (preserve unknown entries if parsed to avoid loss).

B3. Inspect **every** path that constructs/reconstructs `WorkspaceManifest` records, not merely serializer: save, sync, restore, preview, drift detection and tag/version-file updates. Preserve AI section when changing unrelated manifest properties.

B4. Explicit policy on schema version (stay v1 for optional compatible property if acceptable under current conventions; otherwise bump with backward compatibility tests). Do not risk older installed app silently removing AI settings; document this compatibility decision.

### Local preference

B5. Implement one provider pin per workspace in local GM state, using the same existing DB/session conventions as `WorkspaceOpenInRecentTools`, but a dedicated abstraction and storage record/value. Selecting a tool only pins it; removing unpins it. Pin shared between Changes and PR dialog.

B6. Implement a generation-capability catalog: IDs, icons, label, executable name(s), noninteractive availability, model flag support, structured output/limited tool support. **Cursor editor (`cursor`) is not proof Cursor Agent CLI (`agent`) exists.**

B7. Availability detection should run in the effective execution identity/environment, with bounded caching and explicit refresh; distinguish missing executable, unsupported capabilities and auth errors. Reuse existing icons and styling where possible.

### Tests / gate

- Parse/serialize optional AI list and absent AI section.
- Unknown providers/duplicates/unsafe model strings, stable ordering, repeatable bytes.
- Restore/rewrite leaves AI section intact.
- Local pin independently varies per workspace; no source-controlled pin; removing tool does not remove model settings.
- CLI discovery does not mistake Cursor editor for Agent CLI.

## 4. Work package C — Immutable Git comparison scope and evidence DTO

**Owner:** Application contracts / Git Worker architecture. **Dependencies:** existing commit-mode helper and PR selected-target model.

C1. Define compact serializable request: operation kind, workspace/context identity, repository identities, comparison mode, PR head/base refs, evidence policy and caps. Do **not** trust client-supplied paths blindly; resolve workspace repository paths server-side and validate.

C2. Define evidence result: per-repo source/base/head/merge-base OIDs, files and statuses, commit subjects when relevant, semantic fact records and excerpts with bounded lengths, coverage/truncation flags and per-repo errors. Result must **not** include whole files.

C3. For Commit, capture existing `OfferedCommitMode`, capture target repository IDs with `IsRepositoryTarget`, snapshot metadata and a generation request ID. Correct intent even when the UI changes while gathering data.

C4. For PR, capture each `NewPrTargetRepo.HeadBranch` and **current** modal selection from `BaseBranchByRepositoryId`. Do not take `ParentBranchName` as authoritative after user chooses a target. Treat a missing base as an error rather than using `DefaultBranch`.

C5. Provide pure validation of duplicate repo IDs, mixed context, unavailable/identical source/base, cancellation and byte/token budgets.

### Tests / gate

- PR head/base exactly equal existing CreatePullRequestRequest for every target repo.
- A release/feature parent is never rewritten to main.
- Mixed target branches across repositories supported.
- Commit scope preserves Staged/All even when UI status changes after click.

## 5. Work package D — Fast Worker-side evidence collector

**Owner:** Worker/Git. **Highest correctness/performance risk; test independently before AI integration.**

### D1. Data inventory and snapshot reuse

- Reuse already available `GitChangeSnapshot` when valid to enumerate changed paths and staged/worktree state; line stats may be null.
- For manual generation after watcher eviction, use a cheap authoritative validation/read on relevant repositories; never rely blindly on stale watcher data.
- Snapshot `Version`/`ScannedAt` is an invalidation hint, not a guarantee of byte consistency. Capture HEAD/index identity and a cheap relevant worktree validity signature; re-check when applying results.

### D2. Commit Staged evidence

- Compare **HEAD vs index** only (`git diff --cached` equivalent), including partial staging on an otherwise modified file.
- Exclude unstaged-only differences even when in the same changed path.
- Maintain existing repository participation: only repos with staged content.
- Validate rename/delete/type changes, binary/submodule, conflicts and unusual filenames.

### D3. Commit All evidence

- Compare **HEAD vs effective final working tree**, incorporating staged tracked state, unstaged tracked state, deletions and untracked files; do not sum cached and unstaged patches as if separate independent changes.
- Avoid reading unrestricted untracked contents; validate safe path and type, size, encoding and allowlist/exclude policy. Respect Git ignore result rather than scanning ignored trees.
- Handle race between `git add --all` during eventual commit and selected generation scope; warn when evidence becomes stale. Do not stage files to generate evidence.

### D4. PR comparison evidence (absolute rule)

- Resolve selected target/base and selected head in each repo, with unambiguous full refs and correct local vs remote tracking expectations. No implicit network fetch.
- Capture source OID, target OID, and merge-base OID; generate equivalent of `git diff <merge-base> <head>` and selected commit subjects (`target..head`) under caps.
- Exclude parent-only changes, default-only changes and dirty working tree.
- Unrelated histories, missing refs, shallow history, unpushed source and stale remote refs: explicit error/coverage indication, not default fallback.
- For Push & Create, use the local source HEAD that will be pushed. Recheck evidence freshness before UI apply/PR creation.

### D5. Cheap-first bounded extraction

- Phase zero: status inventory + known repo/branch/dependency metadata.
- Phase one: machine-readable name/status/numstat and bounded commit headings. Git paths with `-z` or equivalent; avoid ad hoc newline parsing.
- Classify paths and hunk relevance **before** allocating large patches. Set per-file read limits, bounded overall diff commands/output, and per-repo budget.
- Phase two: high-information additions/deletions and nearby structural context from selected files only. Extract `.csproj` package/project reference deltas without copying huge XML files. Include tests and changed assertions where cheap. Mechanical duplicates grouped across repositories.
- No Roslyn compilation in v1; optional syntax extraction only if verified cheap and accurate.
- A 100k-line diff must not result in 100k lines being loaded into RAM merely to trim to 4k prompt tokens afterward.

### D6. Execution / cancellation

- Batch processing at **repository level** inside Worker. One read request per repository (or one batch request fan-out), not one SignalR request per path.
- Use existing Git CLI process runner/read-lock lease semantics and short-lived LibGit2Sharp repository objects. Never introduce long-lived handles that interfere with worktree removal.
- Enforce bounded concurrent repository reads; benchmark default 8 against VDI storage contention. Avoid nested `Task.WhenAll` over all files.
- Inspect `DiffJobQueue` fairness; do not starve user file-diff preview by stuffing the interactive queue with long evidence jobs. Add a small evidence queue/lane only with measured justification.
- Stop quickly on cancellation, timeout and no-change state, and retain per-repo diagnostics.

### D7. Evidence unit tests and local Git fixtures

Real temporary Git repositories, with explicit parents:

1. staged-only; unstaged-only; partially staged same file; Commit All mixed state;
2. untracked ignored/nonignored, rename with modifications, deletion, binaries, large files, symlinks and merge conflicts;
3. nested `main → release → feature` with release-only commits excluded;
4. two repos targeting different nondefault bases;
5. branch advanced after evidence; missing target; ambiguous branch/tag; shallow clone/no merge-base;
6. ref not fetched, detached head, worktrees, paths with spaces/newlines;
7. cancellation during read and worktree removal;
8. 100k-line patch with bounded memory/bytes and bounded transport response.

**Gate:** Can inspect deterministic evidence JSON in a test/diagnostic mode, with sensitive content scrubbed; no model invocation necessary. Diff semantics match authoritative Git comparisons on fixtures.

## 6. Work package E — Semantic preprocessor, cross-repo aggregator and token budgeter

**Owner:** Pure Core/Application logic. **Depends on evidence schema; can parallelize with Worker implementation once schema fixed.**

E1. Add deterministic file category classification with explicit treatment of `.cs`, `.csproj`, `.props`, `.targets`, JSON/YAML, tests, docs, generated and binary files. Extract *facts and bounded literal evidence*, not fabricated intent.

E2. Add structured before/after package/reference changes and group identical version bumps across repo/project boundaries. Preserve counts, affected paths and exceptions.

E3. Rank meaningful changes using a policy: public APIs, implementation behavior, tests, dependency contracts and representative hunks. Avoid ranking solely by lines changed. Reserve a minimum quota for every materially changed repo.

E4. Assemble one prompt from all included repositories with: task, immutable scope details, per-repo base/head references (PR), aggregate highlights, evidence, omitted-data counts and JSON output schema. Commit requests must not mention PR comparisons and vice versa.

E5. Hard caps: initial ordinary target 1k–3k tokens for Commit, 3k–6k for PR; initial hard cap 4k Commit / 8k PR. Reserve instructions/output headroom; enforce limits before content materialization **and** at serialization. Provider-specific token estimates are approximate; err conservative.

E6. Build a safe compaction mechanism: choose top-ranked hunks under budget; collapse mechanical repeats; mark omissions and partial coverage. If insufficient evidence for truthful generation, return a specific not-enough-context response rather than guessing.

E7. Cache immutable PR evidence by repo+head OID+target OID+merge-base OID+policy version; cache mutable commit evidence only with sufficient freshness signature and event invalidation. Do not let cache live across changed index/worktree content.

### Tests / gate

- Deterministic output ordering, bounded bytes, stable repeated inputs, path escaping, no secret-bearing raw logs.
- A giant generated/lock file never overwhelms high-value source/test evidence.
- Ten repositories with identical package updates collapse correctly.
- Large PR receives meaningful evidence from each materially changed repository.
- Truncation coverage marker and refusal when evidence inadequate.
- Token-budget test does not require external API/model.

## 7. Work package F — AI provider adapters and isolated execution

**Owner:** AI infrastructure, not Git UI. **Can parallelize with E.**

F1. Define `IAiTextGenerator`, `IAiCliAdapter`, `AiProviderConfiguration`, `GeneratedGitMetadata` and structured errors. Adapters accept only a prepared prompt and optional model string, never a repo path.

F2. Implement Claude, Codex, Cursor adapters using *current verified* noninteractive CLI flags, safe `ArgumentList`, bounded stdin, structured JSON response. Model option emitted only when configured. Keep format parsing provider-specific and result DTO generic.

F3. Verify each CLI's no-tools/read-only constraints. CLI being print-mode is **not** proof it cannot run tools. Use an isolated temporary working directory and additional OS sandboxing/user-context restrictions as appropriate. Refuse providers for which reliable safe execution cannot be established.

F4. Check runtime identity: Desktop/Blazor App/Worker may run under different accounts or PATH. Select a user-context process host if that is necessary for installed provider authentication; keep the adapter contract unchanged.

F5. Timeouts, cancellation, kill process tree, max stdout/stderr, retry policy, auth/unsupported-model/CLI-not-found errors. Never store provider credentials in `graymoon.json`. No prompts/diffs/secrets in standard logs.

F6. Parse schema robustly. A Commit result contains nonempty subject and optional body; PR result contains title/Markdown description. Limit content lengths and reject unsafe surprises. Model text is data; never invoke Git or shell from it.

### Tests / gate

- Fake CLI executables: stdout valid JSON, invalid JSON, partial JSON, Unicode, huge stdout, slow process, cancellation, nonzero exit, missing binary, auth failure, unsupported model.
- Verify arguments passed as discrete values; malicious model/config input cannot inject shell options/commands.
- Check provider invocation count exactly one and no `git` child processes from AI adapters.
- User can still commit/create PR with AI offline.

## 8. Work package G — Shared connected AI UI component and integration

**Owner:** App UI. **Depends on A, B, C and F; use mocked evidence/provider while D/E mature.**

G1. Build a small reusable AI-selector component: one preferred tool, joined main AI segment + chevron, provider menu, pin/unpin, status/tooltip. Don't copy the entire feature-selector's multi-tool launching logic or build a big abstraction hierarchy.

G2. Changes: join **three parts with no visual gaps** `Commit Staged | ✨ provider | ▾`; use icon-only preferred segment at narrower widths. The primary button remains a distinct keyed element and alone carries Ctrl/Cmd+Enter. Dropdown overlays, does not expand layout height.

G3. Selecting tool pins only; clicking preferred AI segment calls GM evidence pipeline then provider once. With no pin, chevron helps choose. Loading spinner/status belongs inside AI segment and should not disable the existing Commit action. Error appears locally, not as a destructive form reset.

G4. Commit message result fills existing shared `_workspaceCommitMessage`. If user edited while inference was pending, offer Replace/Copy rather than overwrite. Keep existing message handling, memory, `Push committed` and existing warnings.

G5. PR: AI selector at the **right of Details/Branch/Review tab bar**, not in Create or Push & Create button. Preserve tab, reviewer and branch UI. Populate **one** `_title` and **one** `_body` only, from aggregate evidence. Selecting a target branch invalidates last generation fingerprint.

G6. Compare current UI generation ID, provider selection, text version and Git scope fingerprint at result time. Ignore/cancel responses for dismissed modals, changed workspace, changed feature context or stale selected targets. Never silently replace manual edits.

G7. Accessibility: tab order, keyboard activating AI segment/dropdown, screen reader labels including selected provider, focus management, menu Escape/outside-click, hover tooltips, compact responsive widths, light/dark theme and high DPI.

### Tests / gate

- Exactly one visible primary commit button, no separate AI gap, pinned segment optional.
- Dropdown selection **never** invokes AI; AI segment click invokes exactly once.
- Ctrl+Enter never targets AI or chevron.
- PR creation uses existing `form.Title`/`form.Body` shared for all requests.
- Switching selected base invalidates prior PR evidence; manual text stays intact.
- User edits during slow provider response preserved.
- Provider pin persists locally across navigation and stays out of manifest.

## 9. Work package H — End-to-end performance, quality and VDI validation

**Owner:** Integration/performance engineer. **Depends on A–G.**

H1. Instrument structured, non-sensitive spans:

```text
AI.Generate.Click → ScopeCapture → SnapshotResolve → GitMetadataRead
→ EvidenceExtract → Aggregate → PromptSerialize
→ CliProcessStart → ProviderInference → OutputParse → UiApply
```

H2. Metrics: total/p50/p95 latency, cache hit rate, participating repo count, changed file count, selected hunk count, raw eligible diff size estimate, fetched bytes, transferred bytes, estimated/actual provider tokens (when available), queue wait and subprocess start. No raw prompt content in telemetry.

H3. Benchmark profiles:

- Commit: 1 repo, 5 repos, 39 repos; staged/unstaged/mixed; warm/cold watcher caches.
- PR: 1/5/39 selected repos with differing selected target branches; nested release parent; parent advanced while source unchanged.
- Huge changes: 100k+ modified lines, generated/lock files, large new file, many small files.
- VDI: cold storage, heavy background sync activity, watcher lease expired, authenticated provider already installed.
- CLI choices: Claude/Codex/Cursor, missing model vs explicit model, provider error/cancel.

H4. Initial target: warm commit context <250 ms where practical; warm moderate multirepo PR context <1s; prompt within 4k/8k hard caps, one AI call. These are **targets**, not acceptance guarantees; record p50/p95 and explain outliers. End-to-end generation depends on chosen model/CLI. Measure actual prompt quality alongside speed.

H5. Quality evaluation: build a small golden change-set suite with expected factual elements for bug fixes, API work, dependency updates, CI changes, pure docs, cross-repo features and nested parents. Review hallucinations, missing important changes and misleading test claims. Tune deterministic preprocessor rather than adding AI summarization rounds.

H6. Regression: run full relevant App/Worker/Common test suites and build, review install/packaging behavior, verify no locked handles after cancellation and no unexpected worker queue delay.

## 10. Multi-agent ownership and sequencing

Suggested roles, each with **non-overlapping production file ownership**:

| Agent | Owns | Avoid editing |
| --- | --- | --- |
| UI/Commit | A: Changes UI and keyboard JS | Worker Git mutations |
| Manifest/Preferences | B: Common manifest and local pin service | Changes/PR pages |
| Git/Worker | C/D: Evidence contracts, Worker handlers, Git comparison | UI and provider CLI code |
| Preprocessing | E: pure classification/packing/budget/cache | Git mutation code |
| Provider Infrastructure | F: CLI adapters and execution host | Git evidence collector |
| UI/PR Integration | G: shared selector and PR modal after A | Core Git execution |
| Test/Perf | H: integration fixtures/benchmarks | Unrelated cleanup |

**Order:** Baseline → A → B+C in parallel → D with E and F developed against frozen DTOs → G integration → H validation. Merge each package with tests and review, not one huge speculative change. Avoid parallel edits to `Program.cs`, migrations and other shared registration files; designate one integration owner for DI wiring.

## 11. Living project documents

Maintain:

- `docs/ai/AI-Git-Metadata-Design.md` — decisions, invariants, diagrams, API surface and tradeoffs.
- `docs/ai/AI-Git-Metadata-Implementation.md` — table of work packages, owner, status (`Not started`, `In progress`, `Validated`, `Blocked`), PR/commit links, tests run, benchmark deltas, open risks.
- `docs/ai/AI-Git-Metadata-Performance.md` — measured VDI data and evidence quality results, with machine/spec details but no secrets.

Start with these documents as baseline, and update implementation status immediately when units are validated. Any scope/invariant change needs explicit design note.

## 12. Exit criteria

- [ ] Source compiled and all targeted tests pass.
- [ ] Commit All/Staged semantics unchanged; exactly one visible primary button; Blazor race safeguarded.
- [ ] `graymoon.json` supports multiple provider-specific models and survives restore/rewrite.
- [ ] Only one per-workspace locally pinned provider; select does not generate.
- [ ] Changes control is visually one contiguous component with Commit, AI and dropdown.
- [ ] PR AI selector sits on right side of tab bar; no Create/Push & Create modifications.
- [ ] Multi-repo Commit produces one message; multi-repo PR produces one title/body.
- [ ] Per-repo PR evidence compares exact selected parent/base, including nondefault and heterogeneous targets.
- [ ] GM collects bounded evidence itself; AI CLI performs zero Git discovery.
- [ ] Staged partial files and Commit All final working-tree content correctly represented.
- [ ] Token/byte caps enforced before full-patch transfer or materialization.
- [ ] Stale scopes/manual text edits never silently overwritten.
- [ ] Provider failure does not block manual commit or PR creation.
- [ ] Provider sandbox/no-tools assumptions tested, cancellation and process cleanup verified.
- [ ] 39-repo VDI benchmark and 100k-line stress test recorded with p50/p95, prompt size and quality samples.

## 13. Bootstrap coding-agent prompt

> Review the latest `main` code, repository architectural rules (`CLAUDE.md`, `AGENTS.md`), and the two `GrayMoon-AI-Generation-*` design documents before editing. Inspect existing changes snapshots, Worker diff/read queues, Git comparison services, workspace manifest parsing/rewrites, feature-selector pinning, New PR selected base mapping and adaptive Commit/keyboard handlers. Confirm implementation file ownership and create living design/progress docs. Implement **work package A only** first, with focused regression tests and no Worker Git mutation changes. Report the exact changed files, risks and tests. After A is validated, proceed through packages B–H with independently verifiable commits and no unrelated refactoring. Never let AI CLIs run Git; the Worker must prepare bounded evidence. Every PR repository comparison must use the exact source and selected target branch, never default fallback. Preserve one shared title/body across multi-repository PRs and one shared commit message across workspace commits.
