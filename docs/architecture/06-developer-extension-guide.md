# 06 - Developer Extension Guide

## 1. Start by locating the ownership boundary

Before changing behavior, classify the feature.

### Local Git/filesystem behavior

Belongs in `GrayMoon.Worker`.

Examples:

```text
git command
filesystem read/write
project discovery
restore
Git Changes diff/status
hook processing
```

### Multi-repository orchestration

Belongs in `GrayMoon.App`.

Examples:

```text
dependency-ordered update
synchronized push
batch branch creation
batch PR flow
batch recomputation
```

### Reusable user/automation operation

Expose it through `GrayMoon.Application` where appropriate.

### Persisted read-heavy page

Prefer a dedicated query service.

---

## 2. Do not let pages become the domain layer

A page may own:

```text
modal visibility
button state
dialog wording
toast presentation
navigation
local selection
```

A page should not be the only place that knows:

```text
whether an operation is safe
how a multi-step operation works
how destructive eligibility is calculated
which repositories must be changed
```

Return to Default is the model to follow:

```text
headless analysis
structured plan
UI confirmation
headless execution
```

---

## 3. Preserve App-Worker separation

Never add code to GrayMoon.App that shells out to local Git just because it is easier.

If App needs local state:

```text
define/reuse Worker command
send concrete request
Worker executes
return typed result
persist in App
```

This keeps Docker/web deployment valid.

The same applies to filesystem probes such as `Directory.Exists`: in Docker the App cannot see the developer's paths, so the answer must come from the Worker.

---

## 4. Use application contracts for reusable mutations

If a workflow is needed by:

```text
Blazor
REST
future automation
```

it should not have three implementations.

Preferred:

```text
IWorkspaceSomethingOperations
        │
        ├─ page calls
        └─ endpoint calls
```

The endpoint should be thin.

---

## 5. Use query services for large pages

Do not load the full Workspace aggregate with every navigation property for grids that may contain many rows.

Follow existing query patterns:

```text
CountAsync
GetIndexAsync
GetPageAsync / GetByIdsAsync
GetSnapshotAsync
GetHeaderStateAsync
```

Design indexes together with the query.

---

## 6. Persist expensive observations

If obtaining state requires:

```text
Git
GitVersion
GitHub API
package registry
filesystem scan
project parse
```

consider whether the UI should render a persisted projection first.

GrayMoon favors fast persisted reads plus background refresh.

---

## 7. Define state authority

For every new persisted field answer:

```text
Who is authoritative?
When is it refreshed?
When is it invalid?
Can another operation know only part of it?
What is the correct uniqueness key?
```

Do not store values that cannot be reliably refreshed or invalidated.

---

## 8. Respect partial repository snapshots

`WorkspaceRepositoryStateWriter` intentionally writes only probed groups.

When adding new repository state:

- add a clear snapshot group;
- mark it probed only when the operation truly obtained authoritative data;
- test that unrelated partial operations preserve existing values.

---

## 9. Batch recomputation

When one user action updates several repositories:

Bad:

```text
repo A write
recompute whole Workspace
repo B write
recompute whole Workspace concurrently
repo C write
recompute whole Workspace concurrently
```

Good:

```text
repo A write
repo B write
repo C write
all complete
recompute once
broadcast once
```

Use the existing recompute-scope pattern.

---

## 10. Dependency graph changes

If a feature affects dependency semantics, inspect all dependency sources:

```text
physical project PackageReferences
configured file tokens
generated packages
custom dependencies
```

Also inspect all consumers:

```text
Repositories grouping
Update
Push Updated
synchronized push
package wait
Dependencies page
badges
notifications
restore planning
```

Changing only the graph page is not enough.

---

## 11. Branch/tag changes

Remember that GrayMoon persists both:

```text
ref inventory
current checkout state
```

Keep synthetic detached-HEAD descriptions out of persisted real branches.

When branch state changes, consider:

```text
PR invalidation
Actions branch cache
GitVersion
commit counts
default divergence
upstream
tags
dependency/version state
hooks
```

---

## 12. Git Changes changes

Do not bypass the snapshot pipeline.

A new mutation should converge to:

```text
Worker mutation
authoritative status snapshot
versioned cache
App write queue
SQLite
browser invalidation
```

Preserve:

```text
path validation
large pathset handling
watcher debounce
separate read/diff Worker pools
```

---

## 13. SignalR changes

Use browser hub events as invalidation signals.

Avoid sending giant domain objects to every circuit.

Handlers should:

```text
filter identity
debounce where needed
reload persisted state
```

If a new event can fire very frequently, plan backpressure/debounce.

---

## 14. Background operation changes

Every long-running mutation must define:

```text
operation lock scope (structural Workspace or one Feature context)
progress
cancellation
page disposal behavior
error aggregation
final persistence
final broadcast
```

Do not depend on page-owned scoped services after the component is gone unless the work creates its own scope.

---

## 15. Worker command changes

Worker DTO conventions:

```text
typed request/response classes
explicit JSON property names
null validation in handlers
sealed classes
```

Command handlers should remain narrow.

Shared Git execution should use the common Git/command abstraction rather than starting processes ad hoc.

Adding a Worker command touches:

```text
GrayMoon.Worker/Jobs/Requests + Jobs/Response   DTOs
GrayMoon.Worker/Commands                        ICommandHandler<TRequest, TResponse>
Cli/Handlers/RunCommandHandler.cs              DI registration
Services/CommandDispatcher.cs                  executor entry
Services/CommandJobFactory.cs                  DeserializeRequest case
Hosted/SignalRConnectionHostedService.cs       ReadOnlyCommands / DiffCommands, only if not a main-pool command
GrayMoon.Abstractions/Worker/WorkerHubMethods.cs optional name constant
```

The `.claude/skills/add-worker-command` skill walks through these steps.

---

## 16. Remote credentials

Never write connector tokens into:

```text
git config
repository files
logs
terminal output
```

Remote Git authentication is supplied dynamically.

Logging must redact secrets.

---

## 17. Retry behavior

Do not blindly retry deterministic Git failures such as:

```text
protected branch rejection
invalid ref
known auth failure
non-fast-forward requiring user action
```

Retry only when the failure class is plausibly transient and the existing product behavior expects it.

---

## 18. UI consistency

GrayMoon has a strong visual language.

When adding UI:

- reuse existing buttons;
- reuse existing modal layout;
- reuse callouts;
- reuse disabled/busy styling;
- reuse keyboard behavior;
- avoid one-off icons or decorative patterns;
- prefer shared CSS/classes over page-specific hacks.

---

## 19. Search/filter consistency

Use the shared filter-expression implementation.

Do not create page-specific mini query languages unless the page truly needs an additional field predicate.

---

## 20. Database change checklist

For a schema change:

1. update EF model;
2. update new-database creation;
3. add guarded existing-database migration in `src/GrayMoon.App/Migrations.cs` (or `Migrations.Features.cs` for Feature context tables) and call it from `Migrations.RunAllAsync`;
4. ensure new column is nullable or has default;
5. add indexes;
6. test migration from existing shape;
7. test fresh database;
8. ensure no duplicate source of truth remains;
9. if the data is observed from a checkout, key it by `WorkspaceFeatureContextId` and backfill the special Workspace context for existing rows.

---

## 20A. Feature context scoping

A Workspace repository can be checked out in several contexts at once (the special Workspace plus each Feature). The rule (`.cursor/rules/feature-context-scoping.mdc`):

1. any query or write of projects, dependency stats, GitVersion, PR, Actions, or Git Changes data takes a `WorkspaceFeatureContextId`, not just a `workspaceId`;
2. for a Feature, read and write the context tables (`WorkspaceRepositoryContextState` and siblings); never fall back to the shared link row when the context row is missing - the answer is unknown;
3. mirror writes onto the shared `WorkspaceRepositoryLink` / legacy tables only for the special Workspace context;
4. include the context id in any lookup key that is not globally unique across contexts;
5. resolve repository paths through `IWorkspaceContextPathResolver`, and attribute Worker notifications through `IWorkspaceHookContextAttributor`; never derive a context from the UI selection inside an operation;
6. structural changes (membership, Feature create/remove) use `TryStartStructural`; per-context mutations use a context job key (`WorkspaceJobKeys.ContextOverlayKey`).

Reference test: `WorkspaceRepositoryLinkListQueryServiceTests.Feature_context_sort_keyset_and_level_grouping_use_context_state_not_shared_link`.

---

## 21. Test expectations

Every architectural change should include tests at the narrowest appropriate layer.

Examples:

```text
repository writer tests
query-service tests
dependency graph tests
Worker command tests
operation-runner tests
hook sync tests
Git Changes activation/snapshot tests
migration tests
```

After broad Workspace changes, run all three test projects.

---

## 22. Code conventions

Repository coding conventions are defined in root `CLAUDE.md`.

Important examples include:

```text
CRLF line endings
ASCII hyphen only
sealed concrete services/handlers by default
C# primary constructors where appropriate
sealed record modal state
typed sealed Worker DTO classes
```

Architecture docs do not replace those coding rules.

---

## 23. Documentation rule

When current behavior changes, update these architecture documents.

Do not add a permanent new document named:

```text
Feature-X-Design-v7
Analysis-final-final
Implementation-Plan-2
```

Temporary proposals are fine during design, but shipped behavior should be folded into the current-state reference. The `docs/worktree/` design set is in that state: Features have shipped and this folder is now the reference.

The architecture folder should answer "how GrayMoon works now".
