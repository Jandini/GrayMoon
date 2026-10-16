# GrayMoon — AI Git Metadata Generation: Final Architecture & UX Design

**Status:** Approved design baseline for implementation planning; no source changes performed  
**Reviewed:** GitHub `Jandini/GrayMoon`, `main`, 16 October 2026  
**Scope:** Fast, accurate, user-initiated generation of one workspace commit message and one shared multi-repository PR title/description via installed Claude Code, Codex, or Cursor CLI.  
**Primary principle:** **GrayMoon reads Git and prepares a bounded factual evidence packet; AI generates text only.**

## 1. Locked product decisions

1. The workspace manifest is **`graymoon.json`**, not `.graymoon.json`.
2. AI configuration contains a **list of providers**, each with its own optional model; no `pr`, `commit`, or `auto` switches.
3. AI generation is **manual**: only an explicit AI-segment click starts inference. Selecting a provider in the dropdown **pins it without invoking AI**.
4. The **Changes** toolbar has **one adaptive Commit button** (Commit Staged when any repository has staged changes; otherwise Commit All), visually joined into a **single connected control**: `Commit Staged | [preferred AI] | ▾`.
5. Only **one AI provider may be pinned** in a workspace on a particular machine. The pin is **local GM state per workspace**, not source-controlled in `graymoon.json`.
6. The **PR dialog** keeps Create and Push & Create untouched, with an AI/provider dropdown at the **right side of the tab bar**.
7. A single workspace commit operation always receives **one shared generated commit message**, not one message per repository.
8. A multi-repository PR operation receives **one shared generated title and one shared description**. All PRs receive the same metadata, exactly as the current modal and create workflow do.
9. For **each repository**, PR evidence must compare **its selected source head with its actual selected parent/target base**; the parent can be any branch. It is **never assumed** to be the default branch.
10. GM performs every Git operation and context-preparation step. Claude/Codex/Cursor receives **a complete prebuilt prompt** and has **no need or permission** to inspect workspaces or run Git.
11. No direct API implementation in v1, but the provider abstraction must allow it later without changing Git evidence or UI workflows.
12. This feature must not change staging, commits, pushes, PR creation, worktree behavior, Git synchronization, or watcher lifecycle.

## 2. Evidence confirmed in `main`

| File | Finding | Design implication |
| --- | --- | --- |
| `src/GrayMoon.App/Components/Pages/WorkspaceGitChanges.razor` | Two commit buttons when staged content exists; comments document Blazor handler-identity race and use separate keyed elements. | Replace with one **visible** keyed alternative, preserving captured mode, disabled state and shortcut semantics. |
| `src/GrayMoon.App/Services/GitChanges/WorkspaceCommitMode.cs` | `ResolveOfferedMode`, `StageAllFirst`, `IsRepositoryTarget`, `ButtonTitle` exist. | Reuse unchanged. Staged scope is workspace-wide and skips repositories without staged changes. |
| `src/GrayMoon.App/Components/Pages/WorkspaceGitChanges.MultiRepo.cs` | Shared message, bounded repository fan-out, default branch warning and optional successful-only push. | Keep execution logic unchanged; AI only fills `_workspaceCommitMessage`. |
| `src/GrayMoon.App/wwwroot/js/git-changes-splitter.js` | Ctrl/Cmd+Enter clicks the first enabled button in the commit-buttons container. | Scope shortcut to an explicit primary-commit selector, never the AI segment. |
| `src/GrayMoon.App/Components/Modals/NewPullRequestModal.razor` | One `_title`, one `_body`, and per-repository selected base; tabs Details/Branch/Review. | AI creates one shared title/body; selector belongs at tab-bar right. |
| `src/GrayMoon.App/Components/Modals/NewPullRequestModels.cs` | `ParentBranchName` is provenance suggestion; `BaseBranchByRepositoryId` is selected PR base. | **Selected base** is authoritative. Parent suggestion or default is not authoritative after user selection. |
| `src/GrayMoon.App/Components/Pages/WorkspaceRepositories.PullRequests.cs` | Each creation request gets `form.Title`/`form.Body`, its own `t.HeadBranch` and base from `form.BaseBranchByRepositoryId`. | Match the source/base used in creation; do not add per-repo text. |
| `src/GrayMoon.Common/Manifest/WorkspaceManifest.cs` and `WorkspaceManifestFile.cs` | `graymoon.json` schema v1, structured serialization and parsing, byte-stable output. | Add optional AI property without losing it on restore/rewrite/drift handling. |
| `src/GrayMoon.Common/Git/GitChangeModels.cs` | `GitChangeSnapshot` tracks version, branch, HEAD, staged and worktree flags, optional line stats. | Reuse valid snapshots as file inventory, not as proof that file contents remain unchanged. |
| `src/GrayMoon.Worker/Services/GitChanges/GitCliRepositoryGitChangesService.cs` | Status and diff APIs; diff document can contain complete before/after contents. | Build a new **bounded evidence** result instead of shipping entire files to the App. |
| `src/GrayMoon.Worker/Queue/DiffJobQueue.cs` | Separate diff queue and concurrency bound. | Investigate reuse for repository-level evidence requests, protect interactive diffs from saturation. |
| `src/GrayMoon.Worker/Services/LibGit2SharpLocalGitSnapshotReader.cs` | In-process Git reads with short-lived repository handles and removal leases. | Reuse patterns where equivalent; benchmark native CLI vs libgit2sharp, do not refactor all Git operations. |
| `src/GrayMoon.App/Components/Features/FeatureOpenInTools.cs`, `Services/Ui/WorkspaceOpenInRecentTools.cs` | Known tool IDs and local per-workspace pinned-tool persistence pattern. | Reuse UI conventions, not the launch-command abstraction nor multi-pin state. |
| `src/GrayMoon.App/Services/Ui/InstalledOpenInTools.cs` | Existing installed-app detection covers Claude/Codex and Cursor editor. | AI capability must verify **headless CLI** availability separately, especially Cursor `agent` vs editor `cursor`. |

**Review boundary:** These observations are from retrieved `main` source, not a local compile or benchmark. Revalidate against `main` HEAD at implementation start.

## 3. Configuration model

Example **section** embedded in the existing workspace `graymoon.json` (not a replacement for the rest of its manifest):

```json
{
  "ai": {
    "providers": [
      { "provider": "claude", "model": "sonnet" },
      { "provider": "codex", "model": "<codex-model-id>" },
      { "provider": "cursor", "model": "<cursor-model-id>" }
    ]
  }
}
```

- Models are provider-specific opaque strings; placeholder IDs above must be replaced with supported IDs on the installed CLI. No global model enum or hardcoded constantly-changing model list.
- `model` absent/null/empty means **provider CLI default**; do not send a model flag in that case.
- `ai` or `providers` missing means **no explicit per-provider overrides**. Installed, supported CLI tools can still be selected and use their defaults; no auto-generation.
- Stable provider IDs: `claude`, `codex`, `cursor`. Reject duplicate IDs, malformed lists, unsafe or unreasonably long model strings, and unknown IDs with an actionable message. Whether unknown future provider IDs are preserved or rejected should follow established manifest compatibility policy; **never discard an unknown list entry in a rewrite**.
- Preserve the new section during serialization, restore, workspace definition sync, drift reconciliation and subsequent unrelated manifest changes. Keep property ordering, LF, trailing newline, current schema compatibility rules. Bump schema only if established schema policy requires it; document the choice and tests.
- `graymoon.json` is portable and may be checked in. Never store credentials, auth state, executable paths, CLI history or pinned local provider in it.
- Local per-workspace AI preference stores at most one pin (`providerId`); it may differ across computers, and user choice takes precedence over any manifest ordering. No requirement for separate PR and Commit pins.
- Provider not installed, not authenticated, unsupported model, or CLI capability mismatch: surface a compact message; never silently switch provider or model.

## 4. UX design

### 4.1 Changes: one joined segmented control

Visual concept: **`[ Commit Staged ][ ✨ Claude ][ ▾ ]`**, with contiguous border, common height/radius, internal dividers and no gaps. The pinned tool segment may display icon only at narrow widths, with an accessible provider label/tooltip. The dropdown should overlay content without changing the toolbar height or clipping behind the Changes pane.

- **Primary segment:** executes existing commit operation, not AI. Label is `Commit Staged` iff *any* repository has staged content, otherwise `Commit All`; labels never wrap.
- **Middle AI segment:** invokes the pinned available provider once to generate or replace the **shared** commit textarea message. A generation preview/confirmation is required if the user already edited message text, so no silent data loss. No staging, committing or pushing.
- **Chevron segment:** opens tool list; selecting a tool pins it and closes dropdown, **no generation**. An explicit remove/unpin action removes the middle segment while preserving primary + chevron. Show only headless-capable installed AI CLIs; unavailable configured tools may appear disabled with explanation.
- Preserve `Push committed` checkbox, existing disabled conditions, job overlay, default branch warning, keyboard shortcut and worker state. The AI segment has its own loading/error states and must not disable the primary Git action unless existing conditions require it.
- Keyboard: Ctrl/Cmd+Enter always targets the primary commit segment, never AI or dropdown. The explicit selector must remain stable under responsive widths.
- **Blazor identity safeguard:** render mutually exclusive keyed primary-commit alternatives, with a captured mode passed to `CommitWorkspaceAsync`. Do not dynamically reuse one handler with a mode that could change between render and dispatch. Regression-test watcher events during pointer/key clicks.

### 4.2 PR dialog: selector at right end of tab row

- Keep Details / Branch / Review tabs, Create, and Push & Create exactly as current UX.
- Add joined `[ ✨ preferred ][ ▾ ]` selector to the right of the tabs; same provider pin and model settings as Changes. An AI click generates **one** title and **one** Markdown description from the selected repository set.
- The selected base branch in the Branch tab is authoritative. If the user switches a base branch, invalidate any prior PR evidence and mark generated text potentially stale; next generation collects the new comparison. Do not automatically change user-edited text.
- Generate even when Details is not active only if selected target branches are valid; populate Details and show result there without losing reviewer selections, draft checkbox or branch settings.
- On generating over edited title/description, do not overwrite without an explicit replace/preview choice. Preserve existing PR template behavior when description is intentionally left blank.
- For partial comparison errors, do not misrepresent a partial summary as complete. Prefer a clear “cannot generate complete shared PR description” indication with missing repositories; permit explicit partial generation only if UX supports clear disclosure.

### 4.3 Provider pin semantics

- One pin per workspace per machine shared across both surfaces. Dropdown selection changes pin but **does not run** generation.
- Unpinned state shows primary Commit + chevron in Changes, and provider selection affordance in PR. Clicking dropdown can pin an installed provider.
- First visit with no local pin: do not silently trigger AI. May use a manifest provider as visual suggestion, but keep selection explicit and deterministic.
- Provider list installation checks must distinguish actual headless generation availability from “Open in Cursor/Claude/Codex” launch availability.

## 5. End-to-end architecture and ownership

```text
Existing Changes / PR UI
    ├── capture immutable Git operation scope (App)
    ├── AI provider preference/config resolution (App + Common)
    └── request WorkspaceGitEvidenceBatch (Application contract)
             └── Worker: bounded per-repo evidence collection
                 ├── reuse valid GM snapshot and refs
                 ├── short Git metadata reads (index/tree/commit history)
                 ├── bounded selective hunks / structured config deltas
                 ├── deterministic classification + context limits
                 └── return compact repository evidence + completeness markers
    └── App or Worker aggregation: deterministic cross-repo evidence packing
    └── AI provider execution gateway (isolated CLI process)
             ├── Claude adapter
             ├── Codex adapter
             └── Cursor adapter
    └── strict response parse/validation
    └── apply result to existing editable text fields (if still current)
```

**Ownership boundaries:**

- **Common:** serializable config, evidence/result DTOs and pure domain rules when shared between processes.
- **Application:** contracts, immutable scope and orchestration interfaces; no direct dependency on Razor or specific CLI executable.
- **Worker:** read-only Git access; use existing Worker-side Git leases, cancellation, command lanes and path validation. Prefer Worker-side AI CLI execution only after verifying the Worker identity has access to the user's configured CLI auth; otherwise use a separate **user-context process host** while keeping the provider interface unchanged. Never log prompts/diffs by default.
- **App:** selected scope, local preference persistence, UI state, result application, integration with existing workflows. Never run `git diff` in Razor.
- **Provider adapters:** invoke CLI with argument lists and stdin; never examine source repos, stage, commit, push, fetch, or inspect `.git`.

Recommended contracts (indicative; adapt names to GM's established interfaces):

```csharp
public enum GitMetadataKind { Commit, PullRequest }
public enum GitCommitEvidenceMode { Staged, All }

public sealed record RepositoryComparisonScope(
    int RepositoryId,
    string RepositoryPath,
    string HeadRef,
    string? TargetBaseRef,
    GitCommitEvidenceMode? CommitMode);

public sealed record GitEvidenceBatchRequest(
    GitMetadataKind Kind,
    IReadOnlyList<RepositoryComparisonScope> Repositories,
    int MaxEvidenceBytes,
    string PolicyVersion);

public interface IGitChangeEvidenceService
{
    Task<GitEvidenceBatch> CollectAsync(
        GitEvidenceBatchRequest request, CancellationToken cancellationToken);
}

public interface IGitEvidencePromptBuilder
{
    PreparedAiPrompt Build(GitEvidenceBatch evidence, GitMetadataKind kind, AiPromptBudget budget);
}

public interface IAiTextGenerator
{
    Task<AiStructuredResult> GenerateAsync(PreparedAiPrompt prompt, AiProviderChoice provider,
        CancellationToken cancellationToken);
}
```

The contracts are *shapes*, not a mandate to make separate services for every trivial transform. Avoid one-file-per-record fragmentation; cluster related DTOs.

## 6. Commit evidence: exact adaptive scope

### 6.1 Snapshot and mode

At invocation, capture (a) `WorkspaceCommitMode` from `WorkspaceCommitModeHelper.ResolveOfferedMode`, (b) relevant repository IDs, (c) current context/worktree identities, (d) known snapshots, (e) whether Push committed is checked **only for UI context**—never allow AI to change it.

`WorkspaceCommitModeHelper.IsRepositoryTarget` is authoritative for participation:

- **Staged:** only repositories with staged changes; include **only HEAD → index** content. Ignore all unstaged hunks, even on a file partly staged.
- **All:** repositories with changes; describe the effective **HEAD → working-tree** result, plus bounded untracked-file evidence (and staged deletions where files are absent). Do not concatenate separate staged and unstaged patches, which double-count files and may present contradictory versions.
- Unborn HEAD, partial staging, renamed/deleted/untracked paths, submodules, binary files, symlinks and conflicts require explicit handling. If the actual commit cannot proceed because of unresolved conflicts, generation must not suggest otherwise.

**Race guard:** The prepared evidence is based on an immutable snapshot of intent, but disk bytes can change. Capture HEAD/index identity and file metadata around reads; use short-lived read leases; invalidate stale generation when relevant changes occur. The UI must never silently overwrite new text after a late response. Snapshot version alone is not sufficient proof of stable file content.

### 6.2 Best fast-path data

1. From GM status snapshot: staged flag, worktree flag, paths, HEAD, per-repo ID, rename and available totals (not all snapshots contain line stats).
2. For target paths only, collect tiny machine-readable file statistics and selected diffs; do **not** re-run full status unless snapshot is unavailable/stale or authoritative verification is needed.
3. Build an evidence inventory before fetching hunks: identify high-value app/test/config files, mechanical duplicates, oversized paths and binaries.
4. Get selective content under per-file/per-repo/global byte caps. Untracked content must be read read-only with safe path validation, size/encoding checks and minimal excerpts.
5. Aggregate across repos and send only one prompt to one provider.

## 7. PR evidence: source against actual parent/target branch

**Nonnegotiable invariant:** Each repository's evidence must compare the exact **source head and selected target/base branch** that GM will use to create that repository's PR, not a default branch inferred later.

1. Capture `NewPrTargetRepo.HeadBranch` and the dialog's `BaseBranchByRepositoryId[repoId]` for every selected repository. `ParentBranchName` is merely a provenance suggestion used to initially propose a base. After target selection, **the selected base wins**.
2. Resolve the source and target to immutable commit OIDs in the correct local worktree/repository; use full refs where possible and avoid accidental tag/ref ambiguity. If PR targets remote branch tips, prefer the corresponding appropriate remote tracking refs, and surface when local history is missing/stale. Do not implicitly fetch during AI generation.
3. Compute merge-base (or provider-consistent comparison base), then the equivalent of `git diff <merge-base> <head>` / `git diff <target>...<head>` and `git log <target>..<head>` for commit subjects, ensuring comparison semantics match GitHub PR behavior. Note: commit listing and three-dot file comparison answer different questions.
4. Do not include uncommitted working-tree changes, target-only commits, prior parent history or unrelated default-branch changes. A feature whose parent is `release/2.0` **must not** be compared with `main`.
5. Different repositories may target different branches. Collect per-repo comparisons independently in bounded parallel, then combine into a **single** shared prompt and return a **single** title/body.
6. If source/base cannot be resolved or no merge-base exists: return a specific per-repo error and explicit coverage status. **Never fallback to default branch.** If local refs are stale, offer existing explicit Fetch/Refresh workflow rather than silently changing refs or generating wrong evidence.
7. Invalidate prepared evidence when any selected base changes, source or target commit changes, selected repositories change, or generation policy changes. Re-check before applying result or creating PRs; display stale indication, not destructive replacement.
8. If Push & Create will push additional local commits, use local source HEAD and the selected target ref; avoid describing a remote branch that has not yet received those commits. If HEAD changes before push/creation, invalidate generated evidence.

### Nested-branch acceptance example

```text
main ------ M1 ---- M2
        \
         release/2.0 ---- R1 ---- R2
                                   \
                                    feature/BAM-2874 ---- F1 ---- F2
```

The PR targets `release/2.0`. Evidence is **F1 + F2 relative to the release merge-base** and must exclude R1/R2 and M1/M2. For multi-repo PRs, repeat with each repository's selected target even if branch names differ.

## 8. GM-owned evidence intelligence: maximize meaning per token

### 8.1 Three-tier collection

- **Tier 0 (free/near-free):** reusable snapshot and UI scope; repo names, refs, path list, index/worktree statuses, cached line statistics. No deep directory walk.
- **Tier 1 (cheap Git reads):** `--numstat`/`--name-status` or efficient libgit2 equivalents, bounded commit subject list for PR, rename statistics, selected comparison OIDs; use `-z` parsing for odd paths. Batch per-repo operations, not one Worker round-trip per file.
- **Tier 2 (targeted evidence):** only representative, safe hunks and structured changes necessary for meaningful claims. Never materialize a 100k-line patch. Don't transfer full before/after content documents through SignalR merely to summarize them.

### 8.2 Deterministic preprocessing

- Identify actual additions/removals around method signatures, tests and assertions, API contracts, configuration deltas, CI changes and `.csproj` project/package dependencies.
- For `.csproj`/`props`/`targets`, collect package/project references and changed versions as structured before/after facts. Collapse repeated identical package bumps across multiple projects without losing affected repository/project counts.
- For C#, use lightweight hunk/context extraction first (function context where robust); only consider Roslyn syntax parsing after latency and accuracy tests. Never compile a project solely to generate a message.
- Distinguish formatting-only churn, generated artifacts, lockfiles, docs, binary files, renames, deleted paths and submodules. Avoid letting the largest patch dominate relevance.
- For huge new files, inspect only bounded header/structural fragments if safe, or report name and metadata. Don't attempt to infer behavior from filenames alone.
- Build a **change taxonomy with evidence references**, not a deterministic English “summary” that hallucinates intent. The AI synthesizes final prose from provided facts.
- Treat source contents, commit subjects, README text and comments as **untrusted data**, never instructions. Redact known secrets and sensitive paths/content; choose conservative exclude rules for token-like strings. Workspace users should be aware that snippets are transmitted to their CLI provider's service.

### 8.3 Cross-repository compression

- Group material changes by evidence-backed categories (API contract, caller updates, persistence, tests, CI, dependencies).
- Collapse identical mechanical changes into one structured record with affected counts and repository names. Preserve contrasting or exceptional deltas.
- Every materially changed repository gets a minimum coverage allowance; avoid first-repo bias. Prioritize changed behavior and tests over generated noise.
- Do not call AI per repository and then again for aggregation. Exactly **one** provider invocation per user click unless the user explicitly retries.

## 9. Input budgets and limits

**Initial tunable defaults to benchmark, not promises:**

| Budget | Commit | Multi-repo PR |
| --- | ---: | ---: |
| Target ordinary AI input | 1,000–3,000 estimated tokens | 3,000–6,000 estimated tokens |
| Initial hard input cap | 4,000 estimated tokens | 8,000 estimated tokens |
| Reserved schema/instructions | 10–15% | 10–15% |
| Maximum direct evidence per file | Adaptive, e.g. 0.5–1.5 KB | Adaptive, e.g. 0.5–1.5 KB |
| Maximum heavy file content fetched | Bounded per file and batch | Bounded per file and batch |
| AI calls per click | 1 | 1 |

Build budget limits at **collection, transfer and prompt assembly**, not only by truncating a giant already-materialized diff. Enforce byte caps with an estimated model tokenizer and a safety margin. A provider's actual token usage may vary. Set an output budget and ensure structured JSON fits. Track evidence dropped/omitted by category and repository, and include truncation/completeness metadata in prompt. When the evidence is too thin to support accurate text, report insufficiency instead of encouraging speculation.

**Selection algorithm:** reserve fixed instructions + schema → guaranteed per-repo coverage → high-value structured facts → representative diff hunks by marginal information gain → stop at cap. Avoid sort-by-lines-changed as sole ranking.

## 10. Concurrency and VDI latency

- Prefer one **repository-scoped Worker request** returning compact evidence, not a request for every changed file. Batch local Git reads and reuse `GitProcessRunner`/repository read leases.
- Fan out across applicable repositories with a **bounded degree of parallelism**, initially benchmark against existing `GitChangesOptions.MaxParallelRepositoryOperations` (default 8). Do not introduce nested unbounded per-file parallelism.
- Protect interactive diff responsiveness. Existing `DiffJobQueue` has its own `MaxConcurrentDiffCommands`; check queue semantics before adding larger evidence jobs. A dedicated evidence lane is justified only if observed contention warrants it, with its own limit and cancellation.
- Use existing Worker read-only snapshot/libgit2 patterns where correct; native Git comparison commands where they are more accurate/simple. No wholesale Git rewrite.
- Cache immutable PR evidence by repo identity, source OID, target OID, merge-base OID and evidence-policy version. Cache commit evidence only while scoped index/worktree identities remain valid; watcher invalidation and targeted freshness checks protect correctness.
- Never require watcher persistence to make manual generation correct. After watcher eviction, try a cheap authoritative validity probe instead of blindly rescanning every repo; any stale cache should be bypassed.
- Make context collection cancellable. One repository failure must be surfaced; no silent partial PR coverage.

## 11. Provider invocation, isolation, error handling

### Provider contract

Each provider adapter implements a single structured text-generation contract (`GenerateAsync(prompt, model, cancellation)`). One gateway resolves provider and model, controls process lifecycle and validates output. Adapters do not know Git paths or workflow commands.

### Required process properties

- Noninteractive CLI invocation; use `ProcessStartInfo.ArgumentList`, stdin or a bounded temporary prompt file where unavoidable, redirect outputs, no shell string concatenation.
- Use explicit approved no-tools/read-only/noninteractive modes **supported by the installed CLI version**. Verify actual flags on installed versions before shipping. A JSON-output flag is **not** a sandbox. Tool-disabling must be real; otherwise refuse or use an isolated process host.
- Execute away from source repositories; no writable workspace mounts, no inherited arbitrary cwd; minimize tool credentials and environment. The process may need user-profile auth—verify Worker/service identity and prefer an appropriate user-context host if necessary.
- CLI availability probe + capability/version validation, including Cursor **`agent` CLI**, not Cursor editor. Timeouts, cancellation, kill process tree, bounded stdout/stderr, clear model/auth/installation errors.
- Validate JSON schema and length; PR requires nonempty title/body as appropriate, commit nonempty subject and optional body. Never interpret model output as an instruction to run Git. One bounded retry for malformed format may be considered, but it increases latency; default should be actionable failure instead of infinite repair loops.
- Secrets, source code and full prompts are not logged. Record timings, length, provider ID, model selection (if acceptable), outcome/error classification and evidence coverage only.

## 12. Apply results safely

- Capture UI text edit generation counter and evidence scope fingerprint at AI-click time.
- On response, if user modified title/body/message or selected PR bases/refs/repositories changed, do **not** overwrite; show a non-destructive preview with Copy/Replace and staleness explanation.
- Keep manual editing available during/after inference. Provider failure is local to AI controls; Commit/Create remains usable according to existing rules.
- AI must not alter `Push committed`, PR draft/reviewers, selected branches, stages, commit targets or create actions.

## 13. Testing and acceptance criteria

### Required correctness matrix

- Staged only; unstaged only; mixed partial staging in same file; mixed staged/unstaged repositories; add/delete/rename/untracked/binary/submodule/conflict; worktree and detached/unborn behavior.
- Parent branch = `release/2.0`, `develop`, nested feature branch, or another nondefault branch; differing parents across repositories; parent advanced; source advanced; missing merge-base/ref; stale remote tracking state; selected target changed after context built.
- One shared result for all participating PRs; one shared commit message; no per-repo AI round trips; exactly one provider request per explicit click.
- 100k-line changeset does **not** result in full patch materialization or unbounded output. Budget enforcement operates before large content transfer.
- Blazor handler identity and watcher mid-click race; Ctrl/Cmd+Enter commits, never generates; pinned provider persistence and cross-machine portability.
- CLI absent/auth expired/unsupported model/blocked tools/timeout/cancel/bad JSON/huge stdout; edited-text protection; Worker path removed midway.
- Manifest parse → serialize → restore → reconcile → rewrite preserves providers/models and existing fields.

### Proposed performance targets, subject to enterprise VDI validation

- Warm commit evidence preparation: **aim <250 ms**, excluding model invocation and unusual large files.
- Warm multi-repo PR evidence preparation: **aim <1 second** for moderate scopes, excluding remote fetch and model invocation.
- No model-side Git discovery or source scanning; zero write commands during evidence collection.
- Benchmark cold/warm, 1/5/39-repo cases, watcher evictions and 100k-line changes. Revisit budgets/concurrency based on p50/p95, token use and generated-text quality, rather than optimizing only median local-machine time.

## 14. Out of scope for v1

Direct APIs, local LLM inference, auto-generation on dialog open, AI-written per-repo commit messages or PR bodies, AI staging/committing/pushing, automatic fetch, full Roslyn semantic compilation, provider-specific repository browsing, general-purpose agent orchestration and a new global background scheduler.

## 15. Final acceptance statement

**GrayMoon captures the user's exact intended Git scope, prepares bounded evidence quickly and safely, and makes one isolated text-generation call. For PRs, every repository's evidence is measured against the actual selected parent/target branch, never implicitly against default. The output is one shared editable commit message or one shared editable PR title/description. Existing Git execution semantics and UX safeguards remain intact.**
