# GrayMoon - AI Git Metadata Generation: Implementation Log

**Companions:** `GrayMoon-AI-Generation-Design.md`, `GrayMoon-AI-Generation-Implementation-Plan.md`
**Status:** Work packages A-G implemented and tested; H measured locally (not on VDI). Branch `ai-assisted-pull-commit`, uncommitted.

## 1. What was built, by work package

| Package | Status | Where |
| --- | --- | --- |
| A - one adaptive Commit button | Validated | `WorkspaceGitChanges.razor`, `git-changes-splitter.js` |
| B - manifest `ai`, local pin, provider discovery | Validated | `Common/Manifest/*`, `Common/Ai/AiProviderConfiguration.cs`, `App/Services/Ai/AiProviderServices.cs`, `Migrations.cs` step 10 |
| C - immutable scope and evidence DTOs | Validated | `Application/Ai/IWorkspaceAiGenerationOperations.cs`, `Common/Ai/GitEvidenceModels.cs` |
| D - Worker evidence collector | Validated | `Worker/Services/GitEvidence/GitEvidenceCollector.cs`, `Worker/Commands/CollectGitEvidenceCommand.cs` |
| E - classification, facts, packing, budget | Validated | `Common/Ai/GitEvidenceClassifier.cs`, `ProjectFileFacts.cs`, `EvidenceRedactor.cs`, `AiPromptBuilder.cs` |
| F - CLI adapters and isolated execution | Validated (Claude, Codex); Cursor per documentation only | `Worker/Services/Ai/*`, `Common/BoundedProcessRunner.cs` |
| G - shared control, Changes and PR integration | Implemented; layout checked by screenshot, not in a live App | `Components/Ai/*`, `WorkspaceGitChanges.Ai.cs`, `NewPullRequestModal.Ai.cs`, `wwwroot/css/ai-generation.css`, `wwwroot/js/gm-ai-menu.js` |
| H - performance and quality | Measured on one developer machine | section 5 |

## 2. Architecture as built

```text
Changes / New PR dialog (App, Blazor)
  capture scope: commit mode + target repos, or per-repo source + selected target
  IWorkspaceAiGenerationOperations (Application contract, App implementation)
    1. provider + model: graymoon.json ai.providers, else the fast default; CLI availability (cached probe)
    2. Worker CollectGitEvidence   one request for all repositories, bounded fan-out
    3. AiPromptBuilder (Common)    one prompt under a hard token budget
    4. Worker GenerateAiText       one CLI process, no tools, empty temp folder
    5. AiGeneratedTextParser       strict JSON shape and length checks
  AiTextApplyPolicy: apply into an untouched empty field for an unchanged scope, otherwise suggest (Replace / Copy / Dismiss)
```

The App never runs git or a CLI. The Worker runs both, because it runs as the user (Docker mode has no other place with the user's CLI sign-in) and already owns repository leases. The provider adapters receive only prompt text and an optional model; no adapter sees a repository path.

## 3. Decisions and deviations from the plan

1. **Worker hosts the CLI.** Design section 5 allowed Worker or a user-context host. The Worker already runs as the signed-in user (service logon account or console), and in Docker mode it is the only process on the host, so no extra host was added. Verified: the Worker refreshes PATH from the user environment at start (`HostEnvironmentPath.RefreshProcessPath`).
2. **Two Worker commands, not one.** `CollectGitEvidence` and `GenerateAiText` are separate so the prompt is built in Common (testable, provider-neutral) and a future direct-API provider can replace step 4 without touching evidence or UI.
3. **Default models.** Changed after review: with no model in `graymoon.json`, GrayMoon uses Claude `haiku` and Cursor `grok-4.7-low-fast` (commit and PR text from prepared evidence is a small task; strong models cost more for no gain). Codex keeps its CLI default because no small model is accepted by every account (ChatGPT accounts reject `gpt-5.1-codex-mini` and `gpt-5-mini`, verified). An explicit model always wins.
4. **Manifest schema stays version 1.** Following the `codegraph` and `versionFiles` precedent: an optional, file-owned section. An older GrayMoon that rewrites the file would drop `ai` (as it would drop `codegraph`); bumping the schema would instead make every older GrayMoon refuse the whole file, which is worse. `WriteAuthoritativeManifestAsync` copies `ai` from the file; drift ignores it.
5. **Unknown and duplicate providers are kept, validated at use.** A typo in `ai` must not make the whole definition unreadable (restore, drift, tag pins). Structural errors (an entry without `provider`, `providers` not a list) still fail the parse like other sections. Duplicates and unsafe models disable only that provider with a message naming the fix; unknown ids show a notice.
6. **Pin is a column** (`Workspaces.PinnedAiProviderId`, strict migration step 10), not a new table: one value per Workspace.
7. **All-or-nothing evidence.** If any repository cannot be compared (missing target ref, no merge base, no commits ahead, conflicts), nothing is generated and the message names the repositories. The plan allowed explicit partial generation; it was not added, because a shared description that silently leaves a repository out is wrong for that repository.
8. **Target ref is `refs/remotes/origin/<selected base>`**, the branch GitHub compares against; the source is `refs/heads/<head>`. Full refs, so a tag of the same name is never used (tested). No fetch; a missing ref tells the user to Refresh the branches.
9. **Bounded process runner.** `ICommandLineService` buffers all output and logs arguments at Debug, which is wrong for diffs and prompts. `BoundedProcessRunner` (Common) takes an argument list, stops a process once its stdout cap is reached, logs nothing but executable, timing and sizes, and runs behind the same repository-access lease as every other Worker process (`RepositoryAccessBoundedProcessRunner`; the lease code is now shared in `RepositoryAccessGuard`).
10. **No new Worker queue.** Evidence collection is one job in the main pool with its own bounded fan-out (`GitChangesOptions.MaxParallelRepositoryOperations`), so interactive diffs (diff pool) are not affected. A dedicated lane was not justified by measurement.
11. **Staleness signal.** The commit scope fingerprint is per target repository: HEAD, staged and changed counts, and the Worker scan time. Any watcher-driven rescan changes it, so text written while files moved is offered as a suggestion instead of applied. The PR fingerprint is the set of (repository, source, selected target).
12. **Outside-click closing uses JS.** The commit area is a CSS query container, which contains fixed-position elements, so a full-screen backdrop cannot work there; `gm-ai-menu.js` listens for presses outside the control instead.

## 4. Provider CLI invocation (verified against installed versions)

| Provider | Verified on | Invocation | Isolation |
| --- | --- | --- | --- |
| Claude Code `claude` | 2.1.301 | `-p --output-format json --tools "" --safe-mode --strict-mcp-config --no-session-persistence --permission-prompts none --system-prompt-file <tmp>/system.md --json-schema <schema> [--model m]`, evidence on stdin | No tools; no CLAUDE.md, hooks, skills, plugins or MCP; answer read from `structured_output` |
| Codex `codex exec` | 0.162.1 | `--ignore-user-config --ignore-rules --ephemeral --skip-git-repo-check --sandbox read-only --disable shell_tool --disable unified_exec ...(other tool features) -c web_search=disabled --output-schema <tmp>/schema.json -o <tmp>/answer.json -C <tmp> [-m m] -`, prompt on stdin | Verified that the model reports no shell tool; user config, rules and MCP servers not loaded |
| Cursor Agent `agent` / `cursor-agent` | not installed here; per Cursor CLI reference | `-p --output-format json --mode ask --trust --workspace <tmp> [--sandbox enabled] [--model m] "<read request.md>"`, request in `<tmp>/request.md`; `--sandbox enabled` on macOS and Linux only (on Windows the CLI refuses it: "Sandbox requires macOS or Linux") | Ask mode (read-only), no `--force`, no `--approve-mcps`. The CLI cannot remove its read tools, so the menu says "read-only mode" |

- The Cursor editor (`cursor`) is not the Agent CLI. Cursor generation needs the Cursor Agent CLI installed and signed in (`agent login`) on the Worker machine.
- No argument spans lines and models are restricted to `[A-Za-z0-9._:/@[]-]`, so a `.cmd`/`.bat` shim cannot be used for injection; tested with `sonnet & calc`, `--dangerously-skip-permissions` and similar.
- Errors are classified from CLI output (not signed in, unsupported model, unsupported CLI options, timeout, invalid output) and shown in one line, redacted.

## 5. Measurements (developer machine, Windows 11, local SSD; not VDI)

Real workspace: `GrayMoon` (working tree with this change, 53 files) and `GrayMoon.Desktop`; PR: `GrayMoon` `ai-assisted-pull-commit` against `origin/main`.

| Step | Result |
| --- | --- |
| Commit All evidence, 2 repositories, 55 files | 111 ms warm, 206 ms cold (497 ms before reads within a repository ran in parallel) |
| PR evidence, 1 repository | 137-146 ms warm, 176 ms cold |
| Prompt packing | about 50 ms (first call, regex warm-up included) |
| Commit prompt | 3,976 estimated tokens (cap 4,000), 7 excerpts, marked partial |
| PR prompt | 1,457 estimated tokens |
| Claude `haiku` call | 6.0 s commit, 8.5 s PR |
| Claude default model call | 9.5 s commit, 11.0 s PR |
| Codex default model call | 8.9 s commit, 9.3 s PR |

Generated texts were accurate to the evidence (no invented tickets or tests) for all three runs. Not yet measured: 5 and 39 repositories, VDI storage, watcher-evicted cold cases, p50/p95 over many runs. The 39-repository and 100k-line cases are covered for bounds by tests (prompt stays under the cap with every repository listed; a 100k-line file is read only to its cap), not for latency.

## 6. Tests added

- Common (`AiGenerationCommonTests.cs`): manifest round trip, unknown/duplicate preservation, model resolution and defaults, unsafe models, inventory and diff parsing, trimming, classification, sensitive paths, ranking, redaction (no false positive on `cancellationToken`), project file facts, prompt determinism, hard cap with 39 repositories, per-repository coverage, lock files never crowding out source, 10 identical package bumps collapsed, identical hunks shown once, evidence-block escape, strict answer parsing.
- Worker (`GitEvidenceCollectorTests.cs`, real temporary repositories): Staged reads only the index in a partly staged file, All describes the final working tree once plus untracked files and respects `.gitignore`, unborn branch, rename/delete/binary/spaces, sensitive files never read, 100k-line change bounded at read time, nested `main -> release/2.0 -> feature` excludes release and main commits, parent advanced, uncommitted changes excluded from PR, missing target and source, unrelated histories, no commits ahead, tag never used as target, unsafe branch names, no repository mutation, cancellation. (`AiCliRunnerTests.cs`, fake CLIs): argument shape per provider, stdin, isolation folder deleted, default model omits the flag, error classification, timeout kill, output cap, missing CLI, injection-safe models, one process per call and never git.
- App (`AiGenerationTests.cs`): `ai` kept on rewrite and ignored by drift, `ReadAiAsync`, one evidence request and one provider call with the configured or default model, per-repository selected targets passed exactly, one failing repository stops generation, invalid targets refused before any Git work, missing CLI and bad configuration fail before evidence, nothing-to-describe, invalid answers, provider error kinds kept, provider menu, pin per Workspace and never in `graymoon.json`, migration step 10, apply policy, fingerprints.

Full suites after the change: Common 370 passed; Worker 794 passed, 1 skipped (pre-existing); App 1513 passed.

## 7. Open items and risks

1. **Live UI pass.** The controls were checked by rendering their markup with the real stylesheets; they were not clicked in a running App with a Worker (the machine's own GrayMoon App and Worker were in use). Check: Ctrl+Enter commits and never generates; the menu opens over the tree and closes on outside click and Escape; the PR selector does not close the dialog on Escape; icon-only segment below about 22rem.
2. **Cursor model ids rotate.** The first default, `grok`, was rejected by Cursor Agent CLI 2026.10.14 on Windows ("Cannot use this model: grok"): Cursor has no bare alias, only versioned ids, so the default is now `grok-4.7-low-fast` (from `agent --list-models`). When Cursor retires that id, set `ai.providers` for `cursor` or change `AiProviderConfiguration.DefaultModel`. The same run also showed that Cursor's command sandbox exists only on macOS and Linux, so `--sandbox enabled` is passed only there.
3. **CLI option drift.** The no-tools options are verified for the versions above; a newer CLI that renames them fails with "UnsupportedCli" rather than running with tools.
4. **VDI benchmark (H3/H4)** and a golden quality suite (H5) remain to be done on representative workspaces.
5. **Feature contexts read `ai` from the special Workspace's `graymoon.json`**, like `codegraph`; an edit to `ai` made only inside a Feature worktree is not used until it reaches the Workspace checkout.
