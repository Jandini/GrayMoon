# GrayMoon: Robust Git Clone Failure Reporting and GitHub SAML SSO Recovery

> **Implementation prompt for Claude Code / Codex / Cursor**  
> **Target:** GrayMoon `main`, pre-0.2.0  
> **Primary goal:** Every failed clone is reported truthfully and actionably in Workspace Repositories; GitHub SAML SSO failures offer a safe authorization action.  
> **Design priority:** Minimal risk, correct state, security, compatibility, excellent UX. **Implement and test**, not only design.

## 0. Non-negotiable operating instructions

You are modifying the **existing GrayMoon codebase**. Start by reading the currently checked-out branch, its diff against `main`, and relevant code/tests. Findings below were verified against GitHub `Jandini/GrayMoon` `main` at commit `a84bde55e51544ecf3d1ef5fd152f09c0cb1f9cf` on 2026-10-16; **do not assume paths or behavior have remained unchanged**. The working tree may contain unrelated changes: do not revert or overwrite them. If the local branch differs, adapt to current code rather than force historical snippets.

Deliver an implementation with focused tests. Prefer a handful of localized changes over sweeping refactors. Do **not** redesign authentication, introduce mandatory GitHub preflight requests, change Git transport or cloning strategy, add database migrations to store SSO URLs, alter parallelism, or add new network calls on successful clone paths.

**Begin with a short evidence-based trace** of the existing failure, then implement. Do not stop at a proposal or request another approval unless there is a genuine safety-critical blocker.

## 1. Real failing case and expected behavior

Git clone produced a failure like:

```text
Cloning into 'cifx'...
remote: The 'acme-labs' organization has enabled or enforced SAML SSO.
remote: To access this repository, you must re-authorize the OAuth Application 'Visual Studio'.
remote: To access this repository, visit https://github.com/enterprises/acme-labs-global/sso?authorization_request=<OPAQUE-SENSITIVE-VALUE> and try your request again.
fatal: unable to access 'https://github.com/acme-labs/cifx.git/': The requested URL returned error: 403
```

The Worker logs `Git clone failed after retries ... ExitCode=128 ...`, but **Workspace Repositories shows no useful error**. The actual URL query token is sensitive; the example deliberately replaces it.

Expected:

- `cifx` is **failed / not cloned**, never falsely `InSync`.
- Its row shows **GitHub SSO authorization required**, with a clear explanation such as “Reauthorize Visual Studio to access acme-labs, then retry cloning.” Distinguish the OAuth application mentioned by GitHub from the credential source GrayMoon used: do not falsely claim the connector token is invalid or that a Visual Studio token is being used by GrayMoon.
- If Git supplied a **validated** actionable GitHub SSO authorization URL, show **Authorize SSO**. Open the precise URL in the user's browser without exposing its query parameter in displayed text, logs, telemetry, or persistence. If absent/invalid, show explanation but no unsafe action.
- Show **Retry** through the existing per-repository Sync action. Do **not** start polling or auto-retrying on browser open; browser SSO completion is unknown.
- Other independent repositories continue under the existing bounded-concurrency policy.
- Any ordinary clone failure also appears in the affected row with an appropriate sanitized message; generic `403` is **not** automatically SSO.
- A successful retry clears the stale failure. A repeated failure updates/replaces the stale failure instead of appending endless copies.

## 2. Verified code landmarks and actual defects

Review these *actual* code paths before editing:

### Worker/Git

- `src/GrayMoon.Worker/Services/GitService.cs`: `CloneAsync` calls `GitProcessRunner.RunRemoteAsync` and reduces failure to `false` after logging stderr. **Failure detail is lost.** `CloneIntoAsync` has a similar bool-only failure path.
- `src/GrayMoon.Worker/Abstractions/IGitService.cs`: clone signatures return `Task<bool>`; inspect **all callers, fakes and test implementations** before modifying interfaces. Prefer a small typed result or overload only when justified; do not break unrelated consumers inadvertently.
- `src/GrayMoon.Worker/Commands/SyncRepositoryCommand.cs`: if clone fails and repo directory is absent, execution can fall through to `SyncRepositoryResponse { Success = true }`. **False success is a primary bug.** Existing `SyncRepositoryResponse` already has `Success`, `ErrorMessage` and group fields.
- `src/GrayMoon.Worker/Commands/AttachWorkspaceRepositoryCommand.cs`: uses `CloneIntoAsync` and currently falls back to generic `Git clone failed.`. Preserve its special root-attachment/restore semantics, including existing-root adoption, unborn branch and safe recovery.
- `src/GrayMoon.Worker/Services/GitRemoteAuth.cs`: recognizes generic authentication failures and adds an often inappropriate **connector-token hint**. Prefer a specific SSO diagnostic *ahead* of a generic auth hint, while keeping existing credential transports unchanged.
- `src/GrayMoon.Worker/Services/GitProcessRunner.cs` and `GitResiliencePipelines.cs`: remote operations already use shared authenticated execution and resilience pipelines.
- `src/GrayMoon.Common/Git/GitRetryClassifier.cs`: allowlists transient failures; HTTP `403` alone is normally definitive. **Review first, do not rewrite retry policy unnecessarily**. Be aware a combined stderr can contain both SSO markers and a generic transient marker; definitive auth failures should win if needed, without weakening legitimate transient retries.

### App/sync mapping

- `src/GrayMoon.App/Models/Api/WorkerSyncApiModels.cs`: `WorkerVersionBranchResponse` **already has `Success` and top-level `ErrorMessage`**.
- `src/GrayMoon.App/Services/Git/WorkspaceGitService.ResponseParsing.cs`: `ParseSyncRepositoryResponse` reads fetch/version errors but normally builds `combinedError` without top-level `ErrorMessage`. The Worker *transport envelope* success is distinct from the **payload** success. Fix both interpretation and error propagation. Do not collapse useful specifics to `Sync failed`.
- `src/GrayMoon.App/Services/Git/WorkspaceGitService.Sync.cs`: parallel fan-out, result persistence via `RepositoryStateSnapshot` and subsequent workspace recomputation. Avoid treating early failures as successful probes or wiping previously valid columns.
- `src/GrayMoon.App/Services/Orchestration/WorkspaceSyncHandler.cs`: existing `onProgress` status logic can return `RepoSyncStatus.InSync` **because `info.ErrorMessage` is nonempty**. Correct that. **Do not** simply classify all missing GitVersion values as failures: Basic workspaces deliberately may not use versioning.
- `src/GrayMoon.App/Services/Workspaces/WorkspaceRepositoryStateWriter.cs`: inspect persisted `SyncStatus` derivation, probe flags and error policy before adjusting. Also inspect `src/GrayMoon.App/Services/Worker/SyncCommandHandler.cs`: it supplies `ErrorMessageForcesInSync = true` for some notification paths, which may be deliberate for nonblocking version warnings. **Do not globally invert this option**. Distinguish fatal clone/fetch failure from nonfatal GitVersion warnings.

### Workspace Repositories UI

- `src/GrayMoon.App/Components/Pages/WorkspaceRepositories.Sync.cs`: already has `SyncSingleRepoAsync`, sync callbacks, and post-job application/clearing of per-repo errors.
- `src/GrayMoon.App/Components/Pages/WorkspaceRepositories.Display.cs`: `repositoryErrors` and helpers currently append messages. Respect existing handling for other operations; use a narrow replace-on-attempt mechanism as necessary.
- `src/GrayMoon.App/Components/Pages/WorkspaceRepositories.Realtime.cs`: SignalR `RepositoryError` also updates errors. Consider event ordering, duplicate updates and context switching.
- `src/GrayMoon.App/Components/Pages/WorkspaceRepositories.razor` and `src/GrayMoon.App/Components/Pages/WorkspaceRepositoriesRow.razor`: the row already renders `RepositoryError` in an alert row. Reuse that surface and existing row sync action. Keep layout/virtualization stable.
- Find corresponding `.razor.css` / styling, existing URL/browser-open helpers, tests and conventions. Do not add an auth modal or separate global error system.

**Root-cause trace to prove with tests:** Git stderr -> `GitService.CloneAsync` -> `SyncRepositoryCommand` -> `SyncRepositoryResponse` -> Worker envelope -> `ParseSyncRepositoryResponse` -> `RepoGitVersionInfo` / `RepositoryStateSnapshot` -> orchestration status/persistence -> Workspace Repositories row. Include the `CloneIntoAsync` -> Workspace attachment/restore flow in the analysis.

## 3. Implementation design: smallest coherent change

### 3.1 Carry clone failure detail end to end

Provide a small failure-bearing return type for cloning, using an existing pattern where possible (e.g., `(bool Success, string? ErrorMessage, ...)` or a minimal record), **not a new general-purpose Git error architecture**.

- Preserve exit code plus a *sanitized* technical explanation for diagnostics; do not give UI raw, unfiltered stderr or credential-bearing URLs.
- Optionally include a small failure kind (`SsoAuthorizationRequired`, `AuthenticationFailed`, `AccessDenied`, `RepositoryNotFound`, `NetworkFailure`, `Unknown`) when it actually simplifies presentation. Do not create unused enum values, plugin-style provider registries or deep hierarchies.
- Expose an optional **ephemeral** authorization action URL **separately from safe displayed error text**. The URL must never be embedded in `ErrorMessage` or appended to long-lived log / DB fields. If current transports persist all failure payloads, investigate their lifecycle and choose the minimal safe boundary that keeps the URL transient. If a safe transient action cannot be ensured, prefer no clickable URL over unsafe storage.
- Update all relevant interface implementations, mocks and callers. Include `CloneIntoAsync`'s generic-error path in this repair, without rewriting workspace-root attach.
- Preserve existing cancellation behavior (`OperationCanceledException` is cancellation, not auth failure).

### 3.2 Never call failed clone successful

In `SyncRepositoryCommand`:

- Failed clone returns immediately with `Success = false` and the repo-specific error. Do not execute later sync stages for a nonclone.
- A clone process returning zero must also be verified against expected repository existence / Git metadata before declaring clone successful, using existing path validation conventions. Do not equate an empty or partial folder with a valid checkout.
- No clone URL and missing directory should be represented as **NotCloned or actionable missing-clone-source state**, not a fabricated success. Distinguish intentional optional/absent repo workflows after reviewing current contracts.
- Preserve correctly handled successful clones, safe.directory, long paths, tag pins and capability-dependent `.NET`/GitVersion enrichment.
- Do not blindly delete a failed clone directory. Failed clone can leave partial content or .git. Inspect existing cleanup/restore rules; only delete **provably GrayMoon-created, safe-to-delete residue**, and otherwise surface a safe remediation. Never destroy user edits, pre-existing folders or unrelated files. Ensure Retry neither gets stuck forever nor accidentally overwrites data.

### 3.3 Propagate command result and preserve state

- Distinguish Worker envelope `response.Success` from payload `success` accurately. Honor top-level payload `errorMessage`.
- Where error fields overlap, prefer specific fatal error over generic hints; deduplicate, normalize newlines, keep length reasonable, and avoid leaking sensitive URLs.
- Keep `RepoGitVersionInfo.ErrorMessage` and `RepositoryStateSnapshot.ErrorMessage` consistent.
- Mark only *actually performed* probes as probed. An early failed clone/fetch must not erase valid persisted branch/version/projects, or claim refs were refreshed. Preserve previously legitimate state, but clearly display the **current failed attempt** and failed / not-cloned status.
- Distinguish **fatal sync failure** from a nonfatal enrichment/version warning. If `ErrorMessage` serves both today, consider a small explicit success/failure flag or probe outcome rather than inferring fatality from presence of error text; keep compatibility with other callers.
- Audit the save/refresh cycle: transient `onProgress` Error must not be overwritten moments later by an `InSync` persisted state after the page reload. Persist the appropriate per-repository status using established writer semantics, including context-specific repository states and overall workspace `IsInSync`.
- Existing and older Worker payloads with absent optional fields must remain parseable. Prefer additive JSON fields; no command renames or breaking serialized contracts.

### 3.4 GitHub SAML SSO classifier and secure URL handling

Classify only explicit signals such as:

- `organization has enabled or enforced SAML SSO`;
- `must re-authorize the OAuth Application ...` when accompanied by GitHub SSO evidence;
- other explicit, well-supported GitHub SSO-required messages.

Do **not** equate plain HTTP 403, repository-not-found or generic permission denied with SSO. Case-insensitive, CRLF/LF tolerant, support `remote:` prefixes and additional unrelated stderr lines. Do not assume auth provider, app name or org can always be extracted. Normalize untrusted app/org labels for UI as plain encoded text.

When an exact GitHub authorization URL is present:

1. Parse with `Uri.TryCreate`, require absolute `https` and an **approved exact hostname** (initially `github.com`; do not accept `github.com.evil.tld`, userinfo tricks, backslashes, control chars, nonstandard ports, or `javascript:`).
2. Restrict accepted URL path to the known GitHub SSO authorization route(s), rather than any GitHub URL. Do not synthesize arbitrary URLs from organization strings.
3. Preserve the exact validated URL only for opening the browser and only for the lifetime of the pending action; never log or persist query parameters. Do not accidentally expose it in tooltip/title, copied error details, SignalR text events, query strings of GrayMoon navigation, database error fields or telemetry.
4. When GitHub provides no acceptable URL, omit the action. No guessed direct SSO URL required.
5. Ensure any raw stderr logging has sensitive authorization-request query values redacted. Find all implicated log sites (both `CloneAsync` and `CloneIntoAsync`; consider fetch/pull/push and generic logger when they might carry the same output). Keep useful exit codes and sanitized context.

Handle errors returned by Git Credential Manager as well as direct connector-token transports; use the error content, never a blanket assumption that an invalid connector token is the cause. Retain current `GitRemoteAuth` mechanism, including noninteractive token handling.

### 3.5 Retry classification

- Inspect the current `GitRetryClassifier` and Polly pipeline. Existing `403` handling is already definitive: avoid gratuitous policy changes.
- If GitHub SSO output includes some unrelated transient phrase, ensure an explicit SSO refusal still does not retry, while preserving transient retry behavior in all other cases.
- Do not modify retry count, timeouts, network-process concurrency or cancellation without a targeted failing test demonstrating need.
- User-triggered retry is allowed and reuses the existing single-repo synchronization action.

### 3.6 UI specifics

Reuse `WorkspaceRepositoriesRow`'s existing error row and the existing single-repository Sync handler.

For SSO, the desired UI is compact:

> **GitHub SSO authorization required**  
> Reauthorize **Visual Studio** to access **acme-labs**. Complete authorization in your browser, then retry cloning.  
> **Authorize SSO** (only if safe URL is available) · **Retry**

Implementation rules:

- Render labels as text (Blazor encoding), not raw HTML/markup from stderr.
- Button/link must be accessible and keyboard-operable; reuse existing GrayMoon icon/button colors and spacing. If using an anchor, apply `rel="noopener noreferrer"` and suitable no-referrer behavior; check existing external-browser conventions.
- Show normal clone failure explanations in the same row, even if no SSO action is available.
- Keep generic error text **safe and short** with optional sanitized expandable technical detail if existing UI supports it; do not force giant rows/long URL overflow.
- Ensure the row and action remain associated with the correct repository, workspace, and selected Feature/Workspace context. Prevent stale async results or SignalR notifications from updating a newly selected context. Honor existing `_contextGeneration` and job guards.
- Keep errors visible after the overlay disappears and through normal refresh as appropriate to existing persistence semantics. A successful retry clears them; a failure replaces the previous attempt's error (do not append duplicates). Avoid clearing another repo's error.
- Do not block the entire batch or cause one repository failure to suppress progress/results for others.
- Do not add a global repeated SSO banner or new multi-repo UI architecture for this task.

## 4. Failure and security edge cases

Explicitly review and test:

| Case | Required result |
|---|---|
| GitHub SSO with exact safe URL | SSO classification, actionable button, no persisted/logged query value |
| GitHub SSO without URL | SSO error, no fabricated button |
| Visual Studio OAuth reauthorization text | SSO explanation when appropriately evidenced; app name only if present |
| Generic 403 | Access denied/auth guidance, **not** SSO |
| 401, expired/missing token | Generic auth guidance appropriate to actual authentication source |
| 404 / repository not found | Missing/inaccessible repository; avoid claiming definitively nonexistent when access can mask existence |
| Network/DNS/curl timeout or 5xx | Existing bounded transient retries and useful failure message |
| TLS/certificate error | Useful error; no unnecessary retries under certificate policy failure |
| Malicious URL in stderr (`github.com.evil.tld`, redirect/userinfo, HTTP, wrong path) | No authorization action |
| Text with CRLF, ANSI sequences, extra remote lines | Stable classification and cleaned UI |
| User cancels clone | Cancellation semantics, no fabricated credential failure |
| Clone says exit 0 but directory missing | No successful sync |
| Clone leaves partial directory | Subsequent retry has safe, non-destructive remediation |
| Multiple failed repos among successful repos | Per-repo isolation; correct aggregate success state |
| Basic workspace without GitVersion | Valid synchronization may be successful |
| Failed fetch after existing checkout | Preserve prior valid metadata; status reflects unsuccessful current attempt |
| Successful retry, subsequent refresh/context switch | No stale error or stale SSO action |
| Older Worker missing added fields | Safe fallback behavior |

## 5. Tests and verification gates

Add focused tests **before or alongside each change** in the existing projects:

1. **Unit tests, Worker clone result:** simulate Git exit `128` with the supplied SSO error; assert a failure result carries the correct *safe* information and optional classified action; success remains unchanged.
2. **Worker command tests:** failed clone yields `SyncRepositoryResponse.Success = false` and error; skips fetch/version/project phases; successful clone takes the existing path; failed or missing folder doesn't silently return success; safe partial-folder behavior.
3. **`CloneIntoAsync` / attach-root regression:** generic fail is replaced by actionable safe clone failure; protect existing content and special restore behavior.
4. **Classifier tests:** SSO, plain 403, malformed/malicious URLs, OAuth app name, no URL, varied Git stderr, sanitizer. Explicitly assert that the `authorization_request` value is absent from displayed and persisted/loggable fields.
5. **Retry tests:** no retries for SSO/403, transient network failures retry as before, cancellation propagates.
6. **App response parsing:** envelope versus payload success; top-level error propagated, no duplicate fetch/version messages; older payload shape; correct `RepositoryStateSnapshot` probe markers.
7. **Persistence/status tests:** failure cannot become `InSync` after saving/reloading or overall workspace recompute. Do not regress Basic (nonversioned) workspaces, version-warning behavior, Feature contexts, or asynchronous Worker sync notifications.
8. **Blazor component/page tests:** error appears in correct repository row, valid SSO link action rendered, malformed URL blocked, Retry uses existing action, failure survives job completion, successful retry removes error, context-switch result isolation, no oversized URL leak in markup.
9. **Multi-repo test:** one SSO fail among other success paths does not cancel sibling operations or mark whole Workspace synchronized.

Avoid real enterprise credentials/network requirements; use deterministic fakes and existing test helpers. Where executable Git integration tests are used, rely on safe local bare repositories and preserve isolation.

Run a **targeted suite first**, then all relevant affected suites and builds. Report exact commands and pass/fail results. Do not assert UI behavior was manually checked if it was not. Fix all introduced compilation breaks in test fake implementations.

## 6. Small, verifiable implementation phases

**Phase A — Trace and baseline tests:** Identify Worker->App->row path and existing successful/failing behavior. List affected callers and tests. Document actual root cause and the observed file/state lifetime.

**Phase B — Correct Worker result:** Preserve clone failure information, correct `SyncRepositoryCommand` early-exit semantics, handle safe retry residue. Cover `CloneIntoAsync`'s secondary path without broad refactoring. Add Worker tests.

**Phase C — SSO classification and security:** Add deterministic classification, URL validation and redaction. Prove logging/transport do not expose the opaque authorization request. Add classification/security/retry tests.

**Phase D — App state correctness:** Parse payload error and fatal status correctly; preserve probe semantics; avoid false `InSync` in both presentation and persistence; keep versioning-less workspaces functional. Add parsing/orchestration tests.

**Phase E — UI:** Render per-row SSO messaging/actions using existing UI patterns; avoid stale/duplicate errors; validate context, retry and success-clear behavior. Add component tests.

**Phase F — Regression review:** Search all affected clone callers and external operations, run builds/tests, inspect diff for unintended changes, document what was and was not verified.

You may parallelize **independent test writing or review** with multiple AI agents, but do not run concurrent edits to the same contracts/files. Keep one integration owner responsible for shared types and final regression validation.

## 7. Scope limits / what NOT to do

- No OAuth flow in GrayMoon, no browser callback server, no token refresh or credential migration.
- No GitHub API preflight for all repositories: Git already supplies concrete failure evidence; preflight cannot reliably validate all GCM/Visual Studio credentials.
- No database migration to store authorization URLs or OAuth metadata.
- No new auth service or generic exception framework unless current code truly requires one.
- No blanket replacement of all `RepoSyncStatus` policies; fatal clone/fetch failure differs from nonfatal version warnings.
- No change to `RunRemoteAsync` token injection, `GIT_CONFIG_*` environment support, legacy fallback, or Git Credential Manager configuration.
- No broad rewrite of `GitService`, GitProcessRunner, orchestration queues, watcher integration, repo sync concurrency or workspace restore behavior.
- No arbitrary directory cleanup, force checkout, or deletion of pre-existing/modified content.
- No logging raw authorization URL query parameters or credentials, even for tests in CI output.

## 8. Definition of done

All of the following are demonstrated with automated tests (or explicitly called out as unverified manual behavior):

- A failed clone produces a **failed Worker response** with a useful, sanitized repo-specific message.
- Failed clone is never silently converted to a successful sync or marked `InSync`, including after persistence/page reload.
- SSO failure is recognized **only with evidence**, shows specific guidance and safe authorization link when available.
- Sensitive SSO URL query values never appear in persisted error text, normal logs, UI text, telemetry or publicly copied details.
- Generic clone failures remain visible and understandable.
- Existing cloned repositories, Feature worktrees, Basic workspaces, GitVersion warnings, tag pins, restore/attach and retry behavior are not regressed.
- Single-repository Retry works, success clears error, partial-clone failures do not lead to data loss.
- Mixed multi-repository batches retain independent outcomes and accurate workspace summary.
- No new remote calls or measurable overhead on successful clone path beyond negligible local checks/classification.
- Targeted and relevant regression suites pass.

## 9. Final response from implementing agent

Provide:

1. **Root cause:** evidence-based, exact methods and line references, including the reason the row currently shows no error.
2. **Design decisions:** the minimal contract chosen, how URL lifetime/security was addressed, and why.
3. **Changes:** concise per-file summary; note whether both `CloneAsync` and `CloneIntoAsync` are covered.
4. **Validation:** exact tests/builds run and results (separate from unrun tests/manual UI checks).
5. **Risk check:** safe partial directory behavior, backward-compatible Worker contracts, no credential leakage, no status regressions.
6. **Remaining limitations:** anything still unsupported or needing an enterprise-SSO manual test.

**Implement completely, keep the diff focused, and do not report success until the failure-to-UI path is verified end to end.**
