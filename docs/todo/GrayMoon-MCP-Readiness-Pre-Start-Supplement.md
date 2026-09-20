# GrayMoon Features - MCP Readiness Pre-Start Supplement

## Purpose

This supplement contains the **small amount of code alignment recommended before the worktree Feature implementation begins**.

It is intentionally narrow.

The MCP-readiness review does **not** recommend implementing MCP now, and does **not** recommend a broad pre-worktree refactor of all GrayMoon application contracts.

The goal is to remove one concrete automation/safety split that would otherwise be carried into Feature lifecycle design.

Baseline reviewed:

```text
Jandini/GrayMoon
commit cd9a17cb21e3ba2757542cc9f55f6fc19e2538c8
```

Companion documents:

```text
GrayMoon-Features-Pre-Design-Decision-Summary-v3.md
GrayMoon-Workspace-Current-Features-Baseline-Appendix.md
```

---

# 1. Recommendation

Before starting the main worktree Feature implementation:

> **Move Return-to-Default preflight/safety analysis behind a reusable headless application contract and make UX + REST use the same analysis/execution path.**

This is the only pre-start code change recommended by the MCP-readiness review.

Everything else should be incorporated into the worktree/context migration itself.

---

# 2. Why This One Change Should Happen First

Return to Default already contains the safety semantics GrayMoon intends to reuse conceptually for Feature cleanup.

The current code has two materially different surfaces.

## Human UX path

`WorkspaceRepositories.ReturnToDefault.cs` performs substantial preparation and decision logic, including:

```text
refresh PR state
read fresh persisted branch state
determine repositories not already on default
check commits ahead of default
allow merged/closed PR to satisfy cleanup safety
fetch latest branch state
refresh upstream information
determine whether a remote branch exists
construct the confirmation model
ask whether the remote branch should be deleted
ask whether destructive local deletion is explicitly allowed
optionally close an open PR in Return All
then execute ReturnToDefaultDirectAsync
```

Much of this logic is page-owned.

## REST / automation path

`POST /api/workspaces/{workspaceId}/return-to-default` calls:

```text
IWorkspaceSyncOperations.ReturnToDefaultAsync
    ↓
WorkspaceSyncHandler.ReturnToDefaultUnattendedAsync
```

That path has its own safety implementation and its own policy choices.

It:

```text
fetches repositories
refreshes PR state
checks tag state
checks ahead-of-default state
requires merged/closed PR when ahead
returns to default unattended
automatically deletes remote branch when upstream exists
forces local branch deletion
```

Both paths are reasonable for their current callers, but they are **not one shared lifecycle policy**.

That is undesirable before introducing:

```text
Remove Feature
```

and later:

```text
MCP
```

because Feature cleanup should not become a third independent implementation of essentially the same safety concepts.

---

# 3. Target Shape

Introduce a reusable application-level preflight/plan.

Exact naming can be refined, but conceptually:

```text
AnalyzeReturnToDefaultAsync(...)
    ↓
ReturnToDefaultPlan
```

The plan should describe facts and consequences, not UI.

Example conceptual model:

```text
ReturnToDefaultPlan
- WorkspaceId
- Repositories[]
- CanProceedAutomatically
- RequiresConfirmation
- HasBlockingRepositories
- BlockingReason(s)

ReturnToDefaultRepositoryPlan
- RepositoryId
- RepositoryName
- CurrentBranch
- DefaultBranch
- IsAlreadyOnDefault
- IsOnTag
- CommitsAheadOfDefault
- HasUpstream
- PullRequestState
- PullRequestNumber?
- CanDiscardLocalBranchSafely
- RemoteBranchCanBeDeleted
- RequiresExplicitDiscardConfirmation
```

The plan should be generated from fresh enough state for the safety decision.

The exact internal data source may include:

```text
fresh branch fetch
persisted context checkout state
fresh PR state
commit/divergence state
upstream state
```

---

# 4. Execution Must Accept Explicit Choices

Execution should be separate from analysis.

Conceptually:

```text
ReturnToDefaultAsync(plan target / ids, options)
```

with explicit options such as:

```text
deleteRemoteBranch
allowForceDeleteLocalBranch
closeOpenPullRequest
```

Do not infer destructive user consent from the fact that a caller happens to be REST, UI, or future MCP.

The caller decides which options it is authorized to request.

The application service enforces whether those options are valid for the analyzed state.

---

# 5. UX After the Change

The current UX should remain behaviorally unchanged.

Flow:

```text
user clicks Return to Default
    ↓
application analyzes
    ↓
page renders existing GrayMoon confirmation UX from the plan
    ↓
user chooses cleanup options
    ↓
application executes
```

The page should no longer own the core decision rules.

It may still own:

```text
dialog wording
toasts
rendering
button state
countdown presentation
loading-overlay integration
```

The underlying safety facts and eligibility rules should live outside the component.

---

# 6. REST After the Change

REST should use the same analysis.

An unattended endpoint may still be supported, but it should be an explicit policy layered on top of the shared plan.

For example:

```text
Analyze
    ↓
verify every repository is eligible for unattended cleanup
    ↓
execute with documented unattended options
```

Do not maintain a separate independent safety algorithm inside `WorkspaceSyncHandler`.

This gives future MCP a clean path:

```text
MCP analyze_return_to_default
    ↓
same plan

MCP return_to_default
    ↓
same execution contract
```

without changing GrayMoon's lifecycle rules.

---

# 7. Why This Helps Worktree Feature Cleanup

The approved Feature lifecycle requires `Remove Feature` to handle:

```text
merged PR cleanup
closed-but-unmerged abandonment
explicit abort
uncommitted work
unpushed work
unmerged work
local branch cleanup
optional safe remote branch cleanup
offline operation
partial cleanup/recovery
```

The correct design is not to reuse Return-to-Default implementation mechanically.

The useful reuse is the **architecture pattern**:

```text
analyze consequences
    ↓
present / authorize
    ↓
execute explicit choices
```

Doing this cleanup before Feature implementation establishes that pattern in GrayMoon using an existing, proven lifecycle feature.

Then `Remove Feature` can be designed correctly from its first implementation rather than introducing the pattern for the first time in new worktree code.

---

# 8. Tests Required for the Pre-Start Change

The refactor must preserve existing UX behavior.

At minimum test:

## Already on default

```text
no destructive operation
reported/skipped correctly
```

## Branch ahead, no merged/closed PR

```text
blocked from unattended cleanup
human path communicates why
```

## Branch ahead, merged PR

```text
eligible for cleanup
```

## Branch ahead, closed PR

```text
eligible according to existing Return-to-Default policy
```

## Upstream exists

```text
plan reports remote branch
remote deletion occurs only when execution option requests it
```

## No upstream

```text
plan reports no remote cleanup
```

## Local branch requires force deletion

```text
requires explicit destructive authorization unless existing safe PR state allows current policy
```

## Tag checkout

```text
blocked / not eligible
```

## PR refresh failure

```text
unattended path does not guess that cleanup is safe
```

## Fetch failure

```text
analysis fails safely
```

## Multi-repository partial state

```text
plan represents per-repository safety
execution result preserves per-repository errors
```

---

# 9. Changes Explicitly NOT Recommended Before Worktrees

Do **not** delay Feature implementation for a broad MCP cleanup.

The following should happen naturally while the affected contracts are being migrated to `WorkspaceFeatureContext`.

## Do not migrate every operation signature twice

Do not first rewrite all:

```text
workspaceId
```

operations to a temporary abstraction and then later rewrite them again for Feature contexts.

Instead, when the context migration begins, change affected application commands/queries directly to the final explicit context identity.

## Do not broadly rename historical namespaces now

`GrayMoon.Application` is already a separate assembly and does not reference `GrayMoon.App`.

Some Application-owned types still carry historical `GrayMoon.App.*` namespaces.

Do not perform a large namespace-only refactor now.

As contracts are touched during context migration, move new/changed automation DTOs toward clear `GrayMoon.Application` ownership.

## Do not implement MCP tools/server

No MCP transport, tool registration, resources, prompts, or server code belongs in the worktree implementation.

## Do not rewrite all errors before worktrees

Existing `OperationResult` string errors can continue.

New Feature lifecycle operations should introduce structured conditions where useful, and existing operations can migrate incrementally.

## Do not move every query interface before it needs context

App-local query services should become application-facing when context migration or future automation actually requires them.

Avoid churn for query surfaces that are not touched.

---

# 10. Requirements to Carry Into the Worktree Detailed Design

The detailed worktree implementation must include these MCP-readiness rules:

```text
1. explicit WorkspaceFeatureContext identity in context-sensitive application contracts
2. no ambient/current-browser Feature lookup inside application services
3. path resolution stays internal to GrayMoon
4. commands remain headless
5. queries become context-aware where they describe checkout-derived state
6. new Feature lifecycle operations use analyze -> authorize -> execute
7. new Feature results expose machine-readable conditions where useful
8. MCP later reuses GrayMoon.Application rather than creating a duplicate domain service layer
9. context-level operation locking is shared by UX, REST, and future MCP
10. MCP implementation remains deferred until Feature UX is thoroughly validated
```

---

# 11. Definition of Done for This Supplement

The pre-start alignment is complete when:

- Return-to-Default safety analysis is available through a headless application service/contract;
- the existing human UX consumes that analysis without behavior regression;
- unattended/REST Return to Default consumes the same analysis rather than maintaining its own independent eligibility algorithm;
- destructive choices are explicit execution inputs;
- tests cover the existing merged/closed/ahead/upstream/tag/failure cases;
- no MCP implementation has been added.

After that, the worktree Feature implementation can begin without further MCP-specific prerequisite work.
