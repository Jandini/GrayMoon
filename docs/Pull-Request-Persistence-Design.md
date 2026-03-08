# Pull Request Persistence – Design Document

## Overview

Persist the **Pull Request** column state so it survives page refresh (F5) and stays in sync with the rest of the grid. Use a **single dedicated service and repository** for PRs. Update persisted PR state **whenever something updates the repository row** (sync, refresh, push, hooks). Design with the intention to add **polling every 5 seconds** for repos in "checks running" (unstable) state later.

---

## 1. Goals

- **Persistence:** PR badge state is stored in the database and loaded with the workspace so the column matches others (no "none" flash on F5).
- **Single place:** One service and one repository own all PR read/write; no PR logic scattered across pages or multiple services.
- **Update on row change:** Whenever sync, refresh, push, or agent hooks update a workspace repository row, refresh that repo's PR state and persist it.
- **Future:** Same service can be used to poll repos in "unstable" (checks running) every 5s without duplicating logic.

---

## 2. Storage: New Table

Use a **dedicated table** so PR concerns stay in one place and `WorkspaceRepositoryLink` is not bloated.

**Table: `WorkspaceRepositoryPullRequests`**

| Column                 | Type     | Notes |
|------------------------|----------|--------|
| WorkspaceRepositoryId  | int (PK, FK) | 1:1 with WorkspaceRepositories |
| PullRequestNumber      | int?     | null = no PR |
| State                  | string?  | "open", "closed" |
| Mergeable              | bool?    | null = unknown |
| MergeableState         | string?  | "unknown", "clean", "dirty", "unstable", "blocked" |
| HtmlUrl                | string?  | PR URL |
| MergedAt               | datetime?| When merged (for "merged" badge) |
| LastCheckedAt          | datetime | When we last fetched from API |

- **PK:** `WorkspaceRepositoryId` (one row per workspace–repo link).
- **FK:** `WorkspaceRepositoryId` → `WorkspaceRepositories(WorkspaceRepositoryId)` ON DELETE CASCADE.
- When there is no PR, store a row with `PullRequestNumber = null` (and other fields null) so we know "we checked, no PR"; or delete the row. Prefer **upsert: if no PR, write null number and state** so `LastCheckedAt` is still set.

**Entity:** `WorkspaceRepositoryPullRequest` (model in `GrayMoon.App.Models`).

---

## 3. Repository: Single Dedicated Repo

**Interface:** `IWorkspacePullRequestRepository` (optional; can be concrete only).

**Implementation:** `WorkspacePullRequestRepository`

- **GetByWorkspaceIdAsync(workspaceId)**  
  Returns all PR rows for that workspace (join WorkspaceRepositories on WorkspaceId).  
  Return type: `Dictionary<int, WorkspaceRepositoryPullRequest?>` keyed by **RepositoryId** (so the page can look up by `link.RepositoryId`). Implementation: load links for workspace, load PR rows for those `WorkspaceRepositoryId`s, then build dict RepositoryId → PR row (convert to `PullRequestInfo` or a small DTO in the service layer).

- **UpsertAsync(workspaceRepositoryId, PullRequestInfo? pr, CancellationToken)**  
  Insert or update the row for that link. If `pr == null`, set PullRequestNumber (and state/mergeable/mergeable_state/html_url/merged_at) to null, set LastCheckedAt = UtcNow. If `pr != null`, set all fields from `PullRequestInfo` and LastCheckedAt = UtcNow.

- **DeleteAsync(workspaceRepositoryId)**  
  Optional: remove row when repo is removed from workspace (CASCADE already does this if we use FK; otherwise call delete when link is removed).

No other component should read/write this table; only `WorkspacePullRequestRepository` and the one service that uses it.

---

## 4. Service: Single Dedicated Service

**Interface:** `IWorkspacePullRequestService` (optional).

**Implementation:** `WorkspacePullRequestService`

Dependencies:

- `IWorkspacePullRequestRepository` (or `WorkspacePullRequestRepository`)
- `GitHubPullRequestService` (existing: fetches PR from API for a repo+branch)
- `AppDbContext` or a way to load workspace links with Repository + Connector (e.g. `WorkspaceRepository` or a small load method) for the repos to refresh
- Optional: `IHubContext<WorkspaceSyncHub>` to notify UI after background refresh (see below)

**Methods:**

1. **GetPersistedPullRequestsForWorkspaceAsync(workspaceId, CancellationToken)**  
   Calls repository `GetByWorkspaceIdAsync(workspaceId)`, returns `IReadOnlyDictionary<int, PullRequestInfo?>` keyed by **RepositoryId**. Used on **page load**: workspace is already loaded with links; page (or this service) loads persisted PRs and builds `prByRepositoryId` from this so the grid shows last-known state immediately.

2. **RefreshPullRequestsAsync(workspaceId, IReadOnlyList<int> repositoryIds, CancellationToken)**  
   - For each `repositoryId`, resolve the workspace repo link (WorkspaceRepositoryId, Repository, BranchName, Connector). Skip if not GitHub or no branch.
   - For each link, call existing `GitHubPullRequestService.GetPullRequestForBranchAsync(repository, connector, branchName)` to get current `PullRequestInfo?`.
   - Call repository `UpsertAsync(workspaceRepositoryId, pr)` for each.
   - This is the **only** method that fetches from API and persists. All "refresh PR" flows call this.

3. **RefreshPullRequestsForWorkspaceAsync(workspaceId, CancellationToken)**  
   Convenience: load all repository IDs for the workspace (from WorkspaceRepositories) and call `RefreshPullRequestsAsync(workspaceId, repoIds, ct)`. Useful after full sync or for "refresh all" on page load.

**Future (polling):**

- **GetRepositoryIdsWithUnstableChecksAsync(workspaceId)**  
  Query persisted PR rows for that workspace where `MergeableState == "unstable"`. Return list of `RepositoryId` (or WorkspaceRepositoryId; service can map).
- A **hosted service** or timer can run every 5s for active workspaces, call `GetRepositoryIdsWithUnstableChecksAsync`, then `RefreshPullRequestsAsync(workspaceId, thoseRepoIds)`, then notify clients (e.g. `WorkspaceSynced` or a dedicated `WorkspacePullRequestRefreshed`) so the grid updates. No new fetch/persist logic; only this orchestration.

---

## 5. When to Refresh PR (Call the Service)

All of these should call **the same method**: `WorkspacePullRequestService.RefreshPullRequestsAsync(workspaceId, repositoryIds)`.

| Trigger | Where | Repository IDs |
|--------|--------|------------------|
| **Sync (batch)** | `WorkspaceGitService` after `PersistVersionsAsync` | `resultList.Select(r => r.RepoId).ToList()` |
| **Refresh single repo** | `WorkspaceGitService` after `PersistVersionsAsync` (single-repo path) | `[repositoryId]` |
| **Push** | After push flow refreshes version (e.g. `RefreshRepositoryVersion`), that path already goes through `PersistVersionsAsync`; ensure PR refresh runs after that | Same as refresh: one repo |
| **Agent hook (commit/checkout/merge)** | `SyncCommandHandler` after `SaveChangesAsync` and dependency recompute | `[n.RepositoryId]` |
| **Page load** | Optional: after loading workspace and showing persisted PRs, background call `RefreshPullRequestsForWorkspaceAsync(workspaceId)` so data is fresh; when done, notify UI (e.g. SignalR) so grid can update without full reload |

**Implementation notes:**

- **WorkspaceGitService:** Inject `IWorkspacePullRequestService`. At the end of `PersistVersionsAsync`, after `SaveChangesAsync` and dependency merge, call `_workspacePullRequestService.RefreshPullRequestsAsync(workspaceId, resultList.Select(r => r.RepoId).ToList(), cancellationToken)`. Do not await in a way that blocks the sync response if you want sync to return first; fire-and-forget or background is acceptable so long as failures are logged.
- **SyncCommandHandler:** Inject `IWorkspacePullRequestService`. After `RecomputeAndPersistRepositoryDependencyStatsAsync` and before/instead of (or after) `SendAsync("WorkspaceSynced")`, call `_workspacePullRequestService.RefreshPullRequestsAsync(n.WorkspaceId, [n.RepositoryId], CancellationToken.None)`. Fire-and-forget is acceptable; then `WorkspaceSynced` can be sent so the UI reloads; when the page reloads workspace data it will include the new PR row (or the next load will, if refresh is async).

Prefer **await** so PR is persisted before the client gets `WorkspaceSynced` and reloads; then the reloaded data already has the new PR state.

---

## 6. Page Load and Grid Data

- **WorkspaceRepository.GetByIdAsync** already loads `workspace.Repositories` with `Repository` and `Connector`. Extend to **include** the PR row: add navigation `WorkspaceRepositoryLink.PullRequest` (optional 1:1 to `WorkspaceRepositoryPullRequest`), and `.Include(link => link.PullRequest)` in `GetByIdAsync`. Then each `link` has `link.PullRequest` (null or entity).
- **WorkspaceRepositories.razor:**  
  - On load, after `ReloadWorkspaceDataAsync`, build `prByRepositoryId` from the links: for each `wr` in `workspaceRepositories`, `prByRepositoryId[wr.RepositoryId] = wr.PullRequest != null ? MapToPullRequestInfo(wr.PullRequest) : null`. So we **never** call `LoadPullRequestsInBackgroundAsync` for the initial paint; we use persisted data only.
  - Optional: after first paint, call `WorkspacePullRequestService.RefreshPullRequestsForWorkspaceAsync(workspaceId)` in the background; when done, either reload workspace (e.g. `ReloadWorkspaceDataAsync`) or have the service notify via SignalR and the page updates `prByRepositoryId` from the result (or reloads workspace). That way F5 shows last-known state immediately, then it can update when refresh completes.

**Mapping:** `WorkspaceRepositoryPullRequest` (entity) → `PullRequestInfo` (display model) in one place (e.g. extension method or method on the service/repository).

---

## 7. Migration and DbContext

- **Migration:** In `Program.cs` (or existing migration path), add a step: if table `WorkspaceRepositoryPullRequests` does not exist, create it with columns above and FK to `WorkspaceRepositories(WorkspaceRepositoryId)` ON DELETE CASCADE.
- **AppDbContext:** Add `DbSet<WorkspaceRepositoryPullRequest> WorkspaceRepositoryPullRequests => Set<WorkspaceRepositoryPullRequest>();` and configure the entity (PK, FK, optional string lengths, index on WorkspaceRepositoryId if not PK).
- **WorkspaceRepositoryLink:** Add `public WorkspaceRepositoryPullRequest? PullRequest { get; set; }` and configure 1:1 in `OnModelCreating` (optional dependent side).

---

## 8. Summary

| Item | Choice |
|------|--------|
| Storage | New table `WorkspaceRepositoryPullRequests` (1:1 with link, keyed by WorkspaceRepositoryId). |
| Repository | Single `WorkspacePullRequestRepository`: GetByWorkspaceIdAsync, UpsertAsync. |
| Service | Single `WorkspacePullRequestService`: GetPersistedPullRequestsForWorkspaceAsync, RefreshPullRequestsAsync(workspaceId, repoIds), RefreshPullRequestsForWorkspaceAsync; uses GitHubPullRequestService + repository. |
| When to refresh | After PersistVersionsAsync (sync/refresh/push), after SyncCommandHandler (hook); optional background refresh on page load. |
| Page load | Load workspace with Include(PullRequest); build prByRepositoryId from link.PullRequest; no in-memory-only fetch for first paint. |
| Future polling | Add GetRepositoryIdsWithUnstableChecksAsync; a timer/hosted service calls RefreshPullRequestsAsync(workspaceId, thoseIds) every 5s and notifies UI. |

All PR persistence and refresh logic stays in **one repository** and **one service**; the rest of the app only calls `RefreshPullRequestsAsync` when a repository row is updated and reads PR from the link when loading the workspace.
