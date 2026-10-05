# Post-release plans (v1.1): REST API for Features, and the `WorkspaceFeatureOperations` refactor

Written 2026-10-04. **Documentation only.** Nothing in this file is implemented, and nothing here is part of the v1 plan in `06` except the one documentation step in section 1.1. Both items were deliberately kept out of v1: the first because the REST API is a separate surface that needs its own test harness, the second because it touches too much shipped code to risk before release.

Code facts below were read from the tree on 2026-10-04 (App `WorkspaceOperationsEndpoints.cs`, `BranchEndpoints.cs`, `WorkspaceEndpoints.cs`, `WorkspaceFeatureOperations.cs`). Line numbers will drift; re-check before starting.

---

## 1. REST API that can target a Feature

Review reference: `05` finding R5 (low severity, "document, or add an optional `contextId`"). Part F of `06` carries it as "REST API cannot target a Feature - document in R1, implement v1.1".

### 1.1 What v1 does (and what R1 must say)

This is the only part that ships with v1: a short section in `docs/user-guide/features.md` (R1) and the CHANGELOG.

Suggested text for R1:

> **REST API and Features.** The REST API works on the Workspace only. Every operation endpoint (`/api/workspaces/{id}/sync`, `push`, `update`, `pull`, `prepare-workspace`, `return-to-default`, `undo-push`, `restore-packages`, `git-changes*`, `files*`, and all `/api/branches/*`) always acts on the Workspace's own checkouts, even when the Workspace has Features. Calling the API never touches a Feature's worktrees. There are no endpoints to create, list, repair or remove Features, or to run an operation inside one. Use the app for that. Targeting a Feature from the API is planned for a later version.

Why that statement is true today:

- Every operation endpoint in `WorkspaceOperationsEndpoints.cs` calls a private `SpecialContextAsync(workspaceId, ...)`, and every context-aware endpoint in `BranchEndpoints.cs` and `WorkspaceEndpoints.cs` calls `GetOrCreateSpecialWorkspaceContextIdAsync` directly. No endpoint reads a context from the request.
- B3 and B4 scoped the project, file, package and push queries by context, so a Workspace-targeted REST call on a Workspace that has Features reads and writes only Workspace data.
- `IWorkspaceFeatureOperations` (create, analyze/remove, list, status, repair, rollback) is used only by the Blazor UI. No endpoint maps it.

Items to verify before R1 is written, because the claim above must match the code:

1. `GET /api/workspaces/{id}/repos` calls `GetAllSnapshotsAsync(workspaceId)` with no explicit context. Confirm it returns the Workspace context.
2. `POST .../pull-requests` and `.../pull-requests/merge` take no context at all (workspace-wide, keyed by repository id). Confirm what pull-request rows they touch when a Feature has its own PRs.
3. `WorkspaceCommandHttp.RunExclusiveAsync` locks per Workspace with `WorkspaceJobKeys.RepositoriesOverlayKey(workspaceId)`. Confirm what happens to a REST call while a Feature structural operation (create, remove, repair, rollback) holds `operationLock.TryStartStructural`. The expected, acceptable answer is HTTP 409 "A workspace operation is already running."

### 1.2 Goal for v1.1

An API client (script, CI job, the planned MCP surface) can do to a Feature what the UI does, with the same safety rules, and existing clients see no change.

### 1.3 Design options

| Option | Shape | For | Against |
|---|---|---|---|
| A. Optional `contextId` in each body or query | `POST /api/workspaces/3/push` with `{"contextId": 17}` | Smallest diff; what `05` R5 suggested | Callers must know numeric context ids; every request class grows a field; easy to forget on a new endpoint; GET and body-less POSTs need a query string |
| **B. Nested route (recommended)** | `/api/workspaces/3/features/{featureName}/push` mirrors the Workspace routes | Names are unique per Workspace (E1, case-insensitive), so they are stable and readable; the workspace-mismatch check comes free; the same handlers serve both routes; unambiguous in logs | Needs the endpoint set mapped twice (done once, with a shared helper) |
| C. Header (`X-GrayMoon-Feature`) | Same route, header chooses the context | No route duplication | Invisible in logs and docs; easy to drop in a proxy; conflicts with F3's header rules |

**Recommendation: B**, with the branch endpoints (which take `workspaceId` in the body, not the route) getting an optional `featureName` body field instead. Missing field means the Workspace, exactly as today.

### 1.4 Design (option B)

**Route shape**

- `/api/workspaces/{workspaceId}/...` stays byte-for-byte as it is.
- `/api/workspaces/{workspaceId}/features/{featureName}/...` serves the same operations against that Feature's context.
- A single private mapping method takes a route group and a context-resolver delegate, and is called twice: once with "special Workspace context", once with "Feature by name". This removes the repeated `SpecialContextAsync` call from every handler and makes forgetting a Feature route a compile-time impossibility.

**Resolving the Feature**

- New resolver method (on `IWorkspaceFeatureContextResolver` or a thin wrapper): find the Feature context by `(workspaceId, name)`, case-insensitive. Unknown name: 404 "Feature not found." A Feature of another Workspace is simply not found (404, never 403).
- State rules, matching the UI's C3 behaviour:
  - `Ready`: all operations allowed.
  - `Creating`, `Removing`: 409 with the state in the body.
  - `NeedsRepair`: read endpoints allowed; mutating endpoints refused with 409 and a message pointing at repair (the UI disables Sync, Push and Update in that state).

**Operations covered in the Feature-scoped group**

| Endpoint (under `.../features/{featureName}`) | Notes |
|---|---|
| `GET /repos` | Context-scoped snapshot, including drift (`I4`) and the Feature's repositories only |
| `GET /operations` | Running-operation probe, same payload |
| `POST /sync`, `/update`, `/push`, `/pull`, `/undo-push`, `/restore-packages` | Same bodies. `prepare-workspace` (creates a branch in every repo) is **not exposed**: a Feature owns exactly one branch (`I3`), so it returns 404 in the Feature group |
| `POST /return-to-default` | **Not exposed.** The guard in `FeatureBranchGuard` refuses it in a Feature, and the group does not map it |
| `GET /git-changes`, `POST /git-changes/commit`, `/stage`, `/unstage` | Same bodies |
| `GET /files`, `POST /files`, `GET /files/search`, `POST /files/update-versions` | Already context-parameterised in the service layer |
| `POST /pull-requests`, `/pull-requests/merge` | Only after the PR context question in 1.1 item 2 is answered |

**Branch endpoints** (`/api/branches/*`, workspace id in the body): add an optional `featureName` property to each request class. `FeatureBranchGuard` (I3) already runs inside `WorkspaceBranchOperations` when a Feature context is passed, so New Branch, checkout of other branches and Return to Default are already refused with a 400. The endpoint work is only to resolve and pass the context. `CountReposWithLocalBranchAsync`, `GetBranchesAsync` and `GetCommonBranchesAsync` take no context today; leave them alone unless a Feature needs different results (branch lists are shared, which is correct).

**Feature lifecycle endpoints** (new, second phase)

| Endpoint | Maps to |
|---|---|
| `GET /api/workspaces/{id}/features` | `ListFeaturesAsync` (name, state, repository counts) |
| `GET /api/workspaces/{id}/features/{name}` | `GetFeatureStatusAsync` (the status-panel data) |
| `POST /api/workspaces/{id}/features` | `CreateFeatureAsync`; body `{ "name": "..." }`; returns the structured `BranchExists` collisions on conflict (E5) |
| `POST .../features/{name}/repair` | `RepairFeatureAsync` |
| `POST .../features/{name}/rollback` | `RollbackFeatureAsync` |
| `GET .../features/{name}/remove-plan` | `AnalyzeRemoveFeatureAsync` (read-only, live Agent facts) |
| `DELETE .../features/{name}` | `RemoveFeatureAsync` |

Removal is destructive and its safety depends on the dialog. Rules for `DELETE`:

- Every `RemoveFeatureOptions` value (discard uncommitted changes, delete unmerged branches, delete local branches, delete remote branches, unlock worktrees) is an **explicit body field defaulting to false**. No "force" shortcut.
- The request must echo what the caller saw in `remove-plan`. Recommended: the plan response carries a `planToken` (hash of the per-repo head SHAs, dirty counts and ahead counts) and `DELETE` re-analyses and returns 409 when the token no longer matches. A script cannot delete based on stale facts.
- The response is the D2 per-repo report, unchanged.

### 1.5 Security

- F3 (`RequestSecurityMiddleware`) already requires a same-host Origin plus `X-GrayMoon-Request: 1` for browser-origin writes, and lets requests with no `Origin` header (scripts) through. That is acceptable for the Workspace routes, which already allow `push` from a script. Decide explicitly whether destructive Feature endpoints (`DELETE`, `rollback`) should additionally require the `X-GrayMoon-Request: 1` header even without an `Origin`. Recommendation: yes, to make accidental `curl` deletes and cross-site simple requests equally impossible; document it.
- No new authentication. The app has no user login (DEC-4); the REST API stays loopback-oriented.

### 1.6 Prerequisites and ordering

1. **No REST endpoint tests exist** (`05` section on tests: `WebApplicationFactory` is never used). Build the harness first (REST-1). Without it, refactoring 20 handlers to a shared mapping is unsafe.
2. Resolve the Part F item "Create PR and Push for a Feature repo that is off its Feature branch" before exposing `push` and `pull-requests` on Feature routes, otherwise the API would make that bug scriptable.
3. Do the refactor in section 2 first, or in parallel, only for the facade seam; the lifecycle endpoints call `IWorkspaceFeatureOperations`, which the refactor does not change.

### 1.7 Units

| Unit | Title | Size | Notes |
|---|---|---|---|
| REST-1 | Endpoint test harness and characterization tests | M | `WebApplicationFactory` over the App with the fake Agent bridge. One test per existing Workspace route recording status code and JSON shape. This is the A4 step 0 for everything below |
| REST-2 | Shared route mapping + Feature-scoped operation routes | M | Refactor the Workspace group to the mapping helper (characterization tests stay green unchanged), add the Feature group, resolver, state rules |
| REST-3 | `featureName` on branch endpoints | S | Optional property; absent means today's behaviour |
| REST-4 | Feature lifecycle: list, status, remove-plan (read-only) | S | No risk to data |
| REST-5 | Feature lifecycle: create, repair, rollback, remove | M | Plan token, explicit options, destructive-endpoint header rule |
| REST-6 | Docs | S | `docs/user-guide/features.md` REST section, endpoint reference, replace the v1 "Workspace only" paragraph |

### 1.8 Tests to add

- Workspace routes: unchanged responses for every route, run before and after REST-2.
- Feature routes: unknown Feature 404; Feature of another Workspace 404; `Creating`/`Removing` 409; `NeedsRepair` mutating 409 and read 200; `prepare-workspace` and `return-to-default` 404 under a Feature.
- A Feature-scoped call never changes Workspace rows, and a Workspace-scoped call never changes Feature rows (reuse the two-context seeding from `WorkspaceB4ContextLeakTests`).
- Concurrency: a REST call during a Feature structural operation returns 409.
- Remove: stale `planToken` returns 409; missing options default to false; the report matches D2.
- Branch endpoints: `featureName` present with `New Branch` refused with the I3 message.

### 1.9 Regression guard

Additive only. No existing route, query parameter, request property, status code or JSON field changes. The default for every missing selector is the Workspace. Old API clients keep working with no edits.

### 1.10 Out of scope

Authentication, API keys, a client SDK, an MCP server, multi-branch Features, and a "create Feature on a subset of repositories" parameter (that waits for the repository picker, DEC-5).

---

## 2. Refactor `WorkspaceFeatureOperations`

Review reference: `02` section 4.3; `04` P2-8. **Deliberately after release (and after at least one release in real use).** The class is the heart of Create, Remove, Repair and Rollback; Remove can lose a user's work if it is wrong. v1 changed it in many places (lanes A, B, C, D, I) and the test suite is only now trustworthy.

### 2.1 Why it is worth doing

- `Services/Features/WorkspaceFeatureOperations.cs` is **1,977 lines** (the review counted 1,181; v1 grew it by two thirds). Everything is in one class: orchestration, Agent payload building, projection seeding, remove classification and UI-oriented state.
- The same shapes are repeated:
  - **Per-repository fan-out with a gate:** `SemaphoreSlim(MaxParallel)` plus `Task.WhenAll` appears in Create (about lines 243 and 308), Remove analysis (426, 497), Remove (720, 947), Repair (1455, 1521) and Rollback's dirty collection (1574, 1597).
  - **Structural-operation wrapper:** a `TaskCompletionSource` around `operationLock.TryStartStructural` appears at about lines 56, 563, 1335 and 1382, and again in `WorkspaceExternalWorktreeOperations` (about 133 to 172).
- Agent payloads are anonymous objects with string command names next to `AgentHubMethods.*` constants. There is no compile-time contract between App and Agent for them.
- It is the file future Feature work (REST lifecycle, repository picker, adopt-existing-branch) will keep touching, so each addition is currently a merge-conflict and regression risk.

### 2.2 Public surface to keep (the facade)

`IWorkspaceFeatureOperations` (`GrayMoon.Application/Features`) does not change. It is the UI contract (`ui-application-facades.mdc`). Its members: `CreateFeatureAsync`, `AnalyzeRemoveFeatureAsync`, `RemoveFeatureAsync`, `ListFeaturesAsync`, `GetParentBranchNamesByRepositoryIdAsync`, `IsRemoveIncompleteAsync`, `GetFeatureStatusAsync`, `RepairFeatureAsync`, `RollbackFeatureAsync`. After the refactor `WorkspaceFeatureOperations` keeps implementing it and delegates.

### 2.3 Target structure (all under `App/Services/Features`)

| New type | Takes from the current class | Nature |
|---|---|---|
| `StructuralOperationRunner` (internal) | The four `TryStartStructural` + `TaskCompletionSource` wrappers, plus `WorkspaceExternalWorktreeOperations`'s | Generic helper, `RunStructuralAsync<T>(...)` |
| `PerRepositoryFanOut` (internal) | Every `SemaphoreSlim(MaxParallel)` + `Task.WhenAll` block | Generic helper: run work per repo with the gate, collect per-repo outcomes, never throw for one repo's failure, honour cancellation, report "x of y" progress |
| `FeatureCreateService` | `CreateFeatureAsync`, `CreateFeatureCoreAsync`, `FailCreate`, head snapshot | Create |
| `FeatureRemoveService` | `AnalyzeRemoveFeatureAsync`, `RemoveFeatureAsync`, `RemoveFeatureCoreAsync`, live-status probes, `DeleteContextScopedProjectDataAsync`, workspace refresh after remove | Remove (largest piece, about 700 lines) |
| `FeatureRemoveClassifier` (static, pure) | `Classify`, `IsAutomaticallySafe`, `IsPrMergedOrNeverCreated`, `ComposeRemoveWarning` | Pure logic, exhaustively unit-testable |
| `FeatureRepairService` | `RepairFeatureAsync`, `RollbackFeatureAsync`, their cores, `CollectDirtyReposForRollbackAsync` | Repair and rollback |
| `FeatureProjectionSeeder` | `SeedInitialFeatureProjectionsAsync` (about 240 lines) | Projection seeding |
| `FeatureStatusQueries` | `ListFeaturesAsync`, `GetFeatureStatusAsync`, `IsRemoveIncompleteAsync`, `GetParentBranchNamesByRepositoryIdAsync` | Read-only |

Target: no class above 500 lines; the facade a few hundred lines of delegation.

### 2.4 Method: strangler, one small step per change, no behaviour change

Each step is its own commit and leaves the app releasable.

1. **Baseline.** Record build warnings and test counts for Common, App, Agent and Desktop. Add characterization tests only where the existing 9 test files do not already pin the behaviour being moved. Candidates: cancellation in the middle of a fan-out, one repo failing while others succeed (row states and report), progress reaching "n of n", structural lock refusal text, and `MaxParallelOperations = 1` ordering.
2. **Partial classes, zero logic change.** Split the file into `WorkspaceFeatureOperations.Create.cs`, `.Remove.cs`, `.Repair.cs`, `.Status.cs`, `.Projections.cs` using `partial`. Pure cut-and-paste, so the diff is mechanical and reviewable (`git diff --color-moved`). This alone makes later steps small.
3. **Extract `StructuralOperationRunner`.** Replace one call site per commit, then the external-worktree one. Behaviour, messages and the lock key must be identical.
4. **Extract `PerRepositoryFanOut`.** Replace one block per commit, starting with the lowest-risk one (Rollback's dirty collection), ending with Remove. Per-repo exception handling, ordering of completion callbacks and progress text must be identical.
5. **Extract `FeatureRemoveClassifier`** (pure functions, move and keep the tests), then **`FeatureStatusQueries`**, then **`FeatureProjectionSeeder`**.
6. **Move Create, Repair/Rollback and last Remove** into their services. The partial classes from step 2 make each move a rename of the containing type plus constructor wiring.
7. **Optional: typed Agent payloads.** Introduce request records for the anonymous payloads. Where the Agent's request class lives in `GrayMoon.Agent` (App cannot reference it today), either define the shared records in `GrayMoon.Common` or leave the anonymous objects and only centralize the command-name constants. Decide at the time. This step has the least payoff and the highest compatibility risk, so it can be dropped.

### 2.5 Rules (non-negotiable)

- **No behaviour change.** No log message, error text, state transition, DB write order or progress string changes in the same commit that moves it. Bug fixes found on the way go to Part F, not into the refactor.
- **Agent protocol is frozen.** Serialized JSON of every command the class sends must be byte-identical. Add a golden-JSON test for each payload before step 7, and keep the old-shape compatibility tests from D1, D5 and I1 green.
- `IWorkspaceFeatureOperations` and all DI registrations that the UI sees do not change. New services are internal and registered next to the facade.
- Respect `dbcontext-scoped-lifetime.mdc`: new services get the same factories and scopes the current class uses; never hold a `DbContext` across the fan-out.
- Do not combine with H1 logging changes, with E-lane UX work, or with the REST lifecycle work. One concern per change.
- After every step run the full build and all four test projects. Counts must be equal or higher, never lower.

### 2.6 Risks and mitigations

| Risk | Why it matters | Mitigation |
|---|---|---|
| Remove changes meaning subtly | Data loss | Remove moves last; the pure classifier moves first with its tests; Remove tests (`RemoveFeatureReportTests`, `RemoveFeatureWorkspaceRefreshTests`, `RemoveFeatureProjectDataTests`) stay untouched and green |
| Fan-out helper changes exception or cancellation semantics | Stuck Features, lost errors | Characterization tests from step 1 written first and failing if the semantics move |
| `Creating`/`Removing` state transitions reorder | Reconciler (C2) misreads a Feature as stuck | Reconciler tests (`WorkspaceFeatureReconcilerTests`, 18) and repair tests stay green; no change to the order of row writes |
| `DbContext` lifetime mistakes in extracted services | SQLite "table locked" (seen in C1) | Same factory pattern; run `CreateFeatureIntentAtomicityTests` repeatedly |
| Merge conflicts with other Feature work | Slows everything | Do it in a quiet window with no other Feature units in flight; steps are small and land quickly |

### 2.7 Units

| Unit | Title | Size |
|---|---|---|
| RF-1 | Baseline and missing characterization tests | S |
| RF-2 | Split into partial classes (mechanical) | S |
| RF-3 | `StructuralOperationRunner` | S |
| RF-4 | `PerRepositoryFanOut` | M |
| RF-5 | Classifier, status queries, projection seeder | M |
| RF-6 | Create, repair/rollback, remove services; facade delegates | M |
| RF-7 | Typed Agent payloads (optional) | M |

### 2.8 Done when

No file in `Services/Features` over about 500 lines; `IWorkspaceFeatureOperations` unchanged; the four test projects pass with equal or higher counts; no Agent protocol diff; the owner's real-database Create, Remove, Repair and Rollback checks (the old GATE-2 and GATE-3 steps) pass on the refactored build.

---

## 3. Relationship between the two

They are independent. The refactor makes new Feature code (including REST lifecycle) cheaper to write but is not a prerequisite for it, because REST calls the unchanged facade. If only one is done first, do REST-1 (test harness) and RF-1 to RF-3 (low-risk) together, since both build the safety net the later steps need.
