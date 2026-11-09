# Connector token failures: clear indication instead of "unknown" errors

Status: draft for review (analysis only, no code changed). Written 2026-10-27.

## 1. Why this document exists

During a Push Updated run a connector token became invalid. The run did not say "your token expired"; it reported
generic errors from the Push Orchestrator. GrayMoon already reports a bad token nicely in several places (the
Connectors page, the connector health gate, the Worker's git hint). This document lists every place a token is used,
what happens there today when the token is bad, what has to be handled, where in the code each fix belongs, and
whether to be proactive (check before) or reactive (react when a call fails).

Scope: connector tokens (GitHub PAT, NuGet / GitHub Packages / ProGet credentials). The Worker secret
(`WorkerSecretMiddleware`, `WorkerTokenProvider` 401 "reinstall the Worker") is a different credential and is only
mentioned where it can be confused with a connector token.

I could not see the user's log, so the exact text that appeared as "unknown" is not confirmed. Section 9 says what to
grab from the log to confirm which of the paths below fired.

## 2. What exists today (and works)

| Piece | Where | What it does |
|---|---|---|
| Connector health flag | `Connector.IsHealthy`, `LastError`, `Status` | One flag per connector; set by the Connectors page test (`ConnectorRepository.UpdateStatusAsync`), by `GitHubRepositoryService` on a failed repository fetch, and by the startup check. |
| Startup health pass | `TokenHealthBackgroundService` | Decrypts each token and calls `TestConnectionAsync` (GitHub: `GET /user`; NuGet: index/auth probe). **Runs once at App start** (one `ExecuteAsync` pass, no timer). |
| Health gate before work | `ConnectorHealthService.EnsureConnectorHealthyForRepositoryAsync` / `...ForRepositoriesAsync` | Throws `ConnectorHealthException` with the stored `LastError` when the flag is false. Called from push (single repo and parallel level push), sync, commit sync, branch update, return to default, branch operations. Reads stored state only (no network). |
| Git auth hint | `GitRemoteAuth.IsAuthFailure`, `AuthFailureHint`, `WithAuthHint` (Worker) | Regex over git output (`authentication failed`, `could not read username`, `bad credentials`, `error: 401/403`, `repository not found`, `remote: permission to ... denied`, ...) prefixes "Git authentication failed. Check the connector token: it may be missing, expired, or lack access". Applied in fetch, pull, push and remote-branch delete in `GitService`. |
| No retry on auth | `GitRetryClassifier` | 401/403/404 are explicitly not retryable. Good: a bad token fails fast. |
| SSO distinction | `GitHubSsoAuthorization` | SSO refusals are separated from "token invalid". |
| Friendly API text | `GitHubApiErrorHelper.FormatFriendlyGitHubHttpError`, `NuGetService.MapHttpStatusToMessage` | 401/403 wording for connector tests and Actions badges. |
| Fault vs fail | `ConnectorTestResult.Fault` / `Fail` | GitHub and NuGet tests already separate "the connector is at fault" (401/403/404) from other failures. |

The git path is in decent shape. The gaps are everywhere else, and in how a failure travels up to the user.

## 3. Root causes (why it turns into "unknown")

**R1. The health flag is a startup snapshot.** `TokenHealthBackgroundService` checks once. A token that expires or is
revoked while the App is running keeps `IsHealthy = true`, so the health gate passes and the run starts. Nothing ever
flips the flag to false when a real call gets a 401. After the first failed run the next run starts the same way.

**R2. No runtime feedback.** No code path says "this call proved the token is bad, record that on the connector".
`UpdateStatusAsync` is called only from the Connectors page and the repository fetch.

**R3. Failures are strings, not types.** The Worker returns `ErrorMessage` text; the App recognises problems by
substring (`PushErrorFormatter`, `GitRemoteAuth.IsAuthFailure` lives in the Worker project and is not visible to the
App). A bad token is therefore only recognisable where someone remembered to grep for it. Anywhere else it is "an
error".

**R4. Several failure points are not gated at all.** The health gate is missing from undo push, fetch, Feature
operations that fetch, repository attach, GitHub REST calls (pull requests, Actions, CI feed), registry sync and the
package wait. Update and Push has no pre-flight of its own.

**R5. The NuGet side collapses 401 into "not there".** `NuGetService.PackageExistsAsync` returns `bool`: a 401/403 is
`false`, the same as "package id does not exist on this registry". `PackageRegistrySyncService` therefore finds "no
registry" for a package, `SynchronizedPushNotPossibleException` is raised, and the new "Continue with normal push?"
prompt appears. The user is asked about skipping the registry wait when the real problem is an expired registry token.
The package wait (`WorkspacePushService`, `LookupPackageVersionAsync`) does classify `AuthenticationFailed`, but only
for diagnostics: the wait keeps polling until its timeout, then reports missing packages.

**R6. One bad token fans out.** Pushes in a level run in parallel; each repository fails separately with its own
message. The update lane keeps walking later levels (by design) and each level's commit sync fetches and pulls with the
same dead token. The user sees many repository errors and level errors instead of one cause.

**R7. Partial state, no explanation.** If the token dies after level 1 was pushed, levels 2+ are committed locally but
not pushed. The run ends with failures but nothing says "the token stopped this, the first N levels went out, fix the
token and run again".

**R8. Message reshaping can hide the cause.** `PushErrorFormatter.Format` checks archived, large file, protected branch,
merge conflict, pull failure and non-fast-forward before returning the raw text. A 403 can contain words that match an
earlier branch (an archived repository is a real 403; a protected-branch rejection is not a token problem). An auth
failure that is wrapped in a "pull failed" style message would be shown as "could not pull remote changes, check
repository state", which sends the user to the wrong place. The auth check should come first and be explicit.

## 4. Scenario catalogue

Token states to distinguish (each needs its own wording and, where possible, its own action):

| ID | Token state | Typical signal |
|---|---|---|
| T1 | Missing or empty | `UnprotectToken` returns null/empty; git "could not read Username" |
| T2 | Cannot be decrypted (key changed, tampered) | `UnprotectToken` throws; already "Token needs to be re-entered." in the startup pass only |
| T3 | Expired (fine-grained PAT, time limited) | 401 "Bad credentials" |
| T4 | Revoked or deleted | 401 |
| T5 | Valid but missing scope (classic vs fine-grained, `read:packages`, `repo`, `workflow`) | 403, `remote: Permission to X denied`, or 404 `repository not found` on a private repo |
| T6 | Valid but SSO not authorized for the organization | 403 with SSO URL (handled by `GitHubSsoAuthorization`) |
| T7 | Valid but organization blocks classic PATs or requires approval | 403 with an organization policy message |
| T8 | Rotated by the user during a run (new token saved on the Connectors page) | run keeps sending the old token it captured |
| T9 | Rate limited (looks like 403 on GitHub) | not a token problem; must stay separate |
| T10 | Wrong host / URL for the connector | 404 or DNS; not a token problem |

Where each can bite (token use sites found in code):

### 4.1 Git over HTTPS (Worker, token passed as `bearerToken` and applied by `GitRemoteAuth`)

| ID | Operation | Code | Health gate today | Bad-token result today |
|---|---|---|---|---|
| G1 | Push (single) | `WorkspacePushService` ~l.984/992; Worker `PushRepositoryCommand`, `GitService.PushAsync` | yes (stale flag) | Worker hint present; App shows raw text via `PushErrorFormatter` |
| G2 | Push level (parallel) | `WorkspacePushService` ~l.1334 | yes (stale flag) | one error per repository, no common cause |
| G3 | Pull / integrate before push | `GitRemoteIntegrateService`, `GitService` pull | n/a (inside push) | hint present; formatter may reword (R8) |
| G4 | Fetch / commit sync during update | `WorkspaceCommitSyncHandler` l.64/81/182/190 | yes (stale flag) | per level errors; update continues to next level |
| G5 | Sync | `WorkspaceGitService.Sync.cs` l.68/110 | yes (stale flag) | hint present |
| G6 | Fetch | `WorkspaceGitService.Fetch.cs` l.71 | no | hint present, no connector named |
| G7 | Return to default | `WorkspaceGitService.ReturnToDefault.cs` l.103/132 | yes | hint present |
| G8 | Branch create / delete / upstream | `WorkspaceBranchOperations` l.475/805 | yes | hint present |
| G9 | Update branch from default | `WorkspaceBranchUpdateHandler` l.55/92 | yes | hint present |
| G10 | Undo push | `WorkspaceUndoPushHandler` l.64/78 | no | falls back to the literal "Unknown error" when no message |
| G11 | Attach / clone repository | `WorkspaceRepositoryOperations` l.583 | no | hint present |
| G12 | Feature create / repair / remove (remote branch delete, fetch) | `WorkspaceFeatureOperations` l.987 | no | hint present, may be logged only |
| G13 | Hook-driven background sync | Worker `CheckoutHookSyncCommand`, `RefreshBranchesCommand` | no | silent or logged only |

### 4.2 GitHub REST (App, `GitHubService`)

| ID | Operation | Today |
|---|---|---|
| H1 | Pull request create / merge / status / reconcile | `GitHubApiErrorHelper` gives 401 wording to the caller; no connector flag update |
| H2 | Actions workflow runs and jobs, live CI terminal during push (`GhaWorkflowLiveFeedService`) | errors become badges; polling continues against a dead token (rate-limit tracker only knows 429) |
| H3 | Repository list refresh | already sets connector status to Error (`GitHubRepositoryService` l.75): the one good example |
| H4 | Markdown preview token fetch (`WorkspaceGitChanges.Markdown.cs` l.367) | image fetch fails silently |
| H5 | Connector test | good |

### 4.3 NuGet / package registries (App, `NuGetService`)

| ID | Operation | Today |
|---|---|---|
| N1 | Registry sync (`PackageRegistrySyncService` l.72/149, `PackageExistsAsync`) | 401 becomes "package not found" (R5) |
| N2 | Package wait (`LookupPackageVersionAsync`) | classified `AuthenticationFailed`, but only logged; wait runs to timeout |
| N3 | Service index (`GetPackageBaseAddressAsync`) | non-success returns null, same as "no package base address" |
| N4 | `dotnet restore` after a level | uses the user's nuget.config, outside GrayMoon's token; out of scope, but its failure text should not be mislabelled |

### 4.4 Flow scenarios (how failures combine)

| ID | Scenario | Needed behaviour |
|---|---|---|
| F1 | Token already bad before the run | Refuse to start; name the connector and the action. No local commits made. |
| F2 | Token expires between click and first push | Detect on the first call, stop, explain. |
| F3 | Expires after level 1 was pushed | Stop the run; report "levels 1..N pushed, level N+1 and later not pushed because the token for connector X is invalid"; local commits stay. |
| F4 | Several connectors in one workspace, only one bad | Name that connector and the repositories it serves; other repositories are not blamed. |
| F5 | Parallel pushes all fail | One notice, not N; per-repository rows still marked. |
| F6 | Update lane and push lane both hit it | Update lane must not keep walking levels with a dead token; first auth failure ends the run (cancel both lanes). |
| F7 | User fixes the token during the run | Run still fails with the old token (it was captured); the message should say "re-run after updating". |
| F8 | Approval flow | A token failure must never produce "Continue with normal push?" (R5). That prompt is only for missing registry mappings. |
| F9 | REST callers | 401-class failures need a distinct status/body (for example 424 or 400 with `errorKind: "connectorAuth"` and `connectorId`), not the generic failed body. |
| F10 | Prepare Workspace | branch gate failures from auth should report as auth, not "branch could not be created". |

## 5. Proactive or reactive?

**Recommendation: reactive as the source of truth, plus one cheap proactive guard. Not proactive alone.**

Why reactive must be the base:
- A token can expire or be revoked at any moment, including in the middle of a multi-level run. Only the call that
  fails knows. A pre-check cannot see the future.
- The check itself can be wrong: GitHub answers 403 for rate limits, SSO and policy as well, and a `GET /user` success
  does not prove the token can push to repository X or read packages.
- Every network pre-check costs time and API budget on every run, which matters on slow VDIs.

Why a proactive guard is still worth having:
- Starting a long run, committing locally to many repositories and then failing at level 2 leaves partial state (R7).
  Failing before anything changes is much better for the user.
- Several failure points have no gate (R4).

So:
1. **Reactive (mandatory):** one classifier turns every 401/403/auth-looking failure into a typed result; on the first
   one the run stops, the connector is marked unhealthy with a specific reason, and one clear message is shown.
2. **Proactive, free:** at the start of Update and Push (and Prepare, Push, REST) check the stored health flag of every
   connector in scope. Cost: one DB read, no network, no Worker call. Because the flag becomes truthful through the
   reactive path (R1/R2 fixed), it is now accurate after the first failure instead of only after a restart.
3. **Proactive, cheap and throttled (optional):** if the flag was last verified more than N minutes ago (suggest 30),
   verify the connectors in scope with one lightweight call each (`GET /user`, NuGet index), in parallel, bounded by
   the existing `MaxParallelOperations`. One call per connector, not per repository, and skipped when a recent success
   or any successful authenticated call is on record. Record `LastVerifiedUtc`.
4. **Not recommended:** a periodic background poll of all connectors. It spends API calls while nobody is working and
   still cannot catch a mid-run expiry.

## 6. Design

### 6.1 One typed failure

Add `ConnectorFailureKind` (Common, so both processes use it): `None, Missing, Undecryptable, Invalid (401, expired or
revoked), InsufficientAccess (403 or private 404), SsoRequired, PolicyBlocked, RateLimited, NotAuthRelated`.

Add one classifier in Common (move `GitRemoteAuth.AuthFailureRegex` and `GitHubSsoAuthorization` logic behind it):
`ConnectorFailureClassifier.FromGitOutput(text)`, `.FromHttp(statusCode, body, headers)`. Rules:
- 401 is `Invalid`.
- 403 is checked in this order: rate-limit markers (T9), SSO (T6), archived/protected-branch/large-file rejections
  (not auth), then `InsufficientAccess`.
- `repository not found` is `InsufficientAccess` only when the repository is known to exist in GrayMoon (it was cloned
  or listed before); otherwise it stays a generic failure.

### 6.2 Carry it to the App

- Worker responses that run remote git (`PushRepositoryResponse`, sync, fetch, pull, branch, undo push) get an optional
  `errorKind` field next to `ErrorMessage`. App and Worker are version-locked, so no capability flag is needed.
- App-side calls (`GitHubService`, `NuGetService`) throw or return the same kind. `GitHubHttpRequestException` already
  carries `StatusCode`; add the kind.
- `NuGetService.PackageExistsAsync` returns a result (exists / absent / authFailed) instead of `bool`.

### 6.3 React in one place

New `ConnectorFailureReporter` (App, scoped), called by every site above:
1. Marks the connector unhealthy through the existing `UpdateStatusAsync("Error", reason)` with a specific reason
   ("Token for connector 'X' is invalid or expired. Update it on the Connectors page.").
2. Raises one notice per connector per run (dedupe by connector id and run id).
3. Returns the failure to the caller so the run stops.

`ConnectorHealthService.EnsureConnectorHealthy...` then stays correct for the next operation without a restart.

### 6.4 Stop the run

- `UpdateAndPushOrchestrator`: the first connector auth failure from either lane cancels the shared run (the update lane
  must not keep committing and fetching with a dead token; today it walks all levels on purpose for ordinary push
  failures, and that stays for non-auth failures). Result carries the kind and the connector.
- `WorkspaceUpdateAndPushOperations`: a new status, for example `ConnectorAuthFailed`, with the connector id and name,
  the levels that were pushed, and the repositories left unpushed. `UpdateAndPushResultPresenter` turns it into one
  error notice. It never becomes `ApprovalRequired` (F8).
- `WorkspacePushService`: an auth failure in a parallel level push is reported once for the level (F5); the sequential
  and pipelined paths both go through the same helper.
- `PushErrorFormatter.Format`: check the typed kind (or the classifier) first, before archived, protected branch,
  pull failure and non-fast-forward (R8).

## 7. Where each scenario is implemented

| Concern | File(s) | Change |
|---|---|---|
| Classifier and kind | `GrayMoon.Common/Git/` (new `ConnectorFailureClassifier`, `ConnectorFailureKind`); `GitRemoteAuth` and `GitHubSsoAuthorization` delegate to it | one set of rules for both processes |
| Worker reports kind | `GitService` (push, pull, fetch, branch delete), `GitRemoteIntegrateService`, `PushRepositoryCommand` and the other remote commands' responses (`Jobs/Response/*`) | add `errorKind`, keep `ErrorMessage` with the hint |
| Token missing / undecryptable at use | `ConnectorHelpers.UnprotectToken` call sites listed in 4.1 to 4.3 | use one helper that returns a typed failure instead of null or an exception; covers T1, T2 |
| Reactive reporter | new `ConnectorFailureReporter` (App); `ConnectorRepository.UpdateStatusAsync` | flips the flag with a specific reason, dedupes notices (R1, R2) |
| Health gate coverage | `WorkspaceUndoPushHandler`, `WorkspaceGitService.Fetch`, `WorkspaceFeatureOperations`, `WorkspaceRepositoryOperations`, GitHub REST entry points, `PackageRegistrySyncService` | add `EnsureConnectorsHealthy...` once per operation, batched (R4) |
| Run pre-flight | `WorkspaceUpdateAndPushOperations.ExecuteAsync` (and `ApproveNormalPushContinuation`), `PrepareWorkspaceOrchestrator`, REST `/update-and-push`, `/push`, `/prepare-workspace` | one batched flag read for the connectors of the repositories in scope; optional throttled verification (F1, F2) |
| Stop both lanes | `UpdateAndPushOrchestrator`, `DependencyUpdateOrchestrator` (`onLevelCompleted` path), `WorkspacePushService.RunPushLaneAsync` | first auth failure cancels the run, result carries the kind (F3, F6) |
| One notice, partial-state report | `WorkspaceUpdateAndPushOperations` (new status), `UpdateAndPushResultPresenter`, `WorkspaceOperationOutcomeStore`, popup and Repositories page notice code | message names connector, pushed levels, unpushed repositories (R6, R7, F4, F5) |
| NuGet 401 no longer "not found" | `NuGetService.PackageExistsAsync`, `GetPackageBaseAddressAsync`, `PackageRegistrySyncService`, `WorkspacePushService` package wait | typed result; wait stops at the first `AuthenticationFailed`; registry sync raises an auth failure, not a missing mapping (R5, F8) |
| Message ordering | `PushErrorFormatter.Format` | auth first (R8) |
| REST | `WorkspaceOperationsEndpoints.MapCombined` and the `/push` handler | distinct status and body with `errorKind`, `connectorId` (F9) |
| Prepare Workspace | `PrepareWorkspaceOrchestrator` branch gate | branch failure caused by auth reported as auth (F10) |
| Live feeds | `GhaWorkflowLiveFeedService`, `IGitHubRateLimitTracker` | stop polling a connector after an auth failure until the flag is healthy again (H2) |
| Undo push literal | `WorkspaceUndoPushHandler` l.78 | replace "Unknown error" with the typed message |

## 8. Suggested order of work

1. Classifier, kind, `NuGetService` typed result, `PushErrorFormatter` ordering. Pure functions with unit tests (table of
   real git and HTTP texts per state T1 to T10, including the false positives: archived, protected branch, rate limit,
   SSO).
2. `ConnectorFailureReporter` and flag flipping; add the free pre-flight flag read to Update and Push, Prepare, Push and
   REST. This alone fixes "second run still starts with a dead token".
3. Worker `errorKind` plumbing; stop-both-lanes and the new result status; presenter wording; REST mapping.
4. Gate the ungated operations (R4) and the package wait / registry sync (R5).
5. Optional throttled verification (`LastVerifiedUtc`), then live-feed polling stop.

Each step is independently shippable and each leaves the existing behaviour for non-auth failures unchanged.

## 9. Tests to add

- Classifier table tests (Common.Tests), including ambiguous 403s and the `repository not found` rule.
- `UpdateAndPushOrchestratorPipelineTests` style: an auth failure at level 2 stops the update lane, level 1 stays
  pushed, the result names the connector, and no approval is offered.
- `WorkspaceUpdateAndPushOperationsTests`: unhealthy connector refuses before any call; auth failure maps to the new
  status; token failure is never `ApprovalRequired`.
- `PushErrorFormatterTests`: an auth failure wrapped in pull-failure or non-fast-forward wording stays an auth message.
- NuGet: 401 on `PackageExistsAsync` is auth failed, not absent; the package wait ends at the first 401.
- Dedupe: N parallel repository failures for one connector produce one notice.
- REST: status and body for the auth case.

## 10. Risks and open questions

- **Over-matching.** `repository not found`, `access denied` and bare 403 are ambiguous. Classifying them too eagerly
  would mark a healthy connector unhealthy and block all work until the user re-tests it. Mitigation: flip the flag only
  for `Invalid` (401 / bad credentials) automatically; for `InsufficientAccess` report the message but do not flip the
  flag.
- **Flag as a blocker.** A wrongly false flag blocks runs. Provide a way out: the Connectors page test already resets
  it; consider also clearing it on the next successful authenticated call.
- **Token captured per run.** Operations read the token when they start, so a token saved mid-run is not used (T8).
  Acceptable; the message should say to re-run.
- **Two token routes.** The App sends `bearerToken` in command args, while `WorkerTokenProvider` fetches by repository
  id for hook-driven work. Both must classify the same way.
- **Confirm the real failure.** From the user's log, collect: the Worker lines `Git push failed ... Stderr=` or `Git
  pull failed ... Stderr=`, any `Git <op> failed authentication` warning, the connector's `Status`/`LastError`, and
  whether the failing level was the first. That tells which of G2/G3/G4/N1/N2 produced the "unknown" text and whether
  the health gate was the stale-flag case (R1).
- **Performance.** Steps 1 to 3 add no network calls on the happy path. The throttled verification (section 5, item 3)
  is the only proactive network cost: one call per connector per 30 minutes, bounded parallelism; skip it on VDIs by a
  setting if needed.
